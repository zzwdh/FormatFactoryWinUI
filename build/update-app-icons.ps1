[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '../src/FormatFactoryWinUI/Assets/AppIconSource.png'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../src/FormatFactoryWinUI/Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourceFile = (Resolve-Path -LiteralPath $SourcePath).Path
$assetDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$sourceImage = [System.Drawing.Bitmap]::new($sourceFile)

function New-IconBitmap([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

        # Fit the complete approved image without stretching or cropping it.
        $scale = [Math]::Min($Size / [double]$sourceImage.Width, $Size / [double]$sourceImage.Height)
        $width = [Math]::Max(1, [int][Math]::Round($sourceImage.Width * $scale))
        $height = [Math]::Max(1, [int][Math]::Round($sourceImage.Height * $scale))
        $destination = [System.Drawing.Rectangle]::new(
            [int][Math]::Floor(($Size - $width) / 2.0),
            [int][Math]::Floor(($Size - $height) / 2.0), $width, $height)
        $graphics.DrawImage($sourceImage, $destination, 0, 0, $sourceImage.Width, $sourceImage.Height,
            [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    }
    catch {
        $bitmap.Dispose()
        throw
    }
    finally {
        $attributes.Dispose()
        $graphics.Dispose()
    }
    return $bitmap
}

try {
    foreach ($size in @(44, 150)) {
        $bitmap = New-IconBitmap $size
        try {
            $bitmap.Save((Join-Path $assetDirectory "AppIcon$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }

    # Windows supports PNG-compressed 32-bit frames within an ICO container.
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = New-IconBitmap $size
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
            $bitmap.Dispose()
        }
    }

    $iconStream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($iconStream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame)
        }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'AppIcon.ico'), $iconStream.ToArray())
    }
    finally {
        $writer.Dispose()
        $iconStream.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
}

Write-Output "Updated AppIcon44.png, AppIcon150.png and AppIcon.ico from $sourceFile"
