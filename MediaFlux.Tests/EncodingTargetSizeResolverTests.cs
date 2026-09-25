using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingTargetSizeResolverTests
{
    [Fact]
    public void AutomaticQualityUsesActiveManualTargetAndIgnoresDormantOrInvalidValues()
    {
        Assert.Equal(750, EncodingTargetSizeResolver.ResolveAutomaticQualityTargetMb(
            automaticQuality: true,
            autoTargetSize: false,
            configuredManualTarget: "750"));
        Assert.Null(EncodingTargetSizeResolver.ResolveAutomaticQualityTargetMb(
            automaticQuality: true,
            autoTargetSize: true,
            configuredManualTarget: "750"));
        Assert.Null(EncodingTargetSizeResolver.ResolveAutomaticQualityTargetMb(
            automaticQuality: true,
            autoTargetSize: false,
            configuredManualTarget: "0"));
    }

    [Fact]
    public void ExplicitQualityDoesNotReceiveAutomaticQualityTargetResolution()
    {
        Assert.Null(EncodingTargetSizeResolver.ResolveAutomaticQualityTargetMb(
            automaticQuality: false,
            autoTargetSize: false,
            configuredManualTarget: "750"));
    }

    [Fact]
    public void ResolvedAutomaticQualityTargetSupersedesQualityInFrozenPlanAndShadow()
    {
        double? targetMb = EncodingTargetSizeResolver.ResolveAutomaticQualityTargetMb(
            automaticQuality: true,
            autoTargetSize: false,
            configuredManualTarget: "750");
        var context = new EncodingDecisionContext(
            new MediaProbeResult
            {
                Success = true,
                SizeBytes = 400_000_000,
                Streams =
                [
                    new MediaProbeStreamInfo
                    {
                        Index = 0, CodecType = "video", CodecName = "h264",
                        Width = 1920, Height = 1080, FrameRate = 30, BitRate = 4_000_000
                    }
                ]
            },
            EncodingInputSource.FromFile("source.mkv"),
            new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"),
            UseGpu: true,
            TargetMb: targetMb,
            ScaleMode: EncodingService.ScaleMode.None,
            Restoration: new VideoRestorationSettings(),
            EncoderPreset: "p5",
            QualityValue: null,
            TenBit: false,
            AudioChannels: null,
            MapMode: EncodingService.StreamMapMode.KeepAll,
            CopySubtitles: true,
            CopyDataStreams: true,
            CopyAttachments: true,
            ContainerConfigured: OutputContainerSelection.Matroska,
            CompatibilityPolicy: ContainerCompatibilityPolicy.Intelligent,
            KnownDuration: TimeSpan.FromMinutes(10),
            QualityIntent: EncodingQualityIntent.Automatic(QualityTarget.Balanced));

        EncodingPlan plan = EncodingPlanService.Create(context);
        EncodingPlanService.EncodingPlanExecutionValues execution =
            EncodingPlanService.GetExecutionValues(plan);

        Assert.Equal(750, execution.TargetMb);
        Assert.True(execution.QualityResolution.IsSupersededByTargetSize);
        Assert.Null(execution.QualityResolution.EffectiveQuality);
        Assert.Equal(SourceAdaptiveShadowStatus.ExplicitUserPolicy,
            plan.SourceAdaptiveShadow!.Status);

        EncodingDecisionContext automaticQualityContext = context with { TargetMb = null };
        EncodingPlan automaticQualityPlan = EncodingPlanService.Create(automaticQualityContext);
        EncodingPlanService.EncodingPlanExecutionValues automaticQualityExecution =
            EncodingPlanService.GetExecutionValues(automaticQualityPlan);

        Assert.Null(automaticQualityExecution.TargetMb);
        Assert.False(automaticQualityExecution.QualityResolution.IsSupersededByTargetSize);
        Assert.NotNull(automaticQualityExecution.QualityResolution.EffectiveQuality);
        Assert.Equal(SourceAdaptiveShadowStatus.CalibrationCandidate,
            automaticQualityPlan.SourceAdaptiveShadow!.Status);
    }
}
