using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BeastClicker.Tools.Gif;

/// <summary>
/// Median-cut quantisation and frame differencing for the GIF encoder.
///
/// One palette is shared by every frame. System.Drawing's GIF codec otherwise
/// quantises to a fixed palette and dithers, which shreds flat UI colour into
/// rainbow speckle; handing it a bitmap that is already 8bpp-indexed makes it
/// keep our palette and skip dithering entirely.
/// </summary>
internal static class Quant
{
    private sealed class Box
    {
        public readonly List<int> Colors = new();   // packed 0xRRGGBB
        public int RMin = 255, RMax, GMin = 255, GMax, BMin = 255, BMax;

        public void Measure()
        {
            RMin = GMin = BMin = 255;
            RMax = GMax = BMax = 0;
            foreach (int c in Colors)
            {
                int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                if (r < RMin) RMin = r;
                if (r > RMax) RMax = r;
                if (g < GMin) GMin = g;
                if (g > GMax) GMax = g;
                if (b < BMin) BMin = b;
                if (b > BMax) BMax = b;
            }
        }

        public int Range => Math.Max(RMax - RMin, Math.Max(GMax - GMin, BMax - BMin));
    }

    public static Color[] BuildPalette(IReadOnlyList<Bitmap> frames, int maxColors, int step)
    {
        var seen = new HashSet<int>();
        foreach (var bmp in frames)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int len = d.Stride * bmp.Height;
            var buf = new byte[len];
            Marshal.Copy(d.Scan0, buf, 0, len);
            bmp.UnlockBits(d);
            for (int i = 0; i + 2 < len; i += 4 * step)
                seen.Add((buf[i + 2] << 16) | (buf[i + 1] << 8) | buf[i]);
        }

        var root = new Box();
        root.Colors.AddRange(seen);
        root.Measure();
        var boxes = new List<Box> { root };

        while (boxes.Count < maxColors)
        {
            Box? target = null;
            int best = -1;
            foreach (var b in boxes)
                if (b.Colors.Count > 1 && b.Range > best) { best = b.Range; target = b; }
            if (target is null) break;

            int ch = (target.RMax - target.RMin >= target.GMax - target.GMin &&
                      target.RMax - target.RMin >= target.BMax - target.BMin) ? 16
                   : (target.GMax - target.GMin >= target.BMax - target.BMin) ? 8 : 0;
            target.Colors.Sort((x, y) => ((x >> ch) & 0xFF).CompareTo((y >> ch) & 0xFF));
            int mid = target.Colors.Count / 2;
            var lo = new Box();
            lo.Colors.AddRange(target.Colors.GetRange(0, mid));
            var hi = new Box();
            hi.Colors.AddRange(target.Colors.GetRange(mid, target.Colors.Count - mid));
            lo.Measure();
            hi.Measure();
            boxes.Remove(target);
            boxes.Add(lo);
            boxes.Add(hi);
        }

