using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CornDownloader
{
    public enum InstallStatus
    {
        Pending,
        Downloading,
        Installing,
        Success,
        Failed,
        Skipped
    }

    public class InstallResult
    {
        public AppEntry App { get; set; }
        public InstallStatus Status { get; set; }
        public string Message { get; set; }
        public bool RebootRequired { get; set; }
    }

    public class DownloadManager
    {
        public bool WingetAvailable { get; private set; }

        // "auto" | "user" | "machine" — appended as --scope when not auto.
        public string WingetScope { get; set; } = "auto";

        private const int MaxParallelDownloads = 3;
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);   // no bytes for this long = dead connection
        private const int    RetryPasses = 2;

        private static readonly HttpClient _http;

        // Elevation prompts are ugly three-at-a-time; run the actual installer launches
        // one after another while downloads stay parallel.
        private static readonly SemaphoreSlim _installerGate = new SemaphoreSlim(1, 1);

        static DownloadManager()
        {
            _http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.None
            })
            {
                Timeout = Timeout.InfiniteTimeSpan   // we manage stall detection ourselves
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        }

        public DownloadManager()
        {
            WingetAvailable = WingetRunner.Resolve();
        }

        private string ScopeArg => WingetScope is "user" or "machine" ? $" --scope {WingetScope}" : "";

        // ── winget: metadata ────────────────────────────────────────────────

        public async Task RefreshSourcesAsync(CancellationToken ct = default)
        {
            if (!WingetAvailable) return;
            var r = await WingetRunner.RunAsync("source update", WingetRunner.QuickTimeout, null, ct);
            if (!r.Succeeded && !r.Cancelled)
                SessionLog.Write($"[WINGET] source update did not complete cleanly ({ExitCodes.Describe(r.ExitCode)})");
        }

        // 'winget export' → JSON of installed packages that exist in a source. Far more
        // reliable than parsing the 'winget list' table.
        public async Task<HashSet<string>> GetAllInstalledIdsAsync(CancellationToken ct = default)
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!WingetAvailable) return installed;

            string tempFile = Path.Combine(Path.GetTempPath(), $"corndownloader_{Guid.NewGuid():N}.json");
            try
            {
                var r = await WingetRunner.RunAsync(
                    $"export -o \"{tempFile}\" --accept-source-agreements --include-versions",
                    WingetRunner.ScanTimeout, null, ct);

                if (r.TimedOut) SessionLog.Write("[SCAN] installed scan timed out — tiles may not show installed state");
                if (!File.Exists(tempFile)) return installed;

                using var doc = JsonDocument.Parse(File.ReadAllText(tempFile, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("Sources", out var sources))
                    foreach (var source in sources.EnumerateArray())
                    {
                        if (!source.TryGetProperty("Packages", out var packages)) continue;
                        foreach (var pkg in packages.EnumerateArray())
                            if (pkg.TryGetProperty("PackageIdentifier", out var idProp))
                                installed.Add(idProp.GetString() ?? "");
                    }
            }
            catch (JsonException ex) { SessionLog.Write("[SCAN] export JSON unreadable: " + ex.Message); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SessionLog.Write("SCAN", ex); }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (Exception ex) { SessionLog.Write("SCAN-CLEANUP", ex); }
            }
            return installed;
        }

        // Parses 'winget upgrade'. winget truncates long IDs with '…' to fit the (virtual)
        // console width, so besides exact token matches we also accept a truncated token
        // as a unique prefix of a catalog ID.
        public async Task<HashSet<string>> GetAvailableUpdatesAsync(CancellationToken ct = default)
        {
            var updatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!WingetAvailable) return updatable;

            try
            {
                var r = await WingetRunner.RunAsync(
                    "upgrade --accept-source-agreements --include-unknown",
                    WingetRunner.ScanTimeout, null, ct);
                if (r.TimedOut) { SessionLog.Write("[SCAN] upgrade scan timed out"); return updatable; }

                var catalogIds = AppCatalog.All.Where(a => !string.IsNullOrEmpty(a.WingetId))
                                               .Select(a => a.WingetId).ToList();

                var tokens = r.Output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var exact  = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);

                foreach (var id in catalogIds)
                    if (exact.Contains(id)) updatable.Add(id);

                foreach (var raw in tokens)
                {
                    string t = raw.TrimEnd('…', '.');
                    if (t.Length == raw.Length || t.Length < 4) continue;   // not truncated / too short to be safe
                    var matches = catalogIds.Where(id => id.StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 1) updatable.Add(matches[0]);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SessionLog.Write("SCAN-UPGRADE", ex); }

            return updatable;
        }

        public async Task<List<string>> GetAvailableVersionsAsync(AppEntry app, CancellationToken ct = default)
        {
            var versions = new List<string>();
            if (!WingetAvailable || string.IsNullOrEmpty(app.WingetId)) return versions;

            try
            {
                var r = await WingetRunner.RunAsync(
                    $"show --id {app.WingetId} --exact --versions --accept-source-agreements",
                    WingetRunner.VersionsTimeout, null, ct);

                bool pastHeader = false;
                foreach (var line in r.Output.Split('\n'))
                {
                    string t = line.Trim();
                    if (!pastHeader) { if (t.StartsWith("---")) pastHeader = true; continue; }
                    if (t.Length > 0 && (char.IsDigit(t[0]) || t[0] == 'v')) versions.Add(t);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SessionLog.Write("VERSIONS", ex); }

            return versions;
        }

        // ── winget: install / upgrade ───────────────────────────────────────

        public async Task<InstallResult> UpgradeAsync(AppEntry app, Action<string> onProgress, CancellationToken ct = default)
        {
            var result = new InstallResult { App = app, Status = InstallStatus.Installing };
            onProgress?.Invoke($"Upgrading {app.Name}...");

            var r = await WingetRunner.RunAsync(
                $"upgrade --id {app.WingetId} --exact --silent --accept-source-agreements --accept-package-agreements{ScopeArg}",
                Timeout.InfiniteTimeSpan, onProgress, ct);

            return Finish(result, r, $"{app.Name} upgraded successfully.");
        }

        private async Task<InstallResult> InstallViaWinget(AppEntry app, InstallResult result, Action<string> onProgress, CancellationToken ct)
        {
            result.Status = InstallStatus.Installing;
            onProgress?.Invoke($"Installing {app.Name} via winget...");

            var args = new StringBuilder($"install --id {app.WingetId} --exact");
            if (!string.IsNullOrEmpty(app.PinnedVersion)) args.Append($" --version \"{app.PinnedVersion}\"");
            args.Append(" --silent --accept-source-agreements --accept-package-agreements");
            if (app.ForceReinstall) args.Append(" --force");
            args.Append(ScopeArg);

            var r = await WingetRunner.RunAsync(args.ToString(), Timeout.InfiniteTimeSpan, onProgress, ct);
            return Finish(result, r, "Installed successfully via winget.");
        }

        private static InstallResult Finish(InstallResult result, WingetResult r, string successMsg)
        {
            if (r.Cancelled)          { result.Status = InstallStatus.Skipped; result.Message = "Cancelled."; }
            else if (r.Succeeded)     { result.Status = InstallStatus.Success; result.Message = successMsg; }
            else                      { result.Status = InstallStatus.Failed;  result.Message = $"winget: {ExitCodes.Describe(r.ExitCode)}"; }
            result.RebootRequired = ExitCodes.NeedsReboot(r.ExitCode);
            return result;
        }

        // ── Dispatch ────────────────────────────────────────────────────────

        public async Task<InstallResult> InstallAsync(
            AppEntry app, string downloadFolder, bool preferWinget, Action<string> onProgress, CancellationToken ct = default)
        {
            var result = new InstallResult { App = app, Status = InstallStatus.Pending };

            if (ct.IsCancellationRequested) { result.Status = InstallStatus.Skipped; result.Message = "Cancelled."; return result; }

            if (!string.IsNullOrEmpty(app.IsBundledWith))
            {
                result.Status  = InstallStatus.Skipped;
                result.Message = $"{app.Name} is included with {app.IsBundledWith} — install that instead.";
                return result;
            }

            bool useWinget = preferWinget && WingetAvailable && !string.IsNullOrEmpty(app.WingetId);
            bool hasDirect = !string.IsNullOrEmpty(app.DirectUrl) && !string.IsNullOrEmpty(app.FileName);

            if (!useWinget && !hasDirect)
            {
                result.Status  = InstallStatus.Failed;
                result.Message = "No installation method available.";
                return result;
            }

            return useWinget
                ? await InstallViaWinget(app, result, onProgress, ct)
                : await InstallViaDirectUrl(app, downloadFolder, result, onProgress, ct);
        }

        // ── Direct URL: download (resumable, verified) + run installer ──────

        private async Task<InstallResult> InstallViaDirectUrl(
            AppEntry app, string downloadFolder, InstallResult result, Action<string> onProgress, CancellationToken ct)
        {
            string destPath = Path.Combine(downloadFolder, app.FileName);
            string partPath = destPath + ".part";
            bool   installed = false;

            try
            {
                result.Status = InstallStatus.Downloading;
                await DownloadWithResumeAsync(app, destPath, partPath, onProgress, ct);

                if (!string.IsNullOrEmpty(app.Sha256))
                {
                    onProgress?.Invoke($"Verifying {app.Name}...");
                    string actual = await ComputeSha256Async(destPath, ct);
                    if (!string.Equals(actual, app.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(destPath); } catch { }
                        throw new InvalidDataException($"SHA-256 mismatch — expected {app.Sha256[..12]}…, got {actual[..12]}…. File deleted.");
                    }
                }

                onProgress?.Invoke($"Download complete. Launching installer for {app.Name}...");
                result.Status = InstallStatus.Installing;

                int exitCode = await RunInstallerAsync(app, destPath, ct);

                if (ct.IsCancellationRequested) { result.Status = InstallStatus.Skipped; result.Message = "Cancelled."; return result; }

                if (ExitCodes.IsInstallerSuccess(exitCode))
                {
                    installed = true;
                    result.Status  = InstallStatus.Success;
                    result.RebootRequired = ExitCodes.NeedsReboot(exitCode);
                    result.Message = result.RebootRequired ? "Installed — restart required." : "Installer ran successfully.";
                }
                else
                {
                    result.Status  = InstallStatus.Failed;
                    result.Message = $"Installer: {ExitCodes.Describe(exitCode)}. File kept at {destPath}";
                }
            }
            catch (OperationCanceledException)
            {
                result.Status  = InstallStatus.Skipped;
                result.Message = "Cancelled (partial download kept for resume).";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ExitCodes.UacDeclined)
            {
                result.Status  = InstallStatus.Failed;
                result.Message = "UAC prompt was declined — installer not run.";
            }
            catch (Exception ex)
            {
                SessionLog.Write($"DIRECT:{app.Name}", ex);
                result.Status  = InstallStatus.Failed;
                result.Message = ex.Message;
            }
            finally
            {
                // Only remove the installer after a successful run. Failed installs keep the
                // file so it can be inspected or run by hand; cancelled downloads keep .part.
                if (installed)
                    try { if (File.Exists(destPath)) File.Delete(destPath); }
                    catch (Exception ex) { SessionLog.Write("DIRECT-CLEANUP", ex); }
            }

            return result;
        }

        private static async Task DownloadWithResumeAsync(AppEntry app, string destPath, string partPath,
            Action<string> onProgress, CancellationToken ct)
        {
            long existing = 0;
            try { if (File.Exists(partPath)) existing = new FileInfo(partPath).Length; } catch { existing = 0; }

            using var req = new HttpRequestMessage(HttpMethod.Get, app.DirectUrl);
            if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            bool resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (existing > 0 && !resuming)
            {
                // Server ignored Range (or file changed) — start over.
                SessionLog.Write($"[DL] {app.Name}: server did not honour Range, restarting download");
                existing = 0;
                try { File.Delete(partPath); } catch { }
            }
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            if (total.HasValue) total += existing;   // ContentLength of a 206 is the remainder

            onProgress?.Invoke(resuming ? $"Resuming {app.Name} from {existing / 1024 / 1024} MB..." : $"Downloading {app.Name}...");

            using (var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var stream   = await response.Content.ReadAsStreamAsync(ct))
            using (var file     = new FileStream(partPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var  buffer  = new byte[1 << 16];
                long read    = existing;
                int  lastPct = -1;
                int  bytes;

                stallCts.CancelAfter(StallTimeout);
                while (true)
                {
                    try { bytes = await stream.ReadAsync(buffer, 0, buffer.Length, stallCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"Download stalled — no data for {StallTimeout.TotalSeconds:0}s. Retry to resume.");
                    }
                    if (bytes <= 0) break;
                    stallCts.CancelAfter(StallTimeout);   // reset the watchdog on every chunk

                    await file.WriteAsync(buffer, 0, bytes, ct);
                    read += bytes;
                    if (total > 0)
                    {
                        int pct = (int)(read * 100 / total.Value);
                        if (pct != lastPct) { lastPct = pct; onProgress?.Invoke($"Downloading {app.Name}: {pct}%"); }
                    }
                }
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(partPath, destPath);
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            using var sha  = SHA256.Create();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var hash = await sha.ComputeHashAsync(file, ct);
            return Convert.ToHexString(hash);
        }

        private static async Task<int> RunInstallerAsync(AppEntry app, string destPath, CancellationToken ct)
        {
            const string defaultExeSilentArgs = "/S /silent /quiet /passive /norestart";
            string ext  = Path.GetExtension(destPath).ToLowerInvariant();
            string args = !string.IsNullOrEmpty(app.SilentArgs) ? app.SilentArgs : defaultExeSilentArgs;

            var psi = ext == ".msi"
                ? new ProcessStartInfo("msiexec", $"/i \"{destPath}\" /passive /norestart") { UseShellExecute = true, Verb = "runas" }
                : new ProcessStartInfo(destPath, args) { UseShellExecute = true, Verb = "runas" };

            await _installerGate.WaitAsync(ct);
            try
            {
                SessionLog.Write($"[INSTALLER] > \"{psi.FileName}\" {psi.Arguments}");
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Process.Start returned null — the installer could not be launched.");
                using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
                await proc.WaitForExitAsync(CancellationToken.None);
                SessionLog.Write($"[INSTALLER] < {ExitCodes.Describe(proc.ExitCode)}");
                return proc.ExitCode;
            }
            finally { _installerGate.Release(); }
        }

        // ── Batch ───────────────────────────────────────────────────────────

        public async Task<List<InstallResult>> InstallAllAsync(
            List<AppEntry> apps, string downloadFolder, bool preferWinget,
            Action<AppEntry, InstallStatus, string> onAppProgress,
            Action<int, int> onOverallProgress,
            CancellationToken ct = default)
        {
            var results     = new List<InstallResult>();
            var resultsLock = new object();
            int total       = apps.Count;
            int done        = 0;

            // Downloads run in parallel; winget calls and installer launches are
            // serialised inside WingetRunner / RunInstallerAsync respectively.
            var semaphore = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads);

            var tasks = apps.Select(async app =>
            {
                await semaphore.WaitAsync();
                try
                {
                    InstallResult result;
                    if (ct.IsCancellationRequested)
                        result = new InstallResult { App = app, Status = InstallStatus.Skipped, Message = "Cancelled." };
                    else
                    {
                        onAppProgress?.Invoke(app, InstallStatus.Installing, $"Starting {app.Name}...");
                        result = await InstallAsync(app, downloadFolder, preferWinget,
                            msg =>
                            {
                                var st = msg.StartsWith("Downloading") || msg.StartsWith("Resuming")
                                    ? InstallStatus.Downloading : InstallStatus.Installing;
                                onAppProgress?.Invoke(app, st, msg);
                            }, ct);
                    }

                    lock (resultsLock) { results.Add(result); done++; }
                    onOverallProgress?.Invoke(done, total);
                    onAppProgress?.Invoke(app, result.Status, result.Message);
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            return results;
        }
    }
}
