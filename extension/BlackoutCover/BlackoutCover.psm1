# Seamless Fullscreen (Blackout Cover) - Playnite script extension.
#
# In Fullscreen mode only:
#  * on game start, launches PlayniteCover.exe, which keeps Playnite's window pinned on top
#    (hiding Steam's "Starting game" window and the desktop), hands over to the game, keeps a
#    black backdrop behind a windowed game, and restores Playnite behind a black cover on exit;
#  * after a Steam game stops, quits the Steam client (optional, see settings.json).
# Does nothing in Desktop mode.
#
# Optional settings: create settings.json next to this file, e.g.  { "QuitSteamAfterGame": false }
#
# NOTE: Playnite does not keep script-level variables between function calls, so every function
# works out its own paths and settings. Playnite's logger is the variable $__logger.

function Write-CoverLog {
    param([string]$Message, [switch]$IsError)
    try {
        if ($IsError) { $__logger.Error($Message) } else { $__logger.Info($Message) }
    } catch { }   # logging must never break the script
}

function Get-CoverDir {
    # Prefer the path Playnite reports (works for portable / custom data folders); fall back to %APPDATA%.
    try {
        $dir = Join-Path $PlayniteApi.Paths.ExtensionsPath 'BlackoutCover'
        if (Test-Path -LiteralPath $dir) { return $dir }
    } catch { }
    return (Join-Path $env:APPDATA 'Playnite\Extensions\BlackoutCover')
}

function Get-CoverPaths {
    $dir = Get-CoverDir
    @{
        Dir      = $dir
        Exe      = Join-Path $dir 'PlayniteCover.exe'
        Settings = Join-Path $dir 'settings.json'
        State    = Join-Path $env:TEMP 'playnite-cover-state.txt'
    }
}

function Get-CoverSettings {
    $s = @{ QuitSteamAfterGame = $true }
    try {
        $p = Get-CoverPaths
        if (Test-Path -LiteralPath $p.Settings) {
            $j = Get-Content -LiteralPath $p.Settings -Raw | ConvertFrom-Json
            if ($null -ne $j.QuitSteamAfterGame) { $s.QuitSteamAfterGame = [bool]$j.QuitSteamAfterGame }
        }
    } catch { Write-CoverLog "BlackoutCover: could not read settings.json: $($_.Exception.Message)" -IsError }
    return $s
}

function Test-FullscreenMode {
    return ($PlayniteApi.ApplicationInfo.Mode.ToString() -eq 'Fullscreen')
}

function Stop-SteamClient {
    # "steam.exe -shutdown" is Steam's own clean-exit command (it finishes any cloud save sync first).
    $steam = Get-Process -Name steam -ErrorAction SilentlyContinue
    if (-not $steam) { Write-CoverLog 'BlackoutCover: Steam is not running, nothing to quit.'; return }
    $exe = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamExe
    if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { $exe = 'C:\Program Files (x86)\Steam\steam.exe' }
    if (Test-Path -LiteralPath $exe) {
        Start-Process -FilePath $exe -ArgumentList '-shutdown' -WindowStyle Hidden
        Write-CoverLog 'BlackoutCover: asked Steam to shut down.'
    } else {
        Write-CoverLog 'BlackoutCover: steam.exe not found, could not quit Steam.' -IsError
    }
}

function OnApplicationStarted {
    param($evnArgs)
    # Custom taskbar / Alt-Tab icon: start the tiny watcher that stamps pc-games.ico onto Playnite's
    # windows (only if both the icon and the watcher are installed). One instance only; it exits on its own.
    try {
        $p = Get-CoverPaths
        $icon = Join-Path $p.Dir 'pc-games.ico'
        $watcher = Join-Path $p.Dir 'PlayniteIcon.exe'
        if ((Test-Path -LiteralPath $icon) -and (Test-Path -LiteralPath $watcher)) {
            Start-Process -FilePath $watcher -ArgumentList ('"' + $icon + '"') -WindowStyle Hidden
            Write-CoverLog 'BlackoutCover: started the window-icon watcher.'
        }
    } catch { Write-CoverLog "BlackoutCover OnApplicationStarted: $($_.Exception.Message)" -IsError }
}

function OnGameStarting {
    param($evnArgs)
    try {
        if (-not (Test-FullscreenMode)) { return }
        $p = Get-CoverPaths
        if (-not (Test-Path -LiteralPath $p.Exe)) { Write-CoverLog "BlackoutCover: $($p.Exe) is missing - run install.ps1" -IsError; return }
        Remove-Item -LiteralPath $p.State -Force -ErrorAction SilentlyContinue
        # fail-safe: the helper gives up waiting after 120 s even if no game window is detected
        Start-Process -FilePath $p.Exe -ArgumentList 'start', ('"' + $p.State + '"'), '120'
    } catch { Write-CoverLog "BlackoutCover OnGameStarting: $($_.Exception.Message)" -IsError }
}

function OnGameStarted {
    param($evnArgs)
    try {
        if (-not (Test-FullscreenMode)) { return }
        $p = Get-CoverPaths
        Set-Content -LiteralPath $p.State -Value ("pid=" + $evnArgs.StartedProcessId) -Force
    } catch { Write-CoverLog "BlackoutCover OnGameStarted: $($_.Exception.Message)" -IsError }
}

function OnGameStopped {
    param($evnArgs)
    # Tell a still-running helper that the game is over (it restores Playnite behind a black cover).
    try {
        if (Test-FullscreenMode) {
            $p = Get-CoverPaths
            Set-Content -LiteralPath $p.State -Value 'stop' -Force
        }
    } catch { Write-CoverLog "BlackoutCover OnGameStopped (state): $($_.Exception.Message)" -IsError }

    # Steam game, played from Fullscreen mode: quit the Steam client too, so it isn't left running.
    # Steam library plugin id: cb91dfc9-b977-43bf-8e70-55f46e410fab
    try {
        if (-not (Test-FullscreenMode)) { return }
        Write-CoverLog "BlackoutCover: game stopped: '$($evnArgs.Game.Name)', plugin $($evnArgs.Game.PluginId)"
        if ((Get-CoverSettings).QuitSteamAfterGame -and $evnArgs.Game.PluginId -eq [guid]'cb91dfc9-b977-43bf-8e70-55f46e410fab') {
            Stop-SteamClient
        }
    } catch { Write-CoverLog "BlackoutCover OnGameStopped (steam): $($_.Exception.Message)" -IsError }
}
