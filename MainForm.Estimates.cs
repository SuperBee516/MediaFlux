using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private (int w, int h) ProbeResolutionPixels(string file)
        {
            return _mediaInfoService.GetResolutionPixels(file);
        }

        private double ProbeFps(string file)
        {
            return _mediaInfoService.GetFps(file);
        }

        // Background Results UI

        private void StartEstimateUiPump()
        {
            if (_estSmartUiTimer == null)
            {
                _estSmartUiTimer = new System.Windows.Forms.Timer { Interval = 100 };
                _estSmartUiTimer.Tick += EstSmartUiTimer_Tick;
                _estSmartUiTimer.Start();
            }
        }

        private void EstSmartUiTimer_Tick(object? sender, EventArgs e)
        {
            ApplySmartEstimateResultsBatch();
        }

        private void StopEstimateUiPump()
        {
            // Cancel workers and clear queues
            _estimateService.ResetAndCancel();

            if (_estimateRefreshTimer != null)
            {
                _estimateRefreshTimer.Stop();
                _estimateRefreshTimer.Tick -= EstimateRefreshTimer_Tick;
                _estimateRefreshTimer.Dispose();
                _estimateRefreshTimer = null;
            }

            if (_estSmartUiTimer != null)
            {
                _estSmartUiTimer.Stop();
                _estSmartUiTimer.Tick -= EstSmartUiTimer_Tick;
                _estSmartUiTimer.Dispose();
                _estSmartUiTimer = null;
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
                            double srcMb = item.SourceMb;
                            double estMb = item.EstimatedMb;
                            bool hasEstimate = srcMb > 0 && estMb > 0;
                            string customSuffix = item.IsCustom ? " (custom)" : string.Empty;

                            row.Cells["colEstimatedSize"].Value = hasEstimate
                                ? $"{FormatSize(estMb)}  {PercentReduction(srcMb, estMb)}{customSuffix}"
                                : item.UnavailableReason ?? "Metadata unavailable";
                            if (srcMb > 0)
                                row.Cells["colSize"].Value = FormatSize(srcMb);
                            row.Cells["colEstimatedSize"].Tag = hasEstimate
                                ? new Tuple<double, double>(srcMb, estMb)
                                : null;
                            row.Cells["colEstimatedSize"].ToolTipText = hasEstimate
                                ? "Calculated independently from this file's size, duration, resolution, frame rate, bitrate, codec, and the current encoding settings."
                                : "Required media metadata could not be determined. MediaFlux will not substitute a shared or fixed estimate.";
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

                                if (item.Fps > 0)
                                    rm.Fps = (int)Math.Round(item.Fps);

                                rm.EstimateDiagnostic = item.EstimateDiagnostic;
                                rm.EstimatedPlannedAudioBitrateKbps =
                                    item.PlannedAudioBitrateKbps;
                                rm.EstimatedPlannedMappedAncillaryBitrateKbps =
                                    item.PlannedMappedAncillaryBitrateKbps;
                            }
                            else
                            {
                                row.Tag = new RowMeta
                                {
                                    Path = path,
                                    DurationSec = durSec,
                                    Resolution = res,
                                    SrcMb = srcMb,
                                    VideoCodec = codec,
                                    Fps = item.Fps > 0 ? (int)Math.Round(item.Fps) : 0,
                                    EstimateDiagnostic = item.EstimateDiagnostic,
                                    EstimatedPlannedAudioBitrateKbps =
                                        item.PlannedAudioBitrateKbps,
                                    EstimatedPlannedMappedAncillaryBitrateKbps =
                                        item.PlannedMappedAncillaryBitrateKbps
                                };
                            }

                            TrackCodecFilterCount(path, codec);

                            ApplySmartRecommendation(row, item.Recommendation);
                            UpdateRowCustomFlag(row);
                            RestoreQueuedStateAfterEstimate(row);
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Smart Estimate UI Update Error for {item.Path}: {ex.Message}\n{ex.StackTrace}");
                            row.Cells["colEstimatedSize"].Value = "Error";
                            SetRecommendationUnavailable(
                                row,
                                "Queue analysis failed for this file.");
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

        private void SafeRefreshEstimates()
        {
            if (dgvEncodeQueue.Rows.Count == 0)
                return;

            RunEstimatePass();
        }

        private void ScheduleEstimateRefresh()
        {
            if (IsDisposed || dgvEncodeQueue == null || dgvEncodeQueue.Rows.Count == 0)
                return;

            _estimateRefreshTimer ??= new System.Windows.Forms.Timer { Interval = 250 };
            _estimateRefreshTimer.Stop();
            _estimateRefreshTimer.Tick -= EstimateRefreshTimer_Tick;
            _estimateRefreshTimer.Tick += EstimateRefreshTimer_Tick;
            _estimateRefreshTimer.Start();
        }

        private void EstimateRefreshTimer_Tick(object? sender, EventArgs e)
        {
            _estimateRefreshTimer?.Stop();
            SafeRefreshEstimates();
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
            ValidatedEncoderSettings encoderSettings =
                GetValidatedEncoderSettingsFromUi(
                    includeConcurrentSessions: false);
            VideoEncoderSelection targetEncoder =
                encoderSettings.Resolved.Selection;
            int quality = encoderSettings.QualityValue;
            int? targetHeight = GetEstimateTargetHeight();

            double manualTargetMb = 0;
            if (!autoRequested && double.TryParse(txtTargetSize.Text, out var m) && m > 0)
                manualTargetMb = m;

            // A valid manual target wins when Auto is off. If the field is blank,
            // continue showing the metadata-based estimate for the selected Quality /
            // File Size profile; the empty field is not an error state.
            bool useProfileEstimate = SizeEstimateService.ShouldUseProfileEstimate(
                autoRequested,
                manualTargetMb);

            int queued = 0;
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow) continue;

                var meta = row.Tag as RowMeta;
                string? path = meta?.Path ?? row.Tag as string;

                if (meta?.ExcludedFromEncodeAsDuplicate == true)
                {
                    row.Cells["colEstimatedSize"].Value = "";
                    SetRecommendationUnavailable(
                        row,
                        "Exact duplicate is excluded from encoding.");
                    SetEncodeRowState(
                        row,
                        "Excluded - exact duplicate",
                        "",
                        "",
                        "Exact duplicate soft-excluded from encoding. The source file was not changed.");
                    continue;
                }

                bool isCustom = RowHasCustomSettings(meta);
                string rowProfile = !string.IsNullOrWhiteSpace(meta?.CustomCompressionProfile)
                    ? meta!.CustomCompressionProfile!
                    : profile;
                double rowManualTargetMb = meta?.CustomTargetMb is > 0
                    ? meta.CustomTargetMb.Value
                    : manualTargetMb;
                bool rowAuto = meta?.CustomTargetMb is > 0
                    ? false
                    : !string.IsNullOrWhiteSpace(meta?.CustomCompressionProfile) || useProfileEstimate;

                if (meta?.IsDvdEncode == true)
                {
                    double sourceMb = meta.SrcMb;
                    double estimateMb = !rowAuto && rowManualTargetMb > 0
                        ? rowManualTargetMb
                        : EstimateDvdEncodeTargetMb(
                            meta,
                            rowProfile,
                            targetEncoder,
                            quality,
                            targetHeight);
                    if (estimateMb > 0)
                    {
                        _estimatedSizeMap[path!] = estimateMb;
                        row.Cells["colEstimatedSize"].Value =
                            $"{FormatSize(estimateMb)}  {PercentReduction(sourceMb, estimateMb)}" +
                            (isCustom ? " (custom)" : "");
                        row.Cells["colEstimatedSize"].Tag =
                            new Tuple<double, double>(sourceMb, estimateMb);
                    }
                    else
                    {
                        _estimatedSizeMap.Remove(path!);
                        row.Cells["colEstimatedSize"].Value = "Metadata unavailable";
                        row.Cells["colEstimatedSize"].Tag = null;
                    }

                    row.Cells["colEstimatedSize"].ToolTipText =
                        "Estimated from the combined DVD title size, duration, resolution, " +
                        "frame rate, codec, and current encoding settings.";
                    SetRecommendationUnavailable(
                        row,
                        "DVD title recommendations remain in the DVD import workflow.");
                    UpdateRowCustomFlag(row);
                    RestoreQueuedStateAfterEstimate(row);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    row.Cells["colEstimatedSize"].Value = "";
                    SetRecommendationUnavailable(
                        row,
                        "The source file is unavailable.");
                    continue;
                }

                // Keep the path → row map in sync for the UI pump
                _rowsByPath[path] = row;
                _estimatedSizeMap.Remove(path);
                row.Cells["colEstimatedSize"].Value = "Analyzing…";
                row.Cells["colEstimatedSize"].Tag = null;
                row.Cells["colEstimatedSize"].ToolTipText = "Reading this file's metadata and recalculating with the current encoding settings.";
                SetRecommendationAnalyzing(row);
                if (row.Cells["colStatus"].Value?.ToString() is not "Encoding" and not "Done" and not "Failed" and not "Canceled" and not "Retry Queued")
                    SetEncodeRowState(row, "Estimating", row.Cells["colProgress"].Value?.ToString(), row.Cells["colETA"].Value?.ToString(), "Estimating output size.");

                // Queue estimate work; UI pump will apply results
                StorageSavingsOptions rowStorageSavings =
                    _config.StorageSavings.CloneNormalized();
                if (meta?.CustomTargetMb is > 0 ||
                    !string.IsNullOrWhiteSpace(meta?.CustomCompressionProfile))
                {
                    rowStorageSavings.Enabled = false;
                }
                QueueEstimate(
                    path,
                    rowAuto,
                    rowProfile,
                    rowManualTargetMb,
                    targetEncoder,
                    quality,
                    targetHeight,
                    GetSelectedAudioChannels(),
                    isCustom,
                    rowStorageSavings);
                queued++;
            }

            _lastEstimateQueuedCount = queued;
            _estimatesDeferredForLargeQueue = false;
            MarkQueueTotalsDirty();
            UpdateSizeTotals(force: true);
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

        private static double EstimateDvdEncodeTargetMb(
            RowMeta meta,
            string compressionProfile,
            VideoEncoderSelection targetEncoder,
            int quality,
            int? targetHeight)
        {
            DvdTitleCandidate? candidate = meta.DvdEncodeOptions?.Candidate;
            if (candidate == null)
                return 0;

            double sourceMb = candidate.CombinedSizeBytes / (1024d * 1024d);
            return SizeEstimateService.EstimateAutoTargetMbSmart(
                sourceMb,
                candidate.CombinedDurationSeconds,
                candidate.VideoWidth ?? 0,
                candidate.VideoHeight ?? 0,
                candidate.FrameRate ?? 0,
                sourceVideoBitrateKbps: 0,
                candidate.VideoCodec,
                compressionProfile,
                targetEncoder,
                quality,
                targetHeight);
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

        private void QueueEstimate(
            string path,
            bool auto,
            string profile,
            double manualTargetMb,
            VideoEncoderSelection targetEncoder,
            int quality,
            int? targetHeight,
            int? targetAudioChannels,
            bool isCustom,
            StorageSavingsOptions storageSavings)
        {
            _estimateService.QueueSmartEstimate(
                path,
                auto,
                profile,
                manualTargetMb,
                targetEncoder,
                quality,
                targetHeight,
                targetAudioChannels,
                isCustom,
                _config.SmartRecommendationsEnabled,
                _config.MinimumExpectedSavingsPercent,
                storageSavings);
        }

        private int? GetEstimateTargetHeight()
        {
            return GetSelectedScaleMode() switch
            {
                EncodingService.ScaleMode.To720p => 720,
                EncodingService.ScaleMode.To1080p => 1080,
                EncodingService.ScaleMode.To1440p => 1440,
                EncodingService.ScaleMode.To4K => 2160,
                _ => null
            };
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
