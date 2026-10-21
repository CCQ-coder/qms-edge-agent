param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'lyr-mes.ico')
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeIcon {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@

$bitmap = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$background = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
    (New-Object System.Drawing.Rectangle 8, 8, 240, 240),
    ([System.Drawing.Color]::FromArgb(255, 20, 82, 156)),
    ([System.Drawing.Color]::FromArgb(255, 0, 38, 86)),
    ([System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
)
$graphics.FillEllipse($background, 8, 8, 240, 240)

$ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(210, 219, 239, 255)), 5
$graphics.DrawEllipse($ring, 18, 18, 220, 220)

$font = New-Object System.Drawing.Font 'Segoe UI', 77, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$graphics.DrawString('LYR', $font, $textBrush, (New-Object System.Drawing.RectangleF 13, 19, 230, 178), $format)

$accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 85, 205, 250))
$graphics.FillEllipse($accent, 108, 197, 40, 40)

$handle = $bitmap.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    try {
        $stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
        try { $icon.Save($stream) }
        finally { $stream.Dispose() }
    }
    finally { $icon.Dispose() }
}
finally {
    [NativeIcon]::DestroyIcon($handle) | Out-Null
    $accent.Dispose()
    $textBrush.Dispose()
    $format.Dispose()
    $font.Dispose()
    $ring.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
