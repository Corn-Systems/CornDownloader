using System;
using System.IO;
using System.Text.Json;

namespace CornDownloader
{
    internal class AppSettings
    {
        public string DownloadFolder { get; set; } = "";
        public bool   PreferWinget   { get; set; } = true;
        public int    WindowWidth    { get; set; } = 1180;
        public int    WindowHeight   { get; set; } = 760;
        public string WindowState    { get; set; } = "Maximized";
        // "auto" = let winget decide, "user" or "machine" = pass --scope explicitly.
        public string WingetScope    { get; set; } = "auto";
        public bool   CheckForUpdates { get; set; } = true;
    }

    internal static class SettingsManager
    {
        private static readonly JsonSerializerOptions _json = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                SessionLog.Write("SETTINGS", ex);
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                AppPaths.EnsureDataDir();
                // Write to a temp file then swap so a crash mid-write can't corrupt settings.json.
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, _json));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                SessionLog.Write("SETTINGS", ex);
            }
        }
    }
}
