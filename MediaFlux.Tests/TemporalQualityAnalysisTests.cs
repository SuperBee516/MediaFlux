using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class TemporalQualityAnalysisTests
{
    private readonly TemporalQualityAnalysisService _service = new("ffmpeg", new NoopRunner());

    [Fact]
    public void StableMatchedMotionIsNotFlagged() => Assert.Equal(TemporalStability.Stable, _service.Analyze(Frames(0.1, 0.01), Frames(0.1, 0.01)).Classification);
    [Fact]
    public void NaturalMotionIsNormalizedAgainstOriginal() => Assert.Equal(TemporalStability.Stable, _service.Analyze(new[] { new TemporalFrame(.1, .1), new TemporalFrame(.7, .3), new TemporalFrame(.2, .1), new TemporalFrame(.8, .3) }, new[] { new TemporalFrame(.1, .1), new TemporalFrame(.7, .3), new TemporalFrame(.2, .1), new TemporalFrame(.8, .3) }).Classification);
    [Fact]
    public void AlternatingRestoredFlickerIsSevere() => Assert.Equal(TemporalStability.SevereInstability, _service.Analyze(Frames(.1, .01), new[] { new TemporalFrame(.1, .01), new TemporalFrame(.9, .5), new TemporalFrame(.1, .01), new TemporalFrame(.9, .5), new TemporalFrame(.1, .01) }).Classification);
    [Fact]
    public void InsufficientEvidenceIsUnknown() => Assert.Equal(TemporalStability.Unknown, _service.Analyze(new[] { new TemporalFrame(.1, .1) }, new[] { new TemporalFrame(.1, .1) }).Classification);
    [Fact]
    public void SevereTemporalFindingReducesRecommendationConfidence()
    {
        var temporal = new TemporalQualityResult(TemporalStability.SevereInstability, 70, 0, 0, 0, 0, 0, 0, "test");
        var recommendation = VideoRestorationRecommendationService.Recommend(new VideoRestorationAnalysisResult { SourcePath = "a", AnimationHint = true, Noise = RestorationEvidenceLevel.Moderate }, true, temporalQuality: temporal);
        Assert.True(recommendation.RequiresManualConfirmation); Assert.Equal(temporal, recommendation.TemporalQuality);
    }
    private static TemporalFrame[] Frames(double luma, double edge) => Enumerable.Range(0, 5).Select(i => new TemporalFrame(luma + i * .01, edge + i * .001)).ToArray();
    private sealed class NoopRunner : IMediaToolProcessRunner { public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new MediaToolProcessResult()); }
}
