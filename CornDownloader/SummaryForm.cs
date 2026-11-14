using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CornSystems;

namespace CornDownloader
{
    // Post-run summary: succeeded / failed sections, retry button, reboot notice.
    public class SummaryForm : Form
    {
        public List<InstallResult> FailedResults { get; } = new();
        private readonly ToolTip _tip = new ToolTip();

        public SummaryForm(List<InstallResult> results)
        {
            int  ok     = results.Count(r => r.Status == InstallStatus.Success);
            int  fail   = results.Count(r => r.Status == InstallStatus.Failed);
            bool reboot = results.Any(r => r.RebootRequired);

            Text            = "// INSTALL SUMMARY";
            Size            = new Size(Dpi.S(560), Dpi.S(540));
            MinimumSize     = new Size(Dpi.S(440), Dpi.S(400));
            BackColor       = Theme.BG;
            ForeColor       = Theme.TEXT_PRI;
            Font            = new Font(Theme.MonoFont, 8.5f);
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;

            try { this.Icon = AppIconBuilder.Get(); } catch (Exception ex) { SessionLog.Write("ICON", ex); }

            var header = new Panel { BackColor = Theme.SURFACE, Dock = DockStyle.Top, Height = Dpi.S(70) };
            bool allOk = fail == 0;
            header.Controls.Add(new Label
            {
                Text      = allOk ? "✔  All apps installed!" : $"⚠  {fail} installation{(fail == 1 ? "" : "s")} failed",
                Font      = new Font(Theme.MonoFont, 11f, FontStyle.Bold),
                ForeColor = allOk ? Theme.SUCCESS : Theme.DANGER,
                AutoSize  = true,
                Location  = new Point(Dpi.S(18), Dpi.S(14))
            });
            header.Controls.Add(new Label
            {
                Text      = $"{ok} succeeded   •   {fail} failed   •   {results.Count} total" + (reboot ? "   •   ⟳ restart required" : ""),
                Font      = new Font(Theme.MonoFont, 8.5f),
                ForeColor = reboot ? Theme.WARNING : Theme.TEXT_SEC,
                AutoSize  = true,
                Location  = new Point(Dpi.S(20), Dpi.S(42))
            });

            var scroll = new Panel
            {
                AutoScroll = true, BackColor = Theme.BG, Dock = DockStyle.Fill,
                Padding    = new Padding(Dpi.S(14), Dpi.S(10), Dpi.S(14), Dpi.S(10))
            };

            int y = Dpi.S(10);
            if (ok > 0)
            {
                scroll.Controls.Add(MakeSectionLabel("Installed successfully", Theme.SUCCESS, y)); y += Dpi.S(28);
                foreach (var r in results.Where(r => r.Status == InstallStatus.Success))
                {
                    scroll.Controls.Add(MakeResultRow(r.App.IconChar, r.App.Name, r.RebootRequired ? "✔ (restart)" : "✔", Theme.SUCCESS, y));
                    y += Dpi.S(38);
                }
                y += Dpi.S(8);
            }
            if (fail > 0)
            {
                scroll.Controls.Add(MakeSectionLabel("Failed", Theme.DANGER, y)); y += Dpi.S(28);
                foreach (var r in results.Where(r => r.Status == InstallStatus.Failed))
                {
                    FailedResults.Add(r);
                    var row = MakeResultRow(r.App.IconChar, r.App.Name, $"✘  {TrimError(r.Message)}", Theme.DANGER, y);
                    _tip.SetToolTip(row, r.Message);   // full error on hover
                    scroll.Controls.Add(row);
                    y += Dpi.S(38);
                }
            }
            scroll.Controls.Add(new Panel { Height = Dpi.S(10), Top = y, BackColor = Color.Transparent });

            var footer = new Panel { BackColor = Theme.SURFACE, Dock = DockStyle.Bottom, Height = Dpi.S(58) };
            var closeBtn = new Button
            {
                Text      = "Close",
                Size      = new Size(Dpi.S(100), Dpi.S(34)),
                BackColor = Theme.SURFACE2, ForeColor = Theme.TEXT_PRI,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Anchor    = AnchorStyles.Right | AnchorStyles.Top
            };
            closeBtn.FlatAppearance.BorderColor = Theme.BORDER;
            closeBtn.Location = new Point(this.ClientSize.Width - Dpi.S(118), Dpi.S(12));
            closeBtn.Click   += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(closeBtn);

            if (fail > 0)
            {
                var retryBtn = new Button
                {
                    Text      = $"↺  Retry {fail} Failed",
                    Size      = new Size(Dpi.S(140), Dpi.S(34)),
                    BackColor = Theme.DANGER, ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font      = new Font(Theme.MonoFont, 8.5f, FontStyle.Bold),
                    Cursor    = Cursors.Hand
                };
                retryBtn.FlatAppearance.BorderSize = 0;
                retryBtn.Location = new Point(this.ClientSize.Width - Dpi.S(268), Dpi.S(12));
                retryBtn.Click   += (s, e) => { DialogResult = DialogResult.Retry; Close(); };
                footer.Controls.Add(retryBtn);
            }

            Controls.AddRange(new Control[] { scroll, header, footer });
            FormClosed += (s, e) => _tip.Dispose();
        }

        private Label MakeSectionLabel(string text, Color color, int y) => new Label
        {
            Text = text.ToUpperInvariant(), Font = new Font(Theme.MonoFont, 7f, FontStyle.Bold),
            ForeColor = color, AutoSize = true, Top = y, Left = Dpi.S(2), BackColor = Color.Transparent
        };

        private Panel MakeResultRow(string icon, string name, string statusText, Color statusColor, int y)
        {
            var row = new Panel { BackColor = Theme.SURFACE, Size = new Size(Dpi.S(490), Dpi.S(32)), Top = y, Left = 0 };
            row.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, row.Width - 1, row.Height - 1);
            };
            row.Controls.Add(new Label
            {
                Text = icon, Font = new Font(Theme.EmojiFont, 11f),
                AutoSize = false, Size = new Size(Dpi.S(28), Dpi.S(28)),
                Location = new Point(Dpi.S(4), Dpi.S(2)), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            });
            row.Controls.Add(new Label
            {
                Text = name, Font = new Font(Theme.MonoFont, 8.5f),
                ForeColor = Theme.TEXT_PRI, AutoSize = true,
                Location = new Point(Dpi.S(36), Dpi.S(8)), BackColor = Color.Transparent
            });
            var statusLbl = new Label
            {
                Text = statusText, Font = new Font(Theme.MonoFont, 7.5f),
                ForeColor = statusColor, AutoSize = true, BackColor = Color.Transparent
            };
            statusLbl.Location = new Point(row.Width - statusLbl.PreferredWidth - Dpi.S(10), Dpi.S(9));
            statusLbl.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
            row.Controls.Add(statusLbl);
            return row;
        }

        private static string TrimError(string msg) =>
            string.IsNullOrEmpty(msg) ? "Unknown error"
            : msg.Length > 48 ? msg.Substring(0, 45) + "..." : msg;
    }
}
