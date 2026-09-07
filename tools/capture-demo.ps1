<#
    Records the running app into docs/demo.gif for the README.

    Frames are captured with PrintWindow (so the window need not be foreground)
    and composited onto a padded backdrop with a soft shadow. Encoding is handled
    by gif-encoder.ps1, which is shared with make-banner.ps1.

    The app is driven through its own global hotkeys, with the pointer parked on
    the window's own backdrop so the demo clicks land on nothing.
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\BeastClicker.exe'),
    [string]$Out = (Join-Path $PSScriptRoot '..\docs\demo.gif'),
    # Past roughly this rate the app's own 100ms status refresh becomes the
    # limit, and extra captures are just duplicates that fold away anyway.
    [int]$Fps = 20,
    [double]$Seconds = 7.0,
    [double]$StartAt = 1.2,
    [double]$StopAt = 5.6,
    [double]$Scale = 0.8
)

. (Join-Path $PSScriptRoot 'gif-encoder.ps1')

if (-not ('Win' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    public struct RECT { public int L, T, R, B; }
}
"@
}

# GetWindowRect includes Windows' invisible resize border, which PrintWindow
# never paints — that is where the black margins came from. The extended frame
# bounds give the actually-painted rectangle, so capture is cropped to it.
$DWMWA_EXTENDED_FRAME_BOUNDS = 9

if (-not ('Px' -as [type])) {
    # Windows 11 rounds window corners and PrintWindow fills the cut-away area
    # with black. Punching those pixels to transparent lets the corners take
    # whatever they are composited onto instead of showing black wedges.
    Add-Type -ReferencedAssemblies $script:GifRefs @"
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Px {
    public static void BlackToAlpha(Bitmap b, int thresh) {
        var rect = new Rectangle(0, 0, b.Width, b.Height);
        var d = b.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int len = d.Stride * b.Height;
        var buf = new byte[len];
        Marshal.Copy(d.Scan0, buf, 0, len);
        for (int i = 0; i + 3 < len; i += 4)
            if (buf[i] < thresh && buf[i+1] < thresh && buf[i+2] < thresh) buf[i+3] = 0;
        Marshal.Copy(buf, 0, d.Scan0, len);
        b.UnlockBits(d);
    }
}
"@
}

$proc = Get-Process BeastClicker -ErrorAction SilentlyContinue
if (-not $proc) {
    $proc = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 4
}
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "Beast Clicker window not found." }

$r = New-Object Win+RECT
[void][Win]::GetWindowRect($hwnd, [ref]$r)
$fullW = $r.R - $r.L; $fullH = $r.B - $r.T

$ext = New-Object Win+RECT
$hr = [Win]::DwmGetWindowAttribute($hwnd, $DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$ext, 16)
if ($hr -eq 0) {
    $cropX = $ext.L - $r.L; $cropY = $ext.T - $r.T
    $ww = $ext.R - $ext.L;  $wh = $ext.B - $ext.T
}
else {
    $cropX = 0; $cropY = 0; $ww = $fullW; $wh = $fullH
}
Write-Host "window $fullW x $fullH -> painted $ww x $wh (trimmed $($fullW-$ww) x $($fullH-$wh) of unpainted border)"

# Park the pointer on the window's own backdrop so the demo clicks hit nothing.
[void][Win]::SetCursorPos(($r.L + 270), ($r.T + 250))
Start-Sleep -Milliseconds 400

function Get-WindowFrame {
    # PrintWindow always renders the whole window rect, so capture that and then
    # crop to the painted area rather than keeping the black border.
    $full = New-Object System.Drawing.Bitmap $fullW, $fullH
    $g = [System.Drawing.Graphics]::FromImage($full)
    $hdc = $g.GetHdc()
    [void][Win]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()

    # $ww/$wh already have the measured edge removed, so only the origin shifts.
    $rect = New-Object System.Drawing.Rectangle ($cropX + $script:TrimL), ($cropY + $script:TrimT), $ww, $wh
    $cropped = $full.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $full.Dispose()
    [Px]::BlackToAlpha($cropped, 12)
    return $cropped
}

# The extended frame bounds still run a pixel proud of the painted content on
# some editions, leaving a hairline black frame. Measure it once instead of
# hardcoding, so this keeps working if the metrics change.
function Measure-BlackEdges($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $isBlack = { param($c) $c.R -lt 12 -and $c.G -lt 12 -and $c.B -lt 12 }

    $t = 0; while ($t -lt 6) {
        $n = 0; for ($x = 0; $x -lt $w; $x++) { if (& $isBlack $bmp.GetPixel($x, $t)) { $n++ } }
        if ($n -lt $w * 0.5) { break }; $t++
    }
    $b = 0; while ($b -lt 6) {
        $n = 0; for ($x = 0; $x -lt $w; $x++) { if (& $isBlack $bmp.GetPixel($x, ($h - 1 - $b))) { $n++ } }
        if ($n -lt $w * 0.5) { break }; $b++
    }
    $l = 0; while ($l -lt 6) {
        $n = 0; for ($y = 0; $y -lt $h; $y++) { if (& $isBlack $bmp.GetPixel($l, $y)) { $n++ } }
        if ($n -lt $h * 0.5) { break }; $l++
    }
    $rr = 0; while ($rr -lt 6) {
        $n = 0; for ($y = 0; $y -lt $h; $y++) { if (& $isBlack $bmp.GetPixel(($w - 1 - $rr), $y)) { $n++ } }
        if ($n -lt $h * 0.5) { break }; $rr++
    }
    return @{ L = $l; T = $t; R = $rr; B = $b }
}

