using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BeastClicker.Tools;

/// <summary>
/// Generates app.ico: a cursor mark on a soft blue rounded square.
/// Drawn per-size rather than downscaled, so the 16px entry stays legible.
/// </summary>
public static class IconGenerator
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    // Normalised cursor outline, shared with the social card.
    internal static readonly (double X, double Y)[] Glyph =
    {
        (0.325, 0.205), (0.325, 0.760), (0.455, 0.632),
        (0.552, 0.822), (0.655, 0.772), (0.556, 0.590), (0.720, 0.575),
    };

    public static Bitmap NewFrame(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        // rounded-square plate
        int inset = Math.Max(1, Ps.Int(s * 0.045));
        int side = s - inset * 2;
        int r = Math.Max(2, Ps.Int(s * 0.22));
        int d = r * 2;

        using var path = new GraphicsPath();
        path.AddArc(inset, inset, d, d, 180, 90);
        path.AddArc(inset + side - d, inset, d, d, 270, 90);
        path.AddArc(inset + side - d, inset + side - d, d, d, 0, 90);
        path.AddArc(inset, inset + side - d, d, d, 90, 90);
        path.CloseFigure();

        var rect = new Rectangle(inset, inset, side, side);
        using (var brush = new LinearGradientBrush(rect,
                   Color.FromArgb(255, 90, 150, 246),
                   Color.FromArgb(255, 43, 104, 208),
                   LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(brush, path);
        }

        // click pulse, only where there is room for it to read
        if (s >= 32)
        {
            float pw = (float)Math.Max(1.0, s * 0.055);
            using var pen = new Pen(Color.FromArgb(115, 255, 255, 255), pw)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            foreach (double f in new[] { 0.30, 0.42 })
            {
                double rr = s * f;
                double cx = s * 0.40, cy = s * 0.44;
                g.DrawArc(pen, (float)(cx - rr), (float)(cy - rr), (float)(rr * 2), (float)(rr * 2), 285, 105);
            }
        }

        var pts = Glyph.Select(p => new PointF((float)(p.X * s), (float)(p.Y * s))).ToArray();

        // soft drop shadow lifts the mark off the plate
        if (s >= 32)
        {
            double off = s * 0.022;
            var shadow = pts.Select(p => new PointF((float)(p.X + off), (float)(p.Y + off))).ToArray();
            using var sb = new SolidBrush(Color.FromArgb(55, 12, 40, 90));
            g.FillPolygon(sb, shadow);
        }

        using (var white = new SolidBrush(Color.White))
        {
            g.FillPolygon(white, pts);
        }

        return bmp;
    }

    public static void Run(string repoRoot)
    {
        var blobs = new List<byte[]>();
        foreach (int s in Sizes)
        {
            using var bmp = NewFrame(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            blobs.Add(ms.ToArray());
        }

        // assemble the ICO container (PNG-compressed entries, supported since Vista)
        using var outStream = new MemoryStream();
        using (var bw = new BinaryWriter(outStream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                byte dim = Sizes[i] >= 256 ? (byte)0 : (byte)Sizes[i];
                bw.Write(dim);
                bw.Write(dim);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)blobs[i].Length);
                bw.Write((uint)offset);
                offset += blobs[i].Length;
            }
            foreach (var b in blobs) bw.Write(b);
        }

        string dest = Path.Combine(repoRoot, "src", "BeastClicker", "Assets", "app.ico");
        File.WriteAllBytes(dest, outStream.ToArray());

        // a large PNG too, purely so the result can be eyeballed
        using (var preview = NewFrame(256))
        {
            preview.Save(Path.Combine(repoRoot, "docs", "icon.png"), ImageFormat.Png);
        }

        Console.WriteLine($"wrote {dest} ({new FileInfo(dest).Length / 1024.0:N1} KB, sizes: {string.Join(", ", Sizes)})");
    }
}
