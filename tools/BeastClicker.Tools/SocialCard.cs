using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace BeastClicker.Tools;

/// <summary>
/// Renders docs/social-preview.png, the 1280x640 card GitHub shows when a link
/// to the repo is posted.
///
/// Laid out for the size it is actually viewed at. Link previews are often
/// rendered around 600px wide, so the type is large, the content sits well
/// inside the edges, and there are three numbers rather than a paragraph.
/// </summary>
public static class SocialCard
{
    private const int W = 1280;
    private const int H = 640;

    private static Color Rgb(int r, int g, int b, int a = 255) => Color.FromArgb(a, r, g, b);

    public static void Run(string repoRoot)
    {
        string shotPath = Path.Combine(repoRoot, "docs", "screenshot.png");
        string outPath = Path.Combine(repoRoot, "docs", "social-preview.png");

        using var shot = Image.FromFile(shotPath);
        using var bmp = new Bitmap(W, H);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // ---- background ----
            var rect = new Rectangle(0, 0, W, H);
            using (var bg = new LinearGradientBrush(rect, Rgb(78, 141, 247), Rgb(24, 62, 168),
                                                    LinearGradientMode.ForwardDiagonal))
            {
                g.FillRectangle(bg, rect);
            }

            // a soft light source behind the window, so the right side does not read flat
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(700, -160, 800, 800);
                using var gb = new PathGradientBrush(glow)
                {
                    CenterColor = Rgb(255, 255, 255, 46),
                    SurroundColors = new[] { Rgb(255, 255, 255, 0) },
                };
                g.FillPath(gb, glow);
            }

            // quiet diagonal banding
            for (int i = 0; i < 4; i++)
            {
                int x0 = 120 + i * 240;
                using var pen = new Pen(Rgb(255, 255, 255, 12), 110);
                g.DrawLine(pen, x0, -80f, x0 - 210, H + 80f);
            }

            // ---- app window, right ----
            int wh = 452;
            int ww = Ps.Int((double)shot.Width * wh / shot.Height);
            int wx = W - ww - 76;
            int wy = Ps.Int((H - wh) / 2.0);
            for (int k = 18; k >= 1; k--)
            {
                using var b = new SolidBrush(Rgb(8, 26, 66, Ps.Int(2 + (18 - k) * 0.9)));
                g.FillRectangle(b, wx - k, wy - k + 6, ww + k * 2, wh + k * 2);
            }
            g.DrawImage(shot, wx, wy, ww, wh);

            // ---- left column ----
            const int x = 76;

            // The icon's own blue plate disappears against a blue background, so the
            // cursor mark is drawn on its own in white. Normalised points come from
            // the icon generator, remapped so the glyph's bounding box lands exactly
            // where we want it.
            const float gx = 76f, gy = 84f, gw = 42f, gh = 66f;
            var pts = IconGenerator.Glyph
                .Select(p => new PointF(
                    (float)(gx + (p.X - 0.325) / 0.395 * gw),
                    (float)(gy + (p.Y - 0.205) / 0.617 * gh)))
                .ToArray();
            var shadowPts = pts.Select(p => new PointF(p.X + 2, p.Y + 3)).ToArray();
            using (var sb = new SolidBrush(Rgb(8, 26, 66, 70))) g.FillPolygon(sb, shadowPts);
            using (var wb = new SolidBrush(Color.White)) g.FillPolygon(wb, pts);

            using var fTitle = new Font("Segoe UI", 54, FontStyle.Bold);
            using var fTag = new Font("Segoe UI", 19);
            using var fNum = new Font("Segoe UI", 30, FontStyle.Bold);
            using var fLab = new Font("Segoe UI", 11, FontStyle.Bold);
            using var fUrl = new Font("Segoe UI", 15);

            using var white = new SolidBrush(Color.White);
            using var tag = new SolidBrush(Rgb(205, 224, 252));
            using var label = new SolidBrush(Rgb(154, 190, 243));
            using var mint = new SolidBrush(Rgb(134, 239, 172));

            g.DrawString("Beast Clicker", fTitle, white, x - 6f, 176f);
            g.DrawString("A fast, precise auto clicker for Windows", fTag, tag, x, 262f);

            using (var rule = new Pen(Rgb(255, 255, 255, 58), 1))
            {
                g.DrawLine(rule, x, 322f, 620f, 322f);
            }

            // three measured figures, not marketing copy
            (string N, string U, string L, bool M)[] stats =
            {
                ("1,000", "/sec", "VERIFIED RATE", true),
                ("~1%", "", "CPU AT 100/SEC", false),
                ("280", "KB", "SINGLE FILE", false),
            };
            float sx = x;
            foreach (var s in stats)
            {
                var brush = s.M ? mint : white;
                g.DrawString(s.N, fNum, brush, sx, 350f);
                float nw = g.MeasureString(s.N, fNum).Width;
                if (s.U.Length > 0) g.DrawString(s.U, fTag, tag, sx + nw - 8, 364f);
                g.DrawString(s.L, fLab, label, sx + 2, 398f);
                sx += 196;
            }

            g.DrawString("github.com/beastops/beastclicker", fUrl, tag, x, 502f);
        }

        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1024.0:N0} KB, {W}x{H})");
    }
}
