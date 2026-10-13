# Dev helper: saves a screenshot of the whole virtual desktop (or a region) as PNG, DPI aware (physical pixels).
param([string]$Path = "$env:TEMP\reveal-shot.png", [int]$X = [int]::MinValue, [int]$Y = 0, [int]$W = 0, [int]$H = 0)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DpiHelper { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i); }
"@
[void][DpiHelper]::SetProcessDPIAware()
$vx = [DpiHelper]::GetSystemMetrics(76); $vy = [DpiHelper]::GetSystemMetrics(77)
$vw = [DpiHelper]::GetSystemMetrics(78); $vh = [DpiHelper]::GetSystemMetrics(79)
if ($X -eq [int]::MinValue) { $X = $vx; $Y = $vy; $W = $vw; $H = $vh }
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $W, $H))
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "virtual screen: $vx,$vy ${vw}x$vh -> $Path"
