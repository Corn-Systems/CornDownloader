using System;
using System.Drawing;
using System.Windows.Forms;
using CornSystems;

namespace CornDownloader
{
    // Category divider inside the app grid: 2px accent bar, label, then a hairline rule.
    public class SectionHeader : Panel
    {
        public SectionHeader(string title, string emoji)
        {
            Height    = Dpi.S(48);
            Margin    = new Padding(Dpi.S(6), Dpi.S(18), Dpi.S(6), Dpi.S(4));
            BackColor = Color.Transparent;
            Anchor    = AnchorStyles.Left | AnchorStyles.Right;

            Control lastParent = null;
            EventHandler syncHandler = (ps, pe) =>
            {
                if (lastParent != null) Width = lastParent.ClientSize.Width - Margin.Horizontal;
            };
            this.ParentChanged += (s, e) =>
            {
                if (lastParent != null) lastParent.ClientSizeChanged -= syncHandler;
                lastParent = Parent;
                if (lastParent != null)
                {
                    Width = lastParent.ClientSize.Width - Margin.Horizontal;
                    lastParent.ClientSizeChanged += syncHandler;
                }
            };

            var bar = new Panel
            {
                BackColor = Theme.ACCENT,
                Size      = new Size(Dpi.S(2), Dpi.S(22)),
                Location  = new Point(Dpi.S(4), Dpi.S(13))
            };

            var lbl = new Label
            {
                Text      = $"{emoji}  {title.ToUpper()}",
                Font      = new Font(Theme.MonoFont, 8.5f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                Location  = new Point(Dpi.S(12), Dpi.S(14)),
                BackColor = Color.Transparent
            };

            this.Paint += (s, e) =>
            {
                int lineY = Height / 2 + 2;
                using var pen = new Pen(Theme.BORDER, 1);
                e.Graphics.DrawLine(pen, lbl.Right + Dpi.S(14), lineY, Width - Dpi.S(20), lineY);
            };

            Controls.AddRange(new Control[] { bar, lbl });
        }
    }
}
