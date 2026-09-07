<#
    Renders docs/social-preview.png, the 1280x640 card GitHub shows when a link
    to the repo is posted.

    Laid out for the size it is actually viewed at. Link previews are often
    rendered around 600px wide, so the type is large, the content sits well
    inside the edges, and there are three numbers rather than a paragraph.
#>
[CmdletBinding()]
param(
    [string]$Shot = (Join-Path $PSScriptRoot '..\docs\screenshot.png'),
    [string]$Icon = (Join-Path $PSScriptRoot '..\docs\icon.png'),
    [string]$Out  = (Join-Path $PSScriptRoot '..\docs\social-preview.png')
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$W = 1280; $H = 640
$rgb = { param($r, $g, $b, $a = 255) [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

$shotImg = [System.Drawing.Image]::FromFile((Resolve-Path $Shot))
$iconImg = [System.Drawing.Image]::FromFile((Resolve-Path $Icon))

$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

# ---- background -------------------------------------------------------------
$rect = New-Object System.Drawing.Rectangle 0, 0, $W, $H
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect, (& $rgb 78 141 247), (& $rgb 24 62 168),
    [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
$g.FillRectangle($bg, $rect); $bg.Dispose()

# a soft light source behind the window, so the right side does not read flat
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(700, -160, 800, 800)
$gb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
$gb.CenterColor = & $rgb 255 255 255 46
$gb.SurroundColors = @(& $rgb 255 255 255 0)
$g.FillPath($gb, $glow)
$gb.Dispose(); $glow.Dispose()

# quiet diagonal banding
for ($i = 0; $i -lt 4; $i++) {
    $x = 120 + $i * 240
    $pen = New-Object System.Drawing.Pen (& $rgb 255 255 255 12), 110
    $g.DrawLine($pen, [float]$x, -80.0, [float]($x - 210), [float]($H + 80))
    $pen.Dispose()
}

# ---- app window, right ------------------------------------------------------
$wh = 452
$ww = [int]($shotImg.Width * $wh / $shotImg.Height)
$wx = $W - $ww - 76
$wy = [int](($H - $wh) / 2)
for ($k = 18; $k -ge 1; $k--) {
    $b = New-Object System.Drawing.SolidBrush (& $rgb 8 26 66 ([int](2 + (18 - $k) * 0.9)))
    $g.FillRectangle($b, ($wx - $k), ($wy - $k + 6), ($ww + $k * 2), ($wh + $k * 2))
    $b.Dispose()
}
$g.DrawImage($shotImg, $wx, $wy, $ww, $wh)

# ---- left column ------------------------------------------------------------
$x = 76

# The icon's own blue plate disappears against a blue background, so the cursor
# mark is drawn on its own in white. Normalised points come from make-icon.ps1,
# remapped so the glyph's bounding box lands exactly where we want it.
$glyph = @(
    @(0.325, 0.205), @(0.325, 0.760), @(0.455, 0.632),
    @(0.552, 0.822), @(0.655, 0.772), @(0.556, 0.590), @(0.720, 0.575)
)
$gx = 76.0; $gy = 84.0; $gw = 42.0; $gh = 66.0
$pts = $glyph | ForEach-Object {
    New-Object System.Drawing.PointF `
        ([float]($gx + (($_[0] - 0.325) / 0.395) * $gw)), `
        ([float]($gy + (($_[1] - 0.205) / 0.617) * $gh))
}
$shadowPts = $pts | ForEach-Object {
    New-Object System.Drawing.PointF ([float]($_.X + 2)), ([float]($_.Y + 3))
}
$sb = New-Object System.Drawing.SolidBrush (& $rgb 8 26 66 70)
$g.FillPolygon($sb, [System.Drawing.PointF[]]$shadowPts); $sb.Dispose()
$wb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$g.FillPolygon($wb, [System.Drawing.PointF[]]$pts); $wb.Dispose()

$fTitle = New-Object System.Drawing.Font 'Segoe UI', 54, ([System.Drawing.FontStyle]::Bold)
$fTag   = New-Object System.Drawing.Font 'Segoe UI', 19
$fNum   = New-Object System.Drawing.Font 'Segoe UI', 30, ([System.Drawing.FontStyle]::Bold)
$fLab   = New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.FontStyle]::Bold)
$fUrl   = New-Object System.Drawing.Font 'Segoe UI', 15

$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$tag   = New-Object System.Drawing.SolidBrush (& $rgb 205 224 252)
$label = New-Object System.Drawing.SolidBrush (& $rgb 154 190 243)
$mint  = New-Object System.Drawing.SolidBrush (& $rgb 134 239 172)

$g.DrawString('Beast Clicker', $fTitle, $white, [float]($x - 6), 176.0)
$g.DrawString('A fast, precise auto clicker for Windows', $fTag, $tag, [float]$x, 262.0)

$rule = New-Object System.Drawing.Pen (& $rgb 255 255 255 58), 1
$g.DrawLine($rule, [float]$x, 322.0, 620.0, 322.0)
$rule.Dispose()

# three measured figures, not marketing copy
$stats = @(
    @{ n = '1,000'; u = '/sec'; l = 'VERIFIED RATE';  m = $true  },
    @{ n = '~1%';   u = '';     l = 'CPU AT 100/SEC'; m = $false },
    @{ n = '280';   u = 'KB';   l = 'SINGLE FILE';    m = $false }
)
$sx = $x
foreach ($s in $stats) {
    $brush = if ($s.m) { $mint } else { $white }
    $g.DrawString($s.n, $fNum, $brush, [float]$sx, 350.0)
    $nw = $g.MeasureString($s.n, $fNum).Width
    if ($s.u) { $g.DrawString($s.u, $fTag, $tag, [float]($sx + $nw - 8), 364.0) }
    $g.DrawString($s.l, $fLab, $label, [float]($sx + 2), 398.0)
    $sx += 196
}

$g.DrawString('github.com/zbeastcorp/beastclicker', $fUrl, $tag, [float]$x, 502.0)

$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

foreach ($o in @($bmp, $shotImg, $iconImg, $fTitle, $fTag, $fNum, $fLab, $fUrl,
                 $white, $tag, $label, $mint)) { $o.Dispose() }

"wrote $Out ({0:N0} KB, ${W}x${H})" -f ((Get-Item $Out).Length / 1KB)
