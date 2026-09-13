using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace CornDownloader
{
    // Window icon. Prefers the icon embedded in the exe (Assets\CornDownloader.ico via
    // <ApplicationIcon>) so the title bar matches shortcuts and the taskbar; falls back
    // to drawing a corn glyph if the build had no icon.
    internal static class AppIconBuilder
    {
        private static Icon _cached;

        public static Icon Get()
        {
            if (_cached != null) return _cached;
            try
            {
                _cached = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (_cached != null) return _cached;
            }
            catch (Exception ex) { SessionLog.Write("ICON", ex); }
            _cached = Draw(32);
            return _cached;
        }

        public static Icon Draw(int size = 32)
        {
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var bgBrush = new SolidBrush(Theme.SURFACE);
                g.FillEllipse(bgBrush, 0, 0, size - 1, size - 1);

                using var ring = new Pen(Theme.ACCENT, size > 24 ? 1.5f : 1f);
                g.DrawEllipse(ring, 1, 1, size - 3, size - 3);

                float fs = size * 0.45f;
                using var font = new Font(Theme.EmojiFont, fs, GraphicsUnit.Pixel);
                using var sf   = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("🌽", font, Brushes.White, new RectangleF(0, 0, size, size), sf);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
