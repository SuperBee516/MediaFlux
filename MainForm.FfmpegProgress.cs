using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            public int SizeKiB { get; set; }
            public double Bitrate { get; set; }
            public double Speed { get; set; }
            public string TimeStr { get; set; } = "--";
            public bool HasData { get; set; }
        }

        private readonly Dictionary<DataGridViewRow, EncodeMetrics> _activeEncodeMetrics = new();
        private readonly List<DataGridViewRow> _activeEncodeRows = new();
        private readonly Dictionary<DataGridViewRow, AiProgressState> _activeAiProgress = new();

        private sealed class AiProgressState
        {
            public Stopwatch ProcessingStopwatch { get; } = new();
            public int LastCompleted { get; set; }
        }

        // Parses lines and updates metrics UI (thread-safe)
        private void HandleFfmpegProgressLine(string line)
        {
            // Always capture raw ffmpeg output for the active job log
            _activeJobLogSb?.AppendLine(line);

            var match = ffmpegProgressRegex.Match(line);
            if (match.Success)
            {
                int fps = int.TryParse(match.Groups[2].Value, out var fpsVal) ? fpsVal : 0;
                int size = int.TryParse(match.Groups[4].Value, out var sizeVal) ? sizeVal : 0;
                string sizeUnit = match.Groups[5].Value;
                string timeStr = match.Groups[6].Value;

                double bitrate = double.TryParse(
                    match.Groups[7].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var br
                ) ? br : 0;

                double speed = double.TryParse(
                    match.Groups[8].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var sp
                ) ? sp : 0;

                int sizeKiB = (sizeUnit == "kB") ? (int)(size / 1.024) : size;

                Ui(() => UpdateEncodeMetricsSingleLine(fps, sizeKiB, bitrate, speed, timeStr));
            }
            else
            {
                // Audio-only style: size= ... time= ... bitrate= ... speed= ...
                var am = ffmpegAudioProgressRegex.Match(line);
                if (am.Success)
                {
                    int size = int.TryParse(am.Groups[1].Value, out var sizeVal) ? sizeVal : 0;
                    string sizeUnit = am.Groups[2].Value;
                    string timeStr = am.Groups[3].Value;

                    double bitrate = double.TryParse(
                        am.Groups[4].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var br
                    ) ? br : 0;

                    double speed = double.TryParse(
                        am.Groups[5].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var sp
                    ) ? sp : 0;

                    int sizeKiB = (sizeUnit == "kB") ? (int)(size / 1.024) : size;

                    // For audio we don’t care about FPS (set to 0)
                    Ui(() => UpdateEncodeMetricsSingleLine(0, sizeKiB, bitrate, speed, timeStr));
                }
            }

            // No grid/ETA updates here anymore – that’s handled per-row and per-job.
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

            TimeSpan eta = TimeSpan.FromSeconds(remainingMediaSeconds / combinedSpeed);
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

        // For encoding metrics panel and progress bar
        private TimeSpan _currentEncodeDuration = TimeSpan.Zero;
        private TimeSpan _currentEncodeTotalDuration = TimeSpan.Zero;
        private static readonly Regex ffmpegProgressRegex = new Regex(
            @"frame=\s*(\d+)\s+fps=\s*(\d+)\s+q=\s*([\d\.]+)\s+size=\s*(\d+)(kB|KiB)\s+time=\s*(\d{2}:\d{2}:\d{2}\.\d{2})\s+bitrate=\s*([\d\.]+)kbits/s\s+speed=\s*([\d\.]+)x",
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
                int size = int.TryParse(match.Groups[4].Value, out var sizeVal) ? sizeVal : 0;
                string sizeUnit = match.Groups[5].Value;
                metrics.TimeStr = match.Groups[6].Value;

                metrics.Bitrate = double.TryParse(
                    match.Groups[7].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var br
                ) ? br : 0;

                metrics.Speed = double.TryParse(
                    match.Groups[8].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var sp
                ) ? sp : 0;

                metrics.SizeKiB = (sizeUnit == "kB") ? (int)(size / 1.024) : size;
                metrics.HasData = true;
                return true;
            }

            var am = ffmpegAudioProgressRegex.Match(line);
            if (am.Success)
            {
                int size = int.TryParse(am.Groups[1].Value, out var sizeVal) ? sizeVal : 0;
                string sizeUnit = am.Groups[2].Value;
                metrics.TimeStr = am.Groups[3].Value;

                metrics.Bitrate = double.TryParse(
                    am.Groups[4].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var br
                ) ? br : 0;

                metrics.Speed = double.TryParse(
                    am.Groups[5].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var sp
                ) ? sp : 0;

                metrics.SizeKiB = (sizeUnit == "kB") ? (int)(size / 1.024) : size;
                metrics.Fps = 0;
                metrics.HasData = true;
                return true;
            }

            return false;
        }

        private bool BeginEncodeMetricsForRow(DataGridViewRow row)
        {
            if (row == null)
                return false;

            bool wasEmpty = _activeEncodeRows.Count == 0;
            if (!_activeEncodeRows.Contains(row))
                _activeEncodeRows.Add(row);

            if (!_activeEncodeMetrics.ContainsKey(row))
                _activeEncodeMetrics[row] = new EncodeMetrics();

            if (wasEmpty)
                ResetEncodeMetricsPanel();

            UpdateEncodeMetricsPanel();
            if (_activeEncodeRows.Count > 1)
            {
                if (progressBarEncode.Style != ProgressBarStyle.Marquee)
                {
                    progressBarEncode.Style = ProgressBarStyle.Marquee;
                    progressBarEncode.MarqueeAnimationSpeed = 30;
                }
            }
            return wasEmpty;
        }

        private void EndEncodeMetricsForRow(DataGridViewRow row)
        {
            if (row == null)
                return;

            _activeAiProgress.Remove(row);
            _activeEncodeMetrics.Remove(row);
            _activeEncodeRows.Remove(row);
            UpdateQueueEstimatedCompletion();

            if (_activeEncodeRows.Count == 0)
            {
                StopJobTimer();
                ResetEncodeMetrics();
                return;
            }

            if (_activeEncodeRows.Count == 1 && progressBarEncode.Style != ProgressBarStyle.Continuous)
                progressBarEncode.Style = ProgressBarStyle.Continuous;

            UpdateEncodeMetricsPanel();
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

                string eta = "Calculating...";
                if (progress.Stage == AiIntermediateStage.AiProcessing)
                {
                    if (!_activeAiProgress.TryGetValue(row, out AiProgressState? state))
                    {
                        state = new AiProgressState();
                        _activeAiProgress[row] = state;
                    }
                    if (!state.ProcessingStopwatch.IsRunning)
                        state.ProcessingStopwatch.Start();
                    state.LastCompleted = Math.Max(state.LastCompleted, progress.Current);
                    if (state.LastCompleted >= 2 && state.ProcessingStopwatch.Elapsed.TotalSeconds > 0)
                    {
                        double framesPerSecond = state.LastCompleted / state.ProcessingStopwatch.Elapsed.TotalSeconds;
                        eta = framesPerSecond > 0
                            ? TimeSpan.FromSeconds((progress.Total - state.LastCompleted) / framesPerSecond).ToString(@"hh\:mm\:ss")
                            : "Calculating...";
                    }
                }

                SetEncodeRowState(
                    row,
                    stage,
                    $"AI {fraction * 100:0}%",
                    eta,
                    $"{progress.Message}: {progress.Current:N0}/{progress.Total:N0} frames.");

                if (_activeEncodeRows.Count <= 1)
                {
                    if (progressBarEncode.Style != ProgressBarStyle.Continuous)
                        progressBarEncode.Style = ProgressBarStyle.Continuous;
                    SetProgress(progressBarEncode, (int)Math.Round(fraction * 100));
                }
            });
        }

        private void UpdateEncodeMetricsForRow(DataGridViewRow row, EncodeMetrics metrics)
        {
            if (row == null || metrics == null)
                return;

            if (!_activeEncodeRows.Contains(row))
                _activeEncodeRows.Add(row);

            _activeEncodeMetrics[row] = metrics;

            UpdateEncodeMetricsPanel();

            bool singleEncode = _activeEncodeRows.Count <= 1;
            if (singleEncode)
            {
                UpdateEncodeProgressBar(metrics.TimeStr);
            }
            else
            {
                if (progressBarEncode.Style != ProgressBarStyle.Marquee)
                {
                    progressBarEncode.Style = ProgressBarStyle.Marquee;
                    progressBarEncode.MarqueeAnimationSpeed = 30;
                }
            }
        }

        // Updates all labels and progress bar (thread-safe)
        private void UpdateEncodeMetricsSingleLine(int fps, int sizeKiB, double bitrate, double speed, string timeStr)
        {
            SetLabel(lblSpeedValue, $"{speed:F1}x");
            SetLabel(lblSizeValue, $"{sizeKiB:N0} KiB");
            SetLabel(lblFPSValue, fps.ToString());
            SetLabel(lblBitrateValue, $"{bitrate:F1} kbits/s");
            SetLabel(lblTimeValue, timeStr);

            SetLabel(lblSpeedValue2, "--");
            SetLabel(lblSizeValue2, "--");
            SetLabel(lblFPSValue2, "--");
            SetLabel(lblBitrateValue2, "--");
            SetLabel(lblTimeValue2, "--");
            SetLabel(lblJobTimer2, "--");

            Ui(() =>
            {
                lblJob1.Text = string.Empty;
                lblJob2.Visible = false;
                lblSpeedValue2.Visible = false;
                lblSizeValue2.Visible = false;
                lblFPSValue2.Visible = false;
                lblBitrateValue2.Visible = false;
                lblTimeValue2.Visible = false;
                lblJobTimer2.Visible = false;
            });

            UpdateEncodeProgressBar(timeStr);
        }

        private void UpdateEncodeProgressBar(string timeStr)
        {
            if (progressBarEncode.Style != ProgressBarStyle.Continuous)
                progressBarEncode.Style = ProgressBarStyle.Continuous;

            if (TimeSpan.TryParseExact(timeStr, @"hh\:mm\:ss\.ff", null, out var current))
            {
                _currentEncodeDuration = current;
                int percent = 0;
                if (_currentEncodeTotalDuration.TotalSeconds > 0)
                    percent = (int)((current.TotalSeconds / _currentEncodeTotalDuration.TotalSeconds) * 100);

                SetProgress(progressBarEncode, percent);
            }
        }

        private void UpdateEncodeMetricsPanel()
        {
            var rows = _activeEncodeRows.Where(_activeEncodeMetrics.ContainsKey)
                .OrderBy(r => r.Index)
                .Take(2)
                .ToList();

            bool showSecond = rows.Count > 1;

            Ui(() =>
            {
                lblJob1.Text = showSecond ? "Job 1:" : string.Empty;
                lblJob2.Text = "Job 2:";
                lblJob2.Visible = showSecond;

                lblSpeedValue2.Visible = showSecond;
                lblSizeValue2.Visible = showSecond;
                lblFPSValue2.Visible = showSecond;
                lblBitrateValue2.Visible = showSecond;
                lblTimeValue2.Visible = showSecond;
                lblJobTimer2.Visible = showSecond;
            });

            if (rows.Count > 0)
                ApplyMetricsToLabels(rows[0], lblSpeedValue, lblSizeValue, lblFPSValue, lblBitrateValue, lblTimeValue);
            else
                ApplyEmptyMetrics(lblSpeedValue, lblSizeValue, lblFPSValue, lblBitrateValue, lblTimeValue);

            if (showSecond)
                ApplyMetricsToLabels(rows[1], lblSpeedValue2, lblSizeValue2, lblFPSValue2, lblBitrateValue2, lblTimeValue2);
            else
                ApplyEmptyMetrics(lblSpeedValue2, lblSizeValue2, lblFPSValue2, lblBitrateValue2, lblTimeValue2);

            SetLabel(lblJobTimer2, "--");
        }

        private void ApplyMetricsToLabels(DataGridViewRow row, Label speedLabel, Label sizeLabel, Label fpsLabel, Label bitrateLabel, Label timeLabel)
        {
            if (!_activeEncodeMetrics.TryGetValue(row, out var metrics) || !metrics.HasData)
            {
                ApplyEmptyMetrics(speedLabel, sizeLabel, fpsLabel, bitrateLabel, timeLabel);
                return;
            }

            SetLabel(speedLabel, $"{metrics.Speed:F1}x");
            SetLabel(sizeLabel, $"{metrics.SizeKiB:N0} KiB");
            SetLabel(fpsLabel, metrics.Fps.ToString());
            SetLabel(bitrateLabel, $"{metrics.Bitrate:F1} kbits/s");
            SetLabel(timeLabel, metrics.TimeStr);
        }

        private void ApplyEmptyMetrics(Label speedLabel, Label sizeLabel, Label fpsLabel, Label bitrateLabel, Label timeLabel)
        {
            SetLabel(speedLabel, "--");
            SetLabel(sizeLabel, "--");
            SetLabel(fpsLabel, "--");
            SetLabel(bitrateLabel, "--");
            SetLabel(timeLabel, "--");
        }

        private void ResetEncodeMetricsPanel()
        {
            ApplyEmptyMetrics(lblSpeedValue, lblSizeValue, lblFPSValue, lblBitrateValue, lblTimeValue);
            ApplyEmptyMetrics(lblSpeedValue2, lblSizeValue2, lblFPSValue2, lblBitrateValue2, lblTimeValue2);
            SetLabel(lblJobTimer2, "--");

            Ui(() =>
            {
                lblJob1.Text = string.Empty;
                lblJob2.Visible = false;
                lblSpeedValue2.Visible = false;
                lblSizeValue2.Visible = false;
                lblFPSValue2.Visible = false;
                lblBitrateValue2.Visible = false;
                lblTimeValue2.Visible = false;
                lblJobTimer2.Visible = false;
            });
        }
        // Resets metrics panel to "--" and progress to 0 and job timer
        private void ResetEncodeMetrics()
        {
            _activeEncodeMetrics.Clear();
            _activeEncodeRows.Clear();

            SetLabel(lblSpeedValue, "--");
            SetLabel(lblSizeValue, "--");
            SetLabel(lblFPSValue, "--");
            SetLabel(lblBitrateValue, "--");
            SetLabel(lblTimeValue, "--");
            SetLabel(lblSpeedValue2, "--");
            SetLabel(lblSizeValue2, "--");
            SetLabel(lblFPSValue2, "--");
            SetLabel(lblBitrateValue2, "--");
            SetLabel(lblTimeValue2, "--");
            SetLabel(lblJobTimer2, "--");

            // Always reset to a normal, non-animated bar when idle
            if (progressBarEncode.Style != ProgressBarStyle.Continuous)
                progressBarEncode.Style = ProgressBarStyle.Continuous;

            SetProgress(progressBarEncode, 0);
            ResetJobTimer();

            Ui(() =>
            {
                lblJob1.Text = string.Empty;
                lblJob2.Visible = false;
                lblSpeedValue2.Visible = false;
                lblSizeValue2.Visible = false;
                lblFPSValue2.Visible = false;
                lblBitrateValue2.Visible = false;
                lblTimeValue2.Visible = false;
                lblJobTimer2.Visible = false;
            });
        }
    }
}
