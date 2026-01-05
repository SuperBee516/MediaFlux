using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;

namespace Encode.Services
{
    /// <summary>
    /// Encapsulates video encoding logic via FFmpeg with GPU or CPU and optional target size.
    /// </summary>
    public class EncodingService
    {
        private readonly string _appPath;
        private readonly Action<string> _progressCallback;
        private readonly Action<string>? _log;

        // cache durations per input file to avoid repeated ffprobe calls
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

        /// <summary>
        /// Structured progress information parsed from ffmpeg output.
        /// </summary>
        public class EncodeProgress
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

        /// <summary>
        /// Main constructor used by existing code.
        /// </summary>
        public EncodingService(string applicationDirectory, Action<string> progressCallback)
            : this(applicationDirectory, progressCallback, null)
        {
        }

        /// <summary>
        /// Extended constructor with optional logging callback.
        /// </summary>
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
        }

        // -------------------------------------------------------
        // PUBLIC ENCODE WRAPPER (with callback)
        // -------------------------------------------------------
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
    int? audioChannels)
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
                _progressCallback,
                CancellationToken.None
            );
        }

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
            Action<string> progressCallback)
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
                progressCallback,
                CancellationToken.None
            );
        }


        // -------------------------------------------------------
        // BACKWARDS COMPATIBLE WRAPPERS (used earlier in project)
        // -------------------------------------------------------
        public Task<bool> EncodeAsync(
            string input,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb)
        {
            return EncodeAsync(
                input,
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                CancellationToken.None);
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
                cancellationToken
            );
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
            return EncodeAsync(
                input,
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                videoCodec,
                scaleMode,
                CancellationToken.None);
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
                cancellationToken
            );
        }

        // -------------------------------------------------------
        // INTERNAL ENCODE IMPLEMENTATION
        // -------------------------------------------------------
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
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Input file must be provided.", nameof(input));
            if (!File.Exists(input))
                throw new FileNotFoundException("Input file does not exist.", input);

            if (string.IsNullOrWhiteSpace(videoCodec))
                throw new ArgumentException("Video codec must be provided.", nameof(videoCodec));

            string outFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.GetDirectoryName(input) ?? Environment.CurrentDirectory
                : outputFolder;

            Directory.CreateDirectory(outFolder);

            string name = Path.GetFileNameWithoutExtension(input);
            string actualSuffix = string.IsNullOrWhiteSpace(suffix) ? "_2" : suffix;

            // Use collision-safe output naming so we don't overwrite existing files
            string output = GetUniqueOutputPath(outFolder, name, actualSuffix, ".mp4");

            // Get total duration once for progress + bitrate estimation
            TimeSpan totalDuration = GetVideoDuration(input);
            if (totalDuration <= TimeSpan.Zero)
            {
                _log?.Invoke("[EncodingService] Warning: could not determine duration, progress percent will be 0.");
            }

            string ffArgs = BuildFfmpegArgs(
                input,
                output,
                videoCodec,
                useGpu,
                targetMb,
                scaleMode,
                nvencPreset,
                tenBit,
                audioChannels
            );

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

            using var proc = new Process
            {
                StartInfo = psi
            };

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
                                    // Graceful shutdown: ask ffmpeg to quit
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
                            // ignore any races/exceptions here
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

                throw new InvalidOperationException(
                    $"ffmpeg exited with code {proc.ExitCode}. See log: {logName}");
            }

            _log?.Invoke("[EncodingService] ffmpeg completed successfully.");
            return true;
        }

        // Central point for handling all ffmpeg output lines.
        // Preserves existing behavior (string callback) and optionally emits structured progress.
        private void HandleProgressLine(string line, Action<string> callback, TimeSpan totalDuration)
        {
            // Preserve existing behavior
            callback(line);

            // Optional logging
            _log?.Invoke($"[ffmpeg] {line}");

            // Try to parse progress line for structured metrics
            if (StructuredProgress == null)
                return;

            if (TryParseProgress(line, totalDuration, out var progress))
            {
                try
                {
                    StructuredProgress?.Invoke(progress);
                }
                catch
                {
                    // Do not let subscriber exceptions break encoding
                }
            }
        }

        private static bool TryParseProgress(string line, TimeSpan totalDuration, out EncodeProgress progress)
        {
            progress = null!;

            // Typical ffmpeg progress line example:
            // frame=  240 fps=30 q=24.0 size=   1024kB time=00:00:10.00 bitrate= 838.9kbits/s speed=1.01x
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

                progress = new EncodeProgress(
                    currentTime,
                    totalDuration,
                    fps,
                    speed,
                    bitrateKbps,
                    percent);

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

        private static bool TryParseTime(string value, out TimeSpan time)
        {
            // ffmpeg typically prints as HH:MM:SS.xx with variable decimals
            // Use TimeSpan.Parse as it is fairly tolerant.
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time);
        }

        private static double TryParseDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;

            return 0;
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

            // Expect something like "838.9kbits/s" or "838.9kbit/s"
            int kIndex = value.IndexOf('k');
            if (kIndex > 0)
                value = value.Substring(0, kIndex);

            return TryParseDouble(value);
        }

        // -------------------------------------------------------
        // ARGUMENT BUILDER (WITH GPU OPTIMIZATIONS)
        // -------------------------------------------------------
        private string BuildFfmpegArgs(
            string input,
            string output,
            string videoCodec,
            bool useGpu,
            double? targetMb,
            ScaleMode scaleMode,
            string? nvencPreset,
            bool tenBit,
            int? audioChannels
        )
        {
            bool isNvenc = videoCodec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);
            bool isNvencAv1 = videoCodec.Equals("av1_nvenc", StringComparison.OrdinalIgnoreCase);

            bool wantsTenBit =
                tenBit &&
                (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                 videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                 videoCodec.Contains("av1", StringComparison.OrdinalIgnoreCase));
            string? tenBitPixFmt = wantsTenBit
                ? (isNvenc ? "p010le" : "yuv420p10le")
                : null;

            string presetForNvenc = "p5";
            if (!string.IsNullOrWhiteSpace(nvencPreset))
            {
                string token = nvencPreset.Trim();
                if (token.StartsWith("p", StringComparison.OrdinalIgnoreCase))
                {
                    string first = token.Split(' ')[0];
                    if (first is "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7")
                        presetForNvenc = first;
                }
            }

            var sb = new StringBuilder();
            sb.Append("-y ");

            // GPU decode logic:
            // - 8-bit NVENC: full GPU frames (cuda + hwaccel_output_format=cuda)
            // - 10-bit NVENC: use CUDA to assist decode, but keep frames in system memory
            //                 (no hwaccel_output_format), so software can do the 8->10bit
            //                 conversion without hitting the auto_scale limitation.
            // - Non-NVENC + GPU: plain hwaccel cuda.
            if (useGpu)
            {
                if (isNvenc)
                {
                    if (wantsTenBit)
                    {
                        // 10-bit HEVC/AV1 encode: safer path
                        sb.Append("-hwaccel cuda ");
                    }
                    else
                    {
                        // 8-bit NVENC encode: full GPU path
                        sb.Append("-hwaccel cuda -hwaccel_output_format cuda ");
                    }
                }
                else
                {
                    sb.Append("-hwaccel cuda ");
                }
            }

            sb.Append($"-i \"{input}\" ");

            string scaleExpr = scaleMode switch
            {
                ScaleMode.To720p => "-2:720",
                ScaleMode.To1080p => "-2:1080",
                ScaleMode.To1440p => "-2:1440",
                ScaleMode.To4K => "-2:2160",
                _ => string.Empty
            };

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
                sb.Append($"-vf format={tenBitPixFmt} ");
            }

            if (targetMb.HasValue && targetMb > 0)
            {
                TimeSpan duration = GetVideoDuration(input);
                double seconds = duration.TotalSeconds <= 0 ? 1 : duration.TotalSeconds;

                double kbps = (targetMb.Value * 8192d) / seconds;
                double maxRate = Math.Round(kbps * 1.08);
                double bufSize = Math.Round(kbps * 1.4);

                sb.Append($"-c:v {videoCodec} ");

                if (wantsTenBit && !string.IsNullOrEmpty(tenBitPixFmt))
                {
                    if (videoCodec.Contains("hevc") || videoCodec.Contains("265"))
                        sb.Append($"-profile:v main10 -pix_fmt {tenBitPixFmt} ");
                    else if (videoCodec.Contains("av1"))
                        sb.Append($"-pix_fmt {tenBitPixFmt} ");
                }

                if (isNvenc)
                {
                    string rcMode = isNvencAv1 ? "vbr" : "vbr_hq";
                    sb.Append(
                        $"-b:v {kbps:F0}k " +
                        $"-maxrate {maxRate:F0}k " +
                        $"-bufsize {bufSize:F0}k " +
                        $"-rc {rcMode} " +
                        $"-preset {presetForNvenc} "
                    );

                    if (isNvencAv1)
                        sb.Append("-cq 28 ");
                }
                else
                {
                    sb.Append($"-b:v {kbps:F0}k -preset slow ");
                }
            }
            else
            {
                sb.Append($"-c:v {videoCodec} ");

                if (wantsTenBit && !string.IsNullOrEmpty(tenBitPixFmt))
                {                    
                    if (videoCodec.Contains("hevc") || videoCodec.Contains("265"))
                        sb.Append($"-profile:v main10 -pix_fmt {tenBitPixFmt} ");
                    else if (videoCodec.Contains("av1"))
                        sb.Append($"-pix_fmt {tenBitPixFmt} ");
                }

                if (isNvenc)
                {
                    if (videoCodec.Contains("h264"))
                        sb.Append($"-rc vbr_hq -cq 22 -preset {presetForNvenc} ");
                    else if (videoCodec.Contains("hevc") || videoCodec.Contains("265"))
                        sb.Append($"-rc vbr_hq -cq 24 -preset {presetForNvenc} ");
                    else
                        sb.Append($"-rc vbr -cq 28 -preset {presetForNvenc} ");
                }
                else
                {
                    if (videoCodec.Equals("libx264"))
                        sb.Append("-crf 23 -preset slow ");
                    else if (videoCodec.Equals("libx265"))
                        sb.Append("-crf 24 -preset slow ");
                    else
                        sb.Append("-crf 30 -preset 6 ");
                }
            }

            sb.Append("-c:a libfdk_aac -vbr 5 ");
            if (audioChannels.HasValue && audioChannels.Value > 0)
                sb.Append($"-ac {audioChannels.Value} ");

            sb.Append("-movflags +faststart ");

            sb.Append($"\"{output}\"");
            return sb.ToString();
        }

        // -------------------------------------------------------
        // DURATION PROBE (with simple cache)
        // -------------------------------------------------------
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
                // If cache lookup somehow fails, fall back to probe.
            }

            TimeSpan result = TimeSpan.Zero;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(_appPath, "ffprobe.exe"),
                    Arguments = $"-v error -show_entries format=duration -of " +
                                "default=noprint_wrappers=1:nokey=1 " +
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

                    if (double.TryParse(
                        output.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var sec))
                    {
                        result = TimeSpan.FromSeconds(sec);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] ffprobe failed for '{file}': {ex}");
                // Ignore and fall through with TimeSpan.Zero
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
                // If cache store fails, just ignore.
            }

            return result;
        }

        // -------------------------------------------------------
        // OUTPUT FILE NAME HELPERS
        // -------------------------------------------------------
        private static string GetUniqueOutputPath(
            string folder,
            string baseName,
            string suffix,
            string extension)
        {
            // baseName + suffix + extension, e.g. "video" + "_2" + ".mp4"
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
