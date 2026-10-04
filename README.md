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
after games, `-SkipFullscreenConfig` to change that Playnite setting yourself, `-IconPng <file>` for a custom
taskbar icon (see below).

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

## Custom icon (shortcuts and taskbar)

Give Playnite any icon you like (for example a "PC Games" tile that matches your Moonlight launcher). The
image is **not** included in this repository - pass your own PNG (any aspect ratio; it is centered on a square
canvas, not stretched).

- **Taskbar / Alt-Tab icon of the running window:** `.\install.ps1 -IconPng path\to\icon.png`. Windows takes a
  running program's taskbar icon from the window itself, so a tiny background process (`PlayniteIcon.exe`,
  compiled by the installer) stamps the icon onto Playnite's windows - see *How the watcher avoids any
  flash* below.
- **Shortcut icons (desktop, Start menu, taskbar pins):** `.\extras\windows\Set-ShortcutIcons.ps1 -IconPng path\to\icon.png`
  converts the PNG to a multi-size `.ico` and sets it on every shortcut that points at Playnite. Shortcuts in
  shared folders (Public Desktop, all-users Start menu) may need an elevated PowerShell; the script tells
  you which ones. `-Restore` puts the default icon back.

### The "Exiting Playnite..." screen

Fullscreen mode shows its own "Exiting Playnite..." screen (logo and spinner) while it shuts down, and Playnite
has no setting to turn it off. So it is covered instead: the moment that screen appears (or Playnite reports that
it is stopping) a black cover goes over the whole primary screen and stays until Playnite has really gone - no
Playnite process left, plus a short grace period. If a new Playnite starts during that grace period (Playnite
relaunches itself when you switch between Desktop and Fullscreen mode) the cover is held until the new window
is on screen, so mode switches are smooth too. Two independent triggers start it, and only one runs: the
extension's `OnApplicationStopped` and the window watcher (it recognises a window titled "Exiting Playnite..."
belonging to the Fullscreen app, so it needs an English Playnite). It has a 40-second fail-safe and does nothing
in Desktop mode. Opt out with `{ "CoverExit": false }` in `settings.json`.
### Dialogs that need you are never hidden

While the helper keeps Playnite on top during a launch (and again while it restores Playnite after a game),
it recognizes windows that need *you* - Steam's controller warning, a cloud-save question - by their title,
`Steam Dialog` (process `steam` / `steamwebhelper`). Those are let through: Playnite is un-pinned, any black
cover is removed, the dialog is raised to the top, and the helper's fail-safe timer is paused while it is up.
Normal behaviour resumes once the dialog is gone. Steam's own "Launching..." progress window is a different
window and is still hidden. It deliberately does **not** click "OK" for you: the same kind of window can ask
real questions, and a blind click could pick the wrong answer. The helper also exits at once if Playnite
itself is closed while waiting, so a cover can never linger. The log (`%TEMP%\playnite-cover.log`) lists the
visible Steam windows every two seconds, to make it easy to check these titles on your Steam version.
## Rename Playnite in the taskbar

Windows labels a running program's taskbar button (and its Alt-Tab entry) with the **window title**, which
Playnite sets to "Playnite". `.\install.ps1 -WindowTitle Games` (or `{ "WindowTitle": "Games" }` in
`settings.json`) makes the same background watcher rename Playnite's windows. Only windows titled exactly
"Playnite" are renamed, so dialogs keep their own titles.

### How the watcher avoids any flash

If the watcher only started *after* Playnite did, Playnite's own icon and "Playnite" title would show for a
moment first. So `install.ps1` registers the watcher to start at logon (a per-user `HKCU\...\Run` entry,
no admin rights) and starts it immediately. It is event-driven: a Windows event hook fires the instant any
window is created, shown or retitled, and a Playnite window gets the icon/title right then, followed by a
short fast-polling burst; a slow poll remains as a safety net. It is idle otherwise (a few MB of memory, no
polling load). In a test on a Notepad window, the custom icon was in place at the first visible moment and a
wrong title was visible for only 4-16 ms (about one screen refresh).

Prefer not to have a resident process? `.\install.ps1 -NoRunAtLogon` skips the Run entry; the extension then
starts the watcher when Playnite starts, which can show Playnite's own icon/title for a fraction of a second.
`uninstall.ps1` removes the Run entry. Timestamps of what the watcher did are in `%TEMP%\playnite-icon.log`.

## Hide Playnite's splash screen

Two layers, because Playnite can start in several ways:

- **`.\install.ps1 -HideSplash`** - the always-running watcher hides Playnite's splash *window* the instant it
  appears (it matches the window class, `SplashScreen` by default, and logs the class of every new Playnite
  window to `%TEMP%\playnite-icon.log` so this can be checked or changed with `--splash-class=`). This covers
  **every** start, including the relaunch Playnite does itself when you switch between Desktop and Fullscreen
  mode, where no command-line switch can be added.
- **`.\extras\windows\Hide-PlayniteSplash.ps1`** - adds Playnite's own documented `--hidesplashscreen` switch
  ("won't show startup splash screen") to every Playnite shortcut (desktop, Start menu, taskbar pins) and, with
  `-Sunshine`, to Sunshine app entries that launch Playnite (administrator rights; Sunshine reads `apps.json` at
  startup, so the change applies the next time it starts; `-RestartSunshine` restarts it only if it is already
  running and no client is connected). `-Restore` removes the switch again.

## Remove the logo from the Fullscreen main menu

`.\extras\theme\Remove-MainMenuLogo.ps1` empties the header of Fullscreen mode's main menu (normally the
Playnite logo) in the **active, installed Fullscreen theme**, so the layout closes up as if it was never there.
It edits only that theme's local `Views\MainMenu.xaml` (keeping `MainMenu.xaml.orig`); no theme files are copied
into this repository. Re-run it after the theme author updates the theme. `-Restore` undoes it. The built-in
Default theme is part of Playnite's install and is not touched.

## Line the Fullscreen header up with the game tiles

With the logo gone, the home screen's header items ("All", "Favorites", "Most Played"...) can look off-centre
because a fixed spacer column pushes them right. `.\extras\theme\Align-FullscreenHeader.ps1` narrows that
spacer by `-ShiftLeft` units (default 66, measured for the "Reskin XBOXSX Ext" theme at 1920x1080: header
text from x=120 to x=54, level with the left edge of the game tiles). Other themes may need another value.
`-Restore` undoes it.

## Remove the logo from Desktop mode

`.\extras\theme\Remove-DesktopLogo.ps1` does the same for Desktop mode. There the logo *is* the main-menu
button (`PART_ElemMainMenu` in `Sidebar.xaml` / `TopPanel.xaml`), so the script hides that element. Playnite's
built-in Default theme lives in the install folder and must not be edited, so the script makes a **copy** of it
called "Default (no logo)" in your data folder (made on your machine from your own install - nothing from
Playnite's theme is stored in this repo), hides the logo there, and selects it in `config.json` (backup kept).
Playnite must be closed. The main menu is then reached from Playnite's tray icon. `-Restore` selects the
built-in theme again.

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
