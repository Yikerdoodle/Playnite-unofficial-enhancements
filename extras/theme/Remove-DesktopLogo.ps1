<#
.SYNOPSIS
  Removes the Playnite logo from Desktop mode (the logo in the sidebar / top panel).

.DESCRIPTION
  In Desktop themes the logo IS the main-menu button: an element named PART_ElemMainMenu in
  Views\Sidebar.xaml and Views\TopPanel.xaml. This script hides that element (Visibility="Collapsed"), so
  the layout closes up as if it was never there. Note the main menu is then no longer reachable by clicking
  the logo; Playnite's tray icon menu (Open / Fullscreen mode / Exit) still is.

  * Built-in "Default" desktop theme (lives in Playnite's install folder, which must not be edited): the
    script makes a COPY of it in your data folder, named "Default (no logo)", hides the logo there, and
    selects that theme in config.json (backup: config.json.bak-nologo).
  * An installed third-party desktop theme: its Views\*.xaml files are patched in place (originals kept as
    *.orig).
  * Playnite must be closed (it rewrites config.json when it exits). Idempotent.
  * No theme files are stored in this repository; the copy is made on your machine from your own install.
  * The copy does not follow Playnite's updates to the Default theme; run the script again after updating.

.PARAMETER DataDir
  Playnite's user data folder (default %APPDATA%\Playnite).

.PARAMETER PlayniteDir
  Playnite's install folder. Auto-detected (running Playnite, then %LOCALAPPDATA%\Playnite, then shortcuts).

.PARAMETER Restore
  Select the built-in Default theme again and restore patched third-party theme files.
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:APPDATA 'Playnite'),
    [string]$PlayniteDir,
    [switch]$Restore,
    [switch]$AllowRunning   # for testing against a COPY of a data folder only
)
$ErrorActionPreference = 'Stop'
$builtinId = 'Playnite_builtin_DefaultDesktop'
$copyId    = 'Local_DefaultNoLogo_7c2f1b5e-3a40-4d6e-9b1a-52e8d0f4a6c3'
$copyDir   = Join-Path $DataDir 'Themes\Desktop\DefaultNoLogo'

