using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BeastClicker.Tools.Gif;

namespace BeastClicker.Tools;

/// <summary>
/// Records the running app into docs/demo.gif for the README.
///
/// Frames are captured with PrintWindow (so the window need not be foreground)
/// and composited onto a padded backdrop with a soft shadow. The app is driven
/// through its own global hotkeys, with the pointer parked on the window's own
/// backdrop so the demo clicks land on nothing.
/// </summary>
public static class DemoCapture
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    // GetWindowRect includes Windows' invisible resize border, which PrintWindow
    // never paints — that is where the black margins came from. The extended
    // frame bounds give the actually-painted rectangle, so capture is cropped to it.
    private const int DwmwaExtendedFrameBounds = 9;

    private const byte VkF6 = 0x75;   // HkToggle
    private const byte VkF8 = 0x77;   // HkStop
    private const uint KeyUp = 2;

    public static void Run(string repoRoot, int fps = 20, double seconds = 7.0,
                           double startAt = 1.2, double stopAt = 5.6, double scale = 0.8)
    {
        string outPath = Path.Combine(repoRoot, "docs", "demo.gif");
        string exe = Path.Combine(repoRoot, "dist", "BeastClicker.exe");

        var proc = Process.GetProcessesByName("BeastClicker").FirstOrDefault();
        if (proc is null)
        {
            proc = Process.Start(exe) ?? throw new InvalidOperationException($"could not start {exe}");
            Thread.Sleep(4000);
            proc.Refresh();
        }

        IntPtr hwnd = proc.MainWindowHandle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Beast Clicker window not found.");

        GetWindowRect(hwnd, out var r);
        int fullW = r.R - r.L, fullH = r.B - r.T;

        int cropX, cropY, ww, wh;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var ext, Marshal.SizeOf<RECT>()) == 0)
        {
            cropX = ext.L - r.L;
            cropY = ext.T - r.T;
            ww = ext.R - ext.L;
            wh = ext.B - ext.T;
        }
        else
        {
            cropX = cropY = 0;
            ww = fullW;
            wh = fullH;
        }
        Console.WriteLine($"window {fullW} x {fullH} -> painted {ww} x {wh} " +
                          $"(trimmed {fullW - ww} x {fullH - wh} of unpainted border)");

        // Park the pointer on the window's own backdrop so the demo clicks hit nothing.
        SetCursorPos(r.L + 270, r.T + 250);
        Thread.Sleep(400);

        // The extended frame bounds still run a pixel proud of the painted content
        // on some editions, leaving a hairline black frame. Measure it once instead
        // of hardcoding, so this keeps working if the metrics change.
        var trim = new Trim();
        using (var probe = Capture(hwnd, fullW, fullH, cropX, cropY, ww, wh, trim))
        {
            trim = MeasureBlackEdges(probe);
        }
        ww -= trim.L + trim.R;
        wh -= trim.T + trim.B;
        Console.WriteLine($"trimming black edge L{trim.L} T{trim.T} R{trim.R} B{trim.B} -> {ww}x{wh}");

        const int pad = 26;
        int cw = Ps.Int((ww + pad * 2) * scale);
        int ch = Ps.Int((wh + pad * 2) * scale);

        // Refresh the still too, so the banner embeds a window image with the same
        // border trimming rather than a stale one with black edges.
        using (var still = Capture(hwnd, fullW, fullH, cropX, cropY, ww, wh, trim))
        {
            still.Save(Path.Combine(repoRoot, "docs", "screenshot.png"), ImageFormat.Png);
        }

        int total = Ps.Int(fps * seconds);
        int delayMs = Ps.Int(1000.0 / fps);
        int startFrame = Ps.Int(fps * startAt);
        int stopFrame = Ps.Int(fps * stopAt);

        Console.WriteLine($"capturing {total} frames at {fps}fps ({cw}x{ch})...");

        // Capture raw only. Compositing the shadow during the loop cost ~37ms a
        // frame and capped the real rate well below the requested one, so it moves
        // to a second pass where it costs nothing but wall clock.
        var raws = new List<Bitmap>(total);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < total; i++)
        {
            if (i == startFrame) Tap(VkF6);
            if (i == stopFrame) Tap(VkF8);
            raws.Add(Capture(hwnd, fullW, fullH, cropX, cropY, ww, wh, trim));
            Thread.Sleep(delayMs);
        }
        Tap(VkF8);   // make sure the demo never leaves it running
        clock.Stop();

        // Capturing each frame costs real time on top of the sleep, so the true
        // interval is longer than requested. Encoding with the measured value keeps
        // the GIF playing at the speed the app actually ran at.
        int realDelayMs = (int)Math.Round(clock.Elapsed.TotalMilliseconds / total);
        Console.WriteLine($"requested {delayMs}ms/frame, measured {realDelayMs}ms/frame");

        Console.WriteLine("compositing...");
        var frames = new List<Bitmap>(raws.Count);
        foreach (var raw in raws)
        {
            frames.Add(Composite(raw, cw, ch, ww, wh, pad, scale));
            raw.Dispose();
        }

        Console.WriteLine("encoding...");
        var res = GifWriter.Write(frames, outPath, realDelayMs);
        foreach (var f in frames) f.Dispose();

        Console.WriteLine($"wrote {outPath} ({res.Bytes / 1024.0:N0} KB, " +
                          $"{res.Stored} stored frames of {res.Total} captured)");
    }

    private static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, KeyUp, IntPtr.Zero);
    }

    private readonly struct Trim
    {
        public int L { get; init; }
        public int T { get; init; }
        public int R { get; init; }
        public int B { get; init; }
    }

    /// <summary>
    /// PrintWindow always renders the whole window rect, so capture that and then
    /// crop to the painted area rather than keeping the black border.
    /// </summary>
    private static Bitmap Capture(IntPtr hwnd, int fullW, int fullH,
                                  int cropX, int cropY, int ww, int wh, Trim trim)
    {
        using var full = new Bitmap(fullW, fullH);
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, 2);
            g.ReleaseHdc(hdc);
        }

        // ww/wh already have the measured edge removed, so only the origin shifts.
        var rect = new Rectangle(cropX + trim.L, cropY + trim.T, ww, wh);
        var cropped = full.Clone(rect, PixelFormat.Format32bppArgb);
        BlackToAlpha(cropped, 12);
        return cropped;
    }

    /// <summary>
    /// Windows 11 rounds window corners and PrintWindow fills the cut-away area
    /// with black. Punching those pixels to transparent lets the corners take
    /// whatever they are composited onto instead of showing black wedges.
    /// </summary>
    private static void BlackToAlpha(Bitmap b, int thresh)
    {
        var rect = new Rectangle(0, 0, b.Width, b.Height);
        var d = b.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int len = d.Stride * b.Height;
        var buf = new byte[len];
        Marshal.Copy(d.Scan0, buf, 0, len);
        for (int i = 0; i + 3 < len; i += 4)
            if (buf[i] < thresh && buf[i + 1] < thresh && buf[i + 2] < thresh) buf[i + 3] = 0;
        Marshal.Copy(buf, 0, d.Scan0, len);
        b.UnlockBits(d);
    }

    private static Trim MeasureBlackEdges(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        static bool IsBlack(Color c) => c.R < 12 && c.G < 12 && c.B < 12;

        int t = 0;
        while (t < 6)
        {
            int n = 0;
            for (int x = 0; x < w; x++) if (IsBlack(bmp.GetPixel(x, t))) n++;
            if (n < w * 0.5) break;
            t++;
        }
        int b = 0;
        while (b < 6)
        {
            int n = 0;
            for (int x = 0; x < w; x++) if (IsBlack(bmp.GetPixel(x, h - 1 - b))) n++;
            if (n < w * 0.5) break;
            b++;
        }
        int l = 0;
        while (l < 6)
        {
            int n = 0;
            for (int y = 0; y < h; y++) if (IsBlack(bmp.GetPixel(l, y))) n++;
            if (n < h * 0.5) break;
            l++;
        }
        int rr = 0;
        while (rr < 6)
        {
            int n = 0;
            for (int y = 0; y < h; y++) if (IsBlack(bmp.GetPixel(w - 1 - rr, y))) n++;
            if (n < h * 0.5) break;
            rr++;
        }
        return new Trim { L = l, T = t, R = rr, B = b };
    }

    private static Bitmap Composite(Bitmap winBmp, int cw, int ch, int ww, int wh, int pad, double scale)
    {
        var c = new Bitmap(cw, ch);
        using var g = Graphics.FromImage(c);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.FromArgb(255, 238, 241, 246));

        int x = Ps.Int(pad * scale), y = Ps.Int(pad * scale);
        int w = Ps.Int(ww * scale), h = Ps.Int(wh * scale);

        // stacked translucent rounds stand in for a blur
        for (int i = 9; i >= 1; i--)
        {
            int a = Ps.Int(3 + (9 - i) * 1.6);
            using var b = new SolidBrush(Color.FromArgb(a, 40, 60, 95));
            g.FillRectangle(b, x - i, y - i + 3, w + i * 2, h + i * 2);
        }

        g.DrawImage(winBmp, x, y, w, h);
        return c;
    }
}
