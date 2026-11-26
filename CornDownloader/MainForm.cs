using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        private Button          _logClose;
        private TextBox         _searchBox;
        private Label           _wingetBadge;
        private LinkLabel       _updateLink;
        private TextBox         _folderBox;
        private CheckBox        _preferWingetChk;
        private RichTextBox     _logBox;
        private Panel           _logPanel;
        private Label           _scanStatusLabel;
        private Label           _emptyLabel;
        private CornScrollHost  _sidebarHost;     // themed scrollbars (CornScrollBar)
        private CornScrollHost  _gridHost;
        private Panel           _filterBar;       // presets row + show-filter chips row
        private FlowLayoutPanel _presetRow;
        private FlowLayoutPanel _chipRow;
        private readonly Dictionary<string, Button> _chipBtns = new();
        private string _statusFilter = "All";     // All | Installed | Updates | NotInstalled | Selected
        private bool   _installedScanDone;
        private bool   _upgradeScanDone;
        private Task   _wingetDetectTask;
        // Every DPI-dependent size/position/font in the chrome registers here so a monitor
        // change can re-run it (see DpiLayout).
        private readonly DpiLayout _dpi = new();
        // One ToolTip shared by every tile — 100+ per-tile instances leaked native windows.
        private readonly ToolTip _sharedTileTip = new ToolTip
        {
            AutoPopDelay = 8000, InitialDelay = 600, ReshowDelay = 300, ShowAlways = true
        };

        public MainForm(CliOptions cli = null)
        {
            _cli      = cli;
            _settings = SettingsManager.Load();
            // winget discovery runs in RunStartupAsync so the window shows immediately.
            _dm       = new DownloadManager(detectWinget: false) { WingetScope = cli?.Scope ?? _settings.WingetScope ?? "auto" };
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

            FormClosing += (s, e) =>
            {
                _closing = true;
                try { _cts?.Cancel(); _lifetimeCts.Cancel(); }
                catch (Exception ex) { SessionLog.Write("CLOSE", ex); }   // a cancellation callback threw
                SaveSettings();
                _sharedTileTip.Dispose();
            };

            // Ctrl+F jumps to search, Ctrl+A selects everything in view, Ctrl+Enter installs.
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.F) { _searchBox.Focus(); _searchBox.SelectAll(); e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.Enter && !_isInstalling) { _installBtn.PerformClick(); e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.A && !(ActiveControl is TextBoxBase)) { SetAllInView(true); e.SuppressKeyPress = true; }
            };

            // Also save on meaningful state changes so a crash doesn't lose settings.
            ResizeEnd += (s, e) => SaveSettings();
            _folderBox.Leave += (s, e) => SaveSettings();
            _preferWingetChk.CheckedChanged += (s, e) => SaveSettings();
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
            if (_closing || IsDisposed) return;
            try
            {
                // Before the handle exists there is no other thread to marshal from — run inline.
                if (IsHandleCreated && InvokeRequired) BeginInvoke(action);
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
                _wingetDetectTask = DetectWingetAsync();   // assigned before the first await: Install waits on it
                await _wingetDetectTask;
                await RefreshWingetSourcesAsync(ct);
                await ScanInstalledAsync(ct);
                _upgradeScanTask = ScanUpgradesAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SessionLog.Write("STARTUP", ex); }
            _initialized = true;
            Ui(UpdateSelectionCount);   // now safe to push the real badge
        }

        private async Task DetectWingetAsync()
        {
            try { await _dm.DetectWingetAsync(); }
            catch (Exception ex) { SessionLog.Write("WINGET-DETECT", ex); }
            Ui(OnWingetDetected);
        }

        private void OnWingetDetected()
        {
            UpdateWingetBadge();
            _preferWingetChk.Enabled = _dm.WingetAvailable;
            _preferWingetChk.Checked = _settings.PreferWinget && _dm.WingetAvailable && !(_cli?.NoWinget ?? false);
            if (!_dm.WingetAvailable)
            {
                SetScanStatus("", Theme.MUTED);
                _installedScanDone = _upgradeScanDone = true;   // nothing to wait for
                RefreshFilterChips();
            }
            LayoutPanels();
            Log(_dm.WingetAvailable ? "[WINGET] detected" : "[WINGET] not found — direct URLs only");
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
            if (_settings.WindowWidth > 0 && _settings.WindowHeight > 0)
            {
                // Saved sizes are logical (96-DPI) pixels so they mean the same thing on a
                // 100% and a 150% monitor. Pre-1.4 settings stored raw pixels.
                var saved = _settings.SizeIsLogical
                    ? new Size(Dpi.S(_settings.WindowWidth), Dpi.S(_settings.WindowHeight))
                    : new Size(_settings.WindowWidth, _settings.WindowHeight);
                var work = Screen.FromPoint(Cursor.Position).WorkingArea;
                this.Size = new Size(Math.Clamp(saved.Width,  MinimumSize.Width,  Math.Max(MinimumSize.Width,  work.Width)),
                                     Math.Clamp(saved.Height, MinimumSize.Height, Math.Max(MinimumSize.Height, work.Height)));
            }
            if (_settings.WindowState == "Maximized")
                this.WindowState = FormWindowState.Maximized;

            string folder = _cli?.Folder ?? _settings.DownloadFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                folder = AppPaths.DefaultDownloadFolder;
            if (_folderBox != null) _folderBox.Text = folder;

            // The prefer-winget checkbox is set in OnWingetDetected once discovery finishes.
        }

        private void SaveSettings()
        {
            // Command-line overrides (--folder, --no-winget) apply to this run only, and a
            // launch where winget wasn't found must not flip the saved preference off.
            if (_folderBox != null && _cli?.Folder == null)
                _settings.DownloadFolder = _folderBox.Text.Trim();
            if (_preferWingetChk != null && _preferWingetChk.Enabled && !(_cli?.NoWinget ?? false))
                _settings.PreferWinget = _preferWingetChk.Checked;
            _settings.WindowState    = this.WindowState == FormWindowState.Maximized ? "Maximized" : "Normal";
            if (this.WindowState == FormWindowState.Normal)
            {
                _settings.WindowWidth   = (int)Math.Round(this.Width  / Dpi.Scale);
                _settings.WindowHeight  = (int)Math.Round(this.Height / Dpi.Scale);
                _settings.SizeIsLogical = true;
            }
            SettingsManager.Save(_settings);
        }

        // ── Winget scans ──────────────────────────────────────────────────────

        private void UpdateInstalledSummary()
        {
            int ic = _installedCache.Values.Count(v => v);
            SetScanStatus($"✔ {ic}/{AppCatalog.All.Count} apps installed", Theme.SUCCESS);
        }

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

                _installedScanDone = true;
                if (installedIds.Count == 0)
                    SetScanStatus("⚠ installed scan returned nothing (winget slow?) — see log", Theme.WARNING);
                else
                    UpdateInstalledSummary();
                UpdateSelectionCount();
                if (_statusFilter is "Installed" or "NotInstalled") PopulateApps(_activeCategory);
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
                foreach (var kv in _tiles)
                    if (_upgradeCache.TryGetValue(kv.Key, out bool u)) kv.Value.SetHasUpdate(u);
                _upgradeScanDone = true;
                RefreshUpgradeButton();
                RefreshFilterChips();
                if (_statusFilter == "Updates") PopulateApps(_activeCategory);
            });
        }

        private void RefreshUpgradeButton()
        {
            if (_upgradeBtn == null) return;
            int count = _upgradeCache.Values.Count(v => v);
            _upgradeBtn.Visible = count > 0;
            _upgradeBtn.Enabled = count > 0 && !_isInstalling;
            _upgradeBtn.Text    = count > 0 ? $"⬆  Update {count} App{(count == 1 ? "" : "s")}" : "";
            LayoutPanels();
            RefreshFilterChips();
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

            _isInstalling = true;   // claim the slot before the first await so a second click can't start another batch
            _upgradeBtn.Enabled = false;
            await WaitForBackgroundScanAsync();
            if (_closing || IsDisposed) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _upgradeBtn.Enabled = false; _upgradeBtn.Text = "⏳ Updating...";
            _installBtn.Enabled = false;
            _cancelBtn.Enabled = true;
            _cancelBtn.Visible = true;
            _overallProgress.Maximum = toUpgrade.Count; _overallProgress.Value = 0;
            int done = 0;
            Log($"[UPGRADE] Upgrading {toUpgrade.Count} app(s) — {DateTime.Now:HH:mm:ss}");

            var results = new List<InstallResult>();
            try
            {
                foreach (var app in toUpgrade)
                {
                    if (token.IsCancellationRequested) break;
                    var result = await _dm.UpgradeAsync(app,
                        msg => Ui(() =>
                        {
                            _statusLabel.Text = $"{app.Name}: {msg}";
                            Log($"[{app.Name}] {msg}");
                            if (_tiles.TryGetValue(app, out var t)) { t.SetStatus(InstallStatus.Installing); t.AppendLog(msg); }
                        }),
                        token);
                    results.Add(result);
                    done++;
                    Ui(() =>
                    {
                        _overallProgress.Value = done;
                        if (_tiles.TryGetValue(app, out var tile))
                        {
                            tile.SetStatus(result.Status);
                            if (result.Status == InstallStatus.Success) tile.SetHasUpdate(false);
                        }
                        if (result.Status == InstallStatus.Success) _upgradeCache[app] = false;
                    });
                }
            }
            catch (Exception ex)
            {
                SessionLog.Write("UPGRADE", ex);
                Log($"[UPGRADE] aborted: {ex.Message}");
            }
            finally
            {
                _isInstalling = false; _installBtn.Enabled = true;
                _cancelBtn.Visible = false;
                _cts?.Dispose(); _cts = null;
            }
            if (_closing || IsDisposed) return;   // window closed mid-upgrade — nothing left to update

            int ok = results.Count(r => r.Status == InstallStatus.Success);
            int fail = results.Count(r => r.Status == InstallStatus.Failed);
            _statusLabel.Text = $"Updates done — {ok} succeeded, {fail} failed.";
            Log($"[UPGRADE DONE] {ok}/{toUpgrade.Count} — {DateTime.Now:HH:mm:ss}");

            RefreshUpgradeButton();

            if (!token.IsCancellationRequested)
            {
                using var summary = new SummaryForm(results, allowRetry: false);
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

                ApplyPack(pack, Path.GetFileName(dlg.FileName), showMissing: true);
            }
            catch (Exception ex)
            {
                SessionLog.Write("IMPORT", ex);
                MessageBox.Show($"Import failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Shared by Import and the preset strip: replace the selection with the pack's apps.
        private void ApplyPack(SelectionPack pack, string label, bool showMissing)
        {
            foreach (var tile in _tiles.Values) tile.IsChecked = false;

            int matched = 0, alreadyInstalled = 0;
            var missingNames = new List<string>();
            foreach (var packed in pack.Apps)
            {
                var entry = AppCatalog.Find(packed.Id, packed.Name);
                if (entry == null || !_tiles.TryGetValue(entry, out var tile)) { missingNames.Add(packed.Name ?? packed.Id); continue; }
                if (_installedCache.TryGetValue(entry, out bool inst) && inst) { alreadyInstalled++; continue; }

                tile.IsChecked = true;
                // PinnedVersion is session-scoped: it lives on the catalog entry until restart.
                // A pack without a pin means "latest", so clear any pin left from earlier.
                entry.PinnedVersion = string.IsNullOrEmpty(packed.PinnedVersion) ? null : packed.PinnedVersion;
                tile.SetPinnedVersion(entry.PinnedVersion);
                if (tile.IsChecked) matched++;
            }

            UpdateSelectionCount();
            _statusLabel.Text = $"✔ {label}: {matched} app{(matched == 1 ? "" : "s")} selected" +
                                (alreadyInstalled > 0 ? $" ({alreadyInstalled} already installed)." : ".");
            Log($"[PACK] {label}: {matched}/{pack.Apps.Count} selected, {alreadyInstalled} installed, {missingNames.Count} missing");
            if (missingNames.Count > 0) Log($"[PACK] not in catalog: {string.Join(", ", missingNames)}");

            // Show exactly what was picked.
            if (matched > 0) { SelectCategory("All"); SetStatusFilter("Selected"); }

            if (showMissing && missingNames.Count > 0)
                MessageBox.Show(this,
                    $"{missingNames.Count} app{(missingNames.Count == 1 ? "" : "s")} in the pack weren't found in the catalog (may have been removed or renamed).",
                    "Partial import", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ── Uninstall ─────────────────────────────────────────────────────────

        private async void OnUninstallRequested(AppEntry app, AppTile tile)
        {
            if (_isInstalling) { _statusLabel.Text = "⏳ Wait for the current batch to finish before uninstalling."; return; }
            if (!_dm.WingetAvailable || string.IsNullOrEmpty(app.WingetId)) return;

            var answer = MessageBox.Show(this,
                $"Uninstall {app.Name}?\n\nwinget runs the app's own uninstaller silently. Settings or data the app keeps may be removed too.",
                "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _isInstalling = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetInstallingUi(true, "⏳ Uninstalling...", 1);
            tile.ShowNote("⏳ Uninstalling...", Theme.WARNING);
            Log($"[UNINSTALL] {app.Name} ({app.WingetId}) — {DateTime.Now:HH:mm:ss}");

            InstallResult result;
            try
            {
                result = await _dm.UninstallAsync(app,
                    msg => Ui(() =>
                    {
                        _statusLabel.Text = $"{app.Name}: {msg}";
                        Log($"[{app.Name}] {msg}");
                        tile.AppendLog(msg);
                    }),
                    token);
            }
            catch (Exception ex)
            {
                SessionLog.Write("UNINSTALL", ex);
                result = new InstallResult { App = app, Status = InstallStatus.Failed, Message = ex.Message };
            }
            finally
            {
                SetInstallingUi(false, "⬇  INSTALL", 0);
            }
            if (_closing || IsDisposed) return;

            _overallProgress.Value = _overallProgress.Maximum;
            switch (result.Status)
            {
                case InstallStatus.Success:
                    _installedCache[app] = false;
                    _upgradeCache[app]   = false;
                    tile.SetInstalled(false);
                    tile.ShowNote(result.RebootRequired ? "⟳ Uninstalled (restart)" : "○ Uninstalled", Theme.MUTED);
                    if (_installedScanDone) UpdateInstalledSummary();
                    RefreshUpgradeButton();
                    _statusLabel.Text = $"✔ {result.Message}" + (result.RebootRequired ? " Restart Windows to finish." : "");
                    if (_statusFilter is "Installed" or "Updates") PopulateApps(_activeCategory);
                    break;
                case InstallStatus.Skipped:
                    tile.SetInstalled(true);
                    _statusLabel.Text = $"⊘ Uninstall of {app.Name} cancelled.";
                    break;
                default:
                    tile.SetInstalled(true);
                    tile.ShowNote("✘ Uninstall failed", Theme.DANGER);
                    _statusLabel.Text = $"✘ Couldn't uninstall {app.Name}: {result.Message}";
                    break;
            }
            Log($"[UNINSTALL] {app.Name}: {result.Status} — {result.Message}");
            UpdateSelectionCount();
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
            _dpi.Add(() =>
            {
                this.MinimumSize = new Size(Dpi.S(900), Dpi.S(600));
                this.Font        = Theme.Font(Theme.MonoFont, 8.5f);
            });
            this.BackColor       = Theme.BG;
            this.ForeColor       = Theme.TEXT_PRI;
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;

            BuildTopBar();
            BuildSidebar();
            BuildMainArea();
            BuildBottomBar();

            this.Controls.AddRange(new Control[] { _topBar, _sidebarHost, _mainArea, _bottomBar });
            DarkTitleBar.Apply(this, Theme.SURFACE, Theme.TEXT_PRI, Theme.BORDER);
            this.Resize += (s, e) => LayoutPanels();
            LayoutPanels();

            DpiLayout.Attach(this, RescaleForDpi);
        }

        // Runs after the window moves to a monitor with a different scale factor (or opens
        // on one). Re-applies every registered chrome metric, re-scales the cached tiles,
        // and rebuilds the section headers at the new size.
        private void RescaleForDpi()
        {
            // Absolute child positions in an AutoScroll panel are relative to the current
            // scroll offset — scroll home first so re-applied Locations land where intended.
            _sidebar.AutoScrollPosition = Point.Empty;
            _dpi.Reapply();
            foreach (var tile in _tiles.Values) tile.ApplyDpi();
            foreach (var row in new Control[] { _presetRow, _chipRow })
                foreach (Control c in row.Controls) ApplyChipMetrics(c);
            LayoutFilterBar();
            PopulateApps(_activeCategory);
            LayoutPanels();
            _sidebarHost.LayoutContent();
            _gridHost.LayoutContent();
            TaskbarBadge.SetCount(IsHandleCreated ? Handle : IntPtr.Zero, _initialized ? _tiles.Values.Count(t => t.IsChecked) : 0);
            try { this.Icon = AppIconBuilder.Get(); } catch (Exception ex) { SessionLog.Write("ICON", ex); }
        }

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
            _sidebarHost.SetBounds(0, topH, sideW, contentH);
            _mainArea.SetBounds(sideW, topH, w - sideW, contentH);

            if (_logPanel != null)
            {
                _logPanel.SetBounds(0, topH + contentH, w, _logPanel.Height);
                _logClose.Location = new Point(_logPanel.Width - Dpi.S(80), Dpi.S(4));
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

                // Status text gets whatever is left of the row and ellipsises past that.
                int statusRight = (_cancelBtn ?? _clearBtn).Left - Dpi.S(12);
                _statusLabel.Width = Math.Max(Dpi.S(120), statusRight - _statusLabel.Left);
            }

            if (_updateLink != null && _upgradeBtn != null)
            {
                _updateLink.Location = new Point(_topBar.Width - Dpi.S(16) - _updateLink.Width, Dpi.S(22));

                // Keep the upgrade button clear of the update link on narrow windows.
                int rightEdge = _updateLink.Visible ? _updateLink.Left - Dpi.S(12) : _topBar.Width - Dpi.S(16);
                _upgradeBtn.Left = Math.Max(_wingetBadge.Right + Dpi.S(12),
                                            Math.Min(Dpi.S(700), rightEdge - _upgradeBtn.Width));
            }
        }

        // ── Top bar ───────────────────────────────────────────────────────────
        private void BuildTopBar()
        {
            _topBar = new Panel { BackColor = Theme.SURFACE, Dock = DockStyle.None };

            var accentBar = new Panel { BackColor = Theme.ACCENT };
            _dpi.Add(() => accentBar.SetBounds(Dpi.S(14), Dpi.S(13), Dpi.S(3), Dpi.S(34)));

            var titleLbl = new Label
            {
                Text      = "🌽  CORN_DOWNLOADER",
                ForeColor = Theme.ACCENT,
                AutoSize  = true,
                UseMnemonic = false
            };
            _dpi.Add(() =>
            {
                titleLbl.Font     = Theme.Font(Theme.MonoFont, 9.5f, FontStyle.Bold);
                titleLbl.Location = new Point(Dpi.S(24), Dpi.S(19));
            });

            _searchBox = new TextBox
            {
                PlaceholderText = "  search apps...  (Ctrl+F)",
                BackColor       = Theme.CARD,
                ForeColor       = Theme.TEXT_PRI,
                BorderStyle     = BorderStyle.FixedSingle
            };
            _dpi.Add(() =>
            {
                _searchBox.Font     = Theme.Font(Theme.MonoFont, 9f);
                // Keep the box clear of the title at any scale (the title is AutoSize).
                _searchBox.Location = new Point(Math.Max(Dpi.S(255), titleLbl.Right + Dpi.S(16)), Dpi.S(16));
                _searchBox.Width    = Dpi.S(240);
            });
            // Debounced: re-filtering ~170 tiles on every keystroke made typing stutter.
            var searchDebounce = new System.Windows.Forms.Timer { Interval = 180 };
            searchDebounce.Tick += (s, e) => { searchDebounce.Stop(); PopulateApps(_activeCategory); };
            _searchBox.TextChanged += (s, e) => { searchDebounce.Stop(); searchDebounce.Start(); };
            _searchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { _searchBox.Clear(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { searchDebounce.Stop(); PopulateApps(_activeCategory); e.SuppressKeyPress = true; }
            };
            FormClosed += (s, e) => searchDebounce.Dispose();

            _wingetBadge = new Label { AutoSize = true };
            _dpi.Add(() =>
            {
                _wingetBadge.Font     = Theme.Font(Theme.MonoFont, 7.5f);
                _wingetBadge.Location = new Point(_searchBox.Right + Dpi.S(15), Dpi.S(21));
            });
            UpdateWingetBadge();

            _upgradeBtn = new Button
            {
                Text      = "⬆  UPDATES AVAILABLE",
                AutoSize  = true,
                BackColor = Theme.METEOR,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Visible   = false,
                Cursor    = Cursors.Hand
            };
            _dpi.Add(() =>
            {
                _upgradeBtn.Font     = Theme.Font(Theme.MonoFont, 7.5f, FontStyle.Bold);
                _upgradeBtn.Location = new Point(Dpi.S(700), Dpi.S(14));
                _upgradeBtn.Height   = Dpi.S(30);
            });
            _upgradeBtn.FlatAppearance.BorderSize = 0;
            _upgradeBtn.Click += OnUpgradeClicked;

            _updateLink = new LinkLabel
            {
                AutoSize         = true,
                LinkColor        = Theme.ACCENT,
                ActiveLinkColor  = Theme.ACCENT_DIM,
                VisitedLinkColor = Theme.ACCENT,
                LinkBehavior     = LinkBehavior.HoverUnderline,
                BackColor        = Color.Transparent,
                Visible          = false
            };
            // Positioned by LayoutPanels; an Anchor here fought it on every resize.
            _dpi.Add(() => _updateLink.Font = Theme.Font(Theme.MonoFont, 7.5f, FontStyle.Bold));
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
            if (!_dm.WingetDetected)
            { _wingetBadge.Text = "⏳  detecting winget..."; _wingetBadge.ForeColor = Theme.TEXT_SEC; }
            else if (_dm.WingetAvailable)
            { _wingetBadge.Text = "✦  winget detected"; _wingetBadge.ForeColor = Theme.SUCCESS; }
            else
            { _wingetBadge.Text = "⚠  winget not found — direct URLs only"; _wingetBadge.ForeColor = Theme.METEOR; }
        }

        // ── Sidebar ───────────────────────────────────────────────────────────
        private void BuildSidebar()
        {
            // AutoScroll: with 9 categories the buttons + scan status no longer fit the
            // sidebar at the minimum window height (or at 150%+ scaling) and were clipped.
            _sidebar = new Panel { BackColor = Theme.SURFACE, AutoScroll = true };
            // Vertical scrolling only: the vertical bar eats ~17px, which would otherwise
            // push the 194px-wide buttons past the edge and add a pointless horizontal bar.
            _sidebar.HorizontalScroll.Enabled = false;
            _sidebar.HorizontalScroll.Visible = false;
            _sidebar.HorizontalScroll.Maximum = 0;

            var sideHeader = new Label
            {
                Text      = "// CATEGORIES",
                ForeColor = Theme.MUTED,
                AutoSize  = true,
                BackColor = Color.Transparent
            };
            _dpi.Add(() =>
            {
                sideHeader.Font     = Theme.Font(Theme.MonoFont, 6.5f, FontStyle.Bold);
                sideHeader.Location = new Point(Dpi.S(12), Dpi.S(12));
            });
            _sidebar.Controls.Add(sideHeader);

            var catBtns = new List<Button>();
            foreach (var cat in _categories)
            {
                var btn = CreateSidebarBtn(cat);
                catBtns.Add(btn);
                _sidebar.Controls.Add(btn);
            }

            var divider  = new Panel { BackColor = Theme.BORDER };
            _sidebar.Controls.Add(divider);

            var selAll = CreateSmallBtn("✦ ALL", Theme.ACCENT);
            selAll.ForeColor = Theme.ACCENT_TEXT;
            selAll.Click    += (s, e) => SetAllInView(true);

            var deselAll = CreateSmallBtn("✗ NONE", Theme.SURFACE2);
            deselAll.ForeColor = Theme.TEXT_SEC;
            deselAll.Click    += (s, e) => SetAllInView(false);

            var recBtn = new Button
            {
                Text      = "★  RECOMMENDED",
                BackColor = Theme.ACCENT,
                ForeColor = Theme.ACCENT_TEXT,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            recBtn.FlatAppearance.BorderSize = 0;
            recBtn.Click += (s, e) =>
            {
                foreach (var kv in _tiles) kv.Value.IsChecked = kv.Key.IsRecommended;
                UpdateSelectionCount();
            };

            var exportBtn = CreateGhostBtn("⬆ EXPORT", Theme.ACCENT, 92);
            exportBtn.Click += (s, e) => ExportSelections();

            var importBtn = CreateGhostBtn("⬇ IMPORT", Theme.TEXT_SEC, 96);
            importBtn.Click += (s, e) => ImportSelections();

            _scanStatusLabel = new Label
            {
                Text      = "⏳ detecting winget...",
                ForeColor = Theme.MUTED,
                AutoSize  = false,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // One step lays out the whole column so every row moves together at a new scale.
            _dpi.Add(() =>
            {
                int y = Dpi.S(34);
                foreach (var btn in catBtns)
                {
                    btn.SetBounds(Dpi.S(8), y, Dpi.S(194), Dpi.S(32));
                    y += Dpi.S(38);
                }
                divider.SetBounds(Dpi.S(12), y + Dpi.S(6), Dpi.S(178), Math.Max(1, Dpi.S(1)));
                y += Dpi.S(14);

                selAll.SetBounds(Dpi.S(8), y + Dpi.S(6), Dpi.S(92), Dpi.S(28));
                deselAll.SetBounds(Dpi.S(106), y + Dpi.S(6), Dpi.S(96), Dpi.S(28));
                recBtn.SetBounds(Dpi.S(8), y + Dpi.S(42), Dpi.S(194), Dpi.S(32));
                recBtn.Font = Theme.Font(Theme.MonoFont, 7f, FontStyle.Bold);
                exportBtn.Location = new Point(Dpi.S(8), y + Dpi.S(82));
                importBtn.Location = new Point(Dpi.S(106), y + Dpi.S(82));
                _scanStatusLabel.Font = Theme.Font(Theme.MonoFont, 6.5f);
                _scanStatusLabel.SetBounds(Dpi.S(8), y + Dpi.S(116), Dpi.S(194), Dpi.S(36));
            });

            _sidebar.Controls.AddRange(new Control[] { selAll, deselAll, recBtn, exportBtn, importBtn, _scanStatusLabel });
            _sidebarHost = new CornScrollHost(_sidebar);
        }

        // width/height are logical (96-DPI) pixels; scaled now and on every DPI change.
        private Button CreateGhostBtn(string text, Color fg, int width, int height = 26)
        {
            var btn = new Button
            {
                Text      = text,
                BackColor = Theme.SURFACE2,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Theme.BORDER2;
            btn.FlatAppearance.BorderSize  = 1;
            _dpi.Add(() =>
            {
                btn.Size = new Size(Dpi.S(width), Dpi.S(height));
                btn.Font = Theme.Font(Theme.MonoFont, 6.5f, FontStyle.Bold);
            });
            return btn;
        }

        private Button CreateSidebarBtn(string category)
        {
            string emoji   = category == "All" ? "✦" : CategoryEmoji(category);
            string display = category switch
            {
                "Media & Entertainment"    => "MEDIA & ENTERTAIN.",
                "Utilities & System Tools" => "UTILITIES & SYS.",
                "Security & Privacy"       => "SECURITY & PRIV.",
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
                AutoEllipsis  = true,
                AutoSize      = false,
                UseMnemonic   = false,   // "MEDIA & ENTERTAIN." rendered as "MEDIA  ENTERTAIN." with an underlined E
                Cursor        = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize         = 0;
            btn.FlatAppearance.BorderColor        = active ? Theme.ACCENT : Color.FromArgb(1, Theme.BG);
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, Theme.ACCENT);

            var badge = new Label
            {
                Text      = total.ToString(),
                AutoSize  = false,
                ForeColor = Theme.MUTED,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                UseMnemonic = false
            };
            btn.Controls.Add(badge);
            // Wide enough for "12/34" once a category has a selection.
            void PositionBadge() => badge.Location = new Point(btn.Width - badge.Width - Dpi.S(4), (btn.Height - badge.Height) / 2);
            _dpi.Add(() =>
            {
                btn.Font     = Theme.Font(Theme.MonoFont, 7f, _activeCategory == category ? FontStyle.Bold : FontStyle.Regular);
                btn.Padding  = new Padding(0, 0, Dpi.S(44), 0);
                badge.Font   = Theme.Font(Theme.MonoFont, 6f, FontStyle.Bold);
                badge.Size   = new Size(Dpi.S(40), Dpi.S(14));
                PositionBadge();
            });
            btn.SizeChanged   += (s, e) => PositionBadge();
            btn.HandleCreated += (s, e) => PositionBadge();

            btn.Paint += (s, e) =>
            {
                if (_activeCategory == category)
                {
                    using var b = new SolidBrush(Theme.ACCENT);
                    e.Graphics.FillRectangle(b, 0, Dpi.S(4), Dpi.S(2), btn.Height - Dpi.S(8));
                }
            };

            btn.Click += (s, e) =>
            {
                SelectCategory(category);
                PopulateApps(category);
            };

            _sidebarBtns[category] = btn;
            return btn;
        }

        // Highlight only — callers repopulate (avoids a double rebuild when a filter changes too).
        private void SelectCategory(string category)
        {
            _activeCategory = category;
            RefreshSidebarButtons();
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
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            _dpi.Add(() => btn.Font = Theme.Font(Theme.MonoFont, 7f, FontStyle.Bold));
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
                btn.Font      = Theme.Font(Theme.MonoFont, 7f, active ? FontStyle.Bold : FontStyle.Regular);
                btn.Invalidate();
            }
        }

        // ── App grid ──────────────────────────────────────────────────────────
        // ── Presets + show-filter strip (top of the main area) ───────────────

        private void BuildFilterBar()
        {
            _filterBar = new Panel { BackColor = Theme.SURFACE, Dock = DockStyle.Top };
            _filterBar.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BORDER);
                e.Graphics.DrawLine(pen, 0, _filterBar.Height - 1, _filterBar.Width, _filterBar.Height - 1);
            };

            FlowLayoutPanel MakeRow() => new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                BackColor     = Color.Transparent,
                Dock          = DockStyle.Top
            };
            _chipRow   = MakeRow();
            _presetRow = MakeRow();
            _presetRow.WrapContents = true;   // user presets wrap onto extra lines instead of being clipped
            // Docked Top in reverse z-order: the preset row (added last) ends up on top.
            _filterBar.Controls.Add(_chipRow);
            _filterBar.Controls.Add(_presetRow);
            _dpi.Add(() =>
            {
                _presetRow.Padding = new Padding(Dpi.S(12), Dpi.S(6), 0, Dpi.S(2));
                _chipRow.Padding   = new Padding(Dpi.S(12), Dpi.S(2), 0, 0);
                LayoutFilterBar();
            });
            _filterBar.Resize += (s, e) => LayoutFilterBar();

            RebuildPresetRow();

            _chipRow.Controls.Add(MakeRowLabel("// SHOW"));
            foreach (var key in new[] { "All", "Installed", "Updates", "NotInstalled", "Selected" })
            {
                var chip = MakeChip("", Theme.TEXT_SEC);
                chip.Tag    = key;
                chip.Click += (s, e) => SetStatusFilter(key);
                _chipBtns[key] = chip;
                _chipRow.Controls.Add(chip);
            }
            RefreshFilterChips();
        }

        private void RebuildPresetRow()
        {
            _presetRow.SuspendLayout();
            foreach (Control c in _presetRow.Controls.Cast<Control>().ToList()) c.Dispose();
            _presetRow.Controls.Clear();

            _presetRow.Controls.Add(MakeRowLabel("// PRESETS"));
            foreach (var preset in Presets.BuiltIn.Concat(Presets.LoadUser()))
            {
                var btn = MakeChip($"{preset.Emoji} {preset.Name.ToUpperInvariant()}", preset.IsUser ? Theme.TEXT_PRI : Theme.ACCENT);
                var names = preset.Pack.Apps
                    .Select(p => AppCatalog.Find(p.Id, p.Name)?.Name ?? p.Name ?? p.Id)
                    .Where(n => !string.IsNullOrEmpty(n)).ToList();
                _sharedTileTip.SetToolTip(btn, $"{preset.Description}\n\n{string.Join(", ", names)}");
                btn.Click += (s, e) => ApplyPack(preset.Pack, $"{preset.Name} preset", showMissing: false);
                if (preset.IsUser)
                {
                    var cms = new ContextMenuStrip();
                    cms.Items.Add("Open presets folder", null, (s, e) => OpenPresetsFolder());
                    btn.ContextMenuStrip = cms;
                    btn.Disposed += (s, e) => cms.Dispose();
                }
                _presetRow.Controls.Add(btn);
            }

            var saveBtn = MakeChip("＋ SAVE", Theme.TEXT_SEC);
            _sharedTileTip.SetToolTip(saveBtn, "Save the current selection as a preset button.\nPresets are .corn packs in your presets folder — share them like any export.");
            saveBtn.Click += (s, e) => SaveSelectionAsPreset();
            _presetRow.Controls.Add(saveBtn);
            _presetRow.ResumeLayout(true);
            LayoutFilterBar();

            // Catch typos in the built-in presets early (logged, never shown to users).
            foreach (var preset in Presets.BuiltIn)
                foreach (var p in preset.Pack.Apps)
                    if (AppCatalog.Find(p.Id, p.Name) == null)
                        Log($"[PRESET] '{preset.Name}' references unknown app id '{p.Id}'");
        }

        private bool _layingFilterBar;

        // Preset row height follows how many lines its chips wrap onto at the current width.
        private void LayoutFilterBar()
        {
            if (_filterBar == null || _presetRow == null || _chipRow == null || _layingFilterBar) return;
            _layingFilterBar = true;
            try
            {
                int width   = Math.Max(Dpi.S(200), _filterBar.ClientSize.Width);
                int presetH = Math.Max(Dpi.S(36), _presetRow.GetPreferredSize(new Size(width, 0)).Height);
                _presetRow.Height = presetH;
                _chipRow.Height   = Dpi.S(34);
                _filterBar.Height = presetH + _chipRow.Height + Dpi.S(2);
            }
            finally { _layingFilterBar = false; }
        }

        private Label MakeRowLabel(string text)
        {
            var lbl = new Label { Text = text, ForeColor = Theme.MUTED, AutoSize = true, BackColor = Color.Transparent };
            ApplyChipMetrics(lbl);
            return lbl;
        }

        private Button MakeChip(string text, Color fg)
        {
            var btn = new Button
            {
                Text         = text,
                AutoSize     = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle    = FlatStyle.Flat,
                BackColor    = Theme.SURFACE2,
                ForeColor    = fg,
                Cursor       = Cursors.Hand,
                UseMnemonic  = false,
                TabStop      = false
            };
            btn.FlatAppearance.BorderColor        = Theme.BORDER2;
            btn.FlatAppearance.BorderSize         = 1;
            btn.FlatAppearance.MouseOverBackColor = Theme.CARD_HOVER;
            ApplyChipMetrics(btn);
            return btn;
        }

        // Chip buttons are rebuilt when presets change, so they're re-scaled from
        // RescaleForDpi instead of registering one DpiLayout step per (possibly disposed) button.
        private static void ApplyChipMetrics(Control c)
        {
            if (c is Label)
            {
                c.Font   = Theme.Font(Theme.MonoFont, 6.5f, FontStyle.Bold);
                c.Margin = new Padding(0, Dpi.S(7), Dpi.S(8), 0);
                return;
            }
            c.Font        = Theme.Font(Theme.MonoFont, 7f, FontStyle.Bold);
            c.Padding     = new Padding(Dpi.S(6), 0, Dpi.S(6), 0);
            c.MinimumSize = new Size(0, Dpi.S(26));
            c.Margin      = new Padding(0, 0, Dpi.S(6), 0);
        }

        private void SetStatusFilter(string key)
        {
            _statusFilter = key;
            RefreshFilterChips();
            PopulateApps(_activeCategory);
        }

        private void RefreshFilterChips()
        {
            if (_chipBtns.Count == 0) return;
            int total     = AppCatalog.All.Count;
            int installed = AppCatalog.All.Count(IsInstalled);
            int updates   = AppCatalog.All.Count(HasUpdate);
            int selected  = _tiles.Values.Count(t => t.IsChecked);
            string Pending(bool done, int n) => done ? n.ToString() : "…";

            var labels = new Dictionary<string, string>
            {
                ["All"]          = $"ALL {total}",
                ["Installed"]    = $"✔ INSTALLED {Pending(_installedScanDone, installed)}",
                ["Updates"]      = $"⬆ UPDATES {Pending(_upgradeScanDone, updates)}",
                ["NotInstalled"] = $"○ NOT INSTALLED {Pending(_installedScanDone, total - installed)}",
                ["Selected"]     = $"☑ SELECTED {selected}",
            };
            foreach (var kv in _chipBtns)
            {
                bool active = kv.Key == _statusFilter;
                var  btn    = kv.Value;
                if (btn.Text != labels[kv.Key]) btn.Text = labels[kv.Key];
                btn.BackColor = active ? Theme.ACCENT_GLOW : Theme.SURFACE2;
                btn.ForeColor = active ? Theme.ACCENT : kv.Key == "Updates" && updates > 0 ? Theme.METEOR : Theme.TEXT_SEC;
                btn.FlatAppearance.BorderColor = active ? Theme.ACCENT : Theme.BORDER2;
            }
        }

        private void SaveSelectionAsPreset()
        {
            var selected = _tiles.Where(kv => kv.Value.IsChecked).Select(kv => kv.Key).ToList();
            if (selected.Count == 0)
            {
                _statusLabel.Text = "✘ Select some apps first, then save them as a preset.";
                return;
            }
            try { Directory.CreateDirectory(AppPaths.PresetsDir); }
            catch (Exception ex) { SessionLog.Write("PRESETS", ex); }

            using var dlg = new SaveFileDialog
            {
                Title            = "Save selection as preset — the file name becomes the button label",
                Filter           = "Corn Downloader pack (*.corn)|*.corn",
                InitialDirectory = AppPaths.PresetsDir,
                FileName         = "My Setup.corn",
                OverwritePrompt  = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string name = Path.GetFileNameWithoutExtension(dlg.FileName);
                var pack = new SelectionPack
                {
                    CreatedAt = DateTime.Now.ToString("O"),
                    Apps      = selected.Select(a => new PackedApp { Id = a.Id, Name = a.Name, PinnedVersion = a.PinnedVersion }).ToList()
                };
                // Always lands in the presets folder (that's what the strip reads), whatever
                // folder the dialog was browsed to.
                string path = Presets.Save(name, pack);
                RebuildPresetRow();
                _statusLabel.Text = $"✔ Saved preset \"{name}\" ({selected.Count} apps).";
                Log($"[PRESET] saved {selected.Count} app(s) → {path}");
            }
            catch (Exception ex)
            {
                SessionLog.Write("PRESET-SAVE", ex);
                MessageBox.Show(this, $"Couldn't save the preset:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenPresetsFolder()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.PresetsDir);
                Process.Start(new ProcessStartInfo(AppPaths.PresetsDir) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) { SessionLog.Write("PRESETS", ex); }
        }

        private void BuildMainArea()
        {
            _mainArea = new Panel { BackColor = Theme.BG };
            _appGrid  = new FlowLayoutPanel
            {
                AutoScroll   = true,
                WrapContents = true,
                BackColor    = Theme.BG
            };
            _dpi.Add(() => _appGrid.Padding = new Padding(Dpi.S(12)));
            _gridHost = new CornScrollHost(_appGrid) { Dock = DockStyle.Fill };
            BuildFilterBar();
            _mainArea.Controls.Add(_gridHost);
            _mainArea.Controls.Add(_filterBar);
            _gridHost.BringToFront();   // Fill must be laid out after the Top-docked filter bar

            // Shown instead of an empty grid when a search matches nothing.
            _emptyLabel = new Label
            {
                ForeColor = Theme.MUTED,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Visible   = false,
                UseMnemonic = false
            };
            _dpi.Add(() =>
            {
                _emptyLabel.Font   = Theme.Font(Theme.MonoFont, 9f);
                _emptyLabel.Margin = new Padding(Dpi.S(8), Dpi.S(24), 0, 0);
            });
        }

        private void PopulateApps(string category)
        {
            _appGrid.SuspendLayout();
            // Headers are rebuilt on every keystroke; Clear() alone would leak their handles/fonts. Tiles are cached and reused.
            foreach (var h in _appGrid.Controls.OfType<SectionHeader>().ToList()) h.Dispose();
            _appGrid.Controls.Clear();
            _appGrid.AutoScrollPosition = Point.Empty;   // a new filter starts at the top, not mid-list

            IEnumerable<AppEntry> apps = category == "All"
                ? AppCatalog.All
                : AppCatalog.All.Where(a => a.Category == category);

            string search = _searchBox?.Text?.Trim() ?? "";
            if (search.Length > 0)
                apps = apps.Where(a => (a.Name ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                       (a.Description ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                       (a.WingetId ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                       (a.Category ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));

            apps = _statusFilter switch
            {
                "Installed"    => apps.Where(IsInstalled),
                "Updates"      => apps.Where(HasUpdate),
                "NotInstalled" => apps.Where(a => !IsInstalled(a)),
                "Selected"     => apps.Where(a => _tiles.TryGetValue(a, out var t) && t.IsChecked),
                _              => apps
            };

            var visible = apps.ToList();
            if (visible.Count == 0)
            {
                _emptyLabel.Text = search.Length > 0 ? $"// no apps match \"{search}\""
                    : _statusFilter switch
                    {
                        "Installed"    => _installedScanDone ? "// none of the catalog apps are installed" : "// still scanning installed apps...",
                        "Updates"      => _upgradeScanDone   ? "// everything is up to date ✔"          : "// still checking for updates...",
                        "NotInstalled" => "// every app here is already installed",
                        "Selected"     => "// nothing selected — tick some apps or pick a preset",
                        _              => "// nothing in this category"
                    };
                _emptyLabel.Visible = true;
                _appGrid.Controls.Add(_emptyLabel);
            }

            foreach (var group in visible.GroupBy(a => a.Category).OrderBy(g => g.Key))
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
                        var entry = app;   // capture per tile
                        tile.UninstallRequested += (s, e) => OnUninstallRequested(entry, (AppTile)s);
                        _tiles[app] = tile;
                    }
                    _appGrid.Controls.Add(tile);
                }
            }

            _appGrid.ResumeLayout(true);
        }

        private bool IsInstalled(AppEntry a) => _installedCache.TryGetValue(a, out bool v) && v;
        private bool HasUpdate(AppEntry a)   => _upgradeCache.TryGetValue(a, out bool v) && v;

        internal static string CategoryEmoji(string cat) => cat switch
        {
            "Browsers"                 => "🌐",
            "Dev Tools"                => "💻",
            "Media & Entertainment"    => "🎬",
            "Productivity"             => "📋",
            "Gaming"                   => "🎮",
            "Utilities & System Tools" => "🔧",
            "Customization"            => "🎨",
            "Security & Privacy"       => "🛡️",
            _                          => "📦"
        };

        // Acts on the tiles in the current category/search view only (CLEAR clears everything).
        private void SetAllInView(bool check)
        {
            foreach (var tile in _appGrid.Controls.OfType<AppTile>()) tile.IsChecked = check;
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
                AutoSize  = true
            };

            _folderBox = new TextBox
            {
                Text        = AppPaths.DefaultDownloadFolder,
                BackColor   = Theme.CARD,
                ForeColor   = Theme.TEXT_PRI,
                BorderStyle = BorderStyle.FixedSingle
            };

            var browseBtn = CreateGhostBtn("BROWSE", Theme.TEXT_SEC, 70, 24);
            browseBtn.Click += (s, e) =>
            {
                using var dlg = new FolderBrowserDialog { InitialDirectory = _folderBox.Text, UseDescriptionForTitle = true, Description = "Save direct-URL installers to" };
                if (dlg.ShowDialog(this) == DialogResult.OK) { _folderBox.Text = dlg.SelectedPath; SaveSettings(); }
            };

            var openFolderBtn = CreateGhostBtn("OPEN", Theme.TEXT_SEC, 50, 24);
            openFolderBtn.Click += (s, e) =>
            {
                string dir = _folderBox.Text.Trim();
                try
                {
                    if (!Directory.Exists(dir)) { _statusLabel.Text = "✘ That folder doesn't exist yet."; return; }
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex) { SessionLog.Write("OPEN-FOLDER", ex); }
            };

            _preferWingetChk = new CheckBox
            {
                Text      = "prefer winget",
                ForeColor = Theme.MUTED,
                Checked   = _dm.WingetAvailable,
                Enabled   = _dm.WingetAvailable,
                AutoSize  = true
            };

            _overallProgress = new ProgressBar
            {
                Style    = ProgressBarStyle.Continuous,
                Minimum  = 0, Maximum = 100, Value = 0
            };

            _statusLabel = new Label
            {
                Text         = "ready.",
                ForeColor    = Theme.MUTED,
                AutoSize     = false,          // fixed box + ellipsis: long winget lines used to run under the buttons
                AutoEllipsis = true,
                UseMnemonic  = false
            };

            _selectionCountLabel = new Label
            {
                ForeColor = Theme.ACCENT,
                AutoSize  = true
            };

            _dpi.Add(() =>
            {
                folderLbl.Font     = Theme.Font(Theme.MonoFont, 7f, FontStyle.Bold);
                folderLbl.Location = new Point(Dpi.S(16), Dpi.S(14));
                _folderBox.Font    = Theme.Font(Theme.MonoFont, 8.5f);
                _folderBox.SetBounds(Math.Max(Dpi.S(110), folderLbl.Right + Dpi.S(8)), Dpi.S(11), Dpi.S(320), Dpi.S(24));
                browseBtn.Location     = new Point(_folderBox.Right + Dpi.S(8), Dpi.S(11));
                openFolderBtn.Location = new Point(browseBtn.Right + Dpi.S(6), Dpi.S(11));
                _preferWingetChk.Font     = Theme.Font(Theme.MonoFont, 7.5f);
                _preferWingetChk.Location = new Point(openFolderBtn.Right + Dpi.S(16), Dpi.S(14));
                _overallProgress.SetBounds(Dpi.S(16), Dpi.S(50), Dpi.S(460), Dpi.S(6));
                _statusLabel.Font  = Theme.Font(Theme.MonoFont, 7.5f);
                _statusLabel.Location = new Point(Dpi.S(16), Dpi.S(62));
                _statusLabel.Height   = Dpi.S(40);
                _selectionCountLabel.Font     = Theme.Font(Theme.MonoFont, 8f, FontStyle.Bold);
                _selectionCountLabel.Location = new Point(_overallProgress.Right + Dpi.S(14), Dpi.S(46));
            });

            _clearBtn = new Button
            {
                Text      = "✗ CLEAR",
                BackColor = Theme.SURFACE2,
                ForeColor = Theme.TEXT_SEC,
                FlatStyle = FlatStyle.Flat,
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
                BackColor = Theme.ACCENT,
                ForeColor = Theme.ACCENT_TEXT,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _installBtn.FlatAppearance.BorderSize = 0;
            _installBtn.Click += OnInstallClicked;

            _cancelBtn = new Button
            {
                Text      = "✗ CANCEL",
                BackColor = Theme.DANGER,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand,
                Visible   = false
            };
            _cancelBtn.FlatAppearance.BorderSize = 0;
            _cancelBtn.Click += (s, e) =>
            {
                _cts?.Cancel();
                _cancelBtn.Enabled = false;   // one click is enough; re-enabled by SetInstallingUi
                _statusLabel.Text  = "⏳ Cancelling — waiting for the running installer to stop...";
            };

            _logToggle = new Button
            {
                Text      = "// LOG",
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _dpi.Add(() =>
            {
                _clearBtn.Size   = new Size(Dpi.S(100), Dpi.S(36));
                _clearBtn.Font   = Theme.Font(Theme.MonoFont, 7.5f, FontStyle.Bold);
                _installBtn.Size = new Size(Dpi.S(160), Dpi.S(36));
                _installBtn.Font = Theme.Font(Theme.MonoFont, 9f, FontStyle.Bold);
                _cancelBtn.Size  = new Size(Dpi.S(110), Dpi.S(36));
                _cancelBtn.Font  = Theme.Font(Theme.MonoFont, 7.5f, FontStyle.Bold);
                _logToggle.Size  = new Size(Dpi.S(65), Dpi.S(24));
                _logToggle.Font  = Theme.Font(Theme.MonoFont, 7f, FontStyle.Bold);
            });
            _logToggle.FlatAppearance.BorderColor = Theme.BORDER;
            _logToggle.FlatAppearance.BorderSize  = 1;
            _logToggle.Click += (s, e) => ToggleLog();

            _bottomBar.Controls.AddRange(new Control[] {
                folderLbl, _folderBox, browseBtn, openFolderBtn, _preferWingetChk,
                _overallProgress, _statusLabel, _selectionCountLabel,
                _clearBtn, _cancelBtn, _installBtn, _logToggle
            });

            _logBox = new RichTextBox
            {
                BackColor   = Theme.BG,
                ForeColor   = Theme.ACCENT,
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                Dock        = DockStyle.Fill,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                DetectUrls  = false
            };

            _logPanel = new Panel { BackColor = Theme.BG, Visible = false };

            _logClose = new Button
            {
                Text      = "✗ CLOSE",
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _logClose.FlatAppearance.BorderSize = 0;
            _logClose.Click += (s, e) => ToggleLog();

            var logOpenFolder = new Button
            {
                Text      = "📁 OPEN LOGS",
                BackColor = Color.Transparent,
                ForeColor = Theme.MUTED,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _dpi.Add(() =>
            {
                _logBox.Font     = Theme.Font(Theme.MonoFont, 8.5f);
                // Leave a strip at the top for the OPEN LOGS / CLOSE buttons instead of
                // letting them sit on top of the first log lines.
                _logPanel.Padding = new Padding(0, Dpi.S(28), 0, 0);
                _logPanel.Height  = Dpi.S(160);
                _logClose.Size    = new Size(Dpi.S(75), Dpi.S(22));
                _logClose.Font    = Theme.Font(Theme.MonoFont, 6.5f, FontStyle.Bold);
                logOpenFolder.SetBounds(Dpi.S(8), Dpi.S(4), Dpi.S(100), Dpi.S(22));
                logOpenFolder.Font = Theme.Font(Theme.MonoFont, 6.5f, FontStyle.Bold);
            });
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
            _logPanel.Controls.Add(_logClose);
            _logPanel.Controls.Add(logOpenFolder);
            _logClose.BringToFront(); logOpenFolder.BringToFront();
            Controls.Add(_logPanel);
        }

        private void ToggleLog()
        {
            _logPanel.Visible = !_logPanel.Visible;
            LayoutPanels();
            if (_logPanel.Visible) _logPanel.BringToFront();
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

            _isInstalling = true;   // claim the slot before the first await so a second click can't start another batch
            _installBtn.Enabled = false;
            if (_upgradeBtn != null) _upgradeBtn.Enabled = false;
            if (_wingetDetectTask != null && !_wingetDetectTask.IsCompleted)
            {
                _statusLabel.Text = "⏳ Waiting for winget detection...";
                try { await _wingetDetectTask; } catch (Exception ex) { SessionLog.Write("WINGET-DETECT", ex); }
            }
            // Read after detection: until then the checkbox is disabled and unticked.
            bool preferWinget = _preferWingetChk.Checked;
            await WaitForBackgroundScanAsync();
            if (_closing || IsDisposed) return;

            try
            {
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
            catch (Exception ex)
            {
                SessionLog.Write("INSTALL", ex);
                Log($"[INSTALL] aborted: {ex.Message}");
                if (!_closing) SetInstallingUi(false, "⬇  INSTALL", 0);   // never leave the UI locked in "installing"
            }
        }

        private void SetInstallingUi(bool installing, string installBtnText, int max)
        {
            _isInstalling       = installing;
            _installBtn.Enabled = !installing;
            _installBtn.Text    = installBtnText;
            _cancelBtn.Visible  = installing;
            _cancelBtn.Enabled  = true;
            if (_upgradeBtn != null) _upgradeBtn.Enabled = !installing && _upgradeCache.Values.Any(v => v);
            if (installing) { _overallProgress.Value = 0; _overallProgress.Maximum = Math.Max(1, max); }
            else            { _cts?.Dispose(); _cts = null; }
            TaskbarBadge.SetProgress(IsHandleCreated ? Handle : IntPtr.Zero, installing ? 0 : -1, Math.Max(1, max));
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
                if (status == InstallStatus.Success)
                {
                    // Reflect the install straight away instead of waiting for the next launch's scan.
                    _installedCache[app] = true;
                    tile.SetInstalled(true);
                    if (_installedScanDone) UpdateInstalledSummary();
                    RefreshFilterChips();
                    if (_upgradeCache.TryGetValue(app, out bool had) && had)
                    {
                        _upgradeCache[app] = false;   // a fresh install is the latest version
                        tile.SetHasUpdate(false);
                        RefreshUpgradeButton();
                    }
                }
                int pct = ParsePercent(msg);
                if (pct >= 0 && (status == InstallStatus.Installing || status == InstallStatus.Downloading))
                    tile.SetProgress(pct);
            }
            _statusLabel.Text = $"{app.Name}: {msg}";
            Log($"[{app.Name}] {msg}");
        });

        private void OnOverallProgress(int done, int total) => Ui(() =>
        {
            _overallProgress.Value = Math.Min(done, _overallProgress.Maximum);
            TaskbarBadge.SetProgress(IsHandleCreated ? Handle : IntPtr.Zero, done, Math.Max(1, total));
        });

        private void UpdateSelectionCount()
        {
            int count = _tiles.Values.Count(t => t.IsChecked);
            if (_selectionCountLabel != null)
                _selectionCountLabel.Text = count == 0 ? "no apps selected" : $"{count} app{(count == 1 ? "" : "s")} selected";
            UpdateSidebarCounts();
            RefreshFilterChips();

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

        // "Downloading X: 42%" → 42; -1 when there's no percentage.
        private static int ParsePercent(string msg) =>
            !string.IsNullOrEmpty(msg) && Regex.Match(msg, @"(\d+)\s*%") is { Success: true } m && int.TryParse(m.Groups[1].Value, out int pct)
                ? Math.Clamp(pct, 0, 100) : -1;
    }
}
