# Generates assets\menuprio.png (256 px) and assets\menuprio.ico (16..256 px, PNG entries).
# Reproducible: run  powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Draw-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = [float]$size / 256.0
    $g.ScaleTransform($scale, $scale)

    # rounded square, indigo -> violet gradient
    $shape = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shape.AddArc(10, 10, 104, 104, 180, 90)
    $shape.AddArc(142, 10, 104, 104, 270, 90)
    $shape.AddArc(142, 142, 104, 104, 0, 90)
    $shape.AddArc(10, 142, 104, 104, 90, 90)
    $shape.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 79, 70, 229)
    $c2 = [System.Drawing.Color]::FromArgb(255, 124, 58, 237)
    $p1 = New-Object System.Drawing.PointF(0, 0)
    $p2 = New-Object System.Drawing.PointF(256, 256)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($p1, $p2, $c1, $c2)
    $g.FillPath($bg, $shape)

    # return arrow (Enter)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float]28)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $arrow.AddLine(126, 140, 164, 140)
    $arrow.AddArc(136, 84, 56, 56, 90, -90)
    $arrow.AddLine(192, 112, 192, 82)
    $g.DrawPath($pen, $arrow)

    $pts = @(
        (New-Object System.Drawing.PointF(70, 140)),
        (New-Object System.Drawing.PointF(118, 108)),
        (New-Object System.Drawing.PointF(118, 172))
    )
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon([System.Drawing.PointF[]]$pts)
    $g.FillPath($white, $head)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = New-Object System.Collections.Generic.List[byte[]]
$pngPath = Join-Path $outDir "menuprio.png"

foreach ($s in $sizes) {
    $bmp = Draw-IconBitmap $s
    if ($s -eq 256) {
        $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

$icoPath = Join-Path $outDir "menuprio.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

# ICONDIRENTRY for every PNG
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = $s
    if ($s -ge 256) { $dim = 0 }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}

for ($i = 0; $i -lt $pngs.Count; $i++) {
    $bw.Write($pngs[$i])
}

$bw.Dispose()
$fs.Dispose()

Write-Host ("wrote {0}" -f $pngPath)
Write-Host ("wrote {0}" -f $icoPath)
