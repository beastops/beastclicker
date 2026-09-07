<#
    Renders docs/banner.gif — the animated README hero.

    Everything except the app window is drawn from scratch, so the motion can be
    designed rather than screen-recorded: the counter races, the rate graph
    scrolls, and the loop lands back where it started.

    The numbers shown are the real measured ones from the benchmark sweep.
#>
[CmdletBinding()]
param(
    [string]$Shot = (Join-Path $PSScriptRoot '..\docs\screenshot.png'),
    [string]$Out  = (Join-Path $PSScriptRoot '..\docs\banner.gif'),
    [int]$Width   = 860,
    [int]$Height  = 360,
    [int]$Fps     = 25,
    [double]$Seconds = 4.0
)

. (Join-Path $PSScriptRoot 'gif-encoder.ps1')

$D = [System.Drawing.Drawing2D.SmoothingMode]
$rgb = { param($r, $g, $b, $a = 255) [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

# Named distinctly from the $Shot parameter: variable names are case-insensitive
# in PowerShell, and $Shot is type-constrained to [string].
$shotImg = [System.Drawing.Image]::FromFile((Resolve-Path $Shot))

$fontTitle = New-Object System.Drawing.Font 'Segoe UI', 40, ([System.Drawing.FontStyle]::Bold)
$fontTag   = New-Object System.Drawing.Font 'Segoe UI', 12.5, ([System.Drawing.FontStyle]::Regular)
$fontBig   = New-Object System.Drawing.Font 'Segoe UI', 30, ([System.Drawing.FontStyle]::Bold)
$fontUnit  = New-Object System.Drawing.Font 'Segoe UI', 10, ([System.Drawing.FontStyle]::Bold)
$fontPill  = New-Object System.Drawing.Font 'Segoe UI', 10.5, ([System.Drawing.FontStyle]::Bold)

$white   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$soft    = New-Object System.Drawing.SolidBrush (& $rgb 206 224 252)
$dim     = New-Object System.Drawing.SolidBrush (& $rgb 168 199 245)
$mint    = New-Object System.Drawing.SolidBrush (& $rgb 134 239 172)

# eased 0..1 that settles before the loop point
function Ease([double]$t) { return 1 - [Math]::Pow(1 - [Math]::Min(1, $t), 3) }

function New-BannerFrame([int]$i, [int]$n) {
    $t = $i / [double]$n                       # 0..1 through the loop
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = $D::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    # ---- backdrop ----
    $rect = New-Object System.Drawing.Rectangle 0, 0, $Width, $Height
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, (& $rgb 74 137 246), (& $rgb 30 78 190),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillRectangle($bg, $rect); $bg.Dispose()

    # Static light streaks. Deliberately not animated: moving them would make
    # every frame fully dirty and defeat the per-frame diff, multiplying file
    # size several times over for scenery nobody looks at.
    for ($s = 0; $s -lt 3; $s++) {
        $off = 150 + $s * 250
        $pen = New-Object System.Drawing.Pen (& $rgb 255 255 255 14), 90
        $g.DrawLine($pen, [float]$off, -60.0, [float]($off - 190), [float]($Height + 60))
        $pen.Dispose()
    }

    # ---- app window, right side ----
    $wh = 286; $ww = [int]($shotImg.Width * $wh / $shotImg.Height)
    $wx = $Width - $ww - 58; $wy = [int](($Height - $wh) / 2)
    for ($k = 12; $k -ge 1; $k--) {
        $b = New-Object System.Drawing.SolidBrush (& $rgb 12 34 78 ([int](2 + (12 - $k) * 1.1)))
        $g.FillRectangle($b, ($wx - $k), ($wy - $k + 4), ($ww + $k * 2), ($wh + $k * 2))
        $b.Dispose()
    }
    $g.DrawImage($shotImg, $wx, $wy, $ww, $wh)

    # ---- title block ----
    $x = 58
    $g.DrawString('Beast Clicker', $fontTitle, $white, [float]($x - 4), 52.0)
    $g.DrawString('Precise  ·  Lightweight  ·  Measured', $fontTag, $soft, [float]$x, 112.0)

    # ---- counter, running the whole loop ----
    # Counts continuously rather than settling early, and cross-fades over the
    # loop seam so the wrap back to zero reads as a dissolve instead of a jump.
    $count = [int]([Math]::Round(82555 * [Math]::Min(1.0, $t / 0.88)))
    $fade = 1.0
    if ($t -gt 0.90) { $fade = 1.0 - (($t - 0.90) / 0.10) }
    elseif ($t -lt 0.07) { $fade = $t / 0.07 }
    $fade = [Math]::Max(0.0, [Math]::Min(1.0, $fade))
    $cb = New-Object System.Drawing.SolidBrush (& $rgb 255 255 255 ([int](255 * $fade)))
    $g.DrawString($count.ToString('N0'), $fontBig, $cb, [float]$x, 168.0)
    $cb.Dispose()
    $g.DrawString('CLICKS DELIVERED', $fontUnit, $dim, [float]($x + 2), 212.0)

    $g.DrawString('1,000', $fontBig, $mint, [float]($x + 212), 168.0)
    $g.DrawString('PER SECOND, VERIFIED', $fontUnit, $dim, [float]($x + 214), 212.0)

    # ---- scrolling rate graph ----
    # Bar count is capped so the row ends before the stat pill; overlapping the
    # two looked like a layout bug.
    $bars = 22; $bw = 9; $gap = 5; $baseY = 312; $maxH = 58
    for ($b = 0; $b -lt $bars; $b++) {
        $phase = ($b / [double]$bars) * 6.28318 * 2 - $t * 6.28318 * 2
        $h = [int]($maxH * (0.42 + 0.58 * (0.5 + 0.5 * [Math]::Sin($phase))))
        $f = $b / [double]($bars - 1)
        $col = & $rgb ([int](134 + (147 - 134) * $f)) ([int](239 + (197 - 239) * $f)) ([int](172 + (253 - 172) * $f)) 235
        $br = New-Object System.Drawing.SolidBrush $col
        $g.FillRectangle($br, ($x + $b * ($bw + $gap)), ($baseY - $h), $bw, $h)
        $br.Dispose()
    }
    $g.DrawString('LIVE CLICK RATE', $fontUnit, $dim, [float]($x + 2), 322.0)

    # ---- pill: the CPU headline ----
    $pillW = 164; $pillH = 30; $px = $x + 330; $py = 268
    $pill = New-Object System.Drawing.SolidBrush (& $rgb 255 255 255 38)
    $g.FillRectangle($pill, $px, $py, $pillW, $pillH); $pill.Dispose()
    $g.DrawString('~1% CPU at 100/sec', $fontPill, $white, [float]($px + 13), [float]($py + 7))

    $g.Dispose()
    return $bmp
}

$total = [int]($Fps * $Seconds)
Write-Host "rendering $total banner frames (${Width}x${Height})..."
$frames = New-Object 'System.Collections.Generic.List[System.Drawing.Bitmap]'
for ($i = 0; $i -lt $total; $i++) { $frames.Add((New-BannerFrame $i $total)) }

Write-Host "encoding..."
$r = Write-AnimatedGif -Frames $frames -Path $Out -DelayMs ([int](1000 / $Fps))

# GitHub's social preview has to be a static 1280x640 image. A GIF will not
# animate in a link preview, so render one frame of the banner onto that canvas
# rather than pointing the setting at the animation.
$card = New-Object System.Drawing.Bitmap 1280, 640
$cg = [System.Drawing.Graphics]::FromImage($card)
$cg.SmoothingMode = $D::AntiAlias
$cg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$cardRect = New-Object System.Drawing.Rectangle 0, 0, 1280, 640
$cardBg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $cardRect, (& $rgb 74 137 246), (& $rgb 30 78 190),
    [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
$cg.FillRectangle($cardBg, $cardRect); $cardBg.Dispose()

$peak = New-BannerFrame ([int]($total * 0.86)) $total
$sw = 1280; $sh = [int](1280 * $Height / $Width)
$cg.DrawImage($peak, 0, [int]((640 - $sh) / 2), $sw, $sh)
$peak.Dispose(); $cg.Dispose()
$card.Save((Join-Path $PSScriptRoot '..\docs\social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$card.Dispose()
Write-Host 'wrote docs/social-preview.png (1280x640, upload in repo Settings)'

foreach ($f in $frames) { $f.Dispose() }
$shotImg.Dispose()
foreach ($o in @($fontTitle, $fontTag, $fontBig, $fontUnit, $fontPill, $white, $soft, $dim, $mint)) { $o.Dispose() }

"wrote $Out ({0:N0} KB, {1} stored frames of {2})" -f ($r.Bytes / 1KB), $r.Stored, $r.Total
