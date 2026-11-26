# 🌽 Corn Downloader v1.4.0

The biggest update since launch: **one-click presets**, **uninstall**, **status filters**, a **dark window** that finally matches the rest of the app, proper **multi-monitor scaling**, and **57 new apps** (177 total).

## ⬇ Downloads

| File | What it is |
|---|---|
| **`CornDownloader-Setup-1.4.0.exe`** | Installer (recommended). Installs to `C:\Program Files\Corn Downloader`, adds Start Menu + Desktop shortcuts, uninstalls cleanly from *Apps & features*. **Upgrades 1.3.x in place.** |
| **`CornDownloader-1.4.0-portable.exe`** | Single self-contained exe. No install and no .NET runtime needed. Run it from anywhere. |

Windows 10 1809+ or Windows 11, 64-bit. [winget](https://aka.ms/getwinget) recommended; the direct-download fallback works without it.

---

## ✨ New

### 🎯 Presets
A new strip above the app grid gives you one-click setups:

- 🎮 **Gaming PC**: Steam, Epic, GOG, Discord, Playnite, MSI Afterburner + RivaTuner, OBS, HWiNFO…
- 💻 **Dev Machine**: VS Code, Git, GitHub CLI, Terminal, PowerShell 7, Node, Python, .NET 10 SDK, Bruno, PowerToys…
- 🎓 **Student**: Firefox, ONLYOFFICE, Obsidian, Zotero, Anki, SumatraPDF, Zoom, Bitwarden…
- 🔐 **Security Lab**: Wireshark, Burp Suite, HxD, KeePassXC, VeraCrypt, PuTTY, WinSCP, Sysinternals, WSL…

Hover a preset to see exactly what it picks. **＋ SAVE** turns your current selection into your own preset button. Presets are ordinary `.corn` packs (in `%AppData%\CornSystems\CornDownloader\presets`), so you can share them, edit them by hand, or run them headless with `--pack`.

### 🧹 Uninstall
Right-click any installed app → **Uninstall...** It confirms first, runs winget's silent uninstall, then flips the tile back to installable.

### 🔎 Show filters
**ALL · ✔ INSTALLED · ⬆ UPDATES · ○ NOT INSTALLED · ☑ SELECTED**, with live counts. They work together with the category sidebar and search. Picking a preset or importing a pack jumps to **SELECTED** so you can review the picks before installing.

### 🌑 Dark window chrome
- Dark title bar on every window (Windows 10 1809+). On Windows 11 it's coloured to blend into the top bar.
- Thin, themed scrollbars replace the bright native ones.

### 🖥 Proper multi-monitor scaling
Drag the window between a 100% laptop screen and a 150% monitor (or change display scaling) and everything rescales: layout, text, tiles, dialogs and the taskbar badge. It also opens at the right size on a secondary monitor and remembers its size correctly across different scaling.

### 📦 57 new apps, plus a 🛡️ Security & Privacy category
- **Browsers:** Vivaldi, Floorp
- **Dev Tools:** Visual Studio 2026, .NET 10 SDK, GitHub CLI, Zed, Sublime Text 4, Bruno, DBeaver, WinMerge, Oh My Posh, PuTTY, WinSCP, CMake, Java 21 (Temurin), Bun
- **Media:** Kdenlive, Shotcut, Krita, Inkscape, Paint.NET, Kodi, yt-dlp
- **Productivity:** Joplin, Logseq, Zotero, Anki, SumatraPDF, ONLYOFFICE, Signal, Telegram, Flow Launcher, Espanso
- **Gaming:** RetroArch, Moonlight, RivaTuner Statistics Server
- **Utilities:** Process Explorer, WizTree, Bulk Crap Uninstaller, CrystalDiskMark, Display Driver Uninstaller, Ditto, AutoHotkey, LocalSend, RustDesk, qBittorrent, Tailscale
- **Customization:** Auto Dark Mode, Nilesoft Shell, Twinkle Tray
- **Security & Privacy (new):** KeePassXC, VeraCrypt, Cryptomator, Burp Suite Community, HxD, simplewall, Portmaster. Bitwarden, Malwarebytes, Wireshark, O&O ShutUp10++, ProtonVPN, WireGuard and OpenVPN moved here too.

### Smaller additions
- Taskbar progress bar while a batch installs
- Right-click a tile → **Copy winget id** / **Copy install command**
- Summary window: **Copy report** button; Enter / Esc close it
- **OPEN** button next to the download folder
- Search also matches category names and shows a message when nothing matches
- Shortcuts: **Ctrl+F** search · **Esc** clear · **Ctrl+A** select all in view · **Ctrl+Enter** install
- Clearer error messages: "the app is running — close it and retry", "not enough disk space", "blocked by group policy", and more

---

## ⚡ Faster startup
The window now opens immediately. Finding winget used to happen *before* the window appeared, which could take many seconds on a cold PC. It now runs in the background while the badge shows "detecting winget...". If you hit Install early, it simply waits for detection to finish.

---

## 🐛 Fixes
- The per-app **"log ▸" drawer always opened blank**. It now shows the app's output.
- Cancelled or skipped apps were stuck on **"⏳ Installing..."** until restart. They now show "⊘ Skipped".
- Sidebar showed **"MEDIA  ENTERTAIN."** with an underlined E, and "O&O ShutUp10++" lost its "&".
- The sidebar was **cut off** at small window sizes or high scaling (Export/Import disappeared). It scrolls now.
- Summary window: the **Close button could end up off-screen**, long errors overlapped app names, and skipped apps weren't listed.
- "**Another install is in progress**" errors (often Windows Update) failed immediately. winget installs now wait 30 s and retry once.
- Per-app MSI silent switches were ignored.
- The update checker could announce a **phantom update** for the version you already had.
- Importing a pack kept stale version pins, and counted already-installed apps as "not found".
- "Update available" badges didn't clear after updating an app through Install.
- Cancel could be clicked repeatedly with no feedback; Install/Update were briefly clickable during startup scans.
- Window size was saved in raw pixels, so it opened too small or too large after a scaling change.
- Long status messages ran underneath the buttons; tile hover flickered; the window icon was blurry at high scaling.

---

## ⬆ Upgrading from 1.3.x
- Run the new installer over the old one. Settings, logs and saved packs are kept.
- Existing `.corn` packs still import, and packs you export from 1.4.0 still open in 1.3.x.
- Your saved window size is converted once to the new scaling-independent format.

## 🔒 SHA-256

```
9f9ff47791683b8035c87b91a2853c3224b861a5ac480a18c2ee81e366569b35  CornDownloader-Setup-1.4.0.exe
97782d0e0ce78b65d07db52f394724c9ba7f8e9469a869b9ae2a14d5c404c1ad  CornDownloader-1.4.0-portable.exe
```

**Full changelog:** https://github.com/Corn-Systems/CornDownloader/compare/v1.3.1...v1.4.0
