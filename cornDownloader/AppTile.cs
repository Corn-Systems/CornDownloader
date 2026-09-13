using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CornSystems;

namespace CornDownloader
{
    // One catalog entry in the grid: checkbox-style tile with version picker,
    // per-app log drawer, installed / update badges and a right-click menu.
    public class AppTile : Panel
    {
        private bool   _checked;
        private bool   _isInstalled    = false;
        private bool   _hasUpdate      = false;
        private bool   _logExpanded    = false;
        private bool   _forceReinstall = false;

        private readonly Color _normalBg;
        private readonly Color _checkedBg;
        private readonly AppEntry   _app;
        private readonly DownloadManager _dm;
        private readonly ToolTip _sharedTip;

        // Sub-controls
        private readonly Label       _statusDot;
        private readonly ProgressBar _progressBar;
        private          Label       _updateBadge;
        private          ComboBox    _versionPicker;
        private          Button      _versionToggle;
        private          RichTextBox _tileLog;
        private          Panel       _logDrawer;
        private          Button      _logToggleBtn;

        private const int BASE_HEIGHT     = 125;   // collapsed tile height (logical px)
        private const int LOG_HEIGHT      = 90;    // log drawer height (logical px)
        private const int VERSION_OFFSET  = 22;    // extra height when version picker visible

