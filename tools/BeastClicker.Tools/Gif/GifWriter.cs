using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace BeastClicker.Tools.Gif;

public readonly record struct GifResult(int Stored, int Total, long Bytes);

/// <summary>
/// Animated GIF writer.
///
/// Two things make the output small and clean: one median-cut palette shared by
/// every frame (see <see cref="Quant"/>), and per-frame dirty rectangles, so only
/// the changed region of each frame is stored and frames where nothing moved fold
/// into the previous frame's delay instead of being written at all.
/// </summary>
public static class GifWriter
{
    private sealed record Parts(byte[] Palette, int GctBits, byte[] Data, int Width, int Height);

    private sealed class Segment
    {
        public required Parts P;
        public int Left;
        public int Top;
        public int Delay;      // centiseconds
    }

    /// <summary>
    /// Encodes one bitmap through System.Drawing's GIF codec, then lifts its
    /// palette, image descriptor and LZW payload back out for splicing into our
    /// own stream. Reusing the platform codec avoids hand-rolling LZW.
    /// </summary>
    private static Parts Extract(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Gif);
        byte[] b = ms.ToArray();

        int packed = b[10];
        bool hasGct = (packed & 0x80) != 0;
        int gctBits = packed & 0x07;
        int gctLen = hasGct ? 3 * (1 << (gctBits + 1)) : 0;

        var palette = new byte[gctLen];
        Array.Copy(b, 13, palette, 0, gctLen);

        int pos = 13 + gctLen;
        while (pos < b.Length)
        {
            if (b[pos] == 0x2C) break;
            if (b[pos] == 0x21)
            {
                pos += 2;
                while (b[pos] != 0) pos += b[pos] + 1;
                pos++;
            }
            else pos++;
        }

        int dataStart = pos + 10;
        int idPacked = b[pos + 9];
        if ((idPacked & 0x80) != 0)
            dataStart += 3 * (1 << ((idPacked & 0x07) + 1));

        int end = b.Length - 1;
        while (end > dataStart && b[end] != 0x3B) end--;

        var data = new byte[end - dataStart];
        Array.Copy(b, dataStart, data, 0, data.Length);

        return new Parts(palette, gctBits, data, bmp.Width, bmp.Height);
    }

    public static GifResult Write(IReadOnlyList<Bitmap> frames, string path,
                                  int delayMs = 100, int holdLastMs = 0)
    {
        if (frames.Count == 0) throw new ArgumentException("no frames", nameof(frames));

        var palette = Quant.BuildPalette(frames, 256, 7);
        var cache = new short[32768];
        Array.Fill(cache, (short)-1);

        int cw = frames[0].Width, ch = frames[0].Height;
        var segments = new List<Segment>();
        Bitmap? prev = null;

        foreach (var f in frames)
        {
            if (prev is null)
            {
                using var ix = Quant.ToIndexed(f, palette, cache);
                segments.Add(new Segment { P = Extract(ix), Left = 0, Top = 0, Delay = Ps.Int(delayMs / 10.0) });
            }
            else
            {
                var rects = Quant.DirtyBands(prev, f, 16, 3);
                if (rects.Length == 0)
                {
                    segments[^1].Delay += Ps.Int(delayMs / 10.0);
                }
                else
                {
                    for (int k = 0; k < rects.Length; k++)
                    {
                        using var sub = f.Clone(rects[k], PixelFormat.Format32bppArgb);
                        using var ix = Quant.ToIndexed(sub, palette, cache);
                        // Only the final band of a frame carries the delay; the
                        // rest paint immediately so the whole frame lands at once.
                        int d = k == rects.Length - 1 ? Ps.Int(delayMs / 10.0) : 0;
                        segments.Add(new Segment
                        {
                            P = Extract(ix),
                            Left = rects[k].X,
                            Top = rects[k].Y,
                            Delay = d,
                        });
                    }
                }
            }
            prev = f;
        }

        if (holdLastMs > 0) segments[^1].Delay += Ps.Int(holdLastMs / 10.0);

        using var gif = new MemoryStream();
        using (var bw = new BinaryWriter(gif, Encoding.ASCII, leaveOpen: true))
        {
            var first = segments[0].P;
            bw.Write(Encoding.ASCII.GetBytes("GIF89a"));
            bw.Write((ushort)cw);
            bw.Write((ushort)ch);
            bw.Write((byte)(0xF0 | first.GctBits));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write(first.Palette);

            // NETSCAPE2.0 application extension: loop forever.
            bw.Write(new byte[] { 0x21, 0xFF, 0x0B });
            bw.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            bw.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

            foreach (var s in segments)
            {
                bw.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 });
                bw.Write((ushort)s.Delay);
                bw.Write(new byte[] { 0x00, 0x00 });
                bw.Write((byte)0x2C);
                bw.Write((ushort)s.Left);
                bw.Write((ushort)s.Top);
                bw.Write((ushort)s.P.Width);
                bw.Write((ushort)s.P.Height);
                bw.Write((byte)(0x80 | s.P.GctBits));
                bw.Write(s.P.Palette);
                bw.Write(s.P.Data);
            }
            bw.Write((byte)0x3B);
        }

        File.WriteAllBytes(path, gif.ToArray());
        return new GifResult(segments.Count, frames.Count, new FileInfo(path).Length);
    }
}
