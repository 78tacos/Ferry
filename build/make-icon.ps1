# Draws Ferry's icon (a white ferry on a blue tile) at the usual sizes and packs them into an .ico.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Ferry.App\Assets\ferry.ico'))

Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Tile: rounded square with a sea-blue gradient.
    $r = 56 * $s
    $rect = New-Object System.Drawing.RectangleF (8 * $s), (8 * $s), (240 * $s), (240 * $s)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
    $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
    $path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 11, 92, 196)), ([System.Drawing.Color]::FromArgb(255, 25, 167, 206)), 60
    $g.FillPath($brush, $path)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    # Cabin and funnel.
    $g.FillRectangle($white, (96 * $s), (78 * $s), (64 * $s), (34 * $s))
    $g.FillRectangle($white, (136 * $s), (56 * $s), (16 * $s), (26 * $s))
    # Hull: a wide trapezoid.
    $hull = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (52 * $s), (120 * $s)),
        (New-Object System.Drawing.PointF (204 * $s), (120 * $s)),
        (New-Object System.Drawing.PointF (180 * $s), (160 * $s)),
        (New-Object System.Drawing.PointF (76 * $s), (160 * $s)))
    $g.FillPolygon($white, $hull)
    # Wave.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 255, 255, 255)), ([Math]::Max(1.5, 14 * $s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawBezier($pen, (48 * $s), (192 * $s), (88 * $s), (172 * $s), (120 * $s), (212 * $s), (160 * $s), (192 * $s))
    $g.DrawBezier($pen, (160 * $s), (192 * $s), (180 * $s), (182 * $s), (196 * $s), (196 * $s), (208 * $s), (190 * $s))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) { , (New-Frame $size) }

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]; $data = $frames[$i]
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $w.Write($data) }
$w.Dispose()
Write-Host "Wrote $Out"
