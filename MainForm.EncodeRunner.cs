using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static MediaFlux.Services.EncodingService;

namespace MediaFlux
{
    public partial class MainForm : Form
    {
        private async void btnStartEncode_Click(object? sender, EventArgs e)
        {
            // prevent re-entry
            if (_encodingActive)
                return;

            if (!ValidateOutputFolderAgainstWatchFolder(cmbEncodeOutput.Text, showMessage: true))
                return;

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

            // Determine whether to process all rows or only the selected ones
            bool processAll = (chkProcessAll?.Checked ?? true);

            HashSet<string> selectedPaths;
            if (processAll)
            {
                // empty set → filter below treats this as "include all rows"
                selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                selectedPaths = dgvEncodeQueue.SelectedRows
                    .Cast<DataGridViewRow>()
                    .Select(r => r.Tag is RowMeta rm ? rm.Path : (r.Tag as string))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            int maxParallel = GetMaxConcurrentEncodes(); // Automatic NVENC parallelism, otherwise 1.

            ReapplyCurrentEncodeQueueSort();

            // Gather rows to process in the same order the user sees in the grid.
            var rowsToProcess = GetEncodeRowsInVisualOrder()
                .Where(r =>
                {
                    var p = r.Tag is RowMeta rm ? rm.Path : (r.Tag as string);
                    // If selectedPaths is empty → we include all rows
                    bool selected = selectedPaths.Count == 0 || (p != null && selectedPaths.Contains(p));
                    bool duplicateExcluded = r.Tag is RowMeta meta && meta.ExcludedFromEncodeAsDuplicate;
                    return selected && !duplicateExcluded;
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

            // Watched files can be queued and started before the background
            // estimate pass attaches metadata to the row. Resolve duration here
            // as a final pre-encode guarantee so percentage, ETA, elapsed media
            // time, and the main progress bar update exactly like manual imports.
            if (durationSec <= 0)
            {
                UiInvoke(() => SetEncodeRowState(
                    row,
                    "Reading metadata",
                    "0%",
                    "--:--:--",
                    "Reading media duration before encoding."));

                try
                {
                    durationSec = await Task.Run(
                        () => ProbeDurationSeconds(file),
                        cancellationToken);
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
                    $"Encoding: {Path.GetFileName(file)} ({_encodeProcessedCount}/{totalNow}) – Queued: {remaining}";

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

            // Encoder mode (GPU/CPU)
            string encoderText = UiGet(() => comboEncoderMode.SelectedItem?.ToString() ?? string.Empty, string.Empty);
            bool useGpu = encoderText.StartsWith("GPU", StringComparison.OrdinalIgnoreCase);

            // ==== TARGET SIZE (MB) ====
            double? targetMb = null;
            var meta = row.Tag as RowMeta;
            bool hasCustomTarget = meta?.CustomTargetMb.HasValue == true;
            bool hasCustomProfile = !string.IsNullOrWhiteSpace(meta?.CustomCompressionProfile);

            string profileText = hasCustomProfile
                ? meta!.CustomCompressionProfile!
                : UiGet(
                    () => comboCompressionProfile!.SelectedItem?.ToString()
                          ?? comboCompressionProfile.Text
                          ?? string.Empty,
                    string.Empty);

            if (hasCustomTarget)
            {
                targetMb = meta!.CustomTargetMb;
            }
            else if (profileText.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
            {
                // Try to keep roughly the same bitrate (with a small safety bump)
                int? srcKbps = ProbeSourceVideoBitrateKbps(file);
                if (srcKbps.HasValue && durationSec > 0)
                {
                    // bits = kbps * 1000 * seconds; MB ≈ bits / 8 / 1024 / 1024
                    //  => MB ≈ (kbps * seconds) / 8192
                    targetMb = ((srcKbps.Value * 1.15) * durationSec) / 8192.0;
                }
            }
            else
            {
                // Manual override from UI?
                var targetText = hasCustomProfile
                    ? string.Empty
                    : UiGet(() => txtTargetSize.Text, string.Empty);
                if (!hasCustomProfile &&
                    double.TryParse(targetText, out var manualMb) &&
                    manualMb > 0)
                {
                    targetMb = manualMb;
                }
                else if (_estimatedSizeMap.TryGetValue(file, out var est) && est > 0)
                {
                    targetMb = est;
                }
                else
                {
                    // Fallback to auto estimator
                    targetMb = EstimateAutoTargetMbSmart(file, profileText);
                }

                // Never “compress” to something basically the same size as source
                var srcMb = GetMbOnDisk(file);
                if (srcMb > 0 && targetMb.HasValue && targetMb.Value >= srcMb * 0.98)
                {
                    // force at least some reduction
                    targetMb = Math.Max(srcMb * 0.90, srcMb - 10);
                }
            }

            _runningEncodeJobs[row] = file;
            string attemptedOutputPath = string.Empty;
            try
            {
                // ==== CALL THE SERVICE ====
                string formatChoice = UiGet(
                    () => comboVideoFormat.SelectedItem?.ToString() ?? "H.265 / HEVC (x265)",
                    "H.265 / HEVC (x265)");
                string videoCodec = ResolveVideoCodec(encoderText, formatChoice);
                var scaleMode = UiGet(() => GetSelectedScaleMode(), ScaleMode.None);

                // Advanced options from UI
                string nvencPreset = UiGet(() => GetSelectedNvencPreset(), string.Empty);
                bool tenBit = UiGet(() => GetTenBitRequested(), false);
                int? audioChannels = UiGet(() => GetSelectedAudioChannels(), null);
                bool concurrentNvenc = UiGet(
                    () => IsNvencSelected(encoderText) && GetMaxConcurrentEncodes() > 1,
                    false);
                string outputFolder = UiGet(() => cmbEncodeOutput.Text, string.Empty);
                string suffix = BuildOutputSuffix(formatChoice);

                // Per-job ffmpeg output callback
                Action<string> jobCallback = line =>
                {
                    jobLog.AppendLine(line);
                    HandleFfmpegProgressLineForRow(row, jobLog, durationSec, line);
                };

                var result = await _encodingService.EncodeWithResultAsync(
                    file,
                    outputFolder,
                    suffix,
                    useGpu,
                    targetMb,
                    videoCodec,
                    scaleMode,
                    nvencPreset,
                    tenBit,
                    audioChannels,
                    jobCallback,
                    concurrentNvenc,
                    cancellationToken: cancellationToken,
                    outputPathCallback: path => attemptedOutputPath = path
                );

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
                    lock (_historyLock)
                    {
                        _historyService.Append(new JobHistoryRecord
                        {
                            Type = JobType.Encode,
                            Status = JobStatus.Success,
                            StartUtc = jobStartUtc,
                            EndUtc = DateTime.UtcNow,
                            SourcePath = file,
                            OutputPath = result.OutputPath,
                            EncoderMode = encoderText,
                            TargetMb = targetMb,
                            DurationSec = durationSec,
                            Log = jobLog.ToString(),
                            Notes = $"Codec={videoCodec}"
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
                    if (deleteSource)
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
                        RememberCompletedEncodePaths(file, result.OutputPath);
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
                    file,
                    attemptedOutputPath,
                    cleanupEnabled,
                    isCanceled ? "canceled" : "failed");
                string historyNotes = $"{notes} Incomplete output cleanup: {cleanupResult}";

                try
                {
                    lock (_historyLock)
                    {
                        _historyService.Append(new JobHistoryRecord
                        {
                            Type = JobType.Encode,
                            Status = isCanceled
                                ? JobStatus.Canceled
                                : JobStatus.Failed,
                            StartUtc = jobStartUtc,
                            EndUtc = DateTime.UtcNow,
                            SourcePath = file,
                            OutputPath = attemptedOutputPath,
                            EncoderMode = encoderText,
                            TargetMb = targetMb,
                            DurationSec = durationSec,
                            Log = jobLog.ToString(),
                            Notes = historyNotes
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
                    file,
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
                        ? $"Canceled: {Path.GetFileName(file)}"
                        : retryQueued
                            ? $"Retry queued: {Path.GetFileName(file)}. Continuing queue."
                        : $"Failed: {Path.GetFileName(file)}. Continuing queue.";
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
