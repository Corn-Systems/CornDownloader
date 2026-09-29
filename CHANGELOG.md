# Changelog

All notable changes to Corn Downloader since **1.2.0**. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

Cleanup pass on top of 1.3.0 — bug fixes, dead-code removal and a smaller file layout. No new features.

### Fixed
- **Cancelling while queued:** cancelling an install while an app was waiting behind another winget call threw out of the whole batch and left the UI stuck on "installing". It now returns a normal cancelled result.
- **One failing app no longer sinks the batch:** an unexpected exception during one app's install used to discard every other result. It is now logged and reported as a failure for that app only.
- **UI no longer locks after an error:** the Install and Update click handlers reset the buttons, cancel button and token if anything throws.
- **Resume after a stale `.part` file:** a server `416 Range Not Satisfiable` response failed every retry. The partial file is now discarded and the download restarts.
- **Version picker:** reloading the version list could throw on an index of -1 and wipe a pinned version.
- **Log panel:** the layout code moved **📁 OPEN LOGS** on top of **✗ CLOSE**. Only the close button is repositioned now.
- **Leaks:** category headers were leaked on every search keystroke, the single-instance activation handle was never disposed, and a winget timeout timer kept running after a normal exit.
- Installed-app scan no longer adds an empty package id when winget reports a package without one.
- SHA-256 mismatch message no longer throws if a catalog hash is shorter than 12 characters.

### Changed
- **ALL / NONE** sidebar buttons now act on the tiles in the current category/search view instead of every tile (**✗ CLEAR** still clears everything).
- Empty `catch { }` blocks now log through `SessionLog` (the crash handler, console output and logger itself keep a commented bare catch).
- Search is case-insensitive via `StringComparison.OrdinalIgnoreCase` instead of lower-casing every field.
- Percentage parsing for progress messages is a single regex.
- Headless mode passes no overall-progress callback instead of a no-op lambda.

### Removed
- Dead code: `RetryPasses`, `WingetRunner.ExePath`, `AppTile._hasUpdate`, `MainForm.RescalePanels`, `MainForm.FilterApps`, the unused `_browseBtn` field.
- 222 redundant `DirectUrl = null` / `FileName = null` initialisers in the catalog.

### Project structure
- Files merged (20 → 15 `.cs` files): `AppPaths` → `AppInfo.cs`; `SelectionPack` → `AppSettings.cs`; `AppIconBuilder` + `TaskbarBadge` → `AppIcons.cs`; `CliOptions` + `HeadlessRunner` → `Cli.cs`; `SectionHeader` → `AppTile.cs`.
- All file names are now PascalCase: `MainForm.cs`, `Program.cs`, `DownloadManager.cs`, `AppCatalog.cs`, `App.manifest`, `CornDownloader.csproj`, `CornDownloader.sln`.
- README project-layout section updated to match; this changelog added.

## [1.3.0]

Development between 2026-08-28 and 2026-09-12 (`v1.3.0` is not tagged yet; the version is set in `CornDownloader.csproj`).

