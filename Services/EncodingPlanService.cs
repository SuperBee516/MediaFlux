using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>
/// Builds the read-only pre-encode presentation from the same resolved policy,
/// geometry, restoration, and encoder contracts used by the encode pipeline.
/// It does not estimate output size or perform any new compatibility decisions.
/// </summary>
public static class EncodingPlanService
{
    /// <summary>
    /// Creates a domain plan by composing the same container and geometry policy
    /// used by EncodingService.  The result is observational: callers must not
    /// use it to choose or alter an execution path.
    /// </summary>
    public static EncodingPlan Create(EncodingDecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MediaProbeStreamInfo? sourceVideo = context.Source.Streams.FirstOrDefault(stream =>
            stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        VideoOutputGeometryPlan? geometry = null;
        if (sourceVideo?.Width is > 0 && sourceVideo.Height is > 0)
        {
            VideoOutputResolutionPlan resolution = VideoRestorationPipeline.ResolveFinalOutputResolution(
                sourceVideo.Width.Value, sourceVideo.Height.Value, context.Restoration, context.ScaleMode);
            geometry = VideoOutputGeometryPlanner.Resolve(
                sourceVideo.Width.Value, sourceVideo.Height.Value, resolution, context.Encoder, context.TenBit);
        }

        OutputContainerDecision container = OutputContainerPolicy.Decide(
            context.ContainerConfigured, context.Source, context.Input, context.MapMode,
            context.CopySubtitles, context.CopyDataStreams, context.CopyAttachments,
            audioWillBeTranscoded: context.AudioChannels is > 0);
        bool strictRejected = container.Resolved == OutputContainer.Mp4 &&
            context.CompatibilityPolicy == ContainerCompatibilityPolicy.Strict &&
            !OutputContainerPolicy.CanProceedAutomatically(container, context.CompatibilityPolicy);
        var reasons = new List<EncodingDecisionReason>
        {
            new(EncodingDecisionReasonCode.UserRequestedVideoReencode, "The existing encoding pipeline always supplies a replacement video stream."),
            new(EncodingDecisionReasonCode.ContainerCompatibility, container.Reason)
        };
        if (context.ContainerConfigured == OutputContainerSelection.Auto)
            reasons.Add(new(EncodingDecisionReasonCode.ContainerAutoResolved, container.Reason));
        if (geometry?.WasNormalized == true)
            reasons.Add(new(EncodingDecisionReasonCode.GeometryNormalized, geometry.Reason));
        if (context.UseGpu)
            reasons.Add(new(EncodingDecisionReasonCode.HardwareEncoderSelected, "The validated request selected hardware encoding."));
        if (context.TargetMb is > 0)
            reasons.Add(new(EncodingDecisionReasonCode.TargetSizeBudget, "A target-size budget is configured."));
        if (context.CompatibilityPolicy == ContainerCompatibilityPolicy.Intelligent)
            reasons.Add(new(EncodingDecisionReasonCode.IntelligentRecoveryAvailable, "One tolerant source-video decode retry is permitted only after corroborated failure evidence."));
        if (strictRejected)
            reasons.Add(new(EncodingDecisionReasonCode.StrictPolicyRejected, "Strict compatibility policy rejects the resolved stream plan."));

        List<EncodingPlanStream> streams = container.StreamPlans.Select(plan => new EncodingPlanStream(
            plan.StreamIndex, plan.StreamType, plan.Codec, plan.Action, plan.TargetCodec)).ToList();
        foreach (EncodingPlanStream audio in streams.Where(stream => stream.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase)))
            reasons.Add(new(audio.Action == StreamCompatibilityAction.Copy ? EncodingDecisionReasonCode.CompatibleAudioPassthrough : EncodingDecisionReasonCode.AudioConversionRequired,
                audio.Action == StreamCompatibilityAction.Copy ? "The container policy preserves this audio stream." : "The container policy requires an audio compatibility action."));
        foreach (EncodingPlanStream subtitle in streams.Where(stream => stream.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) && stream.Action != StreamCompatibilityAction.Copy))
            reasons.Add(new(EncodingDecisionReasonCode.SubtitleConversionRequired, "The container policy requires a subtitle compatibility action."));

