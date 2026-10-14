# Builds the self-contained publish folder and the installer (dist\Reveal-Setup-<version>.exe).
# Requirements: .NET 9 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
# The offline lexicon must already exist in data\build (python tools\lexicon\build_lexicon.py).
param([string]$Version = "1.0.0", [switch]$SkipPublish)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

if (-not (Test-Path "data\build\lexicon.db")) { throw "data\build\lexicon.db not found. Run: python tools\lexicon\fetch_sources.py; python tools\lexicon\build_lexicon.py" }

if (-not $SkipPublish) {
    if (Test-Path publish\win-x64) { Remove-Item publish\win-x64 -Recurse -Force }
    dotnet publish src\Reveal.App -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -p:Version=$Version -o publish\win-x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc "/DMyAppVersion=$Version" installer\Reveal.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-Item "dist\Reveal-Setup-$Version.exe" | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
