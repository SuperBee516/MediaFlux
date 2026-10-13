using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AutomaticTargetPlannerTests
{
    private static readonly VideoEncoderSelection Hevc =
        new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265");

    private static readonly VideoOutputGeometryPlan Geometry = new(
        1920, 1080, 1920, 1080, 1920, 1080, "", "yuv420p", 2, "test geometry");

    private static readonly EncodingInputSource Input = EncodingInputSource.FromFile("C:\\media\\source.mp4");

    private static OutputContainerDecision Container(params StreamCompatibilityPlan[] streams) => new()
    {
        Requested = OutputContainerSelection.Matroska,
        Resolved = OutputContainer.Matroska,
        Reason = "test",
        CopySubtitles = true,
        CopyDataStreams = true,
        CopyAttachments = true,
        StreamPlans = streams
    };

    private static MediaProbeResult Source(string codec = "h264", double fps = 30, int width = 1920,
        int height = 1080, long sourceBytes = 50_000_000, double duration = 100,
        long videoBitrate = 4_000_000, params MediaProbeStreamInfo[] additionalStreams) => new()
    {
        Success = true,
        DurationSeconds = duration,
        SizeBytes = sourceBytes,
        Streams = new[]
        {
            new MediaProbeStreamInfo
            {
                Index = 0, CodecType = "video", CodecName = codec, Width = width,
                Height = height, FrameRate = fps, BitRate = videoBitrate, PixelFormat = "yuv420p"
            }
        }.Concat(additionalStreams).ToArray()
    };

    private static AutomaticTargetPlan Plan(
        MediaProbeResult? source = null,
        QualityTarget quality = QualityTarget.Balanced,
        VideoOutputGeometryPlan? geometry = null,
        OutputContainerDecision? container = null,
        TimeSpan? duration = null) => AutomaticTargetPlanner.Create(
            source ?? Source(), Input, Hevc, geometry ?? Geometry, duration ?? TimeSpan.FromSeconds(100),
            quality, null, container ?? Container());

    [Fact]
    public void FiveProfilesProduceStrictlyOrderedSourceAwareVideoTargets()
    {
        QualityTarget[] profiles =
        [
            QualityTarget.SmallerFile, QualityTarget.Efficient, QualityTarget.Balanced,
            QualityTarget.HighQuality, QualityTarget.MaximumQuality
        ];

        AutomaticTargetPlan[] plans = profiles.Select(profile => Plan(quality: profile)).ToArray();

        Assert.All(plans, plan => Assert.True(plan.IsAvailable, plan.Reason));
        Assert.Equal(profiles, plans.Select(plan => plan.QualityTarget));
        Assert.True(plans.Zip(plans.Skip(1), (left, right) =>
            left.TargetVideoBitrateKbps < right.TargetVideoBitrateKbps).All(ordered => ordered));
        Assert.Equal(new[] { 0.78d, 0.90d, 1.00d, 1.12d, 1.25d }, profiles.Select(AutomaticTargetPlanner.QualityScale));
    }

    [Theory]
    [InlineData("h264", 4_000_000L, 1920, 1080, 30d)]
    [InlineData("hevc", 7_000_000L, 1920, 1080, 60d)]
    [InlineData("h264", 8_000_000L, 3840, 2160, 24d)]
    public void SourceCodecRateGeometryAndFrameRateAffectTarget(
        string codec, long videoBitrate, int width, int height, double fps)
    {
        AutomaticTargetPlan target = Plan(source: Source(codec, fps, width, height,
            sourceBytes: 120_000_000, duration: 120, videoBitrate: videoBitrate),
            geometry: new(width, height, width, height, width, height, "", "yuv420p", 2, "test"),
            duration: TimeSpan.FromSeconds(120));

        Assert.True(target.IsAvailable, target.Reason);
        Assert.True(target.SourceVideoBitrateKbps > 0);
        Assert.True(target.TargetVideoBitrateKbps > 0);
        Assert.Equal((long)Math.Round(target.TargetTotalBitrateKbps!.Value * 120 * 125), target.ProjectedOutputBytes);
    }

    [Fact]
    public void LowBitrateEfficientSourceIsCappedToTenPercentReductionAndMarkedForReview()
    {
        var source = Source("hevc", fps: 30, sourceBytes: 10_000_000, duration: 100, videoBitrate: 750_000);

        AutomaticTargetPlan plan = Plan(source, QualityTarget.SmallerFile);

        Assert.True(plan.IsAvailable, plan.Reason);
        Assert.Equal(AutomaticTargetDisposition.Review, plan.Disposition);
        Assert.True(plan.TargetVideoBitrateKbps >= plan.SourceVideoBitrateKbps * 0.9d);
        Assert.Contains("low-bitrate", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultipleMappedAudioStreamsAndAttachmentsAreIncludedExactlyOnce()
    {
        var source = Source(additionalStreams:
        [
            new() { Index = 1, CodecType = "audio", CodecName = "aac", BitRate = 192_000 },
            new() { Index = 2, CodecType = "audio", CodecName = "aac", BitRate = 256_000 },
            new() { Index = 3, CodecType = "subtitle", CodecName = "ass", BitRate = 20_000 },
            new() { Index = 4, CodecType = "attachment", CodecName = "ttf", ExtraDataSizeBytes = 100_000 }
        ]);
        OutputContainerDecision container = Container(
            new(1, "audio", "aac", StreamCompatibilityAction.Copy, "copy"),
            new(2, "audio", "aac", StreamCompatibilityAction.Copy, "copy"),
            new(3, "subtitle", "ass", StreamCompatibilityAction.Copy, "copy"));

        AutomaticTargetPlan plan = Plan(source, container: container);

        Assert.True(plan.IsAvailable, plan.Reason);
        Assert.Equal(448d, plan.PlannedAudioBitrateKbps!.Value, 3);
        Assert.Equal(28d, plan.PlannedMappedAncillaryBitrateKbps!.Value, 3);
        Assert.True(plan.ContainerOverheadBitrateKbps >= 16);
        double plannedTotal = plan.TargetVideoBitrateKbps!.Value + plan.PlannedAudioBitrateKbps.Value +
            plan.PlannedMappedAncillaryBitrateKbps.Value + plan.ContainerOverheadBitrateKbps!.Value;
        Assert.Equal(plannedTotal, plan.TargetTotalBitrateKbps!.Value, 6);
        Assert.Equal((long)Math.Round(plan.TargetTotalBitrateKbps!.Value * 100 * 125), plan.ProjectedOutputBytes);
    }

    [Fact]
    public void MissingDurationAndInfeasibleOutputBudgetFailClosed()
    {
        Assert.Equal(AutomaticTargetDisposition.Unavailable,
            Plan(source: Source(duration: 0), duration: TimeSpan.Zero).Disposition);

        var source = Source(sourceBytes: 5_000_000, duration: 100, videoBitrate: 350_000,
            additionalStreams: [new() { Index = 1, CodecType = "audio", CodecName = "aac", BitRate = 2_000_000 }]);
        OutputContainerDecision mappedAudio = Container(
            new StreamCompatibilityPlan(1, "audio", "aac", StreamCompatibilityAction.Copy, "copy"));
        AutomaticTargetPlan infeasible = Plan(source, container: mappedAudio);
        Assert.Equal(AutomaticTargetDisposition.Unavailable, infeasible.Disposition);
        Assert.False(infeasible.IsAvailable);
    }

    [Fact]
    public void ProjectionCanHonestlyExceedTheSourceSize()
    {
        AutomaticTargetPlan plan = Plan(
            Source("h264", fps: 30, sourceBytes: 10_000_000, duration: 100, videoBitrate: 750_000),
            QualityTarget.MaximumQuality);

        Assert.True(plan.ProjectedOutputBytes > 0);
        Assert.True(plan.Disposition is AutomaticTargetDisposition.Review or AutomaticTargetDisposition.Unavailable);
        if (plan.IsAvailable)
            Assert.True(plan.ProjectedOutputBytes >= 10_000_000);
    }
}
