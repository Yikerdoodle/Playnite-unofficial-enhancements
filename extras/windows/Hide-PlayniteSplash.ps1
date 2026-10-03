<#
.SYNOPSIS
  Adds Playnite's own --hidesplashscreen switch to the launches you control: Playnite shortcuts (desktop,
  Start menu, taskbar pins) and, optionally, Sunshine app entries.

.DESCRIPTION
  Playnite documents --hidesplashscreen ("won't show startup splash screen"). This script appends it to
  every shortcut that points at Playnite.DesktopApp.exe / Playnite.FullscreenApp.exe, and with -Sunshine to
  the "cmd" of every Sunshine application that launches Playnite.

  It cannot cover Playnite's own relaunch when you switch between Desktop and Fullscreen mode (Playnite
  passes its own fixed arguments there). For that, the icon/title watcher can hide the splash window itself:
  install.ps1 -HideSplash.

  -Sunshine edits Sunshine's apps.json (in Program Files, so it asks for administrator rights). Sunshine only
  reads apps.json at startup, so the change applies after Sunshine restarts. -RestartSunshine does that, but
  refuses while a Moonlight client is connected (a restart ends the stream and closes the streamed app).

.PARAMETER Sunshine
  Also update Sunshine's apps.json.

.PARAMETER RestartSunshine
  Restart the Sunshine service afterwards (only when no client is connected).

.PARAMETER SunshineDir
  Default: C:\Program Files\Sunshine

.PARAMETER Restore
  Remove the switch again.
#>
[CmdletBinding()]
param(
    [switch]$Sunshine,
    [switch]$RestartSunshine,
    [string]$SunshineDir = 'C:\Program Files\Sunshine',
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
$flag = '--hidesplashscreen'

# --- shortcuts ----------------------------------------------------------------------------------------
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
        $has = $s.Arguments -match [regex]::Escape($flag)
        if ($Restore) { if (-not $has) { continue }; $new = ($s.Arguments -replace ('\s*' + [regex]::Escape($flag)), '').Trim() }
        else          { if ($has) { continue };      $new = (($s.Arguments + ' ' + $flag).Trim()) }
        try { $s.Arguments = $new; $s.Save(); $changed++; Write-Host "  updated: $($lnk.FullName)" }
        catch { $denied += $lnk.FullName }
    }
}
Write-Host "$changed shortcut(s) updated."
if ($denied.Count) {
    Write-Host "Could not change (needs administrator rights - re-run from an elevated PowerShell):" -ForegroundColor Yellow
    $denied | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

# --- Sunshine -----------------------------------------------------------------------------------------
if ($Sunshine) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$($MyInvocation.MyCommand.Path)`"",'-Sunshine','-SunshineDir',"`"$SunshineDir`"")
        if ($RestartSunshine) { $argList += '-RestartSunshine' }
        if ($Restore) { $argList += '-Restore' }
        Write-Host "Sunshine's config needs administrator rights - asking..."
        Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
        exit
    }
    $apps = Join-Path $SunshineDir 'config\apps.json'
    if (-not (Test-Path $apps)) { Write-Host "apps.json not found: $apps" -ForegroundColor Red; Read-Host 'Press Enter'; exit 1 }
    $text = Get-Content -LiteralPath $apps -Raw
    # the command is stored like:  "cmd": "\"C:\\...\\Playnite.FullscreenApp.exe\""   (JSON-escaped quotes)
    $pat = '(Playnite\.(?:Desktop|Fullscreen)App\.exe\\")((?:\s' + [regex]::Escape($flag) + ')?)(")'
    if ($text -notmatch $pat) { Write-Host "No Sunshine application launches Playnite; nothing to change." }
    else {
        $new = if ($Restore) { [regex]::Replace($text, $pat, '${1}${3}') } else { [regex]::Replace($text, $pat, "`${1} $flag`${3}") }
        if ($new -eq $text) { Write-Host "Sunshine apps.json already up to date." }
        else {
            Copy-Item -LiteralPath $apps "$apps.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
            [void]($new | ConvertFrom-Json)   # must still be valid JSON
            [System.IO.File]::WriteAllText($apps, $new, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "Updated Sunshine apps.json (backup kept next to it)."
        }
    }
    if ($RestartSunshine) {
        $log = Join-Path $SunshineDir 'config\sunshine.log'
        $last = if (Test-Path $log) { Select-String -Path $log -Pattern 'CLIENT CONNECTED|CLIENT DISCONNECTED|Sunshine version' | Select-Object -Last 1 } else { $null }
        $svc = Get-Service SunshineService -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -ne 'Running') {
            # some setups start/stop Sunshine on demand: never start it just to apply a config change
            Write-Host "Sunshine is not running - not starting it. The change applies the next time Sunshine starts."
        }
        elseif ($last -and $last.Line -match 'CLIENT CONNECTED') {
            Write-Host "A Moonlight client appears to be connected - NOT restarting Sunshine. Restart it yourself when you are done streaming." -ForegroundColor Yellow
        } else {
            Restart-Service SunshineService -Force
            Write-Host "Sunshine restarted."
        }
    } else {
        Write-Host "Restart Sunshine (or re-save the app in its Web UI) for the change to apply."
    }
    Start-Sleep -Seconds 3
}
