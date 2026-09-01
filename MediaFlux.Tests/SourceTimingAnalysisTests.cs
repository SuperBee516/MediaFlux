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
    [Fact] public void ShortSourcesReduceWindows() => Assert.Equal(3, SourceTimingAnalysisService.BuildPositions(10).Length);
    [Fact] public void InsufficientWindowsAreUnknown() { var r = _service.Analyze(Evidence(new TimingWindowEvidence(.5, new[] { 0d, .04 }, true))); Assert.Equal(SourceTimingClassification.Unknown, r.Classification); }
    private static SourceTimingEvidence Evidence(params TimingWindowEvidence[] windows) => new(25, 25, "1/1000", 0, 60, 60, Array.Empty<double>(), windows);
    private static TimingWindowEvidence Window(double p, double start, double cadence) => new(p, Enumerable.Range(0, 8).Select(i => start + i * cadence).ToArray());
    private sealed class NoopRunner : IMediaToolProcessRunner { public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new MediaToolProcessResult()); }
}
