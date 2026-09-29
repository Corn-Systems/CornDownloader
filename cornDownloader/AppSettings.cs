using System;
using System.Collections.Generic;
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

    // Export / import format (.corn / .json). Also the input format for `--pack` in headless mode.
    internal class SelectionPack
    {
        public string Version        { get; set; } = "1";
        public string CreatedAt      { get; set; }
        public List<PackedApp> Apps  { get; set; } = new();
    }

    internal class PackedApp
    {
        // Id is the primary match key (stable across renames). Name is kept for human
        // readability and as a fallback for packs exported before Id existed.
        public string Id            { get; set; }
        public string Name          { get; set; }
        public string PinnedVersion { get; set; }   // null = latest
    }
}
