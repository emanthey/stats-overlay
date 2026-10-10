# Generates StatsOverlay.ico: a dark rounded tile with three usage bars (green/yellow/red),
# matching the overlay's colors. Run: powershell -ExecutionPolicy Bypass -File make_icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded background tile
    $r = [Math]::Max(2.0, $s * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 20, 20, 24))
    $g.FillPath($bg, $path)
    if ($s -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 60, 60, 70)), ([Math]::Max(1.0, $s / 48.0))
        $g.DrawPath($pen, $path)
    }

    # Three bars of increasing height
    $colors = @(
        [System.Drawing.Color]::FromArgb(255, 90, 200, 120),
        [System.Drawing.Color]::FromArgb(255, 240, 190, 60),
        [System.Drawing.Color]::FromArgb(255, 240, 80, 80)
    )
    $heights = 0.35, 0.55, 0.75
    $pad = $s * 0.18
    $gap = $s * 0.08
    $barW = ($s - 2 * $pad - 2 * $gap) / 3
    $bottom = $s - $pad
    for ($i = 0; $i -lt 3; $i++) {
        $h = ($s - 2 * $pad) * $heights[$i]
        $x = $pad + $i * ($barW + $gap)
        $brush = New-Object System.Drawing.SolidBrush $colors[$i]
        $g.FillRectangle($brush, [float]$x, [float]($bottom - $h), [float]$barW, [float]$h)
    }
    $g.Dispose()

    # Encode as 32bpp DIB (BITMAPINFOHEADER + bottom-up BGRA + AND mask)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $s)))
    $bw.Flush()
    $frames += , @{ Size = $s; Data = $ms.ToArray() }
    $bmp.Dispose()
}

# Write ICO container
$out = Join-Path $PSScriptRoot "StatsOverlay.ico"
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $d = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$f.Data.Length); $w.Write([int]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Close()
Write-Output "Wrote $out"
