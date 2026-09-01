using System.Text.RegularExpressions;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PerformanceThroughputTests
{
    [Fact]
    public void AiThroughputSummaryIncludesAggregateChunkMetrics()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.AiProcessing, 20);
        timing.RecordAiChunk(100, TimeSpan.FromMilliseconds(300));
        timing.RecordAiChunk(50, TimeSpan.FromMilliseconds(100));
        string summary = timing.BuildSummary();

        Assert.Contains("AI Throughput", summary);
        Assert.Contains("AI Frames: 150", summary);
        Assert.Contains("AI Chunks: 2", summary);
        Assert.Contains("Average Chunk Time: 0:00.200", summary);
        Assert.Contains("Fastest Chunk: 0:00.100", summary);
        Assert.Contains("Slowest Chunk: 0:00.300", summary);
        Assert.Matches(new Regex(@"Average AI FPS: [1-9]"), summary);
    }

    [Fact]
    public void AiDisabledSummaryOmitsThroughputSection()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.FinalEncode, 1);

        Assert.DoesNotContain("AI Throughput", timing.BuildSummary());
    }

    [Fact]
    public void LargestTimeConsumersAreOrderedByElapsedStageTime()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.SourceAnalysis, 10);
        Complete(timing, PerformanceTimingStage.FinalEncode, 45);
        Complete(timing, PerformanceTimingStage.OutputValidation, 25);
        string summary = timing.BuildSummary();

        int finalEncode = summary.IndexOf("1. Final Encode", StringComparison.Ordinal);
        int validation = summary.IndexOf("2. Output Validation", StringComparison.Ordinal);
        int source = summary.IndexOf("3. Source Analysis", StringComparison.Ordinal);
        Assert.True(finalEncode >= 0 && validation > finalEncode && source > validation);
    }

    [Theory]
    [InlineData(81, 100, 81)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 67)]
    public void PercentageCalculationRoundsMeasuredStageTime(long elapsedTicks, long totalTicks, int expected)
    {
        Assert.Equal(expected, PerformanceTimingService.CalculatePercentage(elapsedTicks, totalTicks));
    }

    [Fact]
    public void AverageAiFpsUsesProcessedFramesAndAiProcessingElapsed()
    {
        Assert.Equal(50d, PerformanceTimingService.CalculateAiFramesPerSecond(100, TimeSpan.FromSeconds(2)));
        Assert.Equal(0d, PerformanceTimingService.CalculateAiFramesPerSecond(100, TimeSpan.Zero));
    }

    [Fact]
    public void CancellationSummaryRetainsCompletedChunksAndMarksUnfinishedStage()
    {
        var timing = new PerformanceTimingService();
        timing.RecordAiChunk(24, TimeSpan.FromMilliseconds(200));
        using (timing.Measure(PerformanceTimingStage.AiProcessing)) { }
        string summary = timing.BuildSummary();

        Assert.Contains("AI Frames: 24", summary);
        Assert.Contains("AI Chunks: 1", summary);
        Assert.Contains("AI Processing: Not Completed", summary);
    }

    [Fact]
    public void FailureSummaryRetainsCompletedChunksAndMarksFailedStage()
    {
        var timing = new PerformanceTimingService();
        timing.RecordAiChunk(12, TimeSpan.FromMilliseconds(120));
        using (timing.Measure(PerformanceTimingStage.AiValidation)) { }
        string summary = timing.BuildSummary();

        Assert.Contains("AI Frames: 12", summary);
        Assert.Contains("AI Validation: Not Completed", summary);
    }

    private static void Complete(PerformanceTimingService timing, PerformanceTimingStage stage, int delayMilliseconds)
    {
        using PerformanceTimingService.PerformanceScope scope = timing.Measure(stage);
        Thread.Sleep(delayMilliseconds);
        scope.Complete();
    }
}
