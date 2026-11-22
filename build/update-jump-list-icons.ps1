[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../src/FormatFactoryWinUI/Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($outputPath)
$glyphs = [ordered]@{
    all = 0xE8A9; video = 0xE714; audio = 0xE8D6; image = 0xE91B
    document = 0xE8A5; ebook = 0xE736; disc = 0xE958; archive = 0xF012
    automation = 0xE895; history = 0xE81C; settings = 0xE713
}
$family = [Drawing.FontFamily]::new('Segoe Fluent Icons')
$font = [Drawing.Font]::new($family, 42, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#168BCE'))
$format = [Drawing.StringFormat]::new()
$format.Alignment = [Drawing.StringAlignment]::Center
$format.LineAlignment = [Drawing.StringAlignment]::Center
try {
    foreach ($entry in $glyphs.GetEnumerator()) {
        $bitmap = [Drawing.Bitmap]::new(64, 64, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $graphics.DrawString([string][char]$entry.Value, $font, $brush,
                [Drawing.RectangleF]::new(0, 0, 64, 64), $format)
            $bitmap.Save((Join-Path $outputPath "JumpList-$($entry.Key).png"), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $format.Dispose(); $brush.Dispose(); $font.Dispose(); $family.Dispose() }
Write-Output "Updated $($glyphs.Count) taskbar navigation icons."
