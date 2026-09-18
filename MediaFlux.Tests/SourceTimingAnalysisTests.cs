using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;
public sealed class SourceTimingAnalysisTests
{
    private readonly SourceTimingAnalysisService _service = new("ffprobe", new NoopRunner());
    [Fact] public void ConsistentWindowsAreCfrEligible() { var r = _service.Analyze(Evidence(Window(.05, 0, .04), Window(.5, 20, .04), Window(.95, 40, .04))); Assert.Equal(SourceTimingClassification.Cfr, r.Classification); Assert.Equal(AiTimingEligibility.EligibleCurrentCfrPipeline, r.AiEligibility); }
    [Fact] public void LaterCadenceChangeIsVfr() { var r = _service.Analyze(Evidence(Window(.05, 0, .04), Window(.5, 20, .04), Window(.95, 40, .08))); Assert.Equal(SourceTimingClassification.Vfr, r.Classification); }
    [Fact] public void NonMonotonicWindowIsUnsafe() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.5, new[] { 0d, .04, .08, .02, .12 }))); Assert.Equal(SourceTimingClassification.IrregularUnsafe, r.Classification); }
    [Fact] public void MaterialTimestampDiscontinuityIsUnsafe() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.5, new[] { 0d, .04, .08, 2d, 2.04 }))); Assert.Equal(SourceTimingClassification.IrregularUnsafe, r.Classification); Assert.True(r.HasDiscontinuity); }
    [Fact] public void ShortSourcesReduceWindows() => Assert.Equal(3, SourceTimingAnalysisService.BuildPositions(10).Length);
    [Fact] public void InsufficientWindowsAreUnknown() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.5, new[] { 0d, .04 }, true))); Assert.Equal(SourceTimingClassification.Unknown, r.Classification); }
    [Fact] public void Cfr25WithBenignJitterIsSupported() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.05, Jittered(0)), new TimingWindowEvidence(.5, Jittered(20)), new TimingWindowEvidence(.95, Jittered(40)))); Assert.Equal(SourceTimingClassification.Cfr, r.Classification); Assert.Equal(AiTimingEligibility.EligibleCurrentCfrPipeline, r.AiEligibility); Assert.Contains("normalized dispersion", r.Reason); }
    [Fact] public void EqualTimestampsDoNotCreateNonMonotonicTiming() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.5, new[] { 0d, .04, .04, .08, .12, .16 }))); Assert.NotEqual(SourceTimingClassification.IrregularUnsafe, r.Classification); Assert.False(r.HasNonMonotonicTimestamps); }
    [Fact] public void IrregularityThresholdBoundariesAreDeterministic() { Assert.Equal(SourceTimingClassification.CfrMinorVariance, _service.Analyze(Evidence(Window(.05, 0, .04), Window(.5, 20, .042), Window(.95, 40, .04))).Classification); Assert.Equal(SourceTimingClassification.Vfr, _service.Analyze(Evidence(Window(.05, 0, .04), Window(.5, 20, .045), Window(.95, 40, .04))).Classification); }
    [Fact] public void UnsafeReasonNamesTheDetectedCondition() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.5, new[] { 0d, .04, .02, .08, .12 }))); Assert.Equal(SourceTimingClassification.IrregularUnsafe, r.Classification); Assert.Contains("Non-monotonic", r.Reason); }
    [Fact] public void NonMonotonicDiagnosticsIdentifyTheFirstBackwardFramePts() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.5, new[] { 0d, .04, .08, .02, .06, .01 }))); Assert.Equal(2, r.NonMonotonicEventCount); Assert.Equal(6, r.SamplesInspected); Assert.Equal(.06, r.LargestBackwardDeltaSeconds, 6); Assert.NotNull(r.FirstNonMonotonicEvent); Assert.Equal("decoded-frame-best-effort-PTS", r.FirstNonMonotonicEvent!.TimestampDomain); Assert.Equal(2, r.FirstNonMonotonicEvent.PreviousSampleIndex); Assert.Equal(3, r.FirstNonMonotonicEvent.CurrentSampleIndex); Assert.Equal(.08, r.FirstNonMonotonicEvent.PreviousTimestampSeconds, 6); Assert.Equal(.02, r.FirstNonMonotonicEvent.CurrentTimestampSeconds, 6); Assert.Contains("backward-events=2", r.Reason); }
    private static SourceTimingEvidence Evidence(params TimingWindowEvidence[] windows) => new(25, 25, "1/1000", 0, 60, 60, Array.Empty<double>(), windows);
    private static TimingWindowEvidence Window(double p, double start, double cadence) => new(p, Enumerable.Range(0, 8).Select(i => start + i * cadence).ToArray());
    private static double[] Jittered(double start) => Enumerable.Range(0, 50).Select(i => start + i * .04 + (i % 3 == 0 ? .0004 : i % 3 == 1 ? -.0003 : 0)).ToArray();
    private sealed class NoopRunner : IMediaToolProcessRunner { public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new MediaToolProcessResult()); }
}
