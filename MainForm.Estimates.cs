using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Encode.Services;

namespace Encode
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
                        // Use the new property names
                        SetRowEstimateRange(row, item.MinKiB, item.MaxKiB);
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
                            double estMb = item.EstimatedMb > 0 ? item.EstimatedMb : srcMb * 0.5;

                            string percentText = PercentReduction(srcMb, estMb);
                            string value = $"{FormatSize(estMb)}  {percentText}";

                            row.Cells["colEstimatedSize"].Value = value;
                            row.Cells["colSize"].Value = FormatSize(srcMb);
                            row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(srcMb, estMb);

                            // Update RowMeta on the UI thread so later consumers can avoid probing
                            string path = item.Path;
                            double durSec = item.DurationSec;
                            string res = item.Resolution ?? "";

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
                            }
                            else
                            {
                                row.Tag = new RowMeta
                                {
                                    Path = path,
                                    DurationSec = durSec,
                                    Resolution = res,
                                    SrcMb = srcMb
                                };
                            }

                            applied++;
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
            _estimateService.ResetAndCancel();

            if (dgvEncodeQueue.Rows.Count == 0)
                return;

            bool auto = chkAutoTargetSize.Checked;
            string profile = comboCompressionProfile.SelectedItem?.ToString() ?? "Medium";

            double manualTargetMb = 0;
            if (!auto && double.TryParse(txtTargetSize.Text, out var m) && m > 0)
                manualTargetMb = m;

            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow) continue;

                var meta = row.Tag as RowMeta;
                string? path = meta?.Path ?? row.Tag as string;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    row.Cells["colEstimatedSize"].Value = "";
                    continue;
                }

                // Keep the path → row map in sync for the UI pump
                _rowsByPath[path] = row;

                // Queue estimate work; UI pump will apply results
                QueueEstimate(path, auto, profile, manualTargetMb);
            }

            // Make sure timers are alive (constructor already calls this, but this is cheap)
            StartEstimateUiPump();
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

        private void UpdateSizeTotals()
        {
            // Ensure we run on the UI thread
            if (InvokeRequired)
            {
                Ui(() => UpdateSizeTotals());
                return;
            }

            if (dgvEncodeQueue == null || dgvEncodeQueue.Rows.Count == 0)
            {
                if (_statusTotalSize != null) _statusTotalSize.Text = "Total Size (0 files): --";
                if (_statusTotalEstimated != null) _statusTotalEstimated.Text = "Total Est: --";
                if (_statusSpaceSaved != null) _statusSpaceSaved.Text = "Space Saved: --";
                return;
            }

            double totalSizeMb = 0;
            double totalEstMb = 0;
            int fileCount = 0;

            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow) continue;

                fileCount++;

                var sizeText = row.Cells["colSize"].Value?.ToString();
                var estText = row.Cells["colEstimatedSize"].Value?.ToString();

                totalSizeMb += ParseSizeToMb(sizeText);
                totalEstMb += ParseSizeToMb(estText);
            }

            // --- Total Size (with file count) ---
            if (_statusTotalSize != null)
            {
                if (fileCount == 0 || totalSizeMb <= 0)
                {
                    _statusTotalSize.Text = "Total Size (0 files): --";
                }
                else
                {
                    _statusTotalSize.Text =
                        $"Total Size ({fileCount} file{(fileCount == 1 ? "" : "s")}): {FormatSize(totalSizeMb)}";
                }
            }

            // --- Total Estimated Output ---
            if (_statusTotalEstimated != null)
            {
                string label = $"Total Est: {FormatSize(totalEstMb)}";

                if (totalSizeMb > 0 && totalEstMb > 0)
                {
                    double ratio = totalEstMb / totalSizeMb;
                    label += $" ({ratio * 100:0}% of source)";
                }

                _statusTotalEstimated.Text = label;
            }

            // --- Space Saved + % saved ---
            if (_statusSpaceSaved != null)
            {
                double saved = totalSizeMb - totalEstMb;
                if (saved < 0) saved = 0;

                string label = $"Space Saved: {FormatSize(saved)}";

                if (totalSizeMb > 0 && saved > 0)
                {
                    double savedPct = saved / totalSizeMb;
                    label += $" ({savedPct * 100:0}% saved)";
                }

                _statusSpaceSaved.Text = label;
            }
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
