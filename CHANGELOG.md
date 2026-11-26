# Changelog

All notable changes to Corn Downloader from **1.3.0** onward. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.4.0] - 2026-11-26

Presets, uninstall, status filters, a darker window, a per-monitor DPI pass, a round of bug fixes and 57 new catalog apps.

### Fixed
- **Per-monitor DPI:** moving the window to a monitor with different scaling only resized the four outer panels — every button, label, tile and font stayed at the old size. All chrome, tiles, section headers and the summary dialog now re-lay out on `DpiChanged`, and the window re-checks its DPI when it is first shown (opening on a secondary monitor) and when it regains focus.
- **Fonts on mixed-DPI setups:** point-size fonts rasterise at the system DPI captured at logon, so text was the wrong size on a secondary monitor even after the layout scaled. Fonts are now built in pixels for the current monitor (`Theme.Font`) and cached (one shared instance per size instead of ~1,800 per launch).
- **Per-app log drawer opened empty:** the log `RichTextBox` was created hidden inside the drawer and never shown, so "log ▸" revealed a blank panel.
- **Cancelled / skipped apps stuck on "⏳ Installing..."** with their progress bar visible until restart (`SetStatus` had no `Skipped` case). They now show "⊘ Skipped".
- **Sidebar categories rendered as "MEDIA  ENTERTAIN."** with an underlined E — `&` was treated as a mnemonic. Same fix for tile category badges, section headers and summary rows ("O&O ShutUp10++").
- **Sidebar clipped at the minimum window height** (and at 150%+ scaling): Export/Import and the scan status fell off the bottom. The sidebar now scrolls vertically.
- **Summary dialog:** the anchored Close button could end up off-screen (anchor computed against the footer's default 200 px width); rows ignored the panel padding; long errors overlapped the app name. Skipped apps are now listed too.
- **Update badge / button out of date** after an install or upgrade succeeded through the normal Install flow.
- **Cancel** could be clicked repeatedly and gave no feedback; it now disables itself and says it is waiting for the running installer.
- **Install/Update buttons** were briefly clickable while waiting for the background scan to finish.
- **MSI `SilentArgs` were ignored** even though the catalog docs said they applied.
- **"Another install is in progress"** (winget `0x8A150102` / MSI 1618 caused by Windows Update or another installer) failed immediately; winget installs now wait 30 s and retry once.
- **Update check** could report a phantom update for a `v1.4.0` tag against a `1.4` build (missing version components compared as -1).
- **Import:** a pack entry without a pinned version kept a pin left over from earlier in the session; already-installed apps in a pack were reported as "not found in the catalog".
- **Window size** was saved in raw pixels, so a size saved at 100% opened too small at 150% (and vice versa). It is now stored in logical pixels and clamped to the screen's working area (old settings are migrated once).
- **Section headers** were wider than the grid's display area (padding not subtracted), which could add a horizontal scrollbar, and kept an event subscription alive after being disposed.
- Tile hover flickered when the cursor moved over a label inside the tile; clicking the method badge / status text didn't toggle the tile.
- The window and taskbar icon were the 32 px frame upscaled at high DPI; the taskbar selection badge was a fixed 16 px.
- Long winget status lines ran underneath the Cancel/Clear buttons; the status text now ellipsises.

### Added
- **Preset strip:** one-click selections for 🎮 Gaming PC, 💻 Dev Machine, 🎓 Student and 🔐 Security Lab. **＋ SAVE** stores the current selection as your own preset; presets are ordinary `.corn` packs in `%AppData%\CornSystems\CornDownloader\presets` (share them, edit them, or pass them to `--pack`). Hover a preset to see what it selects.
- **Uninstall:** right-click an installed tile → **Uninstall...** runs `winget uninstall --exact --silent` after a confirmation, then flips the tile back to installable.
- **Show filters:** ALL · ✔ INSTALLED · ⬆ UPDATES · ○ NOT INSTALLED · ☑ SELECTED chips with live counts, combined with the category and search. Importing a pack or picking a preset jumps to ☑ SELECTED so you can review it.
- **Dark title bar** on every window (Windows 10 1809+), coloured to match the top bar on Windows 11.
- **Themed scrollbars:** the app grid, sidebar and summary dialog use a thin Corn Systems scrollbar instead of the light-grey native one.
- **57 apps** (catalog is now 177) and a new **🛡️ Security & Privacy** category. New: Vivaldi, Floorp, Visual Studio 2026, .NET 10 SDK, GitHub CLI, Zed, Sublime Text 4, Bruno, DBeaver, WinMerge, Oh My Posh, PuTTY, WinSCP, CMake, Temurin JDK 21, Bun, Kdenlive, Shotcut, Krita, Inkscape, Paint.NET, Kodi, yt-dlp, Joplin, Logseq, Zotero, Anki, SumatraPDF, ONLYOFFICE, Signal, Telegram, Flow Launcher, Espanso, RetroArch, Moonlight, RivaTuner Statistics Server, Process Explorer, WizTree, Bulk Crap Uninstaller, CrystalDiskMark, Display Driver Uninstaller, Ditto, AutoHotkey, LocalSend, RustDesk, qBittorrent, Tailscale, Auto Dark Mode, Nilesoft Shell, Twinkle Tray, KeePassXC, VeraCrypt, Cryptomator, Burp Suite Community, HxD, simplewall, Portmaster.
- Taskbar progress bar during install / update batches.
- Tile right-click: **Copy winget id** and **Copy install command**.
- Summary dialog: **Copy report** button; Esc / Enter close it.
- **OPEN** button next to the download folder; search also matches category names, is debounced, and shows a "no apps match" message.
- Shortcuts: Ctrl+F, Esc (clear search), Ctrl+A, Ctrl+Enter.
- Clearer messages for more winget exit codes (package not found, app is running, disk full, blocked by policy, downgrade, ...).

### Changed
- **Faster startup:** winget is located in the background, so the window appears immediately instead of after `winget --version` returned (up to ~20 s per candidate on a cold machine). Install waits for detection if you click it early.
- Bitwarden, Malwarebytes, Wireshark, O&O ShutUp10++, ProtonVPN, WireGuard and OpenVPN moved to **Security & Privacy**. Catalog `Id`s are unchanged, so existing packs still match.
- Version bumped to **1.4.0** (`CornDownloader.csproj`, `App.manifest`, README).

## [1.3.1] - 2026-11-13

Audit pass on top of 1.3.0: every catalog link and winget ID was checked against the live sources, and the bugs that turned up along the way were fixed. No new features.

### Fixed
- **Double-click on Install / Update** could start two batches at once: the "busy" flag was only set after waiting for the background scan.
- **`--check-update` without `--silent`** opened the main window instead of printing the result and exiting, and was refused with "already running" while the GUI was open.
- **Tile badges overlapped:** an installed app with an update drew "✔ Installed" and "⬆ update available" on top of each other.
- **Update summary had a dead Retry button** (only the install flow ever acted on it). A run where nothing succeeded or failed no longer says "All apps installed!".
- **Saved settings:** a launch where winget wasn't found permanently switched *prefer winget* off, and `--folder` / `--no-winget` leaked into `settings.json`.
- **Exit-code names:** 1641 and 3010 were swapped (1641 is "reboot initiated", 3010 is "reboot required"), so the log described them the wrong way round.
- **Resume could corrupt a download** when a "latest" link moved to a newer build between attempts. The partial file's ETag / Last-Modified is now stored (`.part.meta`) and sent as `If-Range`; without a validator the download restarts.
- **Dead links ran as installers:** a link that returns an HTML page is now rejected instead of being saved as `Setup.exe` and launched.
- **Ghost catalog links:** VLC (directory listing, direct link dropped — winget only), Slack (landing page → real installer redirect), K-Lite Codec Pack (404 → winget `CodecGuide.K-LiteCodecPack.Basic`).
- **Dead winget IDs** (checked against the live source with `winget show --exact`): Medal, Proton VPN, Bulk Rename Utility, Min, FreeTube, Streamlink Twitch GUI, itch, Rockstar Games Launcher, Mod Organizer 2 and PyPy moved to their current IDs; Xbox App, WhatsApp and Microsoft PC Manager now use their Microsoft Store product IDs. Catalog `Id`s are unchanged, so exported packs still match.
- **`--silent` with a bad argument popped an error dialog** when the bad argument came before `--silent` (parsing stopped before the flag was seen), hanging unattended scripts. Headless is now decided before parsing, and the dialog is also skipped whenever a console is attached.
- A missing or unreadable `--pack` file in headless mode exits with code 2 (bad arguments) instead of 1.
- Top bar: the upgrade button no longer sits under the update link on narrow windows.
- Tiles are marked installed as soon as an install succeeds instead of on the next launch.

### Changed
- Version bumped to **1.3.1** in `CornDownloader.csproj`, `App.manifest` and the README. The title bar, `--version`, the User-Agent, the update check and the installer file name all read it from the build.
- README: the app count is now 120, the catalog lists match the catalog, and it no longer claims every entry has a direct-URL fallback (only the entries that carry a direct URL do).

### Removed
- **Seven catalog apps that can no longer be installed** (no winget package, no working direct link): Overwolf, Valorant Tracker, CDBurnerXP, FileZilla, EqualizerAPO, DaVinci Resolve, Genshin Impact. The catalog is now 120 apps; packs that reference them import with a "not found in the catalog" notice.
- Dead code: `UpdateInfo.AssetUrl`.
- Leftovers: the `!bin/Release/**/*.exe` exception and unused patterns in `.gitignore`, the no-op `[UninstallDelete]` section in the installer script.

### Project structure
- File and folder names are now PascalCase in git as well as on disk (1.3.0 only renamed them on disk): the `CornDownloader/` folder, `MainForm.cs`, `Program.cs`, `DownloadManager.cs`, `AppCatalog.cs`, `App.manifest`, `CornDownloader.csproj`, `CornDownloader.sln`.

[1.4.0]: https://github.com/Corn-Systems/CornDownloader/compare/v1.3.1...v1.4.0
[1.3.1]: https://github.com/Corn-Systems/CornDownloader/compare/v1.3.0...v1.3.1
