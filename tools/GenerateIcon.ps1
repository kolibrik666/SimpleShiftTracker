$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconDirectory = Join-Path $PSScriptRoot '../src/Tracker.App/Assets'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
$images = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#176E5D'))
    $outline = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $outline.AddArc(8, 8, 80, 80, 180, 90)
    $outline.AddArc(168, 8, 80, 80, 270, 90)
    $outline.AddArc(168, 168, 80, 80, 0, 90)
    $outline.AddArc(8, 168, 80, 80, 90, 90)
    $outline.CloseFigure()
    $graphics.FillPath($background, $outline)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 11)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawEllipse($pen, 62, 62, 132, 132)
    $graphics.DrawLine($pen, 128, 86, 128, 128)
    $graphics.DrawLine($pen, 128, 128, 160, 148)
    $buffer = [System.IO.MemoryStream]::new()
    $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $images.Add($buffer.ToArray())
    $buffer.Dispose(); $pen.Dispose(); $outline.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $iconDirectory 'Tracker.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
}
finally { $writer.Dispose(); $file.Dispose() }
