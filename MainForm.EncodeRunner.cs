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

            bool requestedAll = processAllOverride ?? (chkProcessAll?.Checked ?? true);
            var requestedRows = (requestedAll
                    ? dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                    : dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>())
                .Where(row => !row.IsNewRow)
                .ToList();

            if (!requestedAll && requestedRows.Count == 0)
            {
                ShowStatusInfo("Select one or more files to encode.");
                return;
            }

            if (!EnsureFfmpegToolsAvailable())
                return;
            if (!EnsureSelectedVideoEncoderAvailable())
                return;

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

            // Handle "start at scheduled time" if set
            if (_encodeScheduledUtc.HasValue)
            {
                var wait = _encodeScheduledUtc.Value - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    toolStripStatusLabel1.Text =
                        $"Waiting until {_encodeScheduledUtc.Value.ToLocalTime():g}…";

                    _encodeScheduleCts = new CancellationTokenSource();
                    try
                    {
                        await Task.Delay(wait, _encodeScheduleCts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        // user cancelled the scheduled start
                    }
                }

                _encodeScheduledUtc = null;
                cancelScheduledStartToolStripMenuItem.Enabled = false;

                // after waiting, switch back to encoding status
                SetStatusEncoding(true);
            }

            var queueStartedUtc = DateTime.UtcNow;

            // Snapshot the requested rows before a possible scheduled wait. An explicit
            // context-menu choice must not be changed by the persisted checkbox setting.
            var requestedRowSet = requestedRows.ToHashSet();

            int maxParallel = GetMaxConcurrentEncodes(); // Automatic NVENC parallelism, otherwise 1.

            ReapplyCurrentEncodeQueueSort();

            // Gather rows to process in the same order the user sees in the grid.
            var rowsToProcess = GetEncodeRowsInVisualOrder()
                .Where(r =>
                {
                    bool duplicateExcluded = r.Tag is RowMeta meta && meta.ExcludedFromEncodeAsDuplicate;
                    return (requestedAll || requestedRowSet.Contains(r)) && !duplicateExcluded;
                })
                .ToList();

            foreach (var row in rowsToProcess)
            {
                if (row == null || row.IsNewRow)
                    continue;

                EnsureRowMeta(row).AutoRetryScheduled = false;
            }

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

        private async void ScheduleEncode_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.Rows.Count == 0)
            {
                ShowStatusInfo("Add files before scheduling an encode.");
                return;
            }

            using var dlg = new ScheduleForm();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var runAtUtc = dlg.ScheduledUtc;
            var delay = runAtUtc - DateTime.UtcNow;
            if (delay <= TimeSpan.Zero) delay = TimeSpan.Zero;

            _encodeScheduleCts?.Cancel();
            _encodeScheduleCts = new CancellationTokenSource();
            var token = _encodeScheduleCts.Token;

            toolStripStatusLabel1.Text = $"Encode scheduled for {runAtUtc.ToLocalTime():g}";

            try
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested) return;

                // fire the same path your Start button uses
                btnStartEncode.PerformClick();
                toolStripStatusLabel1.Text = "Scheduled encode started.";
            }
            catch (TaskCanceledException)
            {
                toolStripStatusLabel1.Text = "Scheduled encode canceled.";
            }
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

            var meta = row.Tag as RowMeta;
            DvdImportOptions? dvdOptions = meta?.IsDvdEncode == true
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
            string encoderText = encoderSnapshot.DisplayText;
            string videoCodec =
                encoderSnapshot.Validated.Resolved.Selection.FfmpegCodec;
            bool useGpu = encoderSnapshot.Validated.UseGpu;

            // ==== TARGET SIZE (MB) ====
            double? targetMb = null;
            bool hasCustomTarget = meta?.CustomTargetMb.HasValue == true;
            bool hasCustomProfile = !string.IsNullOrWhiteSpace(meta?.CustomCompressionProfile);
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
            int? estimateTargetHeight = UiGet(GetEstimateTargetHeight, null);
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

            if (hasCustomTarget)
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

                // Never “compress” to something basically the same size as source
                double srcMb = isDvdEncode
                    ? dvdOptions!.Candidate.CombinedSizeBytes / (1024d * 1024d)
                    : GetMbOnDisk(file);
                if (srcMb > 0 && targetMb.HasValue && targetMb.Value >= srcMb * 0.98)
                {
                    // force at least some reduction
                    targetMb = Math.Max(srcMb * 0.90, srcMb - 10);
                }
            }

            _runningEncodeJobs[row] = isDvdEncode
                ? dvdOptions!.OutputPath
                : file;
            string attemptedOutputPath = string.Empty;
            try
            {
                // ==== CALL THE SERVICE ====
                string formatChoice = encoderSnapshot.FormatText;
                var scaleMode = UiGet(() => GetSelectedScaleMode(), ScaleMode.None);

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
                    inputSource = EncodingInputSource.FromFile(
                        file,
                        mediaInfo.AudioBitrateKbps is > 0
                            ? mediaInfo.AudioBitrateKbps.Value
                            : meta?.EstimatedPlannedAudioBitrateKbps is > 0
                                ? meta.EstimatedPlannedAudioBitrateKbps
                                : null,
                        mediaInfo.AudioStreamCount,
                        (mediaInfo.SubtitleBitrateKbps ?? 0) +
                        (mediaInfo.SubtitleBitrateKbps is > 0
                            ? 0
                            : mediaInfo.SubtitleStreamCount * 8d));
                }

                // Per-job ffmpeg output callback
                Action<string> jobCallback = line =>
                {
                    jobLog.AppendLine(line);
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
                    EncoderPreset = encoderPreset,
                    QualityValue =
                        estimateQuality,
                    TenBit = tenBit,
                    AudioChannels = audioChannels,
                    ProgressCallback = jobCallback,
                    ConcurrentEncoderSessions =
                        concurrentEncoderSessions,
                    CancellationToken = cancellationToken,
                    OutputPathCallback =
                        path => attemptedOutputPath = path
                };

                if (!string.IsNullOrWhiteSpace(meta?.EstimateDiagnostic))
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

                if (!result.Success)
                    throw new InvalidOperationException("Encoding returned failure.");

                // On success, mark 100% and clear ETA
                System.Threading.Interlocked.Increment(ref _encodeSucceededCount);
                Ui(() =>
                {
                    if (row.DataGridView != dgvEncodeQueue)
                        return;

                    SetEncodeRowState(row, "Done", "100%", "00:00:00", "Encoding completed successfully.");
                });

                // append success to history – never let this kill the job
                try
                {
                    long? outputSizeBytes = TryGetFileSizeBytes(result.OutputPath);

                    lock (_historyLock)
                    {
                        _historyService.Append(new JobHistoryRecord
                        {
                            Type = isDvdEncode ? JobType.DvdEncode : JobType.Encode,
                            Status = JobStatus.Success,
                            StartUtc = jobStartUtc,
                            EndUtc = DateTime.UtcNow,
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
                                  $"Recommended={dvdOptions.Candidate.IsLikelyMainFeature}; Source deletion disabled"
                                : $"Codec={videoCodec}",
                            DvdTitleSet = isDvdEncode
                                ? dvdOptions!.Candidate.TitleSetId
                                : null,
                            DvdSegmentCount = isDvdEncode
                                ? dvdOptions!.Candidate.Segments.Count
                                : null,
                            DvdOutputMode = isDvdEncode
                                ? DvdOutputMode.EncodeUsingCurrentSettings.ToString()
                                : null,
                            SourceSizeBytes = isDvdEncode
                                ? dvdOptions!.Candidate.CombinedSizeBytes
                                : null,
                            OutputSizeBytes = isDvdEncode
                                ? outputSizeBytes
                                : null,
                            WasRecommendedDvdTitle = isDvdEncode
                                ? dvdOptions!.Candidate.IsLikelyMainFeature
                                : null
                        });
                    }
                }
                catch (Exception logEx)
                {
                    Debug.WriteLine($"History append (success) failed: {logEx}");
                    // We ignore this; the encode itself succeeded.
                }

                try
                {
                    bool deleteSource = UiGet(() => chkDeleteSource.Checked, false);
                    if (inputSource.ShouldDeleteSource(deleteSource))
                        TryDelete(file);
                }
                catch (Exception delEx)
                {
                    Debug.WriteLine($"Source delete failed for {file}: {delEx}");
                    // Worst case: user has to delete manually.
                }

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
                bool isCanceled = _cancelEncode || ex is OperationCanceledException;
                var notes = isCanceled
                    ? "Cancelled by user."
                    : ex.Message;

                bool cleanupEnabled = isCanceled
                    ? _config.DeleteCanceledEncodeOutputs
                    : _config.DeleteFailedEncodeOutputs;
                string cleanupResult = await CleanupIncompleteEncodeOutputAsync(
                    logicalSourcePath,
                    attemptedOutputPath,
                    cleanupEnabled,
                    isCanceled ? "canceled" : "failed");
                string historyNotes = $"{notes} Incomplete output cleanup: {cleanupResult}";
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
                            SourceSizeBytes = isDvdEncode
                                ? dvdOptions!.Candidate.CombinedSizeBytes
                                : null,
                            OutputSizeBytes = isDvdEncode
                                ? TryGetFileSizeBytes(attemptedOutputPath)
                                : null,
                            WasRecommendedDvdTitle = isDvdEncode
                                ? dvdOptions!.Candidate.IsLikelyMainFeature
                                : null,
                            ErrorSummary = isDvdEncode ? notes : null
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
                    $"Output      : {attemptedOutputPath}{Environment.NewLine}" +
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

                Ui(() =>
                {
                    if (row.DataGridView == dgvEncodeQueue)
                    {
                        SetEncodeRowState(
                            row,
                            isCanceled ? "Canceled" : retryQueued ? "Retry Queued" : "Failed",
                            isCanceled ? "Canceled" : retryQueued ? "Retry Queued" : "Failed",
                            "",
                            (isCanceled
                                ? "Canceled by user."
                                : retryQueued
                                    ? "Failed once; queued for automatic retry after the current queue finishes."
                                    : ex.Message) + $" Incomplete output cleanup: {cleanupResult}");
                        row.Cells["colProgress"].ToolTipText = $"{ex.Message}{Environment.NewLine}Incomplete output cleanup: {cleanupResult}";
                    }

                    lblEncodeStatus.Text = isCanceled
                        ? $"Canceled: {displayName}"
                        : retryQueued
                            ? $"Retry queued: {displayName}. Continuing queue."
                        : $"Failed: {displayName}. Continuing queue.";
                    toolStripStatusLabel1.Text = $"Encode error logged: {centralLogPath}";
                });
                // leave the row so user can retry
            }
            finally
            {
                _runningEncodeJobs.TryRemove(row, out _);
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

        private static async Task<string> CleanupIncompleteEncodeOutputAsync(
            string sourcePath,
            string outputPath,
            bool cleanupEnabled,
            string outcome)
        {
            if (!cleanupEnabled)
                return "disabled in Settings.";

            if (string.IsNullOrWhiteSpace(outputPath))
                return "no output path was allocated.";

            string fullSourcePath;
            string fullOutputPath;
            try
            {
                fullSourcePath = Path.GetFullPath(sourcePath);
                fullOutputPath = Path.GetFullPath(outputPath);
            }
            catch (Exception ex)
            {
                return $"not deleted because the attempt path was invalid ({ex.Message}).";
            }

            if (string.Equals(fullSourcePath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
                return "not deleted because the output path matched the source path.";

            const int attempts = 3;
            Exception? lastError = null;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (!File.Exists(fullOutputPath))
                        return "no incomplete output file was present.";

                    File.Delete(fullOutputPath);
                    if (!File.Exists(fullOutputPath))
                        return $"deleted the {outcome} attempt output.";

                    lastError = new IOException("The file still exists after the delete request.");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (attempt < attempts)
                    await Task.Delay(250 * attempt);
            }

            return $"could not delete the {outcome} attempt output after {attempts} attempts ({lastError?.Message ?? "unknown error"}).";
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
