using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services.EncodingV2Gui;

/// <summary>Plain snapshot of the current GUI intent used to decide whether one whole job fits V2.</summary>
public sealed record V2GuiEncodeIntent
{
    public required string SourcePath { get; init; }
    public required string JobId { get; init; }
    public required EncodeExecutionSnapshotSettings Settings { get; init; }
    public bool HasCustomSettings { get; init; }
    public bool IsDvdEncode { get; init; }
    public bool IsLibraryPolicyItem { get; init; }
    public bool HasPredictionShadowAssignment { get; init; }
}

public sealed record V2GuiPreparedEncode
{
    public required V2GuiEncodeIntent Intent { get; init; }
    public required EncodingV2Request Request { get; init; }
    public required V2MediaFacts SourceFacts { get; init; }
    public required FrozenEncodePlan PreflightPlan { get; init; }
    public required OutputContainerDecision ContainerDecision { get; init; }
    public required string RouteReason { get; init; }
    public required double SourceProbeMilliseconds { get; init; }
    public required string FfmpegPath { get; init; }
    public required string FfprobePath { get; init; }
}

public sealed record V2GuiPreparationResult(
    bool Supported,
    string Reason,
    V2GuiPreparedEncode? Prepared)
{
    public static V2GuiPreparationResult Unsupported(string reason) => new(false, reason, null);
    public static V2GuiPreparationResult Ready(V2GuiPreparedEncode prepared) => new(true, prepared.RouteReason, prepared);
}

/// <summary>
/// Maps ordinary fixed-quality GUI intent to one frozen V2 request. It declines the
/// entire job when V2 cannot preserve the existing request's execution semantics.
/// </summary>
public static class V2GuiRequestMapper
{
    public static string? GetIntentUnsupportedReason(V2GuiEncodeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        EncodeExecutionSnapshotSettings settings = intent.Settings;
        if (intent.IsDvdEncode) return "DVD title inputs remain on V1.";
        if (intent.IsLibraryPolicyItem) return "Library policy jobs remain on V1.";
        if (intent.HasCustomSettings) return "Per-row custom settings remain on V1.";
        if (intent.HasPredictionShadowAssignment) return "Prediction-shadow assigned jobs remain on V1.";
        if (settings.AutomaticQuality) return "Source Adaptive quality remains on V1.";
        if (settings.AutoTargetSize) return "Target-size jobs remain on V1.";
        if (string.Equals(settings.CompressionProfile, "No Compression", StringComparison.OrdinalIgnoreCase))
            return "The No Compression profile uses V1 target-size behavior.";
        if (settings.NumericQualityValue is null)
            return "A fixed numeric quality value is required for V2.";
        if (settings.TenBit) return "Ten-bit jobs remain on V1.";
        if (settings.CompatibilityPolicy != ContainerCompatibilityPolicy.Intelligent)
            return "Non-default container compatibility policies remain on V1.";
        if (settings.AdaptivePreAttemptResearchCapture.Enabled)
            return "Adaptive pre-attempt research capture remains on V1.";
        if (!IsRestorationOff(settings.Restoration))
            return "Restoration or custom resize jobs remain on V1.";

        string encoderId = VideoEncoderCompatibility.ResolveEncoderId(settings.EncoderId, settings.CodecFamily);
        bool supportedEncoder = (settings.CodecFamily, encoderId) is
            (VideoCodecFamily.H264, VideoEncoderIds.Libx264) or
            (VideoCodecFamily.Hevc, VideoEncoderIds.Libx265) or
            (VideoCodecFamily.H264, VideoEncoderIds.Nvenc) or
            (VideoCodecFamily.Hevc, VideoEncoderIds.Nvenc);
        if (!supportedEncoder)
            return "V2 GUI production currently supports only x264/x265 and H.264/HEVC NVENC.";
        if (EncodeExecutionSnapshotBuilder.ResolveAudioChannels(settings.AudioChannelSelection).HasValue)
            return "Explicit audio layout conversion remains on V1.";
        if (settings.ScaleMode is not (EncodingService.ScaleMode.None or EncodingService.ScaleMode.To720p or
            EncodingService.ScaleMode.To1080p or EncodingService.ScaleMode.To1440p or EncodingService.ScaleMode.To4K))
            return "The selected geometry mode is not supported by V2.";
        return null;
    }

