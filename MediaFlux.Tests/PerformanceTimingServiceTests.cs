using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PerformanceTimingServiceTests
{
    [Fact]
    public void SuccessfulSummaryIncludesCompletedExecutedStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        Complete(timing, PerformanceTimingStage.FinalEncode);
        Complete(timing, PerformanceTimingStage.OutputValidation);
        Complete(timing, PerformanceTimingStage.Finalization);
        string summary = timing.BuildSummary();

        Assert.Contains("MediaFlux Performance Summary", summary);
        Assert.Contains("Total Elapsed:", summary);
        Assert.Contains("Source Analysis:", summary);
        Assert.Contains("Final Encode:", summary);
        Assert.Contains("Output Validation:", summary);
        Assert.Contains("Finalization:", summary);
        Assert.DoesNotContain("Not Completed", summary);
    }

    [Fact]
    public void AiDisabledSummaryOmitsAiStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        Complete(timing, PerformanceTimingStage.FinalEncode);
        string summary = timing.BuildSummary();

        Assert.DoesNotContain("AI Preparation:", summary);
        Assert.DoesNotContain("AI Extraction:", summary);
        Assert.Contains("Final Encode:", summary);
    }

    [Fact]
    public void AiEnabledSummaryIncludesAccumulatedChunkStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.AiPreparation);
        Complete(timing, PerformanceTimingStage.AiExtraction);
        Complete(timing, PerformanceTimingStage.AiExtraction);
        Complete(timing, PerformanceTimingStage.AiProcessing);
        Complete(timing, PerformanceTimingStage.AiValidation);
        Complete(timing, PerformanceTimingStage.AiReassembly);
        Complete(timing, PerformanceTimingStage.AiIntermediateJoin);
        string summary = timing.BuildSummary();

        Assert.Contains("AI Preparation:", summary);
        Assert.Contains("AI Extraction:", summary);
        Assert.Contains("AI Processing:", summary);
        Assert.Contains("AI Validation:", summary);
        Assert.Contains("AI Reassembly:", summary);
        Assert.Contains("AI Intermediate Join:", summary);
    }

    [Fact]
    public void CancellationMarksUnfinishedStageWithoutHidingCompletedStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        using (timing.Measure(PerformanceTimingStage.FinalEncode)) { }
        string summary = timing.BuildSummary();

        Assert.Contains("Source Analysis:", summary);
        Assert.Contains("Final Encode: Not Completed", summary);
    }

    [Fact]
    public void FailureMarksTheFailingStageNotCompleted()
    {
        var timing = new PerformanceTimingService();
        using (timing.Measure(PerformanceTimingStage.OutputValidation)) { }

        Assert.Contains("Output Validation: Not Completed", timing.BuildSummary());
    }

    [Fact]
    public void MultipleExecutedStagesAreRenderedInPipelineOrder()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.FinalEncode);
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        string summary = timing.BuildSummary();

        Assert.True(summary.IndexOf("Source Analysis:", StringComparison.Ordinal) < summary.IndexOf("Final Encode:", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcurrentScopesAreThreadSafe()
    {
        var timing = new PerformanceTimingService();
        Parallel.For(0, 128, _ => Complete(timing, PerformanceTimingStage.AiProcessing));
        string summary = timing.BuildSummary();

        Assert.Contains("AI Processing:", summary);
        Assert.DoesNotContain("AI Processing: Not Completed", summary);
    }

    [Fact]
    public void ChunkMetricsProducePlannerAndCalibrationSummaries()
    {
        var timing = new PerformanceTimingService();
        timing.SetAiChunkPlannerDecision(new AiChunkPlannerDecision(
            640, 480, MediaFlux.Models.AiRestorationScale.X2,
            1024 * 1024, 180L * 1024 * 1024, 100L * 1024 * 1024 * 1024,
            24L * 1024 * 1024 * 1024, 720, 720, 180, 180, "GPU VRAM", "GPU VRAM unavailable"));
        timing.RecordAiChunk(Chunk(1, 180, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(16), 26, 2_800L * 1024 * 1024, 32, 80L * 1024 * 1024, 50L * 1024 * 1024));
        timing.RecordAiChunk(Chunk(2, 120, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(14), 26, 2_600L * 1024 * 1024, 34, 84L * 1024 * 1024, 50L * 1024 * 1024));

        string summary = timing.BuildSummary();

        Assert.Contains("AI Chunk Planner Summary", summary);
        Assert.Contains("AI Planner Calibration Summary", summary);
        Assert.Contains("Planned Chunk Size: 180 frames", summary);
        Assert.Contains("Average Effective FPS: 10", summary);
        Assert.Contains("Planner appears conservative.", summary);
        Assert.Contains("GPU underutilized:", summary);
        Assert.Contains("Storage conservative:", summary);
        Assert.Contains("AI inference bottleneck:", summary);
    }

    [Fact]
    public void AiDisabledSummaryOmitsPlannerCalibrationSections()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.FinalEncode);

        string summary = timing.BuildSummary();

        Assert.DoesNotContain("AI Chunk Planner Summary", summary);
        Assert.DoesNotContain("AI Planner Calibration Summary", summary);
    }

    [Fact]
    public void CancelledAiOperationRetainsPlannerDecisionWithoutCalibration()
    {
        var timing = new PerformanceTimingService();
        timing.SetAiChunkPlannerDecision(new AiChunkPlannerDecision(1920, 1080, MediaFlux.Models.AiRestorationScale.X2, 1, 180, 1000, null, 180, 720, 180, 180, "GPU VRAM", "GPU VRAM unavailable"));
        using (timing.Measure(PerformanceTimingStage.AiProcessing)) { }

        string summary = timing.BuildSummary();

        Assert.Contains("AI Chunk Planner Summary", summary);
        Assert.DoesNotContain("AI Planner Calibration Summary", summary);
        Assert.Contains("AI Processing: Not Completed", summary);
    }

    [Fact]
    public void ChunkHardwareCollectorAggregatesCompletedSamples()
    {
        var collector = new AiChunkHardwareMetricsCollector();
        collector.Record(new(20, 1024, 40, 10, 5));
        collector.Record(new(60, 3072, 80, 30, 15));

        AiChunkHardwareMetrics metrics = collector.Snapshot();

        Assert.Equal(40, metrics.AverageGpuPercent);
        Assert.Equal(60, metrics.PeakGpuPercent);
        Assert.Equal(2048, metrics.AverageVramUsedBytes);
        Assert.Equal(3072, metrics.PeakVramUsedBytes);
        Assert.Equal(60, metrics.AverageCpuPercent);
        Assert.Equal(30, metrics.AverageDiskThroughputBytesPerSecond);
    }

    private static AiChunkPerformanceMetrics Chunk(int number, int frames, TimeSpan extraction, TimeSpan inference, TimeSpan validation, TimeSpan reassembly, TimeSpan total, double gpu, long vram, double cpu, double disk, long storage)
        => new(number, frames, extraction, inference, validation, reassembly, total,
            new AiChunkHardwareMetrics(gpu, gpu + 4, vram, vram + 100, cpu, disk), storage);

    private static void Complete(PerformanceTimingService timing, PerformanceTimingStage stage)
    {
        using PerformanceTimingService.PerformanceScope scope = timing.Measure(stage);
        scope.Complete();
    }
}
