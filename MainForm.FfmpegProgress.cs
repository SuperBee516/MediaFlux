using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Encode
{
    public partial class MainForm : Form
    {
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

                Ui(() => UpdateEncodeMetrics(fps, sizeKiB, bitrate, speed, timeStr));
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
                    Ui(() => UpdateEncodeMetrics(0, sizeKiB, bitrate, speed, timeStr));
                }
            }

            // No grid/ETA updates here anymore – that’s handled per-row and per-job.
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

        // Updates all labels and progress bar (thread-safe)
        private void UpdateEncodeMetrics(int fps, int sizeKiB, double bitrate, double speed, string timeStr)
        {
            // Text metrics are still fine to update even with multiple jobs
            SetLabel(lblSpeedValue, $"{speed:F1}x");
            SetLabel(lblSizeValue, $"{sizeKiB:N0} KiB");
            SetLabel(lblFPSValue, fps.ToString());
            SetLabel(lblBitrateValue, $"{bitrate:F1} kbits/s");
            SetLabel(lblTimeValue, timeStr);

            // Decide how to drive the big progress bar
            var maxParallel = GetMaxConcurrentEncodes();   // already exists in MainForm
            bool singleEncode = maxParallel <= 1;

            if (singleEncode)
            {
                // Normal, per-job percentage
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
            else
            {
                // When multiple encodes are active, a single percentage is misleading.
                // Show the bar in "indeterminate" mode instead of trying to flip between jobs.
                if (progressBarEncode.Style != ProgressBarStyle.Marquee)
                {
                    progressBarEncode.Style = ProgressBarStyle.Marquee;
                    progressBarEncode.MarqueeAnimationSpeed = 30; // adjust to taste
                }
            }
        }

        // Resets metrics panel to "--" and progress to 0 and job timer
        private void ResetEncodeMetrics()
        {
            SetLabel(lblSpeedValue, "--");
            SetLabel(lblSizeValue, "--");
            SetLabel(lblFPSValue, "--");
            SetLabel(lblBitrateValue, "--");
            SetLabel(lblTimeValue, "--");

            // Always reset to a normal, non-animated bar when idle
            if (progressBarEncode.Style != ProgressBarStyle.Continuous)
                progressBarEncode.Style = ProgressBarStyle.Continuous;

            SetProgress(progressBarEncode, 0);
            ResetJobTimer();
        }
    }
}
