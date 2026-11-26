using System;
using System.Collections.Generic;
using System.Drawing;
using CornSystems;

namespace CornDownloader
{
    // One palette for every form and control. Previously MainForm, AppTile and
    // SummaryForm each carried their own copy (with drifting values for SUCCESS).
    internal static class Theme
    {
        public static readonly Color BG          = Color.FromArgb(  8,   8,  18);
        public static readonly Color BG2         = Color.FromArgb( 13,  13,  32);
        public static readonly Color SURFACE     = Color.FromArgb( 19,  18,  42);
        public static readonly Color SURFACE2    = Color.FromArgb( 26,  24,  53);
        public static readonly Color CARD        = Color.FromArgb( 16,  15,  34);
        public static readonly Color CARD_HOVER  = Color.FromArgb( 19,  18,  45);
        public static readonly Color DRAWER      = Color.FromArgb( 12,  11,  28);

        public static readonly Color ACCENT      = Color.FromArgb(245, 200,  66);
        public static readonly Color ACCENT_DIM  = Color.FromArgb(201, 153,  30);
        public static readonly Color ACCENT_TEXT = Color.FromArgb(  8,   8,  18);
        public static readonly Color METEOR      = Color.FromArgb(244,  81,  30);
        public static readonly Color SKY_PURPLE  = Color.FromArgb(124,  58, 237);

        public static readonly Color SUCCESS     = Color.FromArgb( 34, 197,  94);
        public static readonly Color WARNING     = Color.FromArgb(251, 191,  36);
        public static readonly Color DANGER      = Color.FromArgb(239,  68,  68);

        public static readonly Color BORDER      = Color.FromArgb( 42,  40,  80);
        public static readonly Color BORDER2     = Color.FromArgb( 61,  58, 112);

        public static readonly Color TEXT_PRI    = Color.FromArgb(240, 238, 252);
        public static readonly Color TEXT_SEC    = Color.FromArgb(160, 157, 192);
        public static readonly Color MUTED       = Color.FromArgb(101,  97, 160);

        public const string MonoFont  = "Courier New";
        public const string EmojiFont = "Segoe UI Emoji";
        public const string UiFont    = "Segoe UI";

        // Every UI font goes through here. Sizes are still given in points, but the Font is
        // built in pixels for the monitor the window is on right now (Dpi.Current).
        // A plain point-size Font is rasterised at the *system* DPI GDI captured at logon,
        // so on a second monitor with different scaling the text came out too small or
        // too large next to controls that Dpi.S() had already rescaled.
        //
        // Cached and shared: fonts are immutable, and with ~10 labels per tile the catalog used
        // to allocate ~1,800 Font objects (plus their GDI handles) — again on every DPI change.
        // Callers must not Dispose the returned Font.
        public static Font Font(string family, float points, FontStyle style = FontStyle.Regular)
        {
            float px = (float)Math.Round(Dpi.S(points) * 96f / 72f, 2);
            var key  = (family, px, style);
            lock (_fonts)
            {
                if (!_fonts.TryGetValue(key, out var f))
                    _fonts[key] = f = new Font(family, px, style, GraphicsUnit.Pixel);
                return f;
            }
        }

        private static readonly Dictionary<(string, float, FontStyle), Font> _fonts = new();
    }
}
