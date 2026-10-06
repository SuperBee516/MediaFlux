using MediaFlux.Services;
using System;
using MediaFlux.Models;
using MediaFlux.Services.EncodingV2;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Channels;
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
            bool forceDuplicateScan = false,
            bool preserveResearchAssignmentBySource = true)
        {
            var roots = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 0)
                return;

            Dictionary<string, PredictionShadowExperimentAssignmentBinding> preservedResearchAssignments = new(
                StringComparer.OrdinalIgnoreCase);
            if (replaceExisting && preserveResearchAssignmentBySource)
            {
                preservedResearchAssignments =
                    PredictionShadowExperimentAssignmentPersistence.CaptureForQueueReconstruction(
                        GetEncodeRowsInExecutionOrder()
                            .Where(existingRow => existingRow.Tag is RowMeta)
                            .Select(existingRow =>
                            {
                                RowMeta existingMeta = (RowMeta)existingRow.Tag!;
                                return (existingMeta.Path, existingMeta.PredictionShadowExperimentAssignment);
                            }));
            }

            if (!EnsureFfmpegToolsAvailable())
                return;

            // Register a live import before any discovery/setup work so the runner's
            // drain check cannot close append admission while this import is underway.
            lock (_activeEncodeQueueLock)
                Interlocked.Increment(ref _pendingEncodeImports);

            int importGeneration = Interlocked.Increment(ref _folderImportGeneration);
            var previousDuplicateScan = _duplicateScanCts;
            _duplicateScanCts = null;
            previousDuplicateScan?.Cancel();
            _duplicateRescanPending = false;

            if (rememberRoots)
            {
                if (replaceExisting && !IsQueueEncodingActive())
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
            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            SetQueueWorkCancelVisible(true);
            SetQueueProgress(0, 0, visible: true);
            bool duplicateWorkflowInitiallyEnabled = forceDuplicateScan || chkFindDuplicates.Checked;
            int initialStepCount = duplicateWorkflowInitiallyEnabled ? 4 : 3;
            SetImportPipelineStatus(1, initialStepCount, "Discovering supported video files", 0, 0);

            var provisionalImportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (replaceExisting && !_encodingActive)
                {
                    ClearDuplicateAnnotations();
                    ClearCompletedEncodePaths();
                    _estimateService.ResetAndCancel();
                    _v2EstimateCoordinator?.CancelAll();
                    ResetCodecFilterCounts();
                    _rowsByPath.Clear();
                    _estimatedSizeMap.Clear();
                    _queueSourceSizeMap.Clear();
                    _etaSpeedState.Clear();
                    _queueTotalSourceMb = 0;
                    _queueTotalEstimatedMb = 0;
                    _queueTotalEstimatedSourceMb = 0;
                    _queueTotalSavingsEstimateOutputMb = 0;
                    _queueEstimatedFileCount = 0;
                    _queueSavingsEstimateFileCount = 0;
                    _queueEstimateEligibleFileCount = 0;
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

                var discoveredChannel = Channel.CreateBounded<(string Path, string Codec, DateTime DiscoveredUtc)>(
                    new BoundedChannelOptions(64)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.Wait
                    });
                var files = new List<KeyValuePair<string, string>>();
                var importedPaths = provisionalImportedPaths;
                int added = 0;
                DateTime discoveryStartedUtc = DateTime.UtcNow;
                Task discovery = Task.Run(async () =>
                {
                    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var root in roots)
                        {
                            try
                            {
                                if (File.Exists(root))
                                {
                                    await AddIfSupportedAsync(root).ConfigureAwait(false);
                                }
                                else if (Directory.Exists(root))
                                {
                                    var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                                    foreach (var file in Directory.EnumerateFiles(root, "*.*", option))
                                    {
                                        ct.ThrowIfCancellationRequested();
                                        await AddIfSupportedAsync(file).ConfigureAwait(false);
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
                    }
                    catch (Exception ex)
                    {
                        discoveredChannel.Writer.TryComplete(ex);
                        return;
                    }
                    discoveredChannel.Writer.TryComplete();

                    async Task AddIfSupportedAsync(string file)
                    {
                        ct.ThrowIfCancellationRequested();
                        string ext = Path.GetExtension(file);
                        if (string.IsNullOrEmpty(ext) || !allowedExts.Contains(ext) || !found.Add(file))
                            return;

                        int discovered = Interlocked.Increment(ref _lastImportDiscoveredCount);
                        await discoveredChannel.Writer.WriteAsync((file, "", DateTime.UtcNow), ct)
                            .ConfigureAwait(false);
                        if (discovered % 100 == 0)
                            System.Diagnostics.Debug.WriteLine($"[QueueAdmission] import={importGeneration} discovered={discovered}");
                    }
                }, ct);

                int batchAdded = 0;
                dgvEncodeQueue.SuspendLayout();
                try
                {
                    await foreach (var file in discoveredChannel.Reader.ReadAllAsync(ct))
                    {
                        if (importGeneration != Volatile.Read(ref _folderImportGeneration))
                            break;
                        files.Add(new KeyValuePair<string, string>(file.Path, file.Codec));
                        bool wasAdded = AddDiscoveredEncodeItemIfNotPresent(
                            file.Path,
                            file.DiscoveredUtc);
                        if (wasAdded && _rowsByPath.TryGetValue(file.Path, out DataGridViewRow? addedRow) &&
                            addedRow?.Tag is RowMeta addedMeta)
                        {
                            addedMeta.QueueAdmissionPending = true;
                            addedMeta.QueueAdmissionGeneration = importGeneration;
                            importedPaths.Add(file.Path);
                            added++;
                            PredictionShadowExperimentAssignmentBinding? assignment =
                                PredictionShadowExperimentAssignmentPersistence.RestoreForQueueReconstruction(
                                    preservedResearchAssignments, file.Path);
                            if (assignment != null &&
                                PredictionShadowExperimentAssignmentPersistence.MatchesSource(assignment, file.Path))
                            {
                                addedMeta.PredictionShadowExperimentAssignment = assignment;
                                UpdateResearchExperimentAssignmentPresentation(addedRow);
                            }
                            SetEncodeRowState(addedRow,
                                requestedCodecFilter ? "Checking codec" : "Preparing",
                                "", "", "Queue admission checks are running; this row is not yet eligible to encode.");
                            _ = TryQueueV2Estimate(addedRow, addedMeta, file.Path, qualityChangeStartedUtc: null);
                            _lastImportAddedCount = added;
                        }
                        else if (_rowsByPath.TryGetValue(file.Path, out DataGridViewRow? existingRow) &&
                                 existingRow?.Tag is RowMeta existingMeta && existingMeta.QueueAdmissionPending)
                        {
                            // A superseding import adopts an in-flight provisional row so
                            // the canceled import cannot remove work now owned by this pass.
                            existingMeta.QueueAdmissionGeneration = importGeneration;
                            importedPaths.Add(file.Path);
                        }

                        batchAdded++;
                        if (batchAdded >= 16)
                        {
                            dgvEncodeQueue.ResumeLayout();
                            if (requestedCodecFilter && files.Count > 0)
                            {
                                toolStripStatusLabel1.Text = $"Discovering files and adding rows... {files.Count:N0} found";
                                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                            }
                            await Task.Yield();
                            dgvEncodeQueue.SuspendLayout();
                            batchAdded = 0;
                        }
                    }
                }
                finally
                {
                    dgvEncodeQueue.ResumeLayout();
                }
                await discovery;
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage=queue-discovery-complete import={importGeneration} " +
                    $"files={files.Count} elapsedMs={(DateTime.UtcNow - discoveryStartedUtc).TotalMilliseconds:0.##}");

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

                bool largeQueue = files.Count >= GetLargeQueueThreshold();
                bool progressiveCodecFilter = requestedCodecFilter;
                int codecStep = duplicateWorkflowEnabled ? 3 : 2;

                _largeQueueModeActive = largeQueue;
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

                foreach (string path in importedPaths)
                {
                    if (_rowsByPath.TryGetValue(path, out DataGridViewRow? admittedRow) &&
                        admittedRow?.Tag is RowMeta admittedMeta &&
                        admittedMeta.QueueAdmissionGeneration == importGeneration)
                    {
                        if (admittedMeta.ExcludedFromEncodeAsDuplicate)
                        {
                            _v2EstimateCoordinator?.Forget(admittedMeta.QueueItemId);
                            _estimatedSizeMap.Remove(path);
                            if (admittedRow.DataGridView == dgvEncodeQueue)
                            {
                                admittedRow.Cells["colEstimatedSize"].Value = "";
                                admittedRow.Cells["colEstimatedSize"].Tag = null;
                            }
                        }
                        admittedMeta.QueueAdmissionPending = false;
                        admittedMeta.QueueAdmissionGeneration = -1;
                        if (!admittedMeta.ExcludedFromEncodeAsDuplicate &&
                            !IsQueueRowActivelyEncoding(admittedRow))
                            SetEncodeRowState(admittedRow, "Queued", "", "", "Queue admission checks complete; ready to encode.");
                    }
                }

                if (_encodingActive)
                    AppendEligibleImportedRowsToActiveQueue(importedPaths);

                // Estimate independently eligible idle rows during parallel encoding;
                // active sources are excluded by queue-job ownership in the estimator.
                if (added > 0 && (!largeQueue || _config.AutoAnalyzeLargeQueues))
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
                      (largeQueue && !_config.AutoAnalyzeLargeQueues
                          ? ". V2 fast estimates continue; remaining estimates are deferred until Analyze Queue."
                          : ". Estimates continue in the background.")
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
                importCts.Cancel();
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
                foreach (string path in provisionalImportedPaths)
                {
                    if (_rowsByPath.TryGetValue(path, out DataGridViewRow? pendingRow) &&
                        pendingRow?.Tag is RowMeta pendingMeta &&
                        pendingMeta.QueueAdmissionPending &&
                        pendingMeta.QueueAdmissionGeneration == importGeneration)
                    {
                        RemoveCodecFilteredRow(pendingRow);
                    }
                }
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
                if (!_rowsByPath.TryGetValue(path, out DataGridViewRow? row) || row?.DataGridView != dgvEncodeQueue)
                    continue;
                RowMeta meta = EnsureRowMeta(row);
                if (meta.ExcludedFromEncodeAsDuplicate)
                    continue;

                string codec;
                bool keep;
                if (IsEstimateSourceOwnedByActiveEncode(path))
                {
                    codec = meta.VideoCodec;
                    keep = true;
                }
                else if (meta.V2SourceIdentity is { } identity && _v2EstimateCoordinator is { } coordinator)
                {
                    V2SourceFactsCacheResolution facts = await coordinator.GetSourceFactsAsync(identity, ct);
                    meta.V2SourceFacts = facts.Facts;
                    codec = facts.Facts.VideoCodec;
                    keep = PassesCodecFilter(codec, allowH264, allowHevc, allowAv1, allowOther);
                }
                else
                {
                    codec = await Task.Run(() => GetVideoCodec(path), ct);
                    keep = PassesCodecFilter(codec, allowH264, allowHevc, allowAv1, allowOther);
                }

                TrackCodecFilterCount(path, codec);
                meta.VideoCodec = codec;

                if (keep)
                {
                    if (!meta.QueueAdmissionPending)
                        SetEncodeRowState(row, "Queued", "", "", "Ready to encode.");
                }
                else
                {
                    RemoveCodecFilteredRow(row);
                    removed++;
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

            // importedPaths is a set used for duplicate decisions; restore the
            // queue's logical order before appending each row to the live runner.
            List<DataGridViewRow> importedRows = importedPaths
                .Select(path => _rowsByPath.TryGetValue(path, out var row) ? row : null)
                .Where(row => row != null && row.DataGridView == dgvEncodeQueue &&
                              row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true })
                .Cast<DataGridViewRow>()
                .ToList();
            List<DataGridViewRow> rowsToAppend = OrderRowsByQueueSequence(importedRows);

            foreach (DataGridViewRow row in rowsToAppend)
            {
                TryAppendActiveEncodeQueueRow(row);
            }
        }

        private void RemoveCodecFilteredRow(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow || IsQueueRowActivelyEncoding(row))
                return;

            var path = GetPathFromRow(row);
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (row.Tag is RowMeta removedMeta)
                    _v2EstimateCoordinator?.Forget(removedMeta.QueueItemId);
                _rowsByPath.TryRemove(path, out _);
                _estimatedSizeMap.Remove(path);
                _queueSourceSizeMap.Remove(path);
                _etaSpeedState.Remove(path);
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