    public static V2GuiPreparationResult TryCreate(
        V2GuiEncodeIntent intent,
        V2MediaFacts source,
        string ffmpegPath,
        string ffprobePath,
        double sourceProbeMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(source);
        string? unsupported = GetIntentUnsupportedReason(intent);
        if (unsupported is not null) return V2GuiPreparationResult.Unsupported(unsupported);
        if (!string.IsNullOrWhiteSpace(source.SourcePath) &&
            !string.Equals(Path.GetFullPath(intent.SourcePath), Path.GetFullPath(source.SourcePath), StringComparison.OrdinalIgnoreCase))
            return V2GuiPreparationResult.Unsupported("The probed source does not match this queue row.");
        if (source.Probe.Streams.Any(stream =>
                stream.CodecType.Equals("data", StringComparison.OrdinalIgnoreCase)))
            return V2GuiPreparationResult.Unsupported("Data streams are not represented by the V2 request; this complete job remains on V1.");
        MediaProbeStreamInfo[] videoStreams = source.Probe.Streams
            .Where(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (videoStreams.Length != 1 || videoStreams[0].Dispositions.TryGetValue("attached_pic", out bool attachedPicture) && attachedPicture)
            return V2GuiPreparationResult.Unsupported("V2 represents one ordinary video stream; alternate video and cover-art streams remain on V1.");
        if (source.Probe.Streams.Any(stream => !new[] { "video", "audio", "subtitle", "attachment", "data" }
                .Contains(stream.CodecType, StringComparer.OrdinalIgnoreCase)))
            return V2GuiPreparationResult.Unsupported("The source contains a stream type that is not represented by the V2 request.");

        EncodeExecutionSnapshotSettings settings = intent.Settings;
        if (settings.NumericQualityValue is not int quality)
            return V2GuiPreparationResult.Unsupported("A fixed numeric quality value is required for V2.");
        string encoderId = VideoEncoderCompatibility.ResolveEncoderId(settings.EncoderId, settings.CodecFamily);
        (string encoder, string codec) = (settings.CodecFamily, encoderId) switch
        {
            (VideoCodecFamily.H264, VideoEncoderIds.Libx264) => ("libx264", "h264"),
            (VideoCodecFamily.Hevc, VideoEncoderIds.Libx265) => ("libx265", "hevc"),
            (VideoCodecFamily.H264, VideoEncoderIds.Nvenc) => ("h264_nvenc", "h264"),
            (VideoCodecFamily.Hevc, VideoEncoderIds.Nvenc) => ("hevc_nvenc", "hevc"),
            _ => ("", "")
        };
        if (encoder.Length == 0)
            return V2GuiPreparationResult.Unsupported("The selected encoder and codec pair is not supported by V2.");

        OutputContainerDecision decision;
        try
        {
            decision = OutputContainerPolicy.Decide(
                settings.OutputContainer,
                source.Probe,
                EncodingInputSource.FromFile(source.SourcePath),
                EncodingService.StreamMapMode.KeepAll,
                copySubtitles: true,
                copyDataStreams: true,
                copyAttachments: true,
                audioWillBeTranscoded: false,
                compatibilityPolicy: settings.CompatibilityPolicy);
        }
        catch (Exception ex)
        {
            return V2GuiPreparationResult.Unsupported("The existing container policy could not be represented: " + ex.Message);
        }

        if (!OutputContainerPolicy.CanProceedAutomatically(decision, settings.CompatibilityPolicy) ||
            decision.HasUnsupportedMeaningfulStreams)
            return V2GuiPreparationResult.Unsupported("The selected container requires a V1 compatibility decision for one or more streams.");
        if (decision.CopyAttachments)
            return V2GuiPreparationResult.Unsupported("Matroska attachment preservation is not represented by V2.");

        IReadOnlyList<StreamCompatibilityPlan> audio = decision.StreamPlans
            .Where(plan => plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase)).ToArray();
        string audioMode;
        int? audioBitrate = null;
        if (audio.Count == 0)
        {
            audioMode = "none";
        }
        else if (audio.All(plan => plan.Action == StreamCompatibilityAction.Copy))
        {
            audioMode = "copy";
        }
        else if (audio.All(plan => plan.Action == StreamCompatibilityAction.Transcode &&
                                  plan.TargetCodec?.Equals("aac", StringComparison.OrdinalIgnoreCase) == true))
        {
            audioMode = "aac";
            audioBitrate = 192;
        }
        else
        {
            return V2GuiPreparationResult.Unsupported("Mixed or unusual audio stream actions remain on V1.");
        }

        IReadOnlyList<StreamCompatibilityPlan> subtitles = decision.StreamPlans
            .Where(plan => plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase)).ToArray();
        string subtitleMode;
        if (subtitles.Count == 0)
        {
            subtitleMode = "none";
        }
        else if (subtitles.All(plan => plan.Action == StreamCompatibilityAction.Copy))
        {
            subtitleMode = "copy";
        }
        else
        {
            return V2GuiPreparationResult.Unsupported("Subtitle conversion or omission is not represented by V2.");
        }