        public event EventHandler CheckedChanged;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsChecked
        {
            get => _checked;
            set
            {
                if (value && !string.IsNullOrEmpty(_app.IsBundledWith)) return;
                if (value && !_app.HasInstallMethod) return;
                if (value && _isInstalled && !_forceReinstall) return;
                _checked  = value;
                if (!value) { _forceReinstall = false; _app.ForceReinstall = false; }
                BackColor = value
                    ? (_forceReinstall ? Color.FromArgb(20, Theme.METEOR) : _checkedBg)
                    : _normalBg;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public AppTile(AppEntry app, DownloadManager dm, ToolTip sharedTip)
        {
            Color normalBg = Theme.CARD, checkedBg = Theme.SURFACE2, accent = Theme.ACCENT,
                  textPri  = Theme.TEXT_PRI, textSec = Theme.TEXT_SEC, border = Theme.BORDER;
            _app       = app;
            _dm        = dm;
            _normalBg  = normalBg;
            _checkedBg = checkedBg;
            _sharedTip = sharedTip;

            Size      = new Size(Dpi.S(230), Dpi.S(BASE_HEIGHT));
            BackColor = normalBg;
            Margin    = new Padding(Dpi.S(6));
            Cursor    = _app.HasInstallMethod ? Cursors.Hand : Cursors.No;

            // ── Border + checkmark paint ──────────────────────────────────────
            this.Paint += (s, e) =>
            {
                var g = e.Graphics;
                Color borderCol = _isInstalled && !_forceReinstall ? Theme.BORDER
                    : _checked ? (_forceReinstall ? Theme.METEOR : accent) : border;
                float borderW = (_isInstalled && !_forceReinstall) ? 1f : 1.5f;
                using (var pen = new Pen(borderCol, borderW))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

                if (_isInstalled && !_forceReinstall)
                {
                    using var b = new SolidBrush(Color.FromArgb(60, Theme.SUCCESS));
                    g.FillRectangle(b, 1, 1, Width - 2, Dpi.S(3));
                }
                else if (_checked)
                {
                    Color checkFill = _forceReinstall ? Theme.METEOR : accent;
                    using var brush = new SolidBrush(checkFill);
                    g.FillRectangle(brush, Width - Dpi.S(22), Dpi.S(6), Dpi.S(16), Dpi.S(16));
                    using var whitePen = new Pen(Color.White, 2f);
                    g.DrawLines(whitePen, new[]
                    {
                        new Point(Width - Dpi.S(19), Dpi.S(14)),
                        new Point(Width - Dpi.S(15), Dpi.S(18)),
                        new Point(Width - Dpi.S(9),  Dpi.S(9))
                    });
                }
            };

            // ── Icon ─────────────────────────────────────────────────────────
            var iconLbl = new Label
            {
                Text      = app.IconChar,
                Font      = new Font(Theme.EmojiFont, 15f),
                AutoSize  = false,
                Size      = new Size(Dpi.S(34), Dpi.S(34)),
                Location  = new Point(Dpi.S(8), Dpi.S(8)),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };

            // ── Name ─────────────────────────────────────────────────────────
            var nameLbl = new Label
            {
                Text         = app.Name,
                Font         = new Font(Theme.MonoFont, 9f, FontStyle.Bold),
                ForeColor    = textPri,
                AutoSize     = false,
                Size         = new Size(Dpi.S(152), Dpi.S(34)),
                Location     = new Point(Dpi.S(48), Dpi.S(6)),
                BackColor    = Color.Transparent,
                AutoEllipsis = true,
                UseMnemonic  = false
            };

            // ── Description ──────────────────────────────────────────────────
            var descLbl = new Label
            {
                Text      = app.Description,
                Font      = new Font("Segoe UI", 7f),
                ForeColor = textSec,
                AutoSize  = false,
                Size      = new Size(Dpi.S(210), Dpi.S(26)),
                Location  = new Point(Dpi.S(10), Dpi.S(72)),
                BackColor = Color.Transparent
            };

            // ── Method badge ─────────────────────────────────────────────────
            string method;
            Color  badgeFg;
            if (!string.IsNullOrEmpty(app.IsBundledWith))
            {
                method  = "bundled";
                badgeFg = Theme.MUTED;   // muted — not installable standalone
            }
            else if (app.WingetId != null)
            {
                method  = "winget";
                badgeFg = Theme.ACCENT;
            }
            else if (!string.IsNullOrEmpty(app.DirectUrl) && !string.IsNullOrEmpty(app.FileName))
            {
                method  = "direct";
                badgeFg = Theme.TEXT_SEC;
            }
            else
            {
                // Neither winget nor a direct URL is configured — this entry can't actually
                // be installed yet. Say so instead of silently claiming "direct".
                method  = "⚠ no install";
                badgeFg = Theme.METEOR;
            }

            var methodBadge = new Label
            {
                Text      = method,
                Font      = new Font(Theme.MonoFont, 6f),
                ForeColor = badgeFg,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(Dpi.S(10), Dpi.S(50)),
                Padding   = new Padding(Dpi.S(2), Dpi.S(1), Dpi.S(2), Dpi.S(1))
            };
            methodBadge.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER2, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, methodBadge.Width - 1, methodBadge.Height - 1);
            };

            var catBadge = new Label
            {
                Text      = app.Category.ToUpper(),
                Font      = new Font(Theme.MonoFont, 5.5f),
                ForeColor = Theme.MUTED,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(methodBadge.PreferredWidth + Dpi.S(16), Dpi.S(52))
            };

            // ── Status dot ───────────────────────────────────────────────────
            _statusDot = new Label
            {
                Text      = "",
                AutoSize  = true,
                Location  = new Point(Dpi.S(10), Dpi.S(104)),
                Font      = new Font(Theme.MonoFont, 7f),
                BackColor = Color.Transparent
            };

            // ── Progress bar ─────────────────────────────────────────────────
            _progressBar = new ProgressBar
            {
                Size     = new Size(this.Width - Dpi.S(20), Dpi.S(4)),
                Location = new Point(Dpi.S(10), Dpi.S(118)),
                Style    = ProgressBarStyle.Continuous,
                Minimum  = 0, Maximum = 100, Value = 0,
                Visible  = false
            };

            // ── Update badge ─────────────────────────────────────────────────
            _updateBadge = new Label
            {
                Text      = "⬆ update available",
                AutoSize  = true,
                Location  = new Point(Dpi.S(10), Dpi.S(104)),
                Font      = new Font(Theme.MonoFont, 6.5f),
                ForeColor = Theme.METEOR,
                BackColor = Color.Transparent,
                Visible   = false
            };

            // ── Version picker (winget only) ──────────────────────────────────
            if (app.WingetId != null)
            {
                _versionToggle = new Button
                {
                    Text      = "ver ▾",
                    Size      = new Size(Dpi.S(44), Dpi.S(16)),
                    Location  = new Point(Width - Dpi.S(50), Dpi.S(50)),
                    BackColor = Color.Transparent,
                    ForeColor = Theme.MUTED,
                    FlatStyle = FlatStyle.Flat,
                    Font      = new Font(Theme.MonoFont, 5.5f),
                    Cursor    = Cursors.Hand,
                    Anchor    = AnchorStyles.Top | AnchorStyles.Right
                };
                _versionToggle.FlatAppearance.BorderColor = Theme.BORDER2;
                _versionToggle.FlatAppearance.BorderSize  = 1;
                _versionToggle.Click += OnVersionToggleClicked;

                _versionPicker = new ComboBox
                {
                    DropDownStyle  = ComboBoxStyle.DropDownList,
                    BackColor      = Theme.BG,
                    ForeColor      = Theme.ACCENT,
                    Font           = new Font(Theme.MonoFont, 6.5f),
                    Size           = new Size(Dpi.S(210), Dpi.S(20)),
                    Location       = new Point(Dpi.S(10), Dpi.S(BASE_HEIGHT - 4)),
                    Visible        = false,
                    Anchor         = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                _versionPicker.Items.Add("latest (default)");
                if (!string.IsNullOrEmpty(app.PinnedVersion))
                    _versionPicker.Items.Add(app.PinnedVersion);
                _versionPicker.SelectedIndex = 0;

                _versionPicker.SelectedIndexChanged += (s, e) =>
                {
                    int idx = _versionPicker.SelectedIndex;
                    _app.PinnedVersion = (idx == 0 || _versionPicker.Items[idx].ToString() == "latest (default)")
                        ? null
                        : _versionPicker.Items[idx].ToString();
                    _versionToggle.ForeColor = _app.PinnedVersion != null ? Theme.ACCENT : Theme.MUTED;
                    _versionToggle.Text      = _app.PinnedVersion != null
                        ? $"v{_app.PinnedVersion.Split('.')[0]}▾"
                        : "ver ▾";
                };
            }

            // ── Per-app log drawer ────────────────────────────────────────────
            _logToggleBtn = new Button
            {
                Text      = "log ▸",
                Size      = new Size(Dpi.S(38), Dpi.S(16)),
                Location  = new Point(Dpi.S(10), Dpi.S(104)),
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 5.5f),
                Cursor    = Cursors.Hand,
                Visible   = false    // shown once there's log output
            };
            _logToggleBtn.FlatAppearance.BorderSize = 0;
            _logToggleBtn.Click += OnLogToggleClicked;

            _tileLog = new RichTextBox
            {
                BackColor   = Theme.BG,
                ForeColor   = Color.FromArgb(100, Theme.ACCENT),   // dim gold
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                Font        = new Font(Theme.MonoFont, 6f),
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                Visible     = false
            };

            _logDrawer = new Panel
            {
                BackColor = Theme.DRAWER,
                Visible   = false,
                Location  = new Point(0, Dpi.S(BASE_HEIGHT)),
                Size      = new Size(this.Width, Dpi.S(LOG_HEIGHT))
            };
            // Top border on drawer
            _logDrawer.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER, 1);
                e.Graphics.DrawLine(pen, 0, 0, _logDrawer.Width, 0);
            };
            _tileLog.Dock = DockStyle.Fill;
            _logDrawer.Controls.Add(_tileLog);

