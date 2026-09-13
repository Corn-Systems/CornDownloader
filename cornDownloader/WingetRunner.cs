using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CornDownloader
{
    // winget / msiexec exit codes we make decisions on. Anything not listed is "failed".
    internal static class ExitCodes
    {
        public const int Success = 0;

        // winget (APPINSTALLER_CLI_ERROR_*) — HRESULTs, so they come back negative as int.
        public const int WingetUpdateNotApplicable     = unchecked((int)0x8A15002B);  // "No applicable update found"
        public const int WingetPackageAlreadyInstalled = unchecked((int)0x8A150061);  // "Package already installed"
        public const int WingetInstallInProgress       = unchecked((int)0x8A150102);  // installer reported another install running
        public const int WingetInstallAlreadyInstalled = unchecked((int)0x8A15010D);  // installer reported already installed
        public const int WingetInstallRebootToFinish   = unchecked((int)0x8A150109);
        public const int WingetInstallRebootToInstall  = unchecked((int)0x8A15010A);
        public const int WingetInstallRebootInitiated  = unchecked((int)0x8A15010B);
        public const int WingetInstallCancelledByUser  = unchecked((int)0x8A15010C);

        // Windows Installer / most EXE installers
        public const int MsiRebootRequired  = 1641;
        public const int MsiRebootInitiated = 3010;
        public const int MsiAnotherInstallInProgress = 1618;
        public const int UacDeclined        = 1223;   // ERROR_CANCELLED from ShellExecute("runas")

        public static bool IsWingetSuccess(int code) =>
            code == Success || code == WingetUpdateNotApplicable || code == WingetPackageAlreadyInstalled ||
            code == WingetInstallAlreadyInstalled || code == WingetInstallRebootToFinish || code == WingetInstallRebootInitiated;

        public static bool IsInstallerSuccess(int code) =>
            code == Success || code == MsiRebootRequired || code == MsiRebootInitiated;

        public static bool NeedsReboot(int code) =>
            code == MsiRebootRequired || code == MsiRebootInitiated ||
            code == WingetInstallRebootToFinish || code == WingetInstallRebootToInstall || code == WingetInstallRebootInitiated;

        public static string Describe(int code) => code switch
        {
            Success                        => "success",
            WingetUpdateNotApplicable      => "no applicable update (already current)",
            WingetPackageAlreadyInstalled  => "package already installed",
            WingetInstallInProgress        => "another install is already in progress",
            WingetInstallAlreadyInstalled  => "installer reports already installed",
            WingetInstallRebootToFinish    => "success — reboot required to finish",
            WingetInstallRebootToInstall   => "reboot required before this can install",
            WingetInstallRebootInitiated   => "success — reboot initiated",
            WingetInstallCancelledByUser   => "cancelled by user (UAC declined?)",
            MsiRebootRequired              => "success — reboot required",
            MsiRebootInitiated             => "success — reboot initiated",
            MsiAnotherInstallInProgress    => "another Windows Installer operation is in progress",
            UacDeclined                    => "UAC prompt was declined",
            _                              => $"exit code {code} (0x{code:X8})"
        };
    }

    internal sealed class WingetResult
    {
        public int    ExitCode { get; init; }
        public bool   TimedOut { get; init; }
        public bool   Cancelled { get; init; }
        public string Output   { get; init; } = "";
        public bool   Succeeded => !TimedOut && !Cancelled && ExitCodes.IsWingetSuccess(ExitCode);
    }

    // Single place that knows how to find and run winget.exe.
    //
    //  • Resolve(): prefers the per-user App Execution Alias, then the packaged exe under
    //    Program Files\WindowsApps, then bare "winget" on PATH. The alias is missing when the
    //    app is launched elevated or from a different account (installer post-run, RunAs,
    //    scheduled tasks), which used to make the app think winget wasn't installed.
    //  • RunAsync(): one implementation of "start, wire stdout/stderr, wait with timeout or
    //    cancellation, kill on either, dispose". Replaces four copy-pasted blocks.
    //  • A process-wide gate serialises every winget invocation. winget holds an install
    //    lock and MSI-based packages share the Windows Installer mutex, so parallel winget
    //    calls fail with "another install in progress" (MSI 1618 / winget 0x8A150102).
    internal static class WingetRunner
    {
        public static readonly TimeSpan ScanTimeout    = TimeSpan.FromSeconds(90);   // export / upgrade listing on a fresh machine can be slow
        public static readonly TimeSpan QuickTimeout   = TimeSpan.FromSeconds(20);   // --version, source update
        public static readonly TimeSpan VersionsTimeout = TimeSpan.FromSeconds(30);

        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private static string _exe;

        public static string ExePath => _exe;

        public static bool Resolve()
        {
            foreach (var candidate in Candidates())
            {
                try
                {
                    var psi = new ProcessStartInfo(candidate, "--version")
                    {
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    if (!p.WaitForExit((int)QuickTimeout.TotalMilliseconds)) { try { p.Kill(true); } catch { } continue; }
                    if (p.ExitCode == 0)
                    {
                        _exe = candidate;
                        SessionLog.Write($"[WINGET] resolved: {candidate}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    SessionLog.Write($"[WINGET] candidate '{candidate}' failed: {ex.Message}");
                }
            }
            SessionLog.Write("[WINGET] not found — direct-URL installs only");
            return false;
        }

        private static IEnumerable<string> Candidates()
        {
            string alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(alias)) yield return alias;

            string packaged = null;
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                if (Directory.Exists(root))
                    packaged = Directory.GetDirectories(root, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .Select(d => Path.Combine(d, "winget.exe"))
                        .FirstOrDefault(File.Exists);
            }
            catch { /* WindowsApps is ACL'd; ignore */ }
            if (packaged != null) yield return packaged;

            yield return "winget";
        }

        public static async Task<WingetResult> RunAsync(
            string args, TimeSpan timeout, Action<string> onLine = null, CancellationToken ct = default)
        {
            if (_exe == null) return new WingetResult { ExitCode = -1, Output = "winget not available" };

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await RunUngatedAsync(args, timeout, onLine, ct).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private static async Task<WingetResult> RunUngatedAsync(
            string args, TimeSpan timeout, Action<string> onLine, CancellationToken ct)
        {
            var output = new StringBuilder();
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Process proc = null;

            try
            {
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName               = _exe,
                        Arguments              = args + " --disable-interactivity",
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding  = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (output) output.AppendLine(e.Data);
                    onLine?.Invoke(e.Data);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (output) output.AppendLine("[ERR] " + e.Data);
                    onLine?.Invoke("[ERR] " + e.Data);
                };
                proc.Exited += (s, e) => exited.TrySetResult(true);

                SessionLog.Write($"[WINGET] > winget {args}");
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var timeoutTask = Task.Delay(timeout, ct);
                var finished    = await Task.WhenAny(exited.Task, timeoutTask).ConfigureAwait(false);

                if (finished != exited.Task)
                {
                    try { if (!proc.HasExited) proc.Kill(true); } catch (Exception ex) { SessionLog.Write("WINGET-KILL", ex); }
                    bool cancelled = ct.IsCancellationRequested;
                    SessionLog.Write(cancelled ? "[WINGET] cancelled" : $"[WINGET] timed out after {timeout.TotalSeconds:0}s");
                    return new WingetResult { ExitCode = -1, TimedOut = !cancelled, Cancelled = cancelled, Output = Snapshot() };
                }

                proc.WaitForExit(); // flush async readers
                int code = proc.ExitCode;
                SessionLog.Write($"[WINGET] < {ExitCodes.Describe(code)}");
                return new WingetResult { ExitCode = code, Output = Snapshot() };
            }
            catch (OperationCanceledException)
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
                return new WingetResult { ExitCode = -1, Cancelled = true, Output = Snapshot() };
            }
            catch (Exception ex)
            {
                SessionLog.Write("WINGET", ex);
                return new WingetResult { ExitCode = -1, Output = ex.Message };
            }
            finally
            {
                try { proc?.Dispose(); } catch { }
            }

            string Snapshot() { lock (output) return output.ToString(); }
        }
    }
}
