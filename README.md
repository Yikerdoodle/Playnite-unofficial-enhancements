# Playnite unofficial enhancements

Unofficial, community-made enhancements for [Playnite](https://playnite.link/). Not affiliated with the Playnite
project. Everything here works on a stock Playnite install; nothing replaces or forks Playnite itself.

## Seamless Fullscreen

An extension that makes **Fullscreen mode** feel like a console when you launch and quit games - especially
on a TV, or streamed with Sunshine/Moonlight.

Without it, launching a game from Playnite Fullscreen flashes the Windows desktop, pops up Steam's
"Starting game" window, and quitting shows the desktop and Steam's "Synchronizing cloud" screen
before Playnite comes back. With it:

| Moment | What you see |
|---|---|
| Press play | Playnite's own "Starting..." screen stays on top. Steam's launch window and the desktop never show. |
| Game opens | The game's window is swapped in on top in one step - no gap. |
| Game running | Playnite is minimized behind it. A **windowed** game gets an opaque black backdrop behind it, so it looks fullscreen; the backdrop disappears once the game really is fullscreen. |
| Quit the game | The screen goes black at once, Playnite comes back on top with the black directly behind it, and everything stays covered until Steam's windows are gone. |
| After a Steam game | The Steam client is quit (`steam.exe -shutdown`), so it isn't left in the tray. Optional. |

It only acts in Fullscreen mode. Desktop mode is untouched. Nothing is installed system-wide and no
administrator rights are needed.

> **Status:** written and tested on one machine (Windows 10 22H2, Playnite 10.x, Steam games, a 1080p
> display streamed with Sunshine). It works there; other setups may need tweaks. Issues and PRs welcome.

## Install

Requirements: Windows 10/11, Playnite (stock, from playnite.link). The helper is compiled on your machine
with the C# compiler that ships with Windows - no SDK, no downloads, no prebuilt binaries.

1. **Close Playnite completely** (Fullscreen main menu -> Exit).
2. Clone or download this repo, then in PowerShell:
   ```powershell
   .\install.ps1
   ```
3. Start Playnite, switch to Fullscreen mode, launch a game.

`install.ps1` copies the extension into `%APPDATA%\Playnite\Extensions\BlackoutCover`, compiles
`PlayniteCover.exe`, and switches off Playnite's own Fullscreen setting *"Minimize Playnite after game
startup"* (it keeps a backup of `fullscreenConfig.json`). The helper takes over that job, but only after the
game's window is really up - that is what removes the desktop flash.

Options: `-DataDir <path>` for a non-default/portable data folder, `-NoQuitSteam` to leave Steam running
after games, `-SkipFullscreenConfig` to change that Playnite setting yourself.

To undo: `.\uninstall.ps1` (add `-RestoreConfig` to bring back your old `fullscreenConfig.json`).

## Settings

Create `settings.json` next to the extension (the installer does this for `-NoQuitSteam`):

```json
{ "QuitSteamAfterGame": false }
```

## How it works

`BlackoutCover.psm1` is a Playnite script extension. In Fullscreen mode, `OnGameStarting` launches
`PlayniteCover.exe` and `OnGameStarted` / `OnGameStopped` tell it the game's process id / that the game ended
(through a small state file in `%TEMP%`). The helper is a single process for the whole game session:

1. **Wait** - pins Playnite's fullscreen window topmost (hides Steam's window and the desktop).
2. **Swap** - when the game's window (by process id, or any new large non-shell window) has been up for
   a moment, puts it on top with Playnite directly behind it, and focuses it.
3. **Watch** - minimizes Playnite once it is certain it is invisible: for a fullscreen game after a short
   wait; for a windowed game only after the black backdrop behind it has painted. A transparent,
   click-through, topmost cover stays armed. If the game's window vanishes, the cover turns black *at once*
   (and reverts if the window comes back, e.g. a resolution change).
4. **Restore** - restores Playnite and pins it #1 with the cover directly behind it (so Playnite repaints
   normally and nothing flashes), holds until Steam's windows have gone, then removes the cover.

Every phase has a fail-safe timeout so nothing can ever stay stuck on screen. A log of each step is written to
`%TEMP%\playnite-cover.log`; script errors go to Playnite's own `playnite.log` (lines containing `BlackoutCover`).

## Optional extras (for streaming to a TV)

These are independent of the extension and fairly machine-specific:

- `extras/sunshine/` - a Sunshine profile for low-latency 1080p60 HEVC with HDR available (NVIDIA NVENC),
  and `Apply-Gaming-Mode.ps1`, which backs up your config, enables the Virtual Display Driver if it is
  disabled, detects its id, applies the profile, and restarts Sunshine (it refuses while a client is
  connected). It makes the virtual display the primary one while streaming, so Playnite opens on the streamed screen.
- `extras/windows/Set-TaskbarAutoHide.ps1` - turns on taskbar auto-hide for *every* saved display. Windows keeps
  that setting per monitor, so a virtual display can show a taskbar even though your main one auto-hides.

## Known limitations

- Detecting "the game's window" is heuristic. A game that shows a small launcher first, rebuilds its window, or
  runs through an unusual launcher may need tuning (`FindGameWindow` / `BigWindowOf` in `PlayniteCover.cs`).
- The black cover and Playnite pinning work on the **primary** display only.
- Quitting Steam after a game stops any download Steam was doing; turn that off with `-NoQuitSteam`.
- Playnite's Steam library plugin id (`cb91dfc9-b977-43bf-8e70-55f46e410fab`) is used to recognise Steam games.

## License

MIT - see [LICENSE](LICENSE). Playnite itself is a separate MIT-licensed project by Josef Nemec; this
extension is independent and not affiliated with it.
