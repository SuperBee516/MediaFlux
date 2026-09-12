using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingPlanServiceTests
{
    [Fact]
    public void ShadowPlanKeepsConfiguredAutoSeparateFromEffectiveMkvAndRecordsReasons()
    {
        EncodingPlan plan = EncodingPlanService.Create(Context(
            OutputContainerSelection.Auto,
            ContainerCompatibilityPolicy.Intelligent,
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1281, Height = 720 },
            new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "mp2" },
            new MediaProbeStreamInfo { Index = 2, CodecType = "subtitle", CodecName = "ass" }));

        Assert.Equal(OutputContainerSelection.Auto, plan.Container!.Configured);
        Assert.Equal(OutputContainer.Matroska, plan.Container.Effective);
        Assert.Contains(plan.DecisionReasons, reason => reason.Code == EncodingDecisionReasonCode.ContainerAutoResolved);
        Assert.Contains(plan.DecisionReasons, reason => reason.Code == EncodingDecisionReasonCode.GeometryNormalized);
        Assert.Contains(plan.Audio, stream => stream.Action == StreamCompatibilityAction.Copy);
        Assert.Contains(plan.Subtitles, stream => stream.Action == StreamCompatibilityAction.Copy);
    }

    [Fact]
    public void ShadowPlanReportsMp4CompatibilityWithoutChangingStrictPolicyDecision()
    {
        EncodingPlan plan = EncodingPlanService.Create(Context(
            OutputContainerSelection.Mp4,
            ContainerCompatibilityPolicy.Strict,
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 1920, Height = 1080 },
            new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "mp2" },
            new MediaProbeStreamInfo { Index = 2, CodecType = "subtitle", CodecName = "ass" }));

        Assert.Equal(OutputContainer.Mp4, plan.Container!.Effective);
        Assert.Contains(plan.Audio, stream => stream.Action == StreamCompatibilityAction.Transcode && stream.TargetCodec == "aac");
        Assert.Contains(plan.Subtitles, stream => stream.Action == StreamCompatibilityAction.Transcode && stream.TargetCodec == "mov_text");
        Assert.Contains(plan.DecisionReasons, reason => reason.Code == EncodingDecisionReasonCode.StrictPolicyRejected);
        Assert.Contains(plan.Risks, risk => risk.Category == EncodingRiskCategory.AudioCompatibility);
    }

    [Fact]
    public void ShadowPlanModelsOnlyConditionalIntelligentVideoRecovery()
    {
        EncodingPlan plan = EncodingPlanService.Create(Context(
            OutputContainerSelection.Matroska,
            ContainerCompatibilityPolicy.Intelligent,
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080 }));

        Assert.Equal("Strict", plan.Recovery!.InitialDecodeMode);
        Assert.True(plan.Recovery.TolerantRecoveryPermitted);
        Assert.Equal(1, plan.Recovery.MaximumRetryCount);
        Assert.Contains("NVENC", plan.Recovery.RejectedFailureClasses);
        Assert.Contains("storage", plan.Recovery.RejectedFailureClasses);
        Assert.Contains("cancellation", plan.Recovery.RejectedFailureClasses);
    }

    [Fact]
    public void ExecutionValuesComeFromFrozenPlanAndMatchLegacyPolicy()
    {
        EncodingDecisionContext context = Context(
            OutputContainerSelection.Auto,
            ContainerCompatibilityPolicy.Intelligent,
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1281, Height = 719 },
            new MediaProbeStreamInfo { Index = 1, CodecType = "subtitle", CodecName = "ass" });
        EncodingPlan plan = EncodingPlanService.Create(context);
        EncodingPlanService.EncodingPlanExecutionValues execution =
            EncodingPlanService.GetExecutionValues(plan);

        Assert.Equal(OutputContainer.Matroska, execution.ContainerDecision.Resolved);
        Assert.Equal(1282, execution.Geometry!.Width);
        Assert.Equal(720, execution.Geometry.Height);
        Assert.Equal(100, execution.TargetMb);
        Assert.Equal("hevc_nvenc", execution.Encoder.FfmpegCodec);

        OutputContainerDecision legacyContainer = OutputContainerPolicy.Decide(
            context.ContainerConfigured, context.Source, context.Input, context.MapMode,
            context.CopySubtitles, context.CopyDataStreams, context.CopyAttachments);
        VideoOutputResolutionPlan legacyResolution = VideoRestorationPipeline.ResolveFinalOutputResolution(
            1281, 719, context.Restoration, context.ScaleMode);
        VideoOutputGeometryPlan legacyGeometry = VideoOutputGeometryPlanner.Resolve(
            1281, 719, legacyResolution, context.Encoder, context.TenBit);

        Assert.Empty(EncodingPlanService.Compare(
            plan, legacyContainer, legacyGeometry, context.Encoder, context.TargetMb,
            FfmpegSourceDecodeMode.Strict));

        EncodingPlan laterPlan = EncodingPlanService.Create(Context(
            OutputContainerSelection.Mp4,
            ContainerCompatibilityPolicy.Intelligent,
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080 }));
        Assert.Equal(OutputContainer.Matroska, execution.ContainerDecision.Resolved);
        Assert.Equal(OutputContainer.Mp4, laterPlan.Container!.Effective);
    }

    [Fact]
    public void DivergenceIsReportedWithoutChangingFrozenExecutionValues()
    {
        EncodingPlan plan = EncodingPlanService.Create(Context(
            OutputContainerSelection.Auto,
            ContainerCompatibilityPolicy.Intelligent,
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080 },
            new MediaProbeStreamInfo { Index = 1, CodecType = "subtitle", CodecName = "ass" }));
        EncodingPlanService.EncodingPlanExecutionValues execution =
            EncodingPlanService.GetExecutionValues(plan);
        var legacyMismatch = new OutputContainerDecision
        {
            Requested = OutputContainerSelection.Auto,
            Resolved = OutputContainer.Mp4,
            Reason = "Test mismatch."
        };

        IReadOnlyList<EncodingPlanDivergence> divergences = EncodingPlanService.Compare(
            plan, legacyMismatch, execution.Geometry, execution.Encoder, execution.TargetMb,
            FfmpegSourceDecodeMode.Strict);

        Assert.Contains(divergences, divergence => divergence.Decision == "output-container");
        Assert.Equal(OutputContainer.Matroska, execution.ContainerDecision.Resolved);
    }

    [Fact]
    public void Mp4PlanSurfacesAuthoritativeConversionsAndGeometryCorrection()
    {
        MediaProbeResult source = new()
        {
            Success = true,
            Streams = new MediaProbeStreamInfo[]
            {
                new()
                {
                    Index = 0,
                    CodecType = "video",
                    CodecName = "h264",
                    Width = 1280,
                    Height = 701
                },
                new()
                {
                    Index = 1,
                    CodecType = "audio",
                    CodecName = "mp2"
                },
                new()
                {
                    Index = 2,
                    CodecType = "subtitle",
                    CodecName = "ass"
                }
            }
        };

        EncodingPlan plan = EncodingPlanService.Resolve(
            new EncodingPlanService.Request(
                source,
                EncodingInputSource.FromFile("source.mkv"),
                new VideoEncoderSelection(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265"),
                UseGpu: false,
                TenBit: false,
                AudioChannels: null,
                EncodingService.ScaleMode.None,
                new VideoRestorationSettings(),
                OutputContainerSelection.Mp4));

        Assert.True(plan.IsAvailable);
        EncodingPlanSection video = Assert.Single(plan.Sections, section => section.Title == "Video");
        Assert.Contains("H.264 1280×701 → HEVC 1280×702", video.Items[0].Value);

        EncodingPlanSection audio = Assert.Single(plan.Sections, section => section.Title == "Audio");
        Assert.Contains("MP2 → AAC 192 kbps", audio.Items[0].Value);
        Assert.Contains("MP4", audio.Items[0].Reason);

        EncodingPlanSection subtitles = Assert.Single(plan.Sections, section => section.Title == "Subtitles");
        Assert.Equal("ASS → mov_text", subtitles.Items[0].Value);
        Assert.Contains("converted", subtitles.Items[0].Reason, StringComparison.OrdinalIgnoreCase);

        EncodingPlanSection corrections = Assert.Single(
            plan.Sections,
            section => section.Title == "Compatibility corrections");
        Assert.Contains(corrections.Items, item => item.Label == "Geometry" &&
            item.Value == "1280×701 → 1280×702");
        Assert.Contains(corrections.Items, item => item.Label == "audio");
        Assert.Contains(corrections.Items, item => item.Label == "subtitle");
    }

    [Fact]
    public void RestorationPlanUsesResolvedPresetAndAiSettings()
    {
        MediaProbeResult source = new()
        {
            Success = true,
            Streams = new[]
            {
                new MediaProbeStreamInfo
                {
                    Index = 0,
                    CodecType = "video",
                    CodecName = "mpeg2video",
                    Width = 720,
                    Height = 480
                }
            }
        };
        var restoration = new VideoRestorationSettings
        {
            Mode = VideoRestorationMode.Custom,
            Preset = VideoRestorationPreset.VhsTvCaptureRestore,
            AiMode = AiRestorationMode.General,
            AiModelId = "general-x2",
            AiScale = AiRestorationScale.X2
        };

        EncodingPlan plan = EncodingPlanService.Resolve(
            new EncodingPlanService.Request(
                source,
                EncodingInputSource.FromFile("source.mpg"),
                new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"),
                UseGpu: true,
                TenBit: false,
                AudioChannels: null,
                EncodingService.ScaleMode.None,
                restoration,
                OutputContainerSelection.Matroska));

        EncodingPlanSection processing = Assert.Single(
            plan.Sections,
            section => section.Title == "Processing");
        Assert.Contains(processing.Items, item => item.Value == "VHS / TV capture restore");
        Assert.Contains(processing.Items, item => item.Value.Contains("General · general-x2 · 2×"));
    }

    private static EncodingDecisionContext Context(
        OutputContainerSelection container,
        ContainerCompatibilityPolicy policy,
        params MediaProbeStreamInfo[] streams) => new(
            new MediaProbeResult { Success = true, Streams = streams },
            EncodingInputSource.FromFile("source.mkv"),
            new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"),
            UseGpu: true,
            TargetMb: 100,
            ScaleMode: EncodingService.ScaleMode.None,
            Restoration: new VideoRestorationSettings(),
            EncoderPreset: "p5",
            QualityValue: 24,
            TenBit: false,
            AudioChannels: null,
            MapMode: EncodingService.StreamMapMode.KeepAll,
            CopySubtitles: true,
            CopyDataStreams: true,
            CopyAttachments: true,
            ContainerConfigured: container,
            CompatibilityPolicy: policy,
            KnownDuration: TimeSpan.FromMinutes(10));
}
