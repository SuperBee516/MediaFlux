using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SourceAdaptiveShadowCalibrationTests
{
    [Fact]
    public void AvcToHevcNvencAutomaticPlanCreatesCandidateWithoutChangingCq()
    {
        var (context, quality, geometry) = Create();

        SourceAdaptiveShadowCalibration shadow = new SourceAdaptiveShadowCalibrationService()
            .Create(context, quality, geometry);

        Assert.Contains(shadow.Status, new[] { SourceAdaptiveShadowStatus.CalibrationCandidate, SourceAdaptiveShadowStatus.PredictedExpansion });
        Assert.Equal(quality.EffectiveQuality, shadow.InitialCq);
        Assert.Equal(quality.EffectiveQuality, shadow.FinalExecutionCq);
        Assert.Equal(3_000, shadow.SourceVideoBitrateKbps);
        Assert.Equal("Measured FFprobe video stream bit_rate", shadow.SourceBitrateProvenance);
        Assert.NotNull(shadow.PredictedOutputVideoBitrateKbps);
        Assert.NotNull(shadow.PredictedTotalOutputBytes);
        Assert.NotNull(shadow.PredictedVideoRatio);
        Assert.NotNull(shadow.PredictedTotalRatio);

        EncodingPlan frozenPlan = EncodingPlanService.Create(context);
        EncodingPlanService.EncodingPlanExecutionValues execution = EncodingPlanService.GetExecutionValues(frozenPlan);
        Assert.Equal(execution.QualityResolution.EffectiveQuality, frozenPlan.SourceAdaptiveShadow!.FinalExecutionCq);
    }

    [Theory]
    [InlineData(VideoCodecFamily.H264, "h264", VideoEncoderIds.Nvenc, EncodingQualityIntentKind.QualityTarget, null, SourceAdaptiveShadowStatus.NotApplicable)]
    [InlineData(VideoCodecFamily.Hevc, "hevc", VideoEncoderIds.Nvenc, EncodingQualityIntentKind.QualityTarget, null, SourceAdaptiveShadowStatus.NotApplicable)]
    [InlineData(VideoCodecFamily.Hevc, "h264", VideoEncoderIds.Libx265, EncodingQualityIntentKind.QualityTarget, null, SourceAdaptiveShadowStatus.NotApplicable)]
    [InlineData(VideoCodecFamily.Hevc, "h264", VideoEncoderIds.Nvenc, EncodingQualityIntentKind.LegacyNumeric, null, SourceAdaptiveShadowStatus.ExplicitUserPolicy)]
    [InlineData(VideoCodecFamily.Hevc, "h264", VideoEncoderIds.Nvenc, EncodingQualityIntentKind.QualityTarget, 750d, SourceAdaptiveShadowStatus.ExplicitUserPolicy)]
    public void OutOfScopeAndExplicitPoliciesAreObservational(
        VideoCodecFamily targetCodec,
        string sourceCodec,
        string encoderId,
        EncodingQualityIntentKind intentKind,
        double? targetMb,
        SourceAdaptiveShadowStatus expected)
    {
        var (baseContext, baseQuality, geometry) = Create();
        EncodingQualityIntent intent = intentKind == EncodingQualityIntentKind.QualityTarget
            ? EncodingQualityIntent.Automatic(QualityTarget.Balanced)
            : EncodingQualityIntent.LegacyNumeric(19);
        var encoder = new VideoEncoderSelection(encoderId, targetCodec,
            targetCodec == VideoCodecFamily.Hevc ? "hevc_nvenc" : "h264_nvenc");
        var context = baseContext with
        {
            Encoder = encoder,
            QualityIntent = intent,
            TargetMb = targetMb,
            Source = CopyProbe(baseContext.Source, [Video(sourceCodec, 3_000_000)])
        };
        var quality = baseQuality with { Intent = intent, EffectiveQuality = 19, IsSupersededByTargetSize = targetMb.HasValue };

        SourceAdaptiveShadowCalibration shadow = new SourceAdaptiveShadowCalibrationService()
            .Create(context, quality, geometry);

        Assert.Equal(expected, shadow.Status);
        Assert.Equal(19, shadow.FinalExecutionCq);
    }

    [Fact]
    public void MissingMeasuredSourceVideoBitrateAbstainsInsteadOfUsingContainerBitrate()
    {
        var (context, quality, geometry) = Create();
        context = context with
        {
            Source = CopyProbe(context.Source, [Video("h264", null)], 4_000_000)
        };

        SourceAdaptiveShadowCalibration shadow = new SourceAdaptiveShadowCalibrationService()
            .Create(context, quality, geometry);

        Assert.Equal(SourceAdaptiveShadowStatus.InsufficientEvidence, shadow.Status);
        Assert.Null(shadow.SourceVideoBitrateKbps);
        Assert.Equal("Unavailable; total bitrate is not substituted", shadow.SourceBitrateProvenance);
        Assert.Null(shadow.PredictedVideoRatio);
    }

    [Fact]
    public void ShadowCalculationFailureKeepsCqAndReturnsUnavailableInsteadOfFailingPlan()
    {
        var (_, quality, geometry) = Create();

        SourceAdaptiveShadowCalibration shadow = new SourceAdaptiveShadowCalibrationService()
            .CreateSafely(null!, quality, geometry);

        Assert.Equal(SourceAdaptiveShadowStatus.PredictionUnavailable, shadow.Status);
        Assert.Equal(quality.EffectiveQuality, shadow.InitialCq);
        Assert.Equal(quality.EffectiveQuality, shadow.FinalExecutionCq);
    }

    [Fact]
    public void ChangedGeometryAndRestorationAreExcludedFromPrimaryCohort()
    {
        var (context, quality, _) = Create();
        VideoOutputGeometryPlan downscale = VideoOutputGeometryPlanner.Resolve(
            1920, 1080, new VideoOutputResolutionPlan(1280, 720, "", "downscale"), context.Encoder, false);
        SourceAdaptiveShadowCalibration scaled = new SourceAdaptiveShadowCalibrationService().Create(context, quality, downscale);
        Assert.Equal(SourceAdaptiveShadowStatus.TransformationExcluded, scaled.Status);

        context = context with { Restoration = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, Sharpen = VideoRestorationStrength.Light } };
        VideoOutputGeometryPlan same = VideoOutputGeometryPlanner.Resolve(
            1920, 1080, new VideoOutputResolutionPlan(1920, 1080, "", "same"), context.Encoder, false);
        SourceAdaptiveShadowCalibration restored = new SourceAdaptiveShadowCalibrationService().Create(context, quality, same);
        Assert.Equal(SourceAdaptiveShadowStatus.TransformationExcluded, restored.Status);
    }

    [Fact]
    public void PredictionSeparatesVideoFromAudioAndOutcomeUsesMeasuredOutputEvidence()
    {
        var (context, quality, geometry) = Create();
        SourceAdaptiveShadowCalibrationService service = new();
        SourceAdaptiveShadowCalibration baseline = service.Create(context, quality, geometry);
        var withAudio = context with
        {
            Source = CopyProbe(context.Source,
                [Video("h264", 3_000_000), new MediaProbeStreamInfo { CodecType = "audio", CodecName = "aac", BitRate = 1_000_000 }])
        };
        SourceAdaptiveShadowCalibration audio = service.Create(withAudio, quality, geometry);
        Assert.Equal(baseline.PredictedOutputVideoBitrateKbps, audio.PredictedOutputVideoBitrateKbps);
        Assert.NotEqual(baseline.PredictedTotalOutputBytes, audio.PredictedTotalOutputBytes);

        SourceAdaptiveShadowOutcome outcome = SourceAdaptiveShadowOutcome.FromOutput(
            baseline,
            new MediaProbeResult
            {
                Success = true,
                DurationSeconds = 600,
                BitRate = 6_300_000,
                Streams = [new MediaProbeStreamInfo { CodecType = "video", CodecName = "hevc", BitRate = 6_000_000, Width = 1920, Height = 1080, FrameRate = 30 }]
            },
            500_000_000);
        Assert.Equal(6_000, outcome.ActualOutputVideoBitrateKbps);
        Assert.Equal(500_000_000, outcome.ActualOutputBytes);
        Assert.Equal(2, outcome.ActualVideoRatio);
        Assert.Equal((6_000 / baseline.PredictedOutputVideoBitrateKbps!.Value - 1) * 100, outcome.PredictionErrorPercent);
    }

    [Fact]
    public void UnsuccessfulTerminalOutcomePersistsDecisionWithoutClaimingMeasurements()
    {
        var (context, quality, geometry) = Create();
        SourceAdaptiveShadowCalibration shadow = new SourceAdaptiveShadowCalibrationService().Create(context, quality, geometry);

        SourceAdaptiveShadowOutcome outcome = SourceAdaptiveShadowOutcome.ForTerminalOutcome(
            shadow, succeeded: false, new MediaProbeResult { Success = true }, 1234);

        Assert.Equal(shadow.Status, outcome.Decision.Status);
        Assert.Null(outcome.ActualOutputVideoBitrateKbps);
        Assert.Null(outcome.ActualOutputBytes);
        Assert.Null(outcome.ActualVideoRatio);
    }

    [Fact]
    public void ShadowOutcomePersistsAlongsideLegacyStatisticsRecords()
    {
        var (context, quality, geometry) = Create();
        SourceAdaptiveShadowCalibration shadow = new SourceAdaptiveShadowCalibrationService().Create(context, quality, geometry);
        string path = Path.Combine(Path.GetTempPath(), $"MediaFlux-shadow-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path,
                "{\"SchemaVersion\":1,\"Id\":\"legacy\",\"StartUtc\":\"2026-01-01T00:00:00Z\",\"EndUtc\":\"2026-01-01T00:10:00Z\",\"Outcome\":0,\"Codec\":\"hevc\",\"Encoder\":\"NVENC\"}" + Environment.NewLine);
            var service = new EncodingStatisticsService(path);
            Assert.True(service.AppendFinalized(new EncodingStatisticsRecord
            {
                Id = "shadow",
                StartUtc = DateTime.UtcNow,
                EndUtc = DateTime.UtcNow,
                Outcome = EncodingStatisticsOutcome.Success,
                SourceAdaptiveShadow = SourceAdaptiveShadowOutcome.FromOutput(shadow, null, 10)
            }));

            IReadOnlyList<EncodingStatisticsRecord> reopened = new EncodingStatisticsService(path).GetAll();

            Assert.Equal(2, reopened.Count);
            Assert.Equal(1, reopened.Single(record => record.Id == "legacy").SchemaVersion);
            EncodingStatisticsRecord restored = reopened.Single(record => record.Id == "shadow");
            Assert.Equal(shadow.Status, restored.SourceAdaptiveShadow!.Decision.Status);
            Assert.Null(restored.SourceAdaptiveShadow.ActualOutputVideoBitrateKbps);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static (EncodingDecisionContext Context, EncodingQualityResolution Quality, VideoOutputGeometryPlan Geometry) Create()
    {
        var encoder = new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc");
        MediaProbeResult source = new()
        {
            Success = true,
            SizeBytes = 300_000_000,
            DurationSeconds = 600,
            BitRate = 4_000_000,
            Streams = [Video("h264", 3_000_000)]
        };
        EncodingDecisionContext context = new(
            source, EncodingInputSource.FromFile("source.mkv"), encoder, true, null,
            EncodingService.ScaleMode.None, new VideoRestorationSettings(), "p5", 19, false, null,
            EncodingService.StreamMapMode.KeepAll, true, true, false, OutputContainerSelection.Matroska,
            ContainerCompatibilityPolicy.Intelligent, TimeSpan.FromSeconds(600),
            QualityIntent: EncodingQualityIntent.Automatic(QualityTarget.MaximumQuality));
        var resolution = new VideoOutputResolutionPlan(1920, 1080, "", "same geometry");
        VideoOutputGeometryPlan geometry = VideoOutputGeometryPlanner.Resolve(1920, 1080, resolution, encoder, false);
        EncodingQualityResolution quality = new(context.QualityIntent!, 19, EncoderQualityMechanism.Cq,
            EncodingQualityAssessment.TypicalSource, false, Array.Empty<EncodingQualityReason>());
        return (context, quality, geometry);
    }

    private static MediaProbeStreamInfo Video(string codec, long? bitrate) => new()
    {
        CodecType = "video", CodecName = codec, Width = 1920, Height = 1080,
        FrameRate = 30, BitRate = bitrate
    };

    private static MediaProbeResult CopyProbe(MediaProbeResult source,
        IReadOnlyList<MediaProbeStreamInfo> streams, long? bitRate = null) => new()
    {
        Success = source.Success,
        SizeBytes = source.SizeBytes,
        DurationSeconds = source.DurationSeconds,
        BitRate = bitRate ?? source.BitRate,
        Streams = streams
    };
}
