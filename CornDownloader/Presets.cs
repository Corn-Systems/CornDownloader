using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CornDownloader
{
    // A one-click selection. Built-in presets and the user's own are both plain
    // SelectionPacks — the same .corn format Export/Import and --pack use — so a preset can
    // be shared, edited by hand or fed to headless mode unchanged.
    internal sealed class Preset
    {
        public string        Name        { get; init; }
        public string        Emoji       { get; init; }
        public string        Description { get; init; }
        public SelectionPack Pack        { get; init; }
        public string        FilePath    { get; init; }   // null for built-ins
        public bool          IsUser      => FilePath != null;
    }

    internal static class Presets
    {
        // Ids must match AppCatalog entry Ids; MainForm logs any that don't resolve.
        public static IReadOnlyList<Preset> BuiltIn { get; } = new[]
        {
            Make("Gaming PC", "🎮", "Launchers, voice chat, capture and performance overlays.",
                "Valve.Steam", "EpicGames.EpicGamesLauncher", "GOG.Galaxy", "Discord.Discord",
                "Playnite.Playnite", "Guru3D.Afterburner", "Guru3D.RTSS", "OBSProject.OBSStudio",
                "REALiX.HWiNFO", "Brave.Brave", "7zip.7zip", "VideoLAN.VLC"),

            Make("Dev Machine", "💻", "Editor, Git, runtimes, terminal and API tooling.",
                "Microsoft.VisualStudioCode", "Git.Git", "GitHub.cli", "GitHub.GitHubDesktop",
                "Microsoft.WindowsTerminal", "Microsoft.PowerShell", "JanDeDobbeleer.OhMyPosh",
                "OpenJS.NodeJS.LTS", "Python.Python.3.12", "Microsoft.DotNet.SDK.10",
                "Bruno.Bruno", "WinMerge.WinMerge", "Notepad++.Notepad++", "voidtools.Everything",
                "Microsoft.PowerToys", "7zip.7zip"),

            Make("Student", "🎓", "Notes, citations, flashcards, office docs and calls.",
                "Mozilla.Firefox", "ONLYOFFICE.DesktopEditors", "Obsidian.Obsidian", "DigitalScholar.Zotero",
                "Anki.Anki", "SumatraPDF.SumatraPDF", "Zoom.Zoom", "Discord.Discord",
                "Bitwarden.Bitwarden", "Spotify.Spotify", "VideoLAN.VLC", "7zip.7zip"),

            Make("Security Lab", "🔐", "Traffic analysis, web testing, hex editing and Sysinternals.",
                "WiresharkFoundation.Wireshark", "PortSwigger.BurpSuite", "MHNexus.HxD",
                "KeePassXCTeam.KeePassXC", "IDRIX.VeraCrypt", "PuTTY.PuTTY", "WinSCP.WinSCP",
                "Microsoft.Sysinternals.ProcessExplorer", "Microsoft.Sysinternals.Autoruns",
                "Python.Python.3.12", "Microsoft.VisualStudioCode", "TorProject.TorBrowser",
                "Microsoft.WSL", "7zip.7zip"),
        };

        private static Preset Make(string name, string emoji, string description, params string[] ids) => new Preset
        {
            Name        = name,
            Emoji       = emoji,
            Description = description,
            Pack        = new SelectionPack
            {
                Name        = name,
                Description = description,
                Apps        = ids.Select(id => new PackedApp { Id = id }).ToList()
            }
        };

        // User presets: every *.corn file in %AppData%\CornSystems\CornDownloader\presets.
        public static List<Preset> LoadUser()
        {
            var list = new List<Preset>();
            try
            {
                if (!Directory.Exists(AppPaths.PresetsDir)) return list;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                foreach (var file in Directory.GetFiles(AppPaths.PresetsDir, "*.corn").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var pack = JsonSerializer.Deserialize<SelectionPack>(File.ReadAllText(file), opts);
                        if (pack?.Apps == null || pack.Apps.Count == 0) continue;
                        list.Add(new Preset
                        {
                            Name        = string.IsNullOrWhiteSpace(pack.Name) ? Path.GetFileNameWithoutExtension(file) : pack.Name.Trim(),
                            Emoji       = "📦",
                            Description = string.IsNullOrWhiteSpace(pack.Description) ? "Your saved preset." : pack.Description,
                            Pack        = pack,
                            FilePath    = file
                        });
                    }
                    catch (Exception ex) { SessionLog.Write($"PRESET {Path.GetFileName(file)}", ex); }
                }
            }
            catch (Exception ex) { SessionLog.Write("PRESETS", ex); }
            return list;
        }

        public static string Save(string name, SelectionPack pack)
        {
            Directory.CreateDirectory(AppPaths.PresetsDir);
            string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (safe.Length == 0) safe = "preset";
            string path = Path.Combine(AppPaths.PresetsDir, safe + ".corn");
            pack.Name = name;
            File.WriteAllText(path, JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
    }
}
