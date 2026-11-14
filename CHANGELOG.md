# Changelog

All notable changes to Corn Downloader from **1.3.0** to **1.3.1**. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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

[1.3.1]: https://github.com/Corn-Systems/CornDownloader/compare/v1.3.0...v1.3.1
