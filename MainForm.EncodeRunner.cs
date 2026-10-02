using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaFlux.Models;
using MediaFlux.Services.Encoders;
using static MediaFlux.Services.EncodingService;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private long _lastQueueCompletedTicks;
        private bool _sequentialQueueTiming;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<DataGridViewRow, DateTime> _queueDispatchTimes = new();
        private async void btnStartEncode_Click(object? sender, EventArgs e)
        {
            await StartEncodeAsync();
        }

        private async Task StartEncodeAsync(bool? processAllOverride = null)
        {
            // prevent re-entry
            if (_encodingActive)
                return;
            if (_mediaRemuxCts != null)
            {
                ShowStatusInfo("Wait for the active remux to finish or cancel it before encoding.");
                return;
            }

            var eligibleRows = GetEligibleEncodeRowsInExecutionOrder().ToList();
            var scope = EncodingScopeResolver.Analyze(
                eligibleRows,
                GetSelectedEncodeRowsInExecutionOrder());

            EncodingScopeChoice scopeChoice;
            if (processAllOverride == true)
            {
                scopeChoice = EncodingScopeChoice.EntireQueue;
            }
            else if (processAllOverride == false)
            {
                scopeChoice = EncodingScopeChoice.Selected;
            }
            else if (scope.RequiresChoice)
            {
                DialogResult choice = EncodingScopeForm.ShowChoice(
                    this,
                    scope.SelectedJobs.Count,
                    scope.EligibleJobs.Count);
                scopeChoice = choice == DialogResult.Yes
                    ? EncodingScopeChoice.Selected
                    : choice == DialogResult.No
                        ? EncodingScopeChoice.EntireQueue
                        : EncodingScopeChoice.Cancel;
            }
            else
            {
                scopeChoice = EncodingScopeChoice.EntireQueue;
            }

            IReadOnlyList<DataGridViewRow>? resolvedRows = scope.Resolve(scopeChoice);
            if (resolvedRows == null)
                return;

            bool requestedAll = ReferenceEquals(resolvedRows, scope.EligibleJobs);
            var requestedRows = resolvedRows.ToList();
            var initiallyRequestedRows = requestedRows.ToList();

            if (!requestedAll && requestedRows.Count == 0)
            {
                ShowStatusInfo("Select one or more files to encode.");
                return;
            }

            if (!EnsureFfmpegToolsAvailable())
                return;
            if (!await EnsureRequestedVideoEncodersAvailable(requestedRows))
                return;
            if (!await ConfirmExplicitMp4CompatibilityAsync(requestedRows))
                return;
            // Capture the configured selection once for this run.  The preview is
            // advisory and the mutable UI state must not be reread while workers
            // are building authoritative production requests.
            OutputContainerSelection runOutputContainer = GetSelectedOutputContainer();
            _activeOutputContainer = runOutputContainer;

            if (requestedRows.Any(row => row.Tag is not RowMeta { IsDvdEncode: true }) &&
                !ValidateOutputFolderAgainstWatchFolder(cmbEncodeOutput.Text, showMessage: true))
            {
                return;
            }
            foreach (string dvdOutputFolder in requestedRows
                         .Select(row => (row.Tag as RowMeta)?.DvdEncodeOptions?.OutputPath)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(path => Path.GetDirectoryName(path!) ?? "")
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!ValidateOutputFolderAgainstWatchFolder(
                        dvdOutputFolder,
                        showMessage: true))
                {
                    return;
                }
            }

            RecommendationStartChoice recommendationChoice =
                ReviewRecommendationsBeforeStart(requestedRows);
            if (recommendationChoice == RecommendationStartChoice.Cancel)
                return;
            if (recommendationChoice == RecommendationStartChoice.CandidatesOnly)
            {
                requestedRows = requestedRows
                    .Where(row =>
                        row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true } &&
                        IsSmartEncodeCandidate(row))
                    .ToList();
                requestedAll = false;
                if (requestedRows.Count == 0)
                {
                    ShowStatusInfo(
                        "No Strong or Moderate candidates remain in the requested queue scope.");
                    return;
                }
            }

            _encodingActive = true;
            SetStatusEncoding(true);

            btnStartEncode.Enabled = false;
            btnStopEncode.Enabled = true;
            _cancelEncode = false;
            _encodeFailedCount = 0;
            _encodeStorageRejectedCount = 0;
            _encodeSucceededCount = 0;
            _encodeRetryCount = 0;
            _encodeCts?.Dispose();
            _encodeCts = new CancellationTokenSource();
            var encodeToken = _encodeCts.Token;

            var queueStartedUtc = DateTime.UtcNow;

            // Snapshot the requested rows before a possible scheduled wait. An explicit
            // context-menu choice must not be changed by the persisted checkbox setting.
            var requestedRowSet = requestedRows.ToHashSet();

            int maxParallel = requestedRows.Any(row => row.Tag is RowMeta { LibraryPolicyIntent: not null })
                ? 1
                : GetMaxConcurrentEncodes(); // Policy rows use conservative isolated scheduling.
            _sequentialQueueTiming = maxParallel == 1;
            Interlocked.Exchange(ref _lastQueueCompletedTicks, 0);

            // Gather rows to process in stable logical queue order. Presentation
            // sorting is intentionally not consulted here.
            var rowsToProcess = GetEligibleEncodeRowsInExecutionOrder()
                .Where(r => requestedAll || requestedRowSet.Contains(r))
                .ToList();

            DateTime statisticsRunStartedUtc = DateTime.UtcNow;
            foreach (var row in initiallyRequestedRows)
            {
                if (row == null || row.IsNewRow)
                    continue;

                RowMeta meta = EnsureRowMeta(row);
                meta.StatisticsOperationId = Guid.NewGuid().ToString("N");
                meta.StatisticsStartUtc = statisticsRunStartedUtc;
                meta.StatisticsProcessingSeconds = 0;
            }

            foreach (var row in rowsToProcess)
            {
                if (row == null || row.IsNewRow)
                    continue;

                EnsureRowMeta(row).AutoRetryScheduled = false;
            }

            var rowsToProcessSet = rowsToProcess.ToHashSet();
            RecordSkippedEncodingRows(
                initiallyRequestedRows.Where(row => !rowsToProcessSet.Contains(row)));

            // Expose this list so the context menu can append rows while encoding
            lock (_activeEncodeQueueLock)
            {
                _activeEncodeQueue = rowsToProcess;
                _activeEncodeQueueDispatchedCount = 0;
                _activeEncodeQueueAccepting = true;
            }
            _encodeProcessedCount = 0;
            UpdateQueueEstimatedCompletion();
            UpdateOperationProgressPresentation();

            try
            {
                if (rowsToProcess.Count == 0)
                {
                    lblEncodeStatus.Text = "Nothing to encode.";
                    ResetEncodeMetrics();
                    return;
                }

                lblEncodeStatus.Text = "Encoding…";

                using (SleepPreventionService.Acquire(_config.PreventSleepDuringEncoding))
                {
                    await _encodeQueueRunner.RunAsync(
                        rowsToProcess,
                        row => EncodeSingleRow(row, encodeToken, runOutputContainer),
                        maxParallel,
                        () => _encodeQueuePaused,
                        () => _cancelEncode,
                        encodeToken,
                        _activeEncodeQueueLock,
                        () => Volatile.Read(ref _pendingEncodeImports) > 0,
                        dispatchedCount => _activeEncodeQueueDispatchedCount = dispatchedCount,
                        TryCompleteActiveEncodeQueueWhenDrained,
                        (row, utc) => _queueDispatchTimes[row] = utc);
                }

                if (_cancelEncode)
                    RecordCancelledPendingRetries(rowsToProcess);

                lblEncodeStatus.Text = _cancelEncode
                    ? "Encoding stopped."
                    : _encodeFailedCount > 0
                        ? $"Done. {_encodeFailedCount} job(s) failed; see the failed rows and central error log."
                        : _encodeStorageRejectedCount > 0
                            ? $"Done. {_encodeStorageRejectedCount} job(s) skipped — insufficient savings; original sources retained."
                        : _encodeRetryCount > 0
                            ? $"All done! Retried {_encodeRetryCount} failed job(s)."
                        : "All done!";
                ResetEncodeMetrics();
                ClearEncodeInputFolderIfQueueEmptyAfterProcessing();

                if (!_cancelEncode)
                    await SendDiscordQueueCompleteNotificationAsync(queueStartedUtc);
            }
            finally
            {
                _mp4CompatibilityConfirmedForRun = false;
                _activeOutputContainer = OutputContainerSelection.Mp4;
                lock (_activeEncodeQueueLock)
                {
                    _encodingActive = false;
                    _activeEncodeQueue = null;
                    _activeEncodeQueueDispatchedCount = 0;
                    _activeEncodeQueueAccepting = false;
                }
                ApplyDuplicateCandidateViewFilter();
                btnStartEncode.Enabled = true;
                btnStopEncode.Enabled = false;
                _cancelEncode = false;
                _encodeCts?.Dispose();
                _encodeCts = null;
                SetStatusEncoding(false);
                ClearEncodeInputFolderIfQueueEmptyAfterProcessing();
            }
        }

        private async Task<bool> ConfirmExplicitMp4CompatibilityAsync(
            IReadOnlyList<DataGridViewRow> rows)
        {
            ContainerCompatibilityPolicy policy = GetContainerCompatibilityPolicy();
            if (policy != ContainerCompatibilityPolicy.AlwaysAsk)
                return true;
            if (!rows.Any(row => RequestedOutputContainerForRow(row) == OutputContainerSelection.Mp4))
                return true;

            var warnings = new List<string>();
            var probeService = new FfprobeService(AppPaths.InstallDirectory, _config.FfprobePath);
            foreach (DataGridViewRow row in rows)
            {
                if (RequestedOutputContainerForRow(row) != OutputContainerSelection.Mp4)
                    continue;
                string? displayPath = GetFullPathFromRow(row);
                EncodingInputSource input;
                string probePath;
                if (row.Tag is RowMeta { IsDvdEncode: true, DvdEncodeOptions: not null } dvdMeta)
                {
                    input = new DvdEncodingInputFactory().Create(dvdMeta.DvdEncodeOptions);
                    probePath = input.SourceFiles.FirstOrDefault() ?? input.SourcePath;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(displayPath))
                        continue;
                    input = EncodingInputSource.FromFile(displayPath);
                    probePath = displayPath;
                }
                if (!File.Exists(probePath))
                    continue;
                MediaProbeResult probe = await probeService.ProbeAsync(probePath);
                if (!probe.Success)
                    continue;
                OutputContainerDecision decision = OutputContainerPolicy.Decide(
                    OutputContainerSelection.Mp4,
                    probe,
                    input,
                    StreamMapMode.KeepAll);
                if (decision.CompatibilityWarnings.Count > 0)
                {
                    warnings.Add(
                        $"{Path.GetFileName(displayPath ?? probePath)}: {string.Join("; ", decision.CompatibilityWarnings)}");
                }
            }

            if (warnings.Count == 0)
                return true;

            string details = string.Join(Environment.NewLine, warnings.Take(12));
            if (warnings.Count > 12)
                details += $"{Environment.NewLine}…and {warnings.Count - 12} more file(s).";
            DialogResult answer = MessageBox.Show(
                this,
                "MP4 cannot conservatively preserve every requested stream in this queue. " +
                "Incompatible subtitle, attachment, and data streams will be omitted; " +
                "other incompatible copied streams may fail. MediaFlux will not silently change containers." +
                Environment.NewLine + Environment.NewLine + details +
                Environment.NewLine + Environment.NewLine + "Continue with MP4?",
                "Review MP4 Stream Compatibility",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            _mp4CompatibilityConfirmedForRun = answer == DialogResult.Yes;
            return _mp4CompatibilityConfirmedForRun;
        }

        private OutputContainerSelection RequestedOutputContainerForRow(DataGridViewRow row) =>
            row.Tag is RowMeta { LibraryPolicyIntent: not null } meta
                ? PolicyOutputContainer(meta.LibraryPolicyIntent)
                : GetSelectedOutputContainer();

        

        private void btnStopEncode_Click(object? sender, EventArgs e)
        {
            // Signal the encode loop / workers to stop scheduling new work
            _cancelEncode = true;
            _encodeCts?.Cancel();

            // If the queue was paused, un-pause it so the loop can actually exit
            _encodeQueuePaused = false;
            if (btnPauseQueue != null)
                btnPauseQueue.Text = "Pause Queue";

            lblEncodeStatus.Text = "Encoding stopped by user.";
            btnStopEncode.Enabled = false;

            // Immediately reflect idle state in status bar / cursor
            SetStatusEncoding(false);
        }

        private void btnPauseQueue_Click(object? sender, EventArgs e)
        {
            // Only meaningful while an encode is running
            if (!_encodingActive)
            {
                toolStripStatusLabel1.Text = "No encode is currently running to pause.";
                return;
            }

            _encodeQueuePaused = !_encodeQueuePaused;

            if (sender is Button b)
                b.Text = _encodeQueuePaused ? "Resume Queue" : "Pause Queue";

            toolStripStatusLabel1.Text = _encodeQueuePaused
                ? "Encode queue paused."
                : "Encode queue resumed.";
        }

        private async Task EncodeSingleRow(
            DataGridViewRow row,
            CancellationToken cancellationToken,
            OutputContainerSelection runOutputContainer)
        {
            if (_cancelEncode || cancellationToken.IsCancellationRequested ||
                row == null || row.IsNewRow || row.DataGridView == null ||
                !TryGetRowPathAndDuration(row, out string sourcePath, out _))
            {
                if (row != null) _queueDispatchTimes.TryRemove(row, out _);
                return;
            }

            // Publish job ownership before pre-encode metadata probing. This lets
            // estimate workers exclude the exact source even during parallel
            // dispatch/recovery stages before FFmpeg itself is launched.
            RowMeta meta = EnsureRowMeta(row);
            string activeJobPath = meta.IsDvdEncode && meta.DvdEncodeOptions != null
                ? meta.DvdEncodeOptions.OutputPath
                : sourcePath;
            _runningEncodeJobs[row] = activeJobPath;
            DateTime dispatchUtc = _queueDispatchTimes.TryRemove(row, out DateTime recordedDispatchUtc)
                ? recordedDispatchUtc : DateTime.UtcNow;
            try
            {
                await EncodeSingleRowCore(row, cancellationToken, runOutputContainer, dispatchUtc);
            }
            finally
            {
                _runningEncodeJobs.TryRemove(row, out _);
                UiInvoke(() => RestoreQueuedStateAfterEstimate(row));
            }
        }

        private async Task EncodeSingleRowCore(
            DataGridViewRow row,
            CancellationToken cancellationToken,
            OutputContainerSelection runOutputContainer,
            DateTime dispatchUtc)
        {
            if (_cancelEncode || cancellationToken.IsCancellationRequested)
                return;

            if (row == null || row.IsNewRow || row.DataGridView == null)
                return;

            // Resolve file path & duration from the row
            if (!TryGetRowPathAndDuration(row, out var file, out var durationSec) ||
                string.IsNullOrWhiteSpace(file))
                return;

            RowMeta meta = EnsureRowMeta(row);
            meta.FailureAnalysis = null;
            meta.CuratedFailureDiagnosticReport = null;
            meta.CurrentProcessingStage = "Queued";
            if (string.IsNullOrWhiteSpace(meta.StatisticsOperationId))
                meta.StatisticsOperationId = Guid.NewGuid().ToString("N");
            DvdImportOptions? dvdOptions = meta.IsDvdEncode
                ? meta.DvdEncodeOptions
                : null;
            bool isDvdEncode = dvdOptions != null;
            string logicalSourcePath = isDvdEncode
                ? Path.GetDirectoryName(dvdOptions!.Candidate.Segments[0].Path) ?? file
                : file;
            string displayName = isDvdEncode
                ? $"{Path.GetFileNameWithoutExtension(dvdOptions!.OutputPath)} ({dvdOptions.Candidate.TitleSetId})"
                : Path.GetFileName(file);

            // Watched files can be queued and started before the background
            // estimate pass attaches metadata to the row. Resolve duration here
            // as a final pre-encode guarantee so percentage, ETA, elapsed media
            // time, and the main progress bar update exactly like manual imports.
            if (durationSec <= 0)
            {
                if (isDvdEncode)
                    durationSec = dvdOptions!.Candidate.CombinedDurationSeconds;

                UiInvoke(() => SetEncodeRowState(
                    row,
                    "Reading metadata",
                    "0%",
                    "--:--:--",
                    "Reading media duration before encoding."));

                try
                {
                    if (durationSec <= 0)
                    {
                        durationSec = await Task.Run(
                            () => ProbeDurationSeconds(file),
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (durationSec > 0)
                {
                    double resolvedDuration = durationSec;
                    UiInvoke(() => EnsureRowMeta(row).DurationSec = resolvedDuration);
                }
            }

            // Which encode number is this?
            _encodeProcessedCount++;
            int totalNow = _activeEncodeQueue?.Count ?? dgvEncodeQueue.Rows.Count;
            int remaining = Math.Max(0, totalNow - _encodeProcessedCount);

            // Basic status + metrics wiring
            Ui(() =>
            {
                if (row.DataGridView != dgvEncodeQueue)
                    return;

                lblEncodeStatus.Text =
                    $"Encoding: {displayName} ({_encodeProcessedCount}/{totalNow}) – Queued: {remaining}";

                BeginEncodeMetricsForRow(row);
                _activeEncodeRow = row;
                SetEncodeRowState(row, "Encoding", "0%", "--:--:--", "Encoding is in progress.");
                UpdateOperationProgressPresentation();
            });

            // Start per-job log capture
            var jobLog = new StringBuilder();
            var jobLogCapture = new JobLogCapture();
            _activeJobLog.Value = jobLogCapture;
            void AppendJobLog(string line)
            {
                lock (jobLog) jobLog.AppendLine(line);
                meta.AppendInspectorLogLine(line);
            }
            var jobStartUtc = DateTime.UtcNow;
            var lifecycle = new EncodeLifecycleDiagnostics();
            lifecycle.Record(EncodeLifecycleEvent.QueueDispatch, dispatchUtc);
            long previousCompletionTicks = Interlocked.Read(ref _lastQueueCompletedTicks);
            if (_sequentialQueueTiming && previousCompletionTicks > 0)
                lifecycle.RecordPreviousQueueCompletion(new DateTime(previousCompletionTicks, DateTimeKind.Utc));
            if (meta.StatisticsStartUtc == default)
                meta.StatisticsStartUtc = jobStartUtc;
            long? sourceSizeBytes = isDvdEncode
                ? dvdOptions!.Candidate.CombinedSizeBytes
                : TryGetFileSizeBytes(file);
            int? statisticsSourceHeight = isDvdEncode ? dvdOptions!.Candidate.VideoHeight : null;

            LibraryPolicyQueueItem? policyIntent = meta.LibraryPolicyIntent;
            EncodingPreset? policyEncodingPreset = null;
            if (policyIntent != null)
            {
                policyEncodingPreset = string.IsNullOrWhiteSpace(policyIntent.EncodingPresetName)
                    ? null
                    : _presetService.LoadAll().FirstOrDefault(preset => preset.Name.Equals(policyIntent.EncodingPresetName, StringComparison.OrdinalIgnoreCase));
            }

            EncodeExecutionSnapshotSettings executionSettings = CaptureEncodeExecutionSnapshotSettings(runOutputContainer);

            _runningEncodeJobs[row] = isDvdEncode
                ? dvdOptions!.OutputPath
                : file;
            ScheduleQueueWorkspaceRefresh();
            UpdateTrayStatus();
            string attemptedOutputPath = string.Empty;
            string stagedOutputPath = string.Empty;
            OutputContainerDecision? appliedContainerDecision = null;
            EncodingDiagnosticSummary? diagnosticSummary = null;
            System.Diagnostics.Stopwatch? activeFinalizationWatch = null;
            double finalizationElapsedSeconds = 0;
            void CompleteFinalizationSpan()
            {
                if (activeFinalizationWatch is null)
                    return;
                activeFinalizationWatch.Stop();
                finalizationElapsedSeconds += activeFinalizationWatch.Elapsed.TotalSeconds;
                activeFinalizationWatch = null;
            }
            void StartFinalizationSpan()
            {
                CompleteFinalizationSpan();
                activeFinalizationWatch = System.Diagnostics.Stopwatch.StartNew();
            }
            bool diagnosticStarted = false;
            EncodeExecutionAttempt? executionAttempt = null;
            string encoderText = string.Empty;
            string videoCodec = string.Empty;
            string analysisEncoderId = string.Empty;
            string analysisEncoderPreset = string.Empty;
            bool analysisTenBit = false;
            string analysisOutputContainer = runOutputContainer.ToString();
            string analysisRestoration = _config.VideoRestoration?.Preset.ToString() ?? "Off";
            OutputContainerSelection requestedOutputContainer = runOutputContainer;
            double? targetMb = null;
            int estimateQuality = 0;
            string encoderPreset = string.Empty;
            bool tenBit = false;
            ContainerCompatibilityPolicy compatibilityPolicy = ContainerCompatibilityPolicy.Intelligent;
            try
            {
                EncodingInputSource? preparedInput = null;
                EncodeExecutionDvdFacts? dvdFacts = null;
                EncodeExecutionSourceMeasurements? sourceMeasurements = null;
                if (isDvdEncode)
                {
                    UiInvoke(() => SetEncodeRowState(
                        row,
                        "Preparing DVD segments",
                        "0%",
                        "--:--:--",
                        "Opening the selected DVD title as one continuous program stream."));
                    var inputFactory = new DvdEncodingInputFactory();
                    preparedInput = inputFactory.Create(dvdOptions!);
                    DvdTitleCandidate candidate = dvdOptions!.Candidate;
                    dvdFacts = new EncodeExecutionDvdFacts(
                        dvdOptions.OutputPath,
                        candidate.CombinedSizeBytes,
                        candidate.CombinedDurationSeconds,
                        candidate.VideoWidth,
                        candidate.VideoHeight,
                        candidate.FrameRate,
                        candidate.VideoCodec);
                    jobLog.AppendLine(
                        $"DVD logical source: {logicalSourcePath} ({dvdOptions!.Candidate.TitleSetId}, " +
                        $"{dvdOptions.Candidate.Segments.Count} segments)");
                    jobLog.AppendLine(
                        $"Selected audio streams: {string.Join(", ", dvdOptions.SelectedAudioStreamIndexes)}");
                    jobLog.AppendLine(
                        $"Selected subtitle streams: {string.Join(", ", dvdOptions.SelectedSubtitleStreamIndexes)}");
                    jobLog.AppendLine("Source deletion: disabled");
                }
                else
                {
                    MediaInfoService.MediaInfo mediaInfo =
                        _mediaInfoService.GetInfo(file);
                    sourceMeasurements = new EncodeExecutionSourceMeasurements
                    {
                        AudioBitrateKbps = mediaInfo.AudioBitrateKbps,
                        AudioStreamCount = mediaInfo.AudioStreamCount,
                        SubtitleBitrateKbps = mediaInfo.SubtitleBitrateKbps,
                        SubtitleStreamCount = mediaInfo.SubtitleStreamCount,
                        EstimatedPlannedAudioBitrateKbps = meta.EstimatedPlannedAudioBitrateKbps,
                        Height = mediaInfo.Height
                    };
                }

                double? queuedEstimate = _estimatedSizeMap.TryGetValue(file, out double estimate) && estimate > 0
                    ? estimate
                    : null;
                EncodeExecutionSnapshotBuildResult buildResult = _encodeExecutionSnapshotBuilder.BuildWithDetails(
                    new EncodeExecutionSnapshotIdentity
                    {
                        OperationId = meta.StatisticsOperationId,
                        StatisticsStartUtc = meta.StatisticsStartUtc,
                        MediaFluxVersion = Application.ProductVersion,
                        StatisticsPath = AppPaths.EncodingStatisticsFile,
                        ContainerCompatibilityConfirmed = _mp4CompatibilityConfirmedForRun
                    },
                    executionSettings,
                    new EncodeExecutionSnapshotItem
                    {
                        SourceFilePath = file,
                        LogicalSourcePath = logicalSourcePath,
                        PreparedInput = preparedInput,
                        SourceMeasurements = sourceMeasurements,
                        CustomCompressionProfile = meta.CustomCompressionProfile,
                        CustomTargetMb = meta.CustomTargetMb,
                        EstimatedTargetMb = queuedEstimate,
                        SizePredictionCalibration = meta.SizePredictionCalibration,
                        PredictionShadowExperimentAssignment = meta.PredictionShadowExperimentAssignment,
                        SourceSizeBytes = sourceSizeBytes,
                        MediaDurationSeconds = durationSec,
                        LibraryPolicyIntent = policyIntent,
                        PolicyEncodingPreset = policyEncodingPreset,
                        Dvd = dvdFacts
                    });
                var builtSnapshot = buildResult.Snapshot;
                bool storageSavingsApplies = buildResult.StorageSavingsApplies;
                bool useStorageQualityTarget = buildResult.UsesStorageQualityTarget;
                StorageSavingsOptions storageSavings = buildResult.StorageSavings;
                EncodingInputSource inputSource = builtSnapshot.Input;
                encoderText = builtSnapshot.EncoderText;
                videoCodec = builtSnapshot.Codec;
                analysisEncoderId = builtSnapshot.Encoder.EncoderId;
                analysisEncoderPreset = builtSnapshot.EncoderPreset ?? string.Empty;
                analysisTenBit = builtSnapshot.TenBit;
                requestedOutputContainer = builtSnapshot.OutputContainer;
                analysisOutputContainer = builtSnapshot.OutputContainer.ToString();
                analysisRestoration = builtSnapshot.Restoration.Preset.ToString();
                targetMb = builtSnapshot.TargetMb;
                estimateQuality = builtSnapshot.QualityValue ?? 0;
                encoderPreset = builtSnapshot.EncoderPreset ?? string.Empty;
                tenBit = builtSnapshot.TenBit;
                compatibilityPolicy = builtSnapshot.CompatibilityPolicy;
                statisticsSourceHeight = builtSnapshot.SourceHeight;

                string sourceResolution = !string.IsNullOrWhiteSpace(meta.Resolution)
                    ? meta.Resolution
                    : statisticsSourceHeight is > 0 ? $"{statisticsSourceHeight}p" : "Unknown";
                int? diagnosticOutputHeight = builtSnapshot.OutputHeight;
                _encodingDiagnosticsService.Start(new EncodingDiagnosticJob(
                    meta.StatisticsOperationId, displayName, encoderText,
                    builtSnapshot.Encoder.EncoderId, videoCodec, encoderPreset,
                    sourceResolution, diagnosticOutputHeight is > 0 ? $"{diagnosticOutputHeight}p" : sourceResolution,
                    tenBit ? 10 : 8, durationSec > 0 ? durationSec : null, logicalSourcePath), jobStartUtc);
                _encodingDiagnosticsService.AttachLifecycle(meta.StatisticsOperationId, lifecycle);
                diagnosticStarted = true;

                // Per-job ffmpeg output callback
                Action<string> jobCallback = line =>
                {
                    AppendJobLog(line);
                    if (line.StartsWith("Analyzing compression", StringComparison.Ordinal) ||
                        line.StartsWith("Testing compression", StringComparison.Ordinal) ||
                        line.StartsWith("Encoding at CQ", StringComparison.Ordinal) || line.StartsWith("Encoding at CRF", StringComparison.Ordinal))
                        UiInvoke(() => { if (row.DataGridView == dgvEncodeQueue) SetEncodeRowState(row, line, "", "", line); });
                    if (line.StartsWith("[AdaptiveStorageSavingsRetry] Attempt 1 finalization completed", StringComparison.Ordinal))
                        CompleteFinalizationSpan();
                    _encodingDiagnosticsService.UpdateProgress(meta.StatisticsOperationId, line, durationSec > 0 ? durationSec : null);
                    HandleFfmpegProgressLineForRow(row, jobLog, durationSec, line);
                };

                executionAttempt = _encodeExecutionOrchestrator.CreateAttempt(builtSnapshot);
                var executionCallbacks = new EncodeExecutionCallbacks
                {
                    Progress = jobCallback,
                    StructuredProgress = progress =>
                        Ui(() => ApplyStructuredEncodeProgress(row, progress)),
                    AiProgress = progress => ApplyAiIntermediateProgress(row, progress),
                    OutputPathChanged = path => attemptedOutputPath = path,
                    StagingPathChanged = path => stagedOutputPath = path,
                    FinalizationStatusChanged = status =>
                    {
                        if (status == "Verifying output")
                            StartFinalizationSpan();
                        AppendJobLog($"[MediaFlux] {status}.");
                        UiInvoke(() =>
                        {
                            if (row.DataGridView == dgvEncodeQueue)
                            {
                                SetEncodeRowState(
                                    row,
                                    status,
                                    "99%",
                                    "00:00:00",
                                    $"{status}. The original source is retained until final verification completes.");
                                UpdateOperationProgressPresentation();
                            }
                        });
                    },
                    FaststartStarted = () => Ui(() =>
                    {
                        if (row.DataGridView == dgvEncodeQueue)
                        {
                            SetEncodeRowState(row, EncodeLifecycleDiagnostics.FaststartStatus,
                                EncodeLifecycleDiagnostics.FaststartProgress, "--:--:--",
                                "FFmpeg is relocating MP4 metadata; output verification follows.");
                            UpdateOperationProgressPresentation();
                        }
                    }),
                    ContainerDecision = decision => appliedContainerDecision = decision,
                    PlanSnapshot = snapshot =>
                    {
                        meta.IntelligencePlan = snapshot.Plan;
                        if (snapshot.Plan.AdaptiveSelection is { } adaptiveSelection)
                            AppendJobLog($"[AdaptiveStorageSavings] Preferred={adaptiveSelection.PreferredQuality}; selected={adaptiveSelection.SelectedQuality}; worst={adaptiveSelection.WorstAcceptableQuality}; {adaptiveSelection.Reason}");
                        AppendJobLog(EncodingPlanService.DescribeSummary(snapshot.Plan));
                        Ui(() =>
                        {
                            RefreshQueueWorkspaceRow(row);
                            if (builtSnapshot.AdaptiveStorageSavingsEnabled && snapshot.Plan.Quality is { } resolvedQuality)
                            {
                                row.Cells["colEstimatedSize"].Value = snapshot.Plan.AdaptiveSelection?.Disposition == AdaptiveSelectionDisposition.Skipped
                                    ? "Skipped — insufficient savings at acceptable quality"
                                    : $"{(resolvedQuality.Mechanism == EncoderQualityMechanism.Cq ? "CQ" : "CRF")} {resolvedQuality.EffectiveQuality} selected";
                                row.Cells["colEstimatedSize"].ToolTipText = snapshot.Plan.AdaptiveSelection?.Reason ?? "Resolved source-adaptive quality; actual output remains subject to storage validation.";
                            }
                            RefreshCurrentEncodingIntelligence(row, meta);
                        });
                    },
                    Diagnostic = line =>
                    {
                        Debug.WriteLine(line);
                        if (line.Contains("Assigned attempt did not produce a Frozen event", StringComparison.Ordinal) ||
                            line.Contains("Stored experiment assignment no longer matches", StringComparison.Ordinal))
                            AppendJobLog(line);
                    },
                    PlanDiverged = divergence =>
                        AppendJobLog($"[EncodingPlan] Shadow divergence: {divergence}"),
                    ExecutionOutcome = outcome =>
                     {
                         meta.IntelligenceOutcome = outcome;
                         AppendJobLog(EncodingPlanService.DescribeRecovery(outcome));
                         AppendJobLog(EncodingPlanService.DescribeLifecycle(outcome));
                         Ui(() => RefreshCurrentEncodingIntelligence(row, meta));
                     },
                    RecoveryStatus = update =>
                         UiInvoke(() =>
                         {
                             string status = JobHistoryPresentation.ActiveRecoveryStatus(update);
                             if (row.DataGridView == dgvEncodeQueue)
                                 SetEncodeRowState(row, status, "", "", string.IsNullOrWhiteSpace(update.Detail) ? status : update.Detail);
                         }),
                    FailureDiagnosticReport = report =>
                        meta.CuratedFailureDiagnosticReport = report
                };

                jobLog.AppendLine(
                    $"[MediaFlux] Encode request: source='{inputSource.SourcePath}'; " +
                    $"configured-container={requestedOutputContainer}; effective-container=authoritative resolution pending; " +
                    $"compatibility-policy={compatibilityPolicy}; " +
                    "ffmpeg-launched=false (pending preflight)." );

                if (!string.IsNullOrWhiteSpace(meta.EstimateDiagnostic))
                    jobLog.AppendLine(meta.EstimateDiagnostic);
                if (storageSavingsApplies)
                {
                    jobLog.AppendLine(
                        useStorageQualityTarget
                            ? $"Storage savings mode: HEVC quality target {estimateQuality} (CQ/CRF/ICQ). " +
                              "Output size is a projection and visual quality may be reduced."
                            : $"Storage savings mode: HEVC video bitrate target " +
                              $"{storageSavings.SourceVideoBitratePercent:0.#}% of source. " +
                              "Visual quality may be reduced.");
                }

                EncodeExecutionResult executionResult = await _encodeExecutionOrchestrator.ExecuteAsync(
                    executionAttempt,
                    executionCallbacks,
                    lifecycle,
                    cancellationToken);
                var result = executionResult.EncodedOutput;

                if (!isDvdEncode)
                {
                    jobLog.AppendLine(
                        $"[MediaFlux] FFmpeg arguments: {result.DiagnosticArguments}");
                }
                jobLog.AppendLine(
                    $"[MediaFlux] Validated and finalized: {result.ValidationSummary}");

                SourceDeletionResult sourceDeletion = executionResult.SourceDeletion;
                jobLog.AppendLine($"[MediaFlux] {sourceDeletion.Message}");

                DateTime jobEndUtc = DateTime.UtcNow;
                long? outputSizeBytes =
                    result.FinalOutputSizeBytes ??
                    TryGetFileSizeBytes(result.OutputPath);

                // On success, mark 100% and clear ETA after validated finalization.
                System.Threading.Interlocked.Increment(ref _encodeSucceededCount);
                UiInvoke(() =>
                {
                    if (row.DataGridView != dgvEncodeQueue)
                        return;

                    EncodingTerminalResult? completedTerminal = meta.IntelligenceOutcome?.TerminalResult;
                    SetEncodeRowState(
                        row,
                        JobHistoryPresentation.TerminalLabel(JobStatus.Success, completedTerminal),
                        "100%",
                        "00:00:00",
                        JobHistoryPresentation.SummaryFor(completedTerminal, $"Output validated and finalized. {sourceDeletion.Message}"));
                    lifecycle.Record(EncodeLifecycleEvent.QueueCompleted);
                    if (_sequentialQueueTiming)
                        Interlocked.Exchange(ref _lastQueueCompletedTicks, DateTime.UtcNow.Ticks);
                });

                CompleteFinalizationSpan();
                diagnosticSummary = _encodingDiagnosticsService.Complete(
                    meta.StatisticsOperationId,
                    finalizationElapsedSeconds,
                    lifecycle.Snapshot(inputSource.SourcePath, result.OutputPath, outputSizeBytes));
                meta!.StatisticsProcessingSeconds +=
                    Math.Max(0, (jobEndUtc - jobStartUtc).TotalSeconds);

                // append success to history – never let this kill the job
                try
                {
                    lock (_historyLock)
                    {
                        _historyService.AppendEncodingOutcome(new JobHistoryRecord
                        {
                            Id = meta.StatisticsOperationId,
                            Type = isDvdEncode ? JobType.DvdEncode : JobType.Encode,
                            Status = JobStatus.Success,
                            StartUtc = jobStartUtc,
                            EndUtc = jobEndUtc,
                            SourcePath = logicalSourcePath,
                            OutputPath = result.OutputPath,
                            EncoderMode = encoderText,
                            TargetMb = targetMb,
                            DurationSec = durationSec,
                            Log = isDvdEncode
                                ? $"FFmpeg arguments: {result.DiagnosticArguments}{Environment.NewLine}" +
                                  jobLog
                                : jobLog.ToString(),
                            Notes = isDvdEncode
                                ? $"Codec={videoCodec}; TitleSet={dvdOptions!.Candidate.TitleSetId}; " +
                                  $"Segments={dvdOptions.Candidate.Segments.Count}; " +
                                  $"Recommended={dvdOptions.Candidate.IsLikelyMainFeature}; " +
                                  $"Validated and finalized; {sourceDeletion.Message}"
                                : $"Codec={videoCodec}; Validated and finalized; {sourceDeletion.Message}",
                            DvdTitleSet = isDvdEncode
                                ? dvdOptions!.Candidate.TitleSetId
                                : null,
                            DvdSegmentCount = isDvdEncode
                                ? dvdOptions!.Candidate.Segments.Count
                                : null,
                            DvdOutputMode = isDvdEncode
                                ? DvdOutputMode.EncodeUsingCurrentSettings.ToString()
                                : null,
                            SourceSizeBytes = sourceSizeBytes,
                            OutputSizeBytes = outputSizeBytes,
                            WasRecommendedDvdTitle = isDvdEncode
                                ? dvdOptions!.Candidate.IsLikelyMainFeature
                                : null,
                            FinalizationOutcome = "ValidatedAndFinalized",
                            StagingPath = result.StagingPath,
                            SourceDeletionResult = sourceDeletion.Message,
                            RequestedOutputContainer = result.RequestedOutputContainer.ToString(),
                            ResolvedOutputContainer = result.ResolvedOutputContainer.ToString(),
                            ContainerDecisionReason = result.ContainerDecisionReason,
                            DiagnosticSummary = diagnosticSummary,
                            StorageSavings = result.StorageSavings,
                            AdaptiveSelection = executionAttempt?.AdaptiveSelection,
                            TerminalResult = meta.IntelligenceOutcome?.TerminalResult ?? EncodingTerminalResult.Completed
                        }, executionAttempt?.ExecutionOutcome ?? meta.IntelligenceOutcome);
                    }
                }
                catch (Exception logEx)
                {
                    Debug.WriteLine($"History append (success) failed: {logEx}");
                    // We ignore this; the encode itself succeeded.
                }

                if (meta.IntelligencePlan?.SourceAdaptiveShadow is { } shadowDecision)
                {
                    AppendJobLog(SourceAdaptiveShadowOutcome.FromOutput(
                        shadowDecision, result.FinalOutputProbe, outputSizeBytes).Describe());
                }

                _encodeExecutionOrchestrator.RecordSuccessfulExecution(
                    executionAttempt ?? throw new InvalidOperationException("The completed encode has no execution attempt snapshot."),
                    jobEndUtc,
                    outputSizeBytes,
                    meta.StatisticsProcessingSeconds,
                    $"Validated and finalized. {sourceDeletion.Message}",
                    diagnosticSummary: diagnosticSummary,
                    recoveredSuccessful: jobLog.ToString().Contains("Result=Succeeded", StringComparison.Ordinal),
                    finalOutputProbe: result.FinalOutputProbe);

                try
                {
                    UiInvoke(() =>
                    {
                        if (isDvdEncode)
                        {
                            _mediaInfoService.Invalidate(result.OutputPath);
                            AddCompletedEncodePath(result.OutputPath);
                        }
                        else
                        {
                            RememberCompletedEncodePaths(file, result.OutputPath);
                        }
                        RemoveRowAndCleanup(row, allowCompletedEncodeJob: true);

                        // Re-scan the current input folder and merge any changes
                        RescanInputFolderAndMerge(recomputeEstimates: false);

                        // Recompute estimates for whatever is now in the grid
                        SafeRefreshEstimates();
                        UpdateSizeTotals();
                        UpdateSelectionSizeTotals();
                        ClearEncodeInputFolderIfQueueEmptyAfterProcessing();
                    });
                }
                catch (Exception cleanupEx)
                {
                    Debug.WriteLine($"Post-encode cleanup failed for {file}: {cleanupEx}");
                    // At this point, encode is done; we keep going rather than poisoning the run.
                }
            }
            catch (Exception ex)
            {
                DateTime attemptEndUtc = DateTime.UtcNow;
                CompleteFinalizationSpan();
                diagnosticSummary = _encodingDiagnosticsService.Complete(
                    meta.StatisticsOperationId,
                    finalizationElapsedSeconds);
                meta!.StatisticsProcessingSeconds +=
                    Math.Max(0, (attemptEndUtc - jobStartUtc).TotalSeconds);
                bool isCanceled = _cancelEncode || ex is OperationCanceledException;
                bool adaptivePolicySkipped = ex is AdaptiveStorageSavingsSkippedException;
                bool storagePolicyRejected = adaptivePolicySkipped || ex is EncodeFinalizationException { Result.FailureKind: EncodeFinalizationFailureKind.StoragePolicyRejected };
                if (!isCanceled && !storagePolicyRejected)
                {
                    meta.FailureAnalysis = EncodeFailureAnalysisService.Analyze(
                        new EncodeFailureAnalysisContext(
                            ex,
                            jobLog.ToString(),
                            meta.CurrentProcessingStage,
                            IsCanceled: false,
                            logicalSourcePath,
                            string.IsNullOrWhiteSpace(attemptedOutputPath) ? stagedOutputPath : attemptedOutputPath,
                            analysisEncoderId,
                            encoderText,
                            videoCodec,
                            analysisEncoderPreset,
                            analysisTenBit,
                            appliedContainerDecision?.Resolved.ToString() ?? analysisOutputContainer,
                            analysisRestoration));
                }
                else
                {
                    meta.FailureAnalysis = null;
                }
                EncodeFinalizationException? finalizationFailure =
                    ex as EncodeFinalizationException;
                EncodeFinalizationResult? finalizationResult =
                    finalizationFailure?.Result ??
                    (ex as EncodeFinalizationCanceledException)?.Result;
                EncodeExecutionAssignmentValidationException? assignmentValidationFailure =
                    ex as EncodeExecutionAssignmentValidationException;
                EncodingTerminalResult? terminalResult = meta.IntelligenceOutcome?.TerminalResult;
                if (storagePolicyRejected)
                    terminalResult = adaptivePolicySkipped ? EncodingTerminalResult.AdaptiveStorageSavingsSkipped : EncodingTerminalResult.StoragePolicyRejected;
                if (assignmentValidationFailure != null)
                    terminalResult = EncodingTerminalResult.ValidationFailed;
                if (isCanceled)
                    terminalResult = EncodingTerminalResult.Canceled;
                var notes = isCanceled
                    ? "Cancelled by user."
                    : JobHistoryPresentation.SummaryFor(terminalResult, ex.Message);

                bool cleanupEnabled = isCanceled
                    ? true
                    : _config.DeleteFailedEncodeOutputs;
                string recoverableOutputPath =
                    finalizationResult?.RecoverableOutputPath ?? "";
                string incompleteOutputPath =
                    !string.IsNullOrWhiteSpace(recoverableOutputPath)
                        ? recoverableOutputPath
                        : stagedOutputPath;
                string cleanupResult =
                    await IncompleteEncodeOutputCleanupService.CleanupAsync(
                    logicalSourcePath,
                    incompleteOutputPath,
                    cleanupEnabled,
                    isCanceled ? "canceled" : "failed");
                string sourceRetention =
                    assignmentValidationFailure != null
                        ? "Original source retained because assigned experiment validation failed before encode execution."
                        : "Original source retained because validated finalization did not complete.";
                string historyNotes =
                    $"{notes} {sourceRetention} Incomplete output cleanup: {cleanupResult}";
                if (isDvdEncode)
                {
                    historyNotes +=
                        $" TitleSet={dvdOptions!.Candidate.TitleSetId}; " +
                        $"Segments={dvdOptions.Candidate.Segments.Count}; " +
                        "Source deletion disabled.";
                }

                try
                {
                    lock (_historyLock)
                    {
                        _historyService.AppendEncodingOutcome(new JobHistoryRecord
                        {
                            Id = meta.StatisticsOperationId,
                            Type = isDvdEncode ? JobType.DvdEncode : JobType.Encode,
                            Status = isCanceled
                                ? JobStatus.Canceled
                                : storagePolicyRejected ? JobStatus.Skipped : JobStatus.Failed,
                            StartUtc = jobStartUtc,
                            EndUtc = DateTime.UtcNow,
                            SourcePath = logicalSourcePath,
                            OutputPath = attemptedOutputPath,
                            EncoderMode = encoderText,
                            TargetMb = targetMb,
                            DurationSec = durationSec,
                            Log = !isCanceled && !string.IsNullOrWhiteSpace(meta.CuratedFailureDiagnosticReport)
                                ? meta.CuratedFailureDiagnosticReport
                                : jobLog.ToString(),
                            Notes = historyNotes,
                            DvdTitleSet = isDvdEncode
                                ? dvdOptions!.Candidate.TitleSetId
                                : null,
                            DvdSegmentCount = isDvdEncode
                                ? dvdOptions!.Candidate.Segments.Count
                                : null,
                            DvdOutputMode = isDvdEncode
                                ? DvdOutputMode.EncodeUsingCurrentSettings.ToString()
                                : null,
                            SourceSizeBytes = sourceSizeBytes,
                            OutputSizeBytes = adaptivePolicySkipped ? null : TryGetFileSizeBytes(incompleteOutputPath),
                            WasRecommendedDvdTitle = isDvdEncode
                                ? dvdOptions!.Candidate.IsLikelyMainFeature
                                : null,
                            ErrorSummary = notes,
                            StorageSavings = isCanceled ? null : finalizationResult?.StorageSavings,
                            AdaptiveSelection = executionAttempt?.AdaptiveSelection,
                            FinalizationOutcome =
                                finalizationResult?.FailureKind.ToString() ??
                                (assignmentValidationFailure != null
                                    ? "ExperimentAssignmentValidationFailed"
                                    : isCanceled ? "Canceled" : "FfmpegFailed"),
                            StagingPath = stagedOutputPath,
                            SourceDeletionResult = sourceRetention,
                            RequestedOutputContainer = requestedOutputContainer.ToString(),
                            ResolvedOutputContainer = appliedContainerDecision?.Resolved.ToString(),
                            ContainerDecisionReason = appliedContainerDecision?.Reason,
                            DiagnosticSummary = diagnosticSummary,
                            TerminalResult = isCanceled ? EncodingTerminalResult.Canceled : terminalResult ?? EncodingTerminalResult.EncodeFailed
                        }, executionAttempt?.ExecutionOutcome ?? meta.IntelligenceOutcome);
                    }
                }
                catch (Exception logEx)
                {
                    Debug.WriteLine($"History append (failure) failed: {logEx}");
                    // Don't let logging errors mask the *real* encode error.
                }

                var centralLogPath = ErrorLogService.Append(
                    Application.StartupPath,
                    isCanceled ? "Encode job cancelled" : storagePolicyRejected ? "Encode skipped — insufficient savings" : "Encode job failed",
                    logicalSourcePath,
                    ex,
                    $"Encoder Mode: {encoderText}{Environment.NewLine}" +
                    $"Target MB   : {(targetMb.HasValue ? targetMb.Value.ToString("0.##") : "auto")}{Environment.NewLine}" +
                    $"Duration Sec: {durationSec:0.##}{Environment.NewLine}" +
                    $"Final Output: {attemptedOutputPath}{Environment.NewLine}" +
                    $"Staged File : {stagedOutputPath}{Environment.NewLine}" +
                    $"Recoverable : {recoverableOutputPath}{Environment.NewLine}" +
                    $"Finalization: {finalizationResult?.FailureKind.ToString() ?? "Not reached"}{Environment.NewLine}" +
                    $"Cleanup     : {cleanupResult}{Environment.NewLine}{Environment.NewLine}" +
                    "Captured Job Log:" + Environment.NewLine +
                    jobLog);

                bool retryQueued = false;
                if (!isCanceled)
                {
                    retryQueued = executionAttempt?.AdaptiveSelection is null && EncodingRetryPolicy.AllowsAutomaticRetry(
                            terminalResult, meta.PredictionShadowExperimentAssignment is not null) &&
                        TryQueueFailedRowForAutoRetry(row);
                    if (storagePolicyRejected)
                        System.Threading.Interlocked.Increment(ref _encodeStorageRejectedCount);
                    else if (!retryQueued)
                        System.Threading.Interlocked.Increment(ref _encodeFailedCount);
                }

                DateTime statisticsEndUtc = DateTime.UtcNow;
                if (executionAttempt is not null)
                {
                    _encodeExecutionOrchestrator.RecordFailedExecution(
                        executionAttempt,
                        statisticsEndUtc,
                        isCanceled,
                        finalizationFailure?.Result.FailureKind ??
                            (assignmentValidationFailure != null ? EncodeFinalizationFailureKind.Validation : null),
                        attemptedOutputPath,
                        meta.StatisticsProcessingSeconds,
                        historyNotes,
                        diagnosticSummary,
                        retryQueued);
                }
                else if (isCanceled || !retryQueued)
                {
                    RecordEncodingStatistics(
                        meta.StatisticsOperationId,
                        meta.StatisticsStartUtc,
                        statisticsEndUtc,
                        finalizationFailure?.Result.FailureKind switch
                        {
                            EncodeFinalizationFailureKind.StoragePolicyRejected => EncodingStatisticsOutcome.StoragePolicyRejected,
                            EncodeFinalizationFailureKind.Validation => EncodingStatisticsOutcome.ValidationFailed,
                            EncodeFinalizationFailureKind.Promotion => EncodingStatisticsOutcome.PromotionFailed,
                            EncodeFinalizationFailureKind.FinalVerification => EncodingStatisticsOutcome.FinalVerificationFailed,
                            _ => isCanceled ? EncodingStatisticsOutcome.Cancelled : EncodingStatisticsOutcome.Failed
                        },
                        logicalSourcePath,
                        attemptedOutputPath,
                        videoCodec,
                        encoderText,
                        sourceSizeBytes,
                        outputSizeBytes: null,
                        mediaDurationSeconds: durationSec > 0 ? durationSec : null,
                        processingSeconds: meta.StatisticsProcessingSeconds,
                        notes: historyNotes,
                        diagnosticSummary: diagnosticSummary,
                        predictionPlan: meta.IntelligencePlan,
                        executionOutcome: meta.IntelligenceOutcome);
                }

                Ui(() =>
                {
                    if (row.DataGridView == dgvEncodeQueue)
                    {
                        SetEncodeRowState(
                            row,
                            isCanceled
                                ? "Canceled"
                                : retryQueued
                                    ? "Retry Queued"
                                    : assignmentValidationFailure != null
                                        ? "Validation Failed"
                                    : finalizationFailure?.Result.FailureKind ==
                                      EncodeFinalizationFailureKind.Validation
                                        ? "Validation Failed"
                                    : storagePolicyRejected
                                        ? adaptivePolicySkipped ? "Skipped — insufficient savings at acceptable quality" : "Skipped — insufficient savings"
                                         : finalizationFailure != null
                                             ? "Finalization Failed"
                                             : JobHistoryPresentation.TerminalLabel(JobStatus.Failed, terminalResult),
                            isCanceled ? "Canceled" : retryQueued ? "Retry Queued" : storagePolicyRejected ? "Skipped" : "Failed",
                            "",
                            (isCanceled
                                ? "Canceled by user."
                                : retryQueued
                                    ? "Failed once; queued for automatic retry after the current queue finishes."
                                    : ex.Message) +
                                $" {sourceRetention} Incomplete output cleanup: {cleanupResult}");
                        row.Cells["colProgress"].ToolTipText = $"{ex.Message}{Environment.NewLine}Incomplete output cleanup: {cleanupResult}";
                    }

                    lblEncodeStatus.Text = isCanceled
                        ? $"Canceled: {displayName}"
                        : retryQueued
                            ? $"Retry queued: {displayName}. Continuing queue."
                        : assignmentValidationFailure != null
                            ? $"Experiment assignment validation failed — original retained: {displayName}"
                        : storagePolicyRejected
                            ? $"Skipped — insufficient savings: {displayName}. Original retained. Continuing queue."
                        : finalizationFailure?.Result.FailureKind ==
                          EncodeFinalizationFailureKind.Validation
                            ? $"Output validation failed — original retained: {displayName}"
                                     : finalizationFailure != null
                                 ? $"Output finalization failed — original retained: {displayName}"
                             : terminalResult == EncodingTerminalResult.SourceUnrecoverable
                                 ? $"Failed — Source damaged: {displayName}. Continuing queue."
                             : $"Failed: {displayName}. Continuing queue.";
                    toolStripStatusLabel1.Text = storagePolicyRejected ? $"Storage-policy rejection logged: {centralLogPath}" : $"Encode error logged: {centralLogPath}";
                });
                // leave the row so user can retry
            }
            finally
            {
                if (diagnosticStarted && diagnosticSummary == null)
                    _encodingDiagnosticsService.Cancel(meta.StatisticsOperationId);
                _runningEncodeJobs.TryRemove(row, out _);
                ScheduleQueueWorkspaceRefresh();
                UpdateTrayStatus();
                if (ReferenceEquals(_activeJobLog.Value, jobLogCapture))
                    _activeJobLog.Value = null; // stop log capture for this job
                Ui(() =>
                {
                    if (ReferenceEquals(_activeEncodeRow, row))
                        _activeEncodeRow = null;
                    EndEncodeMetricsForRow(row);
                });
            }
        }

        private bool TryQueueFailedRowForAutoRetry(DataGridViewRow row)
        {
            bool retryFailedJobs = UiGet(() => chkRetryFailedJobs?.Checked ?? false, false);
            if (!retryFailedJobs)
                return false;

            if (_activeEncodeQueue == null || row == null || row.IsNewRow || row.DataGridView != dgvEncodeQueue)
                return false;

            var meta = EnsureRowMeta(row);
            if (meta.AutoRetryScheduled)
                return false;

            meta.AutoRetryScheduled = true;

            lock (_activeEncodeQueueLock)
            {
                // The retry is appended as pending work, so its one logical row's
                // QueueSequence follows that pending position for exports/restarts.
                meta.QueueSequence = AllocateEncodeQueueSequence();
                _activeEncodeQueue.Add(row);
            }

            ScheduleQueueExecutionOrderPresentationRefresh();
            System.Threading.Interlocked.Increment(ref _encodeRetryCount);
            return true;
        }

        private static long? TryGetFileSizeBytes(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    ? new FileInfo(path).Length
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private EncodeExecutionSnapshotSettings CaptureEncodeExecutionSnapshotSettings(
            OutputContainerSelection runOutputContainer)
        {
            var fallback = new EncodeExecutionSnapshotSettings
            {
                OutputFolder = string.Empty,
                CompressionProfile = string.Empty,
                EncoderId = VideoEncoderIds.Nvenc,
                CodecFamily = VideoCodecFamily.Hevc,
                VideoFormatText = "H.265 / HEVC (x265)",
                EncoderDisplayTextOverride = "GPU (NVENC)",
                EncoderPreset = "p5",
                NumericQualityValue = 24,
                AutomaticQuality = false,
                QualityTarget = QualityTarget.Balanced,
                TenBit = false,
                AudioChannelSelection = null,
                LimitGpuEncodingQueueToOneJob = _config.LimitGpuEncodingQueueToOneJob,
                IncludeConcurrentEncoderSessions = false,
                AutoTargetSize = false,
                TargetSizeText = string.Empty,
                ScaleMode = EncodingService.ScaleMode.None,
                EnableOutputSuffix = _config.EnableOutputSuffix,
                EnableCodecSuffix = _config.EnableCodecSuffix,
                OutputSuffix = _config.OutputSuffix,
                Restoration = _config.VideoRestoration?.Clone() ?? new VideoRestorationSettings(),
                StorageSavings = _config.StorageSavings?.CloneNormalized() ?? new StorageSavingsOptions(),
                OutputContainer = runOutputContainer,
                CompatibilityPolicy = ContainerCompatibilityPolicy.Intelligent,
                DeleteSourceAfterCompression = false
            };

            return UiGet(
                () => new EncodeExecutionSnapshotSettings
                {
                    OutputFolder = cmbEncodeOutput.Text,
                    CompressionProfile = comboCompressionProfile?.SelectedItem?.ToString()
                        ?? comboCompressionProfile?.Text
                        ?? string.Empty,
                    EncoderId = GetSelectedEncoderId(),
                    CodecFamily = GetSelectedVideoCodecFamily(),
                    VideoFormatText = comboVideoFormat.Text,
                    EncoderPreset = GetSelectedEncoderPreset(),
                    NumericQualityValue = nudAutoQuality is null
                        ? null
                        : (int)nudAutoQuality.Value,
                    AutomaticQuality = IsAutomaticQualitySelected(),
                    QualityTarget = GetSelectedQualityTarget(),
                    TenBit = GetTenBitRequested(),
                    AudioChannelSelection = comboAudioChannels?.SelectedItem?.ToString(),
                    LimitGpuEncodingQueueToOneJob = _config.LimitGpuEncodingQueueToOneJob,
                    IncludeConcurrentEncoderSessions = true,
                    AutoTargetSize = chkAutoTargetSize.Checked,
                    TargetSizeText = txtTargetSize.Text,
                    ScaleMode = GetSelectedScaleMode(),
                    EnableOutputSuffix = _config.EnableOutputSuffix,
                    EnableCodecSuffix = _config.EnableCodecSuffix,
                    OutputSuffix = _config.OutputSuffix,
                    Restoration = _config.VideoRestoration?.Clone() ?? new VideoRestorationSettings(),
                    StorageSavings = _config.StorageSavings?.CloneNormalized() ?? new StorageSavingsOptions(),
                    OutputContainer = runOutputContainer,
                    CompatibilityPolicy = GetContainerCompatibilityPolicy(),
                    DeleteSourceAfterCompression = chkDeleteSource.Checked
                },
                fallback);
        }

        private async Task SendDiscordQueueCompleteNotificationAsync(DateTime queueStartedUtc)
        {
            if (!_config.DiscordQueueNotificationEnabled)
                return;

            string message = FormatDiscordQueueCompleteMessage(
                _config.DiscordQueueCompleteMessage,
                _encodeSucceededCount,
                _encodeFailedCount,
                _encodeRetryCount,
                queueStartedUtc,
                DateTime.UtcNow);

            try
            {
                await DiscordWebhookService.SendAsync(
                    _config.DiscordWebhookUrl,
                    message,
                    _config.DiscordUserMentionId);
                toolStripStatusLabel1.Text = "Encode queue complete; Discord notification sent.";
            }
            catch (Exception ex)
            {
                string logPath = ErrorLogService.Append(
                    Application.StartupPath,
                    "Discord queue-completion notification failed",
                    exception: ex);
                toolStripStatusLabel1.Text = $"Discord notification failed; see {logPath}.";
            }
        }

        internal static string FormatDiscordQueueCompleteMessage(
            string? template,
            int succeeded,
            int failed,
            int retried,
            DateTime startedUtc,
            DateTime finishedUtc)
        {
            string status = failed > 0 ? "Completed with failures" : "Completed successfully";
            string result = string.IsNullOrWhiteSpace(template)
                ? "Encode queue finished."
                : template;

            return result
                .Replace("{total}", (succeeded + failed).ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{succeeded}", succeeded.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{failed}", failed.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{retried}", retried.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{status}", status, StringComparison.OrdinalIgnoreCase)
                .Replace("{computer}", Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                .Replace("{started}", startedUtc.ToLocalTime().ToString("g"), StringComparison.OrdinalIgnoreCase)
                .Replace("{finished}", finishedUtc.ToLocalTime().ToString("g"), StringComparison.OrdinalIgnoreCase)
                .Replace("{duration}", (finishedUtc - startedUtc).ToString(@"hh\:mm\:ss"), StringComparison.OrdinalIgnoreCase);
        }

    }
}
