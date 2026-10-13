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

    # almond eye
    $cx = $s / 2; $cy = $s / 2
    $eye = New-Object System.Drawing.Drawing2D.GraphicsPath
    $left = New-Object System.Drawing.PointF (0.17 * $s), $cy
    $right = New-Object System.Drawing.PointF (0.83 * $s), $cy
    $eye.AddBezier($left, (New-Object System.Drawing.PointF (0.34 * $s), (0.22 * $s)), (New-Object System.Drawing.PointF (0.66 * $s), (0.22 * $s)), $right)
    $eye.AddBezier($right, (New-Object System.Drawing.PointF (0.66 * $s), (0.78 * $s)), (New-Object System.Drawing.PointF (0.34 * $s), (0.78 * $s)), $left)
    $eye.CloseFigure()
    $lineAlpha = if ($active) { 235 } else { 150 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($lineAlpha, 245, 247, 250)), ([Math]::Max(1.2, 0.058 * $s))
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $eye)

    # iris
    $ir = 0.165 * $s
    $irisRect = New-Object System.Drawing.RectangleF ($cx - $ir), ($cy - $ir), (2 * $ir), (2 * $ir)
    if ($active) {
        $iris = New-Object System.Drawing.Drawing2D.LinearGradientBrush $irisRect, ([System.Drawing.Color]::FromArgb(255, 150, 182, 255)), ([System.Drawing.Color]::FromArgb(255, 86, 118, 255)), 60
    } else {
        $iris = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 112, 118, 130))
    }
    $g.FillEllipse($iris, $irisRect)
    $pr = 0.062 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 12, 13, 17))), ($cx - $pr), ($cy - $pr), (2 * $pr), (2 * $pr))
    if ($active -and $size -ge 32) {
        $hr = 0.032 * $s
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))), ($cx - 0.07 * $s - $hr), ($cy - 0.07 * $s - $hr), (2 * $hr), (2 * $hr))
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