        if (string.IsNullOrWhiteSpace(settings.OutputFolder) || !Directory.Exists(settings.OutputFolder))
            return V2GuiPreparationResult.Unsupported("The configured output folder is unavailable.");
        string suffix = EncodeExecutionSnapshotBuilder.BuildOutputSuffix(
            settings.VideoFormatText, settings.EnableOutputSuffix, settings.EnableCodecSuffix, settings.OutputSuffix);
        string outputBase = Path.GetFileNameWithoutExtension(source.SourcePath) + suffix + decision.Extension;
        string outputPath;
        try { outputPath = OutputPathService.GetCollisionSafePath(Path.Combine(settings.OutputFolder, outputBase)); }
        catch (Exception ex) { return V2GuiPreparationResult.Unsupported("The output path could not be represented: " + ex.Message); }

        int? targetHeight = settings.ScaleMode switch
        {
            EncodingService.ScaleMode.To720p => 720,
            EncodingService.ScaleMode.To1080p => 1080,
            EncodingService.ScaleMode.To1440p => 1440,
            EncodingService.ScaleMode.To4K => 2160,
            _ => null
        };
        bool scaled = targetHeight.HasValue;
        EncodingV2Request request = new()
        {
            JobId = Guid.TryParse(intent.JobId, out Guid jobId) ? jobId.ToString("N") : Guid.NewGuid().ToString("N"),
            SourcePath = Path.GetFullPath(source.SourcePath),
            OutputPath = outputPath,
            Encoder = encoder,
            Codec = codec,
            Container = decision.Resolved == OutputContainer.Matroska ? "mkv" : "mp4",
            QualityMode = encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? "cq" : "crf",
            QualityValue = quality,
            Preset = settings.EncoderPreset,
            PixelFormat = "yuv420p",
            GeometryMode = scaled ? "fitBox" : "source",
            MaxWidth = scaled ? 8192 : null,
            MaxHeight = targetHeight,
            AudioMode = audioMode,
            AudioBitrateKbps = audioBitrate,
            SubtitleMode = subtitleMode,
            DeleteSourceOnSuccess = settings.DeleteSourceAfterCompression,
            FfmpegPath = ffmpegPath,
            FfprobePath = ffprobePath
        };

