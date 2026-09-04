using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private readonly HashSet<string> _codecFilterImportRoots = new(StringComparer.OrdinalIgnoreCase);

        private void ScanAndPopulateEncodeGrid(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                ResetCodecFilterCounts();
                return;
            }

            _ = ImportEncodePathsAsync(
                new[] { folder },
                chkIncludeSubfolders.Checked,
                applyCodecFilters: true,
                replaceExisting: !_encodingActive);
        }

        private void RescanInputFolderAndMerge()
        {
            ClearCompletedEncodePaths();
            RescanInputFolderAndMerge(recomputeEstimates: false);
        }

        private async Task ImportEncodePathsAsync(
            IEnumerable<string> paths,
            bool includeSubfolders,
            bool applyCodecFilters,
            bool replaceExisting = false,
            bool rememberRoots = true,
            bool forceDuplicateScan = false)
        {
            var roots = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 0)
                return;

            if (!EnsureFfmpegToolsAvailable())
                return;

            int importGeneration = Interlocked.Increment(ref _folderImportGeneration);
            var previousDuplicateScan = _duplicateScanCts;
            _duplicateScanCts = null;
            previousDuplicateScan?.Cancel();
            _duplicateRescanPending = false;

            if (rememberRoots)
            {
                if (replaceExisting && !_encodingActive)
                    _codecFilterImportRoots.Clear();

                foreach (var root in roots)
                    _codecFilterImportRoots.Add(root);
            }

            var allowedExts = GetAllowedExts();
            bool allowH264 = chkFilterX264.Checked;
            bool allowHevc = chkFilterX265.Checked;
            bool allowAv1 = chkFilterAv1.Checked;
            bool allowOther = chkFilterOtherCodecs.Checked;
            bool requestedCodecFilter = applyCodecFilters;
            bool requireCodecProbeDuringDiscovery = requestedCodecFilter && _encodingActive;
            _importCts?.Cancel();
            _importCts?.Dispose();
            var importCts = new CancellationTokenSource();
            _importCts = importCts;
            _codecFilterCts?.Cancel();
            _codecFilterCts?.Dispose();
            _codecFilterCts = new CancellationTokenSource();
            var ct = importCts.Token;
            var codecFilterToken = _codecFilterCts.Token;
            _lastImportDiscoveredCount = 0;
            _lastImportAddedCount = 0;
            Interlocked.Increment(ref _pendingEncodeImports);
            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            SetQueueWorkCancelVisible(true);
            SetQueueProgress(0, 0, visible: true);
            bool duplicateWorkflowInitiallyEnabled = forceDuplicateScan || chkFindDuplicates.Checked;
            int initialStepCount = duplicateWorkflowInitiallyEnabled ? 4 : 3;
            SetImportPipelineStatus(1, initialStepCount, "Discovering supported video files", 0, 0);

            try
            {
                if (replaceExisting && !_encodingActive)
                {
                    ClearDuplicateAnnotations();
                    ClearCompletedEncodePaths();
                    _estimateService.ResetAndCancel();
                    ResetCodecFilterCounts();
                    _rowsByPath.Clear();
                    _estimatedSizeMap.Clear();
                    _queueSourceSizeMap.Clear();
                    _queueTotalSourceMb = 0;
                    _queueTotalEstimatedMb = 0;
                    _queueFileCount = 0;
                    _queueTotalsDirty = false;

                    _suppressRowEvents = true;
                    try
                    {
                        dgvEncodeQueue.Rows.Clear();
                    }
                    finally
                    {
                        _suppressRowEvents = false;
                    }
                }

                var files = await Task.Run(() =>
                {
                    var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var root in roots)
                    {
                        try
                        {
                            if (File.Exists(root))
                            {
                                AddIfSupported(root, found);
                            }
                            else if (Directory.Exists(root))
                            {
                                var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                                foreach (var file in Directory.EnumerateFiles(root, "*.*", option))
                                {
                                    ct.ThrowIfCancellationRequested();
                                    AddIfSupported(file, found);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            ErrorLogService.Append(
                                Application.StartupPath,
                                "Encode queue import could not read a path",
                                root,
                                ex);
                        }
                    }

                    return found.ToList();

                    void AddIfSupported(string file, Dictionary<string, string> destination)
                    {
                        ct.ThrowIfCancellationRequested();
                        string ext = Path.GetExtension(file);
                        if (string.IsNullOrEmpty(ext) || !allowedExts.Contains(ext))
                            return;

                        string codec = "";
                        if (requireCodecProbeDuringDiscovery)
                        {
                            codec = GetVideoCodec(file);
                            if (!PassesCodecFilter(codec, allowH264, allowHevc, allowAv1, allowOther))
                                return;
                        }

                        destination.TryAdd(file, codec);
                        int discovered = Interlocked.Increment(ref _lastImportDiscoveredCount);
                        if (discovered % 100 == 0)
                            SafeQueueImportStatusUpdate($"Step 1 of {initialStepCount} — Discovering supported video files: {discovered:N0} found");
                    }

                    void SafeQueueImportStatusUpdate(string text)
                    {
                        try
                        {
                            if (!IsDisposed && IsHandleCreated)
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    toolStripStatusLabel1.Text = text;
                                    UpdateRelocatedEncodeStatus(text);
                                    SetQueueProgress(_lastImportDiscoveredCount, Math.Max(_lastImportDiscoveredCount, 1), visible: true);
                                }));
                            }
                        }
                        catch
                        {
                            // The form may be closing while a background enumeration is winding down.
                        }
                    }
                }, ct);

                ct.ThrowIfCancellationRequested();
                if (importGeneration != Volatile.Read(ref _folderImportGeneration))
                    return;

                var allDiscoveredPaths = files.Select(file => file.Key).ToList();
                var duplicateAnalysisPaths = allDiscoveredPaths
                    .Concat(dgvEncodeQueue.Rows
                        .Cast<DataGridViewRow>()
                        .Select(GetPathFromRow)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Cast<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool duplicateWorkflowEnabled = forceDuplicateScan || chkFindDuplicates.Checked;
                int totalSteps = duplicateWorkflowEnabled ? 4 : 3;
                DuplicateScanResult? importDuplicateResult = null;
                if (duplicateWorkflowEnabled)
                {
                    _duplicateRescanPending = false;
                    importDuplicateResult = await AnalyzeDuplicatePathsForImportAsync(
                        duplicateAnalysisPaths,
                        stepNumber: 2,
                        totalSteps,
                        ct);
                    ct.ThrowIfCancellationRequested();
                    if (importGeneration != Volatile.Read(ref _folderImportGeneration))
                        return;
                }

                int added = 0;
                bool largeQueue = files.Count >= GetLargeQueueThreshold();
                bool progressiveCodecFilter = requestedCodecFilter && !_encodingActive && largeQueue;
                int codecStep = duplicateWorkflowEnabled ? 3 : 2;
                if (requestedCodecFilter && !_encodingActive && !progressiveCodecFilter)
                    files = await FilterFilesByCodecAsync(
                        files,
                        allowH264,
                        allowHevc,
                        allowAv1,
                        allowOther,
                        codecStep,
                        totalSteps,
                        ct);

                _largeQueueModeActive = largeQueue;
                // Finalize the entire import (including any duplicate decision made
                // while discovery is running) before exposing new rows to live workers.
                bool holdActiveQueueAppend = _encodingActive;
                var importedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dgvEncodeQueue.SuspendLayout();
                try
                {
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (AddEncodeItemIfNotPresent(
                            file.Key,
                            refreshEstimates: false,
                            appendToActiveQueue: !holdActiveQueueAppend))
                        {
                            added++;
                            importedPaths.Add(file.Key);
                            _lastImportAddedCount = added;
                            if (requestedCodecFilter && !progressiveCodecFilter)
                                TrackCodecFilterCount(file.Key, file.Value);
                            if (progressiveCodecFilter && _rowsByPath.TryGetValue(file.Key, out var checkingRow))
                                SetEncodeRowState(checkingRow, "Checking codec", "", "", "Checking video codec against current filters.");
                            if (added % 100 == 0)
                            {
                                toolStripStatusLabel1.Text = $"Adding files to queue... {added:N0}/{files.Count:N0}";
                                UpdateRelocatedEncodeStatus($"Adding files to queue... {added:N0}/{files.Count:N0}");
                                SetQueueProgress(added, files.Count, visible: true);
                            }
                        }
                    }
                }
                finally
                {
                    dgvEncodeQueue.ResumeLayout();
                }

                int removedByCodecFilter = 0;
                if (progressiveCodecFilter && added > 0)
                {
                    removedByCodecFilter = await ApplyProgressiveCodecFilterAsync(
                        files.Select(file => file.Key).ToList(),
                        allowH264,
                        allowHevc,
                        allowAv1,
                        allowOther,
                        codecStep,
                        totalSteps,
                        codecFilterToken);
                    added -= removedByCodecFilter;
                }

                if (_duplicateRescanPending && (forceDuplicateScan || chkFindDuplicates.Checked))
                {
                    duplicateWorkflowEnabled = true;
                    totalSteps = 4;
                    _duplicateRescanPending = false;
                    importDuplicateResult = await AnalyzeDuplicatePathsForImportAsync(
                        duplicateAnalysisPaths,
                        stepNumber: 2,
                        totalSteps,
                        ct);
                }

                int finalStep = duplicateWorkflowEnabled ? 4 : 3;
                SetImportPipelineStatus(finalStep, totalSteps, "Finalizing the encoding queue", 0, 1);

                if (duplicateWorkflowEnabled && importDuplicateResult != null)
                    ApplyDuplicateScanResult(
                        importDuplicateResult,
                        replaceExisting ? null : importedPaths);
                else if (!duplicateWorkflowEnabled)
                    ClearDuplicateAnnotations();

                if (holdActiveQueueAppend)
                    AppendEligibleImportedRowsToActiveQueue(importedPaths);

                // Avoid competing media probes while FFmpeg is active. EncodeSingleRow
                // resolves the metadata it needs when each appended job is dispatched.
                if (added > 0 && !_encodingActive && (!largeQueue || _config.AutoAnalyzeLargeQueues))
                    RunEstimatePass();
                else if (added > 0 && largeQueue)
                {
                    _estimatesDeferredForLargeQueue = true;
                    UpdateSizeTotals(force: true);
                }
                else
                    UpdateSizeTotals(force: true);

                UpdateAnalyzeQueueButtonState();

                int excluded = importedPaths.Count(path =>
                    _rowsByPath.TryGetValue(path, out var row) &&
                    row?.Tag is RowMeta meta &&
                    meta.ExcludedFromEncodeAsDuplicate);
                int reviewGroups = importDuplicateResult?.Groups.Count(group =>
                    !group.ConfidenceLabel.Equals("Exact", StringComparison.OrdinalIgnoreCase)) ?? 0;
                string completion = added > 0
                    ? $"Queue ready — {Math.Max(0, added - excluded):N0} eligible, {excluded:N0} exact duplicate(s) excluded" +
                      (reviewGroups > 0 ? $", {reviewGroups:N0} visual group(s) available for review" : "") +
                      (removedByCodecFilter > 0 ? $", {removedByCodecFilter:N0} codec mismatch(es) filtered" : "") +
                      (largeQueue && !_config.AutoAnalyzeLargeQueues ? ". Estimates deferred until Analyze Queue." : ". Estimates continue in the background.")
                    : "Queue ready — no new supported video files were found.";
                toolStripStatusLabel1.Text = completion;
                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                if (_estimateService.PendingEstimates <= 0)
                    SetQueueProgress(0, 0, visible: false);
            }
            catch (OperationCanceledException)
            {
                UpdateDuplicateFinderUiState();
                toolStripStatusLabel1.Text = $"Queue import canceled after adding {_lastImportAddedCount:N0} file(s).";
                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                SetQueueProgress(0, 0, visible: false);
            }
            catch (Exception ex)
            {
                UpdateDuplicateFinderUiState();
                ErrorLogService.Append(
                    Application.StartupPath,
                    "Encode queue import failed",
                    exception: ex);
                toolStripStatusLabel1.Text = "Some files could not be added. See the error log for details.";
                SetQueueProgress(0, 0, visible: false);
            }
            finally
            {
                _activityIndicator?.StopActivity(UiActivity.FolderScan);
                Interlocked.Decrement(ref _pendingEncodeImports);
                SetQueueWorkCancelVisible(_estimateService?.PendingEstimates > 0);
                if (ReferenceEquals(_importCts, importCts))
                {
                    _importCts = null;
                    importCts.Dispose();
                }
            }
        }

        private async Task ReapplyCodecFiltersAsync()
        {
            string inputFolder = cmbInputFolder.Text?.Trim() ?? string.Empty;
            var availableRoots = new HashSet<string>(
                _codecFilterImportRoots.Where(path => File.Exists(path) || Directory.Exists(path)),
                StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(inputFolder))
                availableRoots.Add(inputFolder);

            string[] roots = availableRoots.ToArray();

            if (roots.Length == 0)
            {
                toolStripStatusLabel1.Text = "No source files or input folder are available to reapply codec filters.";
                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                return;
            }

            await ImportEncodePathsAsync(
                roots,
                chkIncludeSubfolders.Checked,
                applyCodecFilters: true,
                replaceExisting: !_encodingActive,
                rememberRoots: false);
        }

        private async Task<List<KeyValuePair<string, string>>> FilterFilesByCodecAsync(
            List<KeyValuePair<string, string>> files,
            bool allowH264,
            bool allowHevc,
            bool allowAv1,
            bool allowOther,
            int stepNumber,
            int totalSteps,
            CancellationToken ct)
        {
            if (files.Count == 0)
                return files;

            toolStripStatusLabel1.Text = $"Step {stepNumber} of {totalSteps} — Checking video codecs: 0/{files.Count:N0}";
            UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
            SetQueueProgress(0, files.Count, visible: true);

            var filtered = new List<KeyValuePair<string, string>>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string path = files[i].Key;
                string codec = await Task.Run(() => GetVideoCodec(path), ct);
                TrackCodecFilterCount(path, codec);
                if (PassesCodecFilter(codec, allowH264, allowHevc, allowAv1, allowOther))
                {
                    filtered.Add(new KeyValuePair<string, string>(path, codec));
                }

                int checkedCount = i + 1;
                if (checkedCount % 25 == 0 || checkedCount == files.Count)
                {
                    toolStripStatusLabel1.Text = $"Step {stepNumber} of {totalSteps} — Checking video codecs: {checkedCount:N0}/{files.Count:N0}";
                    UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                    SetQueueProgress(checkedCount, files.Count, visible: true);
                }
            }

            return filtered;
        }

        private async Task<int> ApplyProgressiveCodecFilterAsync(
            List<string> paths,
            bool allowH264,
            bool allowHevc,
            bool allowAv1,
            bool allowOther,
            int stepNumber,
            int totalSteps,
            CancellationToken ct)
        {
            if (paths.Count == 0)
                return 0;

            int removed = 0;
            toolStripStatusLabel1.Text = $"Step {stepNumber} of {totalSteps} — Checking video codecs: 0/{paths.Count:N0}";
            UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
            SetQueueProgress(0, paths.Count, visible: true);

            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string path = paths[i];
                string codec = await Task.Run(() => GetVideoCodec(path), ct);
                bool keep = PassesCodecFilter(codec, allowH264, allowHevc, allowAv1, allowOther);

                TrackCodecFilterCount(path, codec);

                if (_rowsByPath.TryGetValue(path, out var row) && row?.DataGridView == dgvEncodeQueue)
                {
                    var meta = EnsureRowMeta(row);
                    meta.VideoCodec = codec;

                    if (keep)
                    {
                        SetEncodeRowState(row, "Queued", "", "", "Ready to encode.");
                    }
                    else
                    {
                        RemoveCodecFilteredRow(row);
                        removed++;
                    }
                }

                int checkedCount = i + 1;
                if (checkedCount % 25 == 0 || checkedCount == paths.Count)
                {
                    toolStripStatusLabel1.Text = $"Step {stepNumber} of {totalSteps} — Checking video codecs: {checkedCount:N0}/{paths.Count:N0}";
                    UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                    SetQueueProgress(checkedCount, paths.Count, visible: true);
                }
            }

            if (removed > 0)
                UpdateSizeTotals(force: true);

            return removed;
        }

        private void SetImportPipelineStatus(
            int stepNumber,
            int totalSteps,
            string description,
            int current,
            int total)
        {
            string text = $"Step {stepNumber} of {totalSteps} — {description}";
            toolStripStatusLabel1.Text = text;
            UpdateRelocatedEncodeStatus(text);
            SetQueueProgress(current, total, visible: total > 0);
        }

        private void AppendEligibleImportedRowsToActiveQueue(IEnumerable<string> importedPaths)
        {
            if (!_encodingActive || _activeEncodeQueue == null)
                return;

            lock (_activeEncodeQueueLock)
            {
                foreach (string path in importedPaths)
                {
                    if (!_rowsByPath.TryGetValue(path, out var row) ||
                        row?.DataGridView != dgvEncodeQueue ||
                        row.Tag is RowMeta meta && meta.ExcludedFromEncodeAsDuplicate ||
                        _activeEncodeQueue.Contains(row))
                    {
                        continue;
                    }

                    _activeEncodeQueue.Add(row);
                }
            }
        }

        private void RemoveCodecFilteredRow(DataGridViewRow row)
        {
            var path = GetPathFromRow(row);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _rowsByPath.TryRemove(path, out _);
                _estimatedSizeMap.Remove(path);
                _queueSourceSizeMap.Remove(path);
            }

            _suppressRowEvents = true;
            try
            {
                if (row.DataGridView == dgvEncodeQueue)
                    dgvEncodeQueue.Rows.Remove(row);
            }
            finally
            {
                _suppressRowEvents = false;
            }

            MarkQueueTotalsDirty();
        }

        private void RememberCompletedEncodePaths(string sourcePath, string outputPath)
        {
            _mediaInfoService.Invalidate(sourcePath);
            _mediaInfoService.Invalidate(outputPath);
            AddCompletedEncodePath(sourcePath);
            AddCompletedEncodePath(outputPath);
        }

        private void AddCompletedEncodePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                // The path returned by the encoder is normally absolute; retaining
                // the original value is still better than allowing an automatic re-add.
            }

            lock (_completedEncodePathsLock)
                _completedEncodePaths.Add(path);
        }

        private bool IsCompletedEncodePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                // Compare the original value when normalization is unavailable.
            }

            lock (_completedEncodePathsLock)
                return _completedEncodePaths.Contains(path);
        }

        private void ClearCompletedEncodePaths()
        {
            lock (_completedEncodePathsLock)
                _completedEncodePaths.Clear();
        }
    }
}
