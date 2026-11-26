using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CornSystems
{
    // Themed replacement for the native vertical scrollbar of an AutoScroll panel.
    // Shared across Corn Systems repos (like Dpi.cs) — copy changes to every repo.
    //
    // How it works: CornScrollHost owns the scrolling control and makes it wider than itself by
    // exactly the native scrollbar width whenever that bar is showing, so Windows' light-grey
    // bar is clipped off the right edge. A CornScrollBar is drawn over that strip and drives
    // AutoScrollPosition directly. Mouse wheel, keyboard focus scrolling and
    // ScrollControlIntoView all keep working because the native scrolling is still there.
    //
    //   var host = new CornScrollHost(myFlowPanel) { Dock = DockStyle.Fill };
    //   parent.Controls.Add(host);          // add the host, not the panel
    public sealed class CornScrollHost : Panel
    {
        public ScrollableControl Content { get; }
        public CornScrollBar     Bar     { get; }

        public CornScrollHost(ScrollableControl content)
        {
            Content   = content ?? throw new ArgumentNullException(nameof(content));
            BackColor = content.BackColor;
            content.Dock = DockStyle.None;

            Bar = new CornScrollBar(content) { BackColor = content.BackColor };
            Controls.Add(content);
            Controls.Add(Bar);
            Bar.BringToFront();

            Resize += (s, e) => LayoutContent();
            // Fires when the native bar appears or disappears (the client area changes width).
            content.ClientSizeChanged += (s, e) => LayoutContent();
        }

        private bool _laying;

        public void LayoutContent()
        {
            if (_laying || IsDisposed) return;
            _laying = true;
            try
            {
                int native = Content.VerticalScroll.Visible
                    ? SystemInformation.GetVerticalScrollBarWidthForDpi(Math.Max(96, DeviceDpi))
                    : 0;
                Content.SetBounds(0, 0, Width + native, Height);
                Bar.SetBounds(Width - Bar.BarWidth, 0, Bar.BarWidth, Height);
                Bar.Sync(force: true);
            }
            finally { _laying = false; }
        }
    }

    public sealed class CornScrollBar : Control
    {
        // Defaults are the Corn Systems palette; override per app if needed.
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color ThumbColor { get; set; } = Color.FromArgb( 61,  58, 112);   // BORDER2
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color ThumbHot   { get; set; } = Color.FromArgb(245, 200,  66);   // ACCENT

        public int BarWidth => Dpi.S(9);

        private readonly ScrollableControl _target;
        private readonly Timer _poll = new Timer { Interval = 120 };
        private int  _lastPos = -1, _lastTotal = -1, _lastView = -1;
        private bool _hover, _dragging;
        private int  _dragGrab;   // cursor offset inside the thumb when a drag starts

        public CornScrollBar(ScrollableControl target)
        {
            _target = target;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Cursor  = Cursors.Default;
            Visible = false;

            target.Scroll     += (s, e) => Sync();
            target.MouseWheel += (s, e) => Sync();
            target.Layout     += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)(() => Sync())); };
            // Covers scrolling we get no event for (focus changes, ScrollControlIntoView,
            // programmatic AutoScrollPosition). One property read per tick.
            _poll.Tick += (s, e) => Sync();
            HandleCreated += (s, e) => _poll.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _poll.Dispose();
            base.Dispose(disposing);
        }

        private int Total => Math.Max(_target.DisplayRectangle.Height, _target.ClientSize.Height);
        private int View  => _target.ClientSize.Height;
        private int Pos   => -_target.AutoScrollPosition.Y;
        private int Pad   => Dpi.S(3);

        public void Sync(bool force = false)
        {
            if (IsDisposed || _target.IsDisposed) return;
            int total = Total, view = View, pos = Pos;
            bool needed = total > view + 1;
            if (Visible != needed) Visible = needed;
            if (!needed) return;
            if (force || pos != _lastPos || total != _lastTotal || view != _lastView)
            {
                _lastPos = pos; _lastTotal = total; _lastView = view;
                Invalidate();
            }
        }

        private Rectangle ThumbRect()
        {
            int total = Total, view = View;
            int trackH = Math.Max(1, Height - Pad * 2);
            int thumbH = Math.Max(Dpi.S(28), (int)((long)trackH * view / Math.Max(1, total)));
            thumbH = Math.Min(trackH, thumbH);
            int range  = Math.Max(1, total - view);
            int y = Pad + (int)((long)(trackH - thumbH) * Math.Clamp(Pos, 0, range) / range);
            bool wide = _hover || _dragging;
            int w = wide ? Width - Dpi.S(2) : Math.Max(2, Width - Dpi.S(4));
            return new Rectangle((Width - w) / 2, y, w, thumbH);
        }

        private void ScrollTo(int y)
        {
            int range = Math.Max(0, Total - View);
            y = Math.Clamp(y, 0, range);
            _target.AutoScrollPosition = new Point(-_target.AutoScrollPosition.X, y);
            Sync(force: true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = ThumbRect();
            if (r.Height <= 0 || r.Width <= 0) return;
            var col = _dragging ? ThumbHot : _hover ? Color.FromArgb(200, ThumbHot) : ThumbColor;
            using var brush = new SolidBrush(col);
            using var path  = Rounded(r, r.Width / 2f);
            g.FillPath(brush, path);
        }

        private static GraphicsPath Rounded(Rectangle r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var thumb = ThumbRect();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragGrab = e.Y - thumb.Y;
                Capture   = true;
            }
            else
            {
                // Click on the track pages towards the cursor, like the native bar.
                ScrollTo(Pos + (e.Y < thumb.Y ? -View : View));
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            var thumb  = ThumbRect();
            int trackH = Math.Max(1, Height - Pad * 2);
            int free   = Math.Max(1, trackH - thumb.Height);
            int top    = Math.Clamp(e.Y - _dragGrab - Pad, 0, free);
            ScrollTo((int)((long)top * Math.Max(0, Total - View) / free));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture   = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int lines = SystemInformation.MouseWheelScrollLines;
            int step  = lines < 0 ? View : Math.Max(1, lines) * Dpi.S(16);
            ScrollTo(Pos - Math.Sign(e.Delta) * step);
        }
    }
}