        FrozenEncodePlan plan;
        try
        {
            plan = new V2EncodePlanner().CreatePlan(request, source, ffmpegPath);
            VideoEncoderSelection selection = new(encoderId, settings.CodecFamily, encoder);
            VideoOutputResolutionPlan requestedGeometry = VideoRestorationPipeline.ResolveFinalOutputResolution(
                source.SourceWidth, source.SourceHeight, settings.Restoration, settings.ScaleMode);
            VideoOutputGeometryPlan legacyGeometry = VideoOutputGeometryPlanner.Resolve(
                source.SourceWidth, source.SourceHeight, requestedGeometry, selection, tenBit: false);
            if (plan.Width != legacyGeometry.Width || plan.Height != legacyGeometry.Height)
                return V2GuiPreparationResult.Unsupported("V2 geometry would differ from the existing fixed geometry plan.");
        }
        catch (Exception ex)
        {
            string message = ex is V2PlanningException planning ? planning.Message : ex.Message;
            return V2GuiPreparationResult.Unsupported("V2 could not freeze this complete request: " + message);
        }

        return V2GuiPreparationResult.Ready(new V2GuiPreparedEncode
        {
            Intent = intent,
            Request = request,
            SourceFacts = source,
            PreflightPlan = plan,
            ContainerDecision = decision,
            RouteReason = "supported fixed-quality H.264/HEVC request",
            SourceProbeMilliseconds = sourceProbeMilliseconds,
            FfmpegPath = ffmpegPath,
            FfprobePath = ffprobePath
        });
    }

    private static bool IsRestorationOff(VideoRestorationSettings? settings) => settings is null ||
        settings.Mode == VideoRestorationMode.Off &&
        settings.Preset == VideoRestorationPreset.Off &&
        settings.AiMode == AiRestorationMode.Off &&
        settings.Resize == VideoRestorationResize.Original &&
        settings.Denoise == VideoRestorationStrength.Off &&
        settings.Deblock == VideoRestorationStrength.Off &&
        settings.Deband == VideoRestorationStrength.Off &&
        settings.Sharpen == VideoRestorationStrength.Off &&
        settings.Deinterlace == VideoRestorationDeinterlace.Off &&
        settings.Brightness == 0 && settings.Contrast == 1 && settings.Saturation == 1;
}

/// <summary>GUI application adapter over the Phase 2 queue runner and Phase 1 item lifecycle.</summary>
public sealed class V2GuiProductionAdapter : IV2GuiProductionAdapter
{
    private readonly string _applicationDirectory;
    private readonly string? _configuredFfmpegPath;
    private readonly string? _configuredFfprobePath;

    public V2GuiProductionAdapter(string applicationDirectory, string? configuredFfmpegPath, string? configuredFfprobePath)
    {
        _applicationDirectory = applicationDirectory;
        _configuredFfmpegPath = configuredFfmpegPath;
        _configuredFfprobePath = configuredFfprobePath;
    }