            // ── Assemble ──────────────────────────────────────────────────────
            var ctrls = new List<Control> { iconLbl, nameLbl, descLbl, methodBadge, catBadge,
                                            _statusDot, _progressBar, _updateBadge, _logToggleBtn, _logDrawer };
            if (_versionToggle != null)  ctrls.Add(_versionToggle);
            if (_versionPicker != null)  ctrls.Add(_versionPicker);
            Controls.AddRange(ctrls.ToArray());

            // ── Click-to-toggle ───────────────────────────────────────────────
            void Toggle(object s, EventArgs e)
            {
                if (!string.IsNullOrEmpty(_app.IsBundledWith)) return;
                if (_isInstalled && !_forceReinstall) return;
                IsChecked = !_checked;
            }
            this.Click     += Toggle;
            iconLbl.Click  += Toggle;
            nameLbl.Click  += Toggle;
            descLbl.Click  += Toggle;
            catBadge.Click += Toggle;

            this.MouseEnter += (s, e) => { if (!_checked) BackColor = Theme.CARD_HOVER; };
            this.MouseLeave += (s, e) => { if (!_checked) BackColor = _normalBg; };

            // ── Right-click context menu ──────────────────────────────────────
            var cms = new ContextMenuStrip();
            cms.Opening += (s, e) =>
            {
                cms.Items.Clear();
                if (!string.IsNullOrEmpty(_app.IsBundledWith))
                {
                    var bundledItem = new ToolStripMenuItem($"Bundled with {_app.IsBundledWith}") { Enabled = false };
                    cms.Items.Add(bundledItem);
                    return;
                }
                if (_isInstalled)
                {
                    if (_forceReinstall && _checked)
                    {
                        var cancelItem = new ToolStripMenuItem("Cancel Reinstall");
                        cancelItem.Click += (cs, ce) => { IsChecked = false; };
                        cms.Items.Add(cancelItem);
                    }
                    else
                    {
                        var forceItem = new ToolStripMenuItem("Force Reinstall");
                        forceItem.Click += (cs, ce) =>
                        {
                            _forceReinstall    = true;
                            _app.ForceReinstall = true;
                            _checked           = false;  // reset so setter logic runs cleanly
                            IsChecked          = true;
                            Cursor             = Cursors.Hand;
                        };
                        cms.Items.Add(forceItem);
                    }
                }
                else
                {
                    var selItem = new ToolStripMenuItem(_checked ? "Deselect" : "Select");
                    selItem.Click += (cs, ce) => IsChecked = !_checked;
                    cms.Items.Add(selItem);
                }
                if (!string.IsNullOrEmpty(_app.WingetId))
                {
                    cms.Items.Add(new ToolStripSeparator());
                    var idItem = new ToolStripMenuItem($"winget id: {_app.WingetId}") { Enabled = false };
                    cms.Items.Add(idItem);
                }
            };
            this.ContextMenuStrip = cms;

