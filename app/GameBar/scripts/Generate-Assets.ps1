param(
    # Defaults to the repository root, i.e. the parent of app/GameBar/scripts.
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$ico = Join-Path $RepoRoot "app\favicon.ico"
$assets = Join-Path $RepoRoot "app\GameBar\Assets"
$icons = Join-Path $RepoRoot "app\GameBar\GameBar\Icons"

New-Item -ItemType Directory -Force -Path $assets | Out-Null
New-Item -ItemType Directory -Force -Path $icons | Out-Null

# GDI+ cannot decode this .ico (Icon.ToBitmap throws), so pull the PNG-compressed frame
# out of the container by hand and decode that instead. It is the 256x256 entry.
$bytes = [IO.File]::ReadAllBytes($ico)
$count = [BitConverter]::ToUInt16($bytes, 4)
$source = $null

for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + $i * 16
    $size = [BitConverter]::ToUInt32($bytes, $o + 8)
    $off = [BitConverter]::ToUInt32($bytes, $o + 12)
    $sig = [BitConverter]::ToString($bytes[$off..($off + 7)])
    if ($sig.StartsWith("89-50-4E-47")) {
        $chunk = New-Object byte[] $size
        [Array]::Copy($bytes, $off, $chunk, 0, $size)
        $ms = New-Object System.IO.MemoryStream(, $chunk)
        $source = [System.Drawing.Image]::FromStream($ms)
        Write-Output "source frame: $($source.Width)x$($source.Height)"
        break
    }
}

if ($null -eq $source) { throw "No PNG frame found in $ico" }

function Save-Icon([string]$path, [int]$size, [string]$backdrop, [int]$height = 0) {
    if ($height -le 0) { $height = $size }
    $bmp = New-Object System.Drawing.Bitmap($size, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $size, $height)))
    $g.Dispose()

    if ($backdrop) {
        $under = New-Object System.Drawing.Bitmap($size, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g2 = [System.Drawing.Graphics]::FromImage($under)
        $g2.Clear([System.Drawing.ColorTranslator]::FromHtml($backdrop))
        $g2.DrawImage($bmp, 0, 0, $size, $height)
        $g2.Dispose()
        $bmp.Dispose()
        $bmp = $under
    }

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "wrote $path ($size x $height)"
}

# Widget icons - transparent background, per the Game Bar icon guidance.
foreach ($s in 16, 20, 24, 32, 44, 256) {
    Save-Icon (Join-Path $icons "icon.targetsize-$s.png") $s $null
}

# Package assets. StoreLogo and the wide tile must be opaque, so they get a dark backdrop.
Save-Icon (Join-Path $assets "StoreLogo.scale-100.png") 50 "#1C1C1C"
Save-Icon (Join-Path $assets "Square44x44Logo.scale-100.png") 44 "#1C1C1C"
Save-Icon (Join-Path $assets "Square44x44Logo.targetsize-24_altform-unplated.png") 24 $null
Save-Icon (Join-Path $assets "Square150x150Logo.scale-100.png") 150 "#1C1C1C"
Save-Icon (Join-Path $assets "Wide310x150Logo.scale-100.png") 310 "#1C1C1C" 150

$source.Dispose()