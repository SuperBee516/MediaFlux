using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaFlux.Models;
using MediaFlux.Services.Encoders;

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
        private readonly IEncodeOutputFinalizationService _finalizationService;


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
            public bool FinalizationSucceeded { get; }
            public string StagingPath { get; }
            public string ValidationSummary { get; }
            public long? FinalOutputSizeBytes { get; }
            public OutputContainerSelection RequestedOutputContainer { get; }
            public OutputContainer ResolvedOutputContainer { get; }
            public string ContainerDecisionReason { get; }

            public EncodeResult(
                bool success,
                string outputPath,
                string diagnosticArguments = "",
                bool finalizationSucceeded = false,
                string stagingPath = "",
                string validationSummary = "",
                long? finalOutputSizeBytes = null,
                OutputContainerSelection requestedOutputContainer = OutputContainerSelection.Mp4,
                OutputContainer resolvedOutputContainer = OutputContainer.Mp4,
                string containerDecisionReason = "")
            {
                Success = success;
                OutputPath = outputPath;
                DiagnosticArguments = diagnosticArguments;
                FinalizationSucceeded = finalizationSucceeded;
                StagingPath = stagingPath;
                ValidationSummary = validationSummary;
                FinalOutputSizeBytes = finalOutputSizeBytes;
                RequestedOutputContainer = requestedOutputContainer;
                ResolvedOutputContainer = resolvedOutputContainer;
                ContainerDecisionReason = containerDecisionReason;
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
            string? ffprobePath = null,
            IEncodeOutputFinalizationService? finalizationService = null)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectory))
                throw new ArgumentException("Application directory must be provided.", nameof(applicationDirectory));

            _appPath = applicationDirectory;
            var tools = FfmpegToolResolver.Resolve(applicationDirectory, ffmpegPath, ffprobePath);
            _ffmpegPath = tools.FfmpegPath;
            _ffprobePath = tools.FfprobePath;
            _progressCallback = progressCallback ?? (_ => { });
            _log = logCallback;
            _finalizationService = finalizationService ??
                new EncodeOutputFinalizationService(
                    new EncodeOutputValidationService(
                        new FfprobeService(
                            _ffprobePath,
                            new MediaToolProcessRunner()),
                        new FfmpegDecodeIntegritySpotCheckService(
                            _ffmpegPath)));

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

        /// <summary>
        /// Preferred encoder-neutral API. Legacy overloads remain available while
        /// existing UI and persisted settings migrate to stable encoder IDs.
        /// </summary>
        public Task<EncodeResult> EncodeWithResultAsync(EncodingRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Input);
            ArgumentNullException.ThrowIfNull(request.Encoder);

            ValidatedEncoderSettings validated =
                EncodingRequestValidator.ValidateAndNormalize(
                    EncoderRegistry.Default,
                    request.Encoder,
                    request.UseGpu,
                    request.TargetMb,
                    request.EncoderPreset,
                    request.QualityValue,
                    request.TenBit,
                    request.AudioChannels,
                    request.ConcurrentEncoderSessions);
            EnsureEncoderAvailable(validated.Resolved.Selection);

            return EncodeInternalAsync(
                request.Input,
                request.OutputFolder,
                request.Suffix,
                validated.UseGpu,
                request.TargetMb,
                validated.Resolved.Selection.FfmpegCodec,
                request.ScaleMode,
                validated.Preset,
                validated.TenBit,
                request.AudioChannels,
                request.ProgressCallback ?? _progressCallback,
                validated.ConcurrentEncoderSessions,
                request.MapMode,
                request.CopySubtitles,
                request.CancellationToken,
                request.OutputPathCallback,
                validated.QualityValue,
                validated.Resolved.Selection,
                request.StagingPathCallback,
                request.FinalizationStatusCallback,
                request.OutputContainer,
                request.CopyDataStreams,
                request.CopyAttachments,
                request.ContainerCompatibilityConfirmed,
                request.ContainerDecisionCallback);
        }

        public Task<bool> EncodeAsync(EncodingRequest request)
        {
            return EncodeSuccessAsync(EncodeWithResultAsync(request));
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
            string? encoderPreset,
            bool tenBit,
            int? audioChannels,
            Action<string> callback,
            bool concurrentEncoderSessions,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default,
            Action<string>? outputPathCallback = null,
            int? qualityValue = null,
            VideoEncoderSelection? encoderSelection = null,
            Action<string>? stagingPathCallback = null,
            Action<string>? finalizationStatusCallback = null,
            OutputContainerSelection outputContainer = OutputContainerSelection.Mp4,
            bool copyDataStreams = true,
            bool copyAttachments = true,
            bool containerCompatibilityConfirmed = false,
            Action<OutputContainerDecision>? containerDecisionCallback = null)
        {
            return EncodeInternalAsync(
                EncodingInputSource.FromFile(input),
                outputFolder,
                suffix,
                useGpu,
                targetMb,
                videoCodec,
                scaleMode,
                encoderPreset,
                tenBit,
                audioChannels,
                callback,
                concurrentEncoderSessions,
                mapMode,
                copySubtitles,
                cancellationToken,
                outputPathCallback,
                qualityValue,
                encoderSelection,
                stagingPathCallback,
                finalizationStatusCallback,
                outputContainer,
                copyDataStreams,
                copyAttachments,
                containerCompatibilityConfirmed,
                containerDecisionCallback);
        }

        private async Task<EncodeResult> EncodeInternalAsync(
            EncodingInputSource inputSource,
            string outputFolder,
            string suffix,
            bool useGpu,
            double? targetMb,
            string videoCodec,
            ScaleMode scaleMode,
            string? encoderPreset,
            bool tenBit,
            int? audioChannels,
            Action<string> callback,
            bool concurrentEncoderSessions,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            CancellationToken cancellationToken = default,
            Action<string>? outputPathCallback = null,
            int? qualityValue = null,
            VideoEncoderSelection? encoderSelection = null,
            Action<string>? stagingPathCallback = null,
            Action<string>? finalizationStatusCallback = null,
            OutputContainerSelection outputContainer = OutputContainerSelection.Mp4,
            bool copyDataStreams = true,
            bool copyAttachments = true,
            bool containerCompatibilityConfirmed = false,
            Action<OutputContainerDecision>? containerDecisionCallback = null)
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

            string sourceProbePath = inputSource.Kind == EncodingInputKind.File
                ? inputSource.SourcePath
                : inputSource.SourceFiles.FirstOrDefault() ?? inputSource.SourcePath;
            MediaProbeResult sourceProbe = await new FfprobeService(
                    _ffprobePath,
                    new MediaToolProcessRunner())
                .ProbeAsync(sourceProbePath, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceProbe.Success)
                throw new InvalidOperationException(
                    $"FFprobe could not inspect the source before container selection: {sourceProbe.ErrorMessage}");

            OutputContainerDecision containerDecision = OutputContainerPolicy.Decide(
                outputContainer,
                sourceProbe,
                inputSource,
                mapMode,
                copySubtitles,
                copyDataStreams,
                copyAttachments,
                audioWillBeTranscoded: audioChannels is > 0);
            _log?.Invoke($"[EncodingService] {containerDecision.Reason}");
            containerDecisionCallback?.Invoke(containerDecision);
            if (containerDecision.Requested == OutputContainerSelection.Mp4)
            {
                foreach (string warning in containerDecision.CompatibilityWarnings)
                    _log?.Invoke($"[EncodingService] Container compatibility: {warning}.");
            }
            if (containerDecision.RequiresConfirmation && !containerCompatibilityConfirmed)
            {
                _log?.Invoke(
                    "[EncodingService] Explicit MP4 compatibility was not preconfirmed by the caller; " +
                    "continuing for legacy API compatibility.");
            }

            // Keep the intended final name collision-safe, but write FFmpeg output
            // only to a hidden same-directory staging file until validation passes.
            string finalOutput = OutputPathService.GetCollisionSafePath(
                Path.Combine(outFolder, $"{name}{actualSuffix}{containerDecision.Extension}"));
            string output = OutputPathService.CreateEncodeStagingPath(finalOutput);
            outputPathCallback?.Invoke(finalOutput);
            stagingPathCallback?.Invoke(output);
            VideoEncoderSelection requestedEncoder =
                encoderSelection ??
                EncoderRegistry.Default.ResolveLegacyCodec(videoCodec).Selection;
            bool isAsfFamilyInput =
                inputSource.Kind == EncodingInputKind.File &&
                IsAsfFamilyInput(inputSource.SourcePath);

            bool allowSubtitleCopy = containerDecision.CopySubtitles;
            bool allowDataCopy = containerDecision.CopyDataStreams;
            bool allowAttachmentCopy = containerDecision.CopyAttachments;

            bool forceMp4CompatibleAudio = isAsfFamilyInput &&
                string.Equals(Path.GetExtension(finalOutput), ".mp4", StringComparison.OrdinalIgnoreCase);
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
                encoderPreset,
                tenBit,
                audioChannels,
                concurrentEncoderSessions,
                mapMode,
                allowSubtitleCopy,
                allowDataCopy,
                allowAttachmentCopy,
                containerDecision,
                forceMp4CompatibleAudio,
                totalDuration,
                qualityValue,
                encoderSelection);

            string pipelineDiagnostic = DescribeVideoPipeline(
                inputSource,
                videoCodec,
                useGpu,
                tenBit,
                ffArgs);
            callback($"[MediaFlux] Video pipeline: {pipelineDiagnostic}");
            _log?.Invoke(
                $"[EncodingService] Video pipeline: {pipelineDiagnostic}");

            _log?.Invoke(
                $"[EncodingService] Starting ffmpeg for '{inputSource.SourcePath}' " +
                $"using '{input}' -> staged '{output}' (final '{finalOutput}')");
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

            _log?.Invoke(
                "[EncodingService] ffmpeg completed successfully; validating staged output.");
            EncodeFinalizationResult finalization =
                await _finalizationService.FinalizeAsync(
                    new EncodeOutputValidationRequest
                    {
                        Input = inputSource,
                        OutputPath = output,
                        FinalOutputPath = finalOutput,
                        Encoder = requestedEncoder,
                        ScaleMode = scaleMode,
                        TenBit = tenBit,
                        AudioChannels = audioChannels,
                        MapMode = mapMode,
                        CopySubtitles = allowSubtitleCopy,
                        CopyDataStreams = allowDataCopy,
                        CopyAttachments = allowAttachmentCopy,
                        ContainerDecision = containerDecision,
                        SourceProbe = sourceProbe
                    },
                    finalizationStatusCallback,
                    cancellationToken).ConfigureAwait(false);
            if (!finalization.Success)
            {
                _log?.Invoke(
                    $"[EncodingService] Finalization failed: {finalization.ErrorMessage}");
                throw new EncodeFinalizationException(finalization);
            }

            _log?.Invoke(
                $"[EncodingService] Validated and finalized '{finalization.FinalOutputPath}'.");
            return new EncodeResult(
                true,
                finalization.FinalOutputPath,
                ffArgs,
                finalizationSucceeded: true,
                stagingPath: output,
                validationSummary: finalization.ValidationSummary,
                finalOutputSizeBytes: finalization.FinalOutputSizeBytes,
                requestedOutputContainer: containerDecision.Requested,
                resolvedOutputContainer: containerDecision.Resolved,
                containerDecisionReason: containerDecision.Reason);
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
            string? encoderPreset,
            bool tenBit,
            int? audioChannels,
            bool concurrentEncoderSessions,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            bool copyDataStreams = true,
            bool copyAttachments = true,
            OutputContainerDecision? containerDecision = null,
            bool forceMp4CompatibleAudio = false,
            TimeSpan knownDuration = default,
            int? qualityValue = null,
            VideoEncoderSelection? encoderSelection = null)
        {
            ResolvedVideoEncoder resolved =
                encoderSelection == null
                    ? EncoderRegistry.Default.ResolveLegacyCodec(videoCodec)
                    : EncoderRegistry.Default.Resolve(
                        encoderSelection.EncoderId,
                        encoderSelection.CodecFamily);
            EnsureEncoderAvailable(resolved.Selection);
            bool supportsGpuResidentHighBitDepthOutput =
                useGpu &&
                tenBit &&
                resolved.Selection.EncoderId.Equals(
                    VideoEncoderIds.Nvenc,
                    StringComparison.OrdinalIgnoreCase) &&
                resolved.Selection.CodecFamily is
                    VideoCodecFamily.Hevc or VideoCodecFamily.Av1 &&
                FfmpegEncoderCapabilityService.SupportsEncoderOption(
                    _ffmpegPath,
                    resolved.Selection.FfmpegCodec,
                    "highbitdepth");
            var request = new FfmpegCommandRequest
            {
                Input = input,
                OutputPath = output,
                Encoder = encoderSelection ?? resolved.Selection,
                UseGpu = useGpu,
                TargetMb = targetMb,
                ScaleMode = scaleMode,
                EncoderPreset = encoderPreset,
                QualityValue = qualityValue,
                TenBit = tenBit,
                AudioChannels = audioChannels,
                ConcurrentEncoderSessions = concurrentEncoderSessions,
                MapMode = mapMode,
                CopySubtitles = copySubtitles,
                CopyDataStreams = copyDataStreams,
                CopyAttachments = copyAttachments,
                ContainerDecision = containerDecision ?? new OutputContainerDecision
                {
                    Requested = OutputContainerSelection.Mp4,
                    Resolved = OutputContainer.Mp4,
                    Reason = "Legacy MP4 output.",
                    CopySubtitles = copySubtitles,
                    CopyDataStreams = copyDataStreams,
                    CopyAttachments = copyAttachments
                },
                ForceMp4CompatibleAudio = forceMp4CompatibleAudio,
                KnownDuration = knownDuration,
                NvencHighBitDepthOutputSupported =
                    supportsGpuResidentHighBitDepthOutput
            };

            var builder = new FfmpegCommandBuilder(
                EncoderRegistry.Default,
                GetPrimaryAudioBitrateKbps,
                _log);
            return builder.Build(request);
        }

        private static string DescribeVideoPipeline(
            EncodingInputSource input,
            string videoCodec,
            bool useGpu,
            bool tenBit,
            string ffmpegArguments)
        {
            bool isNvenc =
                useGpu &&
                videoCodec.EndsWith(
                    "_nvenc",
                    StringComparison.OrdinalIgnoreCase);
            if (!isNvenc)
                return "software-frame path to the selected encoder";

            if (input.Kind == EncodingInputKind.File &&
                IsAsfFamilyInput(input.SourcePath))
            {
                return "software decode (WMV/ASF compatibility) -> NVENC";
            }

            if (ffmpegArguments.Contains(
                    "-hwaccel_output_format cuda",
                    StringComparison.Ordinal))
            {
                return tenBit
                    ? "NVDEC/CUDA frames kept on GPU -> NVENC 10-bit output"
                    : "NVDEC/CUDA frames kept on GPU -> NVENC";
            }

            return tenBit
                ? "NVDEC -> host 10-bit conversion -> NVENC (FFmpeg compatibility fallback)"
                : "NVDEC -> NVENC";
        }

        private void EnsureEncoderAvailable(
            VideoEncoderSelection selection)
        {
            FfmpegEncoderCapabilities capabilities =
                FfmpegEncoderCapabilityService.GetCapabilities(_ffmpegPath);
            EncodingRequestValidator.EnsureEncoderAvailable(
                selection,
                capabilities);
        }

        internal static string BuildInputAndMappingArgumentsForTesting(
            EncodingInputSource input,
            StreamMapMode mapMode = StreamMapMode.KeepAll,
            bool copySubtitles = true,
            bool copyDataStreams = true)
        {
            return FfmpegCommandBuilder.BuildInputAndMappingArguments(
                input,
                mapMode,
                copySubtitles,
                copyDataStreams);
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




