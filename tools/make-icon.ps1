# Generates app.ico: a cursor mark on a soft blue rounded square.
# Drawn per-size rather than downscaled, so the 16px entry stays legible.
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded-square plate
    $inset = [Math]::Max(1, [int]($S * 0.045))
    $side = $S - ($inset * 2)
    $r = [Math]::Max(2, [int]($S * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($inset, $inset, $d, $d, 180, 90)
    $path.AddArc($inset + $side - $d, $inset, $d, $d, 270, 90)
    $path.AddArc($inset + $side - $d, $inset + $side - $d, $d, $d, 0, 90)
    $path.AddArc($inset, $inset + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle $inset, $inset, $side, $side
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 90, 150, 246),
        [System.Drawing.Color]::FromArgb(255, 43, 104, 208),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($brush, $path)

    # click pulse, only where there is room for it to read
    if ($S -ge 32) {
        $pw = [Math]::Max(1.0, $S * 0.055)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(115, 255, 255, 255)), $pw
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        foreach ($f in @(0.30, 0.42)) {
            $rr = $S * $f
            $cx = $S * 0.40; $cy = $S * 0.44
            $g.DrawArc($pen, [float]($cx - $rr), [float]($cy - $rr), [float]($rr * 2), [float]($rr * 2), 285, 105)
        }
        $pen.Dispose()
    }

    # cursor arrow
    $pts = @(
        @(0.325, 0.205), @(0.325, 0.760), @(0.455, 0.632),
        @(0.552, 0.822), @(0.655, 0.772), @(0.556, 0.590), @(0.720, 0.575)
    ) | ForEach-Object {
        New-Object System.Drawing.PointF ([float]($_[0] * $S)), ([float]($_[1] * $S))
    }

    # soft drop shadow lifts the mark off the plate
    if ($S -ge 32) {
        $off = $S * 0.022
        $shadowPts = $pts | ForEach-Object {
            New-Object System.Drawing.PointF ([float]($_.X + $off)), ([float]($_.Y + $off))
        }
        $sb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 12, 40, 90))
        $g.FillPolygon($sb, [System.Drawing.PointF[]]$shadowPts)
        $sb.Dispose()
    }

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($white, [System.Drawing.PointF[]]$pts)
    $white.Dispose()

    $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$blobs = @()
foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += , $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

# assemble the ICO container (PNG-compressed entries, supported since Vista)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$blobs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush()

$dest = Join-Path $PSScriptRoot '..\src\BeastClicker\Assets\app.ico'
[System.IO.File]::WriteAllBytes($dest, $out.ToArray())
$bw.Dispose(); $out.Dispose()

# a large PNG too, purely so the result can be eyeballed
$preview = New-Frame 256
$preview.Save((Join-Path $PSScriptRoot '..\docs\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

"wrote $dest ({0:N1} KB, sizes: $($sizes -join ', '))" -f ((Get-Item $dest).Length / 1KB)
