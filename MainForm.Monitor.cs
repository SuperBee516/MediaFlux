using Encode.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace Encode
{
    public partial class MainForm : Form
    {
        // ───────── Monitor state ─────────
        private System.Threading.Timer? _monitorTimer;
        private readonly HashSet<string> _monSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private volatile bool _monitoring = false;
        private readonly object _monLock = new object();
        private FileSystemWatcher? _monWatcher;
        private volatile bool _monNeedsScan = false;

        private void btnBrowseMonFolder_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = string.IsNullOrWhiteSpace(cmbMonFolder.Text) ? Application.StartupPath : cmbMonFolder.Text
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var picked = dlg.SelectedPath;
            cmbMonFolder.Text = picked;

            // reuse history helpers
            AddToHistory(_config.LastInputFolders, picked);
            RefreshHistoryCombo(cmbMonFolder, _config.LastInputFolders);
            _config.Save(_configPath);
        }

        private void btnMonStart_Click(object? sender, EventArgs e)
        {
            if (_monitoring) return;

            string root = cmbMonFolder.Text;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                MessageBox.Show("Pick a valid folder to monitor.", "Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _monSeen.Clear();

            // Find eligible existing files (using all current filters)
            var eligibleExisting = EnumerateEligibleNewFiles(
                root: root,
                includeSubfolders: chkMonIncludeSubfolders.Checked,
                respectCodecFilters: chkMonUseEncodeFilters.Checked,
                minSizeBytes: (long)nudMonMinSizeMb.Value * 1024L * 1024L,
                stabilizeSeconds: (int)nudMonStabilizeSec.Value
            );

            // If we found files, ask whether to process them right now
            if (eligibleExisting.Count > 0)
            {
                var resp = MessageBox.Show(
                    $"Found {eligibleExisting.Count} existing file(s) in \"{root}\".\n\nProcess them now?",
                    "Monitor",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );

                if (resp == DialogResult.Yes)
                {
                    // Mark as seen
                    foreach (var f in eligibleExisting) _monSeen.Add(f);

                    // Enqueue immediately
                    EnqueueBatch(eligibleExisting);

                    // Update estimates
                    SafeRefreshEstimates();

                    // Select all rows so the Start logic processes the full batch
                    dgvEncodeQueue.SuspendLayout();
                    try
                    {
                        dgvEncodeQueue.ClearSelection();
                        foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
                            row.Selected = true;
                    }
                    finally
                    {
                        dgvEncodeQueue.ResumeLayout();
                        dgvEncodeQueue.Refresh();
                    }

                    // Robust auto-start (doesn't depend on button state or active panel)
                    if (chkMonAutoStart.Checked)
                    {
                        AutoStartEncodeIfPossible();
                    }
                }
                else
                {
                    // User chose not to process existing files; seed 'seen' so we won’t re-ask
                    SeedSeen(root, chkMonIncludeSubfolders.Checked);
                }
            }
            else
            {
                // Nothing to pre-process; seed seen so only truly new files are picked up
                SeedSeen(root, chkMonIncludeSubfolders.Checked);
            }

            // Start timer (immediate first tick + cadence)
            int mins = (int)nudMonMinutes.Value;
            _monitorTimer = new System.Threading.Timer(_ => MonitorTickSafe(), null, TimeSpan.Zero, TimeSpan.FromMinutes(mins));
            _monitoring = true;

            // Optional FileSystemWatcher hybrid (if enabled)
            if (chkMonUseWatcher.Checked)
            {
                _monWatcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = chkMonIncludeSubfolders.Checked,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                _monWatcher.Created += (_, __) => { _monNeedsScan = true; };
                _monWatcher.Changed += (_, __) => { _monNeedsScan = true; };
                _monWatcher.Renamed += (_, __) => { _monNeedsScan = true; };
            }

            // ── UI toggles: marshal to UI thread
            Ui(() =>
            {
                btnMonStart.Enabled = false;
                btnMonStop.Enabled = true;
                lblMonStatus.Text = $"Monitoring \"{root}\" every {mins} minute(s)…";
                toolStripStatusLabel1.Text = "Monitoring started";
            });
        }

        // Robustly start encoding from anywhere (Monitor, context menu, etc.)
        private void AutoStartEncodeIfPossible()
        {
            // Don't double-start
            if (_encodingActive) return;

            // Must run on UI thread
            if (InvokeRequired) { Ui(AutoStartEncodeIfPossible); return; }

            try
            {
                // Call the same logic your Start button uses
                btnStartEncode_Click(btnStartEncode, EventArgs.Empty);
            }
            catch
            {
                // Fallback: try simulated click (in case handler changes later)
                try { btnStartEncode.PerformClick(); } catch { /* swallow */ }
            }
        }

        // Enumerate files that PASS all Monitor filters and are not yet in _monSeen
        private List<string> EnumerateEligibleNewFiles(
            string root,
            bool includeSubfolders,
            bool respectCodecFilters,
            long minSizeBytes,
            int stabilizeSeconds)
        {
            var list = new List<string>();
            var searchOpt = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var allowedExts = GetAllowedExtensionsFromUi();

            foreach (var path in Directory.EnumerateFiles(root, "*.*", searchOpt))
            {
                // extension filter
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext) || !allowedExts.Contains(ext))
                    continue;

                // already seen? skip
                if (_monSeen.Contains(path))
                    continue;

                // size threshold
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length < minSizeBytes)
                    continue;

                // stabilization window
                if (stabilizeSeconds > 0)
                {
                    var age = DateTime.UtcNow - fi.LastWriteTimeUtc;
                    if (age < TimeSpan.FromSeconds(stabilizeSeconds))
                        continue;
                }

                // optional codec filter (same logic as your Encode scan)
                if (respectCodecFilters)
                {
                    string codec = string.Empty;
                    try { codec = GetVideoCodec(path); } catch { /* ignore probe errors */ }

                    bool isH264 = string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase);
                    bool isHevc = string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase);

                    if ((isH264 && !chkFilterX264.Checked) || (isHevc && !chkFilterX265.Checked))
                        continue;
                }

                list.Add(path);
            }

            return list;
        }

        // Enqueue a batch and update UI
        private void EnqueueBatch(IReadOnlyList<string> files)
        {
            int enq = 0;
            foreach (var f in files)
            {
                if (AddEncodeItemIfNotPresent(f))
                {
                    enq++;
                    AppendRecentDiscovery(f); // UI control
                }
            }

            if (enq > 0)
            {
                Ui(() =>
                {
                    lblMonStatus.Text = $"Enqueued {enq} file(s).";
                    toolStripStatusLabel1.Text = $"Enqueued {enq} file(s) from initial scan";
                    dgvEncodeQueue.Refresh();
                });
            }
        }

        private void AppendRecentDiscovery(string path)
        {
            if (listMonLastFound == null || listMonLastFound.IsDisposed)
                return;

            if (listMonLastFound.Items.Count > 200)
                listMonLastFound.Items.RemoveAt(0);

            listMonLastFound.Items.Add(path);
        }

        private void btnMonStop_Click(object? sender, EventArgs e)
        {
            StopMonitoring("Monitoring stopped.");
        }

        private void btnMonScanNow_Click(object? sender, EventArgs e)
        {
            if (!_monitoring)
            {
                // allow ad-hoc single scan even if not started
                MonitorScanOnce();
            }
            else
            {
                // kick an extra scan while monitoring
                MonitorTickSafe();
            }
        }

        // StopMonitoring (UI toggles via Ui(...))
        private void StopMonitoring(string status)
        {
            lock (_monLock)
            {
                // stop timer
                _monitorTimer?.Dispose();
                _monitorTimer = null;

                // ensure watcher is disposed and flags reset
                _monWatcher?.Dispose();
                _monWatcher = null;
                _monNeedsScan = false;

                _monitoring = false;
            }

            // ── UI toggles: marshal to UI thread
            Ui(() =>
            {
                btnMonStart.Enabled = true;
                btnMonStop.Enabled = false;
                lblMonStatus.Text = status;
                toolStripStatusLabel1.Text = "Ready";
            });
        }

        private void MonitorTickSafe()
        {
            try
            {
                // If FSW fired, swallow the flag and scan immediately
                if (_monNeedsScan)
                {
                    _monNeedsScan = false;
                    MonitorScanOnce();
                }

                // Then do the normal periodic scan
                MonitorScanOnce();
            }
            catch (Exception ex)
            {
                Ui(() => lblMonStatus.Text = $"Monitor error: {ex.Message}");
            }
        }

        private void MonitorScanOnce()
        {
            string root = cmbMonFolder.Text;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            try
            {
                var searchOpt = chkMonIncludeSubfolders.Checked
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                HashSet<string> allowedExts = GetAllowedExtensionsFromUi();
                bool respectCodecFilters = chkMonUseEncodeFilters.Checked;

                long minBytes = (long)nudMonMinSizeMb.Value * 1024L * 1024L;
                int stabilizeSec = (int)nudMonStabilizeSec.Value;

                var newFiles = new List<string>();

                foreach (var path in Directory.EnumerateFiles(root, "*.*", searchOpt))
                {
                    // ext filter
                    string ext = Path.GetExtension(path);
                    if (string.IsNullOrEmpty(ext) || !allowedExts.Contains(ext))
                        continue;

                    // already seen? skip
                    if (_monSeen.Contains(path))
                        continue;

                    // size threshold
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length < minBytes)
                        continue;

                    // stabilization window
                    if (stabilizeSec > 0)
                    {
                        var age = DateTime.UtcNow - fi.LastWriteTimeUtc;
                        if (age < TimeSpan.FromSeconds(stabilizeSec))
                            continue;
                    }

                    // optional codec filter (same logic as your Encode scan)
                    if (respectCodecFilters)
                    {
                        string codec = string.Empty;
                        try { codec = GetVideoCodec(path); } catch { /* ignore probe errors */ }

                        bool isH264 = string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase);
                        bool isHevc = string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase);

                        if ((isH264 && !chkFilterX264.Checked) || (isHevc && !chkFilterX265.Checked))
                        {
                            _monSeen.Add(path); // don’t keep re-checking filtered out files
                            continue;
                        }
                    }

                    // passed all checks; mark seen and queue locally
                    _monSeen.Add(path);
                    newFiles.Add(path);
                }

                if (newFiles.Count == 0)
                    return;

                // Enqueue to existing Encode queue
                Ui(() =>
                {
                    int enq = 0;
                    foreach (var f in newFiles)
                    {
                        if (AddEncodeItemIfNotPresent(f))
                        {
                            enq++;
                            AppendRecentDiscovery(f);
                        }
                    }

                    if (enq > 0)
                    {
                        lblMonStatus.Text = $"Found {enq} new file(s).";
                        toolStripStatusLabel1.Text = $"Enqueued {enq} new file(s) from Monitor";
                        SafeRefreshEstimates();

                        dgvEncodeQueue.SuspendLayout();
                        try
                        {
                            dgvEncodeQueue.ClearSelection();
                            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
                                row.Selected = true;
                        }
                        finally
                        {
                            dgvEncodeQueue.ResumeLayout();
                            dgvEncodeQueue.Refresh();
                        }

                        if (chkMonAutoStart.Checked)
                            AutoStartEncodeIfPossible();
                    }
                });
            }
            finally
            {
                _activityIndicator?.StopActivity(UiActivity.FolderScan);
            }
        }

        private void SeedSeen(string root, bool includeSubfolders)
        {
            var opt = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            HashSet<string> allowedExts = GetAllowedExtensionsFromUi();
            foreach (var path in Directory.EnumerateFiles(root, "*.*", opt))
            {
                if (allowedExts.Contains(Path.GetExtension(path)))
                    _monSeen.Add(path);
            }
        }

        private HashSet<string> GetAllowedExtensionsFromUi()
        {
            return new HashSet<string>(
                checkedListExt.CheckedItems.Cast<string>(),
                StringComparer.OrdinalIgnoreCase
            );
        }

        private string GetVideoCodec(string path)
        {
            return _mediaInfoService.GetVideoCodec(path);
        }
    }
}