    public async Task<V2GuiPreparationResult> PrepareAsync(V2GuiEncodeIntent intent, CancellationToken cancellationToken)
    {
        string? reason = V2GuiRequestMapper.GetIntentUnsupportedReason(intent);
        if (reason is not null) return V2GuiPreparationResult.Unsupported(reason);
        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(_applicationDirectory, _configuredFfmpegPath, _configuredFfprobePath);
        if (!tools.AreAllAvailable)
            return V2GuiPreparationResult.Unsupported("A matching FFmpeg and FFprobe pair is unavailable for V2.");

        var processRunner = new MediaToolProcessRunner();
        var metadata = new V2SourceMetadataProvider(new FfprobeService(tools.FfprobePath, processRunner));
        long probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        V2MediaFacts source;
        try { source = await metadata.ProbeAsync(intent.SourcePath, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            string detail = ex is V2PlanningException planning ? planning.Message : ex.Message;
            return V2GuiPreparationResult.Unsupported("V2 capability preflight could not inspect the source: " + detail);
        }
        double probeMs = System.Diagnostics.Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds;
        return V2GuiRequestMapper.TryCreate(intent, source, tools.FfmpegPath, tools.FfprobePath, probeMs);
    }

    public async Task<V2GuiQueueItemResult> RunAsync(
        V2GuiPreparedEncode prepared,
        Action<V2EncodingQueueEvent>? eventObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(_applicationDirectory, prepared.FfmpegPath, prepared.FfprobePath);
        if (!tools.AreAllAvailable)
            throw new V2PlanningException("MediaToolsUnavailable", "The matching FFmpeg and FFprobe pair became unavailable after V2 routing.");
        var processRunner = new MediaToolProcessRunner();
        IV2SourceMetadataProvider fallbackMetadata = new V2SourceMetadataProvider(
            new FfprobeService(tools.FfprobePath, processRunner));
        var cachedMetadata = new PreprobedSourceMetadataProvider(fallbackMetadata, prepared.SourceFacts);
        var itemExecutor = new V2CoreQueueItemExecutor((request, _, _) =>
        {
            var validator = new V2OutputValidator(cachedMetadata,
                new FfmpegDecodeIntegritySpotCheckService(tools.FfmpegPath, processRunner));
            return new V2EncodingCore(cachedMetadata, new V2EncodePlanner(),
                new V2ProductionExecutor(processRunner), validator, new V2FileOutputPromoter(), tools.FfmpegPath);
        });
        string jobId = prepared.Request.JobId!;
        var queueRequest = new V2EncodingQueueRequest
        {
            QueueId = "gui-" + jobId,
            Items = [new V2EncodingQueueItemRequest { ItemId = jobId, Request = prepared.Request }]
        };
        V2EncodingQueueResult queue = await new V2EncodingQueueRunner(itemExecutor)
            .RunAsync(queueRequest, eventObserver, cancellationToken).ConfigureAwait(false);
        V2EncodingQueueItemResult item = queue.Items.Single();
        V2ExecutionResult execution = item.Execution ?? new V2ExecutionResult
        {
            JobId = jobId,
            State = V2LifecycleState.Canceled,
            SourcePath = prepared.Request.SourcePath,
            OutputPath = prepared.Request.OutputPath,
            Failure = item.Failure ?? new V2Failure("Canceled", "The item was not started because cancellation was requested."),
            SourceDeletionRequested = prepared.Request.DeleteSourceOnSuccess,
            SourceDeleted = false
        };
        return new V2GuiQueueItemResult(queue, execution, prepared.PreflightPlan);
    }

    private sealed class PreprobedSourceMetadataProvider(
        IV2SourceMetadataProvider fallback,
        V2MediaFacts sourceFacts) : IV2SourceMetadataProvider
    {
        public Task<V2MediaFacts> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFullPath(sourcePath), sourceFacts.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var file = new FileInfo(sourceFacts.SourcePath);
                if (file.Exists && file.Length == sourceFacts.SourceLength &&
                    file.LastWriteTimeUtc.Ticks == sourceFacts.SourceLastWriteUtcTicks)
                    return Task.FromResult(sourceFacts);
                throw new V2PlanningException("SourceChangedAfterRoute",
                    "The source changed after V2 capability routing; the job was stopped before production.");
            }
            return fallback.ProbeAsync(sourcePath, cancellationToken);
        }
    }
}

public sealed record V2GuiQueueItemResult(
    V2EncodingQueueResult Queue,
    V2ExecutionResult Execution,
    FrozenEncodePlan PreflightPlan);

public interface IV2GuiProductionAdapter
{
    Task<V2GuiPreparationResult> PrepareAsync(V2GuiEncodeIntent intent, CancellationToken cancellationToken);

    Task<V2GuiQueueItemResult> RunAsync(
        V2GuiPreparedEncode prepared,
        Action<V2EncodingQueueEvent>? eventObserver,
        CancellationToken cancellationToken);
}
