<#
.SYNOPSIS
  Installs the "Seamless Fullscreen (Blackout Cover)" extension into a stock Playnite.

.DESCRIPTION
  1. Copies the extension into <Playnite data folder>\Extensions\BlackoutCover.
  2. Compiles PlayniteCover.cs into PlayniteCover.exe with the C# compiler that ships with Windows
     (.NET Framework 4) - no SDK, no downloads, no prebuilt binaries.
  3. Turns off Playnite's own "minimize after game startup" Fullscreen setting
     (a backup of fullscreenConfig.json is kept). The helper takes over that job, after the
     game's window is up, so the desktop is never exposed.

  Playnite must be closed (Fullscreen main menu -> Exit). Nothing is installed system-wide and
  nothing needs administrator rights.

.PARAMETER DataDir
  Playnite's user data folder. Default: %APPDATA%\Playnite. For a portable install, pass the
  portable folder's data directory.

.PARAMETER NoQuitSteam
  Do not quit the Steam client after a Steam game stops (writes settings.json).

.PARAMETER SkipFullscreenConfig
  Leave Playnite's fullscreenConfig.json alone (you must then turn off
  "Minimize Playnite after game startup" in Playnite's Fullscreen settings yourself).

.PARAMETER IconPng
  Optional. A PNG to use as Playnite's taskbar / Alt-Tab icon. It is converted to pc-games.ico in the
  extension folder, and a tiny watcher (PlayniteIcon.exe) applies it to Playnite's windows while it runs.
  To also change the *shortcut* icons (desktop, Start menu, pins), run extras\windows\Set-ShortcutIcons.ps1.

.PARAMETER WindowTitle
  Optional. Rename Playnite's windows (the taskbar label and Alt-Tab name) from "Playnite" to this text,
  e.g. -WindowTitle Games. Stored in settings.json; applied by the same background watcher.

.PARAMETER NoRunAtLogon
  By default, when a custom icon and/or window title is configured, the tiny watcher (PlayniteIcon.exe) is
  registered to start at logon (HKCU Run key) and started right away, so Playnite's window is fixed the
  instant it is created and its own icon/title never show. Pass this to skip that; the extension then starts
  the watcher when Playnite starts (which can show Playnite's own icon/title for a moment first).

.PARAMETER AllowRunning
  Skip the "Playnite is closed" check. Only for testing the installer against a COPY of a data
  folder (-DataDir); never use it against your real, running Playnite.

.EXAMPLE
  .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:APPDATA 'Playnite'),
    [switch]$NoQuitSteam,
    [switch]$SkipFullscreenConfig,
    [string]$IconPng,
    [string]$WindowTitle,
    [switch]$NoRunAtLogon,
    [switch]$AllowRunning
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $here 'extension\BlackoutCover'

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# --- checks -------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $DataDir)) {
    Fail "Playnite's data folder was not found: $DataDir`n       Start Playnite once (so it creates it), close it, then run this again - or pass -DataDir."
}
if (-not $AllowRunning -and (Get-Process -Name 'Playnite.DesktopApp','Playnite.FullscreenApp' -ErrorAction SilentlyContinue)) {
    Fail "Playnite is running. Close it completely (Fullscreen main menu -> Exit, or exit from the tray), then run this again."
}
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Fail "The .NET Framework 4 C# compiler (csc.exe) was not found; it ships with Windows 10/11." }

