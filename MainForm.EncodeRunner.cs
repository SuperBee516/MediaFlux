using Encode.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Encode.Services.EncodingService;

namespace Encode
{
    public partial class MainForm : Form
    {
        private async void btnStartEncode_Click(object? sender, EventArgs e)
        {
            // prevent re-entry
            if (_encodingActive)
                return;

            _encodingActive = true;
            SetStatusEncoding(true);

            btnStartEncode.Enabled = false;
            btnStopEncode.Enabled = true;
            _cancelEncode = false;

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

            int maxParallel = GetMaxConcurrentEncodes(); // 1 or 2 depending on GPU/checkbox

            // Gather rows to process, preserving visual order
            var rowsToProcess = dgvEncodeQueue.Rows
                .Cast<DataGridViewRow>()
                .Where(r =>
                {
                    var p = r.Tag is RowMeta rm ? rm.Path : (r.Tag as string);
                    // If selectedPaths is empty → we include all rows
                    return selectedPaths.Count == 0 || (p != null && selectedPaths.Contains(p));
                })
                .OrderBy(r => r.Index)
                .ToList();

            // Expose this list so the context menu can append rows while encoding
            _activeEncodeQueue = rowsToProcess;
            _encodeProcessedCount = 0;

            try
            {
                if (rowsToProcess.Count == 0)
                {
                    lblEncodeStatus.Text = "Nothing to encode.";
                    ResetEncodeMetrics();
                    return;
                }

                lblEncodeStatus.Text = "Encoding…";

                await _encodeQueueRunner.RunAsync(
                    rowsToProcess,
                    row => EncodeSingleRow(row),
                    maxParallel,
                    () => _encodeQueuePaused,
                    () => _cancelEncode);

                lblEncodeStatus.Text = _cancelEncode
                    ? "Encoding stopped."
                    : "All done!";
                ResetEncodeMetrics();
            }
            finally
            {
                _encodingActive = false;
                _activeEncodeQueue = null;
                btnStartEncode.Enabled = true;
                btnStopEncode.Enabled = false;
                _cancelEncode = false;
                SetStatusEncoding(false);
            }
        }

        

