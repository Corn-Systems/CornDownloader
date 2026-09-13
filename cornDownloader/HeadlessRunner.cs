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
                (done, total) => { },
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

        private static bool _consoleAttached;
        public static bool ConsoleAttached => _consoleAttached;

        // WinExe has no console. Attach to the parent (cmd/PowerShell) when launched from
        // one so output shows up; when launched from a script with no console, output
        // still goes to the session log.
        public static void AttachParentConsole()
        {
            try { _consoleAttached = AttachConsole(ATTACH_PARENT_PROCESS); }
            catch { _consoleAttached = false; }
        }

        public static void Out(string line)
        {
            SessionLog.Write("[CLI] " + line);
            if (!_consoleAttached) return;
            try { Console.WriteLine(line); } catch { }
        }

        private const int ATTACH_PARENT_PROCESS = -1;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int dwProcessId);
    }
}
