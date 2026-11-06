# Copies the OEM hardware DLLs that ControlHub talks to into .\lib.
# They are part of the "Fn hotkeys and OSD" app (CLEVOCO.FnhotkeysandOSD) that the laptop
# vendor ships, are NOT redistributed with ControlHub, and stay untouched in their original place.
# Windows refuses to load DLLs straight out of WindowsApps, so a private copy is needed.
#
#   powershell -ExecutionPolicy Bypass -File .\setup-lib.ps1
#   powershell -ExecutionPolicy Bypass -File .\setup-lib.ps1 -Dest C:\some\other\lib   (dry location)

param([string]$Dest = (Join-Path $PSScriptRoot 'lib'))

$ErrorActionPreference = 'Stop'
$pkg = Get-AppxPackage -Name 'CLEVOCO.FnhotkeysandOSD' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pkg) {
    Write-Host 'The "Fn hotkeys and OSD" app (CLEVOCO.FnhotkeysandOSD) is not installed.' -ForegroundColor Red
    Write-Host 'Install it from your laptop vendor''s driver page (Axioo: Control Center / Fn hotkeys driver), then run this again.'
    exit 1
}
Write-Host ("Found {0} {1}" -f $pkg.Name, $pkg.Version)

$src = Join-Path $pkg.InstallLocation 'FnKey\CC40'
$files = 'InsydeDCHU.dll', 'InsydeAirplanemodehid.dll', 'msvcp140.dll', 'vcruntime140.dll'
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
foreach ($f in $files) {
    $from = Join-Path $src $f
    if (-not (Test-Path $from)) {
        if ($f -eq 'InsydeDCHU.dll') { Write-Host "Missing $from - this package layout is unknown, please open an issue." -ForegroundColor Red; exit 1 }
        Write-Host "  (optional) $f not found, skipped" -ForegroundColor Yellow
        continue
    }
    Copy-Item -LiteralPath $from -Destination (Join-Path $Dest $f) -Force
    Write-Host "  copied $f"
}
Write-Host "Done. DLLs are in $Dest" -ForegroundColor Green
