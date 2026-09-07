using System.Diagnostics;
using System.Runtime.InteropServices;
using BeastClicker;

// Two checks:
//   schedule  - does the achieved click rate match the configured rate, and does
//               adding timing variance cost any throughput? (input disabled)
//   emit      - does SendInput actually deliver correctly paired down/up events?
//               Verified with a low-level mouse hook. Clicks are aimed at the
//               Beast Clicker window's own background so nothing else is touched.

if (args.Length > 0 && args[0] == "emit")
    EmitTest.Run();
else if (args.Length > 0 && args[0] == "cpu")
    CpuTest.Run();
else if (args.Length > 0 && args[0] == "watch")
    WatchTest.Run();
else if (args.Length > 0 && args[0] == "realrate")
    RealRateTest.Run(args.Length > 1 ? double.Parse(args[1]) : 1.0);
else if (args.Length > 0 && args[0] == "stress")
    StressTest.Run(
        args.Length > 1 ? double.Parse(args[1]) : 10.0,
        args.Length > 2 ? int.Parse(args[2]) : 180);
else
    ScheduleTest.Run();

/// <summary>
/// Sustained run with periodic sampling. Short benchmarks cannot catch drift,
/// CPU creep or leaks that only appear after minutes, which is the failure mode
/// being chased here. Clicks land on the Beast Clicker window's own background.
/// </summary>
static class StressTest
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static long _down, _up;

    public static void Run(double intervalMs, int seconds)
    {
        var hwnd = Process.GetProcessesByName("BeastClicker")
            .Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("FAIL  start Beast Clicker first."); return; }
        GetWindowRect(hwnd, out var r);
        int x = r.Left + 250, y = r.Top + 300;

        double target = 1000.0 / intervalMs;
        Console.WriteLine($"Stress: {intervalMs}ms interval (target {target:N0}/sec) for {seconds}s");
        Console.WriteLine($"clicking at {x},{y} — Beast Clicker's own background\n");
        Console.WriteLine("   t     window/sec   drift    CPU%    RAM MB   down/up");
        Console.WriteLine(new string('-', 60));

        HookProc proc = Cb;
        var hook = SetWindowsHookExW(WH_MOUSE_LL, proc, IntPtr.Zero, 0);
        if (hook == IntPtr.Zero) { Console.WriteLine("FAIL  could not install hook"); return; }

        uint tid = GetCurrentThreadId();
        var engine = new ClickEngine();
        var self = Process.GetCurrentProcess();

        new Thread(() =>
        {
            engine.Start(new EngineSettings
            {
                IntervalMs = intervalMs,
                UseFixedPos = true,
                FixedX = x,
                FixedY = y,
                Variance = 0.18,
                HoldMs = 0,
            });

            var total = Stopwatch.StartNew();
            var lastCpu = self.TotalProcessorTime;
            long lastDown = 0;
            double lastT = 0;

            while (total.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(15000);

                double t = total.Elapsed.TotalSeconds;
                double dt = t - lastT;
                long d = Interlocked.Read(ref _down);
                double rate = (d - lastDown) / dt;

                self.Refresh();
                var cpu = self.TotalProcessorTime;
                double cpuPct = (cpu - lastCpu).TotalSeconds / dt * 100.0;
                double ram = self.WorkingSet64 / 1024.0 / 1024.0;
                double drift = (rate - target) / target * 100.0;

                Console.WriteLine(
                    $"{t,5:N0}s   {rate,10:N1}   {drift,6:N2}%  {cpuPct,6:N1}  {ram,8:N1}   " +
                    $"{Interlocked.Read(ref _down)}/{Interlocked.Read(ref _up)}");

                lastCpu = cpu; lastDown = d; lastT = t;
            }

            engine.Stop();
            Thread.Sleep(300);
            PostThreadMessage(tid, 0x0012, IntPtr.Zero, IntPtr.Zero);
        }).Start();

        while (GetMessage(out _, IntPtr.Zero, 0, 0)) { }
        UnhookWindowsHookEx(hook);
        engine.Dispose();

        long dn = Interlocked.Read(ref _down), up = Interlocked.Read(ref _up);
        Console.WriteLine();
        Console.WriteLine(dn == up
            ? $"PASS  {dn:N0} presses, {up:N0} releases — perfectly balanced, no stuck button"
            : $"FAIL  {dn:N0} presses vs {up:N0} releases — a button was left held");
    }

    private static IntPtr Cb(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = wParam.ToInt32();
            if (m == WM_LBUTTONDOWN) Interlocked.Increment(ref _down);
            else if (m == WM_LBUTTONUP) Interlocked.Increment(ref _up);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
/// <summary>
/// Runs OUR engine for real and counts the events Windows actually delivers, so
/// the delivered rate can be compared like-for-like against another clicker.
/// Clicks are aimed at the Beast Clicker window's own background.
/// </summary>
static class RealRateTest
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static int _down;

    public static void Run(double intervalMs)
    {
        var hwnd = Process.GetProcessesByName("BeastClicker")
            .Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("FAIL  start Beast Clicker first."); return; }
        GetWindowRect(hwnd, out var r);
        int x = r.Left + 250, y = r.Top + 300;

        HookProc proc = Cb;
        var hook = SetWindowsHookExW(WH_MOUSE_LL, proc, IntPtr.Zero, 0);
        if (hook == IntPtr.Zero) { Console.WriteLine("FAIL  could not install hook"); return; }

        uint tid = GetCurrentThreadId();
        var engine = new ClickEngine();
        var proc2 = Process.GetCurrentProcess();
        var cpu0 = proc2.TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        new Thread(() =>
        {
            engine.Start(new EngineSettings
            {
                IntervalMs = intervalMs,
                UseFixedPos = true,
                FixedX = x,
                FixedY = y,
                Variance = 0.18,
                HoldMs = 0,
            });
            Thread.Sleep(4000);
            engine.Stop();
            Thread.Sleep(200);
            PostThreadMessage(tid, 0x0012, IntPtr.Zero, IntPtr.Zero);
        }).Start();

        while (GetMessage(out _, IntPtr.Zero, 0, 0)) { }
        sw.Stop();
        UnhookWindowsHookEx(hook);
        var cpuUsed = proc2.TotalProcessorTime - cpu0;
        engine.Dispose();

        double secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"ours @ {intervalMs}ms: {_down} clicks in {secs:N1}s -> {_down / secs:N1} clicks/sec, " +
            $"CPU {cpuUsed.TotalSeconds / secs * 100:N1}% of one core");
    }

    private static IntPtr Cb(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN) _down++;
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}

