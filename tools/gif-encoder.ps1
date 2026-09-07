<#
    Shared animated-GIF encoder. Dot-source this, then call Write-AnimatedGif.

    Two things make the output small and clean:

      * one median-cut palette shared by every frame. System.Drawing's GIF codec
        otherwise quantises to a fixed palette and dithers, which shreds flat UI
        colour into rainbow speckle. Handing it a bitmap that is already
        8bpp-indexed makes it keep our palette and skip dithering entirely.

      * per-frame dirty rectangles. Only the changed region of each frame is
        stored, and frames where nothing moved fold into the previous frame's
        delay instead of being written at all.
#>

Add-Type -AssemblyName System.Drawing

# System.Drawing types live in System.Drawing.Common on modern .NET and are only
# type-forwarded from System.Drawing, so reference the real assembly by path.
$script:GifRefs = @(
    [System.Drawing.Bitmap].Assembly.Location,
    [System.Drawing.Color].Assembly.Location,
    'System.Collections',
    'System.Runtime',
    'System.Runtime.InteropServices'
) | Select-Object -Unique

if (-not ('Quant' -as [type])) {
    Add-Type -ReferencedAssemblies $script:GifRefs @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Quant {
    class Box {
        public List<int> Colors = new List<int>();   // packed 0xRRGGBB
        public int RMin=255,RMax,GMin=255,GMax,BMin=255,BMax;
        public void Measure() {
            RMin=GMin=BMin=255; RMax=GMax=BMax=0;
            foreach (int c in Colors) {
                int r=(c>>16)&0xFF, g=(c>>8)&0xFF, b=c&0xFF;
                if(r<RMin)RMin=r; if(r>RMax)RMax=r;
                if(g<GMin)GMin=g; if(g>GMax)GMax=g;
                if(b<BMin)BMin=b; if(b>BMax)BMax=b;
            }
        }
        public int Range { get { return Math.Max(RMax-RMin, Math.Max(GMax-GMin, BMax-BMin)); } }
    }

    public static Color[] BuildPalette(List<Bitmap> frames, int maxColors, int step) {
        var seen = new HashSet<int>();
        foreach (var bmp in frames) {
            var rect = new Rectangle(0,0,bmp.Width,bmp.Height);
            var d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int len = d.Stride*bmp.Height;
            var buf = new byte[len];
            Marshal.Copy(d.Scan0, buf, 0, len);
            bmp.UnlockBits(d);
            for (int i=0; i+2<len; i += 4*step)
                seen.Add((buf[i+2]<<16)|(buf[i+1]<<8)|buf[i]);
        }

        var root = new Box();
        root.Colors.AddRange(seen);
        root.Measure();
        var boxes = new List<Box> { root };

        while (boxes.Count < maxColors) {
            Box target = null; int best = -1;
            foreach (var b in boxes)
                if (b.Colors.Count > 1 && b.Range > best) { best = b.Range; target = b; }
            if (target == null) break;

            int ch = (target.RMax-target.RMin >= target.GMax-target.GMin &&
                      target.RMax-target.RMin >= target.BMax-target.BMin) ? 16
                   : (target.GMax-target.GMin >= target.BMax-target.BMin) ? 8 : 0;
            target.Colors.Sort((x,y) => ((x>>ch)&0xFF).CompareTo((y>>ch)&0xFF));
            int mid = target.Colors.Count/2;
            var lo = new Box(); lo.Colors.AddRange(target.Colors.GetRange(0, mid));
            var hi = new Box(); hi.Colors.AddRange(target.Colors.GetRange(mid, target.Colors.Count-mid));
            lo.Measure(); hi.Measure();
            boxes.Remove(target); boxes.Add(lo); boxes.Add(hi);
        }

        var pal = new Color[Math.Min(256, boxes.Count)];
        for (int i=0; i<pal.Length; i++) {
            long r=0,g=0,b=0; var cs = boxes[i].Colors;
            foreach (int c in cs) { r+=(c>>16)&0xFF; g+=(c>>8)&0xFF; b+=c&0xFF; }
            int n = Math.Max(1, cs.Count);
            pal[i] = Color.FromArgb((int)(r/n), (int)(g/n), (int)(b/n));
        }
        return pal;
    }

    // RGB555 lookup keeps the nearest-colour search off the per-pixel path.
    public static Bitmap ToIndexed(Bitmap src, Color[] pal, short[] cache) {
        int w = src.Width, h = src.Height;
        var outBmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);
        var cp = outBmp.Palette;
        for (int i=0; i<cp.Entries.Length; i++) cp.Entries[i] = i < pal.Length ? pal[i] : Color.Black;
        outBmp.Palette = cp;

        var rect = new Rectangle(0,0,w,h);
        var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dd = outBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
        var sbuf = new byte[sd.Stride*h];
        var dbuf = new byte[dd.Stride*h];
        Marshal.Copy(sd.Scan0, sbuf, 0, sbuf.Length);

        for (int y=0; y<h; y++) {
            int srow = y*sd.Stride, drow = y*dd.Stride;
            for (int x=0; x<w; x++) {
                int o = srow + x*4;
                int r = sbuf[o+2], g = sbuf[o+1], b = sbuf[o];
                int key = ((r>>3)<<10) | ((g>>3)<<5) | (b>>3);
                short idx = cache[key];
                if (idx < 0) {
                    int bestI = 0, bestD = int.MaxValue;
                    for (int i=0; i<pal.Length; i++) {
                        int dr=r-pal[i].R, dg=g-pal[i].G, db=b-pal[i].B;
                        int dist = dr*dr + dg*dg + db*db;
                        if (dist < bestD) { bestD = dist; bestI = i; }
                    }
                    idx = (short)bestI;
                    cache[key] = idx;
                }
                dbuf[drow + x] = (byte)idx;
            }
        }
        Marshal.Copy(dbuf, 0, dd.Scan0, dbuf.Length);
        src.UnlockBits(sd); outBmp.UnlockBits(dd);
        return outBmp;
    }

    // Changed regions split into horizontal bands.
    //
    // A single bounding box has to span the dead space between, say, a counter
    // and a graph lower down, so most of what it stores is unchanged pixels.
    // Emitting each band separately (all but the last at zero delay, so they
    // paint together) cuts the stored area substantially, which is what buys
    // room for a higher frame rate.
    public static Rectangle[] DirtyBands(Bitmap a, Bitmap b, int gap, int maxBands) {
        int w = a.Width, h = a.Height;
        var rect = new Rectangle(0,0,w,h);
        var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int len = da.Stride*h;
        var ba = new byte[len]; var bb = new byte[len];
        Marshal.Copy(da.Scan0, ba, 0, len);
        Marshal.Copy(db.Scan0, bb, 0, len);
        a.UnlockBits(da); b.UnlockBits(db);

        var rowMin = new int[h]; var rowMax = new int[h];
        for (int y=0; y<h; y++) {
            rowMin[y] = w; rowMax[y] = -1;
            int row = y*da.Stride;
            for (int x=0; x<w; x++) {
                int o = row + x*4;
                if (ba[o]!=bb[o] || ba[o+1]!=bb[o+1] || ba[o+2]!=bb[o+2]) {
                    if (x < rowMin[y]) rowMin[y] = x;
                    if (x > rowMax[y]) rowMax[y] = x;
                }
            }
        }

        var bands = new List<int[]>();
        int cur = -1, last = -1;
        for (int y=0; y<h; y++) {
            if (rowMax[y] >= 0) { if (cur < 0) cur = y; last = y; }
            else if (cur >= 0 && y - last > gap) { bands.Add(new[]{cur,last}); cur = -1; }
        }
        if (cur >= 0) bands.Add(new[]{cur,last});
        if (bands.Count == 0) return new Rectangle[0];

        // Too many bands costs more in per-frame overhead than it saves, so
        // collapse the closest pairs until we are within budget.
        while (bands.Count > maxBands) {
            int bi = 0, bg = int.MaxValue;
            for (int i=0; i+1<bands.Count; i++) {
                int d = bands[i+1][0] - bands[i][1];
                if (d < bg) { bg = d; bi = i; }
            }
            bands[bi] = new[]{ bands[bi][0], bands[bi+1][1] };
            bands.RemoveAt(bi+1);
        }

        var outRects = new Rectangle[bands.Count];
        for (int i=0; i<bands.Count; i++) {
            int y0 = bands[i][0], y1 = bands[i][1];
            int minX = w, maxX = -1;
            for (int y=y0; y<=y1; y++) {
                if (rowMax[y] < 0) continue;
                if (rowMin[y] < minX) minX = rowMin[y];
                if (rowMax[y] > maxX) maxX = rowMax[y];
            }
            outRects[i] = new Rectangle(minX, y0, maxX-minX+1, y1-y0+1);
        }
        return outRects;
    }

    // Bounding box of pixels that differ, or null when the frames match.
    public static Rectangle DirtyRect(Bitmap a, Bitmap b) {
        int w = a.Width, h = a.Height;
        var rect = new Rectangle(0,0,w,h);
        var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int len = da.Stride*h;
        var ba = new byte[len]; var bb = new byte[len];
        Marshal.Copy(da.Scan0, ba, 0, len);
        Marshal.Copy(db.Scan0, bb, 0, len);
        a.UnlockBits(da); b.UnlockBits(db);

        int minX=w, minY=h, maxX=-1, maxY=-1;
        for (int y=0; y<h; y++) {
            int row = y*da.Stride;
            for (int x=0; x<w; x++) {
                int o = row + x*4;
                if (ba[o]!=bb[o] || ba[o+1]!=bb[o+1] || ba[o+2]!=bb[o+2]) {
                    if(x<minX)minX=x; if(x>maxX)maxX=x;
                    if(y<minY)minY=y; if(y>maxY)maxY=y;
                }
            }
        }
        if (maxX < 0) return Rectangle.Empty;
        return new Rectangle(minX, minY, maxX-minX+1, maxY-minY+1);
    }
}
"@
}

