<#
.SYNOPSIS
  Converts a PNG (any aspect ratio) into a multi-size .ico (16-256 px, PNG-compressed).

.DESCRIPTION
  Non-square images are centered on a square canvas filled with the image's own top-left pixel colour,
  so they are not stretched. Dot-source this file to get the New-IcoFromPng function, or run it directly:
    .\ConvertTo-Ico.ps1 -Png logo.png -Ico logo.ico
#>
[CmdletBinding()]
param([string]$Png, [string]$Ico)

function New-IcoFromPng {
    param(
        [Parameter(Mandatory)][string]$PngPath,
        [Parameter(Mandatory)][string]$IcoPath,
        [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
    )
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $PngPath).Path)
    try {
        $bg = ([System.Drawing.Bitmap]$src).GetPixel(0, 0)
        $side = [Math]::Max($src.Width, $src.Height)
        $images = @()
        foreach ($s in $Sizes) {
            $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.SmoothingMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
            $g.Clear($bg)
            $scale = $s / $side
            $w = [int][Math]::Round($src.Width * $scale); $h = [int][Math]::Round($src.Height * $scale)
            $g.DrawImage($src, [int](($s - $w) / 2), [int](($s - $h) / 2), $w, $h)
            $g.Dispose()
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            $images += , @{ Size = $s; Data = $ms.ToArray() }
        }
    } finally { $src.Dispose() }

    $fs = [System.IO.File]::Create($IcoPath)
    $bw = New-Object System.IO.BinaryWriter $fs
    try {
        $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)   # ICONDIR
        $offset = 6 + 16 * $images.Count
        foreach ($i in $images) {                                                         # ICONDIRENTRYs
            $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
            $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
            $bw.Write([uint16]1); $bw.Write([uint16]32)
            $bw.Write([uint32]$i.Data.Length); $bw.Write([uint32]$offset)
            $offset += $i.Data.Length
        }
        foreach ($i in $images) { $bw.Write($i.Data) }
    } finally { $bw.Close(); $fs.Close() }
}

if ($Png -and $Ico) { New-IcoFromPng -PngPath $Png -IcoPath $Ico; Write-Host "Wrote $Ico" }
