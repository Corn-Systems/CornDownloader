using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CornDownloader
{
    internal sealed class UpdateInfo
    {
        public bool   IsNewer     { get; init; }
        public string LatestTag   { get; init; }
        public string ReleaseUrl  { get; init; }
        public string AssetUrl    { get; init; }   // first .exe asset, if any
    }

    // Non-blocking check against GitHub Releases. Never throws; returns null when the
    // check couldn't complete (offline, rate-limited, API shape changed).
    internal static class UpdateChecker
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        static UpdateChecker()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                string url = $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    SessionLog.Write($"[UPDATE] GitHub returned {(int)resp.StatusCode}");
                    return null;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                string tag  = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                string html = root.TryGetProperty("html_url", out var h) ? h.GetString() : AppInfo.ReleasesUrl;
                if (string.IsNullOrWhiteSpace(tag)) return null;

                string asset = null;
                if (root.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            a.TryGetProperty("browser_download_url", out var dl))
                        { asset = dl.GetString(); break; }
                    }

                bool newer = IsNewer(tag, AppInfo.Version);
                SessionLog.Write($"[UPDATE] latest={tag} current={AppInfo.Version} newer={newer}");
                return new UpdateInfo { IsNewer = newer, LatestTag = tag, ReleaseUrl = html, AssetUrl = asset };
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                SessionLog.Write("UPDATE", ex);
                return null;
            }
        }

        internal static bool IsNewer(string remoteTag, string local)
        {
            if (!Version.TryParse(Normalize(remoteTag), out var remote)) return false;
            if (!Version.TryParse(Normalize(local), out var current)) return false;
            return remote > current;
        }

        private static string Normalize(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
            v = v.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) v = v.Substring(0, cut);
            // Version.TryParse needs at least major.minor
            return v.Contains('.') ? v : v + ".0";
        }
    }
}
