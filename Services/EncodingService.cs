using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Encode.Services
{
    /// <summary>
    /// Encapsulates video encoding logic via FFmpeg with GPU or CPU, optional 10-bit,
    /// deterministic stream mapping, and optional target size budgeting.
    /// </summary>
    public class EncodingService
    {
        private readonly string _appPath;
        private readonly Action<string> _progressCallback;
        private readonly Action<string>? _log;


        private readonly SynchronizationContext? _syncContext;
        // Cache primary audio bitrate per input file (kbps) to avoid repeated ffprobe calls
        private readonly Dictionary<string, double> _audioBitrateKbpsCache =
            new(StringComparer.OrdinalIgnoreCase);

        // Cache durations per input file to avoid repeated ffprobe calls
        private readonly Dictionary<string, TimeSpan> _durationCache =
            new(StringComparer.OrdinalIgnoreCase);

        public enum ScaleMode
        {
            None,
            To720p,
            To1080p,
            To1440p,
            To4K
        }

        public enum StreamMapMode
        {
            // -map 0:v:0 -map 0:a? -map 0:s? -map 0:d?
            KeepAll,

            // -map 0:v:0 -map 0:a:0? (subtitles/data depend on options)
            FirstAudioOnly
        }

        /// <summary>
        /// Structured progress information parsed from ffmpeg output.
        /// </summary>
        public sealed class EncodeProgress
        {
            public TimeSpan CurrentTime { get; }
            public TimeSpan TotalDuration { get; }
            public double Fps { get; }
            public double Speed { get; }
            public double BitrateKbps { get; }
            public double Percent { get; }

            public EncodeProgress(
                TimeSpan currentTime,
                TimeSpan totalDuration,
                double fps,
                double speed,
                double bitrateKbps,
                double percent)
            {
                CurrentTime = currentTime;
                TotalDuration = totalDuration;
                Fps = fps;
                Speed = speed;
                BitrateKbps = bitrateKbps;
                Percent = percent;
            }
        }

        /// <summary>
        /// Event fired when structured progress is parsed from ffmpeg output.
        /// Consumers can subscribe to get time/fps/ETA etc.
        /// </summary>
        public event Action<EncodeProgress>? StructuredProgress;

        public EncodingService(string applicationDirectory, Action<string> progressCallback)
            : this(applicationDirectory, progressCallback, null)
        {
        }

        public EncodingService(
            string applicationDirectory,
            Action<string> progressCallback,
            Action<string>? logCallback)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectory))
                throw new ArgumentException("Application directory must be provided.", nameof(applicationDirectory));

            _appPath = applicationDirectory;
            _progressCallback = progressCallback ?? (_ => { });
            _log = logCallback;

            // Capture the current SynchronizationContext (WinForms UI thread) to marshal progress callbacks safely.
            _syncContext = SynchronizationContext.Current;
        }

        // --------------------------------------------------------------------
        // Public API (preferred overload)
        // --------------------------------------------------------------------
        public Task<bool> EncodeAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb,
            string videoCodec,
            ScaleMode scaleMode,
            string? nvencPreset,
            bool tenBit,
            int? audioChannels,
            Action<string>? progressCallback,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default)
        {
            return EncodeInternalAsync(
                input,
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                videoCodec,
                scaleMode,
                nvencPreset,
                tenBit,
                audioChannels,
                progressCallback ?? _progressCallback,
                mapMode,
                copySubtitles,
                cancellationToken);
        }

        // Compatibility overload (keeps existing call sites where CancellationToken was arg #12)
        public Task<bool> EncodeAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb,
            string videoCodec,
            ScaleMode scaleMode,
            string? nvencPreset,
            bool tenBit,
            int? audioChannels,
            Action<string>? progressCallback,
            CancellationToken cancellationToken)
        {
            return EncodeAsync(
                input,
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                videoCodec,
                scaleMode,
                nvencPreset,
                tenBit,
                audioChannels,
                progressCallback,
                StreamMapMode.KeepAll,
                true,
                cancellationToken);
        }

        // --------------------------------------------------------------------
        // Backwards compatible wrappers used in earlier code paths
        // --------------------------------------------------------------------
        public Task<bool> EncodeAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb)
        {
            return EncodeAsync(input, outputFolder, suffix, useGpu, targetMb, CancellationToken.None);
        }

        public Task<bool> EncodeAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb,
            CancellationToken cancellationToken)
        {
            string defaultCodec = useGpu ? "hevc_nvenc" : "libx265";

            return EncodeInternalAsync(
                input,
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                defaultCodec,
                ScaleMode.None,
                null,
                false,
                null,
                _progressCallback,
                StreamMapMode.KeepAll,
                true,
                cancellationToken);
        }

        public Task<bool> EncodeAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb,
            string videoCodec,
            ScaleMode scaleMode)
        {
            return EncodeAsync(input, outputFolder, suffix, useGpu, targetMb, videoCodec, scaleMode, CancellationToken.None);
        }

        public Task<bool> EncodeAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb,
            string videoCodec,
            ScaleMode scaleMode,
            CancellationToken cancellationToken)
        {
            return EncodeInternalAsync(
                input,
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                videoCodec,
                scaleMode,
                null,
                false,
                null,
                _progressCallback,
                StreamMapMode.KeepAll,
                true,
                cancellationToken);
        }

        // --------------------------------------------------------------------
        // Internal encode implementation
        // --------------------------------------------------------------------
        private async Task<bool> EncodeInternalAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb,
            string videoCodec,
            ScaleMode scaleMode,
            string? nvencPreset,
            bool tenBit,
            int? audioChannels,
            Action<string> callback,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Input file must be provided.", nameof(input));
            if (!File.Exists(input))
                throw new FileNotFoundException("Input file does not exist.", input);

            if (string.IsNullOrWhiteSpace(videoCodec))
                throw new ArgumentException("Video codec must be provided.", nameof(videoCodec));

            string outFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? (Path.GetDirectoryName(input) ?? Environment.CurrentDirectory)
                : outputFolder;

            Directory.CreateDirectory(outFolder);

            string name = Path.GetFileNameWithoutExtension(input);
            string actualSuffix = string.IsNullOrWhiteSpace(suffix) ? string.Empty : suffix;

            // Collision-safe output naming so we don't overwrite existing files
            string output = GetUniqueOutputPath(outFolder, name, actualSuffix, ".mp4");

            bool allowSubtitleCopy = copySubtitles;
            if (copySubtitles && string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                allowSubtitleCopy = false;
                _log?.Invoke("[EncodingService] MP4 output does not support PGS subtitles; disabling subtitle copy.");
            }

            // MP4 cannot mux arbitrary "data" streams (e.g., GPAC hint tracks / RTP) and will fail with:
            //   "Could not find tag for codec none ... codec not currently supported in container"
            // Therefore, disable copying/mapping data streams when targeting MP4.
            bool allowDataCopy = true;
            if (string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                allowDataCopy = false;
                _log?.Invoke("[EncodingService] MP4 output does not support generic data streams; disabling data stream copy.");
            }

            // Total duration once for progress and target bitrate math
            TimeSpan totalDuration = GetVideoDuration(input);
            if (totalDuration <= TimeSpan.Zero)
                _log?.Invoke("[EncodingService] Warning: could not determine duration, progress percent will be 0.");

            string ffArgs = BuildFfmpegArgs(
                input,
                output,
                videoCodec,
                useGpu,
                targetMb,
                scaleMode,
                nvencPreset,
                tenBit,
                audioChannels,
                mapMode,
                allowSubtitleCopy,
                allowDataCopy);

            _log?.Invoke($"[EncodingService] Starting ffmpeg for '{input}' -> '{output}'");
            _log?.Invoke($"[EncodingService] ffmpeg arguments: {ffArgs}");

            var stderrBuilder = new StringBuilder();

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(_appPath, "ffmpeg.exe"),
                Arguments = ffArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };

            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    HandleProgressLine(e.Data, callback, totalDuration);
            };

            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    HandleProgressLine(e.Data, callback, totalDuration);
                    stderrBuilder.AppendLine(e.Data);
                }
            };

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] Failed to start ffmpeg: {ex}");
                throw;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            CancellationTokenRegistration ctr = default;
            try
            {
                if (cancellationToken.CanBeCanceled)
                {
                    ctr = cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                _log?.Invoke("[EncodingService] Cancellation requested. Sending 'q' to ffmpeg.");
                                try
                                {
                                    proc.StandardInput.WriteLine("q");
                                    proc.StandardInput.Flush();
                                }
                                catch (Exception ex)
                                {
                                    _log?.Invoke($"[EncodingService] Failed to send 'q' to ffmpeg: {ex}");
                                }
                            }
                        }
                        catch
                        {
                            // Ignore races/exceptions here
                        }
                    });
                }

                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log?.Invoke("[EncodingService] Encode operation cancelled.");
                throw;
            }
            finally
            {
                ctr.Dispose();
            }

            if (proc.ExitCode != 0)
            {
                string logName = $"{name}_ffmpeg_error_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                string logPath = Path.Combine(outFolder, logName);

                try
                {
                    File.WriteAllText(logPath, stderrBuilder.ToString());
                }
                catch (Exception writeEx)
                {
                    _log?.Invoke($"[EncodingService] Failed to write ffmpeg error log: {writeEx}");
                }

                _log?.Invoke($"[EncodingService] ffmpeg exited with code {proc.ExitCode}. See log: {logName}");
                throw new InvalidOperationException($"ffmpeg exited with code {proc.ExitCode}. See log: {logName}");
            }

            _log?.Invoke("[EncodingService] ffmpeg completed successfully.");
            return true;
        }

        // --------------------------------------------------------------------
        // Progress parsing + forwarding
        // --------------------------------------------------------------------
        private void HandleProgressLine(string line, Action<string> callback, TimeSpan totalDuration)
        {
            // NOTE: FFmpeg stdout/stderr callbacks are raised on background threads.
            // When running concurrent jobs, invoking UI-bound callbacks from those threads can
            // crash WinForms with cross-thread exceptions. Marshal progress notifications onto
            // the captured SynchronizationContext (typically the UI thread).
            void Publish()
            {
                // Preserve existing behavior
                callback(line);

                // Optional logging
                _log?.Invoke($"[ffmpeg] {line}");

                // Structured progress (optional)
                if (StructuredProgress == null)
                    return;

                if (TryParseProgress(line, totalDuration, out var progress))
                {
                    try { StructuredProgress?.Invoke(progress); }
                    catch { /* don't let subscribers break encoding */ }
                }
            }

            if (_syncContext != null)
            {
                _syncContext.Post(_ => Publish(), null);
            }
            else
            {
                // Fallback for non-UI hosts (tests/CLI) where SynchronizationContext may be null.
                Publish();
            }
        }

        private static bool TryParseProgress(string line, TimeSpan totalDuration, out EncodeProgress progress)
        {
            progress = null!;

            if (line.IndexOf("time=", StringComparison.Ordinal) < 0)
                return false;

            try
            {
                string? timeStr = ExtractValue(line, "time=");
                if (string.IsNullOrWhiteSpace(timeStr))
                    return false;

                if (!TryParseTime(timeStr, out var currentTime))
                    return false;

                string? fpsStr = ExtractValue(line, "fps=");
                string? bitrateStr = ExtractValue(line, "bitrate=");
                string? speedStr = ExtractValue(line, "speed=");

                double fps = TryParseDouble(fpsStr);
                double speed = TryParseSpeed(speedStr);
                double bitrateKbps = TryParseBitrateKbps(bitrateStr);

                double percent = 0;
                if (totalDuration > TimeSpan.Zero)
                {
                    percent = Math.Clamp(
                        currentTime.TotalSeconds / totalDuration.TotalSeconds * 100.0,
                        0.0,
                        100.0);
                }

                progress = new EncodeProgress(currentTime, totalDuration, fps, speed, bitrateKbps, percent);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ExtractValue(string line, string key)
        {
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return null;

            idx += key.Length;
            if (idx >= line.Length)
                return null;

            int end = idx;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
                end++;

            return line.Substring(idx, end - idx);
        }

        private static bool TryParseTime(string value, out TimeSpan time) =>
            TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time);

        private static double TryParseDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        private static double TryParseSpeed(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Trim();
            if (value.EndsWith("x", StringComparison.OrdinalIgnoreCase))
                value = value[..^1];

            return TryParseDouble(value);
        }

        private static double TryParseBitrateKbps(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Trim();

            int kIndex = value.IndexOf('k');
            if (kIndex > 0)
                value = value.Substring(0, kIndex);

            return TryParseDouble(value);
        }

        // --------------------------------------------------------------------
        // Argument builder (GPU optimizations + mapping + target-size budgeting)
        // --------------------------------------------------------------------
        private string BuildFfmpegArgs(
            string input,
            string output,
            string videoCodec,
            bool useGpu,
            double? targetMb,
            ScaleMode scaleMode,
            string? nvencPreset,
            bool tenBit,
            int? audioChannels,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            bool copyDataStreams = true)
        {
            bool isNvenc = videoCodec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);
            bool isNvencAv1 = videoCodec.Equals("av1_nvenc", StringComparison.OrdinalIgnoreCase);
            bool isQsv = videoCodec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase);
            bool isQsvAv1 = videoCodec.Equals("av1_qsv", StringComparison.OrdinalIgnoreCase);

            bool wantsTenBit =
                tenBit &&
                (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                 videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                 videoCodec.Contains("av1", StringComparison.OrdinalIgnoreCase));

            string? tenBitPixFmt = wantsTenBit
                ? (isNvenc || isQsv ? "p010le" : "yuv420p10le")
                : null;

            string presetForNvenc = ParseNvencPresetOrDefault(nvencPreset);

            string scaleExpr = scaleMode switch
            {
                ScaleMode.To720p => "-2:720",
                ScaleMode.To1080p => "-2:1080",
                ScaleMode.To1440p => "-2:1440",
                ScaleMode.To4K => "-2:2160",
                _ => string.Empty
            };

            var sb = new StringBuilder();
            sb.Append("-y ");

            // GPU decode logic:
            // - 8-bit NVENC: full GPU frames (cuda + hwaccel_output_format=cuda)
            // - 10-bit NVENC: no hwaccel_output_format, so software filters can safely convert
            // - QSV: use qsv hwaccel when requested.
            // - Non-NVENC + GPU: plain hwaccel cuda.
            if (useGpu)
            {
                if (isNvenc)
                {
                    if (wantsTenBit)
                    {
                        sb.Append("-hwaccel cuda ");
                    }
                    else
                    {
                        sb.Append("-hwaccel cuda -hwaccel_output_format cuda ");
                    }
                }
                else if (isQsv)
                {
                    sb.Append("-hwaccel qsv ");
                }
                else
                {
                    sb.Append("-hwaccel cuda ");
                }
            }

            sb.Append($"-i \"{input}\" ");

            AppendStreamMapping(sb, mapMode, copySubtitles, copyDataStreams);

            // Subtitle codec handling
            if (copySubtitles)
                sb.Append("-c:s copy ");
            else
                sb.Append("-sn ");

            AppendVideoFilters(sb, isNvenc, useGpu, wantsTenBit, tenBitPixFmt, scaleExpr);

            // Video encode
            if (targetMb.HasValue && targetMb > 0)
            {
                AppendVideoEncodeTargetSize(
                    sb,
                    input,
                    videoCodec,
                    isNvenc,
                    isNvencAv1,
                    isQsv,
                    isQsvAv1,
                    wantsTenBit,
                    tenBitPixFmt,
                    presetForNvenc,
                    targetMb.Value,
                    audioChannels);
            }
            else
            {
                AppendVideoEncodeQualityDefault(
                    sb,
                    videoCodec,
                    isNvenc,
                    isQsv,
                    wantsTenBit,
                    tenBitPixFmt,
                    presetForNvenc);
            }

            // Audio handling (Phase A):
            // Default is COPY audio to avoid unnecessary re-encoding.
            // If the caller requests a specific channel count, we must re-encode (downmix/upmix).
            if (audioChannels.HasValue && audioChannels.Value > 0)
            {
                sb.Append("-c:a libfdk_aac -vbr 5 ");
                sb.Append($"-ac {audioChannels.Value} ");
            }
            else
            {
                sb.Append("-c:a copy ");
            }

            sb.Append("-movflags +faststart ");
            sb.Append($"\"{output}\"");

            return sb.ToString();
        }

        private static string ParseNvencPresetOrDefault(string? nvencPreset)
        {
            string presetForNvenc = "p5";
            if (string.IsNullOrWhiteSpace(nvencPreset))
                return presetForNvenc;

            string token = nvencPreset.Trim();
            if (!token.StartsWith("p", StringComparison.OrdinalIgnoreCase))
                return presetForNvenc;

            string first = token.Split(' ')[0];
            return first is "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7"
                ? first
                : presetForNvenc;
        }

        private static void AppendStreamMapping(StringBuilder sb, StreamMapMode mapMode, bool copySubtitles, bool copyDataStreams)
        {
            sb.Append("-map 0:v:0 ");

            if (mapMode == StreamMapMode.KeepAll)
            {
                sb.Append("-map 0:a? ");
                if (copySubtitles)
                    sb.Append("-map 0:s? ");
                if (copyDataStreams)
                    sb.Append("-map 0:d? ");
                else
                    sb.Append("-dn ");
            }
            else
            {
                sb.Append("-map 0:a:0? ");
                if (copySubtitles)
                    sb.Append("-map 0:s? ");
                if (copyDataStreams)
                    sb.Append("-map 0:d? ");
                else
                    sb.Append("-dn ");
            }
        }

        private static void AppendVideoFilters(
            StringBuilder sb,
            bool isNvenc,
            bool useGpu,
            bool wantsTenBit,
            string? tenBitPixFmt,
            string scaleExpr)
        {
            if (!string.IsNullOrEmpty(scaleExpr))
            {
                if (isNvenc && useGpu && !wantsTenBit)
                {
                    // GPU resizer (8-bit only)
                    sb.Append($"-vf scale_cuda={scaleExpr}:interp_algo=lanczos ");
                }
                else
                {
                    // CPU scaling for 10-bit or non-NVENC paths
                    if (wantsTenBit && !string.IsNullOrEmpty(tenBitPixFmt))
                        sb.Append($"-vf scale={scaleExpr}:flags=lanczos,format={tenBitPixFmt} ");
                    else
                        sb.Append($"-vf scale={scaleExpr}:flags=lanczos ");
                }
            }
            else if (wantsTenBit && !string.IsNullOrEmpty(tenBitPixFmt))
            {
                // No scaling, still force 10-bit pixel format
                sb.Append($"-vf format={tenBitPixFmt} ");
            }
        }

        private void AppendVideoEncodeTargetSize(
            StringBuilder sb,
            string input,
            string videoCodec,
            bool isNvenc,
            bool isNvencAv1,
            bool isQsv,
            bool isQsvAv1,
            bool wantsTenBit,
            string? tenBitPixFmt,
            string presetForNvenc,
            double targetMb,
            int? audioChannels)
        {
            TimeSpan duration = GetVideoDuration(input);
            double seconds = duration.TotalSeconds <= 0 ? 1 : duration.TotalSeconds;

            // Phase D: budget bitrate for audio + container overhead so target size is more accurate.
            double totalKbps = (targetMb * 8192d) / seconds;

            double plannedAudioKbps;
            if (audioChannels.HasValue && audioChannels.Value > 0)
            {
                // AAC VBR5 planning budget (conservative)
                plannedAudioKbps = audioChannels.Value >= 6 ? 256 : 192;
            }
            else
            {
                // Audio is copied; use source bitrate when possible (fallback inside helper).
                plannedAudioKbps = GetPrimaryAudioBitrateKbps(input);
            }

            double overheadKbps = Math.Max(16, totalKbps * 0.01);

            double videoKbps = totalKbps - plannedAudioKbps - overheadKbps;
            if (videoKbps < 100)
                videoKbps = 100;

            double maxRate = Math.Round(videoKbps * 1.08);
            double bufSize = Math.Round(videoKbps * 1.4);

            sb.Append($"-c:v {videoCodec} ");

            AppendTenBitFlags(sb, videoCodec, wantsTenBit, tenBitPixFmt);

            if (isNvenc)
            {
                string rcMode = isNvencAv1 ? "vbr" : "vbr_hq";
                sb.Append(
                    $"-b:v {videoKbps:F0}k " +
                    $"-maxrate {maxRate:F0}k " +
                    $"-bufsize {bufSize:F0}k " +
                    $"-rc {rcMode} " +
                    $"-preset {presetForNvenc} ");

                if (isNvencAv1)
                    sb.Append("-cq 28 ");

                // Phase B: NVENC quality knobs
                sb.Append("-rc-lookahead 20 -spatial_aq 1 -temporal_aq 1 -aq-strength 8 ");
            }
            else if (isQsv)
            {
                string rcMode = isQsvAv1 ? "vbr" : "vbr";
                sb.Append(
                    $"-b:v {videoKbps:F0}k " +
                    $"-maxrate {maxRate:F0}k " +
                    $"-bufsize {bufSize:F0}k " +
                    $"-rc_mode {rcMode} ");
            }
            else
            {
                // CPU VBR (predictable size)
                sb.Append($"-b:v {videoKbps:F0}k -preset slow ");
            }
        }

        private static void AppendVideoEncodeQualityDefault(
            StringBuilder sb,
            string videoCodec,
            bool isNvenc,
            bool isQsv,
            bool wantsTenBit,
            string? tenBitPixFmt,
            string presetForNvenc)
        {
            sb.Append($"-c:v {videoCodec} ");

            AppendTenBitFlags(sb, videoCodec, wantsTenBit, tenBitPixFmt);

            if (isNvenc)
            {
                if (videoCodec.Contains("h264", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"-rc vbr_hq -cq 22 -preset {presetForNvenc} ");
                else if (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                         videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"-rc vbr_hq -cq 24 -preset {presetForNvenc} ");
                else
                    sb.Append($"-rc vbr -cq 28 -preset {presetForNvenc} ");

                // Phase B: NVENC quality knobs
                sb.Append("-rc-lookahead 20 -spatial_aq 1 -temporal_aq 1 -aq-strength 8 ");
            }
            else if (isQsv)
            {
                int quality = 23;
                if (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                    videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
                    quality = 25;
                else if (videoCodec.Contains("av1", StringComparison.OrdinalIgnoreCase))
                    quality = 28;

                sb.Append($"-rc_mode icq -global_quality {quality} ");
            }
            else
            {
                if (videoCodec.Equals("libx264", StringComparison.OrdinalIgnoreCase))
                    sb.Append("-crf 23 -preset slow ");
                else if (videoCodec.Equals("libx265", StringComparison.OrdinalIgnoreCase))
                    sb.Append("-crf 24 -preset slow ");
                else
                    sb.Append("-crf 30 -preset 6 ");
            }
        }

        private static void AppendTenBitFlags(StringBuilder sb, string videoCodec, bool wantsTenBit, string? tenBitPixFmt)
        {
            if (!wantsTenBit || string.IsNullOrEmpty(tenBitPixFmt))
                return;

            if (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($"-profile:v main10 -pix_fmt {tenBitPixFmt} ");
            }
            else if (videoCodec.Contains("av1", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($"-pix_fmt {tenBitPixFmt} ");
            }
        }

        // --------------------------------------------------------------------
        // Audio bitrate probe (Phase D helper)
        // --------------------------------------------------------------------
        private double GetPrimaryAudioBitrateKbps(string file)
        {
            try
            {
                lock (_audioBitrateKbpsCache)
                {
                    if (_audioBitrateKbpsCache.TryGetValue(file, out var cached))
                        return cached;
                }
            }
            catch
            {
                // ignore cache lookup issues
            }

            double kbps = 0;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(_appPath, "ffprobe.exe"),
                    Arguments =
                        "-v error -select_streams a:0 " +
                        "-show_entries stream=bit_rate " +
                        "-of default=noprint_wrappers=1:nokey=1 " +
                        $"\"{file}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();

                    if (long.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps) && bps > 0)
                        kbps = bps / 1000d;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] ffprobe audio bitrate failed for '{file}': {ex}");
            }

            // Fallback planning value if bitrate is missing or VBR
            if (kbps <= 0)
                kbps = 160;

            try
            {
                lock (_audioBitrateKbpsCache)
                {
                    _audioBitrateKbpsCache[file] = kbps;
                }
            }
            catch
            {
                // ignore cache store issues
            }

            return kbps;
        }

        // --------------------------------------------------------------------
        // Duration probe (with cache)
        // --------------------------------------------------------------------
        private TimeSpan GetVideoDuration(string file)
        {
            try
            {
                lock (_durationCache)
                {
                    if (_durationCache.TryGetValue(file, out var cached))
                        return cached;
                }
            }
            catch
            {
                // ignore cache lookup issues
            }

            TimeSpan result = TimeSpan.Zero;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(_appPath, "ffprobe.exe"),
                    Arguments =
                        "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 " +
                        $"\"{file}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();

                    if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                        result = TimeSpan.FromSeconds(sec);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] ffprobe failed for '{file}': {ex}");
            }

            try
            {
                lock (_durationCache)
                {
                    _durationCache[file] = result;
                }
            }
            catch
            {
                // ignore cache store issues
            }

            return result;
        }

        // --------------------------------------------------------------------
        // Output file name helpers
        // --------------------------------------------------------------------
        private static string GetUniqueOutputPath(string folder, string baseName, string suffix, string extension)
        {
            string initialName = $"{baseName}{suffix}{extension}";
            string initialPath = Path.Combine(folder, initialName);

            if (!File.Exists(initialPath))
                return initialPath;

            int counter = 1;
            while (true)
            {
                string candidateName = $"{baseName}{suffix} ({counter}){extension}";
                string candidatePath = Path.Combine(folder, candidateName);

                if (!File.Exists(candidatePath))
                    return candidatePath;

                counter++;
            }
        }
    }
}
