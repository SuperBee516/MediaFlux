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
            public long? FinalOutputLastWriteUtcTicks { get; }
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
                string containerDecisionReason = "",
                long? finalOutputLastWriteUtcTicks = null)
            {
                Success = success;
                OutputPath = outputPath;
                DiagnosticArguments = diagnosticArguments;
                FinalizationSucceeded = finalizationSucceeded;
                StagingPath = stagingPath;
                ValidationSummary = validationSummary;
                FinalOutputSizeBytes = finalOutputSizeBytes;
                FinalOutputLastWriteUtcTicks = finalOutputLastWriteUtcTicks;
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
                            _ffmpegPath),
                        _log));

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
                request.CompatibilityPolicy,
                request.ContainerDecisionCallback,
                request.SampleStart,
                request.SampleDuration,
                VideoRestorationModeResolver.Resolve(request.Restoration),
                request.AiProgressCallback);
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
            ContainerCompatibilityPolicy compatibilityPolicy = ContainerCompatibilityPolicy.Intelligent,
            Action<OutputContainerDecision>? containerDecisionCallback = null,
            TimeSpan? sampleStart = null,
            TimeSpan? sampleDuration = null,
            VideoRestorationSettings? restoration = null,
            Action<AiIntermediateProgress>? aiProgressCallback = null)
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
                compatibilityPolicy,
                containerDecisionCallback,
                sampleStart,
                sampleDuration,
                restoration,
                aiProgressCallback);
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
            ContainerCompatibilityPolicy compatibilityPolicy = ContainerCompatibilityPolicy.Intelligent,
            Action<OutputContainerDecision>? containerDecisionCallback = null,
            TimeSpan? sampleStart = null,
            TimeSpan? sampleDuration = null,
            VideoRestorationSettings? restoration = null,
            Action<AiIntermediateProgress>? aiProgressCallback = null)
        {
            restoration = VideoRestorationModeResolver.Resolve(restoration);
            var performance = new PerformanceTimingService();
            try
            {
            cancellationToken.ThrowIfCancellationRequested();
            VideoRestorationPipeline.Validate(restoration ?? new VideoRestorationSettings(), scaleMode);
            if (restoration?.Preset != VideoRestorationPreset.Off)
            {
                try
                {
                    FfmpegRestorationCapabilities capabilities = new FfmpegRestorationCapabilityService(log: _log).GetAsync(_ffmpegPath, cancellationToken).GetAwaiter().GetResult();
                    if (capabilities.State == FfmpegFilterInventoryState.Available)
                    {
                        VideoRestorationPipeline.SetAvailableFilters(capabilities.Filters);
                        VideoRestorationPipeline.ValidateAvailable(restoration);
                    }
                    else
                    {
                        VideoRestorationPipeline.ClearAvailableFilters();
                        _log?.Invoke("[EncodingService] Restoration filter inventory is Unknown; allowing FFmpeg to validate filters rather than falsely reporting them unavailable.");
                    }
                    _log?.Invoke($"[EncodingService] Restoration filter inventory: {capabilities.State}; FFmpeg {capabilities.Version}; parsed={capabilities.ParsedFilterCount}.");
                }
                catch (OperationCanceledException) { throw; }
                catch (NotSupportedException) { throw; }
                catch (Exception ex) { throw new InvalidOperationException($"MediaFlux could not validate restoration filters before encoding: {ex.Message}", ex); }
            }

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
            performance.SetHardwareSnapshot(HardwarePerformanceService.Capture(
                inputSource.SourcePath,
                Path.Combine(AppPaths.DataDirectory, "ai-intermediates"),
                outFolder,
                _ffmpegPath));

            string name = string.IsNullOrWhiteSpace(inputSource.OutputBaseName)
                ? Path.GetFileNameWithoutExtension(input)
                : inputSource.OutputBaseName;
            string actualSuffix = string.IsNullOrWhiteSpace(suffix) ? string.Empty : suffix;

            string sourceProbePath = inputSource.Kind == EncodingInputKind.File
                ? inputSource.SourcePath
                : inputSource.SourceFiles.FirstOrDefault() ?? inputSource.SourcePath;
            MediaProbeResult sourceProbe;
            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.SourceAnalysis))
            {
            sourceProbe = await new FfprobeService(
                    _ffprobePath,
                    new MediaToolProcessRunner())
                .ProbeAsync(sourceProbePath, cancellationToken)
                .ConfigureAwait(false);
            scope.Complete();
            }
            if (!sourceProbe.Success)
                throw new InvalidOperationException(
                    $"FFprobe could not inspect the source before container selection: {sourceProbe.ErrorMessage}");
            MediaProbeStreamInfo? sourceVideo = sourceProbe.Streams.FirstOrDefault(
                stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            ProgramDurationDecision programDuration = ProgramDurationResolver.Resolve(sourceProbe);
            double? primaryVideoDuration = programDuration.PrimaryVideo is null
                ? null
                : ProgramDurationResolver.GetReliableDuration(programDuration.PrimaryVideo);
            _log?.Invoke($"[EncodingService] Container duration={sourceProbe.DurationSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}s; primary video duration={primaryVideoDuration?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}s; authoritative duration={programDuration.DurationSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}s; reason={programDuration.Reason}");
            VideoOutputResolutionPlan? finalOutputResolution = sourceVideo?.Width is > 0 && sourceVideo.Height is > 0
                ? VideoRestorationPipeline.ResolveFinalOutputResolution(sourceVideo.Width.Value, sourceVideo.Height.Value, restoration, scaleMode)
                : null;
            AiIntermediateVideoResult? aiIntermediate = null;
            VideoRestorationPipelinePlan? aiPlan = null;
            if (restoration is { AiMode: not AiRestorationMode.Off } aiSettings)
            {
                MediaProbeStreamInfo? video = sourceVideo;
                if (video?.FrameRate is not > 0 || video.Width is not > 0 || video.Height is not > 0)
                    throw new AiRestorationValidationException("AI restoration requires a source with a known constant frame rate and resolution.");
                SourceTimingAnalysis timing;
                using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.SourceAnalysis))
                {
                    timing = await new SourceTimingAnalysisService(_ffprobePath, log: _log).AnalyzeAsync(inputSource.SourcePath, cancellationToken).ConfigureAwait(false);
                    scope.Complete();
                }
                SourceTimingAnalysisService.EnsureCurrentCfrSupported(timing);
                IAiRestorationBackend backend = await new AiBackendManager(AppPaths.InstallDirectory, log: _log)
                    .SelectAsync(aiSettings, video.Width.Value, video.Height.Value, cancellationToken).ConfigureAwait(false);
                finalOutputResolution ??= VideoRestorationPipeline.ResolveFinalOutputResolution(video.Width.Value, video.Height.Value, aiSettings, scaleMode);
                bool restoreOriginalAfterAi = scaleMode == ScaleMode.None && VideoRestorationPipeline.Effective(aiSettings).Resize == VideoRestorationResize.Original;
                aiPlan = VideoRestorationPipeline.BuildPlan(aiSettings, scaleMode, restoreOriginalAfterAi ? finalOutputResolution.ScaleFilter : null);
                callback("[MediaFlux] Preparing AI restoration.");
                var intermediate = new AiRestorationIntermediateVideoService(_ffmpegPath, _ffprobePath, Path.Combine(AppPaths.DataDirectory, "ai-intermediates"), backend, log: _log, timing: performance);
                TimeSpan aiDuration = sampleDuration ?? TimeSpan.FromSeconds(programDuration.DurationSeconds ?? 0);
                int expectedFrames = AiRestorationIntermediateVideoService.ResolveExpectedFrameCount(
                    new AiIntermediateVideoRequest(inputSource.SourcePath, video.FrameRate.Value, TimeSpan.FromSeconds(programDuration.DurationSeconds ?? 0), aiSettings, aiPlan, sampleStart, sampleDuration, video.Width ?? 0, video.Height ?? 0, SourceFrameCount: video.FrameCount),
                    aiDuration);
                string stagingRoot = Path.Combine(AppPaths.DataDirectory, "ai-intermediates");
                AiTemporaryStorageEstimate planningEstimate = AiProductionHardeningService.Estimate(video.Width ?? 0, video.Height ?? 0, expectedFrames, aiSettings.AiScale, stagingRoot, AiChunkPlanner.MinimumFramesPerChunk);
                AiChunkPlan plannedChunk = new AiChunkPlanner().Plan(new(video.Width ?? 0, video.Height ?? 0, aiSettings.AiScale, performance.DedicatedGpuVramBytes, planningEstimate, "Pending backend"));
                AiTemporaryStorageEstimate estimate = AiProductionHardeningService.Estimate(video.Width ?? 0, video.Height ?? 0, expectedFrames, aiSettings.AiScale, stagingRoot, plannedChunk.FrameCount);
                _log?.Invoke($"[EncodingService] AI preflight: source={inputSource.SourcePath}; model={aiSettings.AiModelId}; device={aiSettings.AiDevice}; scale={(int)aiSettings.AiScale}x; expectedFrames={expectedFrames}; {estimate.Describe()}; plan={aiPlan.DescribeStages()}.");
                AiProductionHardeningService.EnsureSpace(estimate);
                aiIntermediate = await intermediate.CreateAsync(
                    new AiIntermediateVideoRequest(inputSource.SourcePath, video.FrameRate.Value, TimeSpan.FromSeconds(programDuration.DurationSeconds ?? 0), aiSettings, aiPlan, sampleStart, sampleDuration, video.Width ?? 0, video.Height ?? 0, SourceFrameCount: video.FrameCount),
                    new Progress<AiIntermediateProgress>(p => { callback($"[MediaFlux] {p.Message}"); aiProgressCallback?.Invoke(p); }),
                    cancellationToken).ConfigureAwait(false);
                _log?.Invoke($"[EncodingService] AI resolution plan: source={video.Width}x{video.Height}; aiScale={(int)aiSettings.AiScale}x; intermediate={aiIntermediate.Width}x{aiIntermediate.Height}; requestedFinal={finalOutputResolution.Describe()}; finalScaleDecision={(restoreOriginalAfterAi ? finalOutputResolution.ScaleFilter : "provided by configured restoration/normal encode scale")}; postAiFilters={aiPlan.PostAiFilterChain}.");
                _log?.Invoke($"[EncodingService] AI intermediate ready: {aiIntermediate.Path}; {aiPlan.DescribeStages()}.");
            }

            OutputContainerDecision containerDecision = OutputContainerPolicy.Decide(
                outputContainer,
                sourceProbe,
                inputSource,
                mapMode,
                copySubtitles,
                copyDataStreams,
                copyAttachments,
                audioWillBeTranscoded: audioChannels is > 0);
            if (containerDecision.Resolved == OutputContainer.Mp4 &&
                compatibilityPolicy == ContainerCompatibilityPolicy.Strict &&
                !OutputContainerPolicy.CanProceedAutomatically(containerDecision, compatibilityPolicy))
                throw new InvalidOperationException("Strict container compatibility policy rejected a required stream conversion or omission.");
            if (containerDecision.Resolved == OutputContainer.Mp4 &&
                compatibilityPolicy == ContainerCompatibilityPolicy.Intelligent &&
                containerDecision.HasUnsupportedMeaningfulStreams)
                throw new InvalidOperationException("Intelligent container compatibility could not safely preserve a requested audio, video, or subtitle stream.");
            _log?.Invoke($"[EncodingService] {containerDecision.Reason}");
            containerDecisionCallback?.Invoke(containerDecision);
            if (containerDecision.Requested == OutputContainerSelection.Mp4)
            {
                foreach (string warning in containerDecision.CompatibilityWarnings)
                    _log?.Invoke($"[EncodingService] Container compatibility: {warning}.");
            }
            foreach (StreamCompatibilityPlan plan in containerDecision.StreamPlans.Where(plan => plan.Action != StreamCompatibilityAction.Copy))
                _log?.Invoke($"[EncodingService] Stream {plan.StreamIndex} {plan.StreamType}/{plan.Codec}: {plan.Action}; {plan.Reason}");
            if (containerDecision.ConvertSubtitlesToMovText)
                _log?.Invoke("[EncodingService] ASS/SSA subtitles will be converted to mov_text; language/title/dispositions are retained where MP4 supports them, styling may be lost.");
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
            int plannedAudioStreams = inputSource.HasExplicitStreamSelection
                ? inputSource.AudioStreamIndexes.Count
                : mapMode == StreamMapMode.FirstAudioOnly
                    ? Math.Min(1, sourceProbe.Streams.Count(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase)))
                    : sourceProbe.Streams.Count(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase));
            int plannedSubtitleStreams = !allowSubtitleCopy ? 0
                : containerDecision.Resolved == OutputContainer.Mp4 && containerDecision.StreamPlans.Count > 0
                    ? containerDecision.StreamPlans.Count(plan => plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) && plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode)
                    : inputSource.HasExplicitStreamSelection
                        ? inputSource.SubtitleStreamIndexes.Count
                        : sourceProbe.Streams.Count(stream => stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase));
            _log?.Invoke($"[EncodingService] FFmpeg mapping plan: video=1; audio={plannedAudioStreams}; subtitles={plannedSubtitleStreams}; data={(allowDataCopy ? "included" : "omitted")}; attachments={(allowAttachmentCopy ? "included" : "omitted")}.");
            if (copyDataStreams && !allowDataCopy)
            {
                MediaProbeStreamInfo[] omittedDataStreams = sourceProbe.Streams
                    .Where(stream => stream.CodecType.Equals(
                        "data", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (omittedDataStreams.Length > 0)
                {
                    string descriptions = string.Join(", ", omittedDataStreams
                        .Select(DescribeUnsupportedDataStream)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    _log?.Invoke(
                        $"[EncodingService] Excluded {omittedDataStreams.Length} unsupported " +
                        $"data stream(s) from {containerDecision.Resolved} output: {descriptions}.");
                }
            }

            bool forceMp4CompatibleAudio = isAsfFamilyInput &&
                string.Equals(Path.GetExtension(finalOutput), ".mp4", StringComparison.OrdinalIgnoreCase);
            if (forceMp4CompatibleAudio)
            {
                _log?.Invoke("[EncodingService] WMV/ASF input detected for MP4 output; transcoding audio to AAC.");
            }

            // Total duration once for progress and target bitrate math
            TimeSpan totalDuration = sampleDuration is { } requestedSample && requestedSample > TimeSpan.Zero
                ? requestedSample
                : inputSource.KnownDurationSeconds is > 0
                ? TimeSpan.FromSeconds(inputSource.KnownDurationSeconds.Value)
                : programDuration.DurationSeconds is > 0
                ? TimeSpan.FromSeconds(programDuration.DurationSeconds.Value)
                : GetVideoDuration(input);
            if (totalDuration <= TimeSpan.Zero)
                _log?.Invoke("[EncodingService] Warning: could not determine duration, progress percent will be 0.");

            string sourcePixelFormat = sourceProbe.Streams.FirstOrDefault(stream => stream.CodecType.Equals(
                "video", StringComparison.OrdinalIgnoreCase))?.PixelFormat ?? "";
            string ffArgs;
            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
            {
            ffArgs = BuildFfmpegArgs(
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
                encoderSelection,
                sampleStart,
                sampleDuration,
                sourcePixelFormat,
                restoration: restoration, splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource), restorationFilterOverride: aiPlan?.PostAiFilterChain);
            scope.Complete();
            }

            string restorationChain = aiPlan?.PostAiFilterChain ?? VideoRestorationPipeline.BuildFilterChain(restoration, scaleMode);
            if (!string.IsNullOrWhiteSpace(restorationChain))
                _log?.Invoke($"[EncodingService] Video restoration: {(restoration ?? new VideoRestorationSettings()).Preset}; filters: {restorationChain}");

            string pipelineDiagnostic = DescribeVideoPipeline(
                inputSource,
                videoCodec,
                useGpu,
                tenBit,
                ffArgs);
            _log?.Invoke(
                $"[EncodingService] Output format intent: Requested: " +
                $"{requestedEncoder.CodecFamily} {(tenBit ? "10-bit" : "8-bit")}; " +
                $"Source: {sourceVideo?.CodecName ?? "unknown"} " +
                $"{sourceVideo?.Profile ?? ""} / {sourceVideo?.PixelFormat ?? "unknown"}; " +
                $"Encoder: {requestedEncoder.FfmpegCodec}; " +
                $"Pipeline/conversion selected: {pipelineDiagnostic}.");
            callback($"[MediaFlux] Video pipeline: {pipelineDiagnostic}");
            _log?.Invoke(
                $"[EncodingService] Video pipeline: {pipelineDiagnostic}");

            _log?.Invoke(
                $"[EncodingService] Starting ffmpeg for '{inputSource.SourcePath}' " +
                $"using '{input}' -> staged '{output}' (final '{finalOutput}')");
            _log?.Invoke($"[EncodingService] ffmpeg arguments: {ffArgs}");

            using PerformanceTimingService.PerformanceScope encodeScope = performance.Measure(PerformanceTimingStage.FinalEncode);
            FfmpegProcessResult runResult = await RunFfmpegAsync(
                ffArgs, callback, totalDuration, cancellationToken).ConfigureAwait(false);

            if (runResult.ExitCode != 0 &&
                ShouldRetryWithSoftwareFrames(ffArgs, runResult.StandardError))
            {
                string failedPipeline = pipelineDiagnostic;
                _log?.Invoke(
                    "[EncodingService] GPU-resident NVENC pipeline could not negotiate " +
                    "CUDA frames with the selected FFmpeg build; retrying once with " +
                    "CUDA decode and explicit software-frame conversion. Failure: " +
                    SummarizeFfmpegFailure(runResult.StandardError));
                callback("[MediaFlux] GPU frame pipeline was incompatible; retrying with software-frame conversion.");

                using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
                {
                ffArgs = BuildFfmpegArgs(
                    inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                    encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                    mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                    containerDecision, forceMp4CompatibleAudio, totalDuration,
                    qualityValue, encoderSelection, sampleStart, sampleDuration,
                    sourcePixelFormat, preferNvencGpuResidentFrames: false, restoration: restoration, splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource), restorationFilterOverride: aiPlan?.PostAiFilterChain);
                scope.Complete();
                }
                pipelineDiagnostic = DescribeVideoPipeline(
                    inputSource, videoCodec, useGpu, tenBit, ffArgs);
                _log?.Invoke(
                    $"[EncodingService] Pipeline fallback: {failedPipeline} -> " +
                    $"{pipelineDiagnostic}. ffmpeg arguments: {ffArgs}");
                runResult = await RunFfmpegAsync(
                    ffArgs, callback, totalDuration, cancellationToken).ConfigureAwait(false);
            }

            if (runResult.ExitCode != 0)
            {
                string logPath = ErrorLogService.Append(
                    _appPath,
                    "FFmpeg encode failed",
                    inputSource.SourcePath,
                    details:
                    $"Output     : {output}{Environment.NewLine}" +
                    $"Exit Code  : {runResult.ExitCode}{Environment.NewLine}" +
                    $"Arguments  : {ffArgs}{Environment.NewLine}{Environment.NewLine}" +
                    "FFmpeg Output:" + Environment.NewLine +
                    runResult.StandardError);

                _log?.Invoke($"[EncodingService] ffmpeg exited with code {runResult.ExitCode}. See central log: {logPath}");
                throw new InvalidOperationException($"ffmpeg exited with code {runResult.ExitCode}. See central log: {logPath}");
            }
            encodeScope.Complete();
            encodeScope.Dispose();

            _log?.Invoke(
                "[EncodingService] ffmpeg completed successfully; validating staged output.");
            EncodeFinalizationResult finalization;
            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.Finalization))
            {
            finalization =
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
                        SourceProbe = sourceProbe,
                        ExpectedDurationSeconds = sampleDuration?.TotalSeconds ?? programDuration.DurationSeconds,
                        ExpectedVideoFrameCount = sampleDuration is null ? programDuration.PrimaryVideo?.FrameCount : null,
                        ExpectedVideoFrameCountProvenance = sampleDuration is null && programDuration.PrimaryVideo?.FrameCount is > 0
                            ? FrameCountProvenance.Measured
                            : FrameCountProvenance.Unavailable,
                        ExpectedVideoWidth = finalOutputResolution?.Width,
                        ExpectedVideoHeight = finalOutputResolution?.Height,
                        PerformanceTiming = performance
                    },
                    finalizationStatusCallback,
                    cancellationToken).ConfigureAwait(false);
            if (finalization.Success) scope.Complete();
            }
            if (!finalization.Success)
            {
                _log?.Invoke(
                    $"[EncodingService] Finalization failed: {finalization.ErrorMessage}");
                throw new EncodeFinalizationException(finalization);
            }

            _log?.Invoke(
                $"[EncodingService] Validated and finalized '{finalization.FinalOutputPath}'.");
            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.TemporaryFileCleanup))
            { if (aiIntermediate is not null) { aiIntermediate.Dispose(); scope.Complete(); } }
            performance.LogSummary(_log);
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
                containerDecisionReason: containerDecision.Reason,
                    finalOutputLastWriteUtcTicks:
                    finalization.FinalOutputLastWriteUtcTicks);
            }
            catch
            {
                performance.LogSummary(_log);
                throw;
            }
        }

        private static string DescribeUnsupportedDataStream(MediaProbeStreamInfo stream)
        {
            string codec = string.IsNullOrWhiteSpace(stream.CodecName)
                ? "unknown"
                : stream.CodecName;
            string handler = stream.Tags.TryGetValue("handler_name", out string? value) &&
                !string.IsNullOrWhiteSpace(value)
                ? value
                : stream.CodecLongName;
            return string.IsNullOrWhiteSpace(handler) ? codec : $"{codec} ({handler})";
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

        private async Task<FfmpegProcessResult> RunFfmpegAsync(
            string arguments,
            Action<string> callback,
            TimeSpan totalDuration,
            CancellationToken cancellationToken)
        {
            var stderrBuilder = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    HandleProgressLine(e.Data, callback, totalDuration);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;

                HandleProgressLine(e.Data, callback, totalDuration);
                AppendBounded(stderrBuilder, e.Data, MaxCapturedFfmpegCharacters);
            };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] Failed to start ffmpeg: {ex}");
                throw;
            }

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
                                proc.StandardInput.WriteLine("q");
                                proc.StandardInput.Flush();
                            }
                        }
                        catch
                        {
                            // Ignore cancellation races with FFmpeg shutdown.
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

            return new FfmpegProcessResult(proc.ExitCode, stderrBuilder.ToString());
        }

        private static bool ShouldRetryWithSoftwareFrames(
            string arguments,
            string standardError)
        {
            if (!arguments.Contains("-hwaccel_output_format cuda ", StringComparison.Ordinal))
                return false;

            return standardError.Contains(
                "Impossible to convert between the formats supported by the filter",
                StringComparison.OrdinalIgnoreCase) ||
                standardError.Contains("Error reinitializing filters!", StringComparison.OrdinalIgnoreCase);
        }

        private static string SummarizeFfmpegFailure(string standardError)
        {
            string? detail = standardError.Split(
                    ["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains("Impossible to convert", StringComparison.OrdinalIgnoreCase))
                ?? standardError.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
            return string.IsNullOrWhiteSpace(detail) ? "No FFmpeg diagnostic was captured." : detail.Trim();
        }

        private sealed record FfmpegProcessResult(int ExitCode, string StandardError);

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
            VideoEncoderSelection? encoderSelection = null,
            TimeSpan? sampleStart = null,
            TimeSpan? sampleDuration = null,
            string? sourcePixelFormat = null,
            bool preferNvencGpuResidentFrames = true,
            VideoRestorationSettings? restoration = null,
            SplitSourceInput? splitSource = null,
            string? restorationFilterOverride = null)
        {
            ResolvedVideoEncoder resolved =
                encoderSelection == null
                    ? EncoderRegistry.Default.ResolveLegacyCodec(videoCodec)
                    : EncoderRegistry.Default.Resolve(
                        encoderSelection.EncoderId,
                        encoderSelection.CodecFamily);
            EnsureEncoderAvailable(resolved.Selection);
            bool isNvenc = resolved.Selection.EncoderId.Equals(
                VideoEncoderIds.Nvenc,
                StringComparison.OrdinalIgnoreCase);
            string requiredEncoderPixelFormat = tenBit ? "p010le" : "nv12";
            FfmpegEncoderCapabilities capabilities =
                FfmpegEncoderCapabilityService.GetCapabilities(_ffmpegPath);
            if (isNvenc && capabilities.InspectionSucceeded &&
                !FfmpegEncoderCapabilityService.SupportsEncoderPixelFormat(
                    _ffmpegPath,
                    resolved.Selection.FfmpegCodec,
                    requiredEncoderPixelFormat))
            {
                throw new NotSupportedException(
                    $"Requested: {resolved.Selection.CodecFamily} " +
                    $"{(tenBit ? "10-bit" : "8-bit")} ({requiredEncoderPixelFormat}). " +
                    $"Encoder '{resolved.Selection.FfmpegCodec}' in the configured FFmpeg " +
                    "build does not support that pixel format. Choose a supported encoder " +
                    "or configure a newer FFmpeg build.");
            }
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
                Restoration = restoration?.Clone() ?? new VideoRestorationSettings(),
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
                SampleStart = sampleStart,
                SampleDuration = sampleDuration,
                NvencHighBitDepthOutputSupported =
                    supportsGpuResidentHighBitDepthOutput,
                PreferNvencGpuResidentFrames =
                    preferNvencGpuResidentFrames,
                SourcePixelFormat = sourcePixelFormat ?? ""
                ,SplitSource = splitSource
                ,RestorationFilterOverride = restorationFilterOverride
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

            if (ffmpegArguments.Contains("-vf ", StringComparison.Ordinal))
            {
                return tenBit
                    ? "NVDEC -> host 10-bit conversion -> NVENC"
                    : "NVDEC -> host 8-bit conversion -> NVENC";
            }

            return "NVDEC -> NVENC";
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




