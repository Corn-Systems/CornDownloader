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
        private readonly List<InstallResult> _results;
        private readonly bool _allowRetry;

        public SummaryForm(List<InstallResult> results, bool allowRetry = true)
        {
            _results    = results ?? new List<InstallResult>();
            _allowRetry = allowRetry;

            Text            = "// INSTALL SUMMARY";
            BackColor       = Theme.BG;
            ForeColor       = Theme.TEXT_PRI;
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            AutoScaleMode   = AutoScaleMode.None;   // we scale explicitly via Dpi.S / Theme.Font

            try { this.Icon = AppIconBuilder.Get(); } catch (Exception ex) { SessionLog.Write("ICON", ex); }

            Size = new Size(Dpi.S(560), Dpi.S(540));
            Build();

            // Rebuilding is simpler than re-laying-out a read-only dialog; keep the size the
            // user had in logical terms.
            DpiLayout.Attach(this, () =>
            {
                Size = new Size(Dpi.S(560), Dpi.S(540));
                Build();
            });
            FormClosed += (s, e) => _tip.Dispose();
        }

        private void Build()
        {
            SuspendLayout();
            foreach (var c in Controls.Cast<Control>().ToList()) c.Dispose();
            Controls.Clear();
            FailedResults.Clear();
            _tip.RemoveAll();

            var results = _results;
            int  ok     = results.Count(r => r.Status == InstallStatus.Success);
            int  fail   = results.Count(r => r.Status == InstallStatus.Failed);
            int  skip   = results.Count(r => r.Status == InstallStatus.Skipped);
            bool reboot = results.Any(r => r.RebootRequired);

            MinimumSize = new Size(Dpi.S(440), Dpi.S(400));
            Font        = Theme.Font(Theme.MonoFont, 8.5f);

            var header = new Panel { BackColor = Theme.SURFACE, Dock = DockStyle.Top, Height = Dpi.S(70) };
            header.Controls.Add(new Label
            {
                Text      = fail > 0 ? $"⚠  {fail} installation{(fail == 1 ? "" : "s")} failed"
                          : ok > 0   ? "✔  All apps installed!"
                          :            "Nothing was installed",
                Font      = Theme.Font(Theme.MonoFont, 11f, FontStyle.Bold),
                ForeColor = fail > 0 ? Theme.DANGER : ok > 0 ? Theme.SUCCESS : Theme.TEXT_SEC,
                AutoSize  = true,
                Location  = new Point(Dpi.S(18), Dpi.S(14))
            });
            header.Controls.Add(new Label
            {
                Text      = $"{ok} succeeded   •   {fail} failed" + (skip > 0 ? $"   •   {skip} skipped" : "") +
                            $"   •   {results.Count} total" + (reboot ? "   •   ⟳ restart required" : ""),
                Font      = Theme.Font(Theme.MonoFont, 8.5f),
                ForeColor = reboot ? Theme.WARNING : Theme.TEXT_SEC,
                AutoSize  = true,
                Location  = new Point(Dpi.S(20), Dpi.S(42))
            });

            var scroll = new Panel { AutoScroll = true, BackColor = Theme.BG, Dock = DockStyle.Fill };
            // Rows are positioned absolutely, so Padding never applied to them; use an explicit inset.
            int inset = Dpi.S(14);
            int rowW  = ClientSize.Width - inset * 2 - SystemInformation.VerticalScrollBarWidth;

            int y = Dpi.S(10);
            void Section(string title, Color color, IEnumerable<InstallResult> items, Func<InstallResult, string> status, bool collectFailed)
            {
                var list = items.ToList();
                if (list.Count == 0) return;
                scroll.Controls.Add(MakeSectionLabel(title, color, inset, y)); y += Dpi.S(28);
                foreach (var r in list)
                {
                    if (collectFailed) FailedResults.Add(r);
                    var row = MakeResultRow(r.App.IconChar, r.App.Name, status(r), color, inset, y, rowW);
                    if (!string.IsNullOrEmpty(r.Message)) _tip.SetToolTip(row, r.Message);   // full message on hover
                    scroll.Controls.Add(row);
                    y += Dpi.S(38);
                }
                y += Dpi.S(8);
            }
            Section("Installed successfully", Theme.SUCCESS, results.Where(r => r.Status == InstallStatus.Success),
                    r => r.RebootRequired ? "✔ (restart)" : "✔", false);
            Section("Failed", Theme.DANGER, results.Where(r => r.Status == InstallStatus.Failed),
                    r => $"✘  {TrimError(r.Message)}", true);
            Section("Skipped", Theme.MUTED, results.Where(r => r.Status == InstallStatus.Skipped),
                    r => $"⊘  {TrimError(r.Message)}", false);
            scroll.Controls.Add(new Panel { Height = Dpi.S(10), Top = y, BackColor = Color.Transparent });

            var footer = new Panel { BackColor = Theme.SURFACE, Dock = DockStyle.Bottom, Height = Dpi.S(58) };
            var closeBtn = new Button
            {
                Text      = "Close",
                Size      = new Size(Dpi.S(100), Dpi.S(34)),
                BackColor = Theme.SURFACE2, ForeColor = Theme.TEXT_PRI,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Font      = Theme.Font(Theme.MonoFont, 8.5f)
            };
            closeBtn.FlatAppearance.BorderColor = Theme.BORDER;
            closeBtn.Click   += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(closeBtn);
            AcceptButton = closeBtn;
            CancelButton = closeBtn;

            var copyBtn = new Button
            {
                Text      = "⧉ Copy report",
                Size      = new Size(Dpi.S(120), Dpi.S(34)),
                BackColor = Theme.SURFACE2, ForeColor = Theme.TEXT_SEC,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Font      = Theme.Font(Theme.MonoFont, 7.5f)
            };
            copyBtn.FlatAppearance.BorderColor = Theme.BORDER;
            copyBtn.Click += (s, e) =>
            {
                try { Clipboard.SetText(BuildReport()); copyBtn.Text = "✔ Copied"; }
                catch (Exception ex) { SessionLog.Write("CLIPBOARD", ex); }
            };
            footer.Controls.Add(copyBtn);

            Button retryBtn = null;
            if (fail > 0 && _allowRetry)
            {
                retryBtn = new Button
                {
                    Text      = $"↺  Retry {fail} Failed",
                    Size      = new Size(Dpi.S(150), Dpi.S(34)),
                    BackColor = Theme.DANGER, ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font      = Theme.Font(Theme.MonoFont, 8.5f, FontStyle.Bold),
                    Cursor    = Cursors.Hand
                };
                retryBtn.FlatAppearance.BorderSize = 0;
                retryBtn.Click   += (s, e) => { DialogResult = DialogResult.Retry; Close(); };
                footer.Controls.Add(retryBtn);
            }

            // Positioned from the footer's real width. The old Anchor=Right was computed while
            // the footer was still its default 200px wide, which could push Close off-screen.
            void PlaceButtons()
            {
                int top = (footer.Height - closeBtn.Height) / 2;
                closeBtn.Location = new Point(footer.ClientSize.Width - Dpi.S(18) - closeBtn.Width, top);
                int x = closeBtn.Left - Dpi.S(10);
                if (retryBtn != null) { retryBtn.Location = new Point(x - retryBtn.Width, top); x = retryBtn.Left - Dpi.S(10); }
                copyBtn.Location = new Point(Dpi.S(18), top);
            }
            footer.Resize += (s, e) => PlaceButtons();

            Controls.AddRange(new Control[] { scroll, header, footer });
            ResumeLayout(true);
            PlaceButtons();
        }

        private string BuildReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{AppInfo.Name} {AppInfo.Version} — install report {DateTime.Now:yyyy-MM-dd HH:mm}");
            foreach (var r in _results.OrderBy(r => r.Status).ThenBy(r => r.App.Name))
                sb.AppendLine($"[{r.Status}] {r.App.Name}{(string.IsNullOrEmpty(r.App.WingetId) ? "" : $" ({r.App.WingetId})")}: {r.Message}");
            return sb.ToString();
        }

        private Label MakeSectionLabel(string text, Color color, int x, int y) => new Label
        {
            Text = text.ToUpperInvariant(), Font = Theme.Font(Theme.MonoFont, 7f, FontStyle.Bold),
            ForeColor = color, AutoSize = true, Top = y, Left = x + Dpi.S(2), BackColor = Color.Transparent
        };

        private Panel MakeResultRow(string icon, string name, string statusText, Color statusColor, int x, int y, int width)
        {
            var row = new Panel { BackColor = Theme.SURFACE, Size = new Size(width, Dpi.S(32)), Top = y, Left = x };
            row.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, row.Width - 1, row.Height - 1);
            };
            row.Controls.Add(new Label
            {
                Text = icon, Font = Theme.Font(Theme.EmojiFont, 11f), UseMnemonic = false,
                AutoSize = false, Size = new Size(Dpi.S(28), Dpi.S(28)),
                Location = new Point(Dpi.S(4), Dpi.S(2)), BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            });
            var nameLbl = new Label
            {
                Text = name, Font = Theme.Font(Theme.MonoFont, 8.5f),
                ForeColor = Theme.TEXT_PRI, AutoSize = true, UseMnemonic = false,
                Location = new Point(Dpi.S(36), Dpi.S(8)), BackColor = Color.Transparent
            };
            row.Controls.Add(nameLbl);
            var statusLbl = new Label
            {
                Text = statusText, Font = Theme.Font(Theme.MonoFont, 7.5f),
                ForeColor = statusColor, AutoSize = false, AutoEllipsis = true, UseMnemonic = false,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight
            };
            // Whatever is right of the name; long errors ellipsise instead of overlapping it.
            int statusLeft = Math.Max(nameLbl.Left + nameLbl.PreferredWidth + Dpi.S(12), width / 3);
            statusLbl.SetBounds(statusLeft, Dpi.S(6), Math.Max(Dpi.S(40), width - statusLeft - Dpi.S(10)), Dpi.S(20));
            row.Controls.Add(statusLbl);
            _tip.SetToolTip(statusLbl, statusText);
            return row;
        }

        private static string TrimError(string msg) =>
            string.IsNullOrEmpty(msg) ? "Unknown error"
            : msg.Length > 48 ? msg.Substring(0, 45) + "..." : msg;
    }
}