/// <summary>
/// Counts mouse events produced by some OTHER app, to measure what a competing
/// clicker actually delivers. Drives nothing itself.
/// </summary>
static class WatchTest
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }

    private static int _down;

    public static void Run()
    {
        Console.WriteLine("Counting left-button-down events for 4s.");
        Console.WriteLine("Start the other clicker now (its own hotkey).\n");

        HookProc proc = Cb;
        var hook = SetWindowsHookExW(WH_MOUSE_LL, proc, IntPtr.Zero, 0);
        if (hook == IntPtr.Zero) { Console.WriteLine("FAIL  could not install hook"); return; }

        uint tid = GetCurrentThreadId();
        var sw = Stopwatch.StartNew();
        new Thread(() =>
        {
            Thread.Sleep(4000);
            PostThreadMessage(tid, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }).Start();

        while (GetMessage(out _, IntPtr.Zero, 0, 0)) { }
        sw.Stop();
        UnhookWindowsHookEx(hook);

        double secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"saw {_down} clicks in {secs:N1}s  ->  {_down / secs:N1} clicks/sec");
    }

    private static IntPtr Cb(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN) _down++;
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}

static class CpuTest
{
    public static void Run()
    {
        Console.WriteLine("How much CPU the engine burns while idling between clicks.");
        Console.WriteLine("100% = one core fully consumed.\n");
        Console.WriteLine("interval   targetCPS   actualCPS    CPU (1 core)");
        Console.WriteLine(new string('-', 52));

        foreach (double interval in new[] { 100.0, 50.0, 25.0, 10.0, 1.0 })
        {
            using var engine = new ClickEngine();
            var proc = Process.GetCurrentProcess();

            engine.Start(new EngineSettings { IntervalMs = interval, DryRun = true });
            Thread.Sleep(400); // let it settle before sampling

            var cpu0 = proc.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            long c0 = engine.ClickCount;
            Thread.Sleep(4000);
            long clicks = engine.ClickCount - c0;
            wall.Stop();
            var cpuUsed = proc.TotalProcessorTime - cpu0;
            engine.Stop();
            Thread.Sleep(100);

            double pct = cpuUsed.TotalSeconds / wall.Elapsed.TotalSeconds * 100.0;
            double actual = clicks / wall.Elapsed.TotalSeconds;
            Console.WriteLine(
                $"{interval,6:N0}ms   {1000.0 / interval,9:N1}   {actual,9:N1}   {pct,10:N1}%");
        }
    }
}