### Added
- **Installer:** Inno Setup script (`CornDownloader.iss`) producing `CornDownloader-Setup-<version>.exe` — per-machine install to `Program Files`, Start Menu + Desktop shortcuts, *Apps & features* uninstall entry.
- **Self-contained single-file publish** (win-x64, compressed): no .NET runtime needed. The app icon (`Assets\CornDownloader.ico`) is embedded and also used for the window and taskbar.
- **Headless / CLI mode:** `--silent`/`--headless`, `--pack <file>`, `--apps <id,id,…>`, `--folder`, `--no-winget`, `--scope user|machine`, `--check-update`, `--version`, `--help`. Exit codes: `0` ok · `1` some failed · `2` bad arguments · `3` already running · `4` nothing to install. Output goes to the parent console when there is one, and always to the session log.
- **Update check:** on launch (and via `--check-update`) the app queries GitHub Releases and shows an **⬆ vX.Y.Z available** link in the top bar. Disable with `"CheckForUpdates": false` in `settings.json`.
- **Single instance:** a second launch focuses the running window (or exits with code 3 in headless mode) instead of racing it for `settings.json` and winget.
- **Resumable downloads:** direct downloads stream to a `.part` file and resume with an HTTP `Range` request; a connection that delivers no bytes for 60 s fails fast instead of hanging.
- **Checksum verification:** catalog entries can pin a `Sha256`; the file is verified before the installer runs and deleted on mismatch.
- **Per-app silent arguments:** catalog entries can override the default `/S /silent /quiet /passive /norestart` flags (`SilentArgs`).
- **winget scope:** `WingetScope` setting / `--scope` flag (`auto`, `user`, `machine`).
- **Persistent session log:** daily log at `%AppData%\CornSystems\CornDownloader\logs\yyyy-MM-dd.log` with 14-day retention; the in-app log panel tees into it, and **📁 OPEN LOGS** opens the folder.
- **Crash handler:** unhandled UI-thread and background exceptions are written to `crash.log` and shown in a dialog instead of killing the app.
- **Catalog self-check:** startup validation flags missing names/descriptions/categories, duplicate ids, duplicate download file names and entries with no install method.
- **Winget upgrade detection** matches truncated package ids (winget shortens long ids with `…`) by unique prefix.
- **Manifest:** `longPathAware`, plus a documented `asInvoker` execution level (elevating the whole app would hide winget's per-user alias).
- Shared building blocks split out of the old monolithic files: `Theme` (one palette), `Dpi` (shared PerMonitorV2 helper), `AppInfo`, `AppPaths`, `SessionLog`, `WingetRunner`, `UpdateChecker`, `SummaryForm`, `AppTile`, `SectionHeader`, `SelectionPack`, `TaskbarBadge`, `AppIconBuilder`.
- New catalog apps: **Genshin Impact**, **Logitech G HUB**, **NVIDIA App** (127 apps total, up from 125).

### Changed
- **Rebrand: Corn Studios → Corn Systems** — company/metadata, README, repository links (`Corn-Systems/CornDownloader`), manifest identity, and the settings folder moved from `%AppData%\CornStudios\` to `%AppData%\CornSystems\`. Existing settings are migrated automatically on first run.
- **winget discovery:** the app tries the per-user App Execution Alias, then the packaged exe under `Program Files\WindowsApps`, then `winget` on `PATH`, so it still finds winget when launched elevated or from another account.
- **Installs are safer in parallel:** up to 3 downloads still run at once, but every winget call and every installer launch is now serialised (winget holds an install lock; MSI packages share the Windows Installer mutex), avoiding "another install in progress" failures and stacked UAC prompts. All winget calls run with `--disable-interactivity` and share one timeout/cancel/kill implementation.
- **winget exit codes** are interpreted explicitly (already installed, no applicable update, reboot required/initiated, cancelled) and a **restart required** notice appears in the summary.
- Settings are written atomically (temp file, then swap) so a crash mid-write can't corrupt `settings.json`.
- `.csproj` cleaned up: single `Version` source of truth, company/copyright/description metadata, win-x64 self-contained publish defaults.
- README rewritten for 1.3.0 (install options, headless mode, catalog list, project layout).
- Failed direct installs keep the downloaded file for inspection, and cancelled downloads keep the `.part` for resume.

### Fixed
- A batch of random crash bugs (background scan results arriving after the window closed, disposed-control access, async-void handler exceptions).
- Dead "ghost" catalog links and stale version titles.
- Drifting `SUCCESS` colour across forms (palette now lives in one place).

### Removed
- **Process Hacker** (replaced by its successor **System Informer**) and **Nexus Mod Manager** from the catalog.
- Committed `bin/Release` build output; `.gitignore` now covers build folders.

[Unreleased]: https://github.com/Corn-Systems/CornDownloader/compare/v1.2.0...HEAD
