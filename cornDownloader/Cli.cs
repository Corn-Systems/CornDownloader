using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

    // `--silent` mode: same DownloadManager as the GUI, console + session-log output only.
    internal static class HeadlessRunner
    {
        public const int ExitOk = 0, ExitSomeFailed = 1, ExitBadArgs = 2, ExitAlreadyRunning = 3, ExitNothing = 4;

        public static int Run(CliOptions opts)
        {
            SessionLog.SessionHeader("headless");

            try { return RunAsync(opts).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                SessionLog.Write("HEADLESS", ex);
                Out($"fatal: {ex.Message}");
                return ExitSomeFailed;
            }
        }

        private static async Task<int> RunAsync(CliOptions opts)
        {
            if (opts.CheckUpdate)
            {
                var u = await UpdateChecker.CheckAsync();
                Out(u == null ? "update check failed (offline?)"
                  : u.IsNewer ? $"update available: {u.LatestTag} (current {AppInfo.Version}) → {u.ReleaseUrl}"
                  : $"up to date ({AppInfo.Version})");
                if (!opts.HasSelection) return ExitOk;
            }

            var selection = ResolveSelection(opts, out var missing);
            foreach (var m in missing) Out($"warn: '{m}' not in catalog — skipped");
            if (selection.Count == 0) { Out("nothing to install."); return ExitNothing; }

            var dm = new DownloadManager();
            if (opts.Scope != null) dm.WingetScope = opts.Scope;
            bool preferWinget = !opts.NoWinget;

            string folder = string.IsNullOrWhiteSpace(opts.Folder) ? AppPaths.DefaultDownloadFolder : opts.Folder;
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { Out($"cannot create folder '{folder}': {ex.Message}"); return ExitBadArgs; }

            Out($"{AppInfo.Name} {AppInfo.Version} — {selection.Count} app(s), winget={(dm.WingetAvailable ? "yes" : "no")}, elevated={SystemInfo.IsElevated}");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); Out("cancelling..."); };

            if (dm.WingetAvailable && preferWinget) await dm.RefreshSourcesAsync(cts.Token);

            var results = await dm.InstallAllAsync(selection, folder, preferWinget,
                (app, status, msg) =>
                {
                    if (status is InstallStatus.Success or InstallStatus.Failed or InstallStatus.Skipped)
                        Out($"[{StatusTag(status)}] {app.Name}: {msg}");
                    else if (!msg.Contains('%'))
                        Out($"[ .. ] {app.Name}: {msg}");
                    SessionLog.Write($"[{app.Name}] {msg}");
                },
                null,
                cts.Token);

            int ok = results.Count(r => r.Status == InstallStatus.Success);
            int fail = results.Count(r => r.Status == InstallStatus.Failed);
            int skip = results.Count(r => r.Status == InstallStatus.Skipped);
            bool reboot = results.Any(r => r.RebootRequired);

            Out($"done — {ok} succeeded, {fail} failed, {skip} skipped{(reboot ? " — RESTART REQUIRED" : "")}");
            Out($"log: {SessionLog.CurrentFile}");
            return fail == 0 ? ExitOk : ExitSomeFailed;
        }

        public static List<AppEntry> ResolveSelection(CliOptions opts, out List<string> missing)
        {
            var wanted  = new List<(string id, string name, string pin)>();
            missing     = new List<string>();

            if (opts.PackPath != null)
            {
                var pack = JsonSerializer.Deserialize<SelectionPack>(File.ReadAllText(opts.PackPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                foreach (var p in pack?.Apps ?? new List<PackedApp>())
                    wanted.Add((p.Id, p.Name, p.PinnedVersion));
            }
            foreach (var id in opts.AppIds) wanted.Add((id, null, null));

            var result = new List<AppEntry>();
            foreach (var (id, name, pin) in wanted)
            {
                var entry = AppCatalog.Find(id, name);
                if (entry == null) { missing.Add(id ?? name ?? "?"); continue; }
                if (result.Contains(entry)) continue;
                entry.PinnedVersion = pin;
                result.Add(entry);
            }
            return result;
        }

        private static string StatusTag(InstallStatus s) => s switch
        {
            InstallStatus.Success => " OK ",
            InstallStatus.Failed  => "FAIL",
            InstallStatus.Skipped => "SKIP",
            _                     => " .. "
        };

        public static bool ConsoleAttached { get; private set; }

        // WinExe has no console. Attach to the parent (cmd/PowerShell) when launched from
        // one so output shows up; when launched from a script with no console, output
        // still goes to the session log.
        public static void AttachParentConsole()
        {
            try { ConsoleAttached = AttachConsole(ATTACH_PARENT_PROCESS); }
            catch (Exception ex) { SessionLog.Write("CONSOLE", ex); }
        }

        public static void Out(string line)
        {
            SessionLog.Write("[CLI] " + line);
            if (!ConsoleAttached) return;
            try { Console.WriteLine(line); } catch (Exception) { /* console went away (closed pipe); the line is already in the session log */ }
        }

        private const int ATTACH_PARENT_PROCESS = -1;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int dwProcessId);
    }
}