            // ── Tooltip (full description + install method) ───────────────────
            string tipMethod = !string.IsNullOrEmpty(_app.WingetId) ? $"winget: {_app.WingetId}"
                             : !string.IsNullOrEmpty(_app.IsBundledWith) ? $"bundled with: {_app.IsBundledWith}"
                             : (!string.IsNullOrEmpty(_app.DirectUrl) && !string.IsNullOrEmpty(_app.FileName))
                                 ? "direct download"
                                 : "⚠ no install method configured yet";
            string tipText = $"{app.Description}\n\n{tipMethod}";
            _sharedTip.SetToolTip(this,        tipText);
            _sharedTip.SetToolTip(iconLbl,     tipText);
            _sharedTip.SetToolTip(nameLbl,     tipText);
            _sharedTip.SetToolTip(descLbl,     tipText);
            _sharedTip.SetToolTip(methodBadge, tipText);
        }

        // ── Version picker toggle ─────────────────────────────────────────────
        private async void OnVersionToggleClicked(object sender, EventArgs e)
        {
            if (_versionPicker == null) return;

            bool show = !_versionPicker.Visible;

            if (show && _versionPicker.Items.Count <= 1)
            {
                // Lazy-load versions from winget on first open
                _versionToggle.Text    = "...";
                _versionToggle.Enabled = false;
                List<string> versions;
                try { versions = await _dm.GetAvailableVersionsAsync(_app); }
                catch (Exception ex) { SessionLog.Write("VERSIONS", ex); versions = new List<string>(); }
                if (IsDisposed) return;
                _versionPicker.Items.Clear();
                _versionPicker.Items.Add("latest (default)");
                foreach (var v in versions) _versionPicker.Items.Add(v);
                // Re-select pinned version if it exists
                if (!string.IsNullOrEmpty(_app.PinnedVersion))
                {
                    int idx = _versionPicker.Items.IndexOf(_app.PinnedVersion);
                    _versionPicker.SelectedIndex = idx >= 0 ? idx : 0;
                }
                else _versionPicker.SelectedIndex = 0;
                _versionToggle.Enabled = true;
                _versionToggle.Text    = _app.PinnedVersion != null ? $"v{_app.PinnedVersion.Split('.')[0]}▾" : "ver ▾";
            }

            _versionPicker.Visible = show;
            int extraH = show ? Dpi.S(VERSION_OFFSET) : 0;
            int logH   = _logExpanded ? Dpi.S(LOG_HEIGHT) : 0;
            Height = Dpi.S(BASE_HEIGHT) + extraH + logH;
            _logDrawer.Location = new Point(0, Dpi.S(BASE_HEIGHT) + extraH);
            _versionPicker.Location = new Point(Dpi.S(10), Dpi.S(BASE_HEIGHT) - Dpi.S(4));
        }

        // ── Per-app log toggle ────────────────────────────────────────────────
        private void OnLogToggleClicked(object sender, EventArgs e)
        {
            _logExpanded = !_logExpanded;
            _logDrawer.Visible    = _logExpanded;
            _logToggleBtn.Text    = _logExpanded ? "log ▾" : "log ▸";

            int verH = (_versionPicker != null && _versionPicker.Visible) ? Dpi.S(VERSION_OFFSET) : 0;
            Height = Dpi.S(BASE_HEIGHT) + verH + (_logExpanded ? Dpi.S(LOG_HEIGHT) : 0);
            _logDrawer.Location = new Point(0, Dpi.S(BASE_HEIGHT) + verH);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Appends a line to this tile's per-app log and makes the toggle visible.</summary>
        public void AppendLog(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            if (_tileLog.InvokeRequired)
            {
                _tileLog.Invoke((Action)(() => AppendLog(msg)));
                return;
            }
            _tileLog.AppendText(msg + "\n");
            _tileLog.ScrollToCaret();
            if (!_logToggleBtn.Visible)
            {
                _logToggleBtn.Visible   = true;
                _statusDot.Visible      = false;   // log button takes the status-dot row
                _updateBadge.Visible    = false;
            }
        }

        public void SetStatus(InstallStatus status)
        {
            switch (status)
            {
                case InstallStatus.Installing:
                case InstallStatus.Downloading:
                    if (!_logToggleBtn.Visible) { _statusDot.Text = "⏳ Installing..."; _statusDot.ForeColor = Theme.WARNING; }
                    break;
                case InstallStatus.Success:
                    _logToggleBtn.ForeColor  = Theme.SUCCESS;
                    _logToggleBtn.Text       = _logExpanded ? "log ▾" : "log ▸";
                    _statusDot.Text          = "✔ Done";
                    _statusDot.ForeColor     = Theme.SUCCESS;
                    _statusDot.Visible       = !_logToggleBtn.Visible;
                    _progressBar.Visible     = false;
                    IsChecked = false;
                    break;
                case InstallStatus.Failed:
                    _logToggleBtn.ForeColor  = Theme.DANGER;
                    _statusDot.Text          = "✘ Failed";
                    _statusDot.ForeColor     = Theme.DANGER;
                    _statusDot.Visible       = !_logToggleBtn.Visible;
                    _progressBar.Visible     = false;
                    break;
            }
        }

        public void SetProgress(int percent)
        {
            if (_progressBar == null) return;
            if (percent < 0)
            { _progressBar.Style = ProgressBarStyle.Marquee; _progressBar.Visible = true; }
            else if (percent >= 100)
            { _progressBar.Visible = false; }
            else
            {
                _progressBar.Style   = ProgressBarStyle.Continuous;
                _progressBar.Value   = Math.Min(percent, 100);
                _progressBar.Visible = true;
                if (!_logToggleBtn.Visible) { _statusDot.Text = $"⏳ {percent}%"; _statusDot.ForeColor = Theme.WARNING; }
            }
        }

        public void SetInstalled(bool installed)
        {
            _isInstalled = installed;
            if (installed)
            {
                _forceReinstall    = false;
                _app.ForceReinstall = false;
                _statusDot.Text    = "✔ Installed";
                _statusDot.ForeColor = Theme.SUCCESS;
                _statusDot.Visible = true;
                BackColor          = _normalBg;
                _checked           = false;
                Cursor             = Cursors.Hand;
            }
            Invalidate();
        }

        public void SetHasUpdate(bool hasUpdate)
        {
            _hasUpdate = hasUpdate;
            if (_updateBadge != null)
            {
                _updateBadge.Visible = hasUpdate && !_logToggleBtn.Visible;
                if (!hasUpdate) _statusDot.Visible = !_logToggleBtn.Visible;
            }
            Invalidate();
        }

        /// <summary>Called by import to reflect an externally-pinned version in the picker UI.</summary>
        public void SetPinnedVersion(string version)
        {
            if (_versionPicker == null) return;
            int idx = _versionPicker.Items.IndexOf(version);
            if (idx < 0) { _versionPicker.Items.Add(version); idx = _versionPicker.Items.Count - 1; }
            _versionPicker.SelectedIndex = idx;
            if (_versionToggle != null)
            {
                _versionToggle.Text      = $"v{version.Split('.')[0]}▾";
                _versionToggle.ForeColor = Theme.ACCENT;
            }
        }
    }
}
