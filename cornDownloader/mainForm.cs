using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;

namespace CornDownloader
{
    internal class MainForm : Form
    {
        // ── State ─────────────────────────────────────────────────────────────
        private readonly DownloadManager _dm;
        private readonly CliOptions _cli;
        private readonly Dictionary<AppEntry, AppTile> _tiles          = new();
        private readonly Dictionary<string, Button>    _sidebarBtns    = new();
        private readonly Dictionary<AppEntry, bool>    _installedCache = new();
        private readonly Dictionary<AppEntry, bool>    _upgradeCache   = new();
        private readonly string[] _categories;
        private AppSettings _settings;

        private string _activeCategory = "All";
        private bool   _isInstalling   = false;
        private bool   _initialized    = false;
        private bool   _closing        = false;
        private CancellationTokenSource _cts;
        private readonly CancellationTokenSource _lifetimeCts = new();   // cancels background scans on close
        // Startup upgrade scan; installs/upgrades wait on it so the status label makes sense
        // (WingetRunner already serialises the actual winget calls).
        private Task _upgradeScanTask;

        // ── Controls ──────────────────────────────────────────────────────────
        private Panel           _sidebar;
        private Panel           _mainArea;
        private Panel           _topBar;
        private FlowLayoutPanel _appGrid;
        private Panel           _bottomBar;
        private Label           _statusLabel;
        private Label           _selectionCountLabel;
        private ProgressBar     _overallProgress;
        private Button          _installBtn;
        private Button          _upgradeBtn;
        private Button          _clearBtn;
        private Button          _cancelBtn;
        private Button          _logToggle;
        private TextBox         _searchBox;
        private Label           _wingetBadge;
        private LinkLabel       _updateLink;
        private TextBox         _folderBox;
        private Button          _browseBtn;
        private CheckBox        _preferWingetChk;
        private RichTextBox     _logBox;
        private Panel           _logPanel;
        private Label           _scanStatusLabel;
        // One ToolTip shared by every tile — 100+ per-tile instances leaked native windows.
        private readonly ToolTip _sharedTileTip = new ToolTip
        {
            AutoPopDelay = 8000, InitialDelay = 600, ReshowDelay = 300, ShowAlways = true
        };

        public MainForm(CliOptions cli = null)
        {
            _cli      = cli;
            _settings = SettingsManager.Load();
            _dm       = new DownloadManager { WingetScope = cli?.Scope ?? _settings.WingetScope ?? "auto" };
            _categories = new[] { "All" }
                .Concat(AppCatalog.All.Select(a => a.Category).Distinct().OrderBy(c => c))
                .ToArray();

            InitializeComponent();
            ValidateCatalog();
            ApplySettings();
            PopulateApps("All");
            PreselectFromCli();
            UpdateSelectionCount();
            _ = RunStartupAsync();

            this.FormClosing += (s, e) =>
            {
                _closing = true;
                try { _cts?.Cancel(); } catch { }
                try { _lifetimeCts.Cancel(); } catch { }
                SaveSettings();
                try { _sharedTileTip.Dispose(); } catch { }
            };

            // Also save on meaningful state changes so a crash doesn't lose settings.
            this.ResizeEnd += (s, e) => SaveSettings();
            if (_folderBox       != null) _folderBox.Leave              += (s, e) => SaveSettings();
            if (_preferWingetChk != null) _preferWingetChk.CheckedChanged += (s, e) => SaveSettings();
        }

        // Called from the single-instance watcher thread (via BeginInvoke) when a second
        // copy of the app is launched.
        public void BringToFrontFromOtherInstance()
        {
            if (_closing || IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            TopMost = true; TopMost = false;   // pull above other windows without staying pinned
        }

        // Marshal to the UI thread, and silently drop the call if the form is going away.
        // Background scans that finish after Close() used to hit disposed controls and
        // pop the crash dialog during shutdown.
        private void Ui(Action action)
        {
            if (_closing || IsDisposed || !IsHandleCreated)
            {
                if (!IsHandleCreated && !_closing && !IsDisposed) { try { action(); } catch (Exception ex) { SessionLog.Write("UI", ex); } }
                return;
            }
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex) { SessionLog.Write("UI", ex); }
        }

        // ── Startup ───────────────────────────────────────────────────────────

        // Cheap integrity check on the hand-maintained catalog; logs instead of crashing.
        private void ValidateCatalog()
        {
            var issues  = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var app in AppCatalog.All)
            {
                string label = string.IsNullOrEmpty(app.Name) ? $"(unnamed, Id='{app.Id}')" : app.Name;

                if (string.IsNullOrEmpty(app.Name))        issues.Add($"{label}: missing Name.");
                if (string.IsNullOrEmpty(app.Description)) issues.Add($"{label}: missing Description.");
                if (string.IsNullOrEmpty(app.Category))    issues.Add($"{label}: missing Category.");

                if (string.IsNullOrEmpty(app.Id))
                    issues.Add($"{label}: missing Id (export/import packs won't survive a rename).");
                else if (!seenIds.Add(app.Id))
                    issues.Add($"{label}: duplicate Id '{app.Id}'.");

                if (!string.IsNullOrEmpty(app.FileName) && !seenFiles.Add(app.FileName))
                    issues.Add($"{label}: duplicate FileName '{app.FileName}' — two parallel downloads would clobber each other.");

                if (!app.HasInstallMethod)
                    issues.Add($"{label}: no install method — needs WingetId, DirectUrl+FileName, or IsBundledWith.");
            }

            if (issues.Count > 0)
            {
                Log($"[CATALOG] {issues.Count} issue(s) found at startup:");
                foreach (var issue in issues) Log($"  - {issue}");
            }

