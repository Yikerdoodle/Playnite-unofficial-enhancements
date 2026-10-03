<#
.SYNOPSIS
  Removes the Playnite logo from the header of Fullscreen mode's main menu, in the active Fullscreen theme.

.DESCRIPTION
  Fullscreen themes draw the main menu's header (normally the Playnite logo) from a XAML template named
  "MainMenuHeaderTemplate" in Views\MainMenu.xaml. This script replaces that template's content with an
  empty, zero-sized element, so the layout closes up as if the logo was never there.

  * Works on the theme selected in fullscreenConfig.json, if it lives in the user data folder
    (i.e. an installed third-party theme). The built-in "Default" theme lives in Playnite's install
    folder and is not touched.
  * The original file is kept as MainMenu.xaml.orig (made once). The script is idempotent.
  * Re-run it after the theme author updates the theme (an update restores the logo).
  * Only the theme's local file is edited; no theme files are copied into this repository.

.PARAMETER DataDir
  Playnite's user data folder (default %APPDATA%\Playnite).

.PARAMETER Restore
  Put the original MainMenu.xaml back.
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:APPDATA 'Playnite'),
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'

$cfg = Join-Path $DataDir 'fullscreenConfig.json'
if (-not (Test-Path -LiteralPath $cfg)) { Write-Host "fullscreenConfig.json not found in $DataDir" -ForegroundColor Red; exit 1 }
$theme = ((Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).Theme)
$themeDir = Join-Path $DataDir "Themes\Fullscreen\$theme"
if (-not $theme -or $theme -eq 'Default' -or -not (Test-Path -LiteralPath $themeDir)) {
    Write-Host "The active Fullscreen theme ('$theme') is not an installed theme in $DataDir\Themes\Fullscreen." -ForegroundColor Yellow
    Write-Host "Nothing changed. (The built-in Default theme is part of Playnite's install and is left alone.)"
    exit 0
}
$xaml = Join-Path $themeDir 'Views\MainMenu.xaml'
if (-not (Test-Path -LiteralPath $xaml)) { Write-Host "This theme has no Views\MainMenu.xaml - nothing to patch." -ForegroundColor Yellow; exit 0 }
$orig = "$xaml.orig"

if ($Restore) {
    if (Test-Path -LiteralPath $orig) { Copy-Item -LiteralPath $orig $xaml -Force; Write-Host "Restored the original MainMenu.xaml ($theme)." }
    else { Write-Host "No backup (MainMenu.xaml.orig) found - nothing to restore." }
    exit 0
}

$text = Get-Content -LiteralPath $xaml -Raw
$pattern = '(?s)(<DataTemplate\s+x:Key="MainMenuHeaderTemplate"\s*>).*?(</DataTemplate>)'
if ($text -notmatch $pattern) { Write-Host "MainMenuHeaderTemplate was not found in this theme's MainMenu.xaml; nothing changed." -ForegroundColor Yellow; exit 0 }
$replacement = '${1}<Grid Width="0" Height="0" Visibility="Collapsed" />${2}'
$new = [regex]::Replace($text, $pattern, $replacement)
if ($new -eq $text) { Write-Host "The main menu logo is already removed in theme '$theme'."; exit 0 }

if (-not (Test-Path -LiteralPath $orig)) { Copy-Item -LiteralPath $xaml $orig }
# keep the file's original encoding/BOM handling simple: write UTF-8 with BOM like Playnite's own themes
[System.IO.File]::WriteAllText($xaml, $new, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "Removed the main menu logo from theme '$theme' (backup: MainMenu.xaml.orig)."
Write-Host "It takes effect the next time Playnite's Fullscreen mode starts."