# --- compile first, into a temp folder, so a failure leaves the existing install untouched ----------
$tmp = Join-Path $env:TEMP ("PlayniteCover-build-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $exeTmp = Join-Path $tmp 'PlayniteCover.exe'
    & $csc /nologo /target:winexe "/out:$exeTmp" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $srcDir 'PlayniteCover.cs')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exeTmp)) { Fail "Compiling PlayniteCover.cs failed (csc exit code $LASTEXITCODE)." }
    $iconExeTmp = Join-Path $tmp 'PlayniteIcon.exe'
    & $csc /nologo /target:winexe "/out:$iconExeTmp" (Join-Path $srcDir 'PlayniteIcon.cs')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $iconExeTmp)) { Fail "Compiling PlayniteIcon.cs failed (csc exit code $LASTEXITCODE)." }

    # --- install -----------------------------------------------------------------------------------
    $target = Join-Path $DataDir 'Extensions\BlackoutCover'
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-Process -Name PlayniteCover, PlayniteIcon -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $srcDir 'extension.yaml'), (Join-Path $srcDir 'BlackoutCover.psm1'), (Join-Path $srcDir 'PlayniteCover.cs'), (Join-Path $srcDir 'PlayniteIcon.cs') $target -Force
    Copy-Item $exeTmp, $iconExeTmp $target -Force

    if ($IconPng) {
        . (Join-Path $here 'scripts\ConvertTo-Ico.ps1')
        New-IcoFromPng -PngPath $IconPng -IcoPath (Join-Path $target 'pc-games.ico')
        Write-Host "Taskbar icon installed (pc-games.ico). Shortcut icons: run extras\windows\Set-ShortcutIcons.ps1 -IconPng <png>."
    }
    Get-ChildItem $target -File | Unblock-File -ErrorAction SilentlyContinue
    Write-Host "Installed extension to $target"

    if ($NoQuitSteam -or $WindowTitle) {
        # merge into any existing settings.json so earlier choices are kept
        $sf = Join-Path $target 'settings.json'
        $obj = [ordered]@{}
        if (Test-Path -LiteralPath $sf) { try { (Get-Content -LiteralPath $sf -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $obj[$_.Name] = $_.Value } } catch { } }
        if ($NoQuitSteam) { $obj['QuitSteamAfterGame'] = $false }
        if ($WindowTitle) { $obj['WindowTitle'] = $WindowTitle }
        ($obj | ConvertTo-Json) | Set-Content -LiteralPath $sf -Encoding UTF8
        if ($NoQuitSteam) { Write-Host "Steam will NOT be quit after games (settings.json written)." }
        if ($WindowTitle) { Write-Host "Playnite's windows will be titled '$WindowTitle' (settings.json written)." }
    }

    # --- icon / title watcher: start at logon (and now) so Playnite's own icon/title never flash ---------------
    if (-not $NoRunAtLogon) {
        $icoFile = Join-Path $target 'pc-games.ico'
        $titleVal = $WindowTitle
        if (-not $titleVal) {
            $sf = Join-Path $target 'settings.json'
            if (Test-Path -LiteralPath $sf) { try { $titleVal = [string](Get-Content -LiteralPath $sf -Raw | ConvertFrom-Json).WindowTitle } catch { } }
        }
        $haveIco = Test-Path -LiteralPath $icoFile
        if ($haveIco -or $titleVal) {
            $watcherExe = Join-Path $target 'PlayniteIcon.exe'
            $argList = @(('"' + $(if ($haveIco) { $icoFile } else { '-' }) + '"'))
            if ($titleVal) { $argList += ('"' + $titleVal + '"') }
            $argList += '--resident'
            Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PlayniteIconWatcher' -Value ('"' + $watcherExe + '" ' + ($argList -join ' '))
            Start-Process -FilePath $watcherExe -ArgumentList $argList -WindowStyle Hidden
            Write-Host "Icon/title watcher registered to start at logon and started now (remove with uninstall.ps1)."
        }
    }
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# --- Playnite's own minimize-after-startup setting -----------------------------------------------------
if (-not $SkipFullscreenConfig) {
    $fc = Join-Path $DataDir 'fullscreenConfig.json'
    if (-not (Test-Path -LiteralPath $fc)) {
        Write-Host "NOTE: $fc does not exist yet (Playnite's Fullscreen mode has never been run)." -ForegroundColor Yellow
        Write-Host "      Open Fullscreen mode once, exit Playnite, and run install.ps1 again - or turn off" -ForegroundColor Yellow
        Write-Host "      'Minimize Playnite after game startup' in Playnite's Fullscreen settings." -ForegroundColor Yellow
    } else {
        $text = Get-Content -LiteralPath $fc -Raw
        if ($text -match '"MinimizeAfterGameStartup"\s*:\s*true') {
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            Copy-Item -LiteralPath $fc "$fc.bak-$stamp"
            $text = $text -replace '("MinimizeAfterGameStartup"\s*:\s*)true', '${1}false'
            [System.IO.File]::WriteAllText($fc, $text, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "Turned off 'MinimizeAfterGameStartup' in fullscreenConfig.json (backup: fullscreenConfig.json.bak-$stamp)."
        } else {
            Write-Host "'MinimizeAfterGameStartup' is already off in fullscreenConfig.json."
        }
    }
}

Write-Host ""
Write-Host "Done. Start Playnite, switch to Fullscreen mode, and launch a game." -ForegroundColor Green
Write-Host "Diagnostics: %TEMP%\playnite-cover.log   (and Playnite's own playnite.log, lines containing 'BlackoutCover')"
