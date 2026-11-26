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
        private bool   _isInstalled;
        private bool   _logExpanded;
        private bool   _forceReinstall;

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
        private readonly DpiLayout   _dpi = new();

        private const int BASE_HEIGHT     = 125;   // collapsed tile height (logical px)
        private const int LOG_HEIGHT      = 90;    // log drawer height (logical px)
        private const int VERSION_OFFSET  = 22;    // extra height when version picker visible

        public event EventHandler CheckedChanged;
        public event EventHandler UninstallRequested;   // MainForm confirms and runs it
        public bool IsInstalled => _isInstalled;

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

            // Width first: the anchored version toggle / picker compute their anchor
            // distances from the tile size at the moment their bounds are set.
            _dpi.Add(() =>
            {
                Size   = new Size(Dpi.S(230), Dpi.S(BASE_HEIGHT));
                Margin = new Padding(Dpi.S(6));
            });
            BackColor = normalBg;
            Cursor    = _app.HasInstallMethod ? Cursors.Hand : Cursors.No;
            DoubleBuffered = true;   // hover / check repaint used to flicker the border

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
                AutoSize  = false,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                UseMnemonic = false
            };

            // ── Name ─────────────────────────────────────────────────────────
            var nameLbl = new Label
            {
                Text         = app.Name,
                ForeColor    = textPri,
                AutoSize     = false,
                BackColor    = Color.Transparent,
                AutoEllipsis = true,
                UseMnemonic  = false
            };

            // ── Description ──────────────────────────────────────────────────
            var descLbl = new Label
            {
                Text         = app.Description,
                ForeColor    = textSec,
                AutoSize     = false,
                BackColor    = Color.Transparent,
                AutoEllipsis = true,
                UseMnemonic  = false
            };
            _dpi.Add(() =>
            {
                iconLbl.Font = Theme.Font(Theme.EmojiFont, 15f);
                iconLbl.SetBounds(Dpi.S(8), Dpi.S(8), Dpi.S(34), Dpi.S(34));
                nameLbl.Font = Theme.Font(Theme.MonoFont, 9f, FontStyle.Bold);
                nameLbl.SetBounds(Dpi.S(48), Dpi.S(6), Dpi.S(152), Dpi.S(34));
                descLbl.Font = Theme.Font(Theme.UiFont, 7f);
                descLbl.SetBounds(Dpi.S(10), Dpi.S(72), Dpi.S(210), Dpi.S(28));
            });

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
                ForeColor = badgeFg,
                BackColor = Color.Transparent,
                AutoSize  = true
            };
            methodBadge.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER2, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, methodBadge.Width - 1, methodBadge.Height - 1);
            };

            var catBadge = new Label
            {
                Text      = app.Category.ToUpper(),
                ForeColor = Theme.MUTED,
                BackColor = Color.Transparent,
                AutoSize  = true,
                UseMnemonic = false
            };

            // ── Status dot ───────────────────────────────────────────────────
            _statusDot = new Label
            {
                Text      = "",
                AutoSize  = true,
                BackColor = Color.Transparent
            };

            // ── Progress bar ─────────────────────────────────────────────────
            _progressBar = new ProgressBar
            {
                Style    = ProgressBarStyle.Continuous,
                Minimum  = 0, Maximum = 100, Value = 0,
                Visible  = false
            };

            // ── Update badge ─────────────────────────────────────────────────
            _updateBadge = new Label
            {
                Text      = "⬆ update available",
                AutoSize  = true,
                ForeColor = Theme.METEOR,
                BackColor = Color.Transparent,
                Visible   = false
            };
            _dpi.Add(() =>
            {
                methodBadge.Font     = Theme.Font(Theme.MonoFont, 6f);
                methodBadge.Padding  = new Padding(Dpi.S(2), Dpi.S(1), Dpi.S(2), Dpi.S(1));
                methodBadge.Location = new Point(Dpi.S(10), Dpi.S(50));
                catBadge.Font        = Theme.Font(Theme.MonoFont, 5.5f);
                catBadge.Location    = new Point(Dpi.S(10) + methodBadge.PreferredWidth + Dpi.S(6), Dpi.S(52));
                _statusDot.Font      = Theme.Font(Theme.MonoFont, 7f);
                _statusDot.Location  = new Point(Dpi.S(10), Dpi.S(104));
                _progressBar.SetBounds(Dpi.S(10), Dpi.S(118), Width - Dpi.S(20), Dpi.S(4));
                _updateBadge.Font     = Theme.Font(Theme.MonoFont, 6.5f);
                _updateBadge.Location = new Point(Dpi.S(10), Dpi.S(104));
            });

            // ── Version picker (winget only) ──────────────────────────────────
            if (app.WingetId != null)
            {
                _versionToggle = new Button
                {
                    Text      = "ver ▾",
                    BackColor = Color.Transparent,
                    ForeColor = Theme.MUTED,
                    FlatStyle = FlatStyle.Flat,
                    Cursor    = Cursors.Hand
                };
                _versionToggle.FlatAppearance.BorderColor = Theme.BORDER2;
                _versionToggle.FlatAppearance.BorderSize  = 1;
                _versionToggle.Click += OnVersionToggleClicked;

                _versionPicker = new ComboBox
                {
                    DropDownStyle  = ComboBoxStyle.DropDownList,
                    BackColor      = Theme.BG,
                    ForeColor      = Theme.ACCENT,
                    FlatStyle      = FlatStyle.Flat,
                    Visible        = false
                };
                // Positioned explicitly (tile width is fixed) rather than anchored, so a
                // DPI re-layout can't leave stale anchor distances behind.
                _dpi.Add(() =>
                {
                    _versionToggle.Font = Theme.Font(Theme.MonoFont, 5.5f);
                    _versionToggle.SetBounds(Width - Dpi.S(50), Dpi.S(50), Dpi.S(44), Dpi.S(16));
                    _versionPicker.Font = Theme.Font(Theme.MonoFont, 6.5f);
                    _versionPicker.SetBounds(Dpi.S(10), Dpi.S(BASE_HEIGHT - 2), Width - Dpi.S(20), Dpi.S(20));
                });
                _versionPicker.Items.Add("latest (default)");
                if (!string.IsNullOrEmpty(app.PinnedVersion))
                    _versionPicker.Items.Add(app.PinnedVersion);
                _versionPicker.SelectedIndex = 0;

                _versionPicker.SelectedIndexChanged += (s, e) =>
                {
                    int idx = _versionPicker.SelectedIndex;
                    if (idx < 0) return;   // Items.Clear() while reloading the list
                    _app.PinnedVersion = idx == 0 ? null : _versionPicker.Items[idx].ToString();
                    RefreshVersionToggle();
                };
            }

            // ── Per-app log drawer ────────────────────────────────────────────
            _logToggleBtn = new Button
            {
                Text      = "log ▸",
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand,
                Visible   = false    // shown once there's log output
            };
            _logToggleBtn.FlatAppearance.BorderSize = 0;
            _logToggleBtn.Click += OnLogToggleClicked;

            // Note: no Visible=false here — a docked child that starts hidden stays hidden
            // after its drawer is shown, which is why the per-app log used to open empty.
            _tileLog = new RichTextBox
            {
                BackColor   = Theme.BG,
                ForeColor   = Color.FromArgb(100, Theme.ACCENT),   // dim gold
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                DetectUrls  = false
            };

            _logDrawer = new Panel
            {
                BackColor = Theme.DRAWER,
                Visible   = false
            };
            _dpi.Add(() =>
            {
                _logToggleBtn.Font = Theme.Font(Theme.MonoFont, 5.5f);
                _logToggleBtn.SetBounds(Dpi.S(10), Dpi.S(104), Dpi.S(44), Dpi.S(16));
                _tileLog.Font      = Theme.Font(Theme.MonoFont, 6f);
                _logDrawer.Padding = new Padding(Dpi.S(4), Dpi.S(3), Dpi.S(2), Dpi.S(2));
                _logDrawer.SetBounds(0, Dpi.S(BASE_HEIGHT), Width, Dpi.S(LOG_HEIGHT));
            });
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
            this.Click        += Toggle;
            iconLbl.Click     += Toggle;
            nameLbl.Click     += Toggle;
            descLbl.Click     += Toggle;
            catBadge.Click    += Toggle;
            methodBadge.Click += Toggle;
            _statusDot.Click  += Toggle;
            _updateBadge.Click += Toggle;

            // Hover: moving onto a child label raises MouseLeave on the tile, which used to
            // flash the hover colour off and on. Only drop it when the cursor really left.
            void HoverOn(object s, EventArgs e) { if (!_checked) BackColor = Theme.CARD_HOVER; }
            void HoverOff(object s, EventArgs e)
            {
                if (_checked || IsDisposed) return;
                if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
                BackColor = _normalBg;
            }
            this.MouseEnter += HoverOn;
            this.MouseLeave += HoverOff;
            foreach (var child in new Control[] { iconLbl, nameLbl, descLbl, methodBadge, catBadge, _statusDot, _updateBadge })
            {
                child.MouseEnter += HoverOn;
                child.MouseLeave += HoverOff;
            }

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
                        var forceItem = new ToolStripMenuItem("Force Reinstall / Repair");
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
                    if (!string.IsNullOrEmpty(_app.WingetId))
                    {
                        var uninstallItem = new ToolStripMenuItem("Uninstall...") { ForeColor = Theme.DANGER };
                        uninstallItem.Click += (cs, ce) => UninstallRequested?.Invoke(this, EventArgs.Empty);
                        cms.Items.Add(uninstallItem);
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
                    var copyItem = new ToolStripMenuItem($"Copy winget id  ({_app.WingetId})");
                    copyItem.Click += (cs, ce) =>
                    {
                        try { Clipboard.SetText(_app.WingetId); } catch (Exception ex) { SessionLog.Write("CLIPBOARD", ex); }
                    };
                    cms.Items.Add(copyItem);
                    var cmdItem = new ToolStripMenuItem("Copy install command");
                    cmdItem.Click += (cs, ce) =>
                    {
                        string pin = string.IsNullOrEmpty(_app.PinnedVersion) ? "" : $" --version \"{_app.PinnedVersion}\"";
                        try { Clipboard.SetText($"winget install --id {_app.WingetId} --exact{pin}"); }
                        catch (Exception ex) { SessionLog.Write("CLIPBOARD", ex); }
                    };
                    cms.Items.Add(cmdItem);
                }
            };
            FormClosedSafeDispose(cms);
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
                RefreshVersionToggle();
            }

            _versionPicker.Visible = show;
            Relayout();
        }

        private void RefreshVersionToggle()
        {
            bool pinned = _app.PinnedVersion != null;
            _versionToggle.ForeColor = pinned ? Theme.ACCENT : Theme.MUTED;
            _versionToggle.Text      = pinned ? $"v{_app.PinnedVersion.Split('.')[0]}▾" : "ver ▾";
        }

        // Tile height = base + version picker row (if open) + log drawer (if open).
        private void Relayout()
        {
            int verH = _versionPicker is { Visible: true } ? Dpi.S(VERSION_OFFSET) : 0;
            Height = Dpi.S(BASE_HEIGHT) + verH + (_logExpanded ? Dpi.S(LOG_HEIGHT) : 0);
            _logDrawer.Location = new Point(0, Dpi.S(BASE_HEIGHT) + verH);
        }

        // Called by MainForm after the window moves to a monitor with a different DPI.
        public void ApplyDpi()
        {
            SuspendLayout();
            try   { _dpi.Reapply(); Relayout(); }
            finally { ResumeLayout(true); }
            Invalidate(true);
        }

        private void FormClosedSafeDispose(ContextMenuStrip cms) => Disposed += (s, e) => cms.Dispose();

        // ── Per-app log toggle ────────────────────────────────────────────────
        private void OnLogToggleClicked(object sender, EventArgs e)
        {
            _logExpanded = !_logExpanded;
            _logDrawer.Visible = _logExpanded;
            _logToggleBtn.Text = _logExpanded ? "log ▾" : "log ▸";
            Relayout();
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
                case InstallStatus.Skipped:
                    // Cancelled / skipped: previously had no case, so the tile sat on
                    // "⏳ Installing..." with its progress bar showing until restart.
                    _logToggleBtn.ForeColor  = Theme.MUTED;
                    _statusDot.Text          = "⊘ Skipped";
                    _statusDot.ForeColor     = Theme.MUTED;
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

        // Free-form line in the status row (uninstall progress / result).
        public void ShowNote(string text, Color color)
        {
            _statusDot.Text      = text;
            _statusDot.ForeColor = color;
            _statusDot.Visible   = !_logToggleBtn.Visible;
            _updateBadge.Visible = false;
            _progressBar.Visible = false;
            Invalidate();
        }

        public void SetInstalled(bool installed)
        {
            bool was = _isInstalled;
            _isInstalled = installed;
            if (!installed && was)
            {
                // Uninstalled: back to a normal, selectable tile.
                _forceReinstall      = false;
                _app.ForceReinstall  = false;
                _statusDot.Text      = "";
                _updateBadge.Visible = false;
                BackColor            = _normalBg;
                Cursor               = _app.HasInstallMethod ? Cursors.Hand : Cursors.No;
            }
            if (installed)
            {
                _forceReinstall    = false;
                _app.ForceReinstall = false;
                _statusDot.Text    = "✔ Installed";
                _statusDot.ForeColor = Theme.SUCCESS;
                _statusDot.Visible = !_logToggleBtn.Visible;   // the log button shares this row
                BackColor          = _normalBg;
                _checked           = false;
                Cursor             = Cursors.Hand;
            }
            Invalidate();
        }

        public void SetHasUpdate(bool hasUpdate)
        {
            // Status dot, update badge and log button all share one row — show exactly one.
            bool logRow = _logToggleBtn.Visible;
            _updateBadge.Visible = hasUpdate && !logRow;
            _statusDot.Visible   = !hasUpdate && !logRow;
            Invalidate();
        }

        /// <summary>Called by import to reflect an externally-pinned version in the picker UI.</summary>
        public void SetPinnedVersion(string version)
        {
            if (_versionPicker == null) return;
            if (string.IsNullOrEmpty(version)) { _versionPicker.SelectedIndex = 0; RefreshVersionToggle(); return; }
            int idx = _versionPicker.Items.IndexOf(version);
            if (idx < 0) { _versionPicker.Items.Add(version); idx = _versionPicker.Items.Count - 1; }
            _versionPicker.SelectedIndex = idx;
            RefreshVersionToggle();
        }
    }

    // Category divider inside the app grid: 2px accent bar, label, then a hairline rule.
    public class SectionHeader : Panel
    {
        public SectionHeader(string title, string emoji)
        {
            Height    = Dpi.S(48);
            Margin    = new Padding(Dpi.S(6), Dpi.S(18), Dpi.S(6), Dpi.S(4));
            BackColor = Color.Transparent;

            Control lastParent = null;
            // Parent padding too — otherwise the header is wider than the grid's display area
            // and the FlowLayoutPanel grows a horizontal scrollbar.
            int FitWidth() => Math.Max(Dpi.S(100), lastParent.ClientSize.Width - lastParent.Padding.Horizontal - Margin.Horizontal);
            EventHandler syncHandler = (ps, pe) =>
            {
                if (lastParent != null && !IsDisposed) Width = FitWidth();
            };
            this.ParentChanged += (s, e) =>
            {
                if (lastParent != null) lastParent.ClientSizeChanged -= syncHandler;
                lastParent = Parent;
                if (lastParent != null)
                {
                    Width = FitWidth();
                    lastParent.ClientSizeChanged += syncHandler;
                }
            };
            // Headers are disposed on every re-filter; unhook so the grid doesn't keep them alive.
            this.Disposed += (s, e) => { if (lastParent != null) lastParent.ClientSizeChanged -= syncHandler; };

            var bar = new Panel
            {
                BackColor = Theme.ACCENT,
                Size      = new Size(Dpi.S(2), Dpi.S(22)),
                Location  = new Point(Dpi.S(4), Dpi.S(13))
            };

            var lbl = new Label
            {
                Text      = $"{emoji}  {title.ToUpper()}",
                Font      = Theme.Font(Theme.MonoFont, 8.5f, FontStyle.Bold),
                UseMnemonic = false,
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