            Debug.Assert(issues.Count == 0, $"AppCatalog has {issues.Count} integrity issue(s) — see the log panel for details.");
        }

        private void PreselectFromCli()
        {
            if (_cli == null || !_cli.HasSelection) return;
            try
            {
                var entries = HeadlessRunner.ResolveSelection(_cli, out var missing);
                foreach (var e in entries)
                    if (_tiles.TryGetValue(e, out var tile))
                    {
                        tile.IsChecked = true;
                        if (!string.IsNullOrEmpty(e.PinnedVersion)) tile.SetPinnedVersion(e.PinnedVersion);
                    }
                Log($"[CLI] preselected {entries.Count} app(s){(missing.Count > 0 ? $", {missing.Count} not in catalog" : "")}");
            }
            catch (Exception ex)
            {
                Log($"[CLI] could not load selection: {ex.Message}");
                SessionLog.Write("CLI", ex);
            }
        }

        private async Task RunStartupAsync()
        {
            var ct = _lifetimeCts.Token;
            try
            {
                if (_settings.CheckForUpdates) _ = CheckForUpdatesAsync(ct);
                await RefreshWingetSourcesAsync(ct);
                await ScanInstalledAsync(ct);
                _upgradeScanTask = ScanUpgradesAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SessionLog.Write("STARTUP", ex); }
            _initialized = true;
            Ui(UpdateSelectionCount);   // now safe to push the real badge
        }

        private async Task CheckForUpdatesAsync(CancellationToken ct)
        {
            var info = await UpdateChecker.CheckAsync(ct);
            if (info == null || !info.IsNewer) return;
            Ui(() =>
            {
                _updateLink.Text    = $"⬆ {info.LatestTag} available";
                _updateLink.Tag     = info.ReleaseUrl;
                _updateLink.Visible = true;
                LayoutPanels();
                Log($"[UPDATE] {info.LatestTag} is available — {info.ReleaseUrl}");
            });
        }

        // ── Settings ──────────────────────────────────────────────────────────

        private void ApplySettings()
        {
            if (_settings.WindowState == "Maximized")
                this.WindowState = FormWindowState.Maximized;
            else if (_settings.WindowWidth > 0 && _settings.WindowHeight > 0)
                this.Size = new Size(_settings.WindowWidth, _settings.WindowHeight);

            string folder = _cli?.Folder ?? _settings.DownloadFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                folder = AppPaths.DefaultDownloadFolder;
            if (_folderBox != null) _folderBox.Text = folder;

            if (_preferWingetChk != null)
                _preferWingetChk.Checked = _settings.PreferWinget && _dm.WingetAvailable && !(_cli?.NoWinget ?? false);
        }

        private void SaveSettings()
        {
            _settings.DownloadFolder = _folderBox?.Text.Trim() ?? "";
            _settings.PreferWinget   = _preferWingetChk?.Checked ?? true;
            _settings.WindowState    = this.WindowState == FormWindowState.Maximized ? "Maximized" : "Normal";
            if (this.WindowState == FormWindowState.Normal)
            {
                _settings.WindowWidth  = this.Width;
                _settings.WindowHeight = this.Height;
            }
            SettingsManager.Save(_settings);
        }

        // ── Winget scans ──────────────────────────────────────────────────────

        private void SetScanStatus(string text, Color color) =>
            Ui(() => { if (_scanStatusLabel != null) { _scanStatusLabel.Text = text; _scanStatusLabel.ForeColor = color; } });

        private async Task RefreshWingetSourcesAsync(CancellationToken ct)
        {
            if (!_dm.WingetAvailable) return;
            SetScanStatus("🔄 Refreshing winget sources...", Theme.TEXT_SEC);
            await _dm.RefreshSourcesAsync(ct);
            SetScanStatus("🔍 Scanning installed apps (first run can take a minute)...", Theme.TEXT_SEC);
        }

        private async Task ScanInstalledAsync(CancellationToken ct)
        {
            if (!_dm.WingetAvailable) return;
            var installedIds = await _dm.GetAllInstalledIdsAsync(ct);
            foreach (var app in AppCatalog.All)
                _installedCache[app] = !string.IsNullOrEmpty(app.WingetId) && installedIds.Contains(app.WingetId);

            Ui(() =>
            {
                foreach (var kv in _tiles)
                    if (_installedCache.TryGetValue(kv.Key, out bool inst)) kv.Value.SetInstalled(inst);

                int ic = _installedCache.Values.Count(v => v);
                if (installedIds.Count == 0)
                    SetScanStatus("⚠ installed scan returned nothing (winget slow?) — see log", Theme.WARNING);
                else
                    SetScanStatus($"✔ {ic}/{AppCatalog.All.Count} apps installed", Theme.SUCCESS);
                UpdateSelectionCount();
            });
        }

        private async Task ScanUpgradesAsync(CancellationToken ct)
        {
            if (!_dm.WingetAvailable) return;
            var updatableIds = await _dm.GetAvailableUpdatesAsync(ct);
            foreach (var app in AppCatalog.All)
                _upgradeCache[app] = !string.IsNullOrEmpty(app.WingetId) && updatableIds.Contains(app.WingetId);

            Ui(() =>
            {
                int count = _upgradeCache.Values.Count(v => v);
                if (_upgradeBtn != null)
                {
                    _upgradeBtn.Visible = count > 0;
                    _upgradeBtn.Text    = $"⬆  Update {count} App{(count == 1 ? "" : "s")}";
                }
                foreach (var kv in _tiles)
                    if (_upgradeCache.TryGetValue(kv.Key, out bool u)) kv.Value.SetHasUpdate(u);
            });
        }

