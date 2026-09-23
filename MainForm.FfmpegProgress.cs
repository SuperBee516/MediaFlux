using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private sealed class EncodeMetrics
        {
            public int Fps { get; set; }
            public double Speed { get; set; }
            public string TimeStr { get; set; } = "--";
            public long LastFrame { get; set; }
            public DateTime LastFrameUtc { get; set; }
            public EncodeProgressAttemptTracker AttemptTracker { get; } = new();
        }

        private readonly Dictionary<DataGridViewRow, EncodeMetrics> _activeEncodeMetrics = new();
        private readonly List<DataGridViewRow> _activeEncodeRows = new();

        // Captures raw FFmpeg output and presents existing audio progress in the status strip.
        private void HandleFfmpegProgressLine(string line)
        {
            _activeJobLog.Value?.AppendLine(line);

            var match = ffmpegProgressRegex.Match(line);
            if (match.Success)
            {
                string timeStr = match.Groups[6].Value;
                Ui(() => UpdateAudioProgress(timeStr));
            }
            else
            {
                var am = ffmpegAudioProgressRegex.Match(line);
                if (am.Success)
                    Ui(() => UpdateAudioProgress(am.Groups[3].Value));
            }
        }

        private void HandleFfmpegProgressLineForRowMetrics(DataGridViewRow row, string line)
        {
            if (row == null || string.IsNullOrWhiteSpace(line))
                return;

            if (!TryParseFfmpegProgress(line, out var metrics))
                return;

            Ui(() =>
            {
                UpdateEncodeMetricsForRow(row, metrics);
                UpdateQueueEstimatedCompletion();
            });
        }

        private void UpdateQueueEstimatedCompletion()
        {
            if (_summaryEstimatedCompletionValue == null)
                return;

            if (_activeEncodeRows.Count == 0)
            {
                _summaryEstimatedCompletionValue.Text = dgvEncodeQueue.Rows.Count > 0
                    ? "Starts when encoding begins"
                    : "--";
                return;
            }

            double remainingMediaSeconds = 0;
            double combinedSpeed = 0;
            bool missingDuration = false;

            foreach (DataGridViewRow queueRow in dgvEncodeQueue.Rows)
            {
                if (queueRow.IsNewRow)
                    continue;

                string status = queueRow.Cells["colStatus"].Value?.ToString() ?? string.Empty;
                if (status.Equals("Done", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
                    continue;

                double duration = (queueRow.Tag as RowMeta)?.DurationSec ?? 0;
                if (duration <= 0)
                {
                    missingDuration = true;
                    continue;
                }

                double completed = 0;
                if (_activeEncodeMetrics.TryGetValue(queueRow, out var activeMetrics))
                {
                    completed = ParseFfmpegTimeToSeconds(activeMetrics.TimeStr);
                    if (activeMetrics.Speed > 0)
                        combinedSpeed += activeMetrics.Speed;
                }

                remainingMediaSeconds += Math.Max(0, duration - completed);
            }

            if (combinedSpeed <= 0 || remainingMediaSeconds <= 0)
            {
                _summaryEstimatedCompletionValue.Text = "Calculating...";
                return;
            }

            double? etaSeconds = EncodeEtaCalculator.CalculateAggregateSeconds(remainingMediaSeconds, combinedSpeed);
            if (!etaSeconds.HasValue)
            {
                _summaryEstimatedCompletionValue.Text = "Calculating...";
                return;
            }
            TimeSpan eta = TimeSpan.FromSeconds(etaSeconds.Value);
            string etaText = eta.TotalDays >= 1
                ? $"{(int)eta.TotalDays}d {eta.Hours:00}:{eta.Minutes:00}:{eta.Seconds:00}"
                : eta.ToString(@"hh\:mm\:ss");

            _summaryEstimatedCompletionValue.Text = missingDuration
                ? $"~{etaText} + unknown jobs"
                : $"~{etaText}";
        }

        private static double ParseSpeedX(string line)
        {
            var idx = line.IndexOf("speed=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            var sub = line.Substring(idx + 6);
            var space = sub.IndexOf(' ');
            if (space >= 0) sub = sub.Substring(0, space);
            if (sub.EndsWith("x", StringComparison.OrdinalIgnoreCase)) sub = sub[..^1];
            return double.TryParse(sub, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0;
        }

        // Parse existing per-row progress values used by the queue and ETA summary.
        private static readonly Regex ffmpegProgressRegex = new Regex(
            @"frame=\s*(\d+)\s+fps=\s*([\d\.]+)\s+q=\s*([-\d\.]+)\s+size=\s*(\d+)(kB|KiB)\s+time=\s*(\d{2}:\d{2}:\d{2}\.\d{2})\s+bitrate=\s*([\d\.]+)kbits/s\s+speed=\s*([\d\.]+)x",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Audio-only style progress line: size= ... time= ... bitrate= ... speed= ... (no frame/fps/q)
        private static readonly Regex ffmpegAudioProgressRegex = new Regex(
            @"size=\s*(\d+)(kB|KiB)\s+time=(\d{2}:\d{2}:\d{2}\.\d{2})\s+bitrate=\s*([\d\.]+)kbits/s\s+speed=\s*([\d\.]+)x",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool TryParseFfmpegProgress(string line, out EncodeMetrics metrics)
        {
            metrics = new EncodeMetrics();

            var match = ffmpegProgressRegex.Match(line);
            if (match.Success)
            {
                metrics.Fps = int.TryParse(match.Groups[2].Value, out var fpsVal) ? fpsVal : 0;
                metrics.TimeStr = match.Groups[6].Value;
                metrics.Speed = double.TryParse(
                    match.Groups[8].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var sp
                ) ? sp : 0;

                return true;
            }

            var am = ffmpegAudioProgressRegex.Match(line);
            if (am.Success)
            {
                metrics.TimeStr = am.Groups[3].Value;
                metrics.Speed = double.TryParse(
                    am.Groups[5].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var sp
                ) ? sp : 0;

                metrics.Fps = 0;
                return true;
            }

            return false;
        }

        private void BeginEncodeMetricsForRow(DataGridViewRow row)
        {
            if (row == null)
                return;

            if (!_activeEncodeRows.Contains(row))
                _activeEncodeRows.Add(row);

            if (!_activeEncodeMetrics.ContainsKey(row))
                _activeEncodeMetrics[row] = new EncodeMetrics();

            UpdateOperationProgressPresentation();
        }

        private void EndEncodeMetricsForRow(DataGridViewRow row)
        {
            if (row == null)
                return;

            _activeEncodeMetrics.Remove(row);
            _activeEncodeRows.Remove(row);
            UpdateQueueEstimatedCompletion();

            if (_activeEncodeRows.Count == 0)
            {
                ResetEncodeMetrics();
                return;
            }

            UpdateOperationProgressPresentation();
        }

        private void ApplyAiIntermediateProgress(DataGridViewRow row, AiIntermediateProgress progress)
        {
            if (row == null || row.DataGridView != dgvEncodeQueue || progress.Total <= 0)
                return;

            Ui(() =>
            {
                if (row.DataGridView != dgvEncodeQueue)
                    return;

                double fraction = Math.Clamp((double)progress.Current / progress.Total, 0, 1);
                string stage = progress.Stage switch
                {
                    AiIntermediateStage.ExtractingFrames => "Extracting AI frames",
                    AiIntermediateStage.AiProcessing => "AI restoring",
                    AiIntermediateStage.Reassembling => "Reassembling AI video",
                    AiIntermediateStage.Validating => "Validating AI video",
                    _ => "Preparing AI restoration"
                };

                string eta = progress.EstimatedRemaining?.ToString(@"hh\:mm\:ss") ?? "Calculating...";

                SetEncodeRowState(
                    row,
                    stage,
                    $"AI {fraction * 100:0}%",
                    eta,
                    progress.Message);

                UpdateOperationProgressPresentation();
            });
        }

        private void UpdateEncodeMetricsForRow(DataGridViewRow row, EncodeMetrics metrics)
        {
            if (row == null || metrics == null)
                return;

            if (!_activeEncodeRows.Contains(row))
                _activeEncodeRows.Add(row);

            _activeEncodeMetrics[row] = metrics;

            UpdateOperationProgressPresentation();
        }

        private void ApplyStructuredEncodeProgress(
            DataGridViewRow row,
            EncodingService.EncodeProgress progress)
        {
            if (row == null || row.DataGridView != dgvEncodeQueue)
                return;

            if (!_activeEncodeMetrics.TryGetValue(row, out EncodeMetrics? attemptMetrics))
                attemptMetrics = new EncodeMetrics();
            EncodeProgressAttemptDisposition attemptDisposition =
                attemptMetrics.AttemptTracker.Observe(progress.Attempt);
            if (attemptDisposition == EncodeProgressAttemptDisposition.IgnoreStale)
                return;
            if (attemptDisposition == EncodeProgressAttemptDisposition.AcceptAndReset)
            {
                attemptMetrics.Fps = 0;
                attemptMetrics.Speed = 0;
                attemptMetrics.TimeStr = "--";
                attemptMetrics.LastFrame = 0;
                attemptMetrics.LastFrameUtc = default;
                _activeEncodeMetrics[row] = attemptMetrics;
                row.Cells["colProgress"].Value = "0%";
                row.Cells["colETA"].Value = "--:--:--";
            }

            string existingText = row.Cells["colProgress"].Value?.ToString() ?? "";
            int existingPercent = int.TryParse(existingText.TrimEnd('%'), out int parsedPercent)
                ? parsedPercent
                : 0;
            int percent = Math.Max(existingPercent, (int)Math.Round(progress.Percent));
            row.Cells["colProgress"].Value = $"{Math.Clamp(percent, 0, 100)}%";
            if (progress.EncodedFrames is long frame)
            {
                if (!_activeEncodeMetrics.TryGetValue(row, out EncodeMetrics? metrics))
                    metrics = attemptMetrics;
                DateTime now = DateTime.UtcNow;
                if (metrics.LastFrame > 0 && frame > metrics.LastFrame)
                {
                    double elapsed = (now - metrics.LastFrameUtc).TotalSeconds;
                    if (elapsed >= 0.25)
                        metrics.Fps = (int)Math.Round((frame - metrics.LastFrame) / elapsed);
                }
                if (progress.Fps > 0)
                    metrics.Fps = (int)Math.Round(progress.Fps);
                metrics.Speed = progress.Speed;
                metrics.LastFrame = Math.Max(metrics.LastFrame, frame);
                metrics.LastFrameUtc = now;
                _activeEncodeMetrics[row] = metrics;
                row.Cells["colProgress"].ToolTipText =
                    $"Encoded frames: {frame:N0}" +
                    (progress.Basis is EncodeProgressBasis.MeasuredFrames or EncodeProgressBasis.DerivedCfrFrames
                        ? " (frame-derived progress)"
                        : progress.Basis == EncodeProgressBasis.Indeterminate
                            ? " (FFmpeg timestamp unavailable; progress indeterminate)"
                            : "");
            }
            double? etaSeconds = progress.Basis is EncodeProgressBasis.MeasuredFrames or EncodeProgressBasis.DerivedCfrFrames
                && progress.TotalFrames is long totalFrames
                && progress.EncodedFrames is long encodedFrames
                ? EncodeEtaCalculator.CalculateFrameSeconds(totalFrames, encodedFrames, progress.Fps)
                : progress.Basis == EncodeProgressBasis.Indeterminate
                    ? null
                    : EncodeEtaCalculator.CalculateSeconds(
                        progress.TotalDuration.TotalSeconds,
                        progress.CurrentTime.TotalSeconds,
                        progress.Speed);
            row.Cells["colETA"].Value = etaSeconds.HasValue
                ? TimeSpan.FromSeconds(etaSeconds.Value).ToString(@"hh\:mm\:ss")
                : "--:--:--";
            UpdateOperationProgressPresentation();
        }

        private void ApplyAuthoritativeEncodeProgress(DataGridViewRow row, string timeText, double ffmpegSpeed)
        {
            if (row == null || row.DataGridView != dgvEncodeQueue)
                return;
            if (!TryGetRowPathAndDuration(row, out _, out double durationSec) || durationSec <= 0)
                return;
            double mediaSeconds = ParseFfmpegTimeToSeconds(timeText);
            if (!double.IsFinite(mediaSeconds) || mediaSeconds < 0)
                return;
            string existingText = row.Cells["colProgress"].Value?.ToString() ?? "";
            int existingPercent = int.TryParse(existingText.TrimEnd('%'), out int parsedPercent) ? parsedPercent : 0;
            int percent = EncodeProgressCalculator.CalculatePercent(mediaSeconds, durationSec, existingPercent);
            row.Cells["colProgress"].Value = $"{percent}%";
            double? etaSeconds = EncodeEtaCalculator.CalculateSeconds(durationSec, mediaSeconds, ffmpegSpeed);
            row.Cells["colETA"].Value = etaSeconds.HasValue
                ? TimeSpan.FromSeconds(etaSeconds.Value).ToString(@"hh\:mm\:ss")
                : "--:--:--";
            UpdateOperationProgressPresentation();
        }

        private void UpdateAudioProgress(string timeText)
        {
            if (!_audioProgressActive ||
                !TimeSpan.TryParseExact(timeText, @"hh\:mm\:ss\.ff", null, out TimeSpan current))
            {
                return;
            }

            if (_audioProgressTotalDuration.TotalSeconds > 0)
            {
                int percent = (int)((current.TotalSeconds / _audioProgressTotalDuration.TotalSeconds) * 100);
                _audioProgressPercent = Math.Clamp(percent, 0, 100);
            }
            else
            {
                _audioProgressPercent = null;
            }

            UpdateOperationProgressPresentation();
        }

        private void ResetEncodeMetrics()
        {
            _activeEncodeMetrics.Clear();
            _activeEncodeRows.Clear();
            UpdateOperationProgressPresentation();
        }
    }
}
