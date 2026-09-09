using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using BeastClicker.Tools.Gif;

namespace BeastClicker.Tools;

/// <summary>
/// Renders docs/banner.gif, the animated README hero.
///
/// Everything except the app window is drawn from scratch, so the motion can be
/// designed rather than screen-recorded: the counter races, the rate graph
/// scrolls, and the loop lands back where it started. The numbers shown are the
/// real measured ones from the benchmark sweep.
/// </summary>
public static class Banner
{
    private static Color Rgb(int r, int g, int b, int a = 255) => Color.FromArgb(a, r, g, b);

    public static void Run(string repoRoot, int width = 860, int height = 360,
                           int fps = 25, double seconds = 4.0)
    {
        string shotPath = Path.Combine(repoRoot, "docs", "screenshot.png");
        string outPath = Path.Combine(repoRoot, "docs", "banner.gif");

        using var shot = Image.FromFile(shotPath);

        using var fontTitle = new Font("Segoe UI", 40, FontStyle.Bold);
        using var fontTag = new Font("Segoe UI", 12.5f, FontStyle.Regular);
        using var fontBig = new Font("Segoe UI", 30, FontStyle.Bold);
        using var fontUnit = new Font("Segoe UI", 10, FontStyle.Bold);
        using var fontPill = new Font("Segoe UI", 10.5f, FontStyle.Bold);

        using var white = new SolidBrush(Color.White);
        using var soft = new SolidBrush(Rgb(206, 224, 252));
        using var dim = new SolidBrush(Rgb(168, 199, 245));
        using var mint = new SolidBrush(Rgb(134, 239, 172));

        int total = Ps.Int(fps * seconds);
        Console.WriteLine($"rendering {total} banner frames ({width}x{height})...");

        var frames = new List<Bitmap>(total);
        for (int i = 0; i < total; i++)
        {
            frames.Add(Frame(i, total, width, height, shot,
                             fontTitle, fontTag, fontBig, fontUnit, fontPill,
                             white, soft, dim, mint));
        }

        Console.WriteLine("encoding...");
        var r = GifWriter.Write(frames, outPath, Ps.Int(1000.0 / fps));

        foreach (var f in frames) f.Dispose();

        Console.WriteLine($"wrote {outPath} ({r.Bytes / 1024.0:N0} KB, {r.Stored} stored frames of {r.Total})");
    }

    private static Bitmap Frame(int i, int n, int width, int height, Image shot,
                                Font fontTitle, Font fontTag, Font fontBig, Font fontUnit, Font fontPill,
                                Brush white, Brush soft, Brush dim, Brush mint)
    {
        double t = i / (double)n;                       // 0..1 through the loop
        var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // ---- backdrop ----
        var rect = new Rectangle(0, 0, width, height);
        using (var bg = new LinearGradientBrush(rect, Rgb(74, 137, 246), Rgb(30, 78, 190),
                                                LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(bg, rect);
        }

        // Static light streaks. Deliberately not animated: moving them would make
        // every frame fully dirty and defeat the per-frame diff, multiplying file
        // size several times over for scenery nobody looks at.
        for (int s = 0; s < 3; s++)
        {
            int off = 150 + s * 250;
            using var pen = new Pen(Rgb(255, 255, 255, 14), 90);
            g.DrawLine(pen, off, -60f, off - 190, height + 60f);
        }

        // ---- app window, right side ----
        int wh = 286;
        int ww = Ps.Int((double)shot.Width * wh / shot.Height);
        int wx = width - ww - 58;
        int wy = Ps.Int((height - wh) / 2.0);
        for (int k = 12; k >= 1; k--)
        {
            using var b = new SolidBrush(Rgb(12, 34, 78, Ps.Int(2 + (12 - k) * 1.1)));
            g.FillRectangle(b, wx - k, wy - k + 4, ww + k * 2, wh + k * 2);
        }
        g.DrawImage(shot, wx, wy, ww, wh);

        // ---- title block ----
        const int x = 58;
        g.DrawString("Beast Clicker", fontTitle, white, x - 4f, 52f);
        g.DrawString("Precise  ·  Lightweight  ·  Measured", fontTag, soft, x, 112f);

        // ---- counter, running the whole loop ----
        // Counts continuously rather than settling early, and cross-fades over the
        // loop seam so the wrap back to zero reads as a dissolve instead of a jump.
        int count = (int)Math.Round(82555 * Math.Min(1.0, t / 0.88));
        double fade = 1.0;
        if (t > 0.90) fade = 1.0 - (t - 0.90) / 0.10;
        else if (t < 0.07) fade = t / 0.07;
        fade = Math.Max(0.0, Math.Min(1.0, fade));
        using (var cb = new SolidBrush(Rgb(255, 255, 255, Ps.Int(255 * fade))))
        {
            g.DrawString(count.ToString("N0"), fontBig, cb, x, 168f);
        }
        g.DrawString("CLICKS DELIVERED", fontUnit, dim, x + 2f, 212f);

        g.DrawString("1,000", fontBig, mint, x + 212f, 168f);
        g.DrawString("PER SECOND, VERIFIED", fontUnit, dim, x + 214f, 212f);

        // ---- scrolling rate graph ----
        // Bar count is capped so the row ends before the stat pill; overlapping the
        // two looked like a layout bug.
        const int bars = 22, bw = 9, gap = 5, baseY = 312, maxH = 58;
        for (int b = 0; b < bars; b++)
        {
            double phase = b / (double)bars * 6.28318 * 2 - t * 6.28318 * 2;
            int h = Ps.Int(maxH * (0.42 + 0.58 * (0.5 + 0.5 * Math.Sin(phase))));
            double f = b / (double)(bars - 1);
            var col = Rgb(Ps.Int(134 + (147 - 134) * f),
                          Ps.Int(239 + (197 - 239) * f),
                          Ps.Int(172 + (253 - 172) * f), 235);
            using var br = new SolidBrush(col);
            g.FillRectangle(br, x + b * (bw + gap), baseY - h, bw, h);
        }
        g.DrawString("LIVE CLICK RATE", fontUnit, dim, x + 2f, 322f);

        // ---- pill: the CPU headline ----
        const int pillW = 164, pillH = 30, px = x + 330, py = 268;
        using (var pill = new SolidBrush(Rgb(255, 255, 255, 38)))
        {
            g.FillRectangle(pill, px, py, pillW, pillH);
        }
        g.DrawString("~1% CPU at 100/sec", fontPill, white, px + 13f, py + 7f);

        return bmp;
    }
}
