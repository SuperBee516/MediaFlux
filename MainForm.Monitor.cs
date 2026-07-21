using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private int _h264FileCount;
        private int _h265FileCount;
        private int _av1FileCount;
        private int _otherCodecFileCount;
        private readonly HashSet<string> _codecCountedPaths = new(StringComparer.OrdinalIgnoreCase);

        private System.Threading.Timer? _monitorTimer;
        private readonly HashSet<string> _monSeen = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _monLock = new();
        private volatile bool _monitoring;
        private int _monitorScanInProgress;
        private int _monitorGeneration;
        private bool _updatingWatchFolderCheckbox;
        private System.Windows.Forms.Timer? _watchCountdownTimer;
        private long _nextMonitorScanUtcTicks;
        private volatile bool _watchCheckInProgress;
        private string _lastWatchStatus = "Folder watching is off.";

        private sealed record WatchScanSettings(
            string Root,
            bool IncludeSubfolders,
            int StabilizationSeconds,
            HashSet<string> AllowedExtensions,
            bool AllowH264,
            bool AllowH265,
            bool AllowAv1,
            bool AllowOther);

        private void InitializeWatchFolderUi()
        {
            if (chkWatchFolder == null)
                return;

            // Watching is intentionally session-only. Preserve the configured
            // folder and timing options, but require an explicit opt-in after
            // every application launch.
            if (_config.WatchFolderEnabled)
            {
                _config.WatchFolderEnabled = false;
                _config.Save(_configPath);
            }

            _updatingWatchFolderCheckbox = true;
            chkWatchFolder.Checked = false;
            _updatingWatchFolderCheckbox = false;
            chkWatchFolder.CheckedChanged += chkWatchFolder_CheckedChanged;
            _uiToolTip.SetToolTip(
                chkWatchFolder,
                "Watch the folder configured in Tools > Settings. New files use the current codec filters and encoding settings.");

            _watchCountdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _watchCountdownTimer.Tick += (_, __) => UpdateWatchCountdownDisplay();

            _lastWatchStatus = "Folder watching is off. Enable it to begin this session.";
            UpdateWatchCountdownDisplay();
        }

        private void chkWatchFolder_CheckedChanged(object? sender, EventArgs e)
        {
            if (_updatingWatchFolderCheckbox || chkWatchFolder == null)
                return;

            if (chkWatchFolder.Checked)
            {
                if (!EnsureFfmpegToolsAvailable())
                {
                    SetWatchFolderCheckbox(false);
                    return;
                }

                if (!Directory.Exists(_config.WatchFolderPath))
                {
                    string inputFolder = cmbInputFolder.Text?.Trim() ?? string.Empty;
                    if (Directory.Exists(inputFolder))
                    {
                        _config.WatchFolderPath = inputFolder;
                    }
                    else
                    {
                        MessageBox.Show(
                            this,
                            "Choose a valid Input Folder, or configure the watch folder in Tools > Settings.",
                            "Watch Folder",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        SetWatchFolderCheckbox(false);
                        return;
                    }
                }

                PopulateLastUsedWatchOutputFolder();

                if (!ValidateOutputFolderAgainstWatchFolder(
                        cmbEncodeOutput.Text,
                        showMessage: true,
                        watchWillBeEnabled: true))
                {
                    SetWatchFolderCheckbox(false);
                    return;
                }

                _config.WatchFolderEnabled = true;
                _config.Save(_configPath);
                StartMonitoringFromConfig();
            }
            else
            {
                _config.WatchFolderEnabled = false;
                _config.Save(_configPath);
                StopMonitoring("Folder watching is off.");
            }
        }

        private void ApplyWatchFolderConfiguration()
        {
            SetWatchFolderCheckbox(_config.WatchFolderEnabled);

            if (_config.WatchFolderEnabled)
                StartMonitoringFromConfig();
            else
                StopMonitoring("Folder watching is off.");
        }

        private void SetWatchFolderCheckbox(bool value)
        {
            if (chkWatchFolder == null)
                return;

            _updatingWatchFolderCheckbox = true;
            chkWatchFolder.Checked = value;
            _updatingWatchFolderCheckbox = false;
        }

        private void StartMonitoringFromConfig()
        {
            if (!ResolveFfmpegTools().AreAllAvailable)
            {
                RefreshFfmpegToolAvailability();
                _config.WatchFolderEnabled = false;
                _config.Save(_configPath);
                SetWatchFolderCheckbox(false);
                ShowWatchStatus("Folder watching requires ffmpeg.exe and ffprobe.exe. See the warning above.");
                return;
            }

            string root = _config.WatchFolderPath?.Trim() ?? string.Empty;
            if (!Directory.Exists(root))
            {
                _config.WatchFolderEnabled = false;
                _config.Save(_configPath);
                SetWatchFolderCheckbox(false);
                ShowWatchStatus("Watch folder is unavailable. Folder watching was disabled.");
                return;
            }

            if (!ValidateOutputFolderAgainstWatchFolder(
                    cmbEncodeOutput.Text,
                    showMessage: false,
                    watchWillBeEnabled: true))
            {
                _config.WatchFolderEnabled = false;
                _config.Save(_configPath);
                SetWatchFolderCheckbox(false);
                ShowWatchStatus("Folder watching was disabled because it requires a separate Output Folder.");
                return;
            }

            int minutes = Math.Clamp(_config.WatchFolderIntervalMinutes, 1, 1440);

            lock (_monLock)
            {
                _monitorTimer?.Dispose();
                _monitorTimer = null;
                _monSeen.Clear();
                _monitoring = true;
                _monitorGeneration++;
                _monitorTimer = new System.Threading.Timer(
                    _ => MonitorTickSafe(),
                    null,
                    TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);
            }

            Interlocked.Exchange(ref _nextMonitorScanUtcTicks, 0);
            _watchCheckInProgress = false;
            _watchCountdownTimer?.Start();
            ShowWatchStatus($"Checking \"{root}\" for new files…");
        }

        private void PopulateLastUsedWatchOutputFolder()
        {
            string currentOutput = cmbEncodeOutput.Text?.Trim() ?? string.Empty;
            if (Directory.Exists(currentOutput) &&
                !FolderPathComparer.OutputConflictsWithWatchFolder(
                    currentOutput,
                    _config.WatchFolderPath,
                    _config.WatchFolderIncludeSubfolders))
            {
                return;
            }

            string? lastUsableOutput = _config.LastOutputFolders
                .FirstOrDefault(path =>
                    Directory.Exists(path) &&
                    !FolderPathComparer.OutputConflictsWithWatchFolder(
                        path,
                        _config.WatchFolderPath,
                        _config.WatchFolderIncludeSubfolders));

            if (string.IsNullOrWhiteSpace(lastUsableOutput))
                return;

            cmbEncodeOutput.Text = lastUsableOutput;
            ShowWatchStatus($"Restored last Output Folder: \"{lastUsableOutput}\".");
        }

        private bool ValidateOutputFolderAgainstWatchFolder(
            string? outputFolder,
            bool showMessage,
            bool watchWillBeEnabled = false)
        {
            bool watching = _config.WatchFolderEnabled || watchWillBeEnabled;
            if (watching && string.IsNullOrWhiteSpace(outputFolder))
            {
                if (showMessage)
                {
                    MessageBox.Show(
                        this,
                        "Choose an Output Folder outside the watched folder. A blank Output Folder writes beside the source file and is not allowed while folder watching is enabled.",
                        "Output folder required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return false;
            }

            if (!FolderPathComparer.OutputConflictsWithWatchFolder(
                    outputFolder,
                    _config.WatchFolderPath,
                    _config.WatchFolderIncludeSubfolders))
            {
                return true;
            }

            if (showMessage)
            {
                MessageBox.Show(
                    this,
                    "The Output Folder cannot be the watched folder or a folder inside its watched subfolders. Choose a separate output location.",
                    "Folder conflict",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return false;
        }

        private void StopMonitoring(string status)
        {
            lock (_monLock)
            {
                _monitorTimer?.Dispose();
                _monitorTimer = null;
                _monitoring = false;
                _monitorGeneration++;
            }

            _watchCheckInProgress = false;
            Interlocked.Exchange(ref _nextMonitorScanUtcTicks, 0);
            Ui(() => _watchCountdownTimer?.Stop());
            ShowWatchStatus(status);
        }

        private void MonitorTickSafe()
        {
            if (!_monitoring || Interlocked.Exchange(ref _monitorScanInProgress, 1) != 0)
                return;

            int generation = Volatile.Read(ref _monitorGeneration);
            int intervalMinutes = Math.Clamp(_config.WatchFolderIntervalMinutes, 1, 1440);
            _watchCheckInProgress = true;
            Interlocked.Exchange(ref _nextMonitorScanUtcTicks, 0);
            ShowWatchStatus("Checking for new files…");
            try
            {
                var settings = CaptureWatchScanSettings();
                if (settings == null)
                {
                    ShowWatchStatus($"Check failed at {DateTime.Now:t}: watch folder is unavailable.");
                    return;
                }

                if (!settings.AllowH264 && !settings.AllowH265 && !settings.AllowOther)
                {
                    ShowWatchStatus($"Checked {DateTime.Now:t}: no codec types are selected.");
                    return;
                }

                var newFiles = EnumerateEligibleNewFiles(settings);
                if (!_monitoring || generation != Volatile.Read(ref _monitorGeneration))
                    return;

                if (newFiles.Count == 0)
                {
                    ShowWatchStatus($"Checked {DateTime.Now:t}: no new matching files found.");
                    return;
                }

                // Tell the dynamic queue runner that an append is in flight so it
                // cannot finish in the narrow window before the UI adds the rows.
                Interlocked.Increment(ref _pendingEncodeImports);
                try
                {
                    UiInvoke(() => EnqueueWatchedFiles(newFiles));
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingEncodeImports);
                }
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(
                    Application.StartupPath,
                    "Watch folder scan failed",
                    _config.WatchFolderPath,
                    ex);
                ShowWatchStatus($"Check failed at {DateTime.Now:t}: {ex.Message}");
            }
            finally
            {
                _watchCheckInProgress = false;
                Interlocked.Exchange(ref _monitorScanInProgress, 0);
                ScheduleNextMonitorScan(generation, intervalMinutes);
            }
        }

        private void ScheduleNextMonitorScan(int generation, int intervalMinutes)
        {
            var interval = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 1440));
            lock (_monLock)
            {
                if (!_monitoring || generation != _monitorGeneration || _monitorTimer == null)
                    return;

                _monitorTimer.Change(interval, Timeout.InfiniteTimeSpan);
                Interlocked.Exchange(
                    ref _nextMonitorScanUtcTicks,
                    DateTime.UtcNow.Add(interval).Ticks);
            }

            Ui(UpdateWatchCountdownDisplay);
        }

        private WatchScanSettings? CaptureWatchScanSettings()
        {
            return UiGet(() =>
            {
                if (!_config.WatchFolderEnabled || !Directory.Exists(_config.WatchFolderPath))
                    return null;

                return new WatchScanSettings(
                    _config.WatchFolderPath,
                    _config.WatchFolderIncludeSubfolders,
                    Math.Clamp(_config.WatchFolderStabilizationSeconds, 0, 3600),
                    GetAllowedExts(),
                    chkFilterX264.Checked,
                    chkFilterX265.Checked,
                    chkFilterAv1.Checked,
                    chkFilterOtherCodecs.Checked);
            }, null);
        }

        private List<string> EnumerateEligibleNewFiles(WatchScanSettings settings)
        {
            var eligible = new List<string>();
            var searchOption = settings.IncludeSubfolders
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            foreach (var path in Directory.EnumerateFiles(settings.Root, "*.*", searchOption))
            {
                string extension = Path.GetExtension(path);
                if (string.IsNullOrWhiteSpace(extension) || !settings.AllowedExtensions.Contains(extension))
                    continue;

                lock (_monLock)
                {
                    if (_monSeen.Contains(path))
                        continue;
                }

                if (IsCompletedEncodePath(path))
                    continue;

                FileInfo file;
                try
                {
                    file = new FileInfo(path);
                    if (!file.Exists ||
                        DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(settings.StabilizationSeconds))
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (!CanOpenWatchedFileExclusively(path))
                    continue;

                string codec;
                try
                {
                    codec = GetVideoCodec(path);
                }
                catch
                {
                    // An in-progress or temporarily locked file remains unseen and
                    // will be retried on the next scheduled scan.
                    continue;
                }

                if (!PassesCodecFilter(
                        codec,
                        settings.AllowH264,
                        settings.AllowH265,
                        settings.AllowAv1,
                        settings.AllowOther))
                {
                    // Keep it eligible for a future scan if the user later changes
                    // the Show codec selections.
                    continue;
                }

                lock (_monLock)
                    _monSeen.Add(path);
                eligible.Add(path);
            }

            return eligible;
        }

        private static bool CanOpenWatchedFileExclusively(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.SequentialScan);
                return stream.Length >= 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void EnqueueWatchedFiles(IReadOnlyList<string> files)
        {
            var addedRows = new List<DataGridViewRow>();
            foreach (var file in files)
            {
                try
                {
                    TrackCodecFilterCount(file, GetVideoCodec(file));
                }
                catch
                {
                    // The eligibility scan already validated the codec. A cache
                    // refresh failure here should not prevent queueing the file.
                }

                if (!AddEncodeItemIfNotPresent(file, refreshEstimates: false))
                    continue;

                if (_rowsByPath.TryGetValue(file, out var row))
                    addedRows.Add(row);
                AppendRecentDiscovery(file);
            }

            if (addedRows.Count == 0)
            {
                ShowWatchStatus($"Checked {DateTime.Now:t}: discovered files were already queued.");
                return;
            }

            if (!_encodingActive)
            {
                if (dgvEncodeQueue.Rows.Count < GetLargeQueueThreshold() || _config.AutoAnalyzeLargeQueues)
                    RunEstimatePass();
                else
                    UpdateSizeTotals(force: true);

                dgvEncodeQueue.ClearSelection();
                foreach (var row in addedRows)
                    row.Selected = true;
            }

            string message = $"Checked {DateTime.Now:t}: added {addedRows.Count:N0} new file(s) to the encode queue.";
            ShowWatchStatus(message);

            // AddEncodeItemIfNotPresent appends directly to the live queue while an
            // encode is running. Otherwise start the normal button path now.
            if (!_encodingActive)
                AutoStartEncodeIfPossible();
        }

        private void ShowWatchStatus(string status)
        {
            Ui(() =>
            {
                _lastWatchStatus = status;
                UpdateWatchCountdownDisplay();
                if (lblMonStatus != null && !lblMonStatus.IsDisposed)
                    lblMonStatus.Text = status;
                if (toolStripStatusLabel1 != null)
                    toolStripStatusLabel1.Text = status;
                if (btnMonStart != null)
                    btnMonStart.Enabled = !_monitoring;
                if (btnMonStop != null)
                    btnMonStop.Enabled = _monitoring;
            });
        }

        private void UpdateWatchCountdownDisplay()
        {
            if (lblWatchFolderStatus == null || lblWatchFolderStatus.IsDisposed)
                return;

            lblWatchFolderStatus.Visible = !_config.HideWatchFolderStatusText;
            if (_config.HideWatchFolderStatusText)
                return;

            if (!_monitoring)
            {
                lblWatchFolderStatus.Text = _lastWatchStatus;
                return;
            }

            if (_watchCheckInProgress)
            {
                lblWatchFolderStatus.Text = "Checking for new files…";
                return;
            }

            long nextTicks = Interlocked.Read(ref _nextMonitorScanUtcTicks);
            if (nextTicks <= 0)
            {
                lblWatchFolderStatus.Text = _lastWatchStatus;
                return;
            }

            TimeSpan remaining = new DateTime(nextTicks, DateTimeKind.Utc) - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            lblWatchFolderStatus.Text =
                $"{_lastWatchStatus} Next check in {FormatWatchCountdown(remaining)}.";
        }

        private static string FormatWatchCountdown(TimeSpan remaining)
        {
            int totalHours = (int)remaining.TotalHours;
            return totalHours > 0
                ? $"{totalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
        }

        private void AppendRecentDiscovery(string path)
        {
            if (listMonLastFound == null || listMonLastFound.IsDisposed)
                return;

            if (listMonLastFound.Items.Count >= 200)
                listMonLastFound.Items.RemoveAt(0);

            listMonLastFound.Items.Add(path);
        }

        private void AutoStartEncodeIfPossible()
        {
            if (_encodingActive)
                return;

            if (InvokeRequired)
            {
                Ui(AutoStartEncodeIfPossible);
                return;
            }

            btnStartEncode_Click(btnStartEncode, EventArgs.Empty);
        }

        // Compatibility handlers for the older, hidden Monitor panel. They route
        // through the same persisted watch-folder implementation.
        private void btnBrowseMonFolder_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(_config.WatchFolderPath)
                    ? _config.WatchFolderPath
                    : Application.StartupPath
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            cmbMonFolder.Text = dlg.SelectedPath;
            _config.WatchFolderPath = dlg.SelectedPath;
            _config.Save(_configPath);
        }

        private void btnMonStart_Click(object? sender, EventArgs e)
        {
            if (!EnsureFfmpegToolsAvailable())
                return;

            if (Directory.Exists(cmbMonFolder.Text))
                _config.WatchFolderPath = cmbMonFolder.Text.Trim();
            _config.WatchFolderIntervalMinutes = (int)nudMonMinutes.Value;
            _config.WatchFolderIncludeSubfolders = chkMonIncludeSubfolders.Checked;
            _config.WatchFolderStabilizationSeconds = (int)nudMonStabilizeSec.Value;
            _config.WatchFolderEnabled = true;
            _config.Save(_configPath);
            SetWatchFolderCheckbox(true);
            PopulateLastUsedWatchOutputFolder();
            StartMonitoringFromConfig();
        }

        private void btnMonStop_Click(object? sender, EventArgs e)
        {
            _config.WatchFolderEnabled = false;
            _config.Save(_configPath);
            SetWatchFolderCheckbox(false);
            StopMonitoring("Folder watching is off.");
        }

        private void btnMonScanNow_Click(object? sender, EventArgs e)
        {
            if (!EnsureFfmpegToolsAvailable())
                return;

            _ = System.Threading.Tasks.Task.Run(MonitorTickSafe);
        }

        private string GetVideoCodec(string path) => _mediaInfoService.GetVideoCodec(path);

        private bool PassesCodecFilter(string codec)
        {
            return PassesCodecFilter(
                codec,
                chkFilterX264.Checked,
                chkFilterX265.Checked,
                chkFilterAv1.Checked,
                chkFilterOtherCodecs.Checked);
        }

        private static bool PassesCodecFilter(
            string codec,
            bool allowH264,
            bool allowHevc,
            bool allowAv1,
            bool allowOther)
        {
            if (!allowH264 && !allowHevc && !allowAv1 && !allowOther)
                return false;
            if (IsH264Codec(codec))
                return allowH264;
            if (IsH265Codec(codec))
                return allowHevc;
            if (IsAv1Codec(codec))
                return allowAv1;
            return allowOther;
        }

        private static bool IsH264Codec(string codec) =>
            string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase);

        private static bool IsH265Codec(string codec) =>
            string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase);

        private static bool IsAv1Codec(string codec) =>
            string.Equals(codec, "av1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(codec, "av01", StringComparison.OrdinalIgnoreCase);

        private void UpdateCodecFilterCounts(int h264Count, int h265Count, int av1Count, int otherCount)
        {
            _h264FileCount = h264Count;
            _h265FileCount = h265Count;
            _av1FileCount = av1Count;
            _otherCodecFileCount = otherCount;
            lblFilterX264Count.Text = h264Count.ToString("N0");
            lblFilterX265Count.Text = h265Count.ToString("N0");
            lblFilterAv1Count.Text = av1Count.ToString("N0");
            lblFilterOtherCodecsCount.Text = otherCount.ToString("N0");
        }

        private void ResetCodecFilterCounts()
        {
            _codecCountedPaths.Clear();
            UpdateCodecFilterCounts(0, 0, 0, 0);
        }

        private void TrackCodecFilterCount(string path, string? codec)
        {
            if (string.IsNullOrWhiteSpace(path) || !_codecCountedPaths.Add(path))
                return;

            codec ??= string.Empty;
            if (IsH264Codec(codec))
                UpdateCodecFilterCounts(_h264FileCount + 1, _h265FileCount, _av1FileCount, _otherCodecFileCount);
            else if (IsH265Codec(codec))
                UpdateCodecFilterCounts(_h264FileCount, _h265FileCount + 1, _av1FileCount, _otherCodecFileCount);
            else if (IsAv1Codec(codec))
                UpdateCodecFilterCounts(_h264FileCount, _h265FileCount, _av1FileCount + 1, _otherCodecFileCount);
            else
                UpdateCodecFilterCounts(_h264FileCount, _h265FileCount, _av1FileCount, _otherCodecFileCount + 1);
        }
    }
}
