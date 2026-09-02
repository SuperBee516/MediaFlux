namespace MediaFlux.Services;

/// <summary>Diagnostic-only interpretation of completed AI chunks. It never changes pipeline scheduling or planning.</summary>
public sealed record AiThroughputInvestigation(
    TimeSpan ExtractionElapsed,
    TimeSpan InferenceElapsed,
    TimeSpan ValidationElapsed,
    TimeSpan ReassemblyElapsed,
    TimeSpan StartupShutdownElapsed,
    TimeSpan FfmpegProcessLaunchElapsed,
    TimeSpan TotalElapsed,
    TimeSpan? EstimatedDiskWaitElapsed,
    double ExtractionPercent,
    double InferencePercent,
    double ValidationPercent,
    double ReassemblyPercent,
    double StartupShutdownPercent,
    double? GpuBusyPercent,
    double? GpuIdlePercent,
    double? CpuBusyPercent,
    double? CpuIdlePercent,
    double PipelineEfficiencyPercent,
    double EffectiveFramesPerSecond,
    double TheoreticalOverlappedFramesPerSecond,
    int PlannerSelectedChunkSize,
    int TheoreticalOptimalChunkSize,
    double? AverageDiskThroughputBytesPerSecond,
    long? PeakVramBytes,
    IReadOnlyList<string> Bottlenecks);

public static class AiThroughputInvestigationService
{
    public static AiThroughputInvestigation Analyze(AiChunkPlannerDecision? plannerDecision, IReadOnlyList<AiChunkPerformanceMetrics> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        TimeSpan Sum(Func<AiChunkPerformanceMetrics, TimeSpan> selector) => TimeSpan.FromTicks(chunks.Sum(chunk => selector(chunk).Ticks));
        long frames = chunks.Sum(chunk => (long)chunk.FrameCount);
        TimeSpan extraction = Sum(chunk => chunk.ExtractionElapsed);
        TimeSpan inference = Sum(chunk => chunk.InferenceElapsed);
        TimeSpan validation = Sum(chunk => chunk.ValidationElapsed);
        TimeSpan reassembly = Sum(chunk => chunk.ReassemblyElapsed);
        TimeSpan overhead = Sum(chunk => chunk.StartupShutdownOverhead);
        TimeSpan launch = Sum(chunk => chunk.FfmpegProcessLaunchElapsed);
        TimeSpan total = Sum(chunk => chunk.TotalElapsed);
        double? averageGpu = Average(chunks.Select(chunk => chunk.Hardware.AverageGpuPercent));
        double? averageCpu = Average(chunks.Select(chunk => chunk.Hardware.AverageCpuPercent));
        double? averageDisk = Average(chunks.Select(chunk => chunk.Hardware.AverageDiskThroughputBytesPerSecond));
        long? peakVram = Max(chunks.Select(chunk => chunk.Hardware.PeakVramUsedBytes));
        TimeSpan theoreticalElapsed = TimeSpan.FromTicks(chunks.Sum(chunk =>
            Math.Max(chunk.InferenceElapsed.Ticks, chunk.ExtractionElapsed.Ticks + chunk.ReassemblyElapsed.Ticks) +
            chunk.ValidationElapsed.Ticks + chunk.StartupShutdownOverhead.Ticks));
        TimeSpan? estimatedDiskWait = SumEstimatedDiskWait(chunks);
        var bottlenecks = new List<string>();
        if (averageGpu is < 50 && (extraction + reassembly).Ticks >= inference.Ticks * .25)
            bottlenecks.Add("GPU starvation: low GPU utilization while extraction/reassembly leaves the GPU waiting.");
        if (averageCpu is >= 85 && (extraction + validation).Ticks >= total.Ticks * .20)
            bottlenecks.Add("CPU bottleneck: high CPU utilization during extraction or validation.");
        if (estimatedDiskWait is not null)
            bottlenecks.Add("Disk bottleneck: low measured disk throughput during disk-bound stages.");
        if (Percentage(overhead, total) >= 10)
            bottlenecks.Add("Small chunk overhead: startup/shutdown work is a material share of chunk time.");
        if (Percentage(validation, total) >= 15)
            bottlenecks.Add("Excessive validation overhead: validation is a material share of chunk time.");
        if (Percentage(launch, total) >= 5)
            bottlenecks.Add("Excessive process startup overhead: FFmpeg launches are a material share of chunk time.");

        int planned = plannerDecision?.FinalSelectedChunkSize ?? 0;
        int theoreticalOptimal = plannerDecision is null ? 0 : Math.Clamp(
            Math.Min(plannerDecision.StorageLimitedChunkSize, plannerDecision.VramLimitedChunkSize),
            AiChunkPlanner.MinimumFramesPerChunk,
            AiChunkPlanner.MaximumFramesPerChunk);
        return new(
            extraction, inference, validation, reassembly, overhead, launch, total, estimatedDiskWait,
            Percentage(extraction, total), Percentage(inference, total), Percentage(validation, total), Percentage(reassembly, total), Percentage(overhead, total),
            averageGpu, averageGpu is double gpu ? Math.Max(0, 100 - gpu) : null,
            averageCpu, averageCpu is double cpu ? Math.Max(0, 100 - cpu) : null,
            total > TimeSpan.Zero ? Math.Clamp(100 - Percentage(overhead, total), 0, 100) : 0,
            PerformanceTimingService.CalculateAiFramesPerSecond(frames, total),
            PerformanceTimingService.CalculateAiFramesPerSecond(frames, theoreticalElapsed),
            planned, theoreticalOptimal, averageDisk, peakVram, bottlenecks);
    }

    private static double Percentage(TimeSpan portion, TimeSpan total) => total > TimeSpan.Zero ? portion.TotalMilliseconds * 100d / total.TotalMilliseconds : 0;
    private static double? Average(IEnumerable<double?> values)
    {
        double[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }
    private static long? Max(IEnumerable<long?> values)
    {
        long[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }
    private static TimeSpan? SumEstimatedDiskWait(IEnumerable<AiChunkPerformanceMetrics> chunks)
    {
        long[] ticks = chunks.Select(chunk => chunk.EstimatedDiskWaitElapsed?.Ticks).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return ticks.Length == 0 ? null : TimeSpan.FromTicks(ticks.Sum());
    }
}