        var pal = new Color[Math.Min(256, boxes.Count)];
        for (int i = 0; i < pal.Length; i++)
        {
            long r = 0, g = 0, b = 0;
            var cs = boxes[i].Colors;
            foreach (int c in cs) { r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; }
            int n = Math.Max(1, cs.Count);
            pal[i] = Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
        }
        return pal;
    }

    /// <summary>
    /// Maps a frame onto the shared palette. An RGB555 lookup keeps the
    /// nearest-colour search off the per-pixel path.
    /// </summary>
    public static Bitmap ToIndexed(Bitmap src, Color[] pal, short[] cache)
    {
        int w = src.Width, h = src.Height;
        var outBmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);
        var cp = outBmp.Palette;
        for (int i = 0; i < cp.Entries.Length; i++)
            cp.Entries[i] = i < pal.Length ? pal[i] : Color.Black;
        outBmp.Palette = cp;

        var rect = new Rectangle(0, 0, w, h);
        var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dd = outBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
        var sbuf = new byte[sd.Stride * h];
        var dbuf = new byte[dd.Stride * h];
        Marshal.Copy(sd.Scan0, sbuf, 0, sbuf.Length);

        for (int y = 0; y < h; y++)
        {
            int srow = y * sd.Stride, drow = y * dd.Stride;
            for (int x = 0; x < w; x++)
            {
                int o = srow + x * 4;
                int r = sbuf[o + 2], g = sbuf[o + 1], b = sbuf[o];
                int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                short idx = cache[key];
                if (idx < 0)
                {
                    int bestI = 0, bestD = int.MaxValue;
                    for (int i = 0; i < pal.Length; i++)
                    {
                        int dr = r - pal[i].R, dg = g - pal[i].G, db = b - pal[i].B;
                        int dist = dr * dr + dg * dg + db * db;
                        if (dist < bestD) { bestD = dist; bestI = i; }
                    }
                    idx = (short)bestI;
                    cache[key] = idx;
                }
                dbuf[drow + x] = (byte)idx;
            }
        }
        Marshal.Copy(dbuf, 0, dd.Scan0, dbuf.Length);
        src.UnlockBits(sd);
        outBmp.UnlockBits(dd);
        return outBmp;
    }

    /// <summary>
    /// Changed regions split into horizontal bands.
    ///
    /// A single bounding box has to span the dead space between, say, a counter
    /// and a graph lower down, so most of what it stores is unchanged pixels.
    /// Emitting each band separately (all but the last at zero delay, so they
    /// paint together) cuts the stored area substantially, which is what buys
    /// room for a higher frame rate.
    /// </summary>
    public static Rectangle[] DirtyBands(Bitmap a, Bitmap b, int gap, int maxBands)
    {
        int w = a.Width, h = a.Height;
        var rect = new Rectangle(0, 0, w, h);
        var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int len = da.Stride * h;
        var ba = new byte[len];
        var bb = new byte[len];
        Marshal.Copy(da.Scan0, ba, 0, len);
        Marshal.Copy(db.Scan0, bb, 0, len);
        int stride = da.Stride;
        a.UnlockBits(da);
        b.UnlockBits(db);

        var rowMin = new int[h];
        var rowMax = new int[h];
        for (int y = 0; y < h; y++)
        {
            rowMin[y] = w;
            rowMax[y] = -1;
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int o = row + x * 4;
                if (ba[o] != bb[o] || ba[o + 1] != bb[o + 1] || ba[o + 2] != bb[o + 2])
                {
                    if (x < rowMin[y]) rowMin[y] = x;
                    if (x > rowMax[y]) rowMax[y] = x;
                }
            }
        }

        var bands = new List<int[]>();
        int cur = -1, last = -1;
        for (int y = 0; y < h; y++)
        {
            if (rowMax[y] >= 0) { if (cur < 0) cur = y; last = y; }
            else if (cur >= 0 && y - last > gap) { bands.Add(new[] { cur, last }); cur = -1; }
        }
        if (cur >= 0) bands.Add(new[] { cur, last });
        if (bands.Count == 0) return Array.Empty<Rectangle>();

        // Too many bands costs more in per-frame overhead than it saves, so
        // collapse the closest pairs until we are within budget.
        while (bands.Count > maxBands)
        {
            int bi = 0, bg = int.MaxValue;
            for (int i = 0; i + 1 < bands.Count; i++)
            {
                int d = bands[i + 1][0] - bands[i][1];
                if (d < bg) { bg = d; bi = i; }
            }
            bands[bi] = new[] { bands[bi][0], bands[bi + 1][1] };
            bands.RemoveAt(bi + 1);
        }

        var outRects = new Rectangle[bands.Count];
        for (int i = 0; i < bands.Count; i++)
        {
            int y0 = bands[i][0], y1 = bands[i][1];
            int minX = w, maxX = -1;
            for (int y = y0; y <= y1; y++)
            {
                if (rowMax[y] < 0) continue;
                if (rowMin[y] < minX) minX = rowMin[y];
                if (rowMax[y] > maxX) maxX = rowMax[y];
            }
            outRects[i] = new Rectangle(minX, y0, maxX - minX + 1, y1 - y0 + 1);
        }
        return outRects;
    }
}
