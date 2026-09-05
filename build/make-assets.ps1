<#
.SYNOPSIS
    Regenerates every icon asset from the master AB SwitchBack logo.

.DESCRIPTION
    Produces the PNG sizes the Revit ribbon needs (WPF decodes PNG natively) and the
    .ico files Navisworks and the installer need. ICOs are written as 32-bit BGRA DIBs
    rather than PNG-compressed entries, because Navisworks' icon loader and the older
    WPF IconBitmapDecoder do not reliably read PNG-in-ICO.

    Run this only when the master logo changes; the generated files are committed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\make-assets.ps1 -Source "path\to\logo.png"
#>
[CmdletBinding()]
param(
    [string]$Source
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot  = Split-Path $PSScriptRoot -Parent
$assetsDir = Join-Path $repoRoot 'assets'

if (-not $Source) { $Source = Join-Path $assetsDir 'logo_master.png' }
if (-not (Test-Path $Source)) { throw "Master logo not found: $Source" }

New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null

function Get-Resized {
    param([System.Drawing.Image]$Image, [int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($Image, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
    }
    finally { $graphics.Dispose() }
    return $bitmap
}

# One ICO image entry: BITMAPINFOHEADER + bottom-up BGRA pixels + an empty AND mask.
function ConvertTo-IcoImage {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([UInt32]40)          # biSize
    $writer.Write([Int32]$w)           # biWidth
    $writer.Write([Int32]($h * 2))     # biHeight: XOR image plus AND mask
    $writer.Write([UInt16]1)           # biPlanes
    $writer.Write([UInt16]32)          # biBitCount
    $writer.Write([UInt32]0)           # biCompression = BI_RGB
    $writer.Write([UInt32]($w * $h * 4))
    $writer.Write([Int32]0); $writer.Write([Int32]0)
    $writer.Write([UInt32]0); $writer.Write([UInt32]0)

    # Lock the bits once: GetPixel per pixel is far too slow at 256x256.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $buffer = New-Object byte[] ($data.Stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)

        # DIBs are stored bottom-up.
        for ($y = $h - 1; $y -ge 0; $y--) {
            $writer.Write($buffer, $y * $data.Stride, $w * 4)
        }
    }
    finally { $Bitmap.UnlockBits($data) }

    # AND mask: 1 bit per pixel, rows padded to 4 bytes. Alpha already carries
    # transparency, so the mask stays zero.
    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $blank = New-Object byte[] $maskRow
    for ($y = 0; $y -lt $h; $y++) { $writer.Write($blank) }

    $writer.Flush()

    # The leading comma stops PowerShell unrolling the byte[] into the pipeline,
    # which would otherwise reach the caller as loose objects rather than an array.
    return , $stream.ToArray()
}

function Write-Ico {
    param([System.Drawing.Bitmap[]]$Bitmaps, [string]$Path)

    $images = New-Object System.Collections.Generic.List[byte[]]
    foreach ($bitmap in $Bitmaps) {
        [byte[]]$blob = ConvertTo-IcoImage -Bitmap $bitmap
        $images.Add($blob)
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([UInt16]0)                 # reserved
    $writer.Write([UInt16]1)                 # type: icon
    $writer.Write([UInt16]$Bitmaps.Count)

    $offset = 6 + (16 * $Bitmaps.Count)
    for ($i = 0; $i -lt $Bitmaps.Count; $i++) {
        $size = $Bitmaps[$i].Width
        # 256 is encoded as 0 in the directory entry.
        $encoded = if ($size -ge 256) { 0 } else { $size }

        $writer.Write([byte]$encoded)         # width
        $writer.Write([byte]$encoded)         # height
        $writer.Write([byte]0)                # palette colours
        $writer.Write([byte]0)                # reserved
        $writer.Write([UInt16]1)              # planes
        $writer.Write([UInt16]32)             # bits per pixel
        $writer.Write([UInt32]$images[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }

    foreach ($image in $images) { $writer.Write($image, 0, $image.Length) }
    $writer.Flush()

    [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
}

# Rounded dark tile in the logo's colours, carrying a simple white glyph. Drawn rather
# than hand-authored so the ribbon reads as one set with the logo at every size.
#   status   - three rule lines, reading as a log
#   settings - three sliders with knobs, legible even at 16 px where a gear turns to mush
function New-Glyph {
    param(
        [int]$Size,
        [ValidateSet('status', 'settings')]
        [string]$Kind
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

        $tile = [System.Drawing.Color]::FromArgb(255, 12, 18, 28)      # logo tile
        $accent = [System.Drawing.Color]::FromArgb(255, 41, 155, 232)  # logo blue

        $radius = [Math]::Max(2, [int]($Size * 0.22))
        $rect = New-Object System.Drawing.Rectangle(0, 0, ($Size - 1), ($Size - 1))

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
        $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
        $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
        $path.CloseFigure()

        $fill = New-Object System.Drawing.SolidBrush($tile)
        $g.FillPath($fill, $path)
        $fill.Dispose()

        $border = New-Object System.Drawing.Pen($accent, [Math]::Max(1.0, $Size * 0.06))
        $g.DrawPath($border, $path)
        $border.Dispose()
        $path.Dispose()

        $lineBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $h = [Math]::Max(1.0, $Size * 0.09)
        $left = $Size * 0.26
        $width = $Size * 0.48

        if ($Kind -eq 'status') {
            # Three rule lines, the last one short, reading as a log.
            for ($i = 0; $i -lt 3; $i++) {
                $y = $Size * (0.30 + $i * 0.18)
                $w = if ($i -eq 2) { $width * 0.6 } else { $width }
                $g.FillRectangle($lineBrush, [single]$left, [single]$y, [single]$w, [single]$h)
            }
        }
        else {
            # Three sliders, each with a knob at a different position.
            $knobR = [Math]::Max(1.2, $Size * 0.10)
            $knobAt = @(0.66, 0.34, 0.54)
            for ($i = 0; $i -lt 3; $i++) {
                $y = $Size * (0.30 + $i * 0.18)
                $g.FillRectangle($lineBrush, [single]$left, [single]$y, [single]$width, [single]$h)

                $cx = $left + ($width * $knobAt[$i])
                $cy = $y + ($h / 2)
                $g.FillEllipse($lineBrush, [single]($cx - $knobR), [single]($cy - $knobR),
                               [single]($knobR * 2), [single]($knobR * 2))
            }
        }

        $lineBrush.Dispose()
    }
    finally { $g.Dispose() }

    return $bitmap
}

Write-Host "Master logo: $Source"
$master = [System.Drawing.Image]::FromFile($Source)

try {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $bitmaps = @{}
    foreach ($size in $sizes) { $bitmaps[$size] = Get-Resized -Image $master -Size $size }

    # PNGs for the Revit ribbon and the installer window.
    foreach ($size in @(16, 32, 128, 256)) {
        $path = Join-Path $assetsDir ("logo_$size.png")
        $bitmaps[$size].Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("  {0}" -f (Split-Path $path -Leaf))
    }

    # Single-size ICOs for the Navisworks ribbon buttons.
    Write-Ico -Bitmaps @($bitmaps[16]) -Path (Join-Path $assetsDir 'logo_16.ico')
    Write-Ico -Bitmaps @($bitmaps[32]) -Path (Join-Path $assetsDir 'logo_32.ico')
    Write-Host '  logo_16.ico'
    Write-Host '  logo_32.ico'

    # Multi-resolution ICO for the installer and Add/Remove Programs.
    Write-Ico -Bitmaps @($bitmaps[16], $bitmaps[24], $bitmaps[32], $bitmaps[48], $bitmaps[64], $bitmaps[128], $bitmaps[256]) `
              -Path (Join-Path $assetsDir 'logo.ico')
    Write-Host '  logo.ico (16-256)'

    # Companion glyphs, drawn to match the logo's dark tile and brand blue so the ribbon
    # reads as one set rather than a logo next to clip art.
    $glyphSizes = @(16, 32)
    foreach ($kind in @('status', 'settings')) {
        foreach ($size in $glyphSizes) {
            $glyph = New-Glyph -Size $size -Kind $kind
            try {
                $glyph.Save((Join-Path $assetsDir ("{0}_{1}.png" -f $kind, $size)),
                            [System.Drawing.Imaging.ImageFormat]::Png)
                Write-Ico -Bitmaps @($glyph) -Path (Join-Path $assetsDir ("{0}_{1}.ico" -f $kind, $size))
                Write-Host ("  {0}_{1}.png / .ico" -f $kind, $size)
            }
            finally { $glyph.Dispose() }
        }
    }

    foreach ($size in $sizes) { $bitmaps[$size].Dispose() }
}
finally { $master.Dispose() }

Write-Host ''
Write-Host "Assets written to $assetsDir" -ForegroundColor Green
