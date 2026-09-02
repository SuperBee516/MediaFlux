using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiThroughputInvestigationTests
{
    [Fact]
    public void ComputesStageSharesIdleTimeEfficiencyAndOverlappedThroughput()
    {
        AiThroughputInvestigation report = AiThroughputInvestigationService.Analyze(Planner(), new[] { Chunk(100, 4, 8, 1, 4, 2, 1, 20, 40, 10 * MiB, 40 * MiB) });

        Assert.Equal(20, report.GpuBusyPercent);
        Assert.Equal(80, report.GpuIdlePercent);
        Assert.Equal(60, report.CpuIdlePercent);
        Assert.Equal(17d / 19d * 100d, report.PipelineEfficiencyPercent, 3);
        Assert.True(report.TheoreticalOverlappedFramesPerSecond > report.EffectiveFramesPerSecond);
        Assert.Equal(180, report.PlannerSelectedChunkSize);
        Assert.Equal(720, report.TheoreticalOptimalChunkSize);
        Assert.NotNull(report.EstimatedDiskWaitElapsed);
    }

    [Fact]
    public void DetectsEveryRequestedBottleneckFromCompletedChunkTelemetry()
    {
        AiThroughputInvestigation report = AiThroughputInvestigationService.Analyze(Planner(), new[] { Chunk(100, 4, 8, 4, 4, 3, 2, 20, 90, 10 * MiB, 40 * MiB) });

        Assert.Contains(report.Bottlenecks, value => value.StartsWith("GPU starvation", StringComparison.Ordinal));
        Assert.Contains(report.Bottlenecks, value => value.StartsWith("CPU bottleneck", StringComparison.Ordinal));
        Assert.Contains(report.Bottlenecks, value => value.StartsWith("Disk bottleneck", StringComparison.Ordinal));
        Assert.Contains(report.Bottlenecks, value => value.StartsWith("Small chunk overhead", StringComparison.Ordinal));
        Assert.Contains(report.Bottlenecks, value => value.StartsWith("Excessive validation overhead", StringComparison.Ordinal));
        Assert.Contains(report.Bottlenecks, value => value.StartsWith("Excessive process startup overhead", StringComparison.Ordinal));
    }

    [Fact]
    public void TimingSummaryReportsInvestigationForEveryCompletedChunkSet()
    {
        var timing = new PerformanceTimingService();
        timing.SetAiChunkPlannerDecision(Planner());
        timing.RecordAiChunk(Chunk(100, 4, 8, 1, 4, 2, 1, 20, 40, 40 * MiB, 40 * MiB));

        string summary = timing.BuildSummary();

        Assert.Contains("AI Throughput Investigation", summary);
        Assert.Contains("GPU Busy / Idle:", summary);
        Assert.Contains("Pipeline Efficiency:", summary);
        Assert.Contains("Theoretical Overlapped FPS:", summary);
        Assert.Contains("Planner Selected / Theoretical Optimal Chunk:", summary);
        Assert.Contains("Extraction / AI / Validation / Reassembly Share of Encode:", summary);
    }

    private static AiChunkPlannerDecision Planner() => new(1920, 1080, AiRestorationScale.X2, 1, 1, 1, 16 * 1024L * MiB, 180, 720, 720, 180, "Source resolution", "test");
    private static AiChunkPerformanceMetrics Chunk(int frames, int extraction, int inference, int validation, int reassembly, int overhead, int launch, double gpu, double cpu, double disk, long vram)
    {
        TimeSpan total = TimeSpan.FromSeconds(extraction + inference + validation + reassembly + overhead);
        return new(1, frames, TimeSpan.FromSeconds(extraction), TimeSpan.FromSeconds(inference), TimeSpan.FromSeconds(validation), TimeSpan.FromSeconds(reassembly), total,
            new(gpu, gpu, vram / 2, vram, cpu, disk), 0, TimeSpan.FromSeconds(launch), TimeSpan.FromSeconds(overhead));
    }
    private const long MiB = 1024L * 1024;
}
