using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;
namespace MediaFlux.Tests;
public sealed class VfrManifestExtractionTests
{
    [Fact] public void ChunkSubsetRetainsGlobalManifestIdentity() { var manifest = new AiTimestampManifest("1/1000", Enumerable.Range(0, 181).Select(i => new AiFrameTimingEntry(i, i * 40, i * .04, .04, "1/1000", $"frame-{i:D8}.png", $"frame-{i:D8}.png")).ToArray()); Assert.Equal(180, manifest.Frames.Take(AiRestorationFrameProcessor.MaximumFramesPerChunk).Count()); Assert.Equal(180, manifest.Frames.Skip(180).First().FrameIndex); }
    [Fact] public void NonVfrTimingIsRejectedBeforeExtraction() { var timing = new SourceTimingAnalysis(SourceTimingClassification.Cfr, AiTimingEligibility.EligibleCurrentCfrPipeline, 80, 25, 25, 0, false, false, "cfr"); Assert.NotEqual(SourceTimingClassification.Vfr, timing.Classification); }
}