        private async Task WaitForBackgroundScanAsync()
        {
            if (_upgradeScanTask != null && !_upgradeScanTask.IsCompleted)
            {
                SetScanStatus("⏳ Waiting for background scan to finish...", Theme.TEXT_SEC);
                try { await _upgradeScanTask; }
                catch (Exception ex) { SessionLog.Write("SCAN-WAIT", ex); }
            }
        }

        // ── Upgrade ───────────────────────────────────────────────────────────

        private async void OnUpgradeClicked(object sender, EventArgs e)
        {
            if (_isInstalling) return;
            var toUpgrade = _upgradeCache.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            if (toUpgrade.Count == 0) return;

            await WaitForBackgroundScanAsync();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _isInstalling = true; _upgradeBtn.Enabled = false; _upgradeBtn.Text = "⏳ Updating...";
            _installBtn.Enabled = false;
            _cancelBtn.Visible = true;
            _overallProgress.Maximum = toUpgrade.Count; _overallProgress.Value = 0;
            int done = 0;
            Log($"[UPGRADE] Upgrading {toUpgrade.Count} app(s) — {DateTime.Now:HH:mm:ss}");

            var results = new List<InstallResult>();
            foreach (var app in toUpgrade)
            {
                if (token.IsCancellationRequested) break;
                var result = await _dm.UpgradeAsync(app,
                    msg => Ui(() => { _statusLabel.Text = $"{app.Name}: {msg}"; Log($"[{app.Name}] {msg}"); _tiles[app].AppendLog(msg); }),
                    token);
                results.Add(result);
                done++;
                Ui(() =>
                {
                    _overallProgress.Value = done;
                    if (_tiles.TryGetValue(app, out var tile)) tile.SetStatus(result.Status);
                    if (result.Status == InstallStatus.Success) _upgradeCache[app] = false;
                });
            }

            _isInstalling = false; _installBtn.Enabled = true;
            _cancelBtn.Visible = false;
            _cts.Dispose(); _cts = null;
            int ok = results.Count(r => r.Status == InstallStatus.Success);
            int fail = results.Count(r => r.Status == InstallStatus.Failed);
            _statusLabel.Text = $"Updates done — {ok} succeeded, {fail} failed.";
            Log($"[UPGRADE DONE] {ok}/{toUpgrade.Count} — {DateTime.Now:HH:mm:ss}");

            int remaining = _upgradeCache.Values.Count(v => v);
            _upgradeBtn.Visible = remaining > 0;
            _upgradeBtn.Text    = remaining > 0 ? $"⬆  Update {remaining} App{(remaining == 1 ? "" : "s")}" : "";
            _upgradeBtn.Enabled = remaining > 0;

            if (!token.IsCancellationRequested && !_closing)
            {
                using var summary = new SummaryForm(results);
                summary.ShowDialog(this);
            }
        }

        // ── Export / Import ───────────────────────────────────────────────────

