using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BeastClicker;

public enum ClickButton { Left, Right, Middle }

public sealed class EngineSettings
{
    public double IntervalMs { get; init; } = 25;
    public ClickButton Button { get; init; } = ClickButton.Left;
    public bool DoubleClick { get; init; }
    /// <summary>0 = run until stopped.</summary>
    public int Limit { get; init; }
    public bool UseFixedPos { get; init; }
    public int FixedX { get; init; }
    public int FixedY { get; init; }
    /// <summary>Timing spread as a fraction of the interval. 0 = metronomic.</summary>
    public double Variance { get; init; } = 0.18;
    /// <summary>
    /// Milliseconds the button stays down. 0 keeps press+release in one atomic
    /// SendInput batch, which is what makes a drag impossible.
    /// </summary>
    public double HoldMs { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>
/// Drives synthetic mouse clicks on a dedicated high-priority thread.
///
/// Scheduling uses an absolute grid: deadline(n) = start + n*T + jitter(n), where
/// jitter is a zero-mean stationary AR(1) process. Since E[jitter] = 0 the expected
/// deadline is exactly start + n*T, so the achieved mean click rate matches the
/// configured rate regardless of how much spread is dialled in, and deviations
/// never accumulate into drift.
/// </summary>
public sealed class ClickEngine : IDisposable
{
    #region interop

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    // Windows 10 1803+ exposes a genuinely high-resolution waitable timer. It
    // parks the thread instead of spinning, which is the difference between
    // using ~1% of a core and using ~80% of one.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer, ref long lpDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    #endregion

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private long _count;

    /// <summary>
    /// Bumped once per run. Stop() only clears _running and returns, so a Start()
    /// arriving before the previous worker has noticed would otherwise find the
    /// flag true again and leave both threads clicking — the rate silently
    /// doubles. A worker compares this against the generation it launched with and
    /// retires as soon as it is no longer the current one.
    /// </summary>
    private int _generation;

    public bool IsRunning => _running;
    public long ClickCount => Interlocked.Read(ref _count);

    public event Action? Finished;
    public event Action<string>? Error;

    public static POINT GetCursor()
    {
        GetCursorPos(out var p);
        return p;
    }

    public void Start(EngineSettings s)
    {
        lock (_gate)
        {
            if (_running) return;
            int gen = ++_generation;
            _running = true;
            Interlocked.Exchange(ref _count, 0);

            _thread = new Thread(() => Loop(s, gen))
            {
                IsBackground = true,
                // Deliberately not Highest: this must never outrank the foreground
                // game's threads, or the game stutters while we click.
                Priority = ThreadPriority.AboveNormal,
                Name = "BeastClicker.Engine",
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Asks the worker to stop and returns straight away; it notices within one
    /// wait slice (30 ms at worst) and unwinds itself.
    /// </summary>
    public void Stop()
    {
        _running = false;
    }

    /// <summary>True while this run is neither stopped nor superseded by a newer one.</summary>
    private bool Current(int gen) => _running && Volatile.Read(ref _generation) == gen;

    private static INPUT Mouse(uint flags) => new()
    {
        type = INPUT_MOUSE,
        mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    private static (uint down, uint up) Flags(ClickButton b) => b switch
    {
        ClickButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        ClickButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    [ThreadStatic] private static Random? _rng;
    [ThreadStatic] private static double _spareGauss;
    [ThreadStatic] private static bool _hasSpareGauss;
    private static Random Rng => _rng ??= new Random();

    /// <summary>
    /// Standard normal via Box-Muller. The transform produces two independent
    /// normals from each pair of uniforms, so the second is kept for the next
    /// call rather than discarded — at a thousand clicks a second that halves the
    /// log, sqrt and trig work in the only per-click computation there is.
    /// Thread-static, so a fresh engine thread always starts without a stale spare.
    /// </summary>
    private static double Gauss()
    {
        if (_hasSpareGauss)
        {
            _hasSpareGauss = false;
            return _spareGauss;
        }

        double u = 0;
        while (u == 0) u = Rng.NextDouble();
        double magnitude = Math.Sqrt(-2.0 * Math.Log(u));
        (double sin, double cos) = Math.SinCos(2.0 * Math.PI * Rng.NextDouble());

        _spareGauss = magnitude * sin;
        _hasSpareGauss = true;
        return magnitude * cos;
    }

    private static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;

    /// <summary>
    /// Parks the thread for <paramref name="ms"/> using the high-resolution timer,
    /// falling back to Thread.Sleep if the OS did not give us one.
    /// </summary>
    private static void SleepPrecise(IntPtr timer, double ms)
    {
        if (timer != IntPtr.Zero)
        {
            // Negative due time means "relative", counted in 100ns units.
            long due = -(long)(ms * 10_000.0);
            if (due >= 0) due = -1;
            if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(timer, (uint)(ms + 50));
                return;
            }
        }
        Thread.Sleep(Math.Max(1, (int)ms));
    }

    /// <summary>Blocks until <paramref name="deadline"/>, aborting early if stopped.</summary>
    private bool WaitUntil(Stopwatch sw, long deadline, int gen, IntPtr timer, long spinTicks)
    {
        while (true)
        {
            if (!Current(gen)) return false;
            long remain = deadline - sw.ElapsedTicks;
            if (remain <= 0) return true;

            if (remain > spinTicks)
            {
                // Cap each wait so a stop request is still noticed promptly.
                double sleepMs = Math.Min((remain - spinTicks) / TicksPerMs, 30.0);
                SleepPrecise(timer, sleepMs);
            }
            else
            {
                // Only the last fraction of a millisecond is spun, and even that
                // yields, so we never monopolise a core the way a wide spin window
                // did — that was what made games stutter.
                Thread.SpinWait(60);
            }
        }
    }

    private void Loop(EngineSettings s, int gen)
    {
        var (downFlag, upFlag) = Flags(s.Button);
        var downEvt = Mouse(downFlag);
        var upEvt = Mouse(upFlag);

        // A single SendInput call is injected atomically: nothing, including
        // physical mouse movement, can interleave between the press and release.
        // That is precisely what makes this incapable of producing a drag.
        INPUT[] pair = s.DoubleClick
            ? new[] { downEvt, upEvt, downEvt, upEvt }
            : new[] { downEvt, upEvt };
        INPUT[] single = { downEvt };
        INPUT[] release = { upEvt };

        double holdMs = Clamp(s.HoldMs, 0, Math.Min(60, s.IntervalMs * 0.4));
        bool discreteHold = holdMs >= 1.0;
        bool buttonDown = false;

        // Spin only the last sliver before the deadline. Everything earlier is
        // spent parked on the waitable timer, so CPU use stays near zero.
        // Both are per-run locals: sharing them across runs let a retiring worker
        // close the handle a newly started one was already waiting on.
        //
        // The spin buys sub-millisecond accuracy, and it costs a share of a core
        // at AboveNormal priority. That trade is fine with cores to spare and bad
        // on a dual-core machine, where the thread we are competing with is the
        // application being clicked. Below three cores, give the sliver up and
        // accept the timer's own ~0.5 ms slop — a slightly looser interval beats
        // making the target stutter.
        double spinMs = Environment.ProcessorCount <= 2 ? 0.0
                      : s.IntervalMs < 3 ? 0.45
                      : 0.25;
        long spinTicks = (long)(spinMs * TicksPerMs);
        IntPtr timer = CreateWaitableTimerExW(
            IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

        timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        try
        {
            double T = Math.Max(s.IntervalMs, 0.1) * TicksPerMs;
            const double rho = 0.35;
            double sigma = Clamp(s.Variance, 0, 0.6) * T;
            double innov = Math.Sqrt(1 - rho * rho);
            double jitter = 0;
            long n = 0;
            long done = 0;

            while (Current(gen))
            {
                n++;
                jitter = rho * jitter + innov * sigma * Gauss();
                jitter = Clamp(jitter, -0.45 * T, 0.45 * T);
                long deadline = (long)(n * T + jitter);

                if (!WaitUntil(sw, deadline, gen, timer, spinTicks)) break;

                if (s.UseFixedPos && !s.DryRun) SetCursorPos(s.FixedX, s.FixedY);

                if (!s.DryRun)
                {
                    if (discreteHold)
                    {
                        long holdTicks = (long)(holdMs * TicksPerMs);
                        SendInput(1, single, InputSize);
                        buttonDown = true;
                        // Return value ignored on purpose: even when stopping we must
                        // fall through to the release rather than leave it held down.
                        WaitUntil(sw, sw.ElapsedTicks + holdTicks, gen, timer, spinTicks);
                        SendInput(1, release, InputSize);
                        buttonDown = false;

                        if (s.DoubleClick)
                        {
                            WaitUntil(sw, sw.ElapsedTicks + holdTicks, gen, timer, spinTicks);
                            SendInput(1, single, InputSize);
                            buttonDown = true;
                            WaitUntil(sw, sw.ElapsedTicks + holdTicks, gen, timer, spinTicks);
                            SendInput(1, release, InputSize);
                            buttonDown = false;
                        }
                    }
                    else
                    {
                        SendInput((uint)pair.Length, pair, InputSize);
                    }
                }

                done++;
                Interlocked.Increment(ref _count);

                if (s.Limit > 0 && done >= s.Limit) break;
            }
        }
        catch (Exception ex)
        {
            // Subscribers marshal to the UI dispatcher, which throws once it is
            // shutting down. See the note on Finished below.
            try { Error?.Invoke(ex.Message); } catch { /* nothing left to report to */ }
        }
        finally
        {
            // Failsafe: if anything threw between press and release the physical
            // button would otherwise stay latched for the rest of the session.
            if (buttonDown && !s.DryRun)
            {
                try { SendInput(1, release, InputSize); } catch { /* nothing more to do */ }
            }
            timeEndPeriod(1);
            if (timer != IntPtr.Zero) CloseHandle(timer);

            // A superseded run must not clear the flag or raise Finished: the run
            // that replaced it is still going, and the UI would flip to Stopped
            // while clicks kept arriving.
            if (Volatile.Read(ref _generation) == gen)
            {
                _running = false;
                // This is a finally on a background thread, so an exception here
                // escapes unhandled and takes the process with it. The subscriber
                // marshals to the UI dispatcher, which throws if the window is
                // closing at the moment the last run ends — a crash on exit.
                try { Finished?.Invoke(); } catch { /* the UI is already gone */ }
            }
        }
    }

    /// <summary>Releases every mouse button. Cheap insurance on shutdown.</summary>
    public static void ReleaseAllButtons()
    {
        try
        {
            INPUT[] ups =
            {
                Mouse(MOUSEEVENTF_LEFTUP),
                Mouse(MOUSEEVENTF_RIGHTUP),
                Mouse(MOUSEEVENTF_MIDDLEUP),
            };
            SendInput((uint)ups.Length, ups, InputSize);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running = false;
            _generation++;   // retires any worker still unwinding from an earlier run
        }
        _thread?.Join(300);
        ReleaseAllButtons();
    }
}
