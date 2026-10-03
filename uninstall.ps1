<#
.SYNOPSIS
  Removes the "Seamless Fullscreen (Blackout Cover)" extension from Playnite.

.PARAMETER DataDir
  Playnite's user data folder. Default: %APPDATA%\Playnite.

.PARAMETER RestoreConfig
  Also restore the newest fullscreenConfig.json backup that install.ps1 made (turns Playnite's own
  "minimize after game startup" back on, if it was on before).
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:APPDATA 'Playnite'),
    [switch]$RestoreConfig,
    [switch]$AllowRunning   # testing against a COPY of a data folder only
)
$ErrorActionPreference = 'Stop'

if (-not $AllowRunning -and (Get-Process -Name 'Playnite.DesktopApp','Playnite.FullscreenApp' -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Playnite is running. Close it completely first." -ForegroundColor Red; exit 1
}
Get-Process -Name PlayniteCover -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$target = Join-Path $DataDir 'Extensions\BlackoutCover'
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force; Write-Host "Removed $target" }
else { Write-Host "Extension folder not found (already removed?)." }

if ($RestoreConfig) {
    $fc = Join-Path $DataDir 'fullscreenConfig.json'
    $bak = Get-ChildItem -LiteralPath $DataDir -Filter 'fullscreenConfig.json.bak-*' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
    if ($bak) { Copy-Item -LiteralPath $bak.FullName $fc -Force; Write-Host "Restored fullscreenConfig.json from $($bak.Name)" }
    else { Write-Host "No fullscreenConfig.json backup found." }
}
Remove-Item (Join-Path $env:TEMP 'playnite-cover*.txt'), (Join-Path $env:TEMP 'playnite-cover.log') -ErrorAction SilentlyContinue
Write-Host "Done." -ForegroundColor Green
