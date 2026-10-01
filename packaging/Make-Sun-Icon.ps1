$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'brightness-sun.ico'
$bitmap = New-Object Drawing.Bitmap 32, 32, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$pen = New-Object Drawing.Pen ([Drawing.Color]::White), 3
$pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
for ($angle = 0; $angle -lt 360; $angle += 45) {
    $radians = $angle * [Math]::PI / 180
    $x1 = 16 + [Math]::Cos($radians) * 11
    $y1 = 16 + [Math]::Sin($radians) * 11
    $x2 = 16 + [Math]::Cos($radians) * 14
    $y2 = 16 + [Math]::Sin($radians) * 14
    $graphics.DrawLine($pen, [float]$x1, [float]$y1, [float]$x2, [float]$y2)
}
$brush = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
$graphics.FillEllipse($brush, 9, 9, 14, 14)
$iconHandle = $bitmap.GetHicon()
try {
    $icon = [Drawing.Icon]::FromHandle($iconHandle)
    $stream = [IO.File]::Create($output)
    try { $icon.Save($stream) } finally { $stream.Dispose() }
} finally {
    $brush.Dispose()
    $pen.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
