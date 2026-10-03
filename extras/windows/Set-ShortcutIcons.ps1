<#
.SYNOPSIS
  Gives every Playnite shortcut (desktop, Start menu, taskbar pins) a custom icon made from a PNG.

.DESCRIPTION
  * Converts -IconPng to a multi-size .ico and stores it in %LOCALAPPDATA%\Playnite-Enhancements\ (so the
    shortcuts keep working even if you later remove the extension).
  * Finds .lnk files whose target is Playnite.DesktopApp.exe / Playnite.FullscreenApp.exe in the usual
    places and sets their icon. Shortcuts in shared folders (Public Desktop, the all-users Start menu)
    need administrator rights; the script reports any it could not change - re-run it from an
    elevated PowerShell for those.
  * Refreshes the Windows icon cache so the change shows up.
  * Writes the same .ico into the Playnite extension folder, where the taskbar-icon watcher
    (PlayniteIcon.exe, installed by install.ps1) picks it up for the *running* Playnite window.

  The icon image itself is NOT part of this repository: pass your own PNG.

.PARAMETER IconPng
  Path to the image to use.

.PARAMETER DataDir
  Playnite's user data folder (default %APPDATA%\Playnite), for the extension's copy of the icon.

.PARAMETER Restore
  Put the default Playnite icon back on the shortcuts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Set')][string]$IconPng,
    [string]$DataDir = (Join-Path $env:APPDATA 'Playnite'),
    [Parameter(Mandatory, ParameterSetName = 'Restore')][switch]$Restore
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here '..\..\scripts\ConvertTo-Ico.ps1')

$icoDir = Join-Path $env:LOCALAPPDATA 'Playnite-Enhancements'
$ico = Join-Path $icoDir 'pc-games.ico'
if (-not $Restore) {
    New-Item -ItemType Directory -Force $icoDir | Out-Null
    New-IcoFromPng -PngPath $IconPng -IcoPath $ico
    $extDir = Join-Path $DataDir 'Extensions\BlackoutCover'
    if (Test-Path $extDir) { Copy-Item $ico (Join-Path $extDir 'pc-games.ico') -Force }
    Write-Host "Icon written: $ico"
}

# folders that can hold Playnite shortcuts
$folders = @(
    [Environment]::GetFolderPath('Desktop'),
    [Environment]::GetFolderPath('CommonDesktopDirectory'),
    [Environment]::GetFolderPath('Programs'),
    [Environment]::GetFolderPath('CommonPrograms'),
    (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'),
    (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$sh = New-Object -ComObject WScript.Shell
$changed = 0; $denied = @()
foreach ($f in $folders) {
    foreach ($lnk in (Get-ChildItem -LiteralPath $f -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue)) {
        try { $s = $sh.CreateShortcut($lnk.FullName) } catch { continue }
        if ($s.TargetPath -notmatch '\\Playnite\.(Desktop|Fullscreen)App\.exe$') { continue }
        $want = if ($Restore) { "$($s.TargetPath),0" } else { "$ico,0" }
        if ($s.IconLocation -eq $want) { continue }
        try { $s.IconLocation = $want; $s.Save(); $changed++; Write-Host "  updated: $($lnk.FullName)" }
        catch { $denied += $lnk.FullName }
    }
}
Write-Host "$changed shortcut(s) updated."
if ($denied.Count) {
    Write-Host "Could not change (needs administrator rights - re-run this script from an elevated PowerShell):" -ForegroundColor Yellow
    $denied | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
# refresh the icon cache
Start-Process -FilePath "$env:WINDIR\System32\ie4uinit.exe" -ArgumentList '-show' -WindowStyle Hidden -ErrorAction SilentlyContinue
