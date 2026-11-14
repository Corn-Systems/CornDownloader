using System.Drawing;

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
    }
}
