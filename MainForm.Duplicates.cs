using MediaFlux.Models;
using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace MediaFlux
{
    public partial class MainForm : Form
    {
        private const string DuplicateRuleSuggested = "Suggested duplicate files";
        private const string DuplicateRuleLargest = "Largest file in each group";
        private const string DuplicateRuleSmallest = "Smallest file in each group";
        private const string DuplicateRuleH264 = "Codec: x264 / h.264";
        private const string DuplicateRuleH265 = "Codec: x265 / h.265";
        private const string DuplicateRuleOtherCodec = "Codec: other";
        private const string DuplicateRuleLowerResolution = "Lower resolution";
        private const string DuplicateRuleLowerBitrate = "Lower bitrate";
        private const string DuplicateRuleOlder = "Older files";
        private const string DuplicateRuleNewer = "Newer files";
        private const string DuplicateRuleSelectedRows = "Selected rows";
        private const string DuplicateFilterAll = "All groups";
        private const string DuplicateFilterExact = "Exact";
        private const string DuplicateFilterStrong = "Strong visual";
        private const string DuplicateFilterReview = "Review only";
        private const string DuplicateFilterActionable = "Actionable only";
        private const string DuplicateFilterProtected = "Protected/reference";
        private bool _duplicateCleanupAutoDisablePending;

        private void StartDuplicateScanIfEnabled()
        {
            if (chkFindDuplicates?.Checked != true || _duplicateDetectionService == null)
            {
                ClearDuplicateAnnotations();
                return;
            }

            if (!ResolveFfmpegTools().AreAllAvailable)
            {
                RefreshFfmpegToolAvailability();
                ClearDuplicateAnnotations();
                ShowStatusInfo("Duplicate checking requires ffmpeg.exe and ffprobe.exe. See the warning above.");
                return;
            }

            if (Volatile.Read(ref _pendingEncodeImports) > 0)
            {
                _duplicateRescanPending = true;
                string waiting = "Duplicate check waiting for file discovery to finish.";
                toolStripStatusLabel1.Text = waiting;
                UpdateRelocatedEncodeStatus(waiting);
                SetDuplicateFinderWorkingStatus(waiting);
                return;
            }

            string inputFolder = cmbInputFolder?.Text?.Trim() ?? string.Empty;
            if (Directory.Exists(inputFolder))
            {
                _ = StartDuplicateFolderScanAsync(inputFolder);
                return;
            }

            StartDuplicateScan();
        }

        private async Task<DuplicateScanResult> AnalyzeDuplicatePathsForImportAsync(
            IReadOnlyCollection<string> paths,
            int stepNumber,
            int totalSteps,
            CancellationToken token)
        {
            if (!ResolveFfmpegTools().AreAllAvailable)
            {
                RefreshFfmpegToolAvailability();
                return new DuplicateScanResult(Array.Empty<DuplicateGroup>(), 0, 0);
            }

            string starting = $"Step {stepNumber} of {totalSteps} — Checking for duplicates";
            toolStripStatusLabel1.Text = starting;
            UpdateRelocatedEncodeStatus(starting);
            SetDuplicateFinderWorkingStatus(starting);
            if (paths.Count < 2)
                return new DuplicateScanResult(Array.Empty<DuplicateGroup>(), 0, 0);

            var options = CreateDuplicateScanOptions();
            var progress = new Progress<DuplicateScanProgress>(p =>
            {
                string text = $"Step {stepNumber} of {totalSteps} — {p.Stage}: {p.Current:N0}/{p.Total:N0}";
                toolStripStatusLabel1.Text = text;
                UpdateRelocatedEncodeStatus(text);
                SetQueueProgress(p.Current, p.Total, visible: true);
                SetDuplicateFinderWorkingStatus(text);
            });

            return await _duplicateDetectionService.AnalyzeAsync(paths, options, progress, token);
        }

        private void ApplyDuplicateConfigurationToUi()
        {
            if (chkFindDuplicates == null || chkOnlyDuplicateCandidates == null || comboDuplicateScanMode == null)
                return;

            chkFindDuplicates.Checked = _config.FindDuplicatesOnImport;
            chkOnlyDuplicateCandidates.Checked = _config.OnlyQueueDuplicateCandidates;
            chkOnlyDuplicateCandidates.Enabled = chkFindDuplicates.Checked;
            comboDuplicateScanMode.SelectedItem = DuplicateScanModes.Normalize(_config.DuplicateScanMode);
            comboDuplicateScanMode.Enabled = chkFindDuplicates.Checked;
            if (chkAutoDisableDuplicateFinder != null)
                chkAutoDisableDuplicateFinder.Checked = _config.AutoDisableDuplicateFinderAfterCleanup;
            UpdateDuplicateFinderUiState();
            UpdateDuplicateReferenceFolderUi();
        }

        private async void AnalyzeDuplicatesNow_Click(object? sender, EventArgs e)
        {
            if (_duplicateDetectionService == null)
                return;

            if (!EnsureFfmpegToolsAvailable())
                return;

            string inputFolder = cmbInputFolder?.Text?.Trim() ?? string.Empty;
            if (Directory.Exists(inputFolder))
            {
                await StartDuplicateFolderScanAsync(inputFolder);
                return;
            }

            StartDuplicateScan();
        }

        private async Task StartDuplicateFolderScanAsync(string folder)
        {
            _duplicateScanCts?.Cancel();
            _duplicateScanCts?.Dispose();
            _duplicateScanCts = new CancellationTokenSource();
            var token = _duplicateScanCts.Token;

            SetQueueWorkCancelVisible(true);
            SetQueueProgress(0, 1, visible: true);
            string discoverText = "Discovering all supported videos for duplicate analysis...";
            toolStripStatusLabel1.Text = discoverText;
            UpdateRelocatedEncodeStatus(discoverText);
            SetDuplicateFinderWorkingStatus(discoverText);

            List<string> paths;
            try
            {
                paths = await DiscoverDuplicateFolderScanPathsAsync(folder, token);
            }
            catch (OperationCanceledException)
            {
                if (_duplicateScanCts?.Token != token)
                    return;

                toolStripStatusLabel1.Text = "Duplicate scan canceled.";
                UpdateRelocatedEncodeStatus("Duplicate scan canceled.");
                SetQueueProgress(0, 0, visible: false);
                SetQueueWorkCancelVisible(_estimateService?.PendingEstimates > 0);
                return;
            }
            catch (Exception ex)
            {
                if (_duplicateScanCts?.Token != token)
                    return;

                ErrorLogService.Append(Application.StartupPath, "Duplicate folder discovery failed", folder, ex);
                toolStripStatusLabel1.Text = "Duplicate folder discovery failed. See the error log for details.";
                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                SetQueueProgress(0, 0, visible: false);
                SetQueueWorkCancelVisible(_estimateService?.PendingEstimates > 0);
                return;
            }

            if (token.IsCancellationRequested)
                return;

            if (paths.Count < 2)
            {
                ClearDuplicateAnnotations();
                ShowStatusInfo("The selected folder has fewer than two supported video files to analyze.");
                SetQueueProgress(0, 0, visible: false);
                SetQueueWorkCancelVisible(_estimateService?.PendingEstimates > 0);
                return;
            }

            ResetDuplicateAnnotations("Checking");
            var progress = new Progress<DuplicateScanProgress>(p =>
            {
                string text = $"{p.Stage}... {p.Current:N0}/{p.Total:N0}";
                toolStripStatusLabel1.Text = text;
                UpdateRelocatedEncodeStatus(text);
                SetQueueProgress(p.Current, p.Total, visible: true);
                SetDuplicateFinderWorkingStatus(text);
            });

            string scanText = $"Analyzing all supported videos in selected folder... {paths.Count:N0} file(s)";
            toolStripStatusLabel1.Text = scanText;
            UpdateRelocatedEncodeStatus(scanText);
            _ = RunDuplicateScanAsync(paths, progress, token);
        }

        private Task<List<string>> DiscoverDuplicateFolderScanPathsAsync(string folder, CancellationToken token)
        {
            return Task.Run(() =>
            {
                var allowedExts = GetAllowedExts();
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = chkIncludeSubfolders.Checked
                };

                foreach (var file in Directory.EnumerateFiles(folder, "*.*", options))
                {
                    token.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(file);
                    if (!string.IsNullOrWhiteSpace(ext) && allowedExts.Contains(ext))
                        found.Add(file);
                }

                return found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            }, token);
        }

        private void StartDuplicateScan()
        {
            var paths = dgvEncodeQueue.Rows
                .Cast<DataGridViewRow>()
                .Select(GetPathFromRow)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count < 2)
            {
                ClearDuplicateAnnotations();
                ShowStatusInfo("Add at least two video files before scanning for duplicates.");
                return;
            }

            _duplicateScanCts?.Cancel();
            _duplicateScanCts?.Dispose();
            _duplicateScanCts = new CancellationTokenSource();
            var token = _duplicateScanCts.Token;

            ResetDuplicateAnnotations("Checking");
            SetQueueWorkCancelVisible(true);
            var progress = new Progress<DuplicateScanProgress>(p =>
            {
                string text = $"{p.Stage}... {p.Current:N0}/{p.Total:N0}";
                toolStripStatusLabel1.Text = text;
                UpdateRelocatedEncodeStatus(text);
                SetQueueProgress(p.Current, p.Total, visible: true);
                SetDuplicateFinderWorkingStatus(text);
            });

            _ = RunDuplicateScanAsync(paths, progress, token);
        }

        private async Task RunDuplicateScanAsync(
            IReadOnlyCollection<string> paths,
            IProgress<DuplicateScanProgress> progress,
            CancellationToken token)
        {
            try
            {
                var result = await _duplicateDetectionService!.AnalyzeAsync(paths, CreateDuplicateScanOptions(), progress, token);
                if (token.IsCancellationRequested || _duplicateScanCts?.Token != token)
                    return;

                Ui(() =>
                {
                    ApplyDuplicateScanResult(result);
                    string text = result.Groups.Count > 0
                        ? $"Duplicate scan complete: {result.Groups.Count:N0} group(s), {result.DuplicateFiles:N0} duplicate candidate(s)."
                        : "Duplicate scan complete: no duplicate candidates found.";
                    toolStripStatusLabel1.Text = text;
                    UpdateRelocatedEncodeStatus(text);
                    SetQueueProgress(0, 0, visible: false);
                    SetQueueWorkCancelVisible(_estimateService?.PendingEstimates > 0);
                });
            }
            catch (OperationCanceledException)
            {
                if (_duplicateScanCts?.Token != token)
                    return;

                Ui(() =>
                {
                    toolStripStatusLabel1.Text = "Duplicate scan canceled.";
                    UpdateRelocatedEncodeStatus("Duplicate scan canceled.");
                    SetQueueProgress(0, 0, visible: false);
                });
            }
            catch (Exception ex)
            {
                if (_duplicateScanCts?.Token != token)
                    return;

                ErrorLogService.Append(Application.StartupPath, "Duplicate scan failed", exception: ex);
                Ui(() =>
                {
                    ClearDuplicateAnnotations();
                    toolStripStatusLabel1.Text = "Duplicate scan failed. See the error log for details.";
                    UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                    SetQueueProgress(0, 0, visible: false);
                });
            }
        }

        private void ApplyDuplicateScanResult(
            DuplicateScanResult result,
            IReadOnlySet<string>? newlyImportedPaths = null)
        {
            _lastDuplicateScanResult = result;
            ClearDuplicateAnnotations(resetResult: false);

            foreach (var group in result.Groups)
            {
                bool exactGroupHasExistingQueueFile =
                    newlyImportedPaths != null &&
                    group.ConfidenceLabel.Equals("Exact", StringComparison.OrdinalIgnoreCase) &&
                    group.Items.Any(item => !newlyImportedPaths.Contains(item.Path));

                foreach (var item in group.Items)
                {
                    if (!_rowsByPath.TryGetValue(item.Path, out var row) || row?.DataGridView != dgvEncodeQueue)
                        continue;

                    var meta = EnsureRowMeta(row);
                    meta.DuplicateGroupId = group.Id;
                    meta.DuplicateConfidence = group.ConfidenceLabel;
                    meta.DuplicateConfidenceScore = group.ConfidenceScore;
                    meta.DuplicateRecommendation = item.Recommendation;
                    meta.DuplicateReason = group.Reason;

                    bool isExactCandidate =
                        group.ConfidenceLabel.Equals("Exact", StringComparison.OrdinalIgnoreCase) &&
                        !item.IsReferenceProtected &&
                        (exactGroupHasExistingQueueFile
                            ? newlyImportedPaths!.Contains(item.Path)
                            : !item.Recommendation.Contains("keeper", StringComparison.OrdinalIgnoreCase)) &&
                        !IsEncodeRowCurrentlyRunning(row);
                    if (isExactCandidate && !meta.DuplicateExclusionOverridden)
                    {
                        meta.StatusBeforeDuplicateExclusion = dgvEncodeQueue.Columns.Contains("colStatus")
                            ? row.Cells["colStatus"].Value?.ToString() ?? "Queued"
                            : "Queued";
                        meta.ExcludedFromEncodeAsDuplicate = true;
                        _estimatedSizeMap.Remove(item.Path);
                        SetEncodeRowState(
                            row,
                            "Excluded - exact duplicate",
                            "",
                            "",
                            "Exact byte-for-byte duplicate soft-excluded from encoding. The source file was not changed.");
                    }
                    ApplyDuplicateCells(row, meta);
                }
            }

            ApplyDuplicateCandidateViewFilter();
            RemoveSoftExcludedRowsFromActiveEncodeQueue();

            UpdateDuplicateSummary(result);
            MarkQueueTotalsDirty();
            UpdateSizeTotals(force: true);
            dgvEncodeQueue.Invalidate();
        }

        private DuplicateScanOptions CreateDuplicateScanOptions()
        {
            var referenceFolders = new List<string>();
            if (!string.IsNullOrWhiteSpace(_config.DuplicateReferenceFolder))
                referenceFolders.Add(_config.DuplicateReferenceFolder);

            return new DuplicateScanOptions(
                DuplicateScanModes.Normalize(comboDuplicateScanMode?.SelectedItem?.ToString() ?? _config.DuplicateScanMode),
                referenceFolders,
                _config.DuplicateKeeperPreferences.Clone());
        }

        private void RescoreDuplicateKeeperRecommendations()
        {
            if (_lastDuplicateScanResult == null)
                return;

            var groups = _lastDuplicateScanResult.Groups
                .Select(group => DuplicateKeeperScoringService.Apply(
                    group,
                    _config.DuplicateKeeperPreferences,
                    preserveManualSelection: true))
                .ToList();
            _lastDuplicateScanResult = BuildDuplicateScanResult(groups);
            ApplyDuplicateScanResult(_lastDuplicateScanResult);
            ShowStatusInfo($"Duplicate keeper recommendations updated using {_config.DuplicateKeeperPreferences.Profile}.");
        }

        private void ApplyDuplicateCandidateViewFilter()
        {
            if (dgvEncodeQueue == null || _encodingActive)
                return;

            bool onlyDuplicates = chkOnlyDuplicateCandidates?.Checked == true && _lastDuplicateScanResult != null;
            try
            {
                dgvEncodeQueue.CurrentCell = null;
                foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
                {
                    if (!row.IsNewRow)
                        row.Visible = !onlyDuplicates || (row.Tag as RowMeta)?.DuplicateGroupId != null;
                }
            }
            catch (InvalidOperationException)
            {
                // Visibility is a convenience view. Never let it invalidate scan results.
            }
        }

        private int GetDuplicateSoftExcludedCount()
        {
            return dgvEncodeQueue.Rows
                .Cast<DataGridViewRow>()
                .Count(row => !row.IsNewRow && (row.Tag as RowMeta)?.ExcludedFromEncodeAsDuplicate == true);
        }

        private bool IsEncodeRowCurrentlyRunning(DataGridViewRow row)
        {
            return ReferenceEquals(_activeEncodeRow, row) ||
                   _activeEncodeRows.Contains(row) ||
                   _runningEncodeJobs.ContainsKey(row);
        }

        private void RemoveSoftExcludedRowsFromActiveEncodeQueue()
        {
            lock (_activeEncodeQueueLock)
            {
                _activeEncodeQueue?.RemoveAll(row =>
                    row?.Tag is RowMeta meta && meta.ExcludedFromEncodeAsDuplicate);
            }
        }

        private void ResetDuplicateAnnotations(string status)
        {
            _lastDuplicateScanResult = null;
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow)
                    continue;

                var meta = EnsureRowMeta(row);
                if (meta.ExcludedFromEncodeAsDuplicate)
                {
                    meta.ExcludedFromEncodeAsDuplicate = false;
                    if (row.DataGridView == dgvEncodeQueue)
                        SetEncodeRowState(row, meta.StatusBeforeDuplicateExclusion, "", "", "Duplicate exclusion cleared.");
                }
                meta.DuplicateGroupId = null;
                meta.DuplicateConfidence = status;
                meta.DuplicateConfidenceScore = 0;
                meta.DuplicateRecommendation = "";
                meta.DuplicateReason = "";
                ApplyDuplicateCells(row, meta);
            }
            UpdateDuplicateSummary(null);
        }

        private void ClearDuplicateAnnotations(bool resetResult = true)
        {
            if (resetResult)
                _lastDuplicateScanResult = null;

            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow)
                    continue;

                if (row.Tag is RowMeta meta)
                {
                    if (meta.ExcludedFromEncodeAsDuplicate)
                    {
                        meta.ExcludedFromEncodeAsDuplicate = false;
                        if (row.DataGridView == dgvEncodeQueue)
                            SetEncodeRowState(row, meta.StatusBeforeDuplicateExclusion, "", "", "Duplicate exclusion cleared.");
                    }
                    if (resetResult)
                        meta.DuplicateExclusionOverridden = false;
                    meta.DuplicateGroupId = null;
                    meta.DuplicateConfidence = "";
                    meta.DuplicateConfidenceScore = 0;
                    meta.DuplicateRecommendation = "";
                    meta.DuplicateReason = "";
                }

                ApplyDuplicateCells(row, row.Tag as RowMeta);
                if (!row.IsNewRow)
                    row.Visible = true;
            }
            UpdateDuplicateSummary(resetResult ? null : _lastDuplicateScanResult);
        }

        private void ApplyDuplicateCells(DataGridViewRow row, RowMeta? meta)
        {
            if (dgvEncodeQueue.Columns.Contains("colDuplicate"))
                row.Cells["colDuplicate"].Value = meta?.DuplicateGroupId != null ? $"Group {meta.DuplicateGroupId}" : meta?.DuplicateConfidence ?? "";
            if (dgvEncodeQueue.Columns.Contains("colDuplicateConfidence"))
                row.Cells["colDuplicateConfidence"].Value = meta?.DuplicateGroupId != null ? $"{meta.DuplicateConfidence} ({meta.DuplicateConfidenceScore}%)" : "";
            if (dgvEncodeQueue.Columns.Contains("colDuplicateAction"))
                row.Cells["colDuplicateAction"].Value = meta?.DuplicateRecommendation ?? "";

            string tooltip = meta?.DuplicateGroupId != null
                ? $"{meta.DuplicateReason}. {meta.DuplicateRecommendation}."
                : "";
            if (dgvEncodeQueue.Columns.Contains("colDuplicate"))
                row.Cells["colDuplicate"].ToolTipText = tooltip;
            if (dgvEncodeQueue.Columns.Contains("colDuplicateAction"))
                row.Cells["colDuplicateAction"].ToolTipText = tooltip;
        }

        private void UpdateDuplicateSummary(DuplicateScanResult? result)
        {
            if (_summaryDuplicateGroupsValue != null)
                _summaryDuplicateGroupsValue.Text = result == null ? "--" : $"{result.Groups.Count:N0}";
            if (_summaryDuplicateFilesValue != null)
                _summaryDuplicateFilesValue.Text = result == null ? "--" : $"{result.DuplicateFiles:N0}";
            if (_summaryDuplicateRecoverableValue != null)
                _summaryDuplicateRecoverableValue.Text = result == null || result.PotentialRecoverableBytes <= 0
                    ? "--"
                    : FormatSize(result.PotentialRecoverableBytes);
            UpdateDuplicateFinderUiState();
            if (_duplicateCleanupAutoDisablePending && result != null && result.Groups.Count == 0)
                PromptToDisableDuplicateFinderAfterCleanup();
        }

        private void UpdateDuplicateFinderUiState()
        {
            if (lblDuplicateFinderStatus == null)
                return;

            bool enabled = chkFindDuplicates?.Checked == true;
            comboDuplicateScanMode.Enabled = enabled;
            chkOnlyDuplicateCandidates.Enabled = enabled;
            btnAnalyzeDuplicatesNow.Enabled = enabled;
            btnOpenDuplicateManager.Enabled = _lastDuplicateScanResult?.Groups.Count > 0;
            btnClearDuplicateResults.Enabled = _lastDuplicateScanResult != null;

            if (!enabled)
            {
                lblDuplicateFinderStatus.Text = "Duplicate Finder is off";
                lblDuplicateFinderStatus.ForeColor = SystemColors.GrayText;
                UpdateDuplicateFinderHeaderStatus();
                return;
            }

            string mode = DuplicateScanModes.Normalize(comboDuplicateScanMode?.SelectedItem?.ToString() ?? _config.DuplicateScanMode);
            if (_lastDuplicateScanResult == null)
            {
                lblDuplicateFinderStatus.Text = $"Active: {mode}. No scan results yet.";
                lblDuplicateFinderStatus.ForeColor = Color.FromArgb(80, 80, 80);
                UpdateDuplicateFinderHeaderStatus();
                return;
            }

            lblDuplicateFinderStatus.Text = _lastDuplicateScanResult.Groups.Count == 0
                ? $"Active: {mode}. No duplicate groups found."
                : $"Active: {mode}. {_lastDuplicateScanResult.Groups.Count:N0} group(s), {_lastDuplicateScanResult.DuplicateFiles:N0} duplicate(s), {GetDuplicateSoftExcludedCount():N0} exact duplicate(s) excluded, {FormatSize(_lastDuplicateScanResult.PotentialRecoverableBytes)} recoverable.";
            lblDuplicateFinderStatus.ForeColor = _lastDuplicateScanResult.Groups.Count == 0
                ? SystemColors.GrayText
                : Color.FromArgb(0, 102, 153);
            UpdateDuplicateFinderHeaderStatus();
        }

        private void SetDuplicateFinderWorkingStatus(string text)
        {
            if (lblDuplicateFinderStatus == null)
                return;

            lblDuplicateFinderStatus.Text = text;
            lblDuplicateFinderStatus.ForeColor = Color.FromArgb(146, 64, 14);
            UpdateDuplicateFinderHeaderStatus();
        }

        private void UpdateDuplicateReferenceFolderUi()
        {
            if (lblDuplicateReferenceStatus == null ||
                btnSetDuplicateReferenceFolder == null ||
                btnClearDuplicateReferenceFolder == null)
            {
                return;
            }

            bool showControls = _config.ShowDuplicateReferenceFolderOnMain;
            lblDuplicateReferenceStatus.Visible = showControls;
            btnSetDuplicateReferenceFolder.Visible = showControls;
            btnClearDuplicateReferenceFolder.Visible = showControls;
            if (!showControls)
                return;

            string folder = _config.DuplicateReferenceFolder?.Trim() ?? string.Empty;
            bool hasFolder = Directory.Exists(folder);
            lblDuplicateReferenceStatus.Text = hasFolder
                ? $"Reference Folder: {folder}"
                : "Reference Folder: Not set";
            lblDuplicateReferenceStatus.ForeColor = hasFolder
                ? Color.FromArgb(0, 102, 153)
                : SystemColors.GrayText;
            btnSetDuplicateReferenceFolder.Text = hasFolder ? "Change Reference Folder" : "Set Reference Folder";
            btnClearDuplicateReferenceFolder.Enabled = !string.IsNullOrWhiteSpace(folder);
            _uiToolTip.SetToolTip(lblDuplicateReferenceStatus, "Files in the reference folder are protected and preferred as keepers during duplicate review.");
            _uiToolTip.SetToolTip(btnSetDuplicateReferenceFolder, "Choose the folder whose files should be protected and preferred as keepers.");
            _uiToolTip.SetToolTip(btnClearDuplicateReferenceFolder, "Remove the configured duplicate reference folder.");
        }

        private void SetDuplicateReferenceFolder_Click(object? sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select the duplicate reference folder. Files in this folder are protected and preferred as keepers.",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_config.DuplicateReferenceFolder)
                    ? _config.DuplicateReferenceFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _config.DuplicateReferenceFolder = dialog.SelectedPath;
            _config.Save(_configPath);
            UpdateDuplicateReferenceFolderUi();
            if (chkFindDuplicates?.Checked == true)
                StartDuplicateScanIfEnabled();
            ShowStatusInfo($"Duplicate reference folder set: {dialog.SelectedPath}");
        }

        private void ClearDuplicateReferenceFolder_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_config.DuplicateReferenceFolder))
                return;

            _config.DuplicateReferenceFolder = "";
            _config.Save(_configPath);
            UpdateDuplicateReferenceFolderUi();
            if (chkFindDuplicates?.Checked == true)
                StartDuplicateScanIfEnabled();
            ShowStatusInfo("Duplicate reference folder cleared.");
        }

        private void ClearDuplicateResults_Click(object? sender, EventArgs e)
        {
            _duplicateCleanupAutoDisablePending = false;
            ClearDuplicateAnnotations();
            ShowStatusInfo("Duplicate Finder results cleared. No files were removed.");
            UpdateDuplicateFinderUiState();
        }

        private void PromptToDisableDuplicateFinderAfterCleanup()
        {
            if (!_duplicateCleanupAutoDisablePending)
                return;

            _duplicateCleanupAutoDisablePending = false;
            if (chkFindDuplicates?.Checked != true ||
                chkAutoDisableDuplicateFinder?.Checked != true ||
                _encodingActive)
            {
                return;
            }

            var choice = MessageBox.Show(
                this,
                "Duplicate cleanup is complete and no duplicate groups remain. Turn off Duplicate Finder mode?",
                "Turn Off Duplicate Finder?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (choice != DialogResult.Yes)
                return;

            chkFindDuplicates.Checked = false;
            _config.FindDuplicatesOnImport = false;
            _config.Save(_configPath);
            ClearDuplicateAnnotations();
            ShowStatusInfo("Duplicate Finder mode turned off.");
            UpdateDuplicateFinderUiState();
        }

        private void ShowDuplicateManager_Click(object? sender, EventArgs e)
        {
            if (_lastDuplicateScanResult == null || _lastDuplicateScanResult.Groups.Count == 0)
            {
                ShowStatusInfo("No duplicate scan results are available.");
                return;
            }

            using var dialog = new Form
            {
                Text = "Duplicate Manager",
                StartPosition = FormStartPosition.CenterParent,
                MinimumSize = new Size(900, 520),
                Size = new Size(1100, 650)
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window
            };

            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Delete", HeaderText = "Delete?", Width = 58 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Group", HeaderText = "Group", Width = 70 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Confidence", HeaderText = "Confidence", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "MatchMethod", HeaderText = "Match", Width = 125 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Frames", HeaderText = "Frames", Width = 70 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AvgDistance", HeaderText = "Avg Distance", Width = 92 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DurationDelta", HeaderText = "Delta", Width = 72 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Recommendation", HeaderText = "Recommendation", Width = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "KeeperReason", HeaderText = "Keeper Reason", Width = 125 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reference", HeaderText = "Reference", Width = 82 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", Width = 220 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Size", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Codec", HeaderText = "Codec", Width = 80 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Resolution", HeaderText = "Resolution", Width = 95 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "Duration", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bitrate", HeaderText = "Bitrate", Width = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = "Evidence", Width = 220 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            foreach (DataGridViewColumn column in grid.Columns)
                column.ReadOnly = !string.Equals(column.Name, "Delete", StringComparison.OrdinalIgnoreCase);

            grid.CurrentCellDirtyStateChanged += (_, __) =>
            {
                if (grid.IsCurrentCellDirty)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += (_, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Delete")
                    return;

                var row = grid.Rows[e.RowIndex];
                if (row.Cells["Delete"].ReadOnly)
                    row.Cells["Delete"].Value = false;
            };

            grid.Tag = DuplicateFilterAll;
            RefreshDuplicateManagerGrid(grid);
            InitializeDuplicateManagerContextMenu(dialog, grid);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8)
            };

            var actionPanel = CreateDuplicateActionPanel(dialog, grid);
            var headerPanel = CreateDuplicateManagerHeaderPanel(grid);
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 90 };
            var selectInQueue = new Button { Text = "Select in Queue", Width = 120 };
            var openLocation = new Button { Text = "Open Location", Width = 112 };
            var reviewGroup = new Button
            {
                Text = "Review Duplicate Videos",
                Width = 170,
                Font = new Font(Font, FontStyle.Bold)
            };
            var exportReport = new Button { Text = "Export Report", Width = 112 };
            selectInQueue.Click += (_, __) =>
            {
                SelectDuplicateManagerRowsInQueue(grid);
                dialog.Close();
            };
            openLocation.Click += (_, __) => OpenDuplicateManagerSelectedLocation(grid);
            reviewGroup.Click += (_, __) => ShowDuplicateGroupReview(dialog, grid);
            exportReport.Click += (_, __) => ExportDuplicateReport();

            bar.Controls.Add(close);
            bar.Controls.Add(exportReport);
            bar.Controls.Add(reviewGroup);
            bar.Controls.Add(openLocation);
            bar.Controls.Add(selectInQueue);
            dialog.Controls.Add(grid);
            dialog.Controls.Add(bar);
            dialog.Controls.Add(actionPanel);
            dialog.Controls.Add(headerPanel);
            dialog.AcceptButton = close;
            dialog.ShowDialog(this);
        }

        private Control CreateDuplicateManagerHeaderPanel(DataGridView grid)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                Padding = new Padding(8),
                BackColor = SystemColors.Window
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));

            var title = new Label
            {
                Text = "Duplicate Groups",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 12, 4)
            };
            var summary = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 16, 4),
                Text = BuildDuplicateManagerSummary(DuplicateFilterAll)
            };
            var filterLabel = new Label
            {
                Text = "Filter:",
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 4, 6, 4)
            };
            var filter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Width = 160
            };
            filter.Items.AddRange(new object[]
            {
                DuplicateFilterAll,
                DuplicateFilterExact,
                DuplicateFilterStrong,
                DuplicateFilterReview,
                DuplicateFilterActionable,
                DuplicateFilterProtected
            });
            filter.SelectedItem = DuplicateFilterAll;
            filter.SelectedIndexChanged += (_, __) =>
            {
                string selectedFilter = filter.SelectedItem?.ToString() ?? DuplicateFilterAll;
                grid.Tag = selectedFilter;
                RefreshDuplicateManagerGrid(grid, selectedFilter);
                summary.Text = BuildDuplicateManagerSummary(selectedFilter);
            };

            panel.Controls.Add(title, 0, 0);
            panel.Controls.Add(summary, 1, 0);
            panel.Controls.Add(filterLabel, 2, 0);
            panel.Controls.Add(filter, 3, 0);
            return panel;
        }

        private void InitializeDuplicateManagerContextMenu(Form owner, DataGridView grid)
        {
            var menu = new ContextMenuStrip();
            var reviewGroup = menu.Items.Add("Review Duplicate Videos");
            menu.Items.Add(new ToolStripSeparator());
            var openLocation = menu.Items.Add("Open File Location");
            var openFile = menu.Items.Add("Open File");
            menu.Items.Add(new ToolStripSeparator());
            var deleteFile = menu.Items.Add("Delete File");

            reviewGroup.Click += (_, __) => ShowDuplicateGroupReview(owner, grid);
            openLocation.Click += (_, __) => OpenDuplicateManagerSelectedLocation(grid);
            openFile.Click += (_, __) => OpenDuplicateManagerSelectedFile(grid);
            deleteFile.Click += (_, __) => DeleteDuplicateManagerSelectedFile(owner, grid);

            menu.Opening += (_, e) =>
            {
                string? path = GetSelectedDuplicateManagerPath(grid);
                bool hasExistingFile = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
                reviewGroup.Enabled = hasExistingFile && FindDuplicateManagedFile(path!) != null;
                openLocation.Enabled = hasExistingFile;
                openFile.Enabled = hasExistingFile;
                deleteFile.Enabled = hasExistingFile && !_encodingActive && _config.AllowDuplicateRecycleBin;
                if (!hasExistingFile)
                    e.Cancel = true;
            };

            grid.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Right)
                    return;

                var hit = grid.HitTest(e.X, e.Y);
                if (hit.RowIndex < 0 || hit.RowIndex >= grid.Rows.Count)
                    return;

                grid.ClearSelection();
                grid.Rows[hit.RowIndex].Selected = true;
                grid.CurrentCell = grid.Rows[hit.RowIndex].Cells[Math.Max(0, hit.ColumnIndex)];
            };

            grid.ContextMenuStrip = menu;
        }

        private Control CreateDuplicateActionPanel(Form dialog, DataGridView grid)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 8,
                Padding = new Padding(8),
                BackColor = SystemColors.Control
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var lblRule = new Label
            {
                Text = "Rule:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 6, 0)
            };

            var rule = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 220,
                Anchor = AnchorStyles.Left
            };
            rule.Items.AddRange(new object[]
            {
                DuplicateRuleSuggested,
                DuplicateRuleLargest,
                DuplicateRuleSmallest,
                DuplicateRuleH264,
                DuplicateRuleH265,
                DuplicateRuleOtherCodec,
                DuplicateRuleLowerResolution,
                DuplicateRuleLowerBitrate,
                DuplicateRuleOlder,
                DuplicateRuleNewer,
                DuplicateRuleSelectedRows
            });
            rule.SelectedIndex = 0;

            var includeReviewOnly = new CheckBox
            {
                Text = "Show review-only",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 4, 12, 0)
            };
            _uiToolTip.SetToolTip(includeReviewOnly, "Review-only matches are visible for inspection but are never eligible for Recycle or Quarantine actions.");

            var preview = new Button { Text = "Preview Selection", Width = 120 };
            var recycle = new Button { Text = "Move Selected to Recycle Bin", Width = 190, Visible = _config.AllowDuplicateRecycleBin };
            var quarantine = new Button { Text = "Move Selected to Quarantine", Width = 200, Visible = _config.AllowDuplicateQuarantine };
            var deletePermanent = new Button { Text = "Delete Selected Permanently", Width = 190, Visible = _config.AllowDuplicatePermanentDelete };

            preview.Click += (_, __) => PreviewDuplicateAction(grid, rule.Text, includeReviewOnly.Checked);
            recycle.Click += (_, __) => ExecuteDuplicateFileAction(dialog, grid, rule.Text, DuplicateFileAction.Recycle);
            quarantine.Click += (_, __) => ExecuteDuplicateFileAction(dialog, grid, rule.Text, DuplicateFileAction.Quarantine);
            deletePermanent.Click += (_, __) => ExecuteDuplicateFileAction(dialog, grid, rule.Text, DuplicateFileAction.DeletePermanent);
            _uiToolTip.SetToolTip(preview, "Selects the files that match the current cleanup rule without moving or deleting anything.");
            _uiToolTip.SetToolTip(recycle, "Moves matching duplicate files to the Windows Recycle Bin.");
            _uiToolTip.SetToolTip(quarantine, "Moves matching duplicate files into the configured duplicate quarantine folder.");
            _uiToolTip.SetToolTip(deletePermanent, "Permanently deletes matching duplicate files. This cannot be undone from the Recycle Bin.");

            panel.Controls.Add(lblRule, 0, 0);
            panel.Controls.Add(rule, 1, 0);
            panel.Controls.Add(includeReviewOnly, 2, 0);
            panel.Controls.Add(preview, 3, 0);
            panel.Controls.Add(recycle, 4, 0);
            panel.Controls.Add(quarantine, 5, 0);
            panel.Controls.Add(deletePermanent, 6, 0);
            return panel;
        }

        private void PreviewDuplicateAction(DataGridView grid, string rule, bool includeReviewOnly)
        {
            bool usingCheckedRows = GetCheckedDuplicateManagerPaths(grid).Count > 0;
            var candidates = GetDuplicateActionCandidates(grid, rule, includeReviewOnly);
            grid.ClearSelection();
            foreach (var candidate in candidates)
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (string.Equals(row.Tag as string, candidate.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                        break;
                    }
                }
            }

            ShowStatusInfo(BuildDuplicateActionStatus(usingCheckedRows ? "Checked cleanup files selected" : "Rule preview selected", candidates));
        }

        private void ExecuteDuplicateFileAction(
            Form owner,
            DataGridView grid,
            string rule,
            DuplicateFileAction action)
        {
            if (_encodingActive)
            {
                ShowStatusInfo("Stop the active encode before moving or deleting duplicate files.");
                return;
            }

            if (!IsDuplicateActionEnabled(action))
            {
                ShowStatusInfo("This duplicate cleanup action is disabled in Settings.");
                return;
            }

            bool usingCheckedRows = GetCheckedDuplicateManagerPaths(grid).Count > 0;
            var candidates = GetDuplicateActionCandidates(grid, rule, includeReviewOnly: false);
            if (candidates.Count == 0)
            {
                ShowStatusInfo(usingCheckedRows
                    ? "No checked exact or strong visual duplicate files are eligible for cleanup."
                    : "No exact or strong visual duplicate files match the selected rule.");
                return;
            }

            if (_config.RequireDuplicateCleanupConfirmation)
            {
                string summary = BuildDuplicateActionSummary(candidates, GetDuplicateActionName(action));
                var confirm = MessageBox.Show(
                    owner,
                    summary,
                    "Confirm Duplicate File Action",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                    return;
            }

            string? quarantineRoot = null;
            if (action == DuplicateFileAction.Quarantine)
            {
                quarantineRoot = GetDuplicateQuarantineRoot(owner);
                if (string.IsNullOrWhiteSpace(quarantineRoot))
                    return;
            }

            int succeeded = 0;
            int failed = 0;
            var auditEntries = new List<DuplicateActionAuditEntry>();
            foreach (var candidate in candidates)
            {
                string destination = "";
                try
                {
                    if (action == DuplicateFileAction.Recycle)
                    {
                        FileSystem.DeleteFile(
                            candidate.Path,
                            UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin,
                            UICancelOption.ThrowException);
                    }
                    else if (action == DuplicateFileAction.Quarantine)
                    {
                        destination = BuildQuarantineDestination(quarantineRoot!, candidate);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(candidate.Path, destination);
                    }
                    else
                    {
                        File.Delete(candidate.Path);
                    }

                    if (_rowsByPath.TryGetValue(candidate.Path, out var row) && row.DataGridView == dgvEncodeQueue)
                        RemoveRowAndCleanup(row);
                    succeeded++;
                    auditEntries.Add(new DuplicateActionAuditEntry(
                        DateTime.Now,
                        action,
                        candidate.Group.Id,
                        candidate.Group.ConfidenceLabel,
                        candidate.Group.ConfidenceScore,
                        candidate.Item.Recommendation,
                        candidate.Path,
                        destination,
                        candidate.Item.LengthBytes,
                        "Succeeded",
                        ""));
                }
                catch (Exception ex)
                {
                    failed++;
                    ErrorLogService.Append(Application.StartupPath, "Duplicate file action failed", candidate.Path, ex);
                    auditEntries.Add(new DuplicateActionAuditEntry(
                        DateTime.Now,
                        action,
                        candidate.Group.Id,
                        candidate.Group.ConfidenceLabel,
                        candidate.Group.ConfidenceScore,
                        candidate.Item.Recommendation,
                        candidate.Path,
                        destination,
                        candidate.Item.LengthBytes,
                        "Failed",
                        ex.Message));
                }
            }

            string auditPath = AppendDuplicateActionAudit(auditEntries);
            owner.Close();
            ClearDuplicateAnnotations();
            _duplicateCleanupAutoDisablePending = succeeded > 0;
            if (dgvEncodeQueue.Rows.Count > 1 && chkFindDuplicates.Checked)
            {
                StartDuplicateScanIfEnabled();
            }
            else
            {
                PromptToDisableDuplicateFinderAfterCleanup();
            }

            string result = failed == 0
                ? $"{GetDuplicateActionName(action)} completed for {succeeded:N0} duplicate file(s). Audit: {Path.GetFileName(auditPath)}"
                : $"{GetDuplicateActionName(action)} completed for {succeeded:N0} file(s); {failed:N0} failed. See the error log. Audit: {Path.GetFileName(auditPath)}";
            ShowStatusInfo(result);
        }

        private void ExportDuplicateReport()
        {
            if (_lastDuplicateScanResult == null || _lastDuplicateScanResult.Groups.Count == 0)
            {
                ShowStatusInfo("No duplicate scan results are available to export.");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "Export Duplicate Report",
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"duplicate-report-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                File.WriteAllText(dialog.FileName, BuildDuplicateReportCsv(_lastDuplicateScanResult), Encoding.UTF8);
                ShowStatusInfo($"Duplicate report exported: {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Duplicate report export failed", dialog.FileName, ex);
                ShowStatusInfo("Duplicate report export failed. See the error log for details.");
            }
        }

        private void ExportDuplicateReport_Click(object? sender, EventArgs e)
        {
            ExportDuplicateReport();
        }

        private static string BuildDuplicateReportCsv(DuplicateScanResult result)
        {
            var sb = new StringBuilder();
            AppendCsvRow(
                sb,
                "Group",
                "Confidence",
                "ConfidenceScore",
                "MatchMethod",
                "FrameMatches",
                "FrameComparisons",
                "AverageHashDistance",
                "DurationDeltaSeconds",
                "Reason",
                "Recommendation",
                "KeeperReason",
                "ReferenceProtected",
                "Name",
                "Path",
                "SizeBytes",
                "Size",
                "Codec",
                "Resolution",
                "Duration",
                "BitrateKbps",
                "Created",
                "Modified");

            foreach (var group in result.Groups)
            {
                foreach (var item in group.Items)
                {
                    AppendCsvRow(
                        sb,
                        group.Id.ToString(),
                        group.ConfidenceLabel,
                        group.ConfidenceScore.ToString(),
                        group.MatchMethod,
                        group.FrameMatches > 0 ? group.FrameMatches.ToString() : "",
                        group.FrameComparisons > 0 ? group.FrameComparisons.ToString() : "",
                        group.FrameComparisons > 0 ? group.AverageHashDistance.ToString("0.##") : "",
                        group.DurationDeltaSeconds > 0 ? group.DurationDeltaSeconds.ToString("0.##") : "",
                        group.Reason,
                        item.Recommendation,
                        item.KeeperReason,
                        item.IsReferenceProtected ? "Yes" : "No",
                        Path.GetFileName(item.Path),
                        item.Path,
                        item.LengthBytes.ToString(),
                        FormatSize(item.LengthBytes),
                        item.VideoCodec,
                        item.Width > 0 && item.Height > 0 ? $"{item.Width}x{item.Height}" : "",
                        FormatTimeSpan(item.DurationSeconds),
                        item.BitrateKbps > 0 ? item.BitrateKbps.ToString() : "",
                        item.Created.ToString("yyyy-MM-dd HH:mm:ss"),
                        item.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                }
            }

            return sb.ToString();
        }

        private static void AppendCsvRow(StringBuilder sb, params string[] values)
        {
            sb.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }

        private static string EscapeCsv(string? value)
        {
            value ??= string.Empty;
            bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
            value = value.Replace("\"", "\"\"");
            return mustQuote ? $"\"{value}\"" : value;
        }

        private string AppendDuplicateActionAudit(IReadOnlyCollection<DuplicateActionAuditEntry> entries)
        {
            string logPath = GetDuplicateActionAuditPath();
            string logDir = Path.GetDirectoryName(logPath) ?? AppPaths.DataDirectory;

            try
            {
                Directory.CreateDirectory(logDir);
                bool writeHeader = !File.Exists(logPath) || new FileInfo(logPath).Length == 0;
                var sb = new StringBuilder();
                if (writeHeader)
                {
                    AppendCsvRow(
                        sb,
                        "LocalTime",
                        "Action",
                        "Group",
                        "Confidence",
                        "ConfidenceScore",
                        "Recommendation",
                        "SourcePath",
                        "DestinationPath",
                        "SizeBytes",
                        "Size",
                        "Status",
                        "Message");
                }

                foreach (var entry in entries)
                {
                    AppendCsvRow(
                        sb,
                        entry.LocalTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        entry.Action.ToString(),
                        entry.GroupId.ToString(),
                        entry.ConfidenceLabel,
                        entry.ConfidenceScore.ToString(),
                        entry.Recommendation,
                        entry.SourcePath,
                        entry.DestinationPath,
                        entry.SizeBytes.ToString(),
                        FormatSize(entry.SizeBytes),
                        entry.Status,
                        entry.Message);
                }

                File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Duplicate action audit append failed", logPath, ex);
            }

            return logPath;
        }

        private string GetDuplicateActionAuditPath()
        {
            return Path.Combine(AppPaths.DataDirectory, "logs", "duplicate-actions.csv");
        }

        private void ViewDuplicateActionLogToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            string logPath = GetDuplicateActionAuditPath();
            using var frm = new Form
            {
                Text = "Duplicate Action Log",
                StartPosition = FormStartPosition.CenterParent,
                Width = 1000,
                Height = 650
            };

            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            var btnRefresh = new Button { Text = "Refresh", Width = 90 };
            var btnOpenFolder = new Button { Text = "Open Folder", Width = 110 };
            var btnCopyPath = new Button { Text = "Copy Path", Width = 95 };
            var btnClear = new Button { Text = "Clear Log", Width = 95 };
            var btnClose = new Button { Text = "Close", Width = 90 };
            bar.Controls.AddRange(new Control[] { btnRefresh, btnOpenFolder, btnCopyPath, btnClear, btnClose });

            var lblPath = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = logPath
            };

            var txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9F)
            };

            panel.Controls.Add(bar, 0, 0);
            panel.Controls.Add(lblPath, 0, 1);
            panel.Controls.Add(txtLog, 0, 2);
            frm.Controls.Add(panel);

            void LoadLog()
            {
                lblPath.Text = logPath;
                try
                {
                    txtLog.Text = File.Exists(logPath)
                        ? File.ReadAllText(logPath)
                        : "No duplicate file actions have been logged yet.";
                    txtLog.SelectionStart = txtLog.TextLength;
                    txtLog.ScrollToCaret();
                }
                catch (Exception ex)
                {
                    txtLog.Text = $"Unable to read duplicate action log:{Environment.NewLine}{ex}";
                }
            }

            btnRefresh.Click += (_, __) => LoadLog();
            btnOpenFolder.Click += (_, __) =>
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
            };
            btnCopyPath.Click += (_, __) => Clipboard.SetText(logPath);
            btnClear.Click += (_, __) =>
            {
                var ok = MessageBox.Show(
                    frm,
                    "Clear the duplicate action log?",
                    "Confirm Clear",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (ok != DialogResult.Yes)
                    return;

                try
                {
                    var dir = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(logPath, string.Empty);
                    LoadLog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(frm, ex.Message, "Unable to Clear Log", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            btnClose.Click += (_, __) => frm.Close();

            LoadLog();
            frm.ShowDialog(this);
        }

        private void ShowDuplicateGroupReview(IWin32Window owner, DataGridView grid)
        {
            string? path = GetSelectedDuplicateManagerPath(grid);
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowStatusInfo("Select a duplicate file first.");
                return;
            }

            var managed = FindDuplicateManagedFile(path);
            if (managed == null)
            {
                ShowStatusInfo("The selected file is no longer part of the duplicate scan results.");
                return;
            }

            var reviewGroups = _lastDuplicateScanResult?.Groups.ToList() ?? new List<DuplicateGroup>();
            int currentIndex = Math.Max(0, reviewGroups.FindIndex(group => group.Id == managed.Group.Id));

            using var dialog = new Form
            {
                Text = $"Review Duplicate Group {managed.Group.Id}",
                StartPosition = FormStartPosition.CenterParent,
                KeyPreview = true,
                MinimumSize = new Size(920, 620),
                Size = new Size(1120, 760)
            };

            var header = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 98,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = SystemColors.Window,
                Text = BuildDuplicateGroupReviewHeader(managed.Group, currentIndex + 1, Math.Max(1, reviewGroups.Count))
            };

            var body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(10),
                BackColor = SystemColors.Control
            };

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8),
                BackColor = SystemColors.Control
            };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 90 };
            var next = new Button { Text = "Next >", Width = 90 };
            var previous = new Button { Text = "< Previous", Width = 90 };
            footer.Controls.Add(close);
            footer.Controls.Add(next);
            footer.Controls.Add(previous);

            dialog.Controls.Add(body);
            dialog.Controls.Add(footer);
            dialog.Controls.Add(header);
            dialog.AcceptButton = close;

            async Task LoadCurrentGroupAsync()
            {
                if (_lastDuplicateScanResult == null || _lastDuplicateScanResult.Groups.Count == 0)
                {
                    dialog.Close();
                    return;
                }

                reviewGroups = _lastDuplicateScanResult.Groups.ToList();
                currentIndex = Math.Clamp(currentIndex, 0, reviewGroups.Count - 1);
                var group = reviewGroups[currentIndex];
                dialog.Text = $"Review Duplicate Group {group.Id}";
                header.Text = BuildDuplicateGroupReviewHeader(group, currentIndex + 1, reviewGroups.Count);
                previous.Enabled = reviewGroups.Count > 1;
                next.Enabled = reviewGroups.Count > 1;

                try
                {
                    await PopulateDuplicateGroupReviewAsync(
                        body,
                        group,
                        keeperPath =>
                    {
                        MarkDuplicateReviewKeeper(group.Id, keeperPath);
                        RefreshDuplicateManagerGrid(grid);
                        reviewGroups = _lastDuplicateScanResult?.Groups.ToList() ?? new List<DuplicateGroup>();
                        currentIndex = Math.Max(0, reviewGroups.FindIndex(updated => updated.Id == group.Id));
                        _ = LoadCurrentGroupAsync();
                    },
                        async (cleanupGroup, cleanupItem, action) =>
                    {
                        bool succeeded = ExecuteDuplicateReviewFileAction(dialog, grid, cleanupGroup, cleanupItem, action);
                        if (!succeeded)
                            return;

                        reviewGroups = _lastDuplicateScanResult?.Groups.ToList() ?? new List<DuplicateGroup>();
                        currentIndex = Math.Clamp(currentIndex, 0, Math.Max(0, reviewGroups.Count - 1));
                        await LoadCurrentGroupAsync();
                    });
                }
                catch (Exception ex)
                {
                    ErrorLogService.Append(Application.StartupPath, "Duplicate group review failed", exception: ex);
                    ShowStatusInfo("Duplicate group review failed. See the error log for details.");
                }
            }

            async Task MoveGroupAsync(int delta)
            {
                if (reviewGroups.Count <= 1)
                    return;

                currentIndex = (currentIndex + delta + reviewGroups.Count) % reviewGroups.Count;
                await LoadCurrentGroupAsync();
            }

            previous.Click += async (_, __) => await MoveGroupAsync(-1);
            next.Click += async (_, __) => await MoveGroupAsync(1);
            dialog.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Left)
                {
                    e.Handled = true;
                    await MoveGroupAsync(-1);
                }
                else if (e.KeyCode == Keys.Right)
                {
                    e.Handled = true;
                    await MoveGroupAsync(1);
                }
            };

            dialog.Shown += async (_, __) => await LoadCurrentGroupAsync();
            dialog.FormClosed += (_, __) => DisposeDuplicateReviewImages(body);

            dialog.ShowDialog(owner);
        }

        private static string BuildDuplicateGroupReviewHeader(DuplicateGroup group, int currentGroup, int totalGroups)
        {
            string frames = group.FrameComparisons > 0
                ? $"{group.FrameMatches}/{group.FrameComparisons} frames, avg distance {group.AverageHashDistance:0.#}, duration delta {group.DurationDeltaSeconds:0.#}s"
                : "Exact file hash";
            string actionText = IsActionableDuplicateGroup(group)
                ? "Eligible for duplicate actions"
                : "Review only - duplicate actions are disabled";
            string keeperText = group.Items.Any(item =>
                    string.Equals(item.Recommendation, "Review required", StringComparison.OrdinalIgnoreCase))
                ? "Keeper recommendation: review required (scores too close)"
                : $"Keeper recommendation: {Path.GetFileName(group.Items.FirstOrDefault(item => IsKeeperRecommendation(item.Recommendation))?.Path ?? "none")}";

            return $"Group {group.Id}   Review {currentGroup}/{totalGroups}   {group.ConfidenceLabel} ({group.ConfidenceScore}%)   {group.MatchMethod}{Environment.NewLine}" +
                   $"{frames}{Environment.NewLine}" +
                   $"{actionText}   {keeperText}   {group.Reason}{Environment.NewLine}" +
                   "Use Left/Right arrow keys or the buttons below to review duplicate groups.";
        }

        private async Task PopulateDuplicateGroupReviewAsync(
            FlowLayoutPanel body,
            DuplicateGroup group,
            Action<string> keepSelected,
            Func<DuplicateGroup, DuplicateItem, DuplicateFileAction, Task> cleanupSelected)
        {
            DisposeDuplicateReviewImages(body);
            body.Controls.Clear();
            DuplicatePreviewCacheService.PruneOlderThan(AppPaths.UserDataDirectory, TimeSpan.FromDays(30));

            foreach (var item in group.Items)
            {
                var card = CreateDuplicateReviewCard(group, item, keepSelected, cleanupSelected);
                body.Controls.Add(card.Panel);

                if (!File.Exists(item.Path))
                {
                    card.Status.Text = "File no longer exists";
                    continue;
                }

                try
                {
                    string? thumbnailPath = await CreateDuplicateReviewThumbnailAsync(item);
                    if (!string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath))
                    {
                        using var stream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var image = Image.FromStream(stream);
                        card.Picture.Image = new Bitmap(image);
                        card.Status.Text = "Midpoint preview";
                    }
                    else
                    {
                        card.Status.Text = "Preview unavailable";
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogService.Append(Application.StartupPath, "Duplicate thumbnail extraction failed", item.Path, ex);
                    card.Status.Text = "Preview unavailable";
                }
            }
        }

        private (Panel Panel, PictureBox Picture, Label Status) CreateDuplicateReviewCard(
            DuplicateGroup group,
            DuplicateItem item,
            Action<string> keepSelected,
            Func<DuplicateGroup, DuplicateItem, DuplicateFileAction, Task> cleanupSelected)
        {
            var panel = new Panel
            {
                Width = 332,
                Height = 456,
                Margin = new Padding(8),
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };

            var picture = new PictureBox
            {
                Dock = DockStyle.Top,
                Height = 172,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(8, 6, 8, 0),
                Text = Path.GetFileName(item.Path),
                AutoEllipsis = true,
                Font = new Font(Font, FontStyle.Bold)
            };

            var details = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 2, 8, 4),
                Text = BuildDuplicateReviewItemDetails(item),
                AutoEllipsis = true
            };
            _uiToolTip.SetToolTip(details, $"{item.KeeperReason}{Environment.NewLine}{item.Path}");

            bool isSelectedKeeper = IsKeeperRecommendation(item.Recommendation);
            var keep = new Button
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                Text = isSelectedKeeper ? "Keep selected" : "Keep",
                Enabled = File.Exists(item.Path),
                Margin = new Padding(8)
            };

            if (isSelectedKeeper)
            {
                keep.UseVisualStyleBackColor = false;
                keep.FlatStyle = FlatStyle.Flat;
                keep.FlatAppearance.BorderColor = Color.FromArgb(27, 94, 32);
                keep.FlatAppearance.BorderSize = 1;
                keep.BackColor = Color.FromArgb(46, 125, 50);
                keep.ForeColor = Color.White;
                _uiToolTip.SetToolTip(keep, "This file is currently selected as the keeper for this duplicate group.");
            }
            keep.Click += (_, __) => keepSelected(item.Path);

            var play = new Button
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                Text = "Play Video",
                Enabled = File.Exists(item.Path),
                Margin = new Padding(8)
            };
            play.Click += (_, __) => PlayDuplicateReviewVideo(item.Path);
            _uiToolTip.SetToolTip(play, "Open this video with the default media player.");

            var cleanupPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 62,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(6, 2, 6, 2)
            };
            AddReviewCleanupButton(cleanupPanel, group, item, DuplicateFileAction.Recycle, "Recycle", cleanupSelected);
            AddReviewCleanupButton(cleanupPanel, group, item, DuplicateFileAction.Quarantine, "Quarantine", cleanupSelected);
            AddReviewCleanupButton(cleanupPanel, group, item, DuplicateFileAction.DeletePermanent, "Delete", cleanupSelected);

            var status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Padding = new Padding(8, 3, 8, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Loading preview..."
            };

            panel.Controls.Add(details);
            panel.Controls.Add(status);
            if (cleanupPanel.Controls.Count > 0)
                panel.Controls.Add(cleanupPanel);
            panel.Controls.Add(keep);
            panel.Controls.Add(play);
            panel.Controls.Add(title);
            panel.Controls.Add(picture);
            return (panel, picture, status);
        }

        private void PlayDuplicateReviewVideo(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowStatusInfo("The selected duplicate video no longer exists.");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Open duplicate review video failed", path, ex);
                ShowStatusInfo("Could not open the duplicate video with the default media player.");
            }
        }

        private void AddReviewCleanupButton(
            FlowLayoutPanel panel,
            DuplicateGroup group,
            DuplicateItem item,
            DuplicateFileAction action,
            string text,
            Func<DuplicateGroup, DuplicateItem, DuplicateFileAction, Task> cleanupSelected)
        {
            if (!IsDuplicateActionEnabled(action))
                return;

            bool enabled = CanCleanupDuplicateReviewItem(group, item);
            var button = new Button
            {
                Text = text,
                Width = action == DuplicateFileAction.DeletePermanent ? 86 : 92,
                Height = 26,
                Enabled = enabled && File.Exists(item.Path),
                Margin = new Padding(2)
            };
            button.Click += async (_, __) => await cleanupSelected(group, item, action);
            _uiToolTip.SetToolTip(button, enabled
                ? $"{GetDuplicateActionName(action)} this duplicate file."
                : "Keepers, protected references, and review-only matches cannot be cleaned up from review.");
            panel.Controls.Add(button);
        }

        private static string BuildDuplicateReviewItemDetails(DuplicateItem item)
        {
            string reference = item.IsReferenceProtected ? "Protected reference" : "Queue file";
            string resolution = item.Width > 0 && item.Height > 0 ? $"{item.Width}x{item.Height}" : "Unknown resolution";
            string bitrate = item.BitrateKbps > 0 ? $"{item.BitrateKbps:N0} kbps" : "Unknown bitrate";

            return $"{item.Recommendation}   {reference}{Environment.NewLine}" +
                   $"{FormatSize(item.LengthBytes)}   {item.VideoCodec}   {resolution}{Environment.NewLine}" +
                   $"{FormatTimeSpan(item.DurationSeconds)}   {bitrate}{Environment.NewLine}" +
                   $"{item.KeeperReason}{Environment.NewLine}" +
                   item.Path;
        }

        private static bool CanCleanupDuplicateReviewItem(DuplicateGroup group, DuplicateItem item)
        {
            return IsActionableDuplicateGroup(group) &&
                   !item.IsReferenceProtected &&
                   string.Equals(item.Recommendation, "Trash candidate", StringComparison.OrdinalIgnoreCase);
        }

        private bool ExecuteDuplicateReviewFileAction(
            Form owner,
            DataGridView managerGrid,
            DuplicateGroup group,
            DuplicateItem item,
            DuplicateFileAction action)
        {
            if (_encodingActive)
            {
                ShowStatusInfo("Stop the active encode before moving or deleting duplicate files.");
                return false;
            }

            if (!IsDuplicateActionEnabled(action))
            {
                ShowStatusInfo("This duplicate cleanup action is disabled in Settings.");
                return false;
            }

            if (!CanCleanupDuplicateReviewItem(group, item))
            {
                ShowStatusInfo("This file cannot be cleaned up from review because it is a keeper, protected reference, or review-only match.");
                return false;
            }

            if (!File.Exists(item.Path))
            {
                ShowStatusInfo("The selected duplicate file no longer exists.");
                return false;
            }

            var managed = FindDuplicateManagedFile(item.Path);
            if (managed == null)
            {
                ShowStatusInfo("The selected file is no longer part of the duplicate scan results.");
                return false;
            }

            if (_config.RequireDuplicateCleanupConfirmation)
            {
                var confirm = MessageBox.Show(
                    owner,
                    BuildSingleDuplicateActionSummary(managed, GetDuplicateActionName(action)),
                    "Confirm Duplicate File Action",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                    return false;
            }

            string destination = "";
            try
            {
                if (action == DuplicateFileAction.Recycle)
                {
                    FileSystem.DeleteFile(
                        item.Path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                }
                else if (action == DuplicateFileAction.Quarantine)
                {
                    string? quarantineRoot = GetDuplicateQuarantineRoot(owner);
                    if (string.IsNullOrWhiteSpace(quarantineRoot))
                        return false;

                    destination = BuildQuarantineDestination(quarantineRoot, managed);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(item.Path, destination);
                }
                else
                {
                    File.Delete(item.Path);
                }

                AppendDuplicateActionAudit(new[]
                {
                    new DuplicateActionAuditEntry(
                        DateTime.Now,
                        action,
                        managed.Group.Id,
                        managed.Group.ConfidenceLabel,
                        managed.Group.ConfidenceScore,
                        managed.Item.Recommendation,
                        item.Path,
                        destination,
                        managed.Item.LengthBytes,
                        "Succeeded",
                        "Cleaned up from Review Duplicate Videos")
                });

                RemoveDuplicateManagerDeletedFile(owner, managerGrid, managed);
                ShowStatusInfo($"{GetDuplicateActionName(action)} completed for {Path.GetFileName(item.Path)}.");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Duplicate review cleanup failed", item.Path, ex);
                AppendDuplicateActionAudit(new[]
                {
                    new DuplicateActionAuditEntry(
                        DateTime.Now,
                        action,
                        managed.Group.Id,
                        managed.Group.ConfidenceLabel,
                        managed.Group.ConfidenceScore,
                        managed.Item.Recommendation,
                        item.Path,
                        destination,
                        managed.Item.LengthBytes,
                        "Failed",
                        ex.Message)
                });
                ShowStatusInfo("Could not clean up the selected duplicate file. See the error log.");
                return false;
            }
        }

        private static string BuildSingleDuplicateActionSummary(DuplicateManagedFile managed, string actionName)
        {
            return $"This will {actionName} this duplicate file.{Environment.NewLine}{Environment.NewLine}" +
                   $"Group {managed.Group.Id}: {Path.GetFileName(managed.Path)} ({FormatSize(managed.Item.LengthBytes)}){Environment.NewLine}" +
                   managed.Path +
                   $"{Environment.NewLine}{Environment.NewLine}Continue?";
        }

        private void MarkDuplicateReviewKeeper(int groupId, string keeperPath)
        {
            if (_lastDuplicateScanResult == null)
                return;

            var groups = _lastDuplicateScanResult.Groups
                .Select(group => group.Id == groupId ? MarkDuplicateGroupKeeper(group, keeperPath) : group)
                .ToList();

            _lastDuplicateScanResult = BuildDuplicateScanResult(groups);
            ApplyDuplicateScanResult(_lastDuplicateScanResult);
            ShowStatusInfo($"Group {groupId}: {Path.GetFileName(keeperPath)} marked to keep.");
        }

        private static DuplicateGroup MarkDuplicateGroupKeeper(DuplicateGroup group, string keeperPath)
        {
            return DuplicateKeeperScoringService.ApplyManualKeeper(group, keeperPath);
        }

        private static bool IsKeeperRecommendation(string recommendation)
        {
            return string.Equals(recommendation, "Suggested keeper", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(recommendation, "Selected keeper", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(recommendation, "Protected keeper", StringComparison.OrdinalIgnoreCase);
        }

        private static void DisposeDuplicateReviewImages(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is PictureBox { Image: { } image } picture)
                {
                    picture.Image = null;
                    image.Dispose();
                }

                if (control.HasChildren)
                    DisposeDuplicateReviewImages(control);
            }
        }

        private async Task<string?> CreateDuplicateReviewThumbnailAsync(DuplicateItem item)
        {
            var tools = FfmpegToolResolver.Resolve(Application.StartupPath, _config.FfmpegPath, _config.FfprobePath);
            if (!File.Exists(tools.FfmpegPath))
                return null;

            string previewDir = DuplicatePreviewCacheService.GetPreviewDirectory(AppPaths.UserDataDirectory);
            Directory.CreateDirectory(previewDir);
            string thumbnailPath = DuplicatePreviewCacheService.GetThumbnailPath(AppPaths.UserDataDirectory, item.Path);

            var source = new FileInfo(item.Path);
            if (File.Exists(thumbnailPath) &&
                File.GetLastWriteTimeUtc(thumbnailPath) >= source.LastWriteTimeUtc)
            {
                return thumbnailPath;
            }

            double seekSeconds = item.DurationSeconds > 0
                ? Math.Max(0.1, item.DurationSeconds * 0.5)
                : 1.0;

            var startInfo = new ProcessStartInfo
            {
                FileName = tools.FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(seekSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(item.Path);
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add("scale=320:-2");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(thumbnailPath);

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20));
            var exitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(exitTask, timeoutTask);
            if (completed != exitTask)
            {
                TryKillDuplicatePreviewProcess(process);
                return null;
            }

            return process.ExitCode == 0 && File.Exists(thumbnailPath)
                ? thumbnailPath
                : null;
        }

        private static void TryKillDuplicatePreviewProcess(Process process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }

        private void OpenDuplicateManagerSelectedLocation(DataGridView grid)
        {
            string? path = GetSelectedDuplicateManagerPath(grid);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowStatusInfo("Select an existing duplicate file first.");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Open duplicate location failed", path, ex);
                ShowStatusInfo("Could not open the duplicate file location.");
            }
        }

        private void OpenDuplicateManagerSelectedFile(DataGridView grid)
        {
            string? path = GetSelectedDuplicateManagerPath(grid);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowStatusInfo("Select an existing duplicate file first.");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Open duplicate file failed", path, ex);
                ShowStatusInfo("Could not open the duplicate file.");
            }
        }

        private void DeleteDuplicateManagerSelectedFile(Form owner, DataGridView grid)
        {
            if (_encodingActive)
            {
                ShowStatusInfo("Stop the active encode before deleting duplicate files.");
                return;
            }

            string? path = GetSelectedDuplicateManagerPath(grid);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowStatusInfo("Select an existing duplicate file first.");
                return;
            }

            var managed = FindDuplicateManagedFile(path);
            if (managed == null)
            {
                ShowStatusInfo("The selected file is no longer part of the duplicate scan results.");
                return;
            }
            if (managed.IsReferenceProtected)
            {
                ShowStatusInfo("Reference folder files are protected from duplicate delete actions.");
                return;
            }

            string message =
                $"Send this file to the Recycle Bin?{Environment.NewLine}{Environment.NewLine}" +
                $"{Path.GetFileName(path)}{Environment.NewLine}{path}{Environment.NewLine}{FormatSize(managed.Item.LengthBytes)}";

            if (_config.RequireDuplicateCleanupConfirmation)
            {
                var confirm = MessageBox.Show(
                    owner,
                    message,
                    "Confirm Duplicate File Delete",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                    return;
            }

            try
            {
                FileSystem.DeleteFile(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);

                AppendDuplicateActionAudit(new[]
                {
                    new DuplicateActionAuditEntry(
                        DateTime.Now,
                        DuplicateFileAction.Recycle,
                        managed.Group.Id,
                        managed.Group.ConfidenceLabel,
                        managed.Group.ConfidenceScore,
                        managed.Item.Recommendation,
                        path,
                        "",
                        managed.Item.LengthBytes,
                        "Succeeded",
                        "Deleted from Duplicate Manager context menu")
                });

                RemoveDuplicateManagerDeletedFile(owner, grid, managed);
                ShowStatusInfo("Duplicate file sent to the Recycle Bin.");
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Duplicate Manager delete failed", path, ex);
                AppendDuplicateActionAudit(new[]
                {
                    new DuplicateActionAuditEntry(
                        DateTime.Now,
                        DuplicateFileAction.Recycle,
                        managed.Group.Id,
                        managed.Group.ConfidenceLabel,
                        managed.Group.ConfidenceScore,
                        managed.Item.Recommendation,
                        path,
                        "",
                        managed.Item.LengthBytes,
                        "Failed",
                        ex.Message)
                });
                ShowStatusInfo("Could not delete the selected duplicate file. See the error log.");
            }
        }

        private void RemoveDuplicateManagerDeletedFile(Form owner, DataGridView grid, DuplicateManagedFile deleted)
        {
            var originalGroupItems = deleted.Group.Items.ToList();
            bool groupDropsBelowDuplicateThreshold = originalGroupItems.Count <= 2;

            RemoveEncodeQueueRowForPath(deleted.Path);

            if (groupDropsBelowDuplicateThreshold)
            {
                foreach (var remaining in originalGroupItems.Where(item =>
                             !string.Equals(item.Path, deleted.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    RemoveEncodeQueueRowForPath(remaining.Path);
                }
            }

            UpdateDuplicateScanResultAfterSingleDelete(deleted.Path, deleted.Group.Id, removeWholeGroup: groupDropsBelowDuplicateThreshold);
            RefreshDuplicateManagerGrid(grid);

            if (_lastDuplicateScanResult == null || _lastDuplicateScanResult.Groups.Count == 0)
            {
                owner.Close();
                _duplicateCleanupAutoDisablePending = true;
                PromptToDisableDuplicateFinderAfterCleanup();
            }
        }

        private void RemoveEncodeQueueRowForPath(string path)
        {
            if (_rowsByPath.TryGetValue(path, out var row) && row?.DataGridView == dgvEncodeQueue)
                RemoveRowAndCleanup(row);
        }

        private void UpdateDuplicateScanResultAfterSingleDelete(string deletedPath, int groupId, bool removeWholeGroup)
        {
            if (_lastDuplicateScanResult == null)
                return;

            var groups = new List<DuplicateGroup>();
            foreach (var group in _lastDuplicateScanResult.Groups)
            {
                if (group.Id != groupId)
                {
                    groups.Add(group);
                    continue;
                }

                if (removeWholeGroup)
                    continue;

                var remainingItems = group.Items
                    .Where(item => !string.Equals(item.Path, deletedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(item.Path))
                    .ToList();

                if (remainingItems.Count > 1)
                    groups.Add(group with { Items = remainingItems });
            }

            _lastDuplicateScanResult = BuildDuplicateScanResult(groups);
            ClearDuplicateAnnotations(resetResult: false);
            ApplyDuplicateScanResult(_lastDuplicateScanResult);
        }

        private static DuplicateScanResult BuildDuplicateScanResult(IReadOnlyList<DuplicateGroup> groups)
        {
            int duplicateFiles = groups.Sum(group => Math.Max(0, group.Items.Count - 1));
            long recoverableBytes = groups.Sum(group =>
            {
                if (group.Items.Count <= 1)
                    return 0;

                var keeper = SelectKeeperForSummary(group.Items);
                return keeper == null ? 0 : Math.Max(0, group.Items.Sum(item => item.LengthBytes) - keeper.LengthBytes);
            });

            return new DuplicateScanResult(groups, duplicateFiles, recoverableBytes);
        }

        private static DuplicateItem? SelectKeeperForSummary(IReadOnlyList<DuplicateItem> items)
        {
            return items.FirstOrDefault(item => IsKeeperRecommendation(item.Recommendation))
                   ?? items.FirstOrDefault(item =>
                       string.Equals(item.Recommendation, "Protected keeper", StringComparison.OrdinalIgnoreCase));
        }

        private void RefreshDuplicateManagerGrid(DataGridView grid)
        {
            RefreshDuplicateManagerGrid(grid, GetDuplicateManagerFilter(grid));
        }

        private void RefreshDuplicateManagerGrid(DataGridView grid, string filter)
        {
            grid.Rows.Clear();
            if (_lastDuplicateScanResult == null)
                return;

            foreach (var group in GetDuplicateManagerGroups(filter))
            {
                foreach (var item in group.Items)
                    AddDuplicateManagerGridRow(grid, group, item);
            }
        }

        private string GetDuplicateManagerFilter(DataGridView grid)
        {
            return grid.Tag as string ?? DuplicateFilterAll;
        }

        private IEnumerable<DuplicateGroup> GetDuplicateManagerGroups(string filter)
        {
            if (_lastDuplicateScanResult == null)
                return Enumerable.Empty<DuplicateGroup>();

            return _lastDuplicateScanResult.Groups.Where(group => DuplicateGroupMatchesFilter(group, filter));
        }

        private static bool DuplicateGroupMatchesFilter(DuplicateGroup group, string filter)
        {
            return filter switch
            {
                DuplicateFilterExact => string.Equals(group.ConfidenceLabel, "Exact", StringComparison.OrdinalIgnoreCase),
                DuplicateFilterStrong => string.Equals(group.ConfidenceLabel, "Strong visual match", StringComparison.OrdinalIgnoreCase),
                DuplicateFilterReview => string.Equals(group.ConfidenceLabel, "Review only", StringComparison.OrdinalIgnoreCase),
                DuplicateFilterActionable => IsActionableDuplicateGroup(group),
                DuplicateFilterProtected => group.Items.Any(item => item.IsReferenceProtected),
                _ => true
            };
        }

        private string BuildDuplicateManagerSummary(string filter)
        {
            var groups = GetDuplicateManagerGroups(filter).ToList();
            int fileCount = groups.Sum(group => group.Items.Count);
            int duplicateCount = groups.Sum(group => Math.Max(0, group.Items.Count - 1));
            long recoverableBytes = groups.Sum(group =>
            {
                if (group.Items.Count <= 1)
                    return 0;

                var keeper = SelectKeeperForSummary(group.Items);
                return keeper == null ? 0 : Math.Max(0, group.Items.Sum(item => item.LengthBytes) - keeper.LengthBytes);
            });
            int exactCount = groups.Count(group => string.Equals(group.ConfidenceLabel, "Exact", StringComparison.OrdinalIgnoreCase));
            int strongCount = groups.Count(group => string.Equals(group.ConfidenceLabel, "Strong visual match", StringComparison.OrdinalIgnoreCase));
            int reviewCount = groups.Count(group => string.Equals(group.ConfidenceLabel, "Review only", StringComparison.OrdinalIgnoreCase));
            int keeperReviewCount = groups.Count(group => group.Items.Any(item =>
                string.Equals(item.Recommendation, "Review required", StringComparison.OrdinalIgnoreCase)));

            return $"Groups: {groups.Count:N0}   Files: {fileCount:N0}   Duplicates: {duplicateCount:N0}   Recoverable: {FormatSize(recoverableBytes)}   Exact: {exactCount:N0}   Strong: {strongCount:N0}   Match review: {reviewCount:N0}   Keeper review: {keeperReviewCount:N0}";
        }

        private static void AddDuplicateManagerGridRow(DataGridView grid, DuplicateGroup group, DuplicateItem item)
        {
            bool canSelectForCleanup = IsActionableDuplicateGroup(group) &&
                                       !item.IsReferenceProtected &&
                                       string.Equals(item.Recommendation, "Trash candidate", StringComparison.OrdinalIgnoreCase);
            bool checkedForCleanup = canSelectForCleanup &&
                                     string.Equals(item.Recommendation, "Trash candidate", StringComparison.OrdinalIgnoreCase);
            int rowIndex = grid.Rows.Add(
                checkedForCleanup,
                group.Id,
                $"{group.ConfidenceLabel} ({group.ConfidenceScore}%)",
                group.MatchMethod,
                group.FrameComparisons > 0 ? $"{group.FrameMatches}/{group.FrameComparisons}" : "",
                group.FrameComparisons > 0 ? group.AverageHashDistance.ToString("0.#") : "",
                group.DurationDeltaSeconds > 0 ? $"{group.DurationDeltaSeconds:0.#}s" : "",
                item.Recommendation,
                item.KeeperReason,
                item.IsReferenceProtected ? "Protected" : "",
                Path.GetFileName(item.Path),
                FormatSize(item.LengthBytes),
                item.VideoCodec,
                item.Width > 0 && item.Height > 0 ? $"{item.Width}x{item.Height}" : "",
                FormatTimeSpan(item.DurationSeconds),
                item.BitrateKbps > 0 ? $"{item.BitrateKbps:N0} kbps" : "",
                group.Reason,
                item.Path);
            var row = grid.Rows[rowIndex];
            row.Tag = item.Path;
            row.Cells["Reason"].ToolTipText = group.Reason;
            row.Cells["Delete"].ReadOnly = !canSelectForCleanup;
            row.Cells["Delete"].ToolTipText = canSelectForCleanup
                ? "Checked rows are used by cleanup buttons before the rule dropdown is applied."
                : "Keepers, protected references, and review-only matches cannot be selected for cleanup.";
            if (checkedForCleanup)
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 225);
        }

        private DuplicateManagedFile? FindDuplicateManagedFile(string path)
        {
            if (_lastDuplicateScanResult == null)
                return null;

            foreach (var group in _lastDuplicateScanResult.Groups)
            {
                var item = group.Items.FirstOrDefault(item =>
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                    return new DuplicateManagedFile(group, item);
            }

            return null;
        }

        private static string? GetSelectedDuplicateManagerPath(DataGridView grid)
        {
            if (grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is string selectedPath)
                return selectedPath;

            if (grid.CurrentRow?.Tag is string currentPath)
                return currentPath;

            return null;
        }

        private List<DuplicateManagedFile> GetDuplicateActionCandidates(
            DataGridView grid,
            string rule,
            bool includeReviewOnly)
        {
            if (_lastDuplicateScanResult == null)
                return new List<DuplicateManagedFile>();

            var selectedPaths = grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(row => row.Tag as string)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var checkedPaths = GetCheckedDuplicateManagerPaths(grid);
            if (checkedPaths.Count > 0)
                return GetCheckedDuplicateActionCandidates(checkedPaths, includeReviewOnly);

            var candidates = new List<DuplicateManagedFile>();
            foreach (var group in _lastDuplicateScanResult.Groups)
            {
                if (!IsActionableDuplicateGroup(group) &&
                    !(includeReviewOnly && string.Equals(group.ConfidenceLabel, "Review only", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var groupItems = group.Items
                    .Where(item => File.Exists(item.Path))
                    .Select(item => new DuplicateManagedFile(group, item))
                    .ToList();
                if (groupItems.Count < 2)
                    continue;

                List<DuplicateManagedFile> selected = rule switch
                {
                    DuplicateRuleSuggested => groupItems.Where(item => item.IsTrashCandidate && !item.IsReferenceProtected).ToList(),
                    DuplicateRuleLargest => SelectByExtreme(groupItems, largest: true),
                    DuplicateRuleSmallest => SelectByExtreme(groupItems, largest: false),
                    DuplicateRuleH264 => groupItems.Where(item => IsH264Codec(item.Item.VideoCodec)).ToList(),
                    DuplicateRuleH265 => groupItems.Where(item => IsH265Codec(item.Item.VideoCodec)).ToList(),
                    DuplicateRuleOtherCodec => groupItems.Where(item => !IsH264Codec(item.Item.VideoCodec) && !IsH265Codec(item.Item.VideoCodec)).ToList(),
                    DuplicateRuleLowerResolution => SelectBelowBestResolution(groupItems),
                    DuplicateRuleLowerBitrate => SelectBelowBestBitrate(groupItems),
                    DuplicateRuleOlder => SelectByDate(groupItems, older: true),
                    DuplicateRuleNewer => SelectByDate(groupItems, older: false),
                    DuplicateRuleSelectedRows => groupItems.Where(item => selectedPaths.Contains(item.Path) && !item.IsReferenceProtected).ToList(),
                    _ => new List<DuplicateManagedFile>()
                };

                selected = EnsureOneFileRemainsInGroup(groupItems, selected);
                if (!IsActionableDuplicateGroup(group) && !includeReviewOnly)
                    selected.Clear();
                candidates.AddRange(selected);
            }

            return candidates
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Group.Id)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<DuplicateManagedFile> GetCheckedDuplicateActionCandidates(
            HashSet<string> checkedPaths,
            bool includeReviewOnly)
        {
            if (_lastDuplicateScanResult == null)
                return new List<DuplicateManagedFile>();

            var candidates = new List<DuplicateManagedFile>();
            foreach (var group in _lastDuplicateScanResult.Groups)
            {
                if (!IsActionableDuplicateGroup(group) &&
                    !(includeReviewOnly && string.Equals(group.ConfidenceLabel, "Review only", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var groupItems = group.Items
                    .Where(item => File.Exists(item.Path))
                    .Select(item => new DuplicateManagedFile(group, item))
                    .ToList();
                if (groupItems.Count < 2)
                    continue;

                var selected = groupItems
                    .Where(item => checkedPaths.Contains(item.Path) && !item.IsReferenceProtected && !item.IsSuggestedKeeper)
                    .ToList();
                selected = EnsureOneFileRemainsInGroup(groupItems, selected);
                candidates.AddRange(selected);
            }

            return candidates
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Group.Id)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HashSet<string> GetCheckedDuplicateManagerPaths(DataGridView grid)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!grid.Columns.Contains("Delete"))
                return paths;

            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is not string path || string.IsNullOrWhiteSpace(path))
                    continue;

                if (row.Cells["Delete"].Value is bool checkedForCleanup && checkedForCleanup)
                    paths.Add(path);
            }

            return paths;
        }

        private static bool IsActionableDuplicateGroup(DuplicateGroup group)
        {
            return string.Equals(group.ConfidenceLabel, "Exact", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(group.ConfidenceLabel, "Strong visual match", StringComparison.OrdinalIgnoreCase);
        }

        private static List<DuplicateManagedFile> SelectByExtreme(List<DuplicateManagedFile> groupItems, bool largest)
        {
            long target = largest
                ? groupItems.Max(item => item.Item.LengthBytes)
                : groupItems.Min(item => item.Item.LengthBytes);
            return groupItems.Where(item => item.Item.LengthBytes == target).ToList();
        }

        private static List<DuplicateManagedFile> SelectBelowBestResolution(List<DuplicateManagedFile> groupItems)
        {
            long best = groupItems.Max(item => (long)item.Item.Width * item.Item.Height);
            return groupItems.Where(item => (long)item.Item.Width * item.Item.Height < best).ToList();
        }

        private static List<DuplicateManagedFile> SelectBelowBestBitrate(List<DuplicateManagedFile> groupItems)
        {
            int best = groupItems.Max(item => item.Item.BitrateKbps);
            return groupItems.Where(item => item.Item.BitrateKbps > 0 && item.Item.BitrateKbps < best).ToList();
        }

        private static List<DuplicateManagedFile> SelectByDate(List<DuplicateManagedFile> groupItems, bool older)
        {
            DateTime target = older
                ? groupItems.Min(item => item.Item.Modified)
                : groupItems.Max(item => item.Item.Modified);
            return groupItems.Where(item => item.Item.Modified == target).ToList();
        }

        private static List<DuplicateManagedFile> EnsureOneFileRemainsInGroup(
            List<DuplicateManagedFile> groupItems,
            List<DuplicateManagedFile> selected)
        {
            if (selected.Count < groupItems.Count)
                return selected.Where(item => !item.IsReferenceProtected).ToList();

            var keeper = groupItems.FirstOrDefault(item => item.IsSuggestedKeeper) ?? groupItems.First();
            return selected
                .Where(item => !string.Equals(item.Path, keeper.Path, StringComparison.OrdinalIgnoreCase) && !item.IsReferenceProtected)
                .ToList();
        }

        private string? ChooseDuplicateQuarantineFolder(Form owner)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select a folder where duplicate files should be moved.",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_config.DuplicateQuarantineFolder)
                    ? _config.DuplicateQuarantineFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return null;

            _config.DuplicateQuarantineFolder = dialog.SelectedPath;
            _config.Save(_configPath);
            return Path.Combine(dialog.SelectedPath, $"Encode Duplicate Quarantine {DateTime.Now:yyyy-MM-dd HHmmss}");
        }

        private string? GetDuplicateQuarantineRoot(Form owner)
        {
            if (Directory.Exists(_config.DuplicateQuarantineFolder))
                return Path.Combine(_config.DuplicateQuarantineFolder, $"Encode Duplicate Quarantine {DateTime.Now:yyyy-MM-dd HHmmss}");

            return ChooseDuplicateQuarantineFolder(owner);
        }

        private static string BuildQuarantineDestination(string quarantineRoot, DuplicateManagedFile candidate)
        {
            string groupFolder = Path.Combine(quarantineRoot, $"Group {candidate.Group.Id}");
            string fileName = Path.GetFileName(candidate.Path);
            string destination = Path.Combine(groupFolder, fileName);
            if (!File.Exists(destination))
                return destination;

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 1; i < 10000; i++)
            {
                destination = Path.Combine(groupFolder, $"{stem} ({i}){ext}");
                if (!File.Exists(destination))
                    return destination;
            }

            return Path.Combine(groupFolder, $"{stem} ({Guid.NewGuid():N}){ext}");
        }

        private static string BuildDuplicateActionStatus(string prefix, IReadOnlyCollection<DuplicateManagedFile> candidates)
        {
            long bytes = candidates.Sum(item => item.Item.LengthBytes);
            return $"{prefix}: {candidates.Count:N0} file(s), {FormatSize(bytes)}.";
        }

        private bool IsDuplicateActionEnabled(DuplicateFileAction action)
        {
            return action switch
            {
                DuplicateFileAction.Recycle => _config.AllowDuplicateRecycleBin,
                DuplicateFileAction.Quarantine => _config.AllowDuplicateQuarantine,
                DuplicateFileAction.DeletePermanent => _config.AllowDuplicatePermanentDelete,
                _ => false
            };
        }

        private static string GetDuplicateActionName(DuplicateFileAction action)
        {
            return action switch
            {
                DuplicateFileAction.Recycle => "move to the Recycle Bin",
                DuplicateFileAction.Quarantine => "move to quarantine",
                DuplicateFileAction.DeletePermanent => "delete permanently",
                _ => "process"
            };
        }

        private static string BuildDuplicateActionSummary(
            IReadOnlyCollection<DuplicateManagedFile> candidates,
            string actionName)
        {
            long bytes = candidates.Sum(item => item.Item.LengthBytes);
            int keeperCount = candidates.Count(item => item.IsSuggestedKeeper);
            var sample = string.Join(
                Environment.NewLine,
                candidates.Take(8).Select(item => $"Group {item.Group.Id}: {Path.GetFileName(item.Path)} ({FormatSize(item.Item.LengthBytes)})"));

            if (candidates.Count > 8)
                sample += $"{Environment.NewLine}...and {candidates.Count - 8:N0} more.";

            string keeperWarning = keeperCount > 0
                ? $"{Environment.NewLine}{Environment.NewLine}Warning: {keeperCount:N0} selected file(s) are currently marked as suggested keepers by the scan."
                : "";

            return $"This will {actionName} {candidates.Count:N0} duplicate file(s), totaling {FormatSize(bytes)}.{keeperWarning}{Environment.NewLine}{Environment.NewLine}{sample}{Environment.NewLine}{Environment.NewLine}Continue?";
        }

        private enum DuplicateFileAction
        {
            Recycle,
            Quarantine,
            DeletePermanent
        }

        private sealed class DuplicateManagedFile
        {
            public DuplicateManagedFile(DuplicateGroup group, DuplicateItem item)
            {
                Group = group;
                Item = item;
            }

            public DuplicateGroup Group { get; }
            public DuplicateItem Item { get; }
            public string Path => Item.Path;
            public bool IsReferenceProtected => Item.IsReferenceProtected;
            public bool IsSuggestedKeeper => IsKeeperRecommendation(Item.Recommendation);
            public bool IsTrashCandidate => string.Equals(Item.Recommendation, "Trash candidate", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record DuplicateActionAuditEntry(
            DateTime LocalTime,
            DuplicateFileAction Action,
            int GroupId,
            string ConfidenceLabel,
            int ConfidenceScore,
            string Recommendation,
            string SourcePath,
            string DestinationPath,
            long SizeBytes,
            string Status,
            string Message);

        private void SelectDuplicateManagerRowsInQueue(DataGridView managerGrid)
        {
            dgvEncodeQueue.ClearSelection();
            foreach (DataGridViewRow managerRow in managerGrid.SelectedRows)
            {
                if (managerRow.Tag is not string path || !_rowsByPath.TryGetValue(path, out var queueRow))
                    continue;
                queueRow.Selected = true;
                if (queueRow.DataGridView == dgvEncodeQueue)
                    dgvEncodeQueue.FirstDisplayedScrollingRowIndex = Math.Max(0, queueRow.Index);
            }
        }

        private static string FormatTimeSpan(double seconds)
        {
            if (seconds <= 0)
                return "";
            return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
        }
    }
}
