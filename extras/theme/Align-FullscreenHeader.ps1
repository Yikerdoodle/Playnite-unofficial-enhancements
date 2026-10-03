<#
.SYNOPSIS
  Shifts the Fullscreen home screen's header items ("All", "Favorites", "Most Played", ...) to the left so they
  line up with the left edge of the game tiles below.

.DESCRIPTION
  In Fullscreen themes the header row is a Grid whose first column is a fixed-width spacer (100 units in
  Playnite's default layout) that pushes the filter-preset items to the right. This script narrows that first
  column by -ShiftLeft units (default 66, which on a 1920x1080 layout moves the header's left edge from
  about x=120 to about x=54, in line with the game tiles in the "Reskin XBOXSX Ext" theme; other themes may
  need a different value - measure and pass -ShiftLeft).

  * Works on the active Fullscreen theme if it is an installed theme in the user data folder (not the
    built-in Default theme, which lives in Playnite's install folder).
  * Views\Main.xaml is backed up once as Main.xaml.orig. The script is idempotent: it sets the column to
    (100 - ShiftLeft) whatever its current value is.
  * Only the right-hand side of the header (clock, menu button) is untouched; it uses its own columns.
  * Takes effect the next time Playnite's Fullscreen mode starts.

.PARAMETER DataDir
  Playnite's user data folder (default %APPDATA%\Playnite).

.PARAMETER ShiftLeft
  How many layout units to move the header left. Default 66.

.PARAMETER Restore
  Put the original Main.xaml back.
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:APPDATA 'Playnite'),
    [double]$ShiftLeft = 66,
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'

$cfg = Join-Path $DataDir 'fullscreenConfig.json'
if (-not (Test-Path -LiteralPath $cfg)) { Write-Host "fullscreenConfig.json not found in $DataDir" -ForegroundColor Red; exit 1 }
$theme = ((Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).Theme)
$themeDir = Join-Path $DataDir "Themes\Fullscreen\$theme"
if (-not $theme -or $theme -eq 'Default' -or -not (Test-Path -LiteralPath $themeDir)) {
    Write-Host "The active Fullscreen theme ('$theme') is not an installed theme in $DataDir\Themes\Fullscreen; nothing changed." -ForegroundColor Yellow
    exit 0
}
$xaml = Join-Path $themeDir 'Views\Main.xaml'
$orig = "$xaml.orig"
if (-not (Test-Path -LiteralPath $xaml)) { Write-Host "This theme has no Views\Main.xaml; nothing to patch." -ForegroundColor Yellow; exit 0 }

if ($Restore) {
    if (Test-Path -LiteralPath $orig) { Copy-Item -LiteralPath $orig $xaml -Force; Write-Host "Restored the original Main.xaml ($theme)." }
    else { Write-Host "No backup (Main.xaml.orig) found; nothing to restore." }
    exit 0
}

$text = Get-Content -LiteralPath $xaml -Raw
# the header row: <Grid Grid.Row="0" ...> <Grid.ColumnDefinitions> <ColumnDefinition Width="100" /> ...
$pattern = '(<Grid\s+Grid\.Row="0"[^>]*>\s*<Grid\.ColumnDefinitions>\s*<ColumnDefinition\s+Width=")([0-9.]+)(")'
$m = [regex]::Match($text, $pattern)
if (-not $m.Success) { Write-Host "Could not find the header row's first column in this theme's Main.xaml; nothing changed." -ForegroundColor Yellow; exit 0 }
$target = [Math]::Max(0, 100 - $ShiftLeft)
$targetText = $target.ToString([System.Globalization.CultureInfo]::InvariantCulture)
if ($m.Groups[2].Value -eq $targetText) { Write-Host "The header is already aligned (first column = $targetText) in theme '$theme'."; exit 0 }

if (-not (Test-Path -LiteralPath $orig)) { Copy-Item -LiteralPath $xaml $orig }
$new = $text.Substring(0, $m.Groups[2].Index) + $targetText + $text.Substring($m.Groups[2].Index + $m.Groups[2].Length)
[void][xml]$new   # must still be well-formed
[System.IO.File]::WriteAllText($xaml, $new, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "Header first column: $($m.Groups[2].Value) -> $targetText in theme '$theme' (backup: Main.xaml.orig)."
Write-Host "Takes effect the next time Playnite's Fullscreen mode starts."