        var risks = new List<EncodingRisk>();
        AddCompatibilityRisks(container, risks);
        if (geometry?.WasNormalized == true)
            risks.Add(new(EncodingRiskSeverity.Information, EncodingRiskCategory.Geometry, "geometry-normalized", geometry.Reason));
        if (context.CompatibilityPolicy == ContainerCompatibilityPolicy.Intelligent)
            risks.Add(new(EncodingRiskSeverity.Information, EncodingRiskCategory.SourceDecode, "conditional-video-recovery", "A single tolerant retry is available only for corroborated source-video corruption; cancellation, storage, and NVENC failures are excluded."));

        double? targetKbps = context.TargetMb is > 0 && context.KnownDuration > TimeSpan.Zero
            ? context.TargetMb.Value * 8192d / context.KnownDuration.TotalSeconds : null;
        double? sourceBytes = context.Input.Kind == EncodingInputKind.File && File.Exists(context.Input.SourcePath)
            ? new FileInfo(context.Input.SourcePath).Length : null;
        double? ratio = sourceBytes is > 0 && context.TargetMb is > 0
            ? context.TargetMb.Value * 1024d * 1024d / sourceBytes.Value : null;

        return new EncodingPlan
        {
            IsAvailable = true,
            Source = new EncodingPlanSource(sourceVideo?.CodecName ?? "unknown", sourceVideo?.Width, sourceVideo?.Height, sourceVideo?.FrameRate, context.KnownDuration > TimeSpan.Zero ? context.KnownDuration.TotalSeconds : null),
            Video = new EncodingPlanVideo("Reencode", context.Encoder.FfmpegCodec, context.Encoder.EncoderId, geometry?.RequestedWidth, geometry?.RequestedHeight, geometry?.Width, geometry?.Height, geometry?.PixelFormat),
            Audio = streams.Where(stream => stream.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase)).ToArray(),
            Subtitles = streams.Where(stream => stream.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase)).ToArray(),
            Container = new EncodingPlanContainer(context.ContainerConfigured, container.Resolved, container.Reason),
            Hardware = new EncodingPlanHardware(context.UseGpu, context.Encoder.EncoderId, context.UseGpu),
            Recovery = new EncodingPlanRecovery("Strict", context.CompatibilityPolicy == ContainerCompatibilityPolicy.Intelligent, context.CompatibilityPolicy == ContainerCompatibilityPolicy.Intelligent ? 1 : 0, new[] { "corroborated-source-video-corruption" }, new[] { "cancellation", "storage", "NVENC", "source-truncation", "audio-only" }),
            Validation = new EncodingPlanValidation(context.ValidationProfile.ToString(), true, context.ValidationProfile == EncodeOutputValidationProfile.SampleComparison),
            Estimates = new EncodingPlanEstimates(targetKbps, context.TargetMb, ratio),
            Risks = risks,
            DecisionReasons = reasons
        };
    }

    internal static IReadOnlyList<EncodingPlanDivergence> Compare(
        EncodingPlan plan, OutputContainerDecision actualContainer,
        VideoOutputGeometryPlan? actualGeometry, VideoEncoderSelection actualEncoder,
        double? actualTargetMb, FfmpegSourceDecodeMode actualInitialDecodeMode)
    {
        var divergences = new List<EncodingPlanDivergence>();
        if (plan.Container is { } container && container.Effective != actualContainer.Resolved)
            divergences.Add(new("output-container", container.Effective.ToString(), actualContainer.Resolved.ToString()));
        if (plan.Video is { } video && !video.Codec.Equals(actualEncoder.FfmpegCodec, StringComparison.OrdinalIgnoreCase))
            divergences.Add(new("video-codec", video.Codec, actualEncoder.FfmpegCodec));
        if (plan.Video is { EffectiveWidth: not null, EffectiveHeight: not null } planned && actualGeometry is not null &&
            (planned.EffectiveWidth != actualGeometry.Width || planned.EffectiveHeight != actualGeometry.Height))
            divergences.Add(new("effective-resolution", $"{planned.EffectiveWidth}x{planned.EffectiveHeight}", $"{actualGeometry.Width}x{actualGeometry.Height}"));
        CompareStreamActions(plan.Audio, actualContainer, "audio-action", divergences);
        CompareStreamActions(plan.Subtitles, actualContainer, "subtitle-action", divergences);
        if (plan.Estimates.EstimatedOutputSizeMb != actualTargetMb)
            divergences.Add(new("target-size-mb", plan.Estimates.EstimatedOutputSizeMb?.ToString("0.###") ?? "unknown", actualTargetMb?.ToString("0.###") ?? "unknown"));
        if (plan.Recovery is { } recovery && !recovery.InitialDecodeMode.Equals(actualInitialDecodeMode.ToString(), StringComparison.OrdinalIgnoreCase))
            divergences.Add(new("initial-decode-mode", recovery.InitialDecodeMode, actualInitialDecodeMode.ToString()));
        return divergences;
    }

    private static void CompareStreamActions(
        IReadOnlyList<EncodingPlanStream> planned,
        OutputContainerDecision actualContainer,
        string decision,
        List<EncodingPlanDivergence> divergences)
    {
        foreach (EncodingPlanStream stream in planned)
        {
            StreamCompatibilityPlan? actual = actualContainer.StreamPlans.FirstOrDefault(candidate =>
                candidate.StreamIndex == stream.StreamIndex &&
                candidate.StreamType.Equals(stream.StreamType, StringComparison.OrdinalIgnoreCase));
            if (actual is not null && (actual.Action != stream.Action ||
                !string.Equals(actual.TargetCodec, stream.TargetCodec, StringComparison.OrdinalIgnoreCase)))
            {
                divergences.Add(new(
                    decision,
                    $"#{stream.StreamIndex}:{stream.Action}/{stream.TargetCodec ?? "copy"}",
                    $"#{actual.StreamIndex}:{actual.Action}/{actual.TargetCodec ?? "copy"}"));
            }
        }
    }

    public static string DescribeSummary(EncodingPlan plan)
    {
        string source = plan.Source is { } s ? $"{s.Codec} {s.Width}x{s.Height}" : "unknown";
        string video = plan.Video is { } v ? $"{v.Action}/{v.Codec}" : "unknown";
        string container = plan.Container is { } c ? $"{c.Configured}->{c.Effective}" : "unknown";
        return $"[EncodingPlan] PlanId={plan.PlanId}; Source={source}; Video={video}; Container={container}; Recovery={plan.Recovery?.InitialDecodeMode}; Risks={plan.Risks.Count} informational.";
    }

    private static void AddCompatibilityRisks(OutputContainerDecision decision, List<EncodingRisk> risks)
    {
        foreach (StreamCompatibilityPlan stream in decision.StreamPlans.Where(plan => plan.Action != StreamCompatibilityAction.Copy))
        {
            EncodingRiskCategory category = stream.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase)
                ? EncodingRiskCategory.AudioCompatibility : stream.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase)
                    ? EncodingRiskCategory.SubtitleCompatibility : EncodingRiskCategory.ContainerCompatibility;
            risks.Add(new(EncodingRiskSeverity.Information, category, "container-stream-action", stream.Reason));
        }
    }

    public sealed record Request(
        MediaProbeResult Source,
        EncodingInputSource Input,
        VideoEncoderSelection Encoder,
        bool UseGpu,
        bool TenBit,
        int? AudioChannels,
        EncodingService.ScaleMode ScaleMode,
        VideoRestorationSettings Restoration,
        OutputContainerSelection OutputContainer,
        EncodingService.StreamMapMode MapMode = EncodingService.StreamMapMode.KeepAll,
        bool CopySubtitles = true,
        bool CopyDataStreams = true,
        bool CopyAttachments = true,
        ContainerCompatibilityPolicy CompatibilityPolicy = ContainerCompatibilityPolicy.Intelligent);

    public static EncodingPlan Resolve(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.Encoder);

        var sections = new List<EncodingPlanSection>();
        var compatibilityItems = new List<EncodingPlanItem>();
        MediaProbeStreamInfo? sourceVideo = request.Source.Streams.FirstOrDefault(
            stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));

        if (sourceVideo != null)
        {
            var videoItems = new List<EncodingPlanItem>();
            string sourceCodec = DisplayCodec(sourceVideo.CodecName);
            string sourceResolution = DescribeResolution(sourceVideo.Width, sourceVideo.Height);
            string targetCodec = DisplayCodec(request.Encoder.FfmpegCodec);
            string targetResolution = sourceResolution;
            VideoOutputGeometryPlan? geometry = null;

            if (sourceVideo.Width is > 0 && sourceVideo.Height is > 0)
            {
                VideoOutputResolutionPlan outputResolution =
                    VideoRestorationPipeline.ResolveFinalOutputResolution(
                        sourceVideo.Width.Value,
                        sourceVideo.Height.Value,
                        request.Restoration,
                        request.ScaleMode);
                geometry = VideoOutputGeometryPlanner.Resolve(
                    sourceVideo.Width.Value,
                    sourceVideo.Height.Value,
                    outputResolution,
                    request.Encoder,
                    request.TenBit);
                targetResolution = DescribeResolution(geometry.Width, geometry.Height);
            }

            videoItems.Add(new(
                "Video",
                $"{sourceCodec} {sourceResolution} → {targetCodec} {targetResolution}"));

            string encoderName = request.UseGpu ? "Hardware acceleration" : "Software encoding";
            try
            {
                encoderName = EncoderRegistry.Default
                    .Resolve(request.Encoder.EncoderId, request.Encoder.CodecFamily)
                    .Provider.Capabilities.DisplayName;
                encoderName += request.UseGpu ? " (GPU)" : " (CPU)";
            }
            catch
            {
                // The actual encoder validation remains authoritative at encode time.
            }
            videoItems.Add(new("Encoder", encoderName));

            if (geometry != null && !string.IsNullOrWhiteSpace(geometry.PixelFormat))
                videoItems.Add(new("Pixel format", geometry.PixelFormat));

            sections.Add(new("Video", videoItems));

            if (geometry?.WasNormalized == true)
            {
                compatibilityItems.Add(new(
                    "Geometry",
                    $"{geometry.RequestedWidth}×{geometry.RequestedHeight} → {geometry.Width}×{geometry.Height}",
                    geometry.Reason));
            }
        }

        OutputContainerDecision decision = OutputContainerPolicy.Decide(
            request.OutputContainer,
            request.Source,
            request.Input,
            request.MapMode,
            request.CopySubtitles,
            request.CopyDataStreams,
            request.CopyAttachments,
            audioWillBeTranscoded: request.AudioChannels is > 0);

        var audioItems = new List<EncodingPlanItem>();
        foreach (StreamCompatibilityPlan plan in decision.StreamPlans.Where(IsAudio))
        {
            string action = plan.Action switch
            {
                StreamCompatibilityAction.Copy => "Copy",
                StreamCompatibilityAction.Transcode =>
                    $"{DisplayCodec(plan.TargetCodec ?? "aac")} 192 kbps",
                StreamCompatibilityAction.Omit => "Drop",
                _ => "Unsupported"
            };
            audioItems.Add(new(
                $"Stream {plan.StreamIndex}",
                $"{DisplayCodec(plan.Codec)} → {action}",
                plan.Reason));
        }
        if (audioItems.Count > 0)
            sections.Add(new("Audio", audioItems));

        var subtitleItems = new List<EncodingPlanItem>();
        foreach (StreamCompatibilityPlan plan in decision.StreamPlans.Where(IsSubtitle))
        {
            string action = plan.Action switch
            {
                StreamCompatibilityAction.Copy => "Copy",
                StreamCompatibilityAction.Transcode => DisplayCodec(plan.TargetCodec ?? "converted"),
                StreamCompatibilityAction.Omit => "Drop",
                _ => "Unsupported"
            };
            subtitleItems.Add(new(
                $"Stream {plan.StreamIndex}",
                $"{DisplayCodec(plan.Codec)} → {action}",
                plan.Reason));
        }
        if (subtitleItems.Count > 0)
            sections.Add(new("Subtitles", subtitleItems));

        VideoRestorationSettings effectiveRestoration =
            VideoRestorationModeResolver.Resolve(request.Restoration);
        var processingItems = new List<EncodingPlanItem>();
        if (effectiveRestoration.Preset != VideoRestorationPreset.Off)
        {
            processingItems.Add(new(
                "Restoration",
                DisplayRestorationPreset(effectiveRestoration.Preset)));
            VideoRestorationPipelinePlan pipeline =
                VideoRestorationPipeline.BuildPlan(effectiveRestoration, request.ScaleMode);
            if (pipeline.UsesAi)
            {
                string model = string.IsNullOrWhiteSpace(effectiveRestoration.AiModelId)
                    ? "configured model"
                    : effectiveRestoration.AiModelId;
                processingItems.Add(new(
                    "AI stage",
                    $"{effectiveRestoration.AiMode} · {model} · {(int)effectiveRestoration.AiScale}×"));
            }
            else if (!string.IsNullOrWhiteSpace(pipeline.ConventionalFilterChain))
            {
                processingItems.Add(new("Stage", "FFmpeg restoration filters"));
            }
        }
        if (processingItems.Count > 0)
            sections.Add(new("Processing", processingItems));

        if (decision.Resolved == OutputContainer.Mp4 &&
            request.CompatibilityPolicy == ContainerCompatibilityPolicy.Strict &&
            !OutputContainerPolicy.CanProceedAutomatically(
                decision,
                request.CompatibilityPolicy))
        {
            compatibilityItems.Add(new(
                "Policy",
                "Strict policy will stop before encoding",
                "The resolved stream plan contains a conversion, omission, or unsupported stream."));
        }
        else if (decision.Resolved == OutputContainer.Mp4 &&
                 request.CompatibilityPolicy == ContainerCompatibilityPolicy.AlwaysAsk &&
                 decision.RequiresConfirmation)
        {
            compatibilityItems.Add(new(
                "Policy",
                "Confirmation required before encoding",
                "Always Ask requires approval for the resolved MP4 compatibility corrections."));
        }

        if (request.OutputContainer == OutputContainerSelection.Auto ||
            (request.OutputContainer == OutputContainerSelection.Mp4 && decision.CompatibilityWarnings.Count > 0))
        {
            compatibilityItems.Add(new(
                "Container",
                decision.Resolved.ToString(),
                decision.Reason));
        }
        foreach (StreamCompatibilityPlan plan in decision.StreamPlans.Where(plan =>
                     plan.Action is StreamCompatibilityAction.Transcode or
                     StreamCompatibilityAction.Omit or
                     StreamCompatibilityAction.Unsupported))
        {
            compatibilityItems.Add(new(
                plan.StreamType,
                $"{DisplayCodec(plan.Codec)} → {plan.Action switch
                {
                    StreamCompatibilityAction.Transcode => DisplayCodec(plan.TargetCodec ?? "converted"),
                    StreamCompatibilityAction.Omit => "dropped",
                    _ => "blocked"
                }}",
                plan.Reason));
        }
        if (compatibilityItems.Count > 0)
            sections.Add(new("Compatibility corrections", compatibilityItems));

        return new EncodingPlan
        {
            IsAvailable = true,
            Sections = sections
        };
    }

    private static bool IsAudio(StreamCompatibilityPlan plan) =>
        plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubtitle(StreamCompatibilityPlan plan) =>
        plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase);

    private static string DescribeResolution(int? width, int? height) =>
        width is > 0 && height is > 0 ? $"{width}×{height}" : "unknown resolution";

    private static string DisplayCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return "unknown";

        return codec.Trim().ToLowerInvariant() switch
        {
            "h264" or "avc1" or "libx264" => "H.264",
            "hevc" or "h265" or "hev1" or "libx265" or "hevc_nvenc" => "HEVC",
            "av1" or "av1_nvenc" or "svt-av1" => "AV1",
            "mpeg2video" => "MPEG-2",
            "mp2" => "MP2",
            "mp3" => "MP3",
            "dts" => "DTS",
            "aac" => "AAC",
            "mov_text" => "mov_text",
            "subrip" or "srt" => "SubRip",
            "ass" or "ssa" => codec.Trim().ToUpperInvariant(),
            _ => codec.Trim()
        };
    }

    private static string DisplayRestorationPreset(VideoRestorationPreset preset) => preset switch
    {
        VideoRestorationPreset.VhsTvCaptureRestore => "VHS / TV capture restore",
        VideoRestorationPreset.VintageAnimationLight => "Vintage animation cleanup",
        VideoRestorationPreset.VintageAnimationRestore => "Vintage animation restore",
        VideoRestorationPreset.DvdAnimationRestore => "DVD animation restore",
        VideoRestorationPreset.Custom => "Custom restoration",
        _ => preset.ToString()
    };
}