        private void ExportSelections()
        {
            var selected = _tiles.Where(kv => kv.Value.IsChecked).Select(kv => kv.Key).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("No apps selected to export.", "Nothing to export",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title      = "Export app selection",
                Filter     = "Corn Downloader pack (*.corn)|*.corn|JSON (*.json)|*.json",
                DefaultExt = "corn",
                FileName   = $"corn-pack-{DateTime.Now:yyyy-MM-dd}"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var pack = new SelectionPack
            {
                CreatedAt = DateTime.Now.ToString("o"),
                Apps = selected.Select(a => new PackedApp { Id = a.Id, Name = a.Name, PinnedVersion = a.PinnedVersion }).ToList()
            };

            try
            {
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true }));
                _statusLabel.Text = $"✔ Exported {selected.Count} apps.";
                Log($"[EXPORT] {selected.Count} apps → {dlg.FileName}");
            }
            catch (Exception ex)
            {
                SessionLog.Write("EXPORT", ex);
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportSelections()
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Import app selection",
                Filter = "Corn Downloader pack (*.corn)|*.corn|JSON (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                var pack = JsonSerializer.Deserialize<SelectionPack>(File.ReadAllText(dlg.FileName),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (pack?.Apps == null || pack.Apps.Count == 0)
                {
                    MessageBox.Show("The file contains no app selections.", "Empty pack", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                foreach (var tile in _tiles.Values) tile.IsChecked = false;

                int matched = 0;
                foreach (var packed in pack.Apps)
                {
                    var entry = AppCatalog.Find(packed.Id, packed.Name);
                    if (entry == null || !_tiles.TryGetValue(entry, out var tile)) continue;

                    tile.IsChecked = true;
                    // PinnedVersion is session-scoped: it lives on the catalog entry until restart.
                    if (!string.IsNullOrEmpty(packed.PinnedVersion))
                    {
                        entry.PinnedVersion = packed.PinnedVersion;
                        tile.SetPinnedVersion(packed.PinnedVersion);
                    }
                    matched++;
                }

                UpdateSelectionCount();
                _statusLabel.Text = $"✔ Imported {matched}/{pack.Apps.Count} apps from pack.";
                Log($"[IMPORT] {matched}/{pack.Apps.Count} apps ← {dlg.FileName}");

                if (matched < pack.Apps.Count)
                {
                    int missing = pack.Apps.Count - matched;
                    MessageBox.Show(
                        $"{missing} app{(missing == 1 ? "" : "s")} in the pack weren't found in the catalog (may have been removed or renamed).",
                        "Partial import", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                SessionLog.Write("IMPORT", ex);
                MessageBox.Show($"Import failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void InitializeComponent()
        {
            Dpi.Update(this);
            AutoScaleMode = AutoScaleMode.None;

            try { this.Icon = AppIconBuilder.Get(); } catch (Exception ex) { SessionLog.Write("ICON", ex); }

            int appCount = AppCatalog.All.Count;
            int catCount = AppCatalog.All.Select(a => a.Category).Distinct().Count();
            this.Text = $"{AppInfo.Name} {AppInfo.Version} — {appCount} apps • {catCount} categories";
            this.Size            = new Size(Dpi.S(1180), Dpi.S(760));
            this.MinimumSize     = new Size(Dpi.S(900),  Dpi.S(600));
            this.BackColor       = Theme.BG;
            this.ForeColor       = Theme.TEXT_PRI;
            this.Font            = new Font(Theme.MonoFont, 8.5f, FontStyle.Regular);
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;

            BuildTopBar();
            BuildSidebar();
            BuildMainArea();
            BuildBottomBar();

            this.Controls.AddRange(new Control[] { _topBar, _sidebar, _mainArea, _bottomBar });
            this.Resize += (s, e) => LayoutPanels();
            LayoutPanels();

            DpiChanged += (s, e) =>
            {
                Dpi.Update(e.DeviceDpiNew);
                if (e.SuggestedRectangle != Rectangle.Empty)
                    SetBounds(e.SuggestedRectangle.X, e.SuggestedRectangle.Y,
                              e.SuggestedRectangle.Width, e.SuggestedRectangle.Height);
                this.MinimumSize = new Size(Dpi.S(900), Dpi.S(600));
                RescalePanels();
            };
        }

        private void RescalePanels() => LayoutPanels();

        private void LayoutPanels()
        {
            int w        = ClientSize.Width;
            int h        = ClientSize.Height;
            int topH     = Dpi.S(60);
            int botH     = Dpi.S(120);
            int sideW    = Dpi.S(210);
            int logH     = (_logPanel != null && _logPanel.Visible) ? _logPanel.Height : 0;
            int contentH = h - topH - botH - logH;

            _topBar.SetBounds(0, 0, w, topH);
            _sidebar.SetBounds(0, topH, sideW, contentH);
            _mainArea.SetBounds(sideW, topH, w - sideW, contentH);

            if (_logPanel != null)
            {
                _logPanel.SetBounds(0, topH + contentH, w, _logPanel.Height);
                if (_logPanel.Visible)
                    foreach (Control c in _logPanel.Controls)
                        if (c is Button) c.Location = new Point(_logPanel.Width - Dpi.S(80), Dpi.S(4));
            }

            _bottomBar.SetBounds(0, h - botH, w, botH);

            if (_clearBtn != null && _installBtn != null && _logToggle != null)
            {
                int bw = _bottomBar.Width;
                _installBtn.Location = new Point(bw - Dpi.S(16) - _installBtn.Width, Dpi.S(42));
                _clearBtn.Location   = new Point(_installBtn.Left - Dpi.S(8) - _clearBtn.Width, Dpi.S(42));
                if (_cancelBtn != null)
                    _cancelBtn.Location = new Point(_clearBtn.Left - Dpi.S(8) - _cancelBtn.Width, Dpi.S(42));
                _logToggle.Location  = new Point(bw - Dpi.S(16) - _logToggle.Width, Dpi.S(11));
            }

            if (_updateLink != null && _upgradeBtn != null)
                _updateLink.Location = new Point(_topBar.Width - Dpi.S(16) - _updateLink.Width, Dpi.S(22));
        }

        // ── Top bar ───────────────────────────────────────────────────────────
        private void BuildTopBar()
        {
            _topBar = new Panel { BackColor = Theme.SURFACE, Dock = DockStyle.None };

            var accentBar = new Panel
            {
                BackColor = Theme.ACCENT,
                Size      = new Size(Dpi.S(3), Dpi.S(34)),
                Location  = new Point(Dpi.S(14), Dpi.S(13))
            };

            var titleLbl = new Label
            {
                Text      = "🌽  CORN_DOWNLOADER",
                Font      = new Font(Theme.MonoFont, 9.5f, FontStyle.Bold),
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                Location  = new Point(Dpi.S(24), Dpi.S(19))
            };

            _searchBox = new TextBox
            {
                PlaceholderText = "  search apps...",
                BackColor       = Theme.CARD,
                ForeColor       = Theme.TEXT_PRI,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = new Font(Theme.MonoFont, 9f),
                Size            = new Size(Dpi.S(240), Dpi.S(28)),
                Location        = new Point(Dpi.S(255), Dpi.S(16))
            };
            _searchBox.TextChanged += (s, e) => FilterApps(_searchBox.Text);

            _wingetBadge = new Label
            {
                AutoSize = true,
                Font     = new Font(Theme.MonoFont, 7.5f),
                Location = new Point(Dpi.S(510), Dpi.S(21))
            };
            UpdateWingetBadge();

            _upgradeBtn = new Button
            {
                Text      = "⬆  UPDATES AVAILABLE",
                AutoSize  = true,
                BackColor = Theme.METEOR,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 7.5f, FontStyle.Bold),
                Location  = new Point(Dpi.S(700), Dpi.S(14)),
                Height    = Dpi.S(30),
                Visible   = false,
                Cursor    = Cursors.Hand
            };
            _upgradeBtn.FlatAppearance.BorderSize = 0;
            _upgradeBtn.Click += OnUpgradeClicked;

            _updateLink = new LinkLabel
            {
                AutoSize         = true,
                Font             = new Font(Theme.MonoFont, 7.5f, FontStyle.Bold),
                LinkColor        = Theme.ACCENT,
                ActiveLinkColor  = Theme.ACCENT_DIM,
                VisitedLinkColor = Theme.ACCENT,
                LinkBehavior     = LinkBehavior.HoverUnderline,
                BackColor        = Color.Transparent,
                Visible          = false,
                Anchor           = AnchorStyles.Top | AnchorStyles.Right
            };
            _updateLink.LinkClicked += (s, e) =>
            {
                if (_updateLink.Tag is string url)
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex) { SessionLog.Write("UPDATE-OPEN", ex); }
            };

            _topBar.Controls.AddRange(new Control[] { accentBar, titleLbl, _searchBox, _wingetBadge, _upgradeBtn, _updateLink });
        }

        private void UpdateWingetBadge()
        {
            if (_dm.WingetAvailable)
            { _wingetBadge.Text = "✦  winget detected"; _wingetBadge.ForeColor = Theme.SUCCESS; }
            else
            { _wingetBadge.Text = "⚠  winget not found — direct URLs only"; _wingetBadge.ForeColor = Theme.METEOR; }
        }

        // ── Sidebar ───────────────────────────────────────────────────────────
        private void BuildSidebar()
        {
            _sidebar = new Panel { BackColor = Theme.SURFACE };

            var sideHeader = new Label
            {
                Text      = "// CATEGORIES",
                Font      = new Font(Theme.MonoFont, 6.5f, FontStyle.Bold),
                ForeColor = Theme.MUTED,
                AutoSize  = true,
                Location  = new Point(Dpi.S(12), Dpi.S(12)),
                BackColor = Color.Transparent
            };
            _sidebar.Controls.Add(sideHeader);

            int y = Dpi.S(34);
            foreach (var cat in _categories)
            {
                var btn = CreateSidebarBtn(cat);
                btn.Location = new Point(Dpi.S(8), y);
                btn.Width    = Dpi.S(194);
                _sidebar.Controls.Add(btn);
                y += Dpi.S(38);
            }

            var divider = new Panel
            {
                BackColor = Theme.BORDER,
                Size      = new Size(Dpi.S(178), 1),
                Location  = new Point(Dpi.S(12), y + Dpi.S(6))
            };
            _sidebar.Controls.Add(divider);
            y += Dpi.S(14);

            var selAll = CreateSmallBtn("✦ ALL", Theme.ACCENT);
            selAll.Location  = new Point(Dpi.S(8), y + Dpi.S(6));
            selAll.Width     = Dpi.S(92);
            selAll.ForeColor = Theme.ACCENT_TEXT;
            selAll.Click    += (s, e) => SetAllInView(true);

            var deselAll = CreateSmallBtn("✗ NONE", Theme.SURFACE2);
            deselAll.Location  = new Point(Dpi.S(106), y + Dpi.S(6));
            deselAll.Width     = Dpi.S(96);
            deselAll.ForeColor = Theme.TEXT_SEC;
            deselAll.Click    += (s, e) => SetAllInView(false);

            var recBtn = new Button
            {
                Text      = "★  RECOMMENDED",
                Size      = new Size(Dpi.S(194), Dpi.S(32)),
                Location  = new Point(Dpi.S(8), y + Dpi.S(42)),
                BackColor = Theme.ACCENT,
                ForeColor = Theme.ACCENT_TEXT,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 7f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            recBtn.FlatAppearance.BorderSize = 0;
            recBtn.Click += (s, e) =>
            {
                foreach (var kv in _tiles) kv.Value.IsChecked = false;
                foreach (var kv in _tiles) kv.Value.IsChecked = kv.Key.IsRecommended;
                UpdateSelectionCount();
            };

            var exportBtn = CreateGhostBtn("⬆ EXPORT", Theme.ACCENT, Dpi.S(92));
            exportBtn.Location = new Point(Dpi.S(8), y + Dpi.S(82));
            exportBtn.Click += (s, e) => ExportSelections();

            var importBtn = CreateGhostBtn("⬇ IMPORT", Theme.TEXT_SEC, Dpi.S(96));
            importBtn.Location = new Point(Dpi.S(106), y + Dpi.S(82));
            importBtn.Click += (s, e) => ImportSelections();

            _scanStatusLabel = new Label
            {
                Text      = _dm.WingetAvailable ? "🔍 scanning..." : "",
                ForeColor = Theme.MUTED,
                Font      = new Font(Theme.MonoFont, 6.5f),
                AutoSize  = false,
                Size      = new Size(Dpi.S(194), Dpi.S(36)),
                Location  = new Point(Dpi.S(8), y + Dpi.S(116)),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _sidebar.Controls.AddRange(new Control[] { selAll, deselAll, recBtn, exportBtn, importBtn, _scanStatusLabel });
        }

        private Button CreateGhostBtn(string text, Color fg, int width)
        {
            var btn = new Button
            {
                Text      = text,
                Size      = new Size(width, Dpi.S(26)),
                BackColor = Theme.SURFACE2,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 6.5f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Theme.BORDER2;
            btn.FlatAppearance.BorderSize  = 1;
            return btn;
        }

        private Button CreateSidebarBtn(string category)
        {
            string emoji   = category == "All" ? "✦" : CategoryEmoji(category);
            string display = category switch
            {
                "Media & Entertainment"    => "MEDIA & ENTERTAIN.",
                "Utilities & System Tools" => "UTILITIES & SYS.",
                _                          => category.ToUpper()
            };

            int total = category == "All" ? AppCatalog.All.Count : AppCatalog.All.Count(a => a.Category == category);

            bool active = _activeCategory == category;
            var btn = new Button
            {
                Text          = $"  {emoji}  {display}",
                TextAlign     = ContentAlignment.MiddleLeft,
                FlatStyle     = FlatStyle.Flat,
                BackColor     = active ? Color.FromArgb(40, Theme.ACCENT) : Color.Transparent,
                ForeColor     = active ? Theme.ACCENT : Theme.TEXT_SEC,
                Font          = new Font(Theme.MonoFont, 7f, active ? FontStyle.Bold : FontStyle.Regular),
                Height        = Dpi.S(32),
                Padding       = new Padding(0, 0, Dpi.S(36), 0),
                AutoEllipsis  = true,
                AutoSize      = false,
                Cursor        = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize         = 0;
            btn.FlatAppearance.BorderColor        = active ? Theme.ACCENT : Color.FromArgb(1, Theme.BG);
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, Theme.ACCENT);

            var badge = new Label
            {
                Text      = total.ToString(),
                AutoSize  = false,
                Size      = new Size(Dpi.S(28), Dpi.S(14)),
                Font      = new Font(Theme.MonoFont, 6f, FontStyle.Bold),
                ForeColor = Theme.MUTED,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };
            btn.Controls.Add(badge);
            void PositionBadge() => badge.Location = new Point(btn.Width - Dpi.S(32), (btn.Height - Dpi.S(14)) / 2);
            btn.SizeChanged   += (s, e) => PositionBadge();
            btn.HandleCreated += (s, e) => PositionBadge();

            btn.Paint += (s, e) =>
            {
                PositionBadge();
                if (_activeCategory == category)
                {
                    using var b = new SolidBrush(Theme.ACCENT);
                    e.Graphics.FillRectangle(b, 0, Dpi.S(4), Dpi.S(2), btn.Height - Dpi.S(8));
                }
            };

            btn.Click += (s, e) =>
            {
                _activeCategory = category;
                RefreshSidebarButtons();
                PopulateApps(category);
            };

            _sidebarBtns[category] = btn;
            return btn;
        }

        private void UpdateSidebarCounts()
        {
            foreach (var kv in _sidebarBtns)
            {
                string cat   = kv.Key;
                var    btn   = kv.Value;
                int    total = cat == "All" ? AppCatalog.All.Count : AppCatalog.All.Count(a => a.Category == cat);
                int    sel   = _tiles.Count(t => (cat == "All" || t.Key.Category == cat) && t.Value.IsChecked);

                if (btn.Controls.Count > 0 && btn.Controls[0] is Label badge)
                {
                    badge.Text      = sel > 0 ? $"{sel}/{total}" : total.ToString();
                    badge.ForeColor = sel > 0 ? Theme.ACCENT : Theme.MUTED;
                }
            }
        }

        private Button CreateSmallBtn(string text, Color bg)
        {
            var btn = new Button
            {
                Text      = text,
                Height    = Dpi.S(28),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 7f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void RefreshSidebarButtons()
        {
            foreach (var kv in _sidebarBtns)
            {
                bool active = kv.Key == _activeCategory;
                var  btn    = kv.Value;
                btn.BackColor = active ? Color.FromArgb(40, Theme.ACCENT) : Color.Transparent;
                btn.ForeColor = active ? Theme.ACCENT : Theme.TEXT_SEC;
                btn.Font      = new Font(Theme.MonoFont, 7f, active ? FontStyle.Bold : FontStyle.Regular);
                btn.Invalidate();
            }
        }

        // ── App grid ──────────────────────────────────────────────────────────
        private void BuildMainArea()
        {
            _mainArea = new Panel { BackColor = Theme.BG };
            _appGrid  = new FlowLayoutPanel
            {
                AutoScroll   = true,
                WrapContents = true,
                BackColor    = Theme.BG,
                Padding      = new Padding(Dpi.S(12)),
                Dock         = DockStyle.Fill
            };
            _mainArea.Controls.Add(_appGrid);
        }

        private void PopulateApps(string category)
        {
            _appGrid.SuspendLayout();
            _appGrid.Controls.Clear();

            IEnumerable<AppEntry> apps = category == "All"
                ? AppCatalog.All
                : AppCatalog.All.Where(a => a.Category == category);

            string search = _searchBox?.Text?.Trim().ToLowerInvariant() ?? "";
            if (!string.IsNullOrEmpty(search))
                apps = apps.Where(a => (a.Name ?? "").ToLowerInvariant().Contains(search) ||
                                       (a.Description ?? "").ToLowerInvariant().Contains(search) ||
                                       (a.WingetId ?? "").ToLowerInvariant().Contains(search));

            foreach (var group in apps.ToList().GroupBy(a => a.Category).OrderBy(g => g.Key))
            {
                var header = new SectionHeader(group.Key, CategoryEmoji(group.Key));
                _appGrid.Controls.Add(header);
                _appGrid.SetFlowBreak(header, true);

                foreach (var app in group)
                {
                    if (!_tiles.TryGetValue(app, out var tile))
                    {
                        tile = new AppTile(app, _dm, _sharedTileTip) { IsChecked = false };
                        tile.CheckedChanged += (s, e) => UpdateSelectionCount();
                        _tiles[app] = tile;
                    }
                    _appGrid.Controls.Add(tile);
                }
            }

            _appGrid.ResumeLayout(true);
        }

        internal static string CategoryEmoji(string cat) => cat switch
        {
            "Browsers"                 => "🌐",
            "Dev Tools"                => "💻",
            "Media & Entertainment"    => "🎬",
            "Productivity"             => "📋",
            "Gaming"                   => "🎮",
            "Utilities & System Tools" => "🔧",
            "Customization"            => "🎨",
            _                          => "📦"
        };

        private void FilterApps(string query) => PopulateApps(_activeCategory);

        private void SetAllInView(bool check)
        {
            foreach (var tile in _tiles.Values) tile.IsChecked = check;
            UpdateSelectionCount();
        }

        // ── Bottom bar ────────────────────────────────────────────────────────
        private void BuildBottomBar()
        {
            _bottomBar = new Panel { BackColor = Theme.SURFACE };
            _bottomBar.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER, 1);
                e.Graphics.DrawLine(pen, 0, 0, _bottomBar.Width, 0);
            };

            var folderLbl = new Label
            {
                Text      = "// SAVE TO",
                ForeColor = Theme.MUTED,
                Font      = new Font(Theme.MonoFont, 7f, FontStyle.Bold),
                AutoSize  = true,
                Location  = new Point(Dpi.S(16), Dpi.S(14))
            };

            _folderBox = new TextBox
            {
                Text        = AppPaths.DefaultDownloadFolder,
                BackColor   = Theme.CARD,
                ForeColor   = Theme.TEXT_PRI,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font(Theme.MonoFont, 8.5f),
                Size        = new Size(Dpi.S(320), Dpi.S(24)),
                Location    = new Point(Dpi.S(110), Dpi.S(11))
            };

            _browseBtn = CreateGhostBtn("BROWSE", Theme.TEXT_SEC, Dpi.S(70));
            _browseBtn.Size     = new Size(Dpi.S(70), Dpi.S(24));
            _browseBtn.Location = new Point(Dpi.S(438), Dpi.S(11));
            _browseBtn.Click += (s, e) =>
            {
                using var dlg = new FolderBrowserDialog { InitialDirectory = _folderBox.Text };
                if (dlg.ShowDialog() == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
            };

            _preferWingetChk = new CheckBox
            {
                Text      = "prefer winget",
                ForeColor = Theme.MUTED,
                Font      = new Font(Theme.MonoFont, 7.5f),
                Checked   = _dm.WingetAvailable,
                Enabled   = _dm.WingetAvailable,
                AutoSize  = true,
                Location  = new Point(Dpi.S(524), Dpi.S(14))
            };

            _overallProgress = new ProgressBar
            {
                Size     = new Size(Dpi.S(460), Dpi.S(6)),
                Location = new Point(Dpi.S(16), Dpi.S(50)),
                Style    = ProgressBarStyle.Continuous,
                Minimum  = 0, Maximum = 100, Value = 0
            };

            _statusLabel = new Label
            {
                Text      = "ready.",
                ForeColor = Theme.MUTED,
                Font      = new Font(Theme.MonoFont, 7.5f),
                AutoSize  = true,
                Location  = new Point(Dpi.S(16), Dpi.S(62))
            };

            _selectionCountLabel = new Label
            {
                ForeColor = Theme.ACCENT,
                Font      = new Font(Theme.MonoFont, 8f, FontStyle.Bold),
                AutoSize  = true,
                Location  = new Point(Dpi.S(490), Dpi.S(52))
            };

            _clearBtn = new Button
            {
                Text      = "✗ CLEAR",
                Size      = new Size(Dpi.S(100), Dpi.S(36)),
                BackColor = Theme.SURFACE2,
                ForeColor = Theme.TEXT_SEC,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 7.5f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            _clearBtn.FlatAppearance.BorderColor = Theme.BORDER2;
            _clearBtn.FlatAppearance.BorderSize  = 1;
            _clearBtn.Click += (s, e) =>
            {
                foreach (var tile in _tiles.Values) tile.IsChecked = false;
                UpdateSelectionCount();
            };

            _installBtn = new Button
            {
                Text      = "⬇  INSTALL",
                Size      = new Size(Dpi.S(160), Dpi.S(36)),
                BackColor = Theme.ACCENT,
                ForeColor = Theme.ACCENT_TEXT,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 9f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            _installBtn.FlatAppearance.BorderSize = 0;
            _installBtn.Click += OnInstallClicked;

            _cancelBtn = new Button
            {
                Text      = "✗ CANCEL",
                Size      = new Size(Dpi.S(110), Dpi.S(36)),
                BackColor = Theme.DANGER,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 7.5f, FontStyle.Bold),
                Cursor    = Cursors.Hand,
                Visible   = false
            };
            _cancelBtn.FlatAppearance.BorderSize = 0;
            _cancelBtn.Click += (s, e) => _cts?.Cancel();

            _logToggle = new Button
            {
                Text      = "// LOG",
                Size      = new Size(Dpi.S(65), Dpi.S(24)),
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 7f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            _logToggle.FlatAppearance.BorderColor = Theme.BORDER;
            _logToggle.FlatAppearance.BorderSize  = 1;
            _logToggle.Click += (s, e) => ToggleLog();

            _bottomBar.Controls.AddRange(new Control[] {
                folderLbl, _folderBox, _browseBtn, _preferWingetChk,
                _overallProgress, _statusLabel, _selectionCountLabel,
                _clearBtn, _cancelBtn, _installBtn, _logToggle
            });

            _logBox = new RichTextBox
            {
                BackColor   = Theme.BG,
                ForeColor   = Theme.ACCENT,
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                Font        = new Font(Theme.MonoFont, 8.5f),
                Dock        = DockStyle.Fill,
                ScrollBars  = RichTextBoxScrollBars.Vertical
            };

            _logPanel = new Panel { BackColor = Theme.BG, Visible = false, Height = Dpi.S(160) };

            var logClose = new Button
            {
                Text      = "✗ CLOSE",
                Size      = new Size(Dpi.S(75), Dpi.S(22)),
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 6.5f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            logClose.FlatAppearance.BorderSize = 0;
            logClose.Click += (s, e) => ToggleLog();

            var logOpenFolder = new Button
            {
                Text      = "📁 OPEN LOGS",
                Size      = new Size(Dpi.S(100), Dpi.S(22)),
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Theme.MonoFont, 6.5f, FontStyle.Bold),
                Cursor    = Cursors.Hand,
                Location  = new Point(Dpi.S(8), Dpi.S(4))
            };
            logOpenFolder.FlatAppearance.BorderSize = 0;
            logOpenFolder.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogsDir);
                    Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true });
                }
                catch (Exception ex) { SessionLog.Write("OPEN-LOGS", ex); }
            };

            _logPanel.Controls.Add(_logBox);
            _logPanel.Controls.Add(logClose);
            _logPanel.Controls.Add(logOpenFolder);
            logClose.BringToFront(); logOpenFolder.BringToFront();
            this.Controls.Add(_logPanel);
        }

        private void ToggleLog()
        {
            _logPanel.Visible = !_logPanel.Visible;
            LayoutPanels();
            if (_logPanel.Visible)
            {
                foreach (Control c in _logPanel.Controls)
                    if (c is Button b && b.Text.StartsWith("✗")) b.Location = new Point(_logPanel.Width - Dpi.S(80), Dpi.S(4));
                _logPanel.BringToFront();
            }
        }

        // ── Install ───────────────────────────────────────────────────────────

        private async void OnInstallClicked(object sender, EventArgs e)
        {
            if (_isInstalling) return;

            var selected = _tiles.Where(kv => kv.Value.IsChecked).Select(kv => kv.Key).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select at least one app to install.", "Nothing selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string folder = _folderBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex)
                {
                    SessionLog.Write("FOLDER", ex);
                    MessageBox.Show($"Cannot create folder:\n{folder}\n\n{ex.Message}", "Invalid path",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            bool preferWinget = _preferWingetChk.Checked;
            await WaitForBackgroundScanAsync();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetInstallingUi(true, "⏳ Installing...", selected.Count);
            Log($"[START] Installing {selected.Count} app(s) — {DateTime.Now:HH:mm:ss}");

            var results = await _dm.InstallAllAsync(selected, folder, preferWinget, OnAppProgress, OnOverallProgress, token);
            SetInstallingUi(false, "⬇  INSTALL", 0);
            ReportBatch(results, selected.Count);

            if (token.IsCancellationRequested || _closing) return;

            var pendingResults = results;
            while (!_closing)
            {
                using var summary = new SummaryForm(pendingResults);
                var dr = summary.ShowDialog(this);
                if (dr != DialogResult.Retry || summary.FailedResults.Count == 0) break;

                var retryApps = summary.FailedResults.Select(r => r.App).ToList();
                Log($"[RETRY] Retrying {retryApps.Count} failed app(s)...");

                _cts = new CancellationTokenSource();
                SetInstallingUi(true, "⏳ Retrying...", retryApps.Count);
                pendingResults = await _dm.InstallAllAsync(retryApps, folder, preferWinget, OnAppProgress, OnOverallProgress, _cts.Token);
                SetInstallingUi(false, "⬇  INSTALL", 0);
                ReportBatch(pendingResults, retryApps.Count);
            }
        }

        private void SetInstallingUi(bool installing, string installBtnText, int max)
        {
            _isInstalling       = installing;
            _installBtn.Enabled = !installing;
            _installBtn.Text    = installBtnText;
            _cancelBtn.Visible  = installing;
            if (installing) { _overallProgress.Value = 0; _overallProgress.Maximum = Math.Max(1, max); }
            else            { _cts?.Dispose(); _cts = null; }
        }

        private void ReportBatch(List<InstallResult> results, int attempted)
        {
            int ok      = results.Count(r => r.Status == InstallStatus.Success);
            int fail    = results.Count(r => r.Status == InstallStatus.Failed);
            int skipped = results.Count(r => r.Status == InstallStatus.Skipped);
            bool reboot = results.Any(r => r.RebootRequired);

            string doneMsg = $"Done — {ok} succeeded, {fail} failed";
            if (skipped > 0) doneMsg += $", {skipped} skipped";
            if (reboot)      doneMsg += " — restart required";
            _statusLabel.Text = doneMsg + ".";
            Log($"[DONE] {ok}/{attempted} succeeded{(reboot ? " (restart required)" : "")} — {DateTime.Now:HH:mm:ss}");
        }

        private void OnAppProgress(AppEntry app, InstallStatus status, string msg) => Ui(() =>
        {
            if (_tiles.TryGetValue(app, out var tile))
            {
                tile.SetStatus(status);
                tile.AppendLog(msg);
                int pct = ParsePercent(msg);
                if (pct >= 0 && (status == InstallStatus.Installing || status == InstallStatus.Downloading))
                    tile.SetProgress(pct);
            }
            _statusLabel.Text = $"{app.Name}: {msg}";
            Log($"[{app.Name}] {msg}");
        });

        private void OnOverallProgress(int done, int total) =>
            Ui(() => _overallProgress.Value = Math.Min(done, _overallProgress.Maximum));

        private void UpdateSelectionCount()
        {
            int count = _tiles.Values.Count(t => t.IsChecked);
            if (_selectionCountLabel != null)
                _selectionCountLabel.Text = count == 0 ? "no apps selected" : $"{count} app{(count == 1 ? "" : "s")} selected";
            UpdateSidebarCounts();

            // Badge only after startup so pre-checked defaults don't flash.
            if (_initialized && IsHandleCreated)
                TaskbarBadge.SetCount(this.Handle, count);
        }

        // In-app log panel, teed to the persistent session log.
        private void Log(string msg)
        {
            SessionLog.Write(msg);
            if (_logBox == null) return;
            Ui(() =>
            {
                _logBox.AppendText(msg + "\n");
                _logBox.ScrollToCaret();
            });
        }

        private static int ParsePercent(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return -1;
            int idx = msg.IndexOf('%');
            if (idx <= 0) return -1;
            int end = idx - 1;
            while (end >= 0 && msg[end] == ' ') end--;
            int start = end;
            while (start > 0 && char.IsDigit(msg[start - 1])) start--;
            if (start > end) return -1;
            if (int.TryParse(msg.Substring(start, end - start + 1), out int pct))
                return Math.Clamp(pct, 0, 100);
            return -1;
        }
    }
}