        private void btnStopEncode_Click(object? sender, EventArgs e)
        {
            // Signal the encode loop / workers to stop scheduling new work
            _cancelEncode = true;

            // If the queue was paused, un-pause it so the loop can actually exit
            _encodeQueuePaused = false;
            if (btnPauseQueue != null)
                btnPauseQueue.Text = "Pause Queue";

            // Brutal but effective: kill all ffmpeg processes that are currently running.
            // With the worker pool we can have multiple ffmpeg instances; this guarantees
            // they’re all torn down.
            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("ffmpeg"))
                {
                    try
                    {
                        if (!proc.HasExited)
                            proc.Kill(true);
                    }
                    catch
                    {
                        // Ignore failures (permissions, race conditions, etc.)
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore – worst case, some ffmpeg processes survive and exit on their own.
            }

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
                MessageBox.Show("Nothing to schedule. Add files first.", "Schedule",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        private async Task EncodeSingleRow(DataGridViewRow row)
        {
            if (_cancelEncode)
                return;

            if (row == null || row.IsNewRow || row.DataGridView == null)
                return;

            // Resolve file path & duration from the row
            if (!TryGetRowPathAndDuration(row, out var file, out var durationSec) ||
                string.IsNullOrWhiteSpace(file))
                return;

            // Which encode number is this?
            _encodeProcessedCount++;
            int totalNow = _activeEncodeQueue?.Count ?? dgvEncodeQueue.Rows.Count;
            int remaining = Math.Max(0, totalNow - _encodeProcessedCount);

            // Basic status + metrics wiring
            Ui(() =>
            {
                lblEncodeStatus.Text =
                    $"Encoding: {Path.GetFileName(file)} ({_encodeProcessedCount}/{totalNow}) – Queued: {remaining}";

                _currentEncodeDuration = TimeSpan.Zero;
                _currentEncodeTotalDuration = TimeSpan.FromSeconds(durationSec > 0 ? durationSec : 0);
                bool firstActive = BeginEncodeMetricsForRow(row);
                if (firstActive)
                    StartJobTimer();

                _activeEncodeRow = row;
                row.Cells["colProgress"].Value = "0%";
                row.Cells["colETA"].Value = "--:--:--";
            });

            // Start per-job log capture
            _activeJobLogSb = new StringBuilder();
            var jobLog = _activeJobLogSb;
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

            try
            {
                // ==== CALL THE SERVICE ====
                string formatChoice = UiGet(
                    () => comboVideoFormat.SelectedItem?.ToString() ?? "H.265 / HEVC (x265)",
                    "H.265 / HEVC (x265)");
                string videoCodec = ResolveVideoCodec(useGpu, formatChoice);
                var scaleMode = UiGet(() => GetSelectedScaleMode(), ScaleMode.None);

                // Advanced options from UI
                string nvencPreset = UiGet(() => GetSelectedNvencPreset(), string.Empty);
                bool tenBit = UiGet(() => GetTenBitRequested(), false);
                int? audioChannels = UiGet(() => GetSelectedAudioChannels(), null);
                string outputFolder = UiGet(() => cmbEncodeOutput.Text, string.Empty);
                string suffix = BuildOutputSuffix(formatChoice);

                // Per-job ffmpeg output callback
                Action<string> jobCallback = line =>
                {
                    jobLog.AppendLine(line);
                    HandleFfmpegProgressLineForRow(row, jobLog, durationSec, line);
                };

                bool ok = await _encodingService.EncodeAsync(
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
                    jobCallback
                );

                if (!ok)
                    throw new InvalidOperationException("Encoding returned failure.");

                // On success, mark 100% and clear ETA
                Ui(() =>
                {
                    row.Cells["colProgress"].Value = "100%";
                    row.Cells["colETA"].Value = "00:00:00";
                });

                // Guess the output path the same way the service names it
                var guessedOut = Path.Combine(
                    outputFolder,
                    Path.GetFileNameWithoutExtension(file) + suffix + Path.GetExtension(file)
                );

                // append success to history – never let this kill the job
                try
                {
                    lock (_historyLock)
                    {
                        _historyService.Append(new Encode.Services.JobHistoryRecord
                        {
                            Type = Encode.Services.JobType.Encode,
                            Status = Encode.Services.JobStatus.Success,
                            StartUtc = jobStartUtc,
                            EndUtc = DateTime.UtcNow,
                            SourcePath = file,
                            OutputPath = guessedOut,
                            EncoderMode = encoderText,
                            TargetMb = targetMb,
                            DurationSec = durationSec,
                            Log = _activeJobLogSb?.ToString() ?? "",
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
                    Ui(() =>
                    {
                        RemoveRowAndCleanup(row);

                        // Re-scan the current input folder and merge any changes
                        RescanInputFolderAndMerge(recomputeEstimates: false);

                        // Recompute estimates for whatever is now in the grid
                        SafeRefreshEstimates();
                        UpdateSizeTotals();
                        UpdateSelectionSizeTotals();
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
                // We only have Success / Failed in JobStatus.
                // For user-initiated Stop, we treat it as a "failed" job in history
                // but annotate the Notes as "Cancelled by user." and skip the popup.

                var notes = _cancelEncode
                    ? "Cancelled by user."
                    : ex.Message;

                try
                {
                    lock (_historyLock)
                    {
                        _historyService.Append(new Encode.Services.JobHistoryRecord
                        {
                            Type = Encode.Services.JobType.Encode,
                            Status = Encode.Services.JobStatus.Failed,   // still no Cancelled enum
                            StartUtc = jobStartUtc,
                            EndUtc = DateTime.UtcNow,
                            SourcePath = file,
                            OutputPath = "",
                            EncoderMode = encoderText,
                            TargetMb = targetMb,
                            DurationSec = durationSec,
                            Log = _activeJobLogSb?.ToString() ?? "",
                            Notes = notes
                        });
                    }
                }
                catch (Exception logEx)
                {
                    Debug.WriteLine($"History append (failure) failed: {logEx}");
                    // Don't let logging errors mask the *real* encode error.
                }

                // Only bother the user if this wasn’t a deliberate cancel
                if (!_cancelEncode)
                {
                    MessageBox.Show(
                        $"Encoding failed for:\n{file}\n\nError: {ex.Message}",
                        "Encode Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
                // leave the row so user can retry
            }
            finally
            {
                _activeJobLogSb = null; // stop log capture for this job
                Ui(() =>
                {
                    _activeEncodeRow = null;
                    EndEncodeMetricsForRow(row);
                });
            }
        }

    }
}