# Encode one bitmap through System.Drawing's GIF codec, then lift its palette,
# image descriptor and LZW payload back out for splicing into our own stream.
function Get-GifParts($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Gif)
    $b = $ms.ToArray(); $ms.Dispose()

    $packed = $b[10]
    $hasGct = ($packed -band 0x80) -ne 0
    $gctBits = $packed -band 0x07
    $gctLen = if ($hasGct) { 3 * [math]::Pow(2, $gctBits + 1) } else { 0 }
    $pos = 13 + $gctLen
    $palette = $b[13..(13 + $gctLen - 1)]

    while ($pos -lt $b.Length) {
        if ($b[$pos] -eq 0x2C) { break }
        elseif ($b[$pos] -eq 0x21) {
            $pos += 2
            while ($b[$pos] -ne 0) { $pos += $b[$pos] + 1 }
            $pos++
        }
        else { $pos++ }
    }
    $dataStart = $pos + 10
    $idPacked = $b[$pos + 9]
    if (($idPacked -band 0x80) -ne 0) {
        $lctBits = $idPacked -band 0x07
        $dataStart += 3 * [math]::Pow(2, $lctBits + 1)
    }

    $end = $b.Length - 1
    while ($end -gt $dataStart -and $b[$end] -ne 0x3B) { $end-- }

    return @{
        Palette = $palette
        GctBits = $gctBits
        Data    = $b[$dataStart..($end - 1)]
        Width   = $bmp.Width
        Height  = $bmp.Height
    }
}

