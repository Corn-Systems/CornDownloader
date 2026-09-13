using System.Collections.Generic;

namespace CornDownloader
{
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
