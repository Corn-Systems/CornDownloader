# 🌽 Corn Downloader

> A fast, open-source Windows app installer built in C# / WinForms by **Corn Systems**.
> Drop it on a fresh Windows install, pick your apps, and hit install — winget handles the rest.

**Version:** `1.3.0`
**Platform:** Windows 10 (1809+) / 11, 64-bit
**Runtime:** none required — ships as a self-contained single exe
**License:** MIT

---

## Install

### Installer (recommended)

1. Download `CornDownloader-Setup-<version>.exe` from [Releases](https://github.com/Corn-Systems/CornDownloader/releases).
2. Run it. Installs per-machine to `C:\Program Files\Corn Downloader`, adds Start Menu + Desktop shortcuts, and registers in *Apps & features* for clean uninstall.

### Portable

Grab `CornDownloader.exe` from the same release and run it from anywhere. No install, no .NET runtime needed.

---

## Features

### ⬇ Dual Install Methods
Every catalog entry supports **winget** (preferred, silent) or a **direct URL** download as a fallback. Corn Downloader locates winget at launch — including when the per-user alias is missing (elevated launches, other accounts) — and switches modes automatically. Direct installs are silent (`/S /passive /norestart`, or a per-app override) and request UAC once each, one at a time.

### 🔍 Installed App Detection
On startup the catalog is matched against `winget export` — a single JSON parse, no per-app queries.

### ⬆ One-Click Upgrade
A background `winget upgrade` scan surfaces an **⬆ Update N Apps** button when catalog apps have updates. Truncated IDs in winget's table output are matched by unique prefix so long package names aren't missed.

### 🔁 Resumable, Verified Downloads
Direct downloads write to a `.part` file and resume with an HTTP `Range` request if interrupted. A stalled connection (no bytes for 60 s) fails fast instead of hanging. Entries with a pinned `Sha256` are verified before the installer runs.

### ⚡ Parallel Downloads, Serialised Installs
Up to **3 downloads** run at once. winget calls and installer launches are serialised (winget holds an install lock; MSI packages share the Windows Installer mutex), so you get throughput without "another install is in progress" failures or three UAC prompts at once.

### ✗ Cancel at Any Time
In-flight processes are killed, queued apps are skipped, partial downloads are kept for resume. The summary still reports what succeeded.

### 🔁 Force Reinstall
Right-click an installed tile → **Force Reinstall** re-queues it with `--force` (orange border).

### 📌 Version Pinning
Each winget tile has a **ver ▾** picker fed by `winget show --versions`. Pins are saved in exported packs.

### 📋 Export / Import Packs
Save your selection as a `.corn` (JSON) pack and re-import it on the next machine — or feed it to headless mode (below).

### 📟 Live Log Panel + Persistent Logs
A toggleable terminal-style panel streams winget/installer output. Everything is also written to `%AppData%\CornSystems\CornDownloader\logs\YYYY-MM-DD.log` (14-day retention). **📁 OPEN LOGS** in the panel jumps to the folder.

### ⬆ Update Check
On launch the app checks [GitHub Releases](https://github.com/Corn-Systems/CornDownloader/releases) and shows an **⬆ vX.Y.Z available** link in the top bar when a newer build exists. Disable with `"CheckForUpdates": false` in `settings.json`.

### 🔒 Single Instance
A second launch focuses the running window instead of racing it for `settings.json` and winget.

### 💾 Persistent Settings
Window size/state, download folder, winget preference and scope are saved to `%AppData%\CornSystems\CornDownloader\settings.json`. Settings from the previous `CornStudios` folder are migrated automatically on first run.

---

## Headless / CLI mode

For fresh-install scripts. `CornDownloader.exe` is a GUI exe, so wrap it with `start /wait` (cmd) or `Start-Process -Wait` (PowerShell) if you need the exit code.

```
CornDownloader.exe --pack my-setup.corn --silent
CornDownloader.exe --apps Git.Git,VideoLAN.VLC,Mozilla.Firefox --silent
CornDownloader.exe --pack my-setup.corn            (GUI, pack pre-selected)
```

| Option | Meaning |
|---|---|
| `--pack <file>` | Load a `.corn` / `.json` selection pack exported from the app |
| `--apps <id,id,…>` | Select catalog entries by Id (the winget Id where one exists) |
| `--silent` | Headless: install the selection and exit, no windows |
| `--folder <dir>` | Download folder for direct-URL installers |
| `--no-winget` | Force direct-URL installs even if winget is present |
| `--scope user\|machine` | Pass `--scope` to winget (default: winget decides) |
| `--check-update` | Print whether a newer release exists and exit |
| `--version` / `--help` | Print and exit |

Exit codes: `0` all succeeded · `1` one or more failed · `2` bad arguments · `3` already running · `4` nothing to install.

PowerShell example:

```powershell
$p = Start-Process "C:\Program Files\Corn Downloader\CornDownloader.exe" `
       -ArgumentList '--pack','C:\setup\dev.corn','--silent' -Wait -PassThru
exit $p.ExitCode
```

---

## App Catalog

**127 apps** across 7 categories:

| Category | Apps |
|---|---|
| 🌐 Browsers | Firefox, Chrome, Brave, Chromium, Opera GX, Tor Browser, Waterfox, LibreWolf, Min, Zen |
| 💻 Dev Tools | VS Code, Visual Studio 2022, Git, Node.js, Python, Windows Terminal, GitHub Desktop, Postman, Docker, PowerShell 7, JetBrains Toolbox, Neovim, WSL, Insomnia, FileZilla, HeidiSQL, Wireshark, Blockbench, PyPy, Rust, Go, Android Studio, and more |
| 🎬 Media & Entertainment | VLC, Spotify, OBS, Audacity, HandBrake, MPC-HC, iTunes, Plex, Stremio, foobar2000, ImageGlass, FreeTube, GIMP, DaVinci Resolve, Streamlink Twitch GUI, Blender, and more |
| 📋 Productivity | Notion, Obsidian, Slack, Zoom, LibreOffice, Notepad++, ShareX, Bitwarden, Thunderbird, Stretchly, Greenshot, WhatsApp Desktop, Ferdium, Claude Desktop, and more |
| 🎮 Gaming | Steam, Epic Games, GOG Galaxy, EA App, Ubisoft Connect, Discord, MSI Afterburner, Playnite, Minecraft, Prism Launcher, Heroic, Xbox App, Sunshine, Parsec, Overwolf, Medal, Itch.io, Battle.net, Rockstar Games Launcher, Vortex, Mod Organizer 2, CapFrameX, and more |
| 🔧 Utilities & System Tools | 7-Zip, NanaZip, Everything Search, CPU-Z, HWiNFO, HWMonitor, CrystalDiskInfo, WinDirStat, Autoruns, Malwarebytes, PowerToys, GPU-Z, Revo Uninstaller, OpenVPN, ProtonVPN, WireGuard, Microsoft PC Manager, BleachBit, O&O ShutUp10++, Bulk Rename Utility, System Informer, Ventoy, Rufus, EqualizerAPO, and more |
| 🎨 Customization | Rainmeter, Lively Wallpaper, TranslucentTB, StartAllBack, EarTrumpet, Windhawk, YASB, ModernFlyouts, Komorebi, GlazeWM, ExplorerPatcher, FancyZones (PowerToys) |

---

## Requirements

- Windows 10 1809+ or Windows 11, 64-bit
- [winget](https://aka.ms/getwinget) *(recommended — the direct-URL fallback works without it)*

---

## Build from Source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Clone and build:
   ```
   git clone https://github.com/Corn-Systems/CornDownloader.git
   cd CornDownloader
   dotnet build -c Release
   ```
   Output: `CornDownloader\bin\Release\net10.0-windows\win-x64\CornDownloader.exe`
3. Publish the single-file exe (all single-file / self-contained settings live in the `.csproj`):
   ```
   cd CornDownloader
   dotnet publish -c Release
   ```
   Output: `bin\Release\net10.0-windows\win-x64\publish\CornDownloader.exe`
4. Build the installer with [Inno Setup 6](https://jrsoftware.org/isinfo.php):
   ```
   ISCC.exe CornDownloader.iss
   ```
   Output: `installer_output\CornDownloader-Setup-<version>.exe`

### Project layout

```
CornDownloader/
├─ Program.cs          entry point, single-instance guard, CLI dispatch, crash handler
├─ MainForm.cs         main window
├─ AppTile.cs          catalog tile control
├─ SectionHeader.cs    category divider
├─ SummaryForm.cs      post-run summary dialog
├─ AppCatalog.cs       the app list (edit this to add apps)
├─ DownloadManager.cs  install / upgrade / download logic
├─ WingetRunner.cs     winget discovery, single process runner, exit-code table
├─ UpdateChecker.cs    GitHub Releases version check
├─ HeadlessRunner.cs   --silent mode
├─ CliOptions.cs       argument parser
├─ AppPaths.cs         every on-disk path (settings, logs, crash log, Downloads)
├─ AppSettings.cs      settings model + load/save
├─ SessionLog.cs       persistent daily log
├─ AppInfo.cs          name / version / repo constants
├─ Theme.cs            Corn Systems palette
├─ Dpi.cs              shared PerMonitorV2 helper (identical across Corn Systems repos)
├─ TaskbarBadge.cs     ITaskbarList3 overlay
├─ AppIconBuilder.cs   window icon (embedded .ico, drawn fallback)
├─ Assets/CornDownloader.ico
├─ app.manifest
└─ CornDownloader.iss  Inno Setup script
```

---

## Notes

- Winget installs are fully silent — no installer windows appear.
- Direct-URL installs launch the installer with silent flags and request elevation via UAC once, sequentially.
- A successfully-run installer file is deleted; a failed one is kept next to the log so you can inspect or run it by hand.
- Exit code 1641 / 3010 (and winget's reboot codes) are treated as success and flagged **restart required** in the summary.
- Settings and logs: `%AppData%\CornSystems\CornDownloader\`
- Crash log: `%AppData%\CornSystems\CornDownloader\crash.log`

---

## License

MIT — see [LICENSE](LICENSE)

---

## AI Disclosure

> ⚠ This project contains code written with the assistance of **Claude by Anthropic** (claude.ai).
> Portions of the UI, download logic, and app catalog were developed with Claude. All code has been reviewed and tested by the project maintainer.
