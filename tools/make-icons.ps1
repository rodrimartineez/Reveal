# Generates Reveal's application icon (multi-size .ico) and the tray icons (active / paused).
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\Reveal.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-RevealBitmap([int]$size, [bool]$active) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [double]$size

    # rounded tile
    $r = 0.24 * $s
    $pad = 0.02 * $s
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 38, 41, 52)), ([System.Drawing.Color]::FromArgb(255, 14, 15, 19)), 55
    $g.FillPath($bg, $path)
    $borderPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 255, 255, 255)), ([Math]::Max(1.0, $s / 128))
    $g.DrawPath($borderPen, $path)

    # prism V: two faces of light meeting at the bottom point (coordinates on a 100-unit grid)
    $k = $s / 100.0
    function Face([double[]]$xy) {
        $pts = for ($i = 0; $i -lt $xy.Length; $i += 2) { New-Object System.Drawing.PointF ($xy[$i] * $k), ($xy[$i + 1] * $k) }
        $fp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $fp.AddPolygon([System.Drawing.PointF[]]$pts)
        return $fp
    }
    if ($active) {
        $lightTop = [System.Drawing.Color]::FromArgb(255, 196, 212, 255); $lightBottom = [System.Drawing.Color]::FromArgb(255, 150, 176, 255)
        $darkTop = [System.Drawing.Color]::FromArgb(255, 110, 140, 255); $darkBottom = [System.Drawing.Color]::FromArgb(255, 70, 100, 230)
    } else {
        $lightTop = [System.Drawing.Color]::FromArgb(255, 176, 180, 190); $lightBottom = [System.Drawing.Color]::FromArgb(255, 150, 154, 164)
        $darkTop = [System.Drawing.Color]::FromArgb(255, 112, 118, 130); $darkBottom = [System.Drawing.Color]::FromArgb(255, 92, 97, 108)
    }
    $round = [Math]::Max(1.0, 0.045 * $s)   # stroke in the face colour rounds every corner
    $faces = @(
        @{ Path = (Face @(64, 25, 78, 25, 52, 76, 45, 62)); Top = $darkTop; Bottom = $darkBottom },
        @{ Path = (Face @(22, 25, 37, 25, 53, 61, 48, 76)); Top = $lightTop; Bottom = $lightBottom }
    )
    foreach ($f in $faces) {
        $box = New-Object System.Drawing.RectangleF 0, (0.2 * $s), $s, (0.6 * $s)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $box, $f.Top, $f.Bottom, 90
        $edge = New-Object System.Drawing.Pen $brush, $round
        $edge.LineJoin = 'Round'
        $g.FillPath($brush, $f.Path)
        $g.DrawPath($edge, $f.Path)
    }

    $g.Dispose()
    return $bmp
}

function Save-Ico([string]$file, [int[]]$sizes, [bool]$active) {
    $images = @()
    foreach ($sz in $sizes) {
        $bmp = New-RevealBitmap $sz $active
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , @{ Size = $sz; Bytes = $ms.ToArray() }
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($file)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $wh = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $bw.Write([byte]$wh); $bw.Write([byte]$wh); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$img.Bytes.Length); $bw.Write([uint32]$offset)
        $offset += $img.Bytes.Length
    }
    foreach ($img in $images) { $bw.Write($img.Bytes) }
    $bw.Close(); $fs.Close()
}

Save-Ico (Join-Path $out 'reveal.ico') @(16, 24, 32, 48, 64, 128, 256) $true
Save-Ico (Join-Path $out 'tray-active.ico') @(16, 20, 24, 32, 48) $true
Save-Ico (Join-Path $out 'tray-paused.ico') @(16, 20, 24, 32, 48) $false

# PNG used in the UI header
$big = New-RevealBitmap 256 $true
$big.Save((Join-Path $out 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
Write-Host "Icons written to $out"
