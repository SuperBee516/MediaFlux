using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    /// <summary>
    /// Encapsulates video encoding logic via FFmpeg with GPU or CPU, optional 10-bit,
    /// deterministic stream mapping, and optional target size budgeting.
    /// </summary>
    public class EncodingService
    {
        private const int MaxCapturedFfmpegCharacters = 512 * 1024;
        private readonly string _appPath;
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;
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

        public sealed class EncodeResult
        {
            public bool Success { get; }
            public string OutputPath { get; }
            public string DiagnosticArguments { get; }

            public EncodeResult(
                bool success,
                string outputPath,
                string diagnosticArguments = "")
            {
                Success = success;
                OutputPath = outputPath;
                DiagnosticArguments = diagnosticArguments;
            }
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
            Action<string>? logCallback,
            string? ffmpegPath = null,
            string? ffprobePath = null)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectory))
                throw new ArgumentException("Application directory must be provided.", nameof(applicationDirectory));

            _appPath = applicationDirectory;
            var tools = FfmpegToolResolver.Resolve(applicationDirectory, ffmpegPath, ffprobePath);
            _ffmpegPath = tools.FfmpegPath;
            _ffprobePath = tools.FfprobePath;
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
            bool concurrentNvenc = false,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default)
        {
            return EncodeSuccessAsync(EncodeInternalAsync(
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
                concurrentNvenc,
                mapMode,
                copySubtitles,
                cancellationToken));
        }

        public Task<EncodeResult> EncodeWithResultAsync(
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
            bool concurrentNvenc = false,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default,
            Action<string>? outputPathCallback = null)
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
                concurrentNvenc,
                mapMode,
                copySubtitles,
                cancellationToken,
                outputPathCallback);
        }

        public Task<EncodeResult> EncodeWithResultAsync(
            EncodingInputSource input,
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
            bool concurrentNvenc = false,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default,
            Action<string>? outputPathCallback = null)
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
                concurrentNvenc,
                mapMode,
                copySubtitles,
                cancellationToken,
                outputPathCallback);
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
                false,
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

            return EncodeSuccessAsync(EncodeInternalAsync(
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
                false,
                StreamMapMode.KeepAll,
                true,
                cancellationToken));
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
            return EncodeSuccessAsync(EncodeInternalAsync(
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
                false,
                StreamMapMode.KeepAll,
                true,
                cancellationToken));
        }

        // --------------------------------------------------------------------
        // Internal encode implementation
        // --------------------------------------------------------------------
        private static async Task<bool> EncodeSuccessAsync(Task<EncodeResult> task)
        {
            return (await task.ConfigureAwait(false)).Success;
        }

        private Task<EncodeResult> EncodeInternalAsync(
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
            bool concurrentNvenc,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default,
            Action<string>? outputPathCallback = null)
        {
            return EncodeInternalAsync(
                EncodingInputSource.FromFile(input),
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                videoCodec,
                scaleMode,
                nvencPreset,
                tenBit,
                audioChannels,
                callback,
                concurrentNvenc,
                mapMode,
                copySubtitles,
                cancellationToken,
                outputPathCallback);
        }

        private async Task<EncodeResult> EncodeInternalAsync(
            EncodingInputSource inputSource,
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
            bool concurrentNvenc,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default,
            Action<string>? outputPathCallback = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ArgumentNullException.ThrowIfNull(inputSource);
            string input = inputSource.InputPath;
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Input file must be provided.", nameof(inputSource));
            ValidateInputExists(inputSource);

            if (string.IsNullOrWhiteSpace(videoCodec))
                throw new ArgumentException("Video codec must be provided.", nameof(videoCodec));

            string outFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? (Path.GetDirectoryName(inputSource.SourcePath) ??
                   Path.GetDirectoryName(input) ??
                   Environment.CurrentDirectory)
                : outputFolder;

            Directory.CreateDirectory(outFolder);

            string name = string.IsNullOrWhiteSpace(inputSource.OutputBaseName)
                ? Path.GetFileNameWithoutExtension(input)
                : inputSource.OutputBaseName;
            string actualSuffix = string.IsNullOrWhiteSpace(suffix) ? string.Empty : suffix;

            // Collision-safe output naming so we don't overwrite existing files
            string output = GetUniqueOutputPath(outFolder, name, actualSuffix, ".mp4");
            outputPathCallback?.Invoke(output);
            bool isAsfFamilyInput =
                inputSource.Kind == EncodingInputKind.File &&
                IsAsfFamilyInput(inputSource.SourcePath);

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

            bool forceMp4CompatibleAudio = isAsfFamilyInput &&
                string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase);
            if (forceMp4CompatibleAudio)
            {
                _log?.Invoke("[EncodingService] WMV/ASF input detected for MP4 output; transcoding audio to AAC.");
            }

            // Total duration once for progress and target bitrate math
            TimeSpan totalDuration = inputSource.KnownDurationSeconds is > 0
                ? TimeSpan.FromSeconds(inputSource.KnownDurationSeconds.Value)
                : GetVideoDuration(input);
            if (totalDuration <= TimeSpan.Zero)
                _log?.Invoke("[EncodingService] Warning: could not determine duration, progress percent will be 0.");

            string ffArgs = BuildFfmpegArgs(
                inputSource,
                output,
                videoCodec,
                useGpu,
                targetMb,
                scaleMode,
                nvencPreset,
                tenBit,
                audioChannels,
                concurrentNvenc,
                mapMode,
                allowSubtitleCopy,
                allowDataCopy,
                forceMp4CompatibleAudio,
                totalDuration);

            _log?.Invoke(
                $"[EncodingService] Starting ffmpeg for '{inputSource.SourcePath}' " +
                $"using '{input}' -> '{output}'");
            _log?.Invoke($"[EncodingService] ffmpeg arguments: {ffArgs}");

            var stderrBuilder = new StringBuilder();

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = ffArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
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
                    AppendBounded(stderrBuilder, e.Data, MaxCapturedFfmpegCharacters);
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
                await EnsureProcessExitedAfterCancellationAsync(proc).ConfigureAwait(false);
                throw;
            }
            finally
            {
                ctr.Dispose();
            }

            if (proc.ExitCode != 0)
            {
                string logPath = ErrorLogService.Append(
                    _appPath,
                    "FFmpeg encode failed",
                    inputSource.SourcePath,
                    details:
                    $"Output     : {output}{Environment.NewLine}" +
                    $"Exit Code  : {proc.ExitCode}{Environment.NewLine}" +
                    $"Arguments  : {ffArgs}{Environment.NewLine}{Environment.NewLine}" +
                    "FFmpeg Output:" + Environment.NewLine +
                    stderrBuilder);

                _log?.Invoke($"[EncodingService] ffmpeg exited with code {proc.ExitCode}. See central log: {logPath}");
                throw new InvalidOperationException($"ffmpeg exited with code {proc.ExitCode}. See central log: {logPath}");
            }

            _log?.Invoke("[EncodingService] ffmpeg completed successfully.");
            return new EncodeResult(true, output, ffArgs);
        }

        private static void AppendBounded(StringBuilder builder, string line, int maxCharacters)
        {
            if (builder.Length >= maxCharacters)
                return;

            int available = maxCharacters - builder.Length;
            if (line.Length <= available)
                builder.AppendLine(line);
            else
            {
                builder.Append(line.AsSpan(0, available));
                builder.AppendLine();
                builder.AppendLine("[Additional FFmpeg diagnostic output truncated by MediaFlux.]");
            }
        }

        private async Task EnsureProcessExitedAfterCancellationAsync(Process proc)
        {
            try
            {
                if (proc.HasExited)
                    return;

                var waitTask = proc.WaitForExitAsync();
                var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                if (completed == waitTask)
                {
                    await waitTask.ConfigureAwait(false);
                    return;
                }

                if (!proc.HasExited)
                {
                    _log?.Invoke("[EncodingService] FFmpeg did not exit after graceful cancel; killing launched process.");
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] Failed while finalizing cancelled FFmpeg process: {ex}");
            }
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
            EncodingInputSource input,
            string output,
            string videoCodec,
            bool useGpu,
            double? targetMb,
            ScaleMode scaleMode,
            string? nvencPreset,
            bool tenBit,
            int? audioChannels,
            bool concurrentNvenc,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            bool copyDataStreams = true,
            bool forceMp4CompatibleAudio = false,
            TimeSpan knownDuration = default)
        {
            bool isNvenc = videoCodec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);
            bool isNvencAv1 = videoCodec.Equals("av1_nvenc", StringComparison.OrdinalIgnoreCase);
            bool isQsv = videoCodec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase);
            bool isQsvAv1 = videoCodec.Equals("av1_qsv", StringComparison.OrdinalIgnoreCase);
            bool isAsfFamilyInput =
                input.Kind == EncodingInputKind.File &&
                IsAsfFamilyInput(input.SourcePath);

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

            // QSV hwaccel decode (h264_qsv/hevc_qsv decoders) produces hardware frames.
            // That breaks *CPU* filters like scale/format unless we explicitly hwdownload/vpp_qsv.
            // For now, only enable QSV hwaccel decode when we are not inserting any video filters.
            // (Encoding still uses *_qsv, so we still get hardware encode speed.)
            bool qsvHwDecodeOk =
                useGpu &&
                isQsv &&
                string.IsNullOrEmpty(scaleExpr) &&
                !wantsTenBit;

            var sb = new StringBuilder();
            sb.Append("-y ");

            // GPU decode logic:
            // - 8-bit NVENC: full GPU frames (cuda + hwaccel_output_format=cuda)
            // - 10-bit NVENC: no hwaccel_output_format, so software filters can safely convert
            // - QSV: use qsv hwaccel when requested.
            // - Non-NVENC + GPU: plain hwaccel cuda.
            if (useGpu && !isAsfFamilyInput)
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
                    if (qsvHwDecodeOk)
                        sb.Append("-hwaccel qsv ");
                }
                else
                {
                    sb.Append("-hwaccel cuda ");
                }
            }
            else if (useGpu && isAsfFamilyInput)
            {
                _log?.Invoke("[EncodingService] WMV/ASF input detected; using software decode before selected hardware encode.");
            }

            AppendInput(sb, input);

            AppendStreamMapping(sb, input, mapMode, copySubtitles, copyDataStreams);

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
                    knownDuration,
                    videoCodec,
                    isNvenc,
                    isNvencAv1,
                    isQsv,
                    isQsvAv1,
                    wantsTenBit,
                    tenBitPixFmt,
                    presetForNvenc,
                    targetMb.Value,
                    audioChannels,
                    concurrentNvenc,
                    forceMp4CompatibleAudio);
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
                    presetForNvenc,
                    concurrentNvenc);
            }

            // Audio handling (Phase A):
            // Default is COPY audio to avoid unnecessary re-encoding.
            // If the caller requests a specific channel count, we must re-encode (downmix/upmix).
            if (audioChannels.HasValue && audioChannels.Value > 0)
            {
                sb.Append("-c:a aac -b:a 192k ");
                sb.Append($"-ac {audioChannels.Value} ");
            }
            else if (forceMp4CompatibleAudio)
            {
                sb.Append("-c:a aac -b:a 192k ");
            }
            else
            {
                sb.Append("-c:a copy ");
            }

            sb.Append("-movflags +faststart ");
            sb.Append($"\"{output}\"");

            return sb.ToString();
        }

        internal static string BuildInputAndMappingArgumentsForTesting(
            EncodingInputSource input,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            bool copyDataStreams = true)
        {
            var builder = new StringBuilder();
            AppendInput(builder, input);
            AppendStreamMapping(
                builder,
                input,
                mapMode,
                copySubtitles,
                copyDataStreams);
            return builder.ToString().Trim();
        }

        private static void AppendInput(StringBuilder builder, EncodingInputSource input)
        {
            if (input.Kind == EncodingInputKind.DvdPhysicalConcat)
                builder.Append("-fflags +genpts ");

            builder.Append("-i \"");
            builder.Append(input.InputPath);
            builder.Append("\" ");
        }

        private static void ValidateInputExists(EncodingInputSource input)
        {
            if (input.Kind == EncodingInputKind.File)
            {
                if (!File.Exists(input.InputPath))
                    throw new FileNotFoundException(
                        "Input file does not exist.",
                        input.InputPath);
                return;
            }

            if (input.Kind != EncodingInputKind.DvdPhysicalConcat)
                throw new InvalidOperationException($"Unsupported input kind: {input.Kind}.");
            if (input.SourceFiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "The DVD physical input does not contain any source segments.");
            }

            string? missing = input.SourceFiles.FirstOrDefault(path => !File.Exists(path));
            if (missing != null)
                throw new FileNotFoundException("A DVD program segment is missing.", missing);
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

        private static void AppendStreamMapping(
            StringBuilder sb,
            EncodingInputSource input,
            StreamMapMode mapMode,
            bool copySubtitles,
            bool copyDataStreams)
        {
            if (input.HasExplicitStreamSelection)
            {
                foreach (int streamIndex in input.VideoStreamIndexes)
                    sb.Append($"-map 0:{streamIndex} ");
                foreach (int streamIndex in input.AudioStreamIndexes)
                    sb.Append($"-map 0:{streamIndex} ");
                if (copySubtitles)
                {
                    foreach (int streamIndex in input.SubtitleStreamIndexes)
                        sb.Append($"-map 0:{streamIndex} ");
                }

                if (!copyDataStreams)
                    sb.Append("-dn ");
                return;
            }

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
            EncodingInputSource input,
            TimeSpan duration,
            string videoCodec,
            bool isNvenc,
            bool isNvencAv1,
            bool isQsv,
            bool isQsvAv1,
            bool wantsTenBit,
            string? tenBitPixFmt,
            string presetForNvenc,
            double targetMb,
            int? audioChannels,
            bool concurrentNvenc,
            bool forceMp4CompatibleAudio)
        {
            if (duration <= TimeSpan.Zero)
            {
                _log?.Invoke("[EncodingService] Target-size bitrate budgeting skipped because input duration could not be determined; using quality-based encoding instead.");
                AppendVideoEncodeQualityDefault(
                    sb,
                    videoCodec,
                    isNvenc,
                    isQsv,
                    wantsTenBit,
                    tenBitPixFmt,
                    presetForNvenc,
                    concurrentNvenc);
                return;
            }

            double seconds = duration.TotalSeconds;

            // Phase D: budget bitrate for audio + container overhead so target size is more accurate.
            double totalKbps = (targetMb * 8192d) / seconds;

            double plannedAudioKbps;
            if (audioChannels.HasValue && audioChannels.Value > 0)
            {
                plannedAudioKbps = audioChannels.Value >= 6 ? 384 : 192;
            }
            else if (forceMp4CompatibleAudio)
            {
                plannedAudioKbps = 192;
            }
            else
            {
                // Audio is copied; use source bitrate when possible (fallback inside helper).
                plannedAudioKbps = input.KnownAudioBitrateKbps is > 0
                    ? input.KnownAudioBitrateKbps.Value
                    : GetPrimaryAudioBitrateKbps(input.InputPath);
            }

            double overheadKbps = Math.Max(16, totalKbps * 0.01);

            double videoKbps = totalKbps - plannedAudioKbps - overheadKbps;
            if (videoKbps < 100)
                videoKbps = 100;

            double maxRate = Math.Round(videoKbps * 1.08);
            double bufSize = Math.Round(videoKbps * 1.4);

            _log?.Invoke(
                $"[EncodingService] Target bitrate plan: target={targetMb:0.##} MB, duration={seconds:0.##} sec, " +
                $"total={totalKbps:0} kbps, audio={plannedAudioKbps:0} kbps, video={videoKbps:0} kbps.");

            sb.Append($"-c:v {videoCodec} ");

            AppendTenBitFlags(sb, videoCodec, wantsTenBit, tenBitPixFmt);

            if (isNvenc)
            {
                const string rcMode = "vbr";
                sb.Append(
                    $"-b:v {videoKbps:F0}k " +
                    $"-maxrate {maxRate:F0}k " +
                    $"-bufsize {bufSize:F0}k " +
                    $"-rc {rcMode} " +
                    $"-preset {presetForNvenc} ");

                if (isNvencAv1)
                    sb.Append("-cq 28 ");

                AppendNvencTuningOptions(sb, videoCodec, videoCodec.Contains("av1", StringComparison.OrdinalIgnoreCase), concurrentNvenc);
            }
            else if (isQsv)
            {
                string rcMode = isQsvAv1 ? "vbr" : "vbr";
                sb.Append(
                    $"-b:v {videoKbps:F0}k " +
                    $"-maxrate {maxRate:F0}k " +
                    $"-bufsize {bufSize:F0}k " +
                    $"-rc_mode {rcMode} " +
                    "-preset slow ");

                // Optional subjective quality improvement for HEVC QSV.
                if (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                    videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-mbbrc 1 ");
                }
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
            string presetForNvenc,
            bool concurrentNvenc)
        {
            sb.Append($"-c:v {videoCodec} ");

            AppendTenBitFlags(sb, videoCodec, wantsTenBit, tenBitPixFmt);

            if (isNvenc)
            {
                if (videoCodec.Contains("h264", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"-rc vbr -cq 22 -preset {presetForNvenc} ");
                else if (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                         videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"-rc vbr -cq 24 -preset {presetForNvenc} ");
                else
                    sb.Append($"-rc vbr -cq 28 -preset {presetForNvenc} ");

                AppendNvencTuningOptions(sb, videoCodec, videoCodec.Contains("av1", StringComparison.OrdinalIgnoreCase), concurrentNvenc);
            }
            else if (isQsv)
            {
                // QSV: global_quality behaves like a CRF-ish knob; lower generally means higher quality.
                // Empirically, values around ~18-22 are reasonable defaults for hevc_qsv.
                int quality = 20;
                if (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                    videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
                    quality = 19;
                else if (videoCodec.Contains("av1", StringComparison.OrdinalIgnoreCase))
                    quality = 28;

                // mbbrc can improve subjective quality on QSV at a small performance cost.
                // (Only apply it to HEVC where it tends to be most noticeable.)
                if (videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                    videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"-rc_mode icq -global_quality {quality} -preset slow -mbbrc 1 ");
                else
                    sb.Append($"-rc_mode icq -global_quality {quality} -preset slow ");
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

        private static void AppendNvencTuningOptions(StringBuilder sb, string videoCodec, bool isAv1Nvenc, bool concurrentNvenc)
        {
            sb.Append("-tune hq ");

            if (concurrentNvenc)
            {
                // Dual-session mode needs a lighter frame buffer footprint to avoid NVENC OOM.
                sb.Append("-rc-lookahead 12 -spatial_aq 1 -temporal_aq 1 -aq-strength 8 ");
                sb.Append("-surfaces 24 ");
            }
            else
            {
                sb.Append("-rc-lookahead 32 -spatial_aq 1 -temporal_aq 1 -aq-strength 12 ");
                sb.Append("-surfaces 48 ");

                if (!isAv1Nvenc)
                    sb.Append("-multipass fullres ");
            }

            if (videoCodec.Contains("h264", StringComparison.OrdinalIgnoreCase) ||
                videoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
            {
                if (concurrentNvenc)
                    sb.Append("-bf 3 -b_ref_mode middle -refs 3 ");
                else
                    sb.Append("-bf 4 -b_ref_mode middle -refs 4 ");
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

        private static bool IsAsfFamilyInput(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".wmv", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".asf", StringComparison.OrdinalIgnoreCase);
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
                string output = RunFfprobeText(
                    "-v error -select_streams a:0 " +
                    "-show_entries stream=bit_rate " +
                    "-of default=noprint_wrappers=1:nokey=1 " +
                    $"\"{file}\"",
                    file);

                if (long.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps) && bps > 0)
                    kbps = bps / 1000d;
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
                string output = RunFfprobeText(
                    "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 " +
                    $"\"{file}\"",
                    file);

                if (TryParseDurationSeconds(output, out var sec))
                    result = TimeSpan.FromSeconds(sec);

                if (result <= TimeSpan.Zero)
                {
                    output = RunFfprobeText(
                        "-v error -select_streams v:0 -show_entries stream=duration " +
                        "-of default=noprint_wrappers=1:nokey=1 " +
                        $"\"{file}\"",
                        file);

                    if (TryParseDurationSeconds(output, out sec))
                        result = TimeSpan.FromSeconds(sec);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] ffprobe failed for '{file}': {ex}");
            }

            if (result > TimeSpan.Zero)
            {
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
            }
            else
            {
                _log?.Invoke($"[EncodingService] Duration probe returned no usable duration for '{file}'.");
            }

            return result;
        }

        private static bool TryParseDurationSeconds(string output, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(output))
                return false;

            foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                {
                    seconds = value;
                    return true;
                }
            }

            return false;
        }

        private string RunFfprobeText(string arguments, string file)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi);
            if (p == null)
                return string.Empty;

            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errorTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(30000))
            {
                try
                {
                    _log?.Invoke($"[EncodingService] ffprobe timed out for '{file}'.");
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort cleanup only
                }

                return string.Empty;
            }

            p.WaitForExit();
            _ = errorTask.GetAwaiter().GetResult();
            return outputTask.GetAwaiter().GetResult();
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




