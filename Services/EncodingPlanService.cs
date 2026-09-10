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
