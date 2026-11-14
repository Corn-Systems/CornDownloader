using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CornDownloader
{
    // Identity strings live here so they aren't scattered as literals across the UI,
    // the User-Agent, the update checker and the log files.
    internal static class AppInfo
    {
        public const string Name        = "Corn Downloader";
        public const string Publisher   = "Corn Systems";
        public const string RepoOwner   = "Corn-Systems";
        public const string RepoName    = "CornDownloader";
        public const string RepoUrl     = "https://github.com/" + RepoOwner + "/" + RepoName;
        public const string ReleasesUrl = RepoUrl + "/releases";

        private static string _version;

        // Read from <Version> in the .csproj (via AssemblyInformationalVersion),
        // stripped of any "+commit" suffix SourceLink might append.
        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    var asm  = Assembly.GetExecutingAssembly();
                    var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    if (string.IsNullOrWhiteSpace(info))
                        info = asm.GetName().Version?.ToString(3);
                    int plus = info?.IndexOf('+') ?? -1;
                    _version = plus > 0 ? info.Substring(0, plus) : (info ?? "0.0.0");
                }
                catch { _version = "0.0.0"; }
                return _version;
            }
        }

        public static string UserAgent => $"CornDownloader/{Version} (+{RepoUrl})";
    }

    // Every on-disk location the app uses, in one place. Program.cs, SettingsManager
    // and SessionLog all go through here instead of rebuilding the same Path.Combine.
    internal static class AppPaths
    {
        public const string Vendor       = "CornSystems";
        public const string LegacyVendor = "CornStudios";   // pre-Sep-2026 folder name
        public const string AppFolder    = "CornDownloader";

        public static string DataDir      => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Vendor, AppFolder);
        public static string LegacyDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyVendor, AppFolder);

        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string CrashLog     => Path.Combine(DataDir, "crash.log");
        public static string LogsDir      => Path.Combine(DataDir, "logs");

        // The real Downloads known-folder (honours relocation / OneDrive redirection).
        // Falls back to %USERPROFILE%\Downloads if the shell call fails.
        public static string DefaultDownloadFolder
        {
            get
            {
                try
                {
                    var guid = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
                    if (SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out string path) == 0 && !string.IsNullOrEmpty(path))
                        return path;
                }
                catch (Exception ex) { SessionLog.Write("PATHS", ex); }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        // One-time move of %AppData%\CornStudios\CornDownloader → %AppData%\CornSystems\CornDownloader
        // so the rebrand doesn't silently reset everyone's settings.
        public static void MigrateLegacyData()
        {
            try
            {
                string oldDir = LegacyDataDir, newDir = DataDir;
                if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;

                Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
                try { Directory.Move(oldDir, newDir); return; }
                catch (Exception ex) { SessionLog.Write("MIGRATE", ex); }   // cross-volume or locked — copy instead

                Directory.CreateDirectory(newDir);
                foreach (var f in Directory.GetFiles(oldDir))
                    File.Copy(f, Path.Combine(newDir, Path.GetFileName(f)), overwrite: false);
            }
            catch (Exception ex)
            {
                SessionLog.Write($"[WARN] Legacy settings migration failed: {ex.Message}");
            }
        }

        public static void EnsureDataDir()
        {
            try { Directory.CreateDirectory(DataDir); } catch (Exception ex) { SessionLog.Write("PATHS", ex); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
        private static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
            [MarshalAs(UnmanagedType.LPWStr)] out string pszPath);
    }
}
