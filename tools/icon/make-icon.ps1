# Renders the MagicMouse app icon at all Windows sizes and packs them into a .ico.
# Usage: powershell -File tools\icon\make-icon.ps1
param(
    [string]$OutIco = "$PSScriptRoot\..\..\src\MagicMouse.App\Assets\MagicMouse.ico",
    [string]$PreviewPng = "$PSScriptRoot\preview.png"
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Tile: diagonal blue -> violet gradient
    $tile = New-RoundRect (8*$s) (8*$s) (240*$s) (240*$s) (56*$s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size),
        [System.Drawing.Color]::FromArgb(255, 56, 132, 255), [System.Drawing.Color]::FromArgb(255, 132, 76, 230))
    $g.FillPath($grad, $tile)

    # Mouse body (top view): tall capsule, white with soft shadow
    $mx = 78*$s; $my = 34*$s; $mw = 100*$s; $mh = 188*$s
    $shadow = New-RoundRect ($mx + 3*$s) ($my + 6*$s) $mw $mh ($mw/2)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 20, 10, 60))), $shadow)
    $body = New-RoundRect $mx $my $mw $mh ($mw/2)
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, $my), (New-Object System.Drawing.PointF 0, ($my + $mh)),
        [System.Drawing.Color]::FromArgb(255, 255, 255, 255), [System.Drawing.Color]::FromArgb(255, 226, 230, 242))
    $g.FillPath($bodyBrush, $body)

    # Touch point + ripples near the front of the mouse
    $cx = $mx + $mw/2; $cy = $my + 62*$s
    $accent = [System.Drawing.Color]::FromArgb(255, 88, 104, 245)
    if ($size -ge 32) {
        foreach ($r in @(38, 26)) {
            $alpha = if ($r -eq 38) { 90 } else { 160 }
            $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($alpha, $accent)), ([Math]::Max(1.5, 5*$s))
            $g.DrawEllipse($pen, $cx - $r*$s, $cy - $r*$s, 2*$r*$s, 2*$r*$s)
        }
    }
    $dot = 14*$s
    if ($size -lt 32) { $dot = 22*$s }
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $accent), $cx - $dot, $cy - $dot, 2*$dot, 2*$dot)

    # Swipe hint: short arrow strokes below the touch point (only where there's room)
    if ($size -ge 48) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(130, $accent)), (6*$s)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, $cx, $cy + 44*$s, $cx, $cy + 92*$s)
        $g.DrawLine($pen, $cx - 14*$s, $cy + 78*$s, $cx, $cy + 92*$s)
        $g.DrawLine($pen, $cx + 14*$s, $cy + 78*$s, $cx, $cy + 92*$s)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $b = Render $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $b.Dispose()
}

# ICO container with PNG-compressed entries (supported since Vista)
New-Item -ItemType Directory -Force -Path (Split-Path $OutIco) | Out-Null
$fs = [System.IO.File]::Create($OutIco)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

# Preview sheet: every size on light and dark backgrounds
$pw = 40; foreach ($sz in $sizes) { $pw += $sz + 24 }
$sheet = New-Object System.Drawing.Bitmap $pw, 600
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(245, 245, 247))
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 36))), 0, 300, $pw, 300)
$x = 20
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $img = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream (, $pngs[$i])))
    $g.DrawImage($img, $x, 150 - $sizes[$i] / 2, $sizes[$i], $sizes[$i])
    $g.DrawImage($img, $x, 450 - $sizes[$i] / 2, $sizes[$i], $sizes[$i])
    $x += $sizes[$i] + 24
}
$g.Dispose()
$sheet.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
"Wrote $OutIco ($((Get-Item $OutIco).Length) bytes) and $PreviewPng"
