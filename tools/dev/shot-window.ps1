# Dev helper: screenshot of the top-level window(s) of a process (DPI aware) -> PNG
param([string]$Name = "Reveal", [string]$Path = "$env:TEMP\reveal-window.png", [int]$Pad = 20)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
 [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; } }
"@
[void][W]::SetProcessDPIAware()
$p = Get-Process $Name | ? { $_.MainWindowHandle -ne 0 } | select -First 1
if (-not $p) { Write-Host "no window"; exit 1 }
$r = New-Object W+R; [void][W]::GetWindowRect($p.MainWindowHandle, [ref]$r)
$x = $r.L - $Pad; $y = $r.T - $Pad; $w = $r.Rt - $r.L + 2 * $Pad; $h = $r.B - $r.T + 2 * $Pad
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "window rect: $($r.L),$($r.T) -> $($r.Rt),$($r.B)  saved $Path"
