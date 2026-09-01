using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;
public sealed class AiTimestampManifestTests
{
    [Fact] public void AlternatingVfrCadenceManifestIsValid() => Assert.True(AiTimestampManifestService.Validate(Manifest(.04, .08, .04, .08)).IsValid);
    [Fact] public void NonMonotonicManifestIsRejected() => Assert.False(AiTimestampManifestService.Validate(new("1/1000", new[] { Frame(0, 0, .04), Frame(1, .02, .04), Frame(2, .01, .04) })).IsValid);
    [Fact] public void MissingAiOutputIsRejected() => Assert.False(AiTimestampManifestService.Validate(Manifest(.04, .04), new[] { "frame-00000000.png" }).IsValid);
    [Fact] public void TimestampComparisonPreservesEveryCadenceInterval() { var m = Manifest(.04, .08, .04); Assert.True(AiTimestampManifestService.Compare(m, m.Frames.Select(f => f.PresentationSeconds).ToArray(), .000001).IsValid); }
    [Fact] public void TimestampComparisonRejectsChangedMiddleCadence() { var m = Manifest(.04, .08, .04); Assert.False(AiTimestampManifestService.Compare(m, new[] { 0d, .04, .13 }, .000001).IsValid); }
    [Fact] public void VfrIsAlwaysRejectedByCurrentAiPipeline() { var timing = new SourceTimingAnalysis(SourceTimingClassification.Vfr, AiTimingEligibility.PotentialFutureTimestampAware, 70, 24, 24, .1, false, false, "test"); AiRestorationValidationException ex = Assert.Throws<AiRestorationValidationException>(() => SourceTimingAnalysisService.EnsureCurrentCfrSupported(timing)); Assert.Contains("Conventional restoration", ex.Message); }
    [Theory]
    [InlineData(SourceTimingClassification.Unknown, AiTimingEligibility.Unknown)]
    [InlineData(SourceTimingClassification.IrregularUnsafe, AiTimingEligibility.UnsafeUnsupported)]
    public void UnknownAndUnsafeTimingLeaveConventionalEncodingAvailable(SourceTimingClassification classification, AiTimingEligibility eligibility)
    {
        var timing = new SourceTimingAnalysis(classification, eligibility, 0, null, null, 0, classification == SourceTimingClassification.IrregularUnsafe, false, "Insufficient or unsafe timestamp evidence.");
        AiRestorationValidationException ex = Assert.Throws<AiRestorationValidationException>(() => SourceTimingAnalysisService.EnsureCurrentCfrSupported(timing));
        Assert.Contains("Conventional restoration and normal encoding remain available", ex.Message);
    }
    [Fact] public void VerifiedCfrRemainsEligibleForExistingAiPipeline() => SourceTimingAnalysisService.EnsureCurrentCfrSupported(new(SourceTimingClassification.Cfr, AiTimingEligibility.EligibleCurrentCfrPipeline, 90, 24, 24, 0, false, false, "Verified CFR."));
    private static AiTimestampManifest Manifest(params double[] durations) { double pts = 0; var f = durations.Select((d, i) => { var item = Frame(i, pts, d); pts += d; return item; }).ToArray(); return new("1/1000", f); }
    private static AiFrameTimingEntry Frame(int index, double pts, double duration) => new(index, (long)Math.Round(pts * 1000), pts, duration, "1/1000", $"frame-{index:D8}.png", $"frame-{index:D8}.png");
}
