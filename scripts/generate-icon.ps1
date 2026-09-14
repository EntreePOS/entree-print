param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$frames = @()
foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 32.0, $size / 32.0)
    $ink = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#123344'))
    $edge = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#E9FAFF'), 1)
    $paper = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#FFFFFF'))
    $accent = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#18C8C0'))
    $line = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#123344'), 1.5)
    try {
        $graphics.FillRectangle($ink, 8, 2, 16, 12)
        $graphics.FillRectangle($paper, 10, 3, 12, 8)
        $graphics.FillRectangle($ink, 3, 10, 26, 15)
        $graphics.DrawRectangle($edge, 3, 10, 26, 15)
        $graphics.FillRectangle($accent, 23, 13, 3, 3)
        $graphics.FillRectangle($ink, 8, 19, 16, 12)
        $graphics.FillRectangle($paper, 10, 19, 12, 10)
        $graphics.DrawLine($line, 12, 22, 20, 22)
        $graphics.DrawLine($line, 12, 25, 18, 25)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
            if ($size -eq 256) { $bitmap.Save((Join-Path $OutputDirectory 'entree-print.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        } finally { $stream.Dispose() }
    } finally {
        $ink.Dispose(); $edge.Dispose(); $paper.Dispose(); $accent.Dispose(); $line.Dispose()
        $graphics.Dispose(); $bitmap.Dispose()
    }
}
$iconPath = Join-Path $OutputDirectory 'entree-print.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Generated $iconPath (8 resolutions)"
