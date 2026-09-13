using System;
using System.Collections.Generic;
using System.Linq;

namespace CornDownloader
{
    // Command line:
    //   CornDownloader.exe                         normal GUI
    //   CornDownloader.exe --pack my.corn          GUI with the pack pre-selected
    //   CornDownloader.exe --pack my.corn --silent headless install, no windows
    //   CornDownloader.exe --apps Git.Git,VideoLAN.VLC --silent
    //   options: --folder <dir>  --no-winget  --scope user|machine  --check-update  --version  --help
    internal sealed class CliOptions
    {
        public string PackPath     { get; private set; }
        public List<string> AppIds { get; } = new();
        public bool   Silent       { get; private set; }
        public string Folder       { get; private set; }
        public bool   NoWinget     { get; private set; }
        public string Scope        { get; private set; }
        public bool   CheckUpdate  { get; private set; }
        public bool   ShowVersion  { get; private set; }
        public bool   ShowHelp     { get; private set; }
        public string Error        { get; private set; }

        public bool Headless => Silent;
        public bool HasSelection => PackPath != null || AppIds.Count > 0;

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next(string flag)
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) { o.Error = $"{flag} requires a value."; return null; }
                    return args[++i];
                }

                switch (a.ToLowerInvariant())
                {
                    case "--pack":         o.PackPath = Next(a); break;
                    case "--apps":
                        var list = Next(a);
                        if (list != null) o.AppIds.AddRange(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case "--silent":
                    case "--headless":     o.Silent = true; break;
                    case "--folder":       o.Folder = Next(a); break;
                    case "--no-winget":    o.NoWinget = true; break;
                    case "--scope":
                        o.Scope = Next(a)?.ToLowerInvariant();
                        if (o.Scope != null && o.Scope != "user" && o.Scope != "machine") o.Error = "--scope must be 'user' or 'machine'.";
                        break;
                    case "--check-update": o.CheckUpdate = true; break;
                    case "--version":
                    case "-v":             o.ShowVersion = true; break;
                    case "--help":
                    case "-h":
                    case "/?":             o.ShowHelp = true; break;
                    default:               o.Error = $"Unknown argument: {a}"; break;
                }
                if (o.Error != null) break;
            }

            if (o.Error == null && o.Silent && !o.HasSelection && !o.CheckUpdate)
                o.Error = "--silent needs --pack <file> or --apps <id,id,...>.";
            return o;
        }

        public static string HelpText => string.Join(Environment.NewLine, new[]
        {
            $"{AppInfo.Name} {AppInfo.Version} — {AppInfo.Publisher}",
            "",
            "  CornDownloader.exe [options]",
            "",
            "  --pack <file>        Load a .corn/.json selection pack (exported from the app)",
            "  --apps <id,id,...>   Select catalog entries by Id (e.g. Git.Git,VideoLAN.VLC)",
            "  --silent             Headless: install the selection and exit, no windows",
            "  --folder <dir>       Download folder for direct-URL installers",
            "  --no-winget          Force direct-URL installs even if winget is present",
            "  --scope user|machine Pass --scope to winget (default: let winget decide)",
            "  --check-update       Print whether a newer release exists and exit",
            "  --version            Print the version and exit",
            "  --help               This text",
            "",
            "  Exit codes: 0 all succeeded · 1 one or more failed · 2 bad arguments · 3 already running · 4 nothing to install",
        });
    }
}