static class ScheduleTest
{
    public static void Run()
    {
        Console.WriteLine("interval   variance   targetCPS   actualCPS      error");
        Console.WriteLine(new string('-', 56));

        (double interval, double variance)[] cases =
        {
            (1, 0.00), (1, 0.18),
            (5, 0.18),
            (10, 0.00), (10, 0.18), (10, 0.35),
            (50, 0.18),
            (100, 0.18),
        };

        foreach (var (interval, variance) in cases)
        {
            using var engine = new ClickEngine();
            var sw = Stopwatch.StartNew();
            engine.Start(new EngineSettings
            {
                IntervalMs = interval,
                Variance = variance,
                DryRun = true,
            });

            Thread.Sleep(3000);
            long count = engine.ClickCount;
            sw.Stop();
            engine.Stop();

            double elapsed = sw.Elapsed.TotalSeconds;
            double target = 1000.0 / interval;
            double actual = count / elapsed;
            double err = (actual - target) / target * 100.0;

            Console.WriteLine(
                $"{interval,5:N0}ms   {variance,8:N2}   {target,9:N1}   {actual,9:N1}   {err,8:N2}%");
        }
    }
}

static class EmitTest
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowW(string? cls, string? name);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static int _down, _up;
    private static readonly List<int> _order = new();

    public static void Run()
    {
        var hwnd = Process.GetProcessesByName("BeastClicker")
            .Select(p => p.MainWindowHandle)
            .FirstOrDefault(h => h != IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("FAIL  Beast Clicker window not found — start it first.");
            return;
        }
        GetWindowRect(hwnd, out var r);
        // A point inside the window's own group-box background: receiving clicks
        // there does nothing at all.
        int x = r.Left + 250, y = r.Top + 300;
        Console.WriteLine($"aiming at {x},{y} (inside the Beast Clicker window)");

        HookProc proc = HookCb;
        var hook = SetWindowsHookExW(WH_MOUSE_LL, proc, IntPtr.Zero, 0);
        if (hook == IntPtr.Zero)
        {
            Console.WriteLine("FAIL  could not install mouse hook");
            return;
        }

        uint hookThread = GetCurrentThreadId();
        const int target = 25;

        var worker = new Thread(() =>
        {
            using var engine = new ClickEngine();
            engine.Start(new EngineSettings
            {
                IntervalMs = 20,
                Limit = target,
                UseFixedPos = true,
                FixedX = x,
                FixedY = y,
                Variance = 0.18,
                HoldMs = 0,
            });
            while (engine.IsRunning) Thread.Sleep(20);
            Thread.Sleep(250);
            PostThreadMessage(hookThread, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        });
        worker.Start();

        // The low-level hook only fires while this thread pumps messages.
        while (GetMessage(out _, IntPtr.Zero, 0, 0)) { }

        UnhookWindowsHookEx(hook);
        worker.Join(1000);

        Console.WriteLine($"requested {target} clicks -> saw {_down} down, {_up} up");

        bool paired = _down == _up && _down >= target;
        // Any two consecutive same-direction events would mean a press was left
        // open, which is exactly the state that produces a drag.
        bool alternating = true;
        for (int i = 1; i < _order.Count; i++)
            if (_order[i] == _order[i - 1]) { alternating = false; break; }

        Console.WriteLine(paired ? "PASS  every press has a matching release" : "FAIL  down/up counts do not match");
        Console.WriteLine(alternating ? "PASS  events strictly alternate down,up (no held button)" : "FAIL  consecutive same-direction events");
    }

    private static IntPtr HookCb(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN) { _down++; _order.Add(0); }
            else if (msg == WM_LBUTTONUP) { _up++; _order.Add(1); }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
