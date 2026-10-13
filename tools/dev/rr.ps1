# Dev helper: stop the running dev Reveal, rebuild, relaunch (optionally start capture with Ctrl+Shift+T).
param([switch]$NoStart, [switch]$Capture)
Set-Location (Join-Path $PSScriptRoot '..\..')
Get-Process Reveal -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
$out = dotnet build src\Reveal.App -nologo -v q 2>&1
$errors = $out | Select-String ": error " | % { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) } | Select -Unique | Select -First 12
if ($errors) { $errors; exit 1 }
if (-not $NoStart) {
    Start-Process "src\Reveal.App\bin\Debug\net9.0-windows10.0.19041.0\Reveal.exe" -ArgumentList "--background"
    Start-Sleep 2
    if ($Capture) { . tools\dev\sim.ps1; [Sim]::Combo(0x11, 0x10, 0x54); Start-Sleep 1 }
}
Write-Host "ok"