function Write-AnimatedGif {
    param(
        [System.Collections.Generic.List[System.Drawing.Bitmap]]$Frames,
        [string]$Path,
        [int]$DelayMs = 100,
        [int]$HoldLastMs = 0
    )

    $palette = [Quant]::BuildPalette($Frames, 256, 7)
    $cache = New-Object short[] 32768
    for ($i = 0; $i -lt 32768; $i++) { $cache[$i] = -1 }

    $cw = $Frames[0].Width; $ch = $Frames[0].Height
    $parts = @()
    $prev = $null
    foreach ($f in $Frames) {
        if ($null -eq $prev) {
            $ix = [Quant]::ToIndexed($f, $palette, $cache)
            $parts += , @{ P = (Get-GifParts $ix); L = 0; T = 0; D = [int]($DelayMs / 10) }
            $ix.Dispose()
        }
        else {
            $rects = [Quant]::DirtyBands($prev, $f, 16, 3)
            if ($rects.Length -eq 0) {
                $parts[-1].D += [int]($DelayMs / 10)
            }
            else {
                for ($k = 0; $k -lt $rects.Length; $k++) {
                    $sub = $f.Clone($rects[$k], [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                    $ix = [Quant]::ToIndexed($sub, $palette, $cache)
                    # Only the final band of a frame carries the delay; the rest
                    # paint immediately so the whole frame lands at once.
                    $d = if ($k -eq $rects.Length - 1) { [int]($DelayMs / 10) } else { 0 }
                    $parts += , @{ P = (Get-GifParts $ix); L = $rects[$k].X; T = $rects[$k].Y; D = $d }
                    $ix.Dispose(); $sub.Dispose()
                }
            }
        }
        $prev = $f
    }
    if ($HoldLastMs -gt 0) { $parts[-1].D += [int]($HoldLastMs / 10) }

    # Named distinctly from any $out/$Out parameter: PowerShell variable names
    # are case-insensitive and would silently collide.
    $gifStream = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $gifStream

    $first = $parts[0].P
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("GIF89a"))
    $bw.Write([UInt16]$cw); $bw.Write([UInt16]$ch)
    $bw.Write([Byte](0xF0 -bor $first.GctBits))
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([Byte[]]$first.Palette)

    $bw.Write([Byte[]]@(0x21, 0xFF, 0x0B))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("NETSCAPE2.0"))
    $bw.Write([Byte[]]@(0x03, 0x01, 0x00, 0x00, 0x00))

    foreach ($p in $parts) {
        $bw.Write([Byte[]]@(0x21, 0xF9, 0x04, 0x04))
        $bw.Write([UInt16]$p.D)
        $bw.Write([Byte[]]@(0x00, 0x00))
        $bw.Write([Byte]0x2C)
        $bw.Write([UInt16]$p.L); $bw.Write([UInt16]$p.T)
        $bw.Write([UInt16]$p.P.Width); $bw.Write([UInt16]$p.P.Height)
        $bw.Write([Byte](0x80 -bor $p.P.GctBits))
        $bw.Write([Byte[]]$p.P.Palette)
        $bw.Write([Byte[]]$p.P.Data)
    }
    $bw.Write([Byte]0x3B)
    $bw.Flush()

    [System.IO.File]::WriteAllBytes($Path, $gifStream.ToArray())
    $bw.Dispose(); $gifStream.Dispose()

    return @{ Stored = $parts.Count; Total = $Frames.Count; Bytes = (Get-Item $Path).Length }
}
