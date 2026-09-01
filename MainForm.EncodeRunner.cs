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

            var eligibleRows = GetEncodeRowsInVisualOrder()
                .Where(row => row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true })
                .ToList();
            var scope = EncodingScopeResolver.Analyze(
                eligibleRows,
                dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>());

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
            if (!EnsureRequestedVideoEncodersAvailable(requestedRows))
                return;
            if (!await ConfirmExplicitMp4CompatibilityAsync(requestedRows))
                return;
            _activeOutputContainer = GetSelectedOutputContainer();

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

            ReapplyCurrentEncodeQueueSort();

            // Gather rows to process in the same order the user sees in the grid.
            var rowsToProcess = GetEncodeRowsInVisualOrder()
                .Where(r =>
                {
                    bool duplicateExcluded = r.Tag is RowMeta meta && meta.ExcludedFromEncodeAsDuplicate;
                    return (requestedAll || requestedRowSet.Contains(r)) && !duplicateExcluded;
                })
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
            _activeEncodeQueue = rowsToProcess;
            _encodeProcessedCount = 0;
            UpdateQueueEstimatedCompletion();

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
                        row => EncodeSingleRow(row, encodeToken),
                        maxParallel,
                        () => _encodeQueuePaused,
                        () => _cancelEncode,
                        encodeToken,
                        _activeEncodeQueueLock,
                        () => Volatile.Read(ref _pendingEncodeImports) > 0);
                }

                if (_cancelEncode)
                    RecordCancelledPendingRetries(rowsToProcess);

                lblEncodeStatus.Text = _cancelEncode
                    ? "Encoding stopped."
                    : _encodeFailedCount > 0
                        ? $"Done. {_encodeFailedCount} job(s) failed; see the failed rows and central error log."
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
                _encodingActive = false;
                _activeEncodeQueue = null;
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

        private async Task EncodeSingleRow(DataGridViewRow row, CancellationToken cancellationToken)
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

                _currentEncodeDuration = TimeSpan.Zero;
                _currentEncodeTotalDuration = TimeSpan.FromSeconds(durationSec > 0 ? durationSec : 0);
                bool firstActive = BeginEncodeMetricsForRow(row);
                if (firstActive)
                    StartJobTimer();

                _activeEncodeRow = row;
                SetEncodeRowState(row, "Encoding", "0%", "--:--:--", "Encoding is in progress.");
            });

            // Start per-job log capture
            var jobLog = new StringBuilder();
            _activeJobLogSb = jobLog;
            var jobStartUtc = DateTime.UtcNow;
            if (meta.StatisticsStartUtc == default)
                meta.StatisticsStartUtc = jobStartUtc;
            long? sourceSizeBytes = isDvdEncode
                ? dvdOptions!.Candidate.CombinedSizeBytes
                : TryGetFileSizeBytes(file);
            int? statisticsSourceHeight = isDvdEncode ? dvdOptions!.Candidate.VideoHeight : null;

            // Capture encoder + codec as one immutable selection for this job.
            // This prevents a UI change between reads from producing an invalid
            // cross-backend combination.
            ResolvedVideoEncoder fallbackEncoder =
                EncoderRegistry.Default.Resolve(
                    VideoEncoderIds.Nvenc,
                    VideoCodecFamily.Hevc);
            ValidatedEncoderSettings fallbackSettings =
                EncodingRequestValidator.ValidateAndNormalize(
                    EncoderRegistry.Default,
                    fallbackEncoder.Selection,
                    useGpu: true,
                    targetMb: null,
                    preset: "p5",
                    qualityValue: 24,
                    tenBit: false,
                    audioChannels: null,
                    concurrentEncoderSessions: false);
            var encoderSnapshot = UiGet(
                () =>
                {
                    EncoderCapabilities capabilities =
                        GetSelectedEncoderCapabilities();
                    int? audioChannels =
                        GetSelectedAudioChannels();
                    ValidatedEncoderSettings validated =
                        GetValidatedEncoderSettingsFromUi(
                            includeConcurrentSessions: true);
                    return (
                        DisplayText: capabilities.DisplayName,
                        FormatText: comboVideoFormat.Text,
                        Validated: validated,
                        AudioChannels: audioChannels);
                },
                (
                    DisplayText: "GPU (NVENC)",
                    FormatText: "H.265 / HEVC (x265)",
                    Validated: fallbackSettings,
                    AudioChannels: (int?)null));
            LibraryPolicyQueueItem? policyIntent = meta.LibraryPolicyIntent;
            EncodingPreset? policyEncodingPreset = null;
            if (policyIntent != null)
            {
                policyEncodingPreset = string.IsNullOrWhiteSpace(policyIntent.EncodingPresetName)
                    ? null
                    : _presetService.LoadAll().FirstOrDefault(preset => preset.Name.Equals(policyIntent.EncodingPresetName, StringComparison.OrdinalIgnoreCase));
                VideoCodecFamily policyCodec = policyEncodingPreset == null
                    ? policyIntent.ProposedCodec
                    : VideoEncoderCompatibility.ParseCodecFamily(string.IsNullOrWhiteSpace(policyEncodingPreset.VideoCodec) ? policyEncodingPreset.VideoFormat : policyEncodingPreset.VideoCodec);
                string policyEncoderId = policyEncodingPreset == null
                    ? policyIntent.EncoderId
                    : VideoEncoderCompatibility.ResolveEncoderId(string.IsNullOrWhiteSpace(policyEncodingPreset.EncoderId) ? policyEncodingPreset.EncoderMode : policyEncodingPreset.EncoderId, policyCodec);
                ResolvedVideoEncoder resolvedPolicyEncoder = EncoderRegistry.Default.Resolve(policyEncoderId, policyCodec);
                ValidatedEncoderSettings validatedPolicySettings = EncodingRequestValidator.ValidateAndNormalize(
                    EncoderRegistry.Default,
                    resolvedPolicyEncoder.Selection,
                    useGpu: resolvedPolicyEncoder.Provider.Capabilities.IsHardware,
                    targetMb: null,
                    preset: policyEncodingPreset?.EncoderPreset ?? policyIntent.EncoderPreset,
                    qualityValue: policyEncodingPreset?.QualityValue ?? policyIntent.QualityValue,
                    tenBit: policyEncodingPreset?.TenBit ?? policyIntent.PreferredBitDepth >= 10,
                    audioChannels: encoderSnapshot.AudioChannels,
                    concurrentEncoderSessions: false);
                encoderSnapshot = (
                    DisplayText: resolvedPolicyEncoder.Provider.Capabilities.DisplayName,
                    FormatText: CreateCodecDisplayOption(policyCodec).DisplayName,
                    Validated: validatedPolicySettings,
                    AudioChannels: encoderSnapshot.AudioChannels);
            }
            string encoderText = encoderSnapshot.DisplayText;
            string videoCodec =
                encoderSnapshot.Validated.Resolved.Selection.FfmpegCodec;
            bool useGpu = encoderSnapshot.Validated.UseGpu;

            // ==== TARGET SIZE (MB) ====
            double? targetMb = null;
            bool hasCustomTarget = meta.CustomTargetMb.HasValue;
            bool hasCustomProfile = !string.IsNullOrWhiteSpace(meta.CustomCompressionProfile);
            StorageSavingsOptions storageSavings =
                _config.StorageSavings.CloneNormalized();

            string profileText = hasCustomProfile
                ? meta!.CustomCompressionProfile!
                : UiGet(
                    () => comboCompressionProfile!.SelectedItem?.ToString()
                          ?? comboCompressionProfile.Text
                          ?? string.Empty,
                    string.Empty);
            VideoEncoderSelection estimateTargetEncoder =
                encoderSnapshot.Validated.Resolved.Selection;
            int estimateQuality =
                encoderSnapshot.Validated.QualityValue;
            int? estimateTargetHeight = policyIntent == null
                ? UiGet(GetEstimateTargetHeight, null)
                : policyIntent.PreserveSourceResolution ? null : policyIntent.MaximumOutputHeight;
            string targetText = hasCustomProfile
                ? string.Empty
                : UiGet(() => txtTargetSize.Text, string.Empty);
            double manualTargetMb = 0;
            bool hasManualTarget =
                !hasCustomProfile &&
                double.TryParse(targetText, out manualTargetMb) &&
                manualTargetMb > 0;
            bool storageSavingsApplies =
                storageSavings.Enabled &&
                SizeEstimateService.IsHevcCodec(videoCodec) &&
                !hasCustomTarget &&
                !hasCustomProfile &&
                !hasManualTarget &&
                !profileText.Equals(
                    "No Compression",
                    StringComparison.OrdinalIgnoreCase);
            bool useStorageQualityTarget =
                storageSavingsApplies && storageSavings.UsesQualityTarget;
            if (useStorageQualityTarget)
                estimateQuality = storageSavings.QualityValue;

            if (policyIntent != null)
            {
                targetMb = policyEncodingPreset is { AutoTargetSize: false, ManualTargetMb: > 0 }
                    ? policyEncodingPreset.ManualTargetMb
                    : policyIntent.ProjectedOutputBytes is > 0
                        ? policyIntent.ProjectedOutputBytes.Value / (1024d * 1024d)
                        : null;
            }
            else if (hasCustomTarget)
            {
                targetMb = meta!.CustomTargetMb;
            }
            else if (profileText.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
            {
                // Try to keep roughly the same bitrate (with a small safety bump)
                if (isDvdEncode)
                {
                    targetMb = dvdOptions!.Candidate.CombinedSizeBytes /
                               (1024d * 1024d);
                }
                else
                {
                    int? srcKbps = ProbeSourceVideoBitrateKbps(file);
                    if (srcKbps.HasValue && durationSec > 0)
                    {
                        // bits = kbps * 1000 * seconds; MB ≈ bits / 8 / 1024 / 1024
                        //  => MB ≈ (kbps * seconds) / 8192
                        targetMb = ((srcKbps.Value * 1.15) * durationSec) / 8192.0;
                    }
                }
            }
            else
            {
                // Manual override from UI?
                if (hasManualTarget)
                {
                    targetMb = manualTargetMb;
                }
                else if (useStorageQualityTarget)
                {
                    // Quality-target storage mode must reach the encoder as CQ/CRF.
                    // The queue estimate is a projection only and must not be turned
                    // back into a fixed target-size command.
                    targetMb = null;
                }
                else if (_estimatedSizeMap.TryGetValue(file, out var est) && est > 0)
                {
                    targetMb = est;
                }
                else
                {
                    // Fallback to the metadata-aware estimator. If duration remains
                    // unavailable, leave targetMb unset so EncodingService safely uses
                    // quality-based encoding instead of inventing a fixed percentage.
                    double fallbackEstimate = isDvdEncode
                        ? EstimateDvdEncodeTargetMb(
                            meta!,
                            profileText,
                            estimateTargetEncoder,
                            estimateQuality,
                            estimateTargetHeight)
                        : _sizeEstimateService.EstimateAutoTargetMbSmart(
                            file,
                            profileText,
                            estimateTargetEncoder,
                            estimateQuality,
                            estimateTargetHeight,
                            encoderSnapshot.AudioChannels,
                            storageSavingsApplies ? storageSavings : null);
                    if (fallbackEstimate > 0)
                        targetMb = fallbackEstimate;
                }
            }

            _runningEncodeJobs[row] = isDvdEncode
                ? dvdOptions!.OutputPath
                : file;
            UpdateTrayStatus();
            string attemptedOutputPath = string.Empty;
            string stagedOutputPath = string.Empty;
            OutputContainerDecision? appliedContainerDecision = null;
            EncodingDiagnosticSummary? diagnosticSummary = null;
            DateTime? finalizationStartedUtc = null;
            bool diagnosticStarted = false;
            try
            {
                // ==== CALL THE SERVICE ====
                string formatChoice = encoderSnapshot.FormatText;
                var scaleMode = policyIntent == null
                    ? UiGet(() => GetSelectedScaleMode(), ScaleMode.None)
                    : PolicyScaleMode(policyIntent);

                string encoderPreset =
                    encoderSnapshot.Validated.Preset;
                bool tenBit = encoderSnapshot.Validated.TenBit;
                int? audioChannels = encoderSnapshot.AudioChannels;
                bool concurrentEncoderSessions =
                    encoderSnapshot.Validated.ConcurrentEncoderSessions;
                string outputFolder = isDvdEncode
                    ? Path.GetDirectoryName(dvdOptions!.OutputPath) ?? string.Empty
                    : UiGet(() => cmbEncodeOutput.Text, string.Empty);
                string suffix = BuildOutputSuffix(formatChoice);

                EncodingInputSource inputSource;
                if (isDvdEncode)
                {
                    UiInvoke(() => SetEncodeRowState(
                        row,
                        "Preparing DVD segments",
                        "0%",
                        "--:--:--",
                        "Opening the selected DVD title as one continuous program stream."));
                    var inputFactory = new DvdEncodingInputFactory();
                    inputSource = inputFactory.Create(dvdOptions!);
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
                    statisticsSourceHeight = mediaInfo.Height;
                    inputSource = EncodingInputSource.FromFile(
                        file,
                        mediaInfo.AudioBitrateKbps is > 0
                            ? mediaInfo.AudioBitrateKbps.Value
                            : meta.EstimatedPlannedAudioBitrateKbps is > 0
                                ? meta.EstimatedPlannedAudioBitrateKbps
                                : null,
                        mediaInfo.AudioStreamCount,
                        (mediaInfo.SubtitleBitrateKbps ?? 0) +
                        (mediaInfo.SubtitleBitrateKbps is > 0
                            ? 0
                            : mediaInfo.SubtitleStreamCount * 8d));
                }

                string sourceResolution = !string.IsNullOrWhiteSpace(meta.Resolution)
                    ? meta.Resolution
                    : statisticsSourceHeight is > 0 ? $"{statisticsSourceHeight}p" : "Unknown";
                int? diagnosticOutputHeight = RuntimeOutputHeight(statisticsSourceHeight, scaleMode);
                _encodingDiagnosticsService.Start(new EncodingDiagnosticJob(
                    meta.StatisticsOperationId, displayName, encoderText,
                    encoderSnapshot.Validated.Resolved.Selection.EncoderId, videoCodec, encoderPreset,
                    sourceResolution, diagnosticOutputHeight is > 0 ? $"{diagnosticOutputHeight}p" : sourceResolution,
                    tenBit ? 10 : 8, durationSec > 0 ? durationSec : null, logicalSourcePath), jobStartUtc);
                diagnosticStarted = true;

                // Per-job ffmpeg output callback
                Action<string> jobCallback = line =>
                {
                    jobLog.AppendLine(line);
                    _encodingDiagnosticsService.UpdateProgress(meta.StatisticsOperationId, line, durationSec > 0 ? durationSec : null);
                    HandleFfmpegProgressLineForRow(row, jobLog, durationSec, line);
                };

                ResolvedVideoEncoder selectedEncoder =
                    encoderSnapshot.Validated.Resolved;
                var encodeRequest = new EncodingRequest
                {
                    Input = inputSource,
                    OutputFolder = outputFolder,
                    Suffix = suffix,
                    Encoder = selectedEncoder.Selection,
                    UseGpu = useGpu,
                    TargetMb = targetMb,
                    ScaleMode = scaleMode,
                    Restoration = _config.VideoRestoration.Clone(),
                    EncoderPreset = encoderPreset,
                    QualityValue =
                        estimateQuality,
                    TenBit = tenBit,
                    AudioChannels = audioChannels,
                    ProgressCallback = jobCallback,
                    AiProgressCallback = progress => ApplyAiIntermediateProgress(row, progress),
                    ConcurrentEncoderSessions =
                        concurrentEncoderSessions,
                    CancellationToken = cancellationToken,
                    OutputPathCallback =
                        path => attemptedOutputPath = path,
                    StagingPathCallback =
                        path => stagedOutputPath = path,
                    FinalizationStatusCallback = status =>
                    {
                        finalizationStartedUtc ??= DateTime.UtcNow;
                        jobLog.AppendLine($"[MediaFlux] {status}.");
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
                            }
                        });
                    },
                    OutputContainer = PolicyOutputContainer(policyIntent),
                    ContainerCompatibilityConfirmed = _mp4CompatibilityConfirmedForRun,
                    ContainerDecisionCallback = decision => appliedContainerDecision = decision
                };

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

                var result = await _encodingService.EncodeWithResultAsync(
                    encodeRequest);

                if (!result.Success || !result.FinalizationSucceeded)
                {
                    throw new InvalidOperationException(
                        "Encoding did not complete validated output finalization.");
                }

                if (!isDvdEncode)
                {
                    jobLog.AppendLine(
                        $"[MediaFlux] FFmpeg arguments: {result.DiagnosticArguments}");
                }
                jobLog.AppendLine(
                    $"[MediaFlux] Validated and finalized: {result.ValidationSummary}");

                bool deleteSource = UiGet(() => chkDeleteSource.Checked, false);
                SourceDeletionResult sourceDeletion =
                    SourceDeletionService.DeleteAfterFinalization(
                        file,
                        inputSource,
                        deleteSource,
                        result);
                jobLog.AppendLine($"[MediaFlux] {sourceDeletion.Message}");

                // On success, mark 100% and clear ETA
                System.Threading.Interlocked.Increment(ref _encodeSucceededCount);
                Ui(() =>
                {
                    if (row.DataGridView != dgvEncodeQueue)
                        return;

                    SetEncodeRowState(
                        row,
                        "Done",
                        "100%",
                        "00:00:00",
                        $"Output validated and finalized. {sourceDeletion.Message}");
                });

                DateTime jobEndUtc = DateTime.UtcNow;
                diagnosticSummary = _encodingDiagnosticsService.Complete(
                    meta.StatisticsOperationId,
                    finalizationStartedUtc.HasValue ? Math.Max(0, (jobEndUtc - finalizationStartedUtc.Value).TotalSeconds) : 0);
                meta!.StatisticsProcessingSeconds +=
                    Math.Max(0, (jobEndUtc - jobStartUtc).TotalSeconds);
                long? outputSizeBytes =
                    result.FinalOutputSizeBytes ??
                    TryGetFileSizeBytes(result.OutputPath);

                // append success to history – never let this kill the job
                try
                {
                    lock (_historyLock)
                    {
                        _historyService.Append(new JobHistoryRecord
                        {
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
                            DiagnosticSummary = diagnosticSummary
                        });
                    }
                }
                catch (Exception logEx)
                {
                    Debug.WriteLine($"History append (success) failed: {logEx}");
                    // We ignore this; the encode itself succeeded.
                }

                RecordEncodingStatistics(
                    meta.StatisticsOperationId,
                    meta.StatisticsStartUtc,
                    jobEndUtc,
                    EncodingStatisticsOutcome.Success,
                    logicalSourcePath,
                    result.OutputPath,
                    videoCodec,
                    encoderText,
                    sourceSizeBytes,
                    outputSizeBytes,
                    durationSec > 0 ? durationSec : null,
                    meta.StatisticsProcessingSeconds,
                    $"Validated and finalized. {sourceDeletion.Message}",
                    encoderId: encoderSnapshot.Validated.Resolved.Selection.EncoderId,
                    encoderPreset: encoderSnapshot.Validated.Preset,
                    sourceResolutionTier: EncodingRuntimeEstimatorService.ResolutionTier(statisticsSourceHeight),
                    outputResolutionTier: EncodingRuntimeEstimatorService.ResolutionTier(RuntimeOutputHeight(statisticsSourceHeight, scaleMode)),
                    outputBitDepth: encoderSnapshot.Validated.TenBit ? 10 : 8,
                    scalingApplied: RuntimeOutputHeight(statisticsSourceHeight, scaleMode) is int outputHeight &&
                        statisticsSourceHeight is int sourceHeight && outputHeight != sourceHeight,
                    concurrentEncoderSessions: encoderSnapshot.Validated.ConcurrentEncoderSessions,
                    diagnosticSummary: diagnosticSummary);

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
                        RemoveRowAndCleanup(row);

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
                diagnosticSummary = _encodingDiagnosticsService.Complete(
                    meta.StatisticsOperationId,
                    finalizationStartedUtc.HasValue ? Math.Max(0, (attemptEndUtc - finalizationStartedUtc.Value).TotalSeconds) : 0);
                meta!.StatisticsProcessingSeconds +=
                    Math.Max(0, (attemptEndUtc - jobStartUtc).TotalSeconds);
                bool isCanceled = _cancelEncode || ex is OperationCanceledException;
                EncodeFinalizationException? finalizationFailure =
                    ex as EncodeFinalizationException;
                EncodeFinalizationResult? finalizationResult =
                    finalizationFailure?.Result ??
                    (ex as EncodeFinalizationCanceledException)?.Result;
                var notes = isCanceled
                    ? "Cancelled by user."
                    : ex.Message;

                bool cleanupEnabled = isCanceled
                    ? _config.DeleteCanceledEncodeOutputs
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
                    "Original source retained because validated finalization did not complete.";
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
                        _historyService.Append(new JobHistoryRecord
                        {
                            Type = isDvdEncode ? JobType.DvdEncode : JobType.Encode,
                            Status = isCanceled
                                ? JobStatus.Canceled
                                : JobStatus.Failed,
                            StartUtc = jobStartUtc,
                            EndUtc = DateTime.UtcNow,
                            SourcePath = logicalSourcePath,
                            OutputPath = attemptedOutputPath,
                            EncoderMode = encoderText,
                            TargetMb = targetMb,
                            DurationSec = durationSec,
                            Log = jobLog.ToString(),
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
                            OutputSizeBytes = TryGetFileSizeBytes(incompleteOutputPath),
                            WasRecommendedDvdTitle = isDvdEncode
                                ? dvdOptions!.Candidate.IsLikelyMainFeature
                                : null,
                            ErrorSummary = notes,
                            FinalizationOutcome =
                                finalizationResult?.FailureKind.ToString() ??
                                (isCanceled ? "Canceled" : "FfmpegFailed"),
                            StagingPath = stagedOutputPath,
                            SourceDeletionResult = sourceRetention,
                            RequestedOutputContainer = PolicyOutputContainer(policyIntent).ToString(),
                            ResolvedOutputContainer = appliedContainerDecision?.Resolved.ToString(),
                            ContainerDecisionReason = appliedContainerDecision?.Reason,
                            DiagnosticSummary = diagnosticSummary
                        });
                    }
                }
                catch (Exception logEx)
                {
                    Debug.WriteLine($"History append (failure) failed: {logEx}");
                    // Don't let logging errors mask the *real* encode error.
                }

                var centralLogPath = ErrorLogService.Append(
                    Application.StartupPath,
                    isCanceled ? "Encode job cancelled" : "Encode job failed",
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
                    retryQueued = TryQueueFailedRowForAutoRetry(row);
                    if (!retryQueued)
                        System.Threading.Interlocked.Increment(ref _encodeFailedCount);
                }

                if (isCanceled || !retryQueued)
                {
                    EncodingStatisticsOutcome statisticsOutcome =
                        finalizationFailure?.Result.FailureKind switch
                        {
                            EncodeFinalizationFailureKind.Validation =>
                                EncodingStatisticsOutcome.ValidationFailed,
                            EncodeFinalizationFailureKind.Promotion =>
                                EncodingStatisticsOutcome.PromotionFailed,
                            EncodeFinalizationFailureKind.FinalVerification =>
                                EncodingStatisticsOutcome.FinalVerificationFailed,
                            _ => isCanceled
                                ? EncodingStatisticsOutcome.Cancelled
                                : EncodingStatisticsOutcome.Failed
                        };
                    RecordEncodingStatistics(
                        meta.StatisticsOperationId,
                        meta.StatisticsStartUtc,
                        DateTime.UtcNow,
                        statisticsOutcome,
                        logicalSourcePath,
                        attemptedOutputPath,
                        videoCodec,
                        encoderText,
                        sourceSizeBytes,
                        outputSizeBytes: null,
                        mediaDurationSeconds: durationSec > 0 ? durationSec : null,
                        processingSeconds: meta.StatisticsProcessingSeconds,
                        notes: historyNotes,
                        diagnosticSummary: diagnosticSummary);
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
                                    : finalizationFailure?.Result.FailureKind ==
                                      EncodeFinalizationFailureKind.Validation
                                        ? "Validation Failed"
                                        : finalizationFailure != null
                                            ? "Finalization Failed"
                                            : "Failed",
                            isCanceled ? "Canceled" : retryQueued ? "Retry Queued" : "Failed",
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
                        : finalizationFailure?.Result.FailureKind ==
                          EncodeFinalizationFailureKind.Validation
                            ? $"Output validation failed — original retained: {displayName}"
                            : finalizationFailure != null
                                ? $"Output finalization failed — original retained: {displayName}"
                                : $"Failed: {displayName}. Continuing queue.";
                    toolStripStatusLabel1.Text = $"Encode error logged: {centralLogPath}";
                });
                // leave the row so user can retry
            }
            finally
            {
                if (diagnosticStarted && diagnosticSummary == null)
                    _encodingDiagnosticsService.Cancel(meta.StatisticsOperationId);
                _runningEncodeJobs.TryRemove(row, out _);
                UpdateTrayStatus();
                if (ReferenceEquals(_activeJobLogSb, jobLog))
                    _activeJobLogSb = null; // stop log capture for this job
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
                _activeEncodeQueue.Add(row);
            }

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
