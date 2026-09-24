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
            public long? EncodedFrames { get; }
            public EncodeProgressBasis Basis { get; }
            public long? TotalFrames { get; }
            public bool TimestampStalled { get; }
            public bool TimestampAvailable { get; }
            public int Attempt { get; }

            public EncodeProgress(
                TimeSpan currentTime,
                TimeSpan totalDuration,
                double fps,
                double speed,
                double bitrateKbps,
                double percent,
                long? encodedFrames = null, EncodeProgressBasis basis = EncodeProgressBasis.Timestamp,
                long? totalFrames = null, bool timestampStalled = false, bool timestampAvailable = true,
                int attempt = 1)
            {
                CurrentTime = currentTime;
                TotalDuration = totalDuration;
                Fps = fps;
                Speed = speed;
                BitrateKbps = bitrateKbps;
                Percent = percent;
                EncodedFrames = encodedFrames;
                Basis = basis;
                TotalFrames = totalFrames;
                TimestampStalled = timestampStalled;
                TimestampAvailable = timestampAvailable;
                Attempt = attempt;
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
                        _log,
                        (path, token) => new SourceTimingAnalysisService(_ffprobePath, log: _log).AnalyzeAsync(path, token),
                        new FfmpegFullVideoDecodeCoverageService(_ffmpegPath)));

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
        public async Task<EncodeResult> EncodeWithResultAsync(EncodingRequest request)
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

            if (validated.Resolved.Selection.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase))
            {
                FfmpegNvencRuntimeCapability capability = await FfmpegNvencRuntimeCapabilityService.Shared
                    .CheckAsync(_ffmpegPath, validated.Resolved.Selection.FfmpegCodec, request.CancellationToken)
                    .ConfigureAwait(false);
                if (!capability.IsAvailable)
                    throw new InvalidOperationException(capability.Diagnostic);
            }

            return await EncodeInternalAsync(
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
                request.AiProgressCallback,
                request.SourceDecodeMode,
                request.DisableAutomaticFfmpegRecovery,
                request.FfmpegDiagnosticCallback,
                request.ValidationProfile,
                request.StructuredProgressCallback,
                request.EncodingPlanSnapshotCallback,
                 request.EncodingPlanDivergenceCallback,
                 request.EncodingExecutionOutcomeCallback,
                 request.RecoveryStatusCallback,
                 request.QualityIntent,
                request.QualityResolutionCallback,
                request.FailureDiagnosticReportCallback,
                request.SizePredictionCalibration).ConfigureAwait(false);
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
            Action<AiIntermediateProgress>? aiProgressCallback = null,
            FfmpegSourceDecodeMode sourceDecodeMode = FfmpegSourceDecodeMode.Strict,
            bool disableAutomaticFfmpegRecovery = false,
            Action<string>? ffmpegDiagnosticCallback = null,
            EncodeOutputValidationProfile validationProfile = EncodeOutputValidationProfile.Production,
            Action<EncodeProgress>? structuredProgressCallback = null,
            Action<EncodingPlanSnapshot>? encodingPlanSnapshotCallback = null,
             Action<EncodingPlanDivergence>? encodingPlanDivergenceCallback = null,
             Action<EncodingExecutionOutcome>? encodingExecutionOutcomeCallback = null,
             Action<EncodingRecoveryStatusUpdate>? recoveryStatusCallback = null,
             EncodingQualityIntent? qualityIntent = null,
            Action<EncodingQualityResolution>? qualityResolutionCallback = null,
            Action<string>? failureDiagnosticReportCallback = null,
            EncodingSizePredictionCalibration? sizePredictionCalibration = null)
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
                aiProgressCallback,
                sourceDecodeMode,
                disableAutomaticFfmpegRecovery,
                ffmpegDiagnosticCallback,
                validationProfile,
                structuredProgressCallback,
                encodingPlanSnapshotCallback,
                 encodingPlanDivergenceCallback,
                 encodingExecutionOutcomeCallback,
                 recoveryStatusCallback,
                 qualityIntent,
                qualityResolutionCallback,
                failureDiagnosticReportCallback,
                sizePredictionCalibration);
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
            Action<AiIntermediateProgress>? aiProgressCallback = null,
            FfmpegSourceDecodeMode sourceDecodeMode = FfmpegSourceDecodeMode.Strict,
            bool disableAutomaticFfmpegRecovery = false,
            Action<string>? ffmpegDiagnosticCallback = null,
            EncodeOutputValidationProfile validationProfile = EncodeOutputValidationProfile.Production,
            Action<EncodeProgress>? structuredProgressCallback = null,
            Action<EncodingPlanSnapshot>? encodingPlanSnapshotCallback = null,
             Action<EncodingPlanDivergence>? encodingPlanDivergenceCallback = null,
             Action<EncodingExecutionOutcome>? encodingExecutionOutcomeCallback = null,
             Action<EncodingRecoveryStatusUpdate>? recoveryStatusCallback = null,
             EncodingQualityIntent? qualityIntent = null,
            Action<EncodingQualityResolution>? qualityResolutionCallback = null,
            Action<string>? failureDiagnosticReportCallback = null,
            EncodingSizePredictionCalibration? sizePredictionCalibration = null)
        {
            restoration = VideoRestorationModeResolver.Resolve(restoration);
            var performance = new PerformanceTimingService();
            string? sourceTimelineRepairPath = null;
            string? sourceContainerRepairPath = null;
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

            try
            {
                Directory.CreateDirectory(outFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FfmpegStorageFailure storageFailure = FfmpegStorageFailureClassifier.Classify(ex, destinationOperation: true);
                string detail = storageFailure.IsReliable
                    ? storageFailure.Describe()
                    : "the output staging directory could not be created";
                throw new InvalidOperationException(
                    $"MediaFlux cannot start the encode because {detail}: '{outFolder}'. The original source was retained.", ex);
            }
            performance.SetHardwareSnapshot(HardwarePerformanceService.Capture(
                inputSource.SourcePath,
                AppPaths.AiIntermediatesDirectory,
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
            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.SourceProbe))
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
            MediaProbeResult physicalProbe = sourceProbe;
            MediaProbeStreamInfo? sourceVideo = sourceProbe.Streams.FirstOrDefault(
                stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            ProgramDurationDecision programDuration = ProgramDurationResolver.Resolve(sourceProbe);
            double? primaryVideoDuration = programDuration.PrimaryVideo is null
                ? null
                : ProgramDurationResolver.GetReliableDuration(programDuration.PrimaryVideo);
            _log?.Invoke($"[EncodingService] Container duration={sourceProbe.DurationSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}s; primary video duration={primaryVideoDuration?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}s; authoritative duration={programDuration.DurationSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}s; reason={programDuration.Reason}");
            SourceTimingAnalysis? sourceTiming = null;
            SourceTimingAnalysis? originalSourceTiming = null;
            SourceTimelineRecoveryResult? timelineRecovery = null;
            RationalFrameRate? preplannedTimelineReconstructionRate = null;
            bool requireRegeneratedOutputTimeline = false;
            if (inputSource.Kind == EncodingInputKind.File)
            {
                using PerformanceTimingService.PerformanceScope timingScope = performance.Measure(PerformanceTimingStage.SourceTimingAnalysis);
                sourceTiming = await new SourceTimingAnalysisService(_ffprobePath, log: _log)
                    .AnalyzeAsync(inputSource.SourcePath, cancellationToken).ConfigureAwait(false);
                originalSourceTiming = sourceTiming;
                timingScope.Complete();
                if (sourceTiming.Classification == SourceTimingClassification.IrregularUnsafe)
                {
                    EncodingSourceFailureClassification classification = EncodingSourceFailureClassifier.Classify(sourceTiming.Reason);
                    if (!classification.IsRecoveryCandidate || classification.Type != EncodingSourceFailureType.TimelineCorruption)
                        throw new InvalidOperationException("The source video has materially discontinuous or non-monotonic presentation timestamps. MediaFlux did not start an encode because it cannot prove a complete presentation timeline. The original source was retained.");

                    sourceTimelineRepairPath = Path.Combine(outFolder, $".mediaflux-timeline-repair-{Guid.NewGuid():N}.mkv");
                    _log?.Invoke($"[EncodingRecovery] Type=Timeline; Failure=SourceTimelineCorruption; InitialMode=Strict; RecoveryMode=TimestampNormalization; ProcessResult=NotStarted; MediaDisposition=NotAttempted; source={inputSource.SourcePath}; temporary={sourceTimelineRepairPath}.");
                    timelineRecovery = await new SourceTimelineRecoveryService(_ffmpegPath, _ffprobePath, log: _log)
                        .TryNormalizeAsync(inputSource.SourcePath, sourceTimelineRepairPath, sourceProbe, cancellationToken).ConfigureAwait(false);
                    if (!timelineRecovery.Success)
                    {
                        bool aiRestorationRequested = restoration is { AiMode: not AiRestorationMode.Off };
                        if (!sourceTiming.HasNonMonotonicTimestamps ||
                            timelineRecovery.FailureKind != SourceTimelineRecoveryFailureKind.StreamCopyPreservedUnsafeTiming || aiRestorationRequested || sourceVideo is null)
                            throw new InvalidOperationException($"The source video has a material timestamp defect and the bounded normalization attempt did not prove a safe timeline. {timelineRecovery.Reason} The original source was retained.");

                        MediaProbeStreamInfo[] reconstructionAudio = inputSource.HasExplicitStreamSelection
                            ? sourceProbe.Streams.Where(stream =>
                                stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                                inputSource.AudioStreamIndexes.Contains(stream.Index)).ToArray()
                            : mapMode == StreamMapMode.FirstAudioOnly
                                ? sourceProbe.Streams.Where(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase)).Take(1).ToArray()
                                : sourceProbe.Streams.Where(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase)).ToArray();
                        TimelineReconstructionEligibility reconstruction = await new TimelineReconstructionEligibilityService(_ffmpegPath, _ffprobePath, log: _log)
                            .EvaluateAsync(inputSource.SourcePath, sourceVideo, programDuration.DurationSeconds, reconstructionAudio, cancellationToken)
                            .ConfigureAwait(false);
                        _log?.Invoke($"[EncodingRecovery] TimelineReconstructionEligibility: source-non-monotonic-PTS=yes; stream-copy-still-non-monotonic=yes; ai-restoration=disabled; {reconstruction.DescribeEvidence()}");
                        if (!reconstruction.IsEligible || reconstruction.FrameRate is not { } provenRate)
                            throw new InvalidOperationException($"The source video has a material timestamp defect and stream-copy normalization preserved it. MediaFlux could not prove that deterministic decode/re-encode timestamp reconstruction is safe. {reconstruction.Reason} The original source was retained.");

                        preplannedTimelineReconstructionRate = provenRate;
                        requireRegeneratedOutputTimeline = true;
                        _log?.Invoke($"[EncodingRecovery] Stream-copy normalization preserved unsafe timing; using the existing encode once with proven frame-index timestamp reconstruction at {provenRate.Text}. {reconstruction.DescribeEvidence()}");
                    }

                    if (timelineRecovery.Success)
                    {
                        inputSource = inputSource.WithInputPath(timelineRecovery.RepairedPath);
                        physicalProbe = timelineRecovery.RepairedProbe!;
                        sourceVideo = physicalProbe.Streams.FirstOrDefault(stream =>
                            stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
                        sourceTiming = timelineRecovery.RepairedTiming;
                        _log?.Invoke($"[EncodingRecovery] Type=Timeline; Failure=SourceTimelineCorruption; InitialMode=Strict; RecoveryMode=TimestampNormalization; ProcessResult=Succeeded; MediaDisposition=Salvaged; Reason={timelineRecovery.Reason}");
                    }
                }
            }
            VideoEncoderSelection legacyEncoder =
                encoderSelection ??
                EncoderRegistry.Default.ResolveLegacyCodec(videoCodec).Selection;
            double? legacyTargetMb = targetMb;
            int? legacyAudioChannels = audioChannels;
            StreamMapMode legacyMapMode = mapMode;
            bool legacyCopySubtitles = copySubtitles;
            bool legacyCopyDataStreams = copyDataStreams;
            bool legacyCopyAttachments = copyAttachments;
            TimeSpan planKnownDuration = inputSource.KnownDurationSeconds is > 0
                ? TimeSpan.FromSeconds(inputSource.KnownDurationSeconds.Value)
                : programDuration.DurationSeconds is > 0
                    ? TimeSpan.FromSeconds(programDuration.DurationSeconds.Value)
                    : TimeSpan.Zero;
            var planContext = new EncodingDecisionContext(
                physicalProbe, inputSource, legacyEncoder, useGpu, targetMb, scaleMode,
                restoration?.Clone() ?? new VideoRestorationSettings(), encoderPreset ?? "", qualityValue, tenBit, audioChannels,
                mapMode, copySubtitles, copyDataStreams, copyAttachments, outputContainer,
                compatibilityPolicy, planKnownDuration, validationProfile,
                qualityIntent ?? EncodingQualityIntent.LegacyNumeric(qualityValue),
                timelineRecovery is { Success: true }
                    ? new EncodingSourceFailureClassification(
                        EncodingSourceFailureType.TimelineCorruption,
                        true,
                        "Preflight timestamp defect was repaired and revalidated.",
                        timelineRecovery.Reason)
                    : null,
                sizePredictionCalibration);
            EncodingPlan shadowPlan = EncodingPlanService.Create(planContext);
            var planSnapshot = new EncodingPlanSnapshot(shadowPlan.PlanId, shadowPlan);
            var preflightOutcomes = new List<EncodingPreflightOutcome>
            {
                new(EncodingPreflightCheckKind.SourceProbe, EncodingPreflightStatus.Passed),
                new(EncodingPreflightCheckKind.SourceTiming,
                    inputSource.Kind == EncodingInputKind.File ? EncodingPreflightStatus.Passed : EncodingPreflightStatus.Skipped,
                    inputSource.Kind == EncodingInputKind.File ? preplannedTimelineReconstructionRate is { } approvedRate ? $"Stream-copy normalization preserved unsafe timing; full-stream evidence approved one deterministic {approvedRate.Text} reconstruction encode with mandatory output timeline validation." : timelineRecovery is null ? "Existing source timing analysis passed." : timelineRecovery.Reason : "Not applicable to this input source.")
            };
            var recoveryOutcomes = new List<EncodingRecoveryOutcome>();
            if (timelineRecovery is { Success: true })
                recoveryOutcomes.Add(new EncodingRecoveryOutcome(
                    EncodingRecoveryKind.TimelineNormalization,
                    EncodingRecoveryFailureClass.SourceTimelineCorruption,
                    EncodingRecoveryMode.Strict,
                    EncodingRecoveryMode.SoftwareDecodeWithNvencAndTimestampReconstruction,
                    1, 1, EncodingRecoveryResult.Succeeded,
                    timelineRecovery.Reason,
                    EncodingRecoveryProcessResult.Succeeded,
                    EncodingRecoveryDisposition.Salvaged,
                    programDuration.DurationSeconds,
                    programDuration.DurationSeconds,
                    sourceVideo?.FrameCount,
                    sourceVideo?.FrameCount,
                    0,
                    0,
                    null,
                    timelineRecovery.Reason));
            EncodingValidationOutcome? validationOutcome = null;
            EncodingFinalizationOutcome? finalizationOutcome = null;
            // Keep an immutable placeholder so recovery records created before
            // the first finalization still have a well-defined evidence source.
            EncodeFinalizationResult finalization = new();
            EncodingTerminalResult terminalResult = EncodingTerminalResult.NotRun;
            void PublishExecutionOutcome()
            {
                encodingExecutionOutcomeCallback?.Invoke(new EncodingExecutionOutcome(
                    shadowPlan.PlanId, preflightOutcomes.ToArray(), recoveryOutcomes.ToArray(),
                    validationOutcome, finalizationOutcome, terminalResult));
            }
            void RecordRecovery(EncodingRecoveryKind kind, EncodingRecoveryFailureClass failureClass,
                EncodingRecoveryMode recoveryMode, int maximumAttempts, EncodingRecoveryResult result, string detail,
                EncodingRecoveryDisposition? forcedDisposition = null)
            {
                EncodingPlanDivergence? divergence = EncodingPlanService.CompareRecoveryAttempt(shadowPlan, kind, failureClass);
                if (divergence is not null)
                {
                    _log?.Invoke($"[EncodingPlan] Shadow divergence: {divergence}");
                    encodingPlanDivergenceCallback?.Invoke(divergence);
                }
                recoveryOutcomes.Add(new EncodingRecoveryOutcome(kind, failureClass,
                    EncodingRecoveryMode.Strict, recoveryMode, 1, maximumAttempts, result, detail,
                    result == EncodingRecoveryResult.NotStarted
                        ? EncodingRecoveryProcessResult.NotStarted
                        : result == EncodingRecoveryResult.Succeeded
                            ? EncodingRecoveryProcessResult.Succeeded
                            : EncodingRecoveryProcessResult.Failed,
                    forcedDisposition ?? (result == EncodingRecoveryResult.NotStarted
                        ? EncodingRecoveryDisposition.NotAttempted
                        : result == EncodingRecoveryResult.Failed
                            ? EncodingRecoveryDisposition.Rejected
                            : detail.Contains("validation=passed", StringComparison.OrdinalIgnoreCase) ||
                              detail.Contains("recovered-validation=passed", StringComparison.OrdinalIgnoreCase) ||
                              detail.Contains("retry-validation=passed", StringComparison.OrdinalIgnoreCase)
                                ? EncodingRecoveryDisposition.Salvaged
                                : EncodingRecoveryDisposition.Degraded),
                    DiagnosticReason: detail,
                    SourceDurationSeconds: finalization.StagedValidationResult?.FailureEvidence?.SourceDurationSeconds,
                    ProducedDurationSeconds: finalization.StagedValidationResult?.FailureEvidence?.OutputDurationSeconds,
                    ExpectedFrameCount: finalization.StagedValidationResult?.FailureEvidence?.ExpectedFrameCount,
                    ProducedFrameCount: finalization.StagedValidationResult?.FailureEvidence?.ActualFrameCount,
                    DroppedFrameOrPacketCount: finalization.StagedValidationResult?.FailureEvidence is { } deficit
                        ? Math.Max(0, deficit.ExpectedFrameCount - deficit.ActualFrameCount)
                        : null,
                    DurationDeltaSeconds: finalization.StagedValidationResult?.FailureEvidence is { } durationEvidence
                        ? Math.Abs(durationEvidence.SourceDurationSeconds - durationEvidence.OutputDurationSeconds)
                        : null));
                EncodingExecutionOutcome outcome = new(shadowPlan.PlanId, preflightOutcomes.ToArray(), recoveryOutcomes.ToArray(), validationOutcome, finalizationOutcome, terminalResult);
                _log?.Invoke(EncodingPlanService.DescribeRecovery(outcome));
                encodingExecutionOutcomeCallback?.Invoke(outcome);
            }
            EncodingPlanService.EncodingPlanExecutionValues planExecution =
                EncodingPlanService.GetExecutionValues(shadowPlan);
            // Phase 2/3 authority boundary: downstream FFmpeg/finalization
            // requests consume frozen plan values, never caller/UI values.
            VideoEncoderSelection requestedEncoder = planExecution.Encoder;
            VideoOutputGeometryPlan? plannedOutputGeometry = planExecution.Geometry;
            videoCodec = requestedEncoder.FfmpegCodec;
            useGpu = planExecution.UseGpu;
            targetMb = planExecution.TargetMb;
            qualityValue = planExecution.QualityResolution.EffectiveQuality ?? qualityValue;
            qualityResolutionCallback?.Invoke(planExecution.QualityResolution);
            encoderSelection = requestedEncoder;
            audioChannels = planExecution.AudioChannels;
            mapMode = planExecution.MapMode;
            copySubtitles = planExecution.CopySubtitles;
            copyDataStreams = planExecution.CopyDataStreams;
            copyAttachments = planExecution.CopyAttachments;
            // The legacy calculations are parity-only.  They execute after the
            // plan is frozen and are never used for command construction.
            VideoOutputResolutionPlan? finalOutputResolution = sourceVideo?.Width is > 0 && sourceVideo.Height is > 0
                ? VideoRestorationPipeline.ResolveFinalOutputResolution(sourceVideo.Width.Value, sourceVideo.Height.Value, restoration, scaleMode)
                : null;
            VideoOutputGeometryPlan? legacyOutputGeometry = sourceVideo?.Width is > 0 && sourceVideo.Height is > 0 && finalOutputResolution is not null
                ? VideoOutputGeometryPlanner.Resolve(
                    sourceVideo.Width.Value, sourceVideo.Height.Value,
                    finalOutputResolution, legacyEncoder, tenBit)
                : null;
            _log?.Invoke(EncodingPlanService.DescribeSummary(shadowPlan));
            encodingPlanSnapshotCallback?.Invoke(planSnapshot);
            PublishExecutionOutcome();
            if (plannedOutputGeometry is not null)
            {
                _log?.Invoke($"[EncodingService] Output geometry plan: source={plannedOutputGeometry.SourceWidth}x{plannedOutputGeometry.SourceHeight}; requested={plannedOutputGeometry.RequestedWidth}x{plannedOutputGeometry.RequestedHeight}; planned={plannedOutputGeometry.Width}x{plannedOutputGeometry.Height}; encoder={requestedEncoder.FfmpegCodec}; pixel-format={plannedOutputGeometry.PixelFormat}; reason={plannedOutputGeometry.Reason}.");
                if (plannedOutputGeometry.WasNormalized)
                    _log?.Invoke($"[EncodingService] Output geometry normalized: {plannedOutputGeometry.RequestedWidth}x{plannedOutputGeometry.RequestedHeight} -> {plannedOutputGeometry.Width}x{plannedOutputGeometry.Height}; reason={plannedOutputGeometry.Reason}.");
            }
            else
                _log?.Invoke("[EncodingService] Output geometry plan unavailable because FFprobe did not provide source dimensions.");
            AiIntermediateVideoResult? aiIntermediate = null;
            VideoRestorationPipelinePlan? aiPlan = null;
            if (restoration is { AiMode: not AiRestorationMode.Off } aiSettings)
            {
                MediaProbeStreamInfo? video = sourceVideo;
                if (video?.FrameRate is not > 0 || video.Width is not > 0 || video.Height is not > 0)
                    throw new AiRestorationValidationException("AI restoration requires a source with a known constant frame rate and resolution.");
                SourceTimingAnalysis timing = sourceTiming ?? await new SourceTimingAnalysisService(_ffprobePath, log: _log)
                    .AnalyzeAsync(inputSource.SourcePath, cancellationToken).ConfigureAwait(false);
                SourceTimingAnalysisService.EnsureCurrentCfrSupported(timing);
                IAiRestorationBackend backend = await new AiBackendManager(AppPaths.InstallDirectory, log: _log)
                    .SelectAsync(aiSettings, video.Width.Value, video.Height.Value, cancellationToken).ConfigureAwait(false);
                finalOutputResolution ??= VideoRestorationPipeline.ResolveFinalOutputResolution(video.Width.Value, video.Height.Value, aiSettings, scaleMode);
                bool restoreOriginalAfterAi = scaleMode == ScaleMode.None && VideoRestorationPipeline.Effective(aiSettings).Resize == VideoRestorationResize.Original;
                aiPlan = VideoRestorationPipeline.BuildPlan(aiSettings, scaleMode, restoreOriginalAfterAi ? plannedOutputGeometry?.ScaleFilter : null);
                callback("[MediaFlux] Preparing AI restoration.");
                var intermediate = new AiRestorationIntermediateVideoService(_ffmpegPath, _ffprobePath, AppPaths.AiIntermediatesDirectory, backend, log: _log, timing: performance);
                TimeSpan aiDuration = sampleDuration ?? TimeSpan.FromSeconds(programDuration.DurationSeconds ?? 0);
                int expectedFrames = AiRestorationIntermediateVideoService.ResolveExpectedFrameCount(
                    new AiIntermediateVideoRequest(inputSource.SourcePath, video.FrameRate.Value, TimeSpan.FromSeconds(programDuration.DurationSeconds ?? 0), aiSettings, aiPlan, sampleStart, sampleDuration, video.Width ?? 0, video.Height ?? 0, SourceFrameCount: video.FrameCount),
                    aiDuration);
                string stagingRoot = AppPaths.AiIntermediatesDirectory;
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

            OutputContainerDecision legacyContainerDecision = OutputContainerPolicy.Decide(
                outputContainer,
                sourceProbe,
                inputSource,
                legacyMapMode,
                legacyCopySubtitles,
                legacyCopyDataStreams,
                legacyCopyAttachments,
                audioWillBeTranscoded: legacyAudioChannels is > 0);
            foreach (EncodingPlanDivergence divergence in EncodingPlanService.Compare(
                         shadowPlan, legacyContainerDecision, legacyOutputGeometry, legacyEncoder,
                         legacyTargetMb, sourceDecodeMode))
            {
                _log?.Invoke($"[EncodingPlan] Shadow divergence: {divergence}");
                encodingPlanDivergenceCallback?.Invoke(divergence);
            }
            OutputContainerDecision containerDecision = planExecution.ContainerDecision;
            _log?.Invoke(
                $"[EncodingService] stage=ContainerResolution; configured={containerDecision.Requested}; " +
                $"effective={containerDecision.Resolved}; policy={compatibilityPolicy}; " +
                $"reason={containerDecision.Reason}");
            _log?.Invoke($"[EncodingService] {containerDecision.Reason}");
            containerDecisionCallback?.Invoke(containerDecision);
            foreach (StreamCompatibilityPlan plan in containerDecision.StreamPlans)
                _log?.Invoke($"[EncodingService] Stream {plan.StreamIndex} {plan.StreamType}/{plan.Codec}: requested={plan.RequestedAction}; decision={plan.Action}; target={plan.TargetCodec ?? "copy"}; {plan.Reason}");
            if (containerDecision.Resolved == OutputContainer.Mp4 &&
                compatibilityPolicy == ContainerCompatibilityPolicy.Strict &&
                !OutputContainerPolicy.CanProceedAutomatically(containerDecision, compatibilityPolicy))
                throw new InvalidOperationException($"Strict container compatibility policy rejected the stream plan: {OutputContainerPolicy.DescribeBlockingStreams(containerDecision)}");
            if (containerDecision.Resolved == OutputContainer.Mp4 &&
                containerDecision.HasUnsupportedMeaningfulStreams)
                throw new InvalidOperationException($"Container compatibility cannot safely preserve requested streams: {OutputContainerPolicy.DescribeBlockingStreams(containerDecision)}");
            if (containerDecision.Resolved == OutputContainer.Mp4 &&
                compatibilityPolicy == ContainerCompatibilityPolicy.AlwaysAsk &&
                containerDecision.RequiresConfirmation && !containerCompatibilityConfirmed)
                throw new InvalidOperationException("MP4 compatibility confirmation is required by the Always Ask policy.");
            if (containerDecision.Requested == OutputContainerSelection.Mp4)
            {
                foreach (string warning in containerDecision.CompatibilityWarnings)
                    _log?.Invoke($"[EncodingService] Container compatibility: {warning}.");
            }
            if (containerDecision.ConvertSubtitlesToMovText)
                _log?.Invoke("[EncodingService] ASS/SSA subtitles will be converted to mov_text; language/title/dispositions are retained where MP4 supports them, styling may be lost.");
            if (containerDecision.RequiresConfirmation && !containerCompatibilityConfirmed &&
                compatibilityPolicy != ContainerCompatibilityPolicy.AlwaysAsk)
            {
                _log?.Invoke(
                    "[EncodingService] Explicit MP4 compatibility was not preconfirmed by the caller; " +
                    "continuing for legacy API compatibility.");
            }

            SubtitleConversionPreflightResult subtitlePreflight;
            _log?.Invoke($"[EncodingService] stage=Preflight; effective={containerDecision.Resolved}; validating planned subtitle operations.");
            using (PerformanceTimingService.PerformanceScope subtitleScope = performance.Measure(PerformanceTimingStage.SubtitlePreflight))
            {
                subtitlePreflight = await new SubtitleConversionPreflightService(_ffmpegPath)
                    .ValidateAsync(inputSource, containerDecision, cancellationToken).ConfigureAwait(false);
                subtitleScope.Complete();
            }
            preflightOutcomes.Add(new EncodingPreflightOutcome(
                EncodingPreflightCheckKind.SubtitleConversion,
                subtitlePreflight.Success ? EncodingPreflightStatus.Passed : EncodingPreflightStatus.Failed,
                subtitlePreflight.Success ? "Existing subtitle preflight passed." : subtitlePreflight.ErrorMessage));
            PublishExecutionOutcome();
            if (!subtitlePreflight.Success)
            {
                if (compatibilityPolicy == ContainerCompatibilityPolicy.Intelligent)
                {
                    containerDecision = OutputContainerPolicy.ExcludeFailedTextSubtitle(
                        containerDecision, subtitlePreflight.StreamIndex!.Value, subtitlePreflight.ErrorMessage);
                    _log?.Invoke($"[EncodingService] {subtitlePreflight.ErrorMessage} Excluded only that subtitle stream under Intelligent policy. Diagnostics: {subtitlePreflight.Diagnostics}");
                }
                else
                {
                    throw new InvalidOperationException(subtitlePreflight.ErrorMessage +
                        " MediaFlux did not start the encode because the selected subtitle cannot be preserved under the current compatibility policy. The original source was retained.");
                }
            }

            _log?.Invoke("[EncodingService] Copied audio uses the normal encode path; no full-duration audio integrity preflight is performed.");

            // Keep the intended final name collision-safe, but write FFmpeg output
            // only to a hidden same-directory staging file until validation passes.
            string finalOutput = OutputPathService.GetCollisionSafePath(
                Path.Combine(outFolder, $"{name}{actualSuffix}{containerDecision.Extension}"));
            string output = OutputPathService.CreateEncodeStagingPath(finalOutput);
            _log?.Invoke(
                $"[EncodingService] stage=OutputAllocation; effective={containerDecision.Resolved}; " +
                $"output='{finalOutput}'; staged='{output}'");
            outputPathCallback?.Invoke(finalOutput);
            stagingPathCallback?.Invoke(output);
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

            bool forceMp4CompatibleAudio = (isAsfFamilyInput &&
                string.Equals(Path.GetExtension(finalOutput), ".mp4", StringComparison.OrdinalIgnoreCase)) ||
                containerDecision.TranscodeAudioToAac;
            if (validationProfile == EncodeOutputValidationProfile.SampleComparison &&
                forceMp4CompatibleAudio)
            {
                // The sample command intentionally uses its global AAC audio
                // selection when any selected stream requires conversion. Keep
                // validation aligned with that effective sample output plan;
                // production validation continues to use the original decision.
                containerDecision = OutputContainerPolicy.ResolveEffectiveGlobalAacAudioPlan(
                    containerDecision,
                    globalAacRequired: true);
            }
            if (forceMp4CompatibleAudio)
            {
                _log?.Invoke(isAsfFamilyInput
                    ? "[EncodingService] WMV/ASF input detected for MP4 output; transcoding audio to AAC."
                    : "[EncodingService] Container plan requires compatible AAC audio conversion.");
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
            long? progressTotalFrames = sampleDuration is { } benchmarkWindow && sourceVideo?.FrameRate is > 0
                ? (long)Math.Round(benchmarkWindow.TotalSeconds * sourceVideo.FrameRate.Value)
                : sourceVideo?.FrameCount;

            string sourcePixelFormat = sourceProbe.Streams.FirstOrDefault(stream => stream.CodecType.Equals(
                "video", StringComparison.OrdinalIgnoreCase))?.PixelFormat ?? "";
            string ffArgs;
            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
            {
            _log?.Invoke($"[EncodingService] stage=CommandBuild; effective={containerDecision.Resolved}; building FFmpeg command.");
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
                recoveryFrameRateRational: preplannedTimelineReconstructionRate?.Text,
                timestampReconstructionFilter: preplannedTimelineReconstructionRate is { } plannedReconstructionRate
                    ? $"setpts=N*{plannedReconstructionRate.Denominator}/{plannedReconstructionRate.Numerator}/TB"
                    : null,
                disableHardwareDecode: preplannedTimelineReconstructionRate is not null,
                restoration: restoration, splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource), restorationFilterOverride: aiPlan?.PostAiFilterChain,
                plannedVideoGeometry: plannedOutputGeometry,
                sourceDecodeMode: preplannedTimelineReconstructionRate is null
                    ? sourceDecodeMode
                    : FfmpegSourceDecodeMode.RecoverVideoWithTimestampReconstruction);
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
                $"[EncodingService] stage=FfmpegLaunch; ffmpeg-launched=true; '{inputSource.SourcePath}' " +
                $"using '{input}' -> staged '{output}' (final '{finalOutput}')");
            _log?.Invoke($"[EncodingService] ffmpeg arguments: {ffArgs}");

            FfmpegProcessResult runResult;
            int ffmpegAttempt = 0;
            using (PerformanceTimingService.PerformanceScope initialEncodeScope = performance.Measure(PerformanceTimingStage.FinalEncode))
            {
                runResult = await RunFfmpegAsync(
                    ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback,
                    progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification,
                    ++ffmpegAttempt).ConfigureAwait(false);
                initialEncodeScope.Complete();
            }

            string recoveryDiagnostics = "";
            bool sourceContainerRecoveryAttempted = false;
            bool sourceContainerRecoveryRetryAttempted = false;
            bool sourceUnrecoverable = false;
            bool tolerantSalvageAttempted = false;
            bool tolerantSalvageAudioReconstructed = false;
            string tolerantSalvageDetail = "";
            bool cudaRecoveryAttempted = false;
            bool cudaRecoveryStarted = false;
            FfmpegSourceDecodeCorruption initialSourceCorruption =
                FfmpegSourceDecodeCorruptionClassifier.Classify(runResult.StandardError);
            FfmpegDiagnosticSummary initialDiagnosticSummary = runResult.DiagnosticSummary;
            string initialStandardError = runResult.StandardError;
            bool strongInitialSourceCorruption =
                FfmpegSourceDecodeCorruptionClassifier.HasStrongSourceIntegrityEvidence(
                    initialSourceCorruption, runResult.DiagnosticSummary);
            if (!disableAutomaticFfmpegRecovery &&
                inputSource.Kind == EncodingInputKind.File &&
                strongInitialSourceCorruption)
            {
                recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.SourceCorruptionDetected));
                sourceContainerRecoveryAttempted = true;
                sourceContainerRepairPath = Path.Combine(outFolder, $".mediaflux-source-repair-{Guid.NewGuid():N}.mkv");
                string initialEvidence = initialSourceCorruption.MatchedEvidence.Count > 0
                    ? initialSourceCorruption.DescribeEvidence()
                    : string.Join(" | ", runResult.DiagnosticSummary.Classification.SupportingFamilies);
                _log?.Invoke($"[EncodingRecovery] Type=SourceContainerRemux; Failure=SourceIntegrity; InitialMode=Strict; RecoveryMode=StreamCopyRemux; ProcessResult=NotStarted; MediaDisposition=NotAttempted; source={inputSource.SourcePath}; temporary={sourceContainerRepairPath}; evidence={initialEvidence}.");
                recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.AttemptingSourceRecovery));
                callback("[MediaFlux] Source corruption detected; attempting one container remux for verification.");
                if (TryDeleteFailedStagingOutput(output))
                {
                    SourceContainerRecoveryResult repair = await new SourceContainerRecoveryService(
                        _ffmpegPath, _ffprobePath, log: _log)
                        .TryRemuxAndValidateAsync(
                            inputSource.SourcePath,
                            sourceContainerRepairPath,
                            sourceProbe,
                            programDuration.DurationSeconds,
                            cancellationToken,
                            () => recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.ValidatingRecoveredSource))).ConfigureAwait(false);
                    string repairDetail = $"{initialEvidence}; {repair.Reason}; recovery-validation={(repair.ValidationPassed ? "passed" : "failed")}";
                    RecordRecovery(
                        EncodingRecoveryKind.SourceContainerRemux,
                        EncodingRecoveryFailureClass.SourceContainerCorruption,
                        EncodingRecoveryMode.StreamCopyRemux,
                        1,
                        repair.Success ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                        repairDetail,
                        repair.Success ? null : EncodingRecoveryDisposition.Rejected);
                    if (repair.Success && repair.RepairedProbe is not null)
                    {
                        inputSource = inputSource.WithInputPath(repair.RepairedPath);
                        input = inputSource.InputPath;
                        sourceProbe = repair.RepairedProbe;
                        physicalProbe = repair.RepairedProbe;
                        sourceVideo = sourceProbe.Streams.FirstOrDefault(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
                        programDuration = ProgramDurationResolver.Resolve(sourceProbe);
                        recoveryDiagnostics += $"Source container remux recovery succeeded; validation passed; repaired source={repair.RepairedPath}.{Environment.NewLine}";
                        _log?.Invoke("[EncodingRecovery] Source container remux validation passed; retrying the frozen encode plan exactly once.");
                        recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.RetryingWithRecoveredSource));
                        sourceContainerRecoveryRetryAttempted = true;
                        callback("[MediaFlux] Source recovery validation passed; retrying the original encode plan once.");
                        ffArgs = BuildFfmpegArgs(
                            inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                            encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                            mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                            containerDecision, forceMp4CompatibleAudio, totalDuration,
                            qualityValue, encoderSelection, sampleStart, sampleDuration,
                            sourcePixelFormat, recoveryFrameRateRational: preplannedTimelineReconstructionRate?.Text,
                            timestampReconstructionFilter: preplannedTimelineReconstructionRate is { } plannedRate
                                ? $"setpts=N*{plannedRate.Denominator}/{plannedRate.Numerator}/TB" : null,
                            disableHardwareDecode: preplannedTimelineReconstructionRate is not null,
                            restoration: restoration, splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource),
                            restorationFilterOverride: aiPlan?.PostAiFilterChain,
                            plannedVideoGeometry: plannedOutputGeometry,
                            sourceDecodeMode: preplannedTimelineReconstructionRate is null
                                ? sourceDecodeMode : FfmpegSourceDecodeMode.RecoverVideoWithTimestampReconstruction);
                        using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FinalEncode))
                        {
                            runResult = await RunFfmpegAsync(ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback, progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                            scope.Complete();
                        }
                    }
                    else
                    {
                        if (repair.IndicatesMediaFailure)
                        {
                            bool eligibleForTolerantSalvage =
                                inputSource.Kind == EncodingInputKind.File &&
                                sourceVideo?.CodecName.Equals("h264", StringComparison.OrdinalIgnoreCase) == true &&
                                !cancellationToken.IsCancellationRequested;
                            if (!eligibleForTolerantSalvage)
                            {
                                sourceUnrecoverable = true;
                                recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.SourceUnrecoverable));
                                recoveryDiagnostics += $"Source container remux recovery failed; Tier 2 was not eligible; original source preserved.{Environment.NewLine}";
                            }
                            else if (TryDeleteFailedStagingOutput(output))
                            {
                                SourceAudioDecodePreflightResult audioPreflight = await new SourceAudioDecodePreflightService(_ffmpegPath)
                                    .ValidateCopiedStreamsAsync(inputSource, containerDecision, cancellationToken).ConfigureAwait(false);
                                if (!audioPreflight.Success && !audioPreflight.IsReliableCorruption)
                                {
                                    sourceUnrecoverable = true;
                                    recoveryDiagnostics += $"Tier 2 was blocked because copied audio could not be conclusively validated: {audioPreflight.ErrorMessage} Original source preserved.{Environment.NewLine}";
                                    recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.SourceUnrecoverable));
                                }
                                else
                                {
                                    if (!audioPreflight.Success && audioPreflight.StreamIndex is int corruptAudioStream)
                                    {
                                        containerDecision = OutputContainerPolicy.RecoverCopiedAudio(containerDecision, corruptAudioStream);
                                        containerDecisionCallback?.Invoke(containerDecision);
                                        tolerantSalvageAudioReconstructed = true;
                                    }

                                    tolerantSalvageAttempted = true;
                                    recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.AttemptingDegradedSourceSalvage));
                                    callback("[MediaFlux] Attempting degraded source salvage with tolerant software decode; damaged media may be discarded.");
                                    tolerantSalvageDetail = $"Tier 1 rejected copied corruption; strategy=TolerantDecodeReencode; software-decode=yes; audio={(tolerantSalvageAudioReconstructed ? "reconstructed" : "validated-copy")}; damaged packets/frames may have been discarded.";
                                    _log?.Invoke($"[EncodingRecovery] Type=TolerantDecodeReencode; Failure=SourceContainerCorruption; InitialMode=Strict; RecoveryMode=TolerantDecodeReencode; source={inputSource.SourcePath}; {tolerantSalvageDetail}");
                                    ffArgs = BuildFfmpegArgs(
                                        inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                                        encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                                        mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                                        containerDecision, forceMp4CompatibleAudio, totalDuration,
                                        qualityValue, encoderSelection, sampleStart, sampleDuration,
                                        sourcePixelFormat, disableHardwareDecode: true, restoration: restoration,
                                        splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource),
                                        restorationFilterOverride: aiPlan?.PostAiFilterChain,
                                        plannedVideoGeometry: plannedOutputGeometry,
                                        sourceDecodeMode: FfmpegSourceDecodeMode.TolerantDecodeReencode);
                                    using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.VideoDecodeRecovery))
                                    {
                                        runResult = await RunFfmpegAsync(ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback, progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                                        scope.Complete();
                                    }
                                    RecordRecovery(EncodingRecoveryKind.TolerantDecodeReencode,
                                        EncodingRecoveryFailureClass.SourceContainerCorruption,
                                        EncodingRecoveryMode.TolerantDecodeReencode, 1,
                                        runResult.ExitCode == 0 ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                                        tolerantSalvageDetail + $" process-exit={runResult.ExitCode}; validation=pending.",
                                        EncodingRecoveryDisposition.Degraded);
                                    if (runResult.ExitCode == 0)
                                    {
                                        recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.ValidatingSalvagedMedia));
                                        callback("[MediaFlux] Validating salvaged media before finalization.");
                                    }
                                    else
                                    {
                                        sourceUnrecoverable = true;
                                        recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.SourceUnrecoverable));
                                    }
                                }
                            }
                            else
                            {
                                sourceUnrecoverable = true;
                                recoveryDiagnostics += "Tier 2 was not started because the failed staged output could not be removed safely. Original source preserved." + Environment.NewLine;
                                recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.SourceUnrecoverable));
                            }
                        }
                        else
                        {
                            recoveryDiagnostics += $"Source container remux recovery infrastructure failed; source recoverability was not established; original source preserved.{Environment.NewLine}";
                        }
                    }
                }
                else
                {
                    sourceUnrecoverable = true;
                    recoveryStatusCallback?.Invoke(new(EncodingRecoveryStatusKind.SourceUnrecoverable));
                    RecordRecovery(
                        EncodingRecoveryKind.SourceContainerRemux,
                        EncodingRecoveryFailureClass.SourceContainerCorruption,
                        EncodingRecoveryMode.StreamCopyRemux,
                        1,
                        EncodingRecoveryResult.NotStarted,
                        "Failed staged output could not be removed safely.",
                        EncodingRecoveryDisposition.SourceUnrecoverable);
                }
                terminalResult = sourceUnrecoverable ? EncodingTerminalResult.SourceUnrecoverable : terminalResult;
                PublishExecutionOutcome();
            }
            if (!sourceUnrecoverable && sourceContainerRecoveryRetryAttempted && runResult.ExitCode != 0)
            {
                FfmpegSourceDecodeCorruption retrySourceCorruption =
                    FfmpegSourceDecodeCorruptionClassifier.Classify(runResult.StandardError);
                if (FfmpegSourceDecodeCorruptionClassifier.HasStrongSourceIntegrityEvidence(
                        retrySourceCorruption, runResult.DiagnosticSummary))
                {
                    sourceUnrecoverable = true;
                    recoveryDiagnostics += "The single frozen-plan retry reproduced strong source-integrity corruption; recursive recovery was not attempted." + Environment.NewLine;
                    terminalResult = EncodingTerminalResult.SourceUnrecoverable;
                    PublishExecutionOutcome();
                }
            }
            if (sourceUnrecoverable && runResult.ExitCode == 0)
            {
                // The process may have completed while emitting fatal source
                // evidence; route the bounded recovery failure through the
                // existing diagnostic/reporting path without validating the
                // deleted staged output.
                runResult = runResult with { ExitCode = 1 };
            }
            FfmpegCudaNvdecFailure cudaFailure = FfmpegCudaNvdecFailureClassifier.Classify(runResult.StandardError);
            if (!sourceUnrecoverable && !sourceContainerRecoveryAttempted && !disableAutomaticFfmpegRecovery && runResult.ExitCode != 0 &&
                FfmpegCudaNvdecFailureClassifier.ShouldRetryOnce(
                    requestedEncoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase),
                    ffArgs.Contains("-hwaccel cuda ", StringComparison.Ordinal),
                    cancellationToken.IsCancellationRequested,
                    cudaRecoveryAttempted,
                    cudaFailure))
            {
                string failedPipeline = pipelineDiagnostic;
                cudaRecoveryAttempted = true;
                recoveryDiagnostics = $"Original NVDEC/CUDA failure: {cudaFailure.DescribeEvidence()}.{Environment.NewLine}";
                _log?.Invoke($"[EncodingService] GPU decode failure classified: {cudaFailure.DescribeEvidence()}. Attempt 1: {failedPipeline}.");
                _log?.Invoke("[EncodingService] Retry reason: classified NVDEC/CUDA device failure; removing hardware decode while retaining NVENC.");
                callback("[MediaFlux] GPU decode failed; retrying once with software decode and NVENC.");
                if (TryDeleteFailedStagingOutput(output))
                {
                    cudaRecoveryStarted = true;
                    using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
                    {
                        ffArgs = BuildFfmpegArgs(
                            inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                            encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                            mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                            containerDecision, forceMp4CompatibleAudio, totalDuration,
                            qualityValue, encoderSelection, sampleStart, sampleDuration,
                            sourcePixelFormat, disableHardwareDecode: true, restoration: restoration,
                            splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource),
                            restorationFilterOverride: aiPlan?.PostAiFilterChain,
                            plannedVideoGeometry: plannedOutputGeometry);
                        scope.Complete();
                    }
                    pipelineDiagnostic = DescribeVideoPipeline(inputSource, videoCodec, useGpu, tenBit, ffArgs);
                    _log?.Invoke($"[EncodingService] Attempt 2: software decode -> NVENC; pipeline={pipelineDiagnostic}; ffmpeg arguments: {ffArgs}");
                    using (PerformanceTimingService.PerformanceScope retryScope = performance.Measure(PerformanceTimingStage.FinalEncode))
                    {
                        runResult = await RunFfmpegAsync(ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback, progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                        retryScope.Complete();
                    }
                    _log?.Invoke($"[EncodingService] NVDEC/CUDA recovery retry result: exit={runResult.ExitCode}.");
                    RecordRecovery(EncodingRecoveryKind.HardwareDecode, EncodingRecoveryFailureClass.NvdecCudaFailure,
                        EncodingRecoveryMode.SoftwareDecodeWithNvenc, 1,
                        runResult.ExitCode == 0 ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                        cudaFailure.DescribeEvidence());
                }
                else
                {
                    recoveryDiagnostics += "Recovery retry was not started because the failed staged output could not be removed safely." + Environment.NewLine;
                    RecordRecovery(EncodingRecoveryKind.HardwareDecode, EncodingRecoveryFailureClass.NvdecCudaFailure,
                        EncodingRecoveryMode.SoftwareDecodeWithNvenc, 1, EncodingRecoveryResult.NotStarted,
                        "Failed staged output could not be removed safely.");
                }
            }

            if (!sourceUnrecoverable && !sourceContainerRecoveryAttempted && !disableAutomaticFfmpegRecovery && runResult.ExitCode != 0 &&
                !cudaRecoveryAttempted &&
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
                    sourcePixelFormat, preferNvencGpuResidentFrames: false, restoration: restoration, splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource), restorationFilterOverride: aiPlan?.PostAiFilterChain,
                    plannedVideoGeometry: plannedOutputGeometry);
                scope.Complete();
                }
                pipelineDiagnostic = DescribeVideoPipeline(
                    inputSource, videoCodec, useGpu, tenBit, ffArgs);
                _log?.Invoke(
                    $"[EncodingService] Pipeline fallback: {failedPipeline} -> " +
                    $"{pipelineDiagnostic}. ffmpeg arguments: {ffArgs}");
                using (PerformanceTimingService.PerformanceScope retryScope = performance.Measure(PerformanceTimingStage.FinalEncode))
                {
                    runResult = await RunFfmpegAsync(
                        ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback, progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                    retryScope.Complete();
                }
                RecordRecovery(EncodingRecoveryKind.GpuFramePipeline, EncodingRecoveryFailureClass.GpuFramePipelineFailure,
                    EncodingRecoveryMode.SoftwareFrames, 1,
                    runResult.ExitCode == 0 ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                    "Existing GPU-resident frame negotiation fallback.");
            }

            bool audioRecoveryAttempted = false;
            if (!sourceUnrecoverable && !sourceContainerRecoveryAttempted && !disableAutomaticFfmpegRecovery && runResult.ExitCode != 0 &&
                compatibilityPolicy == ContainerCompatibilityPolicy.Intelligent &&
                !cancellationToken.IsCancellationRequested)
            {
                int? corruptAudioStream = SourceAudioDecodePreflightService.FindCorruptAudioStreamIndex(runResult.StandardError);
                int audioStreamIndex = corruptAudioStream ?? -1;
                StreamCompatibilityPlan? copiedPlan = corruptAudioStream is not null
                    ? containerDecision.StreamPlans.FirstOrDefault(plan => plan.StreamIndex == audioStreamIndex &&
                        plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                        plan.Action == StreamCompatibilityAction.Copy)
                    : null;
                if (copiedPlan is not null)
                {
                    audioRecoveryAttempted = true;
                    string recoveryCodec = OutputContainerPolicy.SafeAudioRecoveryCodec(containerDecision.Resolved);
                        _log?.Invoke($"[EncodingService] Audio corruption detected: stream #{audioStreamIndex}; Copy -> Transcode; codec={recoveryCodec}.");
                        _log?.Invoke($"[EncodingService] Audio recovery initiated for stream #{audioStreamIndex}; policy=Intelligent; attempt=1.");
                        callback($"[MediaFlux] Recovering audio stream #{audioStreamIndex} by transcoding to {recoveryCodec}.");
                        if (TryDeleteFailedStagingOutput(output))
                        {
                            containerDecision = OutputContainerPolicy.RecoverCopiedAudio(containerDecision, audioStreamIndex);
                            containerDecisionCallback?.Invoke(containerDecision);
                            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
                            {
                                ffArgs = BuildFfmpegArgs(
                                    inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                                    encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                                    mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                                    containerDecision, forceMp4CompatibleAudio, totalDuration,
                                    qualityValue, encoderSelection, sampleStart, sampleDuration,
                                    sourcePixelFormat, restoration: restoration,
                                    splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource),
                                    restorationFilterOverride: aiPlan?.PostAiFilterChain,
                                    plannedVideoGeometry: plannedOutputGeometry,
                                    sourceDecodeMode: FfmpegSourceDecodeMode.RecoverAudio);
                                scope.Complete();
                            }
                            using (PerformanceTimingService.PerformanceScope retryScope = performance.Measure(PerformanceTimingStage.AudioIntegrityRecovery))
                            {
                                runResult = await RunFfmpegAsync(ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback, progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                                retryScope.Complete();
                            }
                            _log?.Invoke(runResult.ExitCode == 0
                                ? $"[EncodingService] Audio recovery success: stream #{audioStreamIndex} transcoded to {recoveryCodec}; continuing through normal validation/finalization."
                                : $"[EncodingService] Audio recovery failure: stream #{audioStreamIndex}; exit={runResult.ExitCode}; bounded diagnostics retained.");
                            RecordRecovery(EncodingRecoveryKind.AudioStream, EncodingRecoveryFailureClass.SourceAudioCorruption,
                                EncodingRecoveryMode.AudioTranscode, 1,
                                runResult.ExitCode == 0 ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                                $"Copied audio stream #{audioStreamIndex} -> {recoveryCodec}.");
                        }
                        else
                        {
                            _log?.Invoke($"[EncodingService] Audio recovery failure: stream #{audioStreamIndex}; failed staged output could not be removed safely.");
                            RecordRecovery(EncodingRecoveryKind.AudioStream, EncodingRecoveryFailureClass.SourceAudioCorruption,
                                EncodingRecoveryMode.AudioTranscode, 1, EncodingRecoveryResult.NotStarted,
                                "Failed staged output could not be removed safely.");
                        }
                }
            }

            bool videoRecoveryAttempted = preplannedTimelineReconstructionRate is not null || tolerantSalvageAttempted;
            if (!sourceUnrecoverable && !sourceContainerRecoveryAttempted && !disableAutomaticFfmpegRecovery && runResult.ExitCode != 0)
            {
                FfmpegVideoDecodeRecoveryDecision recovery = FfmpegVideoDecodeRecoveryPolicy.Evaluate(
                    runResult.StandardError,
                    compatibilityPolicy,
                    cancellationToken.IsCancellationRequested,
                    videoRecoveryAttempted,
                    requestedEncoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase),
                    output);
                if (recovery.Eligible)
                {
                    videoRecoveryAttempted = true;
                    _log?.Invoke($"[EncodingService] Reliable source video corruption detected on strict attempt. Evidence: {recovery.Evidence}.");
                    _log?.Invoke("[EncodingService] Intelligent recovery: retrying once with tolerant source-video decode handling. Encoder/settings unchanged.");
                    callback("[MediaFlux] Recovering source video decode once; encoder and settings are unchanged.");
                    recoveryDiagnostics += $"Strict source-video corruption evidence: {recovery.Evidence}.{Environment.NewLine}";
                    if (TryDeleteFailedStagingOutput(output))
                    {
                        using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
                        {
                            ffArgs = BuildFfmpegArgs(
                                inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                                encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                                mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                                containerDecision, forceMp4CompatibleAudio, totalDuration,
                                qualityValue, encoderSelection, sampleStart, sampleDuration,
                                sourcePixelFormat, restoration: restoration,
                                splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource),
                                restorationFilterOverride: aiPlan?.PostAiFilterChain,
                                plannedVideoGeometry: plannedOutputGeometry,
                                sourceDecodeMode: FfmpegSourceDecodeMode.RecoverVideo);
                            scope.Complete();
                        }
                        using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.VideoDecodeRecovery))
                        {
                            runResult = await RunFfmpegAsync(ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback, progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                            scope.Complete();
                        }
                        if (runResult.ExitCode == 0)
                            _log?.Invoke("[EncodingService] Video decode recovery succeeded; validating recovered output.");
                        else
                            _log?.Invoke("[EncodingService] Video decode recovery failed; no further recovery attempts will be made.");
                        RecordRecovery(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.SourceVideoCorruption,
                            EncodingRecoveryMode.Tolerant, 1,
                            runResult.ExitCode == 0 ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                            recovery.Evidence);
                    }
                    else
                    {
                        _log?.Invoke("[EncodingService] Video decode recovery failed; failed staged output could not be removed safely.");
                        RecordRecovery(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.SourceVideoCorruption,
                            EncodingRecoveryMode.Tolerant, 1, EncodingRecoveryResult.NotStarted,
                            "Failed staged output could not be removed safely.");
                    }
                }
            }

            if (runResult.ExitCode != 0)
            {
                FailureDiagnosticReportArtifact? diagnosticArtifacts = null;
                string diagnosticArtifactNote = "";
                try
                {
                    FfmpegDiagnosticSummary reportDiagnostics = sourceContainerRecoveryAttempted
                        ? initialDiagnosticSummary
                        : runResult.DiagnosticSummary;
                    string reportStandardError = sourceContainerRecoveryAttempted
                        ? initialStandardError + Environment.NewLine + runResult.StandardError
                        : runResult.StandardError;
                    terminalResult = sourceUnrecoverable
                        ? EncodingTerminalResult.SourceUnrecoverable
                        : recoveryOutcomes.Count > 0
                        ? EncodingTerminalResult.RecoveryFailed
                        : EncodingTerminalResult.EncodeFailed;
                    EncodingExecutionOutcome failureOutcome = new(
                        shadowPlan.PlanId, preflightOutcomes.ToArray(), recoveryOutcomes.ToArray(),
                        validationOutcome, finalizationOutcome, terminalResult);
                    string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
                        "Encode", inputSource.SourcePath, output, runResult.ExitCode, "FFmpeg process failure",
                        reportDiagnostics, reportStandardError, shadowPlan, failureOutcome,
                        SourceContainer: sourceProbe.FormatName,
                        SourceSizeBytes: inputSource.Kind == EncodingInputKind.File && File.Exists(inputSource.SourcePath)
                            ? new FileInfo(inputSource.SourcePath).Length : null));
                    diagnosticArtifacts = ErrorLogService.TryWriteFailureDiagnosticArtifacts(
                        _appPath, report, reportStandardError);
                    diagnosticArtifactNote = diagnosticArtifacts is null
                        ? "Failure diagnostic artifact generation was unavailable; rolling raw error logging was preserved."
                        : $"Failure diagnostic report: {diagnosticArtifacts.ReportPath}{Environment.NewLine}Raw captured stderr artifact: {diagnosticArtifacts.RawEvidencePath}";
                    try
                    {
                        failureDiagnosticReportCallback?.Invoke(diagnosticArtifacts is null
                            ? report
                            : report + Environment.NewLine + Environment.NewLine + diagnosticArtifactNote);
                    }
                    catch
                    {
                        // Presentation observers must not replace the terminal encode failure.
                    }
                }
                catch (Exception reportException)
                {
                    diagnosticArtifactNote = "Failure diagnostic report generation failed; rolling raw error logging was preserved. " + reportException.Message;
                }
                string logPath = ErrorLogService.Append(
                    _appPath,
                    "FFmpeg encode failed",
                    inputSource.SourcePath,
                    details:
                    $"Output     : {output}{Environment.NewLine}" +
                    $"Exit Code  : {runResult.ExitCode}{Environment.NewLine}" +
                    $"Arguments  : {ffArgs}{Environment.NewLine}{Environment.NewLine}" +
                    diagnosticArtifactNote + Environment.NewLine + Environment.NewLine +
                    recoveryDiagnostics +
                    "FFmpeg Output:" + Environment.NewLine +
                    (sourceContainerRecoveryAttempted
                        ? initialStandardError + Environment.NewLine + runResult.StandardError
                        : runResult.StandardError));

                _log?.Invoke($"[EncodingService] ffmpeg exited with code {runResult.ExitCode}. See central log: {logPath}");
                if (sourceUnrecoverable)
                {
                    TryDeleteFailedStagingOutput(output);
                    throw new InvalidOperationException(
                        "Source media is damaged. MediaFlux detected extensive corruption in the source video stream. " +
                        "The source could not be decoded reliably and automated recovery was unsuccessful. Encoding was stopped " +
                        "to prevent creation of an incomplete or corrupted output file. The original source was preserved. " +
                        $"See central log: {logPath}");
                }
                string recoverySuffix = !cudaRecoveryAttempted
                    ? ""
                    : cudaRecoveryStarted
                        ? " The software-decode NVENC recovery attempt also failed; both attempt diagnostics were recorded."
                        : " The NVDEC/CUDA recovery retry was not started; diagnostics were recorded.";
                FfmpegStorageFailure storageFailure =
                    FfmpegStorageFailureClassifier.Classify(runResult.StandardError, output);
                if (storageFailure.IsReliable)
                    throw new InvalidOperationException(
                        $"FFmpeg stopped because {storageFailure.Describe()}. The partial staged output was not finalized; existing recovery policy controls its retention. The original source was retained. See central log: {logPath}");
                FfmpegNvencFailure nvencFailure = requestedEncoder.EncoderId.Equals(
                    VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase)
                    ? FfmpegNvencFailureClassifier.Classify(runResult.StandardError)
                    : new FfmpegNvencFailure(FfmpegNvencFailureKind.None, Array.Empty<string>());
                if (nvencFailure.IsReliable)
                    throw new InvalidOperationException(
                        $"FFmpeg stopped because {nvencFailure.Describe()}. MediaFlux did not retry with a different encoder because that would change the requested encoding policy. The partial staged output was not finalized; existing recovery policy controls its retention. The original source was retained. See central log: {logPath}");
                FfmpegSourceDecodeCorruption sourceDecodeCorruption =
                    FfmpegSourceDecodeCorruptionClassifier.Classify(runResult.StandardError);
                FfmpegSourceTruncation sourceTruncation =
                    FfmpegSourceTruncationClassifier.Classify(runResult.StandardError);
                if (sourceTruncation.IsReliable)
                    throw new InvalidOperationException("FFmpeg stopped because the source media appears truncated or incomplete. The original source was retained. See central log: " + logPath);
                if (sourceDecodeCorruption.IsReliable)
                    throw new InvalidOperationException("FFmpeg stopped because the source video contains undecodable or corrupt H.264 data. The original source was retained. See central log: " + logPath);
                string audioSuffix = audioRecoveryAttempted
                    ? " The one-time Intelligent audio recovery attempt also failed; the original source was retained."
                    : "";
                string videoSuffix = videoRecoveryAttempted
                    ? " The one-time Intelligent video decode recovery attempt also failed; the original source was retained."
                    : "";
                throw new InvalidOperationException($"ffmpeg exited with code {runResult.ExitCode}.{recoverySuffix}{audioSuffix}{videoSuffix} See central log: {logPath}");
            }
            _log?.Invoke(
                "[EncodingService] ffmpeg completed successfully; validating staged output.");
            EncodeOutputValidationRequest BuildValidationRequest(
                RecoverableSourceBaseline? recoverableBaseline = null,
                EncodingSourceFailureClassification? sourceFailure = null) => new()
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
                        RecoverableSourceBaseline = recoverableBaseline,
                        SourceFailureClassification = sourceFailure,
                        // Only the original source timing can justify existing
                        // frame-deficit exceptions; repaired timing is execution-only.
                        SourceTiming = originalSourceTiming,
                        RequireMonotonicOutputTimeline = requireRegeneratedOutputTimeline,
                        RequireFullVideoDecodeCoverage = requireRegeneratedOutputTimeline || tolerantSalvageAttempted,
                        RequireFullAudioDecodeCoverage = tolerantSalvageAttempted,
                        ExpectedVideoWidth = plannedOutputGeometry?.Width,
                        ExpectedVideoHeight = plannedOutputGeometry?.Height,
                        PerformanceTiming = performance
                        ,Profile = validationProfile
            };
            finalization =
                await _finalizationService.FinalizeAsync(
                    BuildValidationRequest(),
                    finalizationStatusCallback,
                    cancellationToken).ConfigureAwait(false);
            validationOutcome = EncodingPlanService.DescribeValidationOutcome(finalization);
            finalizationOutcome = EncodingPlanService.DescribeFinalizationOutcome(finalization);
            if (preplannedTimelineReconstructionRate is { } reconstructionRate)
                RecordRecovery(EncodingRecoveryKind.TimelineNormalization,
                    EncodingRecoveryFailureClass.LocalizedSourceTimelineCorruption,
                    EncodingRecoveryMode.SoftwareDecodeWithNvencAndTimestampReconstruction, 1,
                    runResult.ExitCode == 0 ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                    $"Stream-copy normalization preserved unsafe timing; deterministic frame-index reconstruction at {reconstructionRate.Text}; output-monotonic-validation={(finalization.Success ? "passed" : "failed")}; no retry was scheduled.");
            PublishExecutionOutcome();
            if (!finalization.Success)
            {
                // FFmpeg can exit successfully after a recoverable demux/container
                // error. Validation remains authoritative, but the current
                // attempt's diagnostics can still justify the existing one-time
                // tolerant source-video retry.
                FfmpegVideoDecodeRecoveryDecision truncatedOutputRecovery =
                    FfmpegVideoDecodeRecoveryPolicy.Evaluate(
                        runResult.StandardError,
                        compatibilityPolicy,
                        cancellationToken.IsCancellationRequested,
                        videoRecoveryAttempted,
                        requestedEncoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase),
                        output);
                _log?.Invoke(
                    $"[EncodingRecovery] Validation failed after FFmpeg completion; " +
                    $"source-corruption-evidence={(truncatedOutputRecovery.Eligible ? truncatedOutputRecovery.Evidence : "none")}; " +
                    $"recovery-permitted={truncatedOutputRecovery.Eligible}; " +
                    $"strict-validation={(finalization.Success ? "passed" : "failed")}." );
                if (!disableAutomaticFfmpegRecovery &&
                    finalization.FailureKind == EncodeFinalizationFailureKind.Validation &&
                    runResult.ExitCode == 0 &&
                    truncatedOutputRecovery.Eligible)
                {
                    videoRecoveryAttempted = true;
                    recoveryDiagnostics +=
                        $"Strict attempt completed with rejected staged output; recognized source corruption evidence: {truncatedOutputRecovery.Evidence}.{Environment.NewLine}";
                    _log?.Invoke(
                        $"[EncodingRecovery] Tolerant retry started; reason=completed-encode-validation-rejected; " +
                        $"evidence={truncatedOutputRecovery.Evidence}; attempt=1; " +
                        "only strict source-fatal flags will be relaxed; encoding plan is unchanged.");
                    callback("[MediaFlux] Output was truncated after recoverable source corruption; retrying once with tolerant source decode.");
                    if (TryDeleteFailedStagingOutput(output))
                    {
                        using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
                        {
                            ffArgs = BuildFfmpegArgs(
                                inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                                encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                                mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                                containerDecision, forceMp4CompatibleAudio, totalDuration,
                                qualityValue, encoderSelection, sampleStart, sampleDuration,
                                sourcePixelFormat, restoration: restoration,
                                splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource),
                                restorationFilterOverride: aiPlan?.PostAiFilterChain,
                                plannedVideoGeometry: plannedOutputGeometry,
                                sourceDecodeMode: FfmpegSourceDecodeMode.RecoverVideo);
                            scope.Complete();
                        }
                        _log?.Invoke($"[EncodingRecovery] Tolerant retry arguments differ from strict attempt only by source decode handling; ffmpeg arguments: {ffArgs}");
                        using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.VideoDecodeRecovery))
                        {
                            runResult = await RunFfmpegAsync(
                                ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback,
                                progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback,
                                sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                            scope.Complete();
                        }
                        if (runResult.ExitCode == 0)
                        {
                            finalization = await _finalizationService.FinalizeAsync(
                                BuildValidationRequest(), finalizationStatusCallback, cancellationToken).ConfigureAwait(false);
                            _log?.Invoke($"[EncodingRecovery] Tolerant retry completed; retry-validation={(finalization.Success ? "passed" : "failed")}; terminal-promotion={(finalization.Success ? "allowed" : "blocked")}");
                        }
                        else
                        {
                            _log?.Invoke($"[EncodingRecovery] Tolerant retry completed; exit={runResult.ExitCode}; retry-validation=not-reached; terminal-promotion=blocked");
                        }
                        RecordRecovery(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.SourceVideoCorruption,
                            EncodingRecoveryMode.Tolerant, 1,
                            runResult.ExitCode == 0 ? EncodingRecoveryResult.Succeeded : EncodingRecoveryResult.Failed,
                            $"{truncatedOutputRecovery.Evidence}; strict-validation=failed; retry-exit={runResult.ExitCode}; retry-validation={(runResult.ExitCode == 0 ? (finalization.Success ? "passed" : "failed") : "not-reached")}");
                        validationOutcome = EncodingPlanService.DescribeValidationOutcome(finalization);
                        finalizationOutcome = EncodingPlanService.DescribeFinalizationOutcome(finalization);
                        PublishExecutionOutcome();
                    }
                    else
                    {
                        _log?.Invoke("[EncodingRecovery] Tolerant retry was not started because the rejected staged output could not be removed safely.");
                        RecordRecovery(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.SourceVideoCorruption,
                            EncodingRecoveryMode.Tolerant, 1, EncodingRecoveryResult.NotStarted,
                            "Rejected staged output could not be removed safely.");
                    }
                }
                if (!finalization.Success &&
                    finalization.FailureKind == EncodeFinalizationFailureKind.Validation &&
                    !requireRegeneratedOutputTimeline &&
                    videoRecoveryAttempted && runResult.ExitCode == 0 &&
                    IsFrameDeficitValidationFailure(finalization.ErrorMessage))
                {
                    EncodingSourceFailureClassification sourceFailure =
                        EncodingSourceFailureClassifier.Classify(runResult.StandardError);
                    if (sourceFailure is
                        {
                            Type: EncodingSourceFailureType.VideoBitstreamCorruption,
                            IsRecoveryCandidate: true,
                            Evidence.Length: > 0
                        } && programDuration.DurationSeconds is > 0)
                    {
                        EncodeOutputValidationFailureEvidence? advertisedFailure =
                            finalization.StagedValidationResult?.FailureEvidence;
                        _log?.Invoke(
                            $"[EncodingRecovery] Measuring recoverable source coverage after bounded tolerant decode; " +
                            $"evidence={sourceFailure.Evidence}; advertised-frames={programDuration.PrimaryVideo?.FrameCount?.ToString() ?? "unknown"}; " +
                            $"output-frames={advertisedFailure?.ActualFrameCount.ToString() ?? "unknown"}; " +
                            $"advertised-duration={programDuration.DurationSeconds:0.###}s.");
                        RecoverableSourceBaselineResult baselineResult =
                            await new RecoverableSourceBaselineService(_ffmpegPath)
                                .MeasureAsync(inputSource.InputPath, programDuration.DurationSeconds.Value, cancellationToken)
                                .ConfigureAwait(false);
                        if (baselineResult.Success && baselineResult.Baseline is { } baseline)
                        {
                            _log?.Invoke(
                                $"[EncodingRecovery] Recoverable source coverage established; " +
                                $"advertised-frames={programDuration.PrimaryVideo?.FrameCount?.ToString() ?? "unknown"}; " +
                                $"recoverable-frames={baseline.DecodedVideoFrameCount}; " +
                                $"recoverable-tail={baseline.TailPresentationSeconds:0.###}s; {baseline.Evidence}.");
                            finalization = await _finalizationService.FinalizeAsync(
                                BuildValidationRequest(baseline, sourceFailure), finalizationStatusCallback, cancellationToken)
                                .ConfigureAwait(false);
                            _log?.Invoke(
                                $"[EncodingRecovery] Recoverable-baseline validation={(finalization.Success ? "passed" : "failed")}; " +
                                $"terminal-promotion={(finalization.Success ? "allowed" : "blocked")}.");
                            validationOutcome = EncodingPlanService.DescribeValidationOutcome(finalization);
                            finalizationOutcome = EncodingPlanService.DescribeFinalizationOutcome(finalization);
                            if (tolerantSalvageAttempted && advertisedFailure is not null)
                            {
                                int salvageIndex = recoveryOutcomes.FindLastIndex(item =>
                                    item.Kind == EncodingRecoveryKind.TolerantDecodeReencode);
                                if (salvageIndex >= 0)
                                {
                                    EncodingRecoveryOutcome salvage = recoveryOutcomes[salvageIndex];
                                    long discardedFrames = Math.Max(0,
                                        advertisedFailure.ExpectedFrameCount - baseline.DecodedVideoFrameCount);
                                    double discardedSeconds = advertisedFailure.FrameRate > 0
                                        ? discardedFrames / advertisedFailure.FrameRate
                                        : 0;
                                    string disclosure =
                                        $" validation={(finalization.Success ? "passed" : "failed")}; " +
                                        $"advertised-video-frames={advertisedFailure.ExpectedFrameCount}; " +
                                        $"recoverable-video-frames={baseline.DecodedVideoFrameCount}; " +
                                        $"disclosed-source-loss={discardedFrames} frames " +
                                        $"({discardedSeconds:0.###}s at {advertisedFailure.FrameRate:0.######} fps); " +
                                        "full-output-video-audio-decode=passed; localized-gap=not-measured.";
                                    recoveryOutcomes[salvageIndex] = salvage with
                                    {
                                        Detail = salvage.Detail + disclosure,
                                        DiagnosticReason = salvage.DiagnosticReason + disclosure,
                                        SourceDurationSeconds = advertisedFailure.SourceDurationSeconds,
                                        ProducedDurationSeconds = advertisedFailure.OutputDurationSeconds,
                                        ExpectedFrameCount = advertisedFailure.ExpectedFrameCount,
                                        ProducedFrameCount = baseline.DecodedVideoFrameCount,
                                        DroppedFrameOrPacketCount = discardedFrames,
                                        DurationDeltaSeconds = Math.Abs(
                                            advertisedFailure.SourceDurationSeconds - advertisedFailure.OutputDurationSeconds),
                                        // The full tolerant decode proves the amount of loss, but does
                                        // not establish one contiguous timestamp gap. Do not invent one.
                                        LargestTimelineGapSeconds = null
                                    };
                                    _log?.Invoke($"[EncodingRecovery] Tier 2 degraded-salvage disclosure:{disclosure}");
                                }
                            }
                            PublishExecutionOutcome();
                        }
                        else
                        {
                            _log?.Invoke($"[EncodingRecovery] Recoverable source coverage was not proven; strict advertised-source validation remains authoritative. {baselineResult.Reason}");
                        }
                    }
                }
                FfmpegVideoDecodeRecoveryDecision frameRecovery =
                    FfmpegVideoDecodeRecoveryPolicy.EvaluateFrameDeficit(
                        finalization.StagedValidationResult?.FailureEvidence,
                        compatibilityPolicy,
                        sourceTiming,
                        cancellationToken.IsCancellationRequested,
                        videoRecoveryAttempted,
                        requestedEncoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase));
                if (!disableAutomaticFfmpegRecovery &&
                    finalization.FailureKind == EncodeFinalizationFailureKind.Validation && frameRecovery.Eligible &&
                    sourceVideo is not null)
                {
                    EncodeOutputValidationFailureEvidence evidence = frameRecovery.FrameDeficit!;
                    SourceTimelineRecoveryAnalysis localizedTimelineRecovery =
                        await new LocalizedSourceTimelineAnalysisService(_ffprobePath, log: _log)
                            .AnalyzeAsync(inputSource.SourcePath, sourceVideo, programDuration.DurationSeconds, cancellationToken)
                            .ConfigureAwait(false);
                    recoveryDiagnostics += $"Frame/cadence validation evidence: {frameRecovery.Evidence}.{Environment.NewLine}";
                    recoveryDiagnostics += $"Deep timing diagnosis: {localizedTimelineRecovery.DescribeEvidence()}; eligible={localizedTimelineRecovery.IsEligible}; reason={localizedTimelineRecovery.Reason}.{Environment.NewLine}";
                    if (localizedTimelineRecovery.IsEligible && localizedTimelineRecovery.NominalFrameRate is { } exactRate)
                    {
                        videoRecoveryAttempted = true;
                        string timestampFilter = $"setpts=N*{exactRate.Denominator}/{exactRate.Numerator}/TB";
                        _log?.Invoke(
                            "[EncodingRecovery] Primary encode completed but output validation detected a material frame deficit while duration remained consistent with the authoritative source timeline. " +
                            $"Deep timing analysis detected localized source presentation-timeline corruption; retrying once with deterministic frame-index timestamp reconstruction at {exactRate.Text} while retaining NVENC. " +
                            $"{frameRecovery.Evidence}; diagnosis={localizedTimelineRecovery.DescribeEvidence()}; reason={EncodingRecoveryFailureClass.LocalizedSourceTimelineCorruption}; strategy=software-decode,setpts,fps-mode-passthrough.");
                        callback("[MediaFlux] Recovering video once with deterministic timestamp reconstruction; NVENC retained.");
                        if (TryDeleteFailedStagingOutput(output))
                        {
                            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.FfmpegInitialization))
                            {
                                ffArgs = BuildFfmpegArgs(
                                    inputSource, output, videoCodec, useGpu, targetMb, scaleMode,
                                    encoderPreset, tenBit, audioChannels, concurrentEncoderSessions,
                                    mapMode, allowSubtitleCopy, allowDataCopy, allowAttachmentCopy,
                                    containerDecision, forceMp4CompatibleAudio, totalDuration,
                                    qualityValue, encoderSelection, sampleStart, sampleDuration,
                                    sourcePixelFormat, recoveryFrameRateRational: exactRate.Text,
                                    timestampReconstructionFilter: timestampFilter,
                                    disableHardwareDecode: true, restoration: restoration,
                                    splitSource: aiIntermediate is null ? null : new SplitSourceInput(aiIntermediate.Path, inputSource),
                                    restorationFilterOverride: aiPlan?.PostAiFilterChain,
                                    plannedVideoGeometry: plannedOutputGeometry,
                                    sourceDecodeMode: FfmpegSourceDecodeMode.RecoverVideoWithTimestampReconstruction);
                                scope.Complete();
                            }
                            pipelineDiagnostic = DescribeVideoPipeline(inputSource, videoCodec, useGpu, tenBit, ffArgs);
                            _log?.Invoke($"[EncodingRecovery] Recovery strategy: software decode -> {timestampFilter} -> NVENC with -fps_mode passthrough; ffmpeg arguments: {ffArgs}");
                            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.VideoDecodeRecovery))
                            {
                                runResult = await RunFfmpegAsync(ffArgs, callback, totalDuration, cancellationToken, ffmpegDiagnosticCallback, progressTotalFrames, sourceVideo?.FrameRate, structuredProgressCallback, sourceTiming?.Classification, ++ffmpegAttempt).ConfigureAwait(false);
                                scope.Complete();
                            }
                            if (runResult.ExitCode == 0)
                            {
                                finalization = await _finalizationService.FinalizeAsync(
                                    BuildValidationRequest(), finalizationStatusCallback, cancellationToken).ConfigureAwait(false);
                                RecordRecovery(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.LocalizedSourceTimelineCorruption,
                                    EncodingRecoveryMode.SoftwareDecodeWithNvencAndTimestampReconstruction, 1,
                                    EncodingRecoveryResult.Succeeded,
                                    localizedTimelineRecovery.DescribeEvidence() + $"; recovered-validation={(finalization.Success ? "passed" : "failed")}");
                                validationOutcome = EncodingPlanService.DescribeValidationOutcome(finalization);
                                finalizationOutcome = EncodingPlanService.DescribeFinalizationOutcome(finalization);
                                PublishExecutionOutcome();
                            }
                            else
                            {
                                RecordRecovery(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.LocalizedSourceTimelineCorruption,
                                    EncodingRecoveryMode.SoftwareDecodeWithNvencAndTimestampReconstruction, 1,
                                    EncodingRecoveryResult.Failed, localizedTimelineRecovery.DescribeEvidence());
                                finalization = new EncodeFinalizationResult
                                {
                                    Success = false,
                                    FailureKind = EncodeFinalizationFailureKind.Validation,
                                    ErrorMessage = "The one-time deterministic timestamp reconstruction recovery encode failed; the recovered output was not finalized. The original source was retained.",
                                    FinalOutputPath = finalOutput,
                                    StagingPath = output,
                                    RecoverableOutputPath = File.Exists(output) ? output : ""
                                };
                                validationOutcome = EncodingPlanService.DescribeValidationOutcome(finalization);
                                finalizationOutcome = EncodingPlanService.DescribeFinalizationOutcome(finalization);
                                PublishExecutionOutcome();
                            }
                        }
                        else
                        {
                            RecordRecovery(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.LocalizedSourceTimelineCorruption,
                                EncodingRecoveryMode.SoftwareDecodeWithNvencAndTimestampReconstruction, 1,
                                EncodingRecoveryResult.NotStarted, "Failed staged output could not be removed safely.");
                        }
                    }
                }
                // A recovery finalization replaces the primary attempt as the
                // authoritative result. Preserve the primary failure in recovery
                // diagnostics, but never turn a successfully finalized recovery
                // into a terminal failure.
                if (!finalization.Success)
                {
                    terminalResult = ResolveTerminalResult(finalization, completedAfterRecovery: false);
                    PublishExecutionOutcome();
                    FfmpegSourceDecodeCorruption sourceDecodeCorruption =
                        FfmpegSourceDecodeCorruptionClassifier.Classify(runResult.StandardError);
                    if (finalization.FailureKind == EncodeFinalizationFailureKind.Validation &&
                        IsFrameDeficitValidationFailure(finalization.ErrorMessage) &&
                        sourceDecodeCorruption.IsReliable)
                    {
                        _log?.Invoke($"[EncodingService] Source decode corruption was corroborated by rejected output validation: {sourceDecodeCorruption.DescribeEvidence()}.");
                        finalization = new EncodeFinalizationResult
                        {
                            Success = false,
                            FailureKind = finalization.FailureKind,
                            ErrorMessage = "Output validation rejected substantial missing video frames. FFmpeg also reported source H.264 bitstream/decode failures, so the source video contains undecodable or corrupt data; MediaFlux did not finalize the output.",
                            FinalOutputPath = finalization.FinalOutputPath,
                            StagingPath = finalization.StagingPath,
                            RecoverableOutputPath = finalization.RecoverableOutputPath,
                            StagedValidationResult = finalization.StagedValidationResult,
                            PromotedValidationResult = finalization.PromotedValidationResult
                        };
                        validationOutcome = EncodingPlanService.DescribeValidationOutcome(finalization);
                        finalizationOutcome = EncodingPlanService.DescribeFinalizationOutcome(finalization);
                        PublishExecutionOutcome();
                    }
                    _log?.Invoke(
                        $"[EncodingService] Finalization failed: {finalization.ErrorMessage}");
                    throw new EncodeFinalizationException(finalization);
                }
            }

            _log?.Invoke(
                $"[EncodingService] Validated and finalized '{finalization.FinalOutputPath}'.");
            // Recovery records are immutable snapshots. Reconcile process-only
            // successes only after the authoritative finalization has accepted
            // the staged output.
            for (int index = 0; index < recoveryOutcomes.Count; index++)
            {
                EncodingRecoveryOutcome item = recoveryOutcomes[index];
                if (item.ProcessResult == EncodingRecoveryProcessResult.Succeeded &&
                    item.MediaDisposition == EncodingRecoveryDisposition.Degraded &&
                    item.Kind != EncodingRecoveryKind.TolerantDecodeReencode)
                    recoveryOutcomes[index] = item with { MediaDisposition = EncodingRecoveryDisposition.Salvaged };
            }
            terminalResult = tolerantSalvageAttempted
                ? EncodingTerminalResult.CompletedAfterDegradedSalvage
                : ResolveTerminalResult(
                    finalization,
                    recoveryOutcomes.Any(outcome => outcome.MediaDisposition is EncodingRecoveryDisposition.Clean or EncodingRecoveryDisposition.Salvaged));
            PublishExecutionOutcome();
            EncodingExecutionOutcome completedOutcome = new(
                shadowPlan.PlanId, preflightOutcomes.ToArray(), recoveryOutcomes.ToArray(), validationOutcome, finalizationOutcome, terminalResult);
            foreach (EncodingPlanDivergence divergence in EncodingPlanService.CompareLifecycle(shadowPlan, completedOutcome))
            {
                _log?.Invoke($"[EncodingPlan] Shadow divergence: {divergence}");
                encodingPlanDivergenceCallback?.Invoke(divergence);
            }
            _log?.Invoke(EncodingPlanService.DescribeLifecycle(completedOutcome));
            using (PerformanceTimingService.PerformanceScope scope = performance.Measure(PerformanceTimingStage.TemporaryFileCleanup))
            {
                if (aiIntermediate is not null) aiIntermediate.Dispose();
                DeleteTemporaryTimelineRepair(sourceTimelineRepairPath);
                DeleteTemporaryTimelineRepair(sourceContainerRepairPath);
                scope.Complete();
            }
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
                DeleteTemporaryTimelineRepair(sourceTimelineRepairPath);
                DeleteTemporaryTimelineRepair(sourceContainerRepairPath);
                performance.LogSummary(_log);
                throw;
            }
        }

        private void DeleteTemporaryTimelineRepair(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                _log?.Invoke($"[EncodingRecovery] Temporary timeline repair cleanup: path={path}; removed={!File.Exists(path)}.");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingRecovery] Temporary timeline repair cleanup failed: path={path}; error={ex.Message}");
            }
        }

        internal static EncodingTerminalResult ResolveTerminalResult(
            EncodeFinalizationResult finalization,
            bool completedAfterRecovery)
        {
            ArgumentNullException.ThrowIfNull(finalization);
            if (!finalization.Success)
                return finalization.FailureKind == EncodeFinalizationFailureKind.Validation
                    ? EncodingTerminalResult.ValidationFailed
                    : EncodingTerminalResult.FinalizationFailed;

            return completedAfterRecovery
                ? EncodingTerminalResult.CompletedAfterRecovery
                : EncodingTerminalResult.Completed;
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
            CancellationToken cancellationToken,
            Action<string>? diagnosticCallback = null,
            long? authoritativeTotalFrames = null,
            double? authoritativeFrameRate = null,
            Action<EncodeProgress>? structuredProgressCallback = null,
            SourceTimingClassification? sourceTimingClassification = null,
            int attempt = 1)
        {
            var stderrBuilder = new StringBuilder();
            var diagnostics = new FfmpegDiagnosticCollector();
            bool cfrFallbackEligible = sourceTimingClassification == SourceTimingClassification.Cfr &&
                totalDuration > TimeSpan.Zero && authoritativeFrameRate is > 0;
            var progressArbitrator = new EncodeProgressArbitrator(
                totalDuration, authoritativeTotalFrames, authoritativeFrameRate, cfrFallbackEligible);
            _log?.Invoke($"[Progress] timing={sourceTimingClassification?.ToString() ?? "unknown"}; duration={(totalDuration > TimeSpan.Zero ? totalDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "unavailable")}; rate={(authoritativeFrameRate is > 0 ? authoritativeFrameRate.Value.ToString("0.###", CultureInfo.InvariantCulture) : "unavailable")}; measuredFrames={(authoritativeTotalFrames is > 0 ? authoritativeTotalFrames.Value.ToString(CultureInfo.InvariantCulture) : "unavailable")}; cfrFallbackEligible={cfrFallbackEligible}.");
            var fallbackState = new ProgressLogState();
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
                    HandleProgressLine(e.Data, callback, totalDuration, authoritativeTotalFrames, authoritativeFrameRate, structuredProgressCallback, progressArbitrator, fallbackState, attempt);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;

                diagnostics.Observe(e.Data, FfmpegDiagnosticComponent.Ffmpeg);
                diagnosticCallback?.Invoke(e.Data);
                HandleProgressLine(e.Data, callback, totalDuration, authoritativeTotalFrames, authoritativeFrameRate, structuredProgressCallback, progressArbitrator, fallbackState, attempt);
                AppendBounded(stderrBuilder, e.Data, MaxCapturedFfmpegCharacters);
            };

            _log?.Invoke($"[EncodingService] Progress telemetry: FFmpeg stdout/stderr lines; structured frame/time fields enabled; authoritative frames={(authoritativeTotalFrames is > 0 ? "available" : "unavailable")}; authoritative frame rate={(authoritativeFrameRate is > 0 ? "available" : "unavailable")}.");

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

            return new FfmpegProcessResult(proc.ExitCode, stderrBuilder.ToString(), diagnostics.Complete());
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

        private static bool IsFrameDeficitValidationFailure(string errorMessage) =>
            errorMessage.Contains("video frames versus", StringComparison.OrdinalIgnoreCase) &&
            errorMessage.Contains("frame deficit", StringComparison.OrdinalIgnoreCase);

        private bool TryDeleteFailedStagingOutput(string stagingPath)
        {
            try
            {
                if (File.Exists(stagingPath))
                    File.Delete(stagingPath);
                return !File.Exists(stagingPath);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[EncodingService] NVDEC/CUDA recovery could not remove failed staging output '{stagingPath}': {ex.Message}");
                return false;
            }
        }

        private sealed record FfmpegProcessResult(int ExitCode, string StandardError, FfmpegDiagnosticSummary DiagnosticSummary);

        private async Task EnsureProcessExitedAfterCancellationAsync(Process proc)
        {
            try
            {
                if (proc.HasExited)
                {
                    proc.WaitForExit();
                    return;
                }

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
                proc.WaitForExit();
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
        private void HandleProgressLine(string line, Action<string> callback, TimeSpan totalDuration, long? authoritativeTotalFrames, double? authoritativeFrameRate, Action<EncodeProgress>? structuredProgressCallback, EncodeProgressArbitrator? progressArbitrator, ProgressLogState fallbackState, int attempt)
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
                if (StructuredProgress == null && structuredProgressCallback == null)
                    return;

                if (TryParseProgress(line, totalDuration, authoritativeTotalFrames, authoritativeFrameRate, out var progress))
                {
                    if (progressArbitrator != null)
                    {
                        double? timestamp = progress.TimestampAvailable && progress.CurrentTime >= TimeSpan.Zero
                            ? progress.CurrentTime.TotalSeconds : null;
                        EncodeProgressArbitrationResult result = progressArbitrator.Update(
                            timestamp, progress.EncodedFrames, progress.Fps, progress.Speed, progress.BitrateKbps, null);
                        if (result.TimestampStalled && !fallbackState.FallbackLogged)
                        {
                            fallbackState.FallbackLogged = true;
                            _log?.Invoke($"[Progress] FFmpeg timestamp stalled while frames continue advancing; switching to frame-derived progress. basis={(progressArbitrator.Basis == EncodeProgressBasis.MeasuredFrames ? "measured frames" : "CFR duration+rate")}; frame={result.EncodedFrames?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; timestamp={result.TimestampSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"}.");
                        }
                        progress = new EncodeProgress(TimeSpan.FromSeconds(Math.Max(0, result.MediaSeconds)), totalDuration,
                            result.Fps, result.Speed, result.BitrateKbps, result.Percent, result.EncodedFrames,
                            result.Basis, result.TotalFrames, result.TimestampStalled, result.TimestampSeconds.HasValue,
                            attempt);
                    }
                    structuredProgressCallback?.Invoke(progress);
                    if (StructuredProgress != null)
                    {
                        try { StructuredProgress(progress); }
                        catch { /* don't let subscribers break encoding */ }
                    }
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

        internal static bool TryParseProgress(string line, TimeSpan totalDuration, long? authoritativeTotalFrames, double? authoritativeFrameRate, out EncodeProgress progress)
        {
            progress = null!;

            if (line.IndexOf("frame=", StringComparison.Ordinal) < 0 &&
                line.IndexOf("time=", StringComparison.Ordinal) < 0 &&
                line.IndexOf("out_time", StringComparison.Ordinal) < 0)
                return false;

            try
            {
                string? timeStr = ExtractValue(line, "time=") ?? ExtractValue(line, "out_time=");
                if (string.IsNullOrWhiteSpace(timeStr))
                {
                    string? microseconds = ExtractValue(line, "out_time_us=") ?? ExtractValue(line, "out_time_ms=");
                    if (long.TryParse(microseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                        timeStr = TimeSpan.FromTicks(value * (line.Contains("out_time_us=", StringComparison.Ordinal) ? 10 : 10_000)).ToString();
                }
                string? frameText = ExtractValue(line, "frame=");
                long? encodedFrames = long.TryParse(frameText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedFrame) && parsedFrame > 0 ? parsedFrame : null;
                if (string.IsNullOrWhiteSpace(timeStr) && encodedFrames is not > 0)
                    return false;
                bool hasTimestamp = TryParseTime(timeStr ?? "", out var currentTime);
                if (!hasTimestamp)
                {
                    if (encodedFrames is not > 0)
                        return false;
                    if (authoritativeTotalFrames is > 0 && authoritativeFrameRate is > 0)
                        currentTime = TimeSpan.FromSeconds(encodedFrames.Value / authoritativeFrameRate.Value);
                    else
                        currentTime = TimeSpan.Zero;
                }

                string? fpsStr = ExtractValue(line, "fps=");
                string? bitrateStr = ExtractValue(line, "bitrate=");
                string? speedStr = ExtractValue(line, "speed=");

                double fps = TryParseDouble(fpsStr);
                double speed = TryParseSpeed(speedStr);
                double bitrateKbps = TryParseBitrateKbps(bitrateStr);

                double percent = 0;
                if (!hasTimestamp && authoritativeTotalFrames is > 0 && authoritativeFrameRate is > 0)
                    percent = Math.Clamp(currentTime.TotalSeconds * authoritativeFrameRate!.Value / authoritativeTotalFrames.Value * 100.0, 0.0, 100.0);
                else if (totalDuration > TimeSpan.Zero)
                {
                    percent = Math.Clamp(
                        currentTime.TotalSeconds / totalDuration.TotalSeconds * 100.0,
                        0.0,
                        100.0);
                }

                progress = new EncodeProgress(currentTime, totalDuration, fps, speed, bitrateKbps, percent, encodedFrames,
                    timestampAvailable: hasTimestamp);
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
            while (idx < line.Length && char.IsWhiteSpace(line[idx]))
                idx++;
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
            string? recoveryFrameRateRational = null,
            string? timestampReconstructionFilter = null,
            bool preferNvencGpuResidentFrames = true,
            bool disableHardwareDecode = false,
            FfmpegSourceDecodeMode sourceDecodeMode = FfmpegSourceDecodeMode.Strict,
            VideoRestorationSettings? restoration = null,
            SplitSourceInput? splitSource = null,
            string? restorationFilterOverride = null,
            VideoOutputGeometryPlan? plannedVideoGeometry = null)
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
            bool supportsNvencCudaFormatConversion =
                useGpu &&
                isNvenc &&
                FfmpegEncoderCapabilityService.SupportsFilter(
                    _ffmpegPath, "scale_cuda");
            _log?.Invoke(
                $"[EncodingService] NVENC CUDA format conversion: " +
                $"available={supportsNvencCudaFormatConversion}; software restoration filters remain host-side.");
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
                NvencCudaFormatConversionSupported =
                    supportsNvencCudaFormatConversion,
                DisableHardwareDecode = disableHardwareDecode,
                SourceDecodeMode = sourceDecodeMode,
                SourcePixelFormat = sourcePixelFormat ?? ""
                ,RecoveryFrameRateRational = recoveryFrameRateRational
                ,TimestampReconstructionFilter = timestampReconstructionFilter
                ,SplitSource = splitSource
                ,RestorationFilterOverride = restorationFilterOverride
                ,PlannedVideoGeometry = plannedVideoGeometry
            };

            var builder = new FfmpegCommandBuilder(
                EncoderRegistry.Default,
                GetPrimaryAudioBitrateKbps,
                _log);
            return builder.Build(request);
        }

        private sealed class ProgressLogState
        {
            public bool FallbackLogged { get; set; }
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

            if (!ffmpegArguments.Contains("-hwaccel cuda ", StringComparison.Ordinal))
                return "software decode -> NVENC";

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




