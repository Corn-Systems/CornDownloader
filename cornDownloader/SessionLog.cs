using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CornDownloader
{
    // Persistent per-day log at %AppData%\CornSystems\CornDownloader\logs\yyyy-MM-dd.log.
    // The in-app log panel tees into this, and every swallowed exception in the
    // codebase reports here instead of vanishing into an empty catch.
    internal static class SessionLog
    {
        private static readonly object _lock = new object();
        private const int RetentionDays = 14;
        private static bool _pruned;

        public static string CurrentFile => Path.Combine(AppPaths.LogsDir, $"{DateTime.Now:yyyy-MM-dd}.log");

        public static void Write(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(AppPaths.LogsDir);
                    if (!_pruned) { Prune(); _pruned = true; }
                    File.AppendAllText(CurrentFile,
                        $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { /* logging must never take the app down */ }
        }

        public static void Write(string tag, Exception ex)
        {
            if (ex == null) return;
            Write($"[{tag}] {ex.GetType().Name}: {ex.Message}");
        }

        public static void SessionHeader(string mode)
        {
            Write($"===== {AppInfo.Name} {AppInfo.Version} started ({mode}) — user={Environment.UserName}, elevated={SystemInfo.IsElevated}, os={Environment.OSVersion.Version} =====");
        }

        private static void Prune()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var f in Directory.GetFiles(AppPaths.LogsDir, "*.log")
                             .Where(f => File.GetLastWriteTime(f) < cutoff))
                    File.Delete(f);
            }
            catch { /* prune is opportunistic */ }
        }
    }

    internal static class SystemInfo
    {
        private static bool? _elevated;

        public static bool IsElevated
        {
            get
            {
                if (_elevated.HasValue) return _elevated.Value;
                try
                {
                    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    var p = new System.Security.Principal.WindowsPrincipal(id);
                    _elevated = p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { _elevated = false; }
                return _elevated.Value;
            }
        }
    }
}
