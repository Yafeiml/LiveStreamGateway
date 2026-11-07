param(
    [string] $Source = (Join-Path $PSScriptRoot '..\assets\LiveStreamGateway.png'),
    [string] $Destination = (Join-Path $PSScriptRoot '..\assets\LiveStreamGateway.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if ($PSVersionTable.PSEdition -eq 'Core') {
    Add-Type -AssemblyName System.Drawing.Common
}

# Only package/resample the approved artwork; do not redraw or alter its design.
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
$image = [System.Drawing.Image]::FromFile($sourcePath)
try {
    if ($image.Width -ne $image.Height) { throw '图标源图必须为正方形。' }
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
        $buffer = [System.IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.DrawImage($image, [System.Drawing.Rectangle]::new(0, 0, $size, $size),
                0, 0, $image.Width, $image.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($buffer.ToArray())
        }
        finally {
            $buffer.Dispose()
            $attributes.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally { $image.Dispose() }

# ICONDIR + ICONDIRENTRY records followed by the alpha-preserving PNG frames.
$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = [uint32](6 + 16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write($offset)
        $offset += [uint32]$frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($destinationPath, $output.ToArray())
}
finally {
    $writer.Dispose()
    $output.Dispose()
}
Write-Output "已生成 $destinationPath（$($sizes -join ', ') px）。"
