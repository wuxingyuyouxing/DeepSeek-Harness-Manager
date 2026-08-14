# build-icon.ps1 — builds DeepSeek-Harness.ico (multi-size, PNG-compressed frames)
# from the official DeepSeek whale mark extracted from deepseek.com's favicon.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$Src    = Join-Path $Root 'assets\whale-frame.png'   # 225x225 official mark
$OutIco = Join-Path $Root 'DeepSeek-Harness.ico'
$Sizes  = @(16, 24, 32, 48, 64, 128, 256)

if (-not (Test-Path $Src)) { throw "Missing source image: $Src" }

$src = [System.Drawing.Image]::FromFile($Src)
$pngs = @{}

foreach ($s in $Sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $bmp.Dispose()
    $ms.Dispose()
}
$src.Dispose()

# Assemble ICO: ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes each) + PNG blobs.
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)                    # reserved
$bw.Write([uint16]1)                    # type: icon
$bw.Write([uint16]$Sizes.Count)         # frame count
$offset = 6 + 16 * $Sizes.Count
foreach ($s in $Sizes) {
    $data = $pngs[$s]
    $dim  = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)               # width  (0 = 256)
    $bw.Write([byte]$dim)               # height (0 = 256)
    $bw.Write([byte]0)                  # color count
    $bw.Write([byte]0)                  # reserved
    $bw.Write([uint16]1)                # planes
    $bw.Write([uint16]32)               # bits per pixel
    $bw.Write([uint32]$data.Length)     # bytes in resource
    $bw.Write([uint32]$offset)          # image offset
    $offset += $data.Length
}
foreach ($s in $Sizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutIco, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

"Wrote $OutIco ($((Get-Item $OutIco).Length) bytes, $($Sizes.Count) frames: $($Sizes -join ', '))"
