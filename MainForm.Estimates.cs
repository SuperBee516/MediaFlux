using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm : Form
    {
        private string GetVideoResolution(string path)
        {
            var (w, h) = ProbeResolutionPixels(path);
            long pix = (long)w * h;
            if (pix >= 3840L * 2160) return "4K";
            if (pix >= 1920L * 1080) return "1080p";
            if (pix >= 1280L * 720) return "720p";
            if (pix >= 720L * 480) return "480p";
            return "Unknown";
        }

        private (int w, int h) ProbeResolutionPixels(string file)
        {
            return _mediaInfoService.GetResolutionPixels(file);
        }

        private double ProbeFps(string file)
        {
            return _mediaInfoService.GetFps(file);
        }

        // Core: estimate a size range (min/max) using bpp bands adjusted by quality
        private (int minKiB, int maxKiB, int midKiB)
    EstimateSizeRangeKiB(string path, string codec, int quality)
        {
            return _sizeEstimateService.EstimateSizeRangeKiB(path, codec, quality);
        }

        private void SetRowEstimateRange(DataGridViewRow row, int minKiB, int maxKiB)
        {
            if (maxKiB <= 0)
            {
                row.Cells["colEstimatedSize"].Value = "—";
                return;
            }
            double minMB = minKiB / 1024.0, maxMB = maxKiB / 1024.0;
            row.Cells["colEstimatedSize"].Value = $"≈ {minMB:0.0}–{maxMB:0.0} MB";
        }

        // Background Results UI

        private void StartEstimateUiPump()
        {
            if (_estUiTimer == null)
            {
                _estUiTimer = new System.Windows.Forms.Timer { Interval = 100 };
                _estUiTimer.Tick += EstUiTimer_Tick;
                _estUiTimer.Start();
            }

            if (_estSmartUiTimer == null)
            {
                _estSmartUiTimer = new System.Windows.Forms.Timer { Interval = 100 };
                _estSmartUiTimer.Tick += EstSmartUiTimer_Tick;
                _estSmartUiTimer.Start();
            }
        }

        private void EstUiTimer_Tick(object? sender, EventArgs e)
        {
            ApplyEstimateResultsBatch();
        }

        private void EstSmartUiTimer_Tick(object? sender, EventArgs e)
        {
            ApplySmartEstimateResultsBatch();
        }

        private void StopEstimateUiPump()
        {
            // Cancel workers and clear queues
            _estimateService.ResetAndCancel();

            if (_estUiTimer != null)
            {
                _estUiTimer.Stop();
                _estUiTimer.Tick -= EstUiTimer_Tick;
                _estUiTimer.Dispose();
                _estUiTimer = null;
            }

            if (_estSmartUiTimer != null)
            {
                _estSmartUiTimer.Stop();
                _estSmartUiTimer.Tick -= EstSmartUiTimer_Tick;
                _estSmartUiTimer.Dispose();
                _estSmartUiTimer = null;
            }
        }

        private void ApplyEstimateResultsBatch()
        {
            // Don’t touch the grid if the form is gone or we’re in auto-target mode
            if (IsDisposed || !IsHandleCreated || chkAutoTargetSize.Checked)
                return;

            const int MaxPerTick = 64;
            int applied = 0;

            dgvEncodeQueue.SuspendLayout();
            try
            {
                // Pull from the background service
                while (applied < MaxPerTick &&
                       _estimateService.TryDequeueRange(out var item))
                {
                    if (_rowsByPath.TryGetValue(item.Path, out var row) && row?.Cells != null)
                    {
                        var meta = row.Tag as RowMeta;
                        if (RowHasCustomSettings(meta))
                        {
                            ApplyCustomSettingsEstimate(row, meta!);
                            RestoreQueuedStateAfterEstimate(row);
                            applied++;
                            continue;
                        }

                        // Use the new property names
                        SetRowEstimateRange(row, item.MinKiB, item.MaxKiB);
                        RestoreQueuedStateAfterEstimate(row);
                        applied++;
                    }
                }
            }
            finally
            {
                dgvEncodeQueue.ResumeLayout();
                if (applied > 0)
                {
                    dgvEncodeQueue.Invalidate();
                    UpdateSizeTotals();
                }
                UpdateEstimateProgressStatus();
            }
        }

        private void ApplySmartEstimateResultsBatch()
        {
            if (IsDisposed || !IsHandleCreated) return;

            const int MaxPerTick = 64;
            int applied = 0;

            dgvEncodeQueue.SuspendLayout();
            try
            {
                while (applied < MaxPerTick && _estimateService.TryDequeueSmart(out var item))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Dequeued {item.Path}: srcMb={item.SourceMb}, estMb={item.EstimatedMb}, dur={item.DurationSec}, res={item.Resolution}");

                    if (_rowsByPath.TryGetValue(item.Path, out var row) && row?.Cells != null)
                    {
                        try
                        {
                            double srcMb = item.SourceMb > 0 ? item.SourceMb : 1.0;
                            double estMb = item.EstimatedMb;
                            bool hasEstimate = estMb > 0;

                            row.Cells["colEstimatedSize"].Value = hasEstimate
                                ? $"{FormatSize(estMb)}  {PercentReduction(srcMb, estMb)}"
                                : "Metadata unavailable";
                            row.Cells["colSize"].Value = FormatSize(srcMb);
                            row.Cells["colEstimatedSize"].Tag = hasEstimate
                                ? new Tuple<double, double>(srcMb, estMb)
                                : null;
                            row.Cells["colEstimatedSize"].ToolTipText = hasEstimate
                                ? "Calculated from source size, duration, resolution, and the selected Quality / File Size profile."
                                : "Duration metadata could not be determined. MediaFlux will not substitute a fixed target size.";
                            if (_queueSourceSizeMap.TryGetValue(item.Path, out var previousSrc))
                                _queueTotalSourceMb += srcMb - previousSrc;
                            else
                                _queueTotalSourceMb += srcMb;

                            _queueSourceSizeMap[item.Path] = srcMb;

                            if (hasEstimate)
                            {
                                if (_estimatedSizeMap.TryGetValue(item.Path, out var previousEst))
                                    _queueTotalEstimatedMb += estMb - previousEst;
                                else
                                    _queueTotalEstimatedMb += estMb;

                                _estimatedSizeMap[item.Path] = estMb;
                            }
                            else if (_estimatedSizeMap.Remove(item.Path, out var previousEst))
                            {
                                _queueTotalEstimatedMb -= previousEst;
                            }
                            _queueTotalsDirty = false;

                            // Update RowMeta on the UI thread so later consumers can avoid probing
                            string path = item.Path;
                            double durSec = item.DurationSec;
                            string res = item.Resolution ?? "";
                            string codec = item.VideoCodec ?? "";

                            if (row.Tag is RowMeta rm)
                            {
                                if (string.IsNullOrWhiteSpace(rm.Path))
                                    rm.Path = path;

                                if (durSec > 0)
                                    rm.DurationSec = durSec;

                                if (!string.IsNullOrEmpty(res))
                                    rm.Resolution = res;

                                if (srcMb > 0)
                                    rm.SrcMb = srcMb;

                                if (!string.IsNullOrWhiteSpace(codec))
                                    rm.VideoCodec = codec;
                            }
                            else
                            {
                                row.Tag = new RowMeta
                                {
                                    Path = path,
                                    DurationSec = durSec,
                                    Resolution = res,
                                    SrcMb = srcMb,
                                    VideoCodec = codec
                                };
                            }

                            TrackCodecFilterCount(path, codec);

                            var updatedMeta = row.Tag as RowMeta;
                            if (RowHasCustomSettings(updatedMeta))
                            {
                                ApplyCustomSettingsEstimate(row, updatedMeta!);
                                RestoreQueuedStateAfterEstimate(row);
                                applied++;
                            }
                            else
                            {
                                RestoreQueuedStateAfterEstimate(row);
                                applied++;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Smart Estimate UI Update Error for {item.Path}: {ex.Message}\n{ex.StackTrace}");
                            row.Cells["colEstimatedSize"].Value = "Error";
                        }
                    }
                }
            }
            finally
            {
                dgvEncodeQueue.ResumeLayout();
                if (applied > 0) dgvEncodeQueue.Invalidate();
                UpdateSizeTotals();
                UpdateEstimateProgressStatus();
            }
        }

        private void UpdateEstimateProgressStatus()
        {
            int pending = _estimateService.PendingEstimates;
            if (_lastEstimateQueuedCount > 0 && pending > 0)
            {
                int completed = Math.Max(0, _lastEstimateQueuedCount - pending);
                string text = $"Analyzing queue metadata... {completed:N0}/{_lastEstimateQueuedCount:N0}";
                toolStripStatusLabel1.Text = text;
                UpdateRelocatedEncodeStatus(text);
                SetQueueProgress(completed, _lastEstimateQueuedCount, visible: true);
                SetQueueWorkCancelVisible(true);
            }
            else if (_lastEstimateQueuedCount > 0 && pending <= 0)
            {
                string text = _estimatesDeferredForLargeQueue
                    ? $"Large queue mode: {dgvEncodeQueue.Rows.Count:N0} files loaded. Use Analyze Queue to estimate sizes."
                    : $"Queue analysis complete for {_lastEstimateQueuedCount:N0} file(s).";
                toolStripStatusLabel1.Text = text;
                UpdateRelocatedEncodeStatus(text);
                _lastEstimateQueuedCount = 0;
                SetQueueProgress(0, 0, visible: false);
                SetQueueWorkCancelVisible(false);
            }
        }

        // Queueuing Hooks
        private void AfterRowAdded(string path, DataGridViewRow row)
        {
            _rowsByPath[path] = row;
            QueueEstimateForPath(path);
        }

        private void QueueEstimateForPath(string path)
        {
            var (codec, _) = GetSelectedCodecInfo();
            int q = GetDefaultQualityForSelection();
            _estimateService.QueueRangeEstimate(path, codec, q);
        }

        private void SafeRefreshEstimates()
        {
            if (dgvEncodeQueue.Rows.Count == 0)
                return;

            RunEstimatePass();
        }

        private void RunEstimatePass()
        {
            RunEstimatePass(force: false);
        }

        private void RunEstimatePass(bool force)
        {
            _estimateService.ResetAndCancel();

            if (dgvEncodeQueue.Rows.Count == 0)
                return;

            _largeQueueModeActive = dgvEncodeQueue.Rows.Count >= GetLargeQueueThreshold();
            if (_largeQueueModeActive && !_config.AutoAnalyzeLargeQueues && !force)
            {
                _estimatesDeferredForLargeQueue = true;
                UpdateAnalyzeQueueButtonState();
                toolStripStatusLabel1.Text = $"Large queue mode: {dgvEncodeQueue.Rows.Count:N0} files loaded. Use Analyze Queue to estimate sizes.";
                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
                return;
            }

            bool autoRequested = chkAutoTargetSize.Checked;
            string profile = comboCompressionProfile.SelectedItem?.ToString() ?? "Medium";

            double manualTargetMb = 0;
            if (!autoRequested && double.TryParse(txtTargetSize.Text, out var m) && m > 0)
                manualTargetMb = m;

            // A blank/invalid manual target means there is no manual size to display.
            // Keep using the Quality / File Size profile for the estimate in that
            // state, matching the encode runner's existing behavior. Previously this
            // queued manual mode with a zero target, so every row was reported as
            // "Metadata unavailable" even when FFprobe metadata was valid.
            bool useProfileEstimate = autoRequested || manualTargetMb <= 0;

            int queued = 0;
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow) continue;

                var meta = row.Tag as RowMeta;
                string? path = meta?.Path ?? row.Tag as string;

                if (meta?.ExcludedFromEncodeAsDuplicate == true)
                {
                    row.Cells["colEstimatedSize"].Value = "";
                    SetEncodeRowState(
                        row,
                        "Excluded - exact duplicate",
                        "",
                        "",
                        "Exact duplicate soft-excluded from encoding. The source file was not changed.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    row.Cells["colEstimatedSize"].Value = "";
                    continue;
                }

                if (RowHasCustomSettings(meta))
                {
                    ApplyCustomSettingsEstimate(row, meta!);
                    ApplyEncodeRowVisualState(row);
                    continue;
                }

                // Keep the path → row map in sync for the UI pump
                _rowsByPath[path] = row;
                if (row.Cells["colStatus"].Value?.ToString() is not "Encoding" and not "Done" and not "Failed" and not "Canceled" and not "Retry Queued")
                    SetEncodeRowState(row, "Estimating", row.Cells["colProgress"].Value?.ToString(), row.Cells["colETA"].Value?.ToString(), "Estimating output size.");

                // Queue estimate work; UI pump will apply results
                QueueEstimate(path, useProfileEstimate, profile, manualTargetMb);
                queued++;
            }

            _lastEstimateQueuedCount = queued;
            _estimatesDeferredForLargeQueue = false;
            if (queued > 0)
            {
                SetQueueWorkCancelVisible(true);
                SetQueueProgress(0, queued, visible: true);
                toolStripStatusLabel1.Text = $"Analyzing queue metadata... 0/{queued:N0}";
                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
            }
            UpdateAnalyzeQueueButtonState();

            // Make sure timers are alive (constructor already calls this, but this is cheap)
            StartEstimateUiPump();
        }

        private void RestoreQueuedStateAfterEstimate(DataGridViewRow row)
        {
            string status = row.Cells["colStatus"].Value?.ToString() ?? "";
            if (status.Equals("Estimating", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(status))
            {
                SetEncodeRowState(row, "Queued", row.Cells["colProgress"].Value?.ToString(), row.Cells["colETA"].Value?.ToString(), "Ready to encode.");
            }
            else
            {
                ApplyEncodeRowVisualState(row);
            }
        }

        private void QueueEstimate(string path, bool auto, string profile, double manualTargetMb)
        {
            _estimateService.QueueSmartEstimate(path, auto, profile, manualTargetMb);
        }

        private double EstimateAutoTargetMbSmart(string path, string prof)
        {
            return _sizeEstimateService.EstimateAutoTargetMbSmart(path, prof);
        }

        private bool IsNoCompressionSelected()
        {
            var text = comboCompressionProfile?.SelectedItem?.ToString()
                       ?? comboCompressionProfile?.Text
                       ?? string.Empty;
            return text.Equals("No Compression", StringComparison.OrdinalIgnoreCase);
        }

        private void chkAutoTargetSize_CheckedChanged(object? sender, EventArgs e)
        {
            // Re-run the unified estimator (same as Refresh button)
            RunEstimatePass();
        }

        // totals & size formatting helpers

        private void MarkQueueTotalsDirty()
        {
            _queueTotalsDirty = true;
        }

        private void UpdateSizeTotals()
        {
            UpdateSizeTotals(force: false);
        }

        private void UpdateSizeTotals(bool force)
        {
            // Ensure we run on the UI thread
            if (InvokeRequired)
            {
                Ui(() => UpdateSizeTotals(force));
                return;
            }

            if (dgvEncodeQueue == null || dgvEncodeQueue.Rows.Count == 0)
            {
                _queueTotalSourceMb = 0;
                _queueTotalEstimatedMb = 0;
                _queueFileCount = 0;
                _queueSourceSizeMap.Clear();
                _estimatedSizeMap.Clear();
                _queueTotalsDirty = false;
                if (_summaryFileCountValue != null) _summaryFileCountValue.Text = "0";
                if (_summaryTotalCurrentValue != null) _summaryTotalCurrentValue.Text = "--";
                if (_summaryNewSizeValue != null) _summaryNewSizeValue.Text = "--";
                if (_summaryEstimatedCompletionValue != null) _summaryEstimatedCompletionValue.Text = "--";
                if (_summaryTotalEstimatedSavedValue != null) _summaryTotalEstimatedSavedValue.Text = "--";
                if (_summaryDuplicateGroupsValue != null) _summaryDuplicateGroupsValue.Text = "--";
                if (_summaryDuplicateFilesValue != null) _summaryDuplicateFilesValue.Text = "--";
                if (_summaryDuplicateRecoverableValue != null) _summaryDuplicateRecoverableValue.Text = "--";
                if (_summarySelectedCountValue != null) _summarySelectedCountValue.Text = "0";
                if (_summarySelectedSavedValue != null) _summarySelectedSavedValue.Text = "--";
                return;
            }

            if (force || _queueTotalsDirty)
            {
                RebuildQueueTotalsFromGrid();
            }
            else if ((DateTime.UtcNow - _lastQueueTotalsRefreshUtc).TotalMilliseconds < 500)
            {
                return;
            }

            _lastQueueTotalsRefreshUtc = DateTime.UtcNow;

            if (_summaryFileCountValue != null)
                _summaryFileCountValue.Text = $"{_queueFileCount} file{(_queueFileCount == 1 ? "" : "s")}";

            if (_summaryTotalCurrentValue != null)
                _summaryTotalCurrentValue.Text = _queueTotalSourceMb > 0 ? FormatSize(_queueTotalSourceMb) : "--";

            double savedForPanel = Math.Max(0, _queueTotalSourceMb - _queueTotalEstimatedMb);
            if (_summaryNewSizeValue != null)
            {
                int estimatedFileCount = _queueSourceSizeMap.Keys.Count(path =>
                    _estimatedSizeMap.TryGetValue(path, out var estimateMb) && estimateMb > 0);
                _summaryNewSizeValue.Text = _queueFileCount > 0 && estimatedFileCount == _queueFileCount
                    ? FormatSize(Math.Max(0, _queueTotalSourceMb - savedForPanel))
                    : "Waiting for estimates";
            }

            if (_summaryTotalEstimatedSavedValue != null)
            {
                _summaryTotalEstimatedSavedValue.Text = savedForPanel > 0 && _queueTotalSourceMb > 0
                    ? $"{FormatSize(savedForPanel)} ({(savedForPanel / _queueTotalSourceMb) * 100.0:0}% saved)"
                    : "--";
            }

            UpdateSelectedSpaceTotals();
            UpdateEncodePreview();
        }

        private void RebuildQueueTotalsFromGrid()
        {
            _queueTotalSourceMb = 0;
            _queueTotalEstimatedMb = 0;
            _queueFileCount = 0;
            _queueSourceSizeMap.Clear();

            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow) continue;

                _queueFileCount++;
                string? path = GetPathFromRow(row);
                double sourceMb = ParseSizeToMb(row.Cells["colSize"].Value?.ToString());
                double estimatedMb = ParseSizeToMb(row.Cells["colEstimatedSize"].Value?.ToString());

                _queueTotalSourceMb += sourceMb;
                _queueTotalEstimatedMb += estimatedMb;

                if (!string.IsNullOrWhiteSpace(path) && sourceMb > 0)
                    _queueSourceSizeMap[path] = sourceMb;

                if (!string.IsNullOrWhiteSpace(path) && estimatedMb > 0)
                    _estimatedSizeMap[path] = estimatedMb;
            }

            _queueTotalsDirty = false;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            double mb = bytes / (1024.0 * 1024.0);
            return FormatSize(mb); // existing MB-based formatter
        }

        private static string FormatSize(double mb)
        {
            if (mb <= 0) return "";
            if (mb >= 1024) return $"{mb / 1024.0:0.##} GB";
            return $"{mb:0.#} MB";
        }

        private static double GetMbOnDisk(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) return fi.Length / (1024.0 * 1024.0);
            }
            catch { }
            return 0;
        }

        private static string PercentReduction(double sourceMb, double estMb)
        {
            if (sourceMb <= 0 || estMb <= 0 || estMb >= sourceMb) return "(N/A)";
            double reduction = 1.0 - (estMb / sourceMb);
            return $"({(reduction * 100):F0}%)"; // e.g., "(45%)"
        }

        private double ParseSizeToMb(string? sizeStr)
        {
            if (string.IsNullOrWhiteSpace(sizeStr)) return 0;

            var parts = sizeStr.Split(' ');
            if (parts.Length < 2) return 0;

            if (!double.TryParse(parts[0], out var value)) return 0;

            string unit = parts[1].ToUpperInvariant();
            return unit switch
            {
                "GB" => value * 1024,
                "MB" => value,
                "KB" => value / 1024,
                _ => value
            };
        }
    }
}
