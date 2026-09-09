<#
    Builds dist/BeastClicker.msix.

    IMPORTANT, and stated up front because it changes how useful this package is:
    an MSIX must be signed by a certificate the target machine already trusts.
    This script signs with a SELF-SIGNED certificate, which means a user cannot
    simply double-click the .msix — they must first install the accompanying
    .cer into Local Machine > Trusted People. Asking strangers to trust a
    certificate is a real security ask, so the portable .exe stays the
    recommended download. Shipping an MSIX that installs cleanly for everyone
    requires a certificate from a trusted CA (or Microsoft Store signing).

    The certificate is created in the CURRENT USER's personal store only. Nothing
    is added to any machine-wide trust store.
#>
[CmdletBinding()]
param(
    [string]$Publisher = 'CN=beastops',
    [string]$PublisherDisplay = 'beastops',
    [string]$Version,
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..'))
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

# The package version follows the csproj rather than being repeated here. A
# second copy of the number is how a release ends up shipping an MSIX still
# labelled with the previous version: the bump gets made in one place and missed
# in the other. MSIX wants four parts and the project gives three, so the
# revision is pinned at 0. Pass -Version to override.
if (-not $Version) {
    $csproj = Join-Path $Root 'src\BeastClicker\BeastClicker.csproj'
    $m = [regex]::Match((Get-Content $csproj -Raw),
                        '<Version>\s*([0-9]+(?:\.[0-9]+){1,2})\s*</Version>')
    if (-not $m.Success) { throw "could not read <Version> from $csproj" }
    $parts = [System.Collections.ArrayList]@($m.Groups[1].Value.Split('.'))
    while ($parts.Count -lt 4) { [void]$parts.Add('0') }
    $Version = $parts -join '.'
    Write-Host "version $Version (read from BeastClicker.csproj)"
}

$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $sdk) {
    $sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
           Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $sdk) { throw 'makeappx.exe not found — install the Windows SDK.' }
$makeappx = $sdk.FullName
$signtool = Join-Path (Split-Path $makeappx) 'signtool.exe'
Write-Host "SDK: $(Split-Path $makeappx)"

$stage = Join-Path $Root 'build\msix'
$assets = Join-Path $stage 'Assets'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# ---- app payload -----------------------------------------------------------
Write-Host 'publishing app into the package layout...'
& dotnet publish (Join-Path $Root 'src\BeastClicker') -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -o $stage 2>&1 |
    Select-String -Pattern 'error' | ForEach-Object { Write-Warning $_ }
Remove-Item (Join-Path $stage 'BeastClicker.pdb') -Force -ErrorAction SilentlyContinue

# ---- tile assets, scaled from the master icon ------------------------------
$src = [System.Drawing.Image]::FromFile((Join-Path $Root 'docs\icon.png'))
function Save-Tile([int]$w, [int]$h, [string]$name, [double]$fill = 0.72) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [int]([Math]::Min($w, $h) * $fill)
    $g.DrawImage($src, [int](($w - $side) / 2), [int](($h - $side) / 2), $side, $side)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Save-Tile 44 44 'Square44x44Logo.png' 0.86
Save-Tile 150 150 'Square150x150Logo.png' 0.62
Save-Tile 310 150 'Wide310x150Logo.png' 0.62
Save-Tile 50 50 'StoreLogo.png' 0.86
# Windows also looks for the targetsize variant of the small tile
Save-Tile 44 44 'Square44x44Logo.targetsize-44_altform-unplated.png' 0.86
$src.Dispose()

# ---- manifest --------------------------------------------------------------
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">

  <Identity Name="BeastOps.BeastClicker"
            Publisher="$Publisher"
            Version="$Version"
            ProcessorArchitecture="x64" />

  <Properties>
    <DisplayName>Beast Clicker</DisplayName>
    <PublisherDisplayName>$PublisherDisplay</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    <Description>A precise, low-overhead auto clicker for Windows.</Description>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Resources>
    <Resource Language="en-us" />
  </Resources>

  <Applications>
    <Application Id="BeastClicker" Executable="BeastClicker.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="Beast Clicker"
        Description="A precise, low-overhead auto clicker for Windows."
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>

  <Capabilities>
    <!-- Desktop app packaged as MSIX: needs full trust to synthesise input. -->
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding UTF8

# ---- pack ------------------------------------------------------------------
$distDir = Join-Path $Root 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$msix = Join-Path $distDir 'BeastClicker.msix'
Remove-Item $msix -Force -ErrorAction SilentlyContinue

Write-Host 'packing...'
& $makeappx pack /d $stage /p $msix /o | Select-Object -Last 3
if (-not (Test-Path $msix)) { throw 'makeappx failed' }

# ---- sign ------------------------------------------------------------------
$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host "creating a self-signed code-signing certificate for $Publisher (current user store only)..."
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -FriendlyName 'Beast Clicker (self-signed)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

Write-Host "signing with $($cert.Thumbprint)..."
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /t http://timestamp.digicert.com $msix 2>&1 |
    Select-Object -Last 3

# Export the public certificate so a user can choose to trust it.
$cerPath = Join-Path $distDir 'BeastClicker.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

"`nwrote $msix ({0:N0} KB)" -f ((Get-Item $msix).Length / 1KB)
"wrote $cerPath  (must be installed to Trusted People before the MSIX will install)"