$script:TrimL = 0; $script:TrimT = 0; $script:TrimR = 0; $script:TrimB = 0
$probe = Get-WindowFrame
$edges = Measure-BlackEdges $probe
$probe.Dispose()
$script:TrimL = $edges.L; $script:TrimT = $edges.T
$script:TrimR = $edges.R; $script:TrimB = $edges.B
$ww = $ww - $edges.L - $edges.R
$wh = $wh - $edges.T - $edges.B
Write-Host "trimming black edge L$($edges.L) T$($edges.T) R$($edges.R) B$($edges.B) -> ${ww}x${wh}"

$pad = 26
$cw = [int](($ww + $pad * 2) * $Scale)
$ch = [int](($wh + $pad * 2) * $Scale)

function New-Composited($winBmp) {
    $c = New-Object System.Drawing.Bitmap $cw, $ch
    $g = [System.Drawing.Graphics]::FromImage($c)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::FromArgb(255, 238, 241, 246))

    $x = [int]($pad * $Scale); $y = [int]($pad * $Scale)
    $w = [int]($ww * $Scale);  $h = [int]($wh * $Scale)

    # stacked translucent rounds stand in for a blur
    for ($i = 9; $i -ge 1; $i--) {
        $a = [int](3 + (9 - $i) * 1.6)
        $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, 40, 60, 95))
        $g.FillRectangle($b, ($x - $i), ($y - $i + 3), ($w + $i * 2), ($h + $i * 2))
        $b.Dispose()
    }

    $g.DrawImage($winBmp, $x, $y, $w, $h)
    $g.Dispose()
    return $c
}

$total = [int]($Fps * $Seconds)
$delayMs = [int](1000 / $Fps)
$startFrame = [int]($Fps * $StartAt)
$stopFrame = [int]($Fps * $StopAt)

# Refresh the still too, so the banner embeds a window image with the same
# border trimming rather than a stale one with black edges.
$still = Get-WindowFrame
$still.Save((Join-Path $PSScriptRoot '..\docs\screenshot.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$still.Dispose()

Write-Host "capturing $total frames at ${Fps}fps ($($cw)x$($ch))..."
# Capture raw only. Compositing the shadow during the loop cost ~37ms a frame
# and capped the real rate well below the requested one, so it moves to a
# second pass where it costs nothing but wall clock.
$raws = New-Object 'System.Collections.Generic.List[System.Drawing.Bitmap]'
$clock = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $total; $i++) {
    if ($i -eq $startFrame) {
        [void][Win]::keybd_event(0x75, 0, 0, [IntPtr]::Zero); [void][Win]::keybd_event(0x75, 0, 2, [IntPtr]::Zero)
    }
    if ($i -eq $stopFrame) {
        [void][Win]::keybd_event(0x76, 0, 0, [IntPtr]::Zero); [void][Win]::keybd_event(0x76, 0, 2, [IntPtr]::Zero)
    }
    $raws.Add((Get-WindowFrame))
    Start-Sleep -Milliseconds $delayMs
}
# make sure the demo never leaves it running
[void][Win]::keybd_event(0x76, 0, 0, [IntPtr]::Zero); [void][Win]::keybd_event(0x76, 0, 2, [IntPtr]::Zero)

$clock.Stop()

# Capturing and compositing each frame costs real time on top of the sleep, so
# the true interval is longer than requested. Encoding with the measured value
# keeps the GIF playing at the speed the app actually ran at.
$realDelayMs = [int]([Math]::Round($clock.Elapsed.TotalMilliseconds / $total))
Write-Host "requested ${delayMs}ms/frame, measured ${realDelayMs}ms/frame"

Write-Host "compositing..."
$frames = New-Object 'System.Collections.Generic.List[System.Drawing.Bitmap]'
foreach ($raw in $raws) {
    $frames.Add((New-Composited $raw))
    $raw.Dispose()
}

Write-Host "encoding..."
$res = Write-AnimatedGif -Frames $frames -Path $Out -DelayMs $realDelayMs
foreach ($f in $frames) { $f.Dispose() }

"wrote $Out ({0:N0} KB, {1} stored frames of {2} captured)" -f ($res.Bytes / 1KB), $res.Stored, $res.Total
