using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

    // Numeric overlay badge on the taskbar button via ITaskbarList3.
    internal static class TaskbarBadge
    {
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
            void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            void SetProgressState(IntPtr hwnd, int tbpFlags);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
            void SetThumbnailClip(IntPtr hwnd, ref Rectangle prcClip);
        }

        [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"),
         ClassInterface(ClassInterfaceType.None)]
        private class TaskbarList { }

        private static readonly ITaskbarList3 _taskbar;

        static TaskbarBadge()
        {
            try { _taskbar = (ITaskbarList3)new TaskbarList(); _taskbar.HrInit(); }
            catch (Exception ex) { SessionLog.Write("TASKBAR", ex); }
        }

        // Pass 0 to clear the badge.
        public static void SetCount(IntPtr hwnd, int count)
        {
            if (_taskbar == null || hwnd == IntPtr.Zero) return;
            try
            {
                if (count <= 0) { _taskbar.SetOverlayIcon(hwnd, IntPtr.Zero, null); return; }

                const int sz = 16;
                using var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode     = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    using var bgBrush = new SolidBrush(Theme.ACCENT);
                    g.FillEllipse(bgBrush, 0, 0, sz - 1, sz - 1);

                    string label = count > 99 ? "99+" : count.ToString();
                    float  fs    = label.Length > 2 ? 5.5f : 7f;
                    using var font = new Font(Theme.MonoFont, fs, FontStyle.Bold);
                    using var tb   = new SolidBrush(Theme.ACCENT_TEXT);
                    using var sf   = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(label, font, tb, new RectangleF(0, 0, sz, sz), sf);
                }

                var hIcon = bmp.GetHicon();
                try     { _taskbar.SetOverlayIcon(hwnd, hIcon, $"{count} apps selected"); }
                finally { DestroyIcon(hIcon); }
            }
            catch (Exception ex) { SessionLog.Write("TASKBAR", ex); }
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
