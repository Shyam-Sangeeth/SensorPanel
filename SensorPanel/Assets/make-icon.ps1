# Draws the SensorPanel icon (a small portrait display with a gauge and bars) and writes SensorPanel.ico
# with PNG images at 16-256 px.
Add-Type -AssemblyName System.Drawing

function New-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0

    # Display body.
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 8 * $s; $x = 12 * $s; $y = 3 * $s; $w = 40 * $s; $h = 58 * $s
    $body.AddArc($x, $y, $r * 2, $r * 2, 180, 90); $body.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
    $body.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90); $body.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
    $body.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 28, 30, 38))), $body)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 110, 120, 150)), ([Math]::Max(1, 2.5 * $s))), $body)

    # Gauge arc with needle.
    $cx = 32 * $s; $cy = 24 * $s; $gr = 12 * $s
    $arc = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 60, 200, 120)), ([Math]::Max(1.5, 4 * $s))
    $g.DrawArc($arc, $cx - $gr, $cy - $gr, $gr * 2, $gr * 2, 135, 150)
    $arc.Color = [System.Drawing.Color]::FromArgb(255, 255, 190, 40); $g.DrawArc($arc, $cx - $gr, $cy - $gr, $gr * 2, $gr * 2, 285, 60)
    $arc.Color = [System.Drawing.Color]::FromArgb(255, 240, 70, 60); $g.DrawArc($arc, $cx - $gr, $cy - $gr, $gr * 2, $gr * 2, 345, 60)
    $needle = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.2, 2.5 * $s))
    $needle.StartCap = 'Round'; $needle.EndCap = 'Round'
    $a = 300 * [Math]::PI / 180
    $g.DrawLine($needle, $cx, $cy, $cx + [Math]::Cos($a) * $gr * 0.8, $cy + [Math]::Sin($a) * $gr * 0.8)

    # Bars.
    if ($size -ge 24) {
        $colors = @([System.Drawing.Color]::FromArgb(255, 70, 160, 255), [System.Drawing.Color]::FromArgb(255, 60, 200, 120))
        $lengths = @(26, 18)
        for ($i = 0; $i -lt 2; $i++) {
            $by = (42 + $i * 8) * $s
            $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 64, 76))), 19 * $s, $by, 26 * $s, 4 * $s)
            $g.FillRectangle((New-Object System.Drawing.SolidBrush $colors[$i]), 19 * $s, $by, $lengths[$i] * $s, 4 * $s)
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) { $images[$s] = New-Icon $s }

$path = Join-Path $PSScriptRoot 'SensorPanel.ico'
$ico = [System.IO.File]::Create($path)
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$s].Length); $w.Write([uint32]$offset)
    $offset += $images[$s].Length
}
foreach ($s in $sizes) { $w.Write($images[$s]) }
$w.Close()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon-256.png'), $images[256])
"Wrote $path"