function Stop-With($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

if (-not $AllowRunning -and (Get-Process -Name 'Playnite.DesktopApp','Playnite.FullscreenApp' -ErrorAction SilentlyContinue)) {
    Stop-With "Playnite is running. Close it completely first (it rewrites config.json when it exits)."
}
$cfgPath = Join-Path $DataDir 'config.json'
if (-not (Test-Path -LiteralPath $cfgPath)) { Stop-With "config.json not found in $DataDir" }
$cfgText = Get-Content -LiteralPath $cfgPath -Raw
$active = ($cfgText | ConvertFrom-Json).Theme

# hide every PART_ElemMainMenu element in the theme's views; returns how many files changed
function Hide-MenuLogo([string]$themeDir, [switch]$KeepOrig) {
    $n = 0
    foreach ($f in Get-ChildItem -LiteralPath (Join-Path $themeDir 'Views') -Filter *.xaml -ErrorAction SilentlyContinue) {
        $t = Get-Content -LiteralPath $f.FullName -Raw
        $pat = '(<Border\s+x:Name="PART_ElemMainMenu")(?![^>]*Visibility=)'
        if ($t -notmatch $pat) { continue }
        if ($KeepOrig -and -not (Test-Path "$($f.FullName).orig")) { Copy-Item -LiteralPath $f.FullName "$($f.FullName).orig" }
        $new = [regex]::Replace($t, $pat, '${1} Visibility="Collapsed"')
        [void][xml]$new   # must still be well-formed XML
        [System.IO.File]::WriteAllText($f.FullName, $new, (New-Object System.Text.UTF8Encoding($true)))
        $n++
    }
    return $n
}

# --- restore -------------------------------------------------------------------------------------------
if ($Restore) {
    $bak = "$cfgPath.bak-nologo"
    if ($active -eq $copyId -and (Test-Path -LiteralPath $bak)) {
        $cfgText = $cfgText -replace '("Theme"\s*:\s*")[^"]*(")', "`${1}$builtinId`${2}"
        [System.IO.File]::WriteAllText($cfgPath, $cfgText, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Selected the built-in Default desktop theme again (the 'Default (no logo)' copy is left in $copyDir; delete it if you like)."
    }
    foreach ($o in Get-ChildItem -LiteralPath (Join-Path $DataDir 'Themes\Desktop') -Recurse -Filter '*.xaml.orig' -ErrorAction SilentlyContinue) {
        Copy-Item -LiteralPath $o.FullName ($o.FullName -replace '\.orig$', '') -Force
        Write-Host "Restored $($o.FullName -replace '\.orig$', '')"
    }
    exit 0
}

# --- built-in Default theme: work on a copy ---------------------------------------------------------------
if ($active -eq $builtinId -or $active -eq $copyId) {
    if (-not (Test-Path -LiteralPath $copyDir)) {
        if (-not $PlayniteDir) {
            $cands = @()
            $running = Get-Process -Name 'Playnite.DesktopApp','Playnite.FullscreenApp' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($running) { $cands += (Split-Path $running.Path) }
            $cands += (Join-Path $env:LOCALAPPDATA 'Playnite')
            $sh = New-Object -ComObject WScript.Shell
            foreach ($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory'), [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('CommonPrograms'))) {
                if ($d -and (Test-Path $d)) { Get-ChildItem $d -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                    try { $tp = $sh.CreateShortcut($_.FullName).TargetPath; if ($tp -match '\\Playnite\.(Desktop|Fullscreen)App\.exe$') { $cands += (Split-Path $tp) } } catch { } } }
            }
            $PlayniteDir = $cands | Where-Object { $_ -and (Test-Path (Join-Path $_ 'Themes\Desktop\Default')) } | Select-Object -First 1
        }
        if (-not $PlayniteDir -or -not (Test-Path (Join-Path $PlayniteDir 'Themes\Desktop\Default'))) {
            Stop-With "Could not find Playnite's install folder (Themes\Desktop\Default). Pass -PlayniteDir."
        }
        New-Item -ItemType Directory -Force (Split-Path $copyDir) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PlayniteDir 'Themes\Desktop\Default') $copyDir -Recurse
        $y = "Id: $copyId`r`nName: Default (no logo)`r`nAuthor: Playnite (logo hidden locally)`r`nVersion: 1.0`r`nThemeApiVersion: 2.0.0`r`n"
        # keep the original ThemeApiVersion
        $orig = Get-Content -LiteralPath (Join-Path $copyDir 'theme.yaml') -Raw
        if ($orig -match '(?m)^ThemeApiVersion:\s*(\S+)') { $y = $y -replace 'ThemeApiVersion:\s*\S+', "ThemeApiVersion: $($Matches[1])" }
        [System.IO.File]::WriteAllText((Join-Path $copyDir 'theme.yaml'), $y, (New-Object System.Text.UTF8Encoding($true)))
        Write-Host "Copied the built-in Default desktop theme to $copyDir"
    }
    $n = Hide-MenuLogo $copyDir
    Write-Host "Hid the menu logo in $n view file(s) of 'Default (no logo)'."
    if ($active -ne $copyId) {
        $bak = "$cfgPath.bak-nologo"
        if (-not (Test-Path -LiteralPath $bak)) { Copy-Item -LiteralPath $cfgPath $bak }
        $cfgText = $cfgText -replace '("Theme"\s*:\s*")[^"]*(")', "`${1}$copyId`${2}"
        [System.IO.File]::WriteAllText($cfgPath, $cfgText, (New-Object System.Text.UTF8Encoding($false)))
        [void]($cfgText | ConvertFrom-Json)   # still valid JSON
        Write-Host "Selected the 'Default (no logo)' desktop theme in config.json (backup: config.json.bak-nologo)."
    }
    Write-Host "Takes effect the next time Playnite starts." -ForegroundColor Green
    exit 0
}

# --- third-party desktop theme: patch in place ---------------------------------------------------------------
$themeDir = Get-ChildItem -LiteralPath (Join-Path $DataDir 'Themes\Desktop') -Directory -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -LiteralPath (Join-Path $_.FullName 'theme.yaml') -Raw -ErrorAction SilentlyContinue) -match ("(?m)^Id:\s*" + [regex]::Escape($active) + "\s*$") } |
    Select-Object -First 1
if (-not $themeDir) { Stop-With "The active desktop theme '$active' was not found in $DataDir\Themes\Desktop." }
$n = Hide-MenuLogo $themeDir.FullName -KeepOrig
Write-Host "Hid the menu logo in $n view file(s) of theme '$active' (originals kept as *.orig)."
Write-Host "Takes effect the next time Playnite starts." -ForegroundColor Green
