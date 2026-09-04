using System.Diagnostics;
using System.Text;

namespace MediaFlux.Services;

/// <summary>Per-encode, in-memory timing collector for diagnostic logging.</summary>
public sealed class PerformanceTimingService
{
    private readonly object _gate = new();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly Dictionary<PerformanceTimingStage, StageTiming> _stages = new();
    private long _aiFrames;
    private int _aiChunks;
    private TimeSpan _aiChunkElapsed;
    private TimeSpan _fastestAiChunk = TimeSpan.MaxValue;
    private TimeSpan _slowestAiChunk;
    private readonly List<AiChunkPerformanceMetrics> _aiChunkMetrics = new();
    private AiChunkPlannerDecision? _aiPlannerDecision;
    private NcnnRuntimeSelection? _ncnnRuntimeSelection;
    private HardwareSnapshot? _hardware;
    private int _hardwareSamples;
    private double _gpuTotal, _cpuTotal, _readTotal, _writeTotal;
    private int _gpuCount, _cpuCount, _readCount, _writeCount;
    private double? _gpuPeak, _cpuPeak;
    private long _vramTotal;
    private int _vramCount;
    private long? _vramPeak;

    public PerformanceScope Measure(PerformanceTimingStage stage) => new(this, stage);

    /// <summary>Records one fully completed AI chunk without retaining chunk history.</summary>
    public void RecordAiChunk(int frameCount, TimeSpan elapsed)
    {
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        lock (_gate)
            RecordAiChunkCore(frameCount, elapsed);
    }

    /// <summary>Records completed per-chunk diagnostics while retaining only the completed chunk metrics.</summary>
    public void RecordAiChunk(AiChunkPerformanceMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.FrameCount <= 0) throw new ArgumentOutOfRangeException(nameof(metrics));
        if (metrics.TotalElapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(metrics));
        lock (_gate)
        {
            RecordAiChunkCore(metrics.FrameCount, metrics.TotalElapsed);
            _aiChunkMetrics.Add(metrics);
        }
    }

    public void SetAiChunkPlannerDecision(AiChunkPlannerDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate) _aiPlannerDecision = decision;
    }

    public void SetHardwareSnapshot(HardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) _hardware = snapshot;
    }

    public long? DedicatedGpuVramBytes
    {
        get { lock (_gate) return _hardware?.DedicatedVramBytes; }
    }

    public string? GpuIdentity
    {
        get { lock (_gate) return _hardware?.Gpu; }
    }

    public string? GpuDriver
    {
        get { lock (_gate) return _hardware?.GpuDriver; }
    }

    /// <summary>Returns the immutable discovery result for read-only runtime observers.</summary>
    public HardwareSnapshot? GetHardwareSnapshot()
    {
        lock (_gate) return _hardware;
    }

    public void SetNcnnRuntimeSelection(NcnnRuntimeSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        lock (_gate) _ncnnRuntimeSelection = selection;
    }

    public void SetAiBackend(string backend, string executablePath)
    {
        lock (_gate)
        {
            if (_hardware is null) return;
            string version;
            try { version = File.Exists(executablePath) ? FileVersionInfo.GetVersionInfo(executablePath).FileVersion ?? "Unknown" : "Unavailable"; }
            catch { version = "Unavailable"; }
            _hardware = _hardware with { AiBackend = $"{backend} {version}" };
        }
    }

    public void RecordHardwareSample(HardwareUsageSample sample)
    {
        lock (_gate)
        {
            _hardwareSamples++;
            Add(sample.GpuPercent, ref _gpuTotal, ref _gpuCount, ref _gpuPeak);
            Add(sample.CpuPercent, ref _cpuTotal, ref _cpuCount, ref _cpuPeak);
            AddAverage(sample.DiskReadBytesPerSecond, ref _readTotal, ref _readCount);
            AddAverage(sample.DiskWriteBytesPerSecond, ref _writeTotal, ref _writeCount);
            if (sample.VramUsedBytes is long vram) { _vramTotal += vram; _vramCount++; _vramPeak = Math.Max(_vramPeak ?? 0, vram); }
        }
    }

    public void LogSummary(Action<string>? log)
    {
        if (log == null) return;
        log(BuildSummary());
    }

    public string BuildSummary()
    {
        TimeSpan totalElapsed = Stopwatch.GetElapsedTime(_startedAt);
        var builder = new StringBuilder()
            .AppendLine("==================================")
            .AppendLine("MediaFlux Performance Summary")
            .AppendLine("==================================")
            .Append("Total Elapsed: ").AppendLine(Format(totalElapsed));
        lock (_gate)
        {
            foreach (PerformanceTimingStage stage in StageOrder)
            {
                if (!_stages.TryGetValue(stage, out StageTiming? timing)) continue;
                builder.Append(StageNames[stage]).Append(": ")
                    .AppendLine(timing.Completed ? Format(timing.Elapsed) : "Not Completed");
            }
            AppendAiThroughput(builder);
            AppendAiChunkPlannerSummary(builder);
            AppendAiPlannerCalibrationSummary(builder);
            AppendAiThroughputInvestigation(builder, totalElapsed);
            AppendNcnnRuntimeSummary(builder);
            AppendHardwareSummary(builder);
            AppendLargestTimeConsumers(builder);
        }
        return builder.ToString().TrimEnd();
    }

    private void Record(PerformanceTimingStage stage, TimeSpan elapsed, bool completed)
    {
        lock (_gate)
        {
            if (_stages.TryGetValue(stage, out StageTiming? timing))
            {
                timing.Elapsed += elapsed;
                timing.Completed &= completed;
            }
            else _stages.Add(stage, new StageTiming(elapsed, completed));
        }
    }

    internal void RecordElapsedForTesting(PerformanceTimingStage stage, TimeSpan elapsed) => Record(stage, elapsed, completed: true);

    private void RecordAiChunkCore(int frameCount, TimeSpan elapsed)
    {
        _aiFrames += frameCount;
        _aiChunks++;
        _aiChunkElapsed += elapsed;
        if (elapsed < _fastestAiChunk) _fastestAiChunk = elapsed;
        if (elapsed > _slowestAiChunk) _slowestAiChunk = elapsed;
    }

    private static string Format(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? elapsed.ToString(@"h\:mm\:ss\.fff")
        : elapsed.ToString(@"m\:ss\.fff");

    private void AppendAiThroughput(StringBuilder builder)
    {
        if (_aiChunks == 0) return;
        TimeSpan processingElapsed = _stages.TryGetValue(PerformanceTimingStage.AiProcessing, out StageTiming? processing)
            ? processing.Elapsed
            : TimeSpan.Zero;
        builder.AppendLine()
            .AppendLine("AI Throughput")
            .Append("AI Frames: ").AppendLine(_aiFrames.ToString())
            .Append("AI Chunks: ").AppendLine(_aiChunks.ToString())
            .Append("Average AI FPS: ").AppendLine(CalculateAiFramesPerSecond(_aiFrames, processingElapsed).ToString("0.##"))
            .Append("Average Chunk Time: ").AppendLine(Format(TimeSpan.FromTicks(_aiChunkElapsed.Ticks / _aiChunks)))
            .Append("Fastest Chunk: ").AppendLine(Format(_fastestAiChunk))
            .Append("Slowest Chunk: ").AppendLine(Format(_slowestAiChunk));
    }

    private void AppendLargestTimeConsumers(StringBuilder builder)
    {
        KeyValuePair<PerformanceTimingStage, StageTiming>[] executedStages = _stages
            .Where(pair => pair.Value.Elapsed > TimeSpan.Zero)
            .OrderByDescending(pair => pair.Value.Elapsed)
            .ToArray();
        KeyValuePair<PerformanceTimingStage, StageTiming>[] stages = executedStages.Take(5).ToArray();
        if (stages.Length == 0) return;

        long totalTicks = executedStages.Sum(pair => pair.Value.Elapsed.Ticks);
        if (totalTicks <= 0) return;
        builder.AppendLine().AppendLine("Largest Time Consumers");
        for (int index = 0; index < stages.Length; index++)
        {
            KeyValuePair<PerformanceTimingStage, StageTiming> stage = stages[index];
            builder.Append(index + 1).Append(". ").Append(StageNames[stage.Key]).Append(" ..... ")
                .Append(CalculatePercentage(stage.Value.Elapsed.Ticks, totalTicks)).AppendLine("%");
        }
    }

    private void AppendAiChunkPlannerSummary(StringBuilder builder)
    {
        if (_aiPlannerDecision is not AiChunkPlannerDecision decision) return;
        builder.AppendLine().AppendLine("AI Chunk Planner Summary")
            .Append("Resolution: ").Append(decision.SourceWidth).Append('x').AppendLine(decision.SourceHeight.ToString())
            .Append("AI Scale: ").Append((int)decision.AiScale).AppendLine("x")
            .Append("Estimated Bytes per Frame: ").AppendLine(Bytes(decision.EstimatedBytesPerFrame))
            .Append("Estimated Peak Extracted Storage: ").AppendLine(Bytes(decision.EstimatedPeakExtractedStorageBytes))
            .Append("Estimated Peak Restored Storage: ").AppendLine(Bytes(decision.EstimatedPeakRestoredStorageBytes))
            .Append("Estimated Intermediate Storage: ").AppendLine(Bytes(decision.EstimatedIntermediateStorageBytes))
            .Append("Active Working Files: ").AppendLine(Bytes(decision.ActiveWorkingFilesBytes))
            .Append("Safety Margin: ").AppendLine(Bytes(decision.SafetyMarginBytes))
            .Append("Final Required Storage: ").AppendLine(Bytes(decision.FinalRequiredStorageBytes))
            .Append("Available Storage: ").AppendLine(Bytes(decision.AvailableTemporaryStorageBytes))
            .Append("Dedicated GPU VRAM: ").AppendLine(Bytes(decision.DedicatedGpuVramBytes))
            .Append("Default Chunk Size: ").Append(decision.DefaultChunkSize).AppendLine(" frames")
            .Append("Storage-Limited Chunk Size: ").Append(decision.StorageLimitedChunkSize).AppendLine(" frames")
            .Append("VRAM-Limited Chunk Size: ").Append(decision.VramLimitedChunkSize).AppendLine(" frames")
            .Append("Final Selected Chunk Size: ").Append(decision.FinalSelectedChunkSize).AppendLine(" frames")
            .Append("Determining Constraint: ").AppendLine(decision.DeterminingConstraint);
    }

    private void AppendAiPlannerCalibrationSummary(StringBuilder builder)
    {
        if (_aiPlannerDecision is null || _aiChunkMetrics.Count == 0) return;
        AiChunkPerformanceMetrics[] chunks = _aiChunkMetrics.ToArray();
        long frames = chunks.Sum(chunk => (long)chunk.FrameCount);
        TimeSpan total = TimeSpan.FromTicks(chunks.Sum(chunk => chunk.TotalElapsed.Ticks));
        TimeSpan average = TimeSpan.FromTicks(total.Ticks / chunks.Length);
        double? averageGpu = Average(chunks.Select(chunk => chunk.Hardware.AverageGpuPercent));
        double? averageCpu = Average(chunks.Select(chunk => chunk.Hardware.AverageCpuPercent));
        double? averageDisk = Average(chunks.Select(chunk => chunk.Hardware.AverageDiskThroughputBytesPerSecond));
        long? averageVram = AverageLong(chunks.Select(chunk => chunk.Hardware.AverageVramUsedBytes));
        long? peakVram = Max(chunks.Select(chunk => chunk.Hardware.PeakVramUsedBytes));
        long? averageMeasuredStorage = AverageLong(chunks.Select(chunk => chunk.MeasuredTemporaryStorageBytes));
        TimeSpan extraction = TimeSpan.FromTicks(chunks.Sum(chunk => chunk.ExtractionElapsed.Ticks));
        TimeSpan inference = TimeSpan.FromTicks(chunks.Sum(chunk => chunk.InferenceElapsed.Ticks));

        builder.AppendLine().AppendLine("AI Planner Calibration Summary")
            .Append("Planned Chunk Size: ").Append(_aiPlannerDecision.FinalSelectedChunkSize).AppendLine(" frames")
            .Append("Actual Average Chunk Duration: ").AppendLine(Format(average))
            .Append("Average Effective FPS: ").AppendLine(CalculateAiFramesPerSecond(frames, total).ToString("0.##"))
            .Append("Peak VRAM Used: ").AppendLine(Bytes(peakVram))
            .Append("Average VRAM Used: ").AppendLine(Bytes(averageVram))
            .Append("Average GPU Utilization: ").AppendLine(Percent(averageGpu))
            .Append("Average CPU Utilization: ").AppendLine(Percent(averageCpu))
            .Append("Average Disk Throughput: ").AppendLine(BytesPerSecond(averageDisk));

        bool gpuConsistentlyBelowForty = chunks.All(chunk => chunk.Hardware.PeakGpuPercent is < 40);
        bool gpuUnderutilized = gpuConsistentlyBelowForty && averageGpu is < 40 && peakVram is long used && _aiPlannerDecision.DedicatedGpuVramBytes is long capacity && used < capacity / 4;
        bool storageConservative = averageMeasuredStorage is long actual && actual > 0 && _aiPlannerDecision.EstimatedTemporaryStoragePerChunk > actual * 2;
        double extractionPercent = Percentage(extraction, total);
        double inferencePercent = Percentage(inference, total);
        bool conservative = _aiPlannerDecision.FinalSelectedChunkSize * 2 < _aiPlannerDecision.DefaultChunkSize || gpuUnderutilized || storageConservative;
        if (conservative) builder.AppendLine("Planner appears conservative.");
        if (gpuUnderutilized) builder.AppendLine($"GPU underutilized: average GPU utilization {averageGpu:0.#}%; peak VRAM {Bytes(peakVram)} of {Bytes(_aiPlannerDecision.DedicatedGpuVramBytes)}.");
        if (storageConservative) builder.AppendLine($"Storage conservative: estimated chunk storage {Bytes(_aiPlannerDecision.EstimatedTemporaryStoragePerChunk)} exceeded measured {Bytes(averageMeasuredStorage)}.");
        if (extractionPercent >= 25) builder.AppendLine($"CPU bottleneck: extraction consumed {extractionPercent:0.#}% of total AI time.");
        if (inferencePercent >= 50) builder.AppendLine($"AI inference bottleneck: inference consumed {inferencePercent:0.#}% of total AI time.");
    }

    private void AppendAiThroughputInvestigation(StringBuilder builder, TimeSpan encodeElapsed)
    {
        if (_aiChunkMetrics.Count == 0) return;
        AiThroughputInvestigation report = AiThroughputInvestigationService.Analyze(_aiPlannerDecision, _aiChunkMetrics);
        builder.AppendLine().AppendLine("AI Throughput Investigation")
            .Append("Extraction: ").Append(Format(report.ExtractionElapsed)).Append(" (").Append(report.ExtractionPercent.ToString("0.#")).AppendLine("%)")
            .Append("AI Inference: ").Append(Format(report.InferenceElapsed)).Append(" (").Append(report.InferencePercent.ToString("0.#")).AppendLine("%)")
            .Append("Validation: ").Append(Format(report.ValidationElapsed)).Append(" (").Append(report.ValidationPercent.ToString("0.#")).AppendLine("%)")
            .Append("Reassembly: ").Append(Format(report.ReassemblyElapsed)).Append(" (").Append(report.ReassemblyPercent.ToString("0.#")).AppendLine("%)")
            .Append("Chunk Startup/Shutdown: ").Append(Format(report.StartupShutdownElapsed)).Append(" (").Append(report.StartupShutdownPercent.ToString("0.#")).AppendLine("%)")
            .Append("FFmpeg Process Launch Latency: ").AppendLine(Format(report.FfmpegProcessLaunchElapsed))
            .Append("Estimated Disk Wait: ").AppendLine(report.EstimatedDiskWaitElapsed is TimeSpan wait ? Format(wait) : "Unavailable")
            .Append("GPU Busy / Idle: ").Append(Percent(report.GpuBusyPercent)).Append(" / ").AppendLine(Percent(report.GpuIdlePercent))
            .Append("CPU Busy / Idle: ").Append(Percent(report.CpuBusyPercent)).Append(" / ").AppendLine(Percent(report.CpuIdlePercent))
            .Append("Peak VRAM: ").AppendLine(Bytes(report.PeakVramBytes))
            .Append("Average Disk Throughput: ").AppendLine(BytesPerSecond(report.AverageDiskThroughputBytesPerSecond))
            .Append("Pipeline Efficiency: ").Append(report.PipelineEfficiencyPercent.ToString("0.#")).AppendLine("%")
            .Append("Effective AI FPS: ").AppendLine(report.EffectiveFramesPerSecond.ToString("0.##"))
            .Append("Theoretical Overlapped FPS: ").AppendLine(report.TheoreticalOverlappedFramesPerSecond.ToString("0.##"));
        builder.Append("Extraction / AI / Validation / Reassembly Share of Encode: ")
            .Append(Percentage(report.ExtractionElapsed, encodeElapsed).ToString("0.#")).Append("% / ")
            .Append(Percentage(report.InferenceElapsed, encodeElapsed).ToString("0.#")).Append("% / ")
            .Append(Percentage(report.ValidationElapsed, encodeElapsed).ToString("0.#")).Append("% / ")
            .Append(Percentage(report.ReassemblyElapsed, encodeElapsed).ToString("0.#")).AppendLine("%");
        if (report.PlannerSelectedChunkSize > 0)
            builder.Append("Planner Selected / Theoretical Optimal Chunk: ").Append(report.PlannerSelectedChunkSize).Append(" / ").Append(report.TheoreticalOptimalChunkSize).AppendLine(" frames");
        if (report.Bottlenecks.Count == 0) builder.AppendLine("Bottlenecks: No material bottleneck detected from available chunk telemetry.");
        else foreach (string bottleneck in report.Bottlenecks) builder.Append("Bottleneck: ").AppendLine(bottleneck);
    }

    private void AppendNcnnRuntimeSummary(StringBuilder builder)
    {
        if (_ncnnRuntimeSelection is not NcnnRuntimeSelection selection) return;
        builder.AppendLine().AppendLine("NCNN Performance Configuration")
            .Append("Threads: ").AppendLine(selection.Configuration.ThreadsDisplay)
            .Append("Tile: ").AppendLine(selection.Configuration.TileDisplay)
            .Append("Source: ").AppendLine(selection.Source switch
            {
                NcnnRuntimeConfigurationSource.AutoTuned => "Auto-tuned",
                NcnnRuntimeConfigurationSource.Cached => "Cached",
                NcnnRuntimeConfigurationSource.BenchmarkDatabase => "Benchmark database",
                _ => "Safe default"
            });
        if (selection.BaselineFramesPerSecond is double baseline) builder.Append("Baseline FPS: ").AppendLine(baseline.ToString("0.##"));
        if (selection.SelectedFramesPerSecond is double selected) builder.Append("Selected FPS: ").AppendLine(selected.ToString("0.##"));
        if (selection.ImprovementPercent is double improvement) builder.Append("Improvement: ").Append(improvement.ToString("0.#")).AppendLine("%");
    }

    private static void Add(double? value, ref double total, ref int count, ref double? peak)
    {
        if (!value.HasValue) return;
        total += value.Value;
        count++;
        peak = Math.Max(peak ?? value.Value, value.Value);
    }
    private static void AddAverage(double? value, ref double total, ref int count)
    {
        if (!value.HasValue) return;
        total += value.Value;
        count++;
    }

    private void AppendHardwareSummary(StringBuilder builder)
    {
        if (_hardware is null) return;
        builder.AppendLine().AppendLine("Hardware Summary")
            .Append("CPU: ").Append(_hardware.Cpu).Append(" (").Append(_hardware.LogicalCores).AppendLine(" logical cores)")
            .Append("GPU: ").Append(_hardware.Gpu).Append("; Driver: ").Append(_hardware.GpuDriver).Append("; Dedicated VRAM: ").AppendLine(Bytes(_hardware.DedicatedVramBytes))
            .Append("Installed RAM: ").AppendLine(Bytes(_hardware.InstalledRamBytes))
            .Append("Drives: Source ").Append(_hardware.SourceDrive).Append("; Temp ").Append(_hardware.TempDrive).Append("; Output ").AppendLine(_hardware.OutputDrive)
            .Append("Windows: ").AppendLine(_hardware.WindowsVersion)
            .Append("FFmpeg: ").AppendLine(_hardware.FfmpegVersion);
        if (!string.IsNullOrWhiteSpace(_hardware.AiBackend)) builder.Append("AI Backend: ").AppendLine(_hardware.AiBackend);
        if (_hardwareSamples == 0) return;
        builder.Append("Samples: ").AppendLine(_hardwareSamples.ToString())
            .Append("GPU Utilization Avg/Peak: ").Append(Percent(_gpuTotal, _gpuCount)).Append(" / ").AppendLine(Percent(_gpuPeak))
            .Append("GPU VRAM Avg/Peak: ").Append(_vramCount == 0 ? "Unavailable" : Bytes(_vramTotal / _vramCount)).Append(" / ").AppendLine(Bytes(_vramPeak))
            .Append("CPU Utilization Avg/Peak: ").Append(Percent(_cpuTotal, _cpuCount)).Append(" / ").AppendLine(Percent(_cpuPeak))
            .Append("Disk Read/Write Avg: ").Append(BytesPerSecond(_readTotal, _readCount)).Append(" / ").AppendLine(BytesPerSecond(_writeTotal, _writeCount));
    }

    private static string Percent(double total, int count) => count == 0 ? "Unavailable" : $"{total / count:0.#}%";
    private static string Percent(double? value) => value.HasValue ? $"{value:0.#}%" : "Unavailable";
    private static string Bytes(long? bytes) => bytes.HasValue ? $"{bytes.Value / 1073741824d:0.##} GiB" : "Unavailable";
    private static string BytesPerSecond(double total, int count) => count == 0 ? "Unavailable" : $"{total / count / 1048576d:0.##} MiB/s";
    private static string BytesPerSecond(double? bytes) => bytes.HasValue ? $"{bytes.Value / 1048576d:0.##} MiB/s" : "Unavailable";
    private static double? Average(IEnumerable<double?> values)
    {
        double[] available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Average();
    }
    private static long? AverageLong(IEnumerable<long?> values)
    {
        long[] available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : (long)available.Average(value => (double)value);
    }
    private static long? Max(IEnumerable<long?> values)
    {
        long[] available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Max();
    }
    private static double Percentage(TimeSpan portion, TimeSpan total) => total > TimeSpan.Zero ? portion.TotalMilliseconds * 100d / total.TotalMilliseconds : 0;

    internal static double CalculateAiFramesPerSecond(long frames, TimeSpan elapsed) =>
        frames > 0 && elapsed > TimeSpan.Zero ? frames / elapsed.TotalSeconds : 0;

    internal static int CalculatePercentage(long elapsedTicks, long totalTicks) =>
        totalTicks > 0 ? (int)Math.Round(elapsedTicks * 100d / totalTicks, MidpointRounding.AwayFromZero) : 0;

    private sealed class StageTiming
    {
        public StageTiming(TimeSpan elapsed, bool completed) { Elapsed = elapsed; Completed = completed; }
        public TimeSpan Elapsed { get; set; }
        public bool Completed { get; set; }
    }

    private static readonly PerformanceTimingStage[] StageOrder =
    {
        PerformanceTimingStage.SourceAnalysis,
        PerformanceTimingStage.AiPreparation,
        PerformanceTimingStage.AiExtraction,
        PerformanceTimingStage.AiProcessing,
        PerformanceTimingStage.AiValidation,
        PerformanceTimingStage.AiReassembly,
        PerformanceTimingStage.AiIntermediateJoin,
        PerformanceTimingStage.FfmpegInitialization,
        PerformanceTimingStage.FinalEncode,
        PerformanceTimingStage.OutputValidation,
        PerformanceTimingStage.Finalization,
        PerformanceTimingStage.TemporaryFileCleanup
    };

    private static readonly IReadOnlyDictionary<PerformanceTimingStage, string> StageNames =
        new Dictionary<PerformanceTimingStage, string>
        {
            [PerformanceTimingStage.SourceAnalysis] = "Source Analysis",
            [PerformanceTimingStage.AiPreparation] = "AI Preparation",
            [PerformanceTimingStage.AiExtraction] = "AI Extraction",
            [PerformanceTimingStage.AiProcessing] = "AI Processing",
            [PerformanceTimingStage.AiValidation] = "AI Validation",
            [PerformanceTimingStage.AiReassembly] = "AI Reassembly",
            [PerformanceTimingStage.AiIntermediateJoin] = "AI Intermediate Join",
            [PerformanceTimingStage.FfmpegInitialization] = "FFmpeg Initialization",
            [PerformanceTimingStage.FinalEncode] = "Final Encode",
            [PerformanceTimingStage.OutputValidation] = "Output Validation",
            [PerformanceTimingStage.Finalization] = "Finalization",
            [PerformanceTimingStage.TemporaryFileCleanup] = "Cleanup"
        };

    public sealed class PerformanceScope : IDisposable
    {
        private readonly PerformanceTimingService _owner;
        private readonly PerformanceTimingStage _stage;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private int _disposed;
        private bool _completed;

        internal PerformanceScope(PerformanceTimingService owner, PerformanceTimingStage stage)
        { _owner = owner; _stage = stage; }

        public void Complete() => _completed = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Record(_stage, Stopwatch.GetElapsedTime(_startedAt), _completed);
        }
    }
}

/// <summary>Constant-memory hardware aggregation for a single AI chunk.</summary>
public sealed class AiChunkHardwareMetricsCollector
{
    private readonly object _gate = new();
    private double _gpuTotal, _cpuTotal, _diskTotal;
    private int _gpuCount, _cpuCount, _diskCount;
    private double? _gpuPeak;
    private long _vramTotal;
    private int _vramCount;
    private long? _vramPeak;

    public void Record(HardwareUsageSample sample)
    {
        lock (_gate)
        {
            Add(sample.GpuPercent, ref _gpuTotal, ref _gpuCount, ref _gpuPeak);
            if (sample.CpuPercent is double cpu) { _cpuTotal += cpu; _cpuCount++; }
            if (sample.VramUsedBytes is long vram) { _vramTotal += vram; _vramCount++; _vramPeak = Math.Max(_vramPeak ?? 0, vram); }
            if (sample.DiskReadBytesPerSecond.HasValue || sample.DiskWriteBytesPerSecond.HasValue)
            { _diskTotal += (sample.DiskReadBytesPerSecond ?? 0) + (sample.DiskWriteBytesPerSecond ?? 0); _diskCount++; }
        }
    }

    public AiChunkHardwareMetrics Snapshot()
    {
        lock (_gate) return new(
            _gpuCount == 0 ? null : _gpuTotal / _gpuCount,
            _gpuPeak,
            _vramCount == 0 ? null : _vramTotal / _vramCount,
            _vramPeak,
            _cpuCount == 0 ? null : _cpuTotal / _cpuCount,
            _diskCount == 0 ? null : _diskTotal / _diskCount);
    }

    private static void Add(double? value, ref double total, ref int count, ref double? peak)
    {
        if (!value.HasValue) return;
        total += value.Value; count++; peak = Math.Max(peak ?? value.Value, value.Value);
    }
}

public sealed record AiChunkHardwareMetrics(
    double? AverageGpuPercent, double? PeakGpuPercent,
    long? AverageVramUsedBytes, long? PeakVramUsedBytes,
    double? AverageCpuPercent, double? AverageDiskThroughputBytesPerSecond);

public sealed record AiChunkPerformanceMetrics(
    int ChunkNumber, int FrameCount,
    TimeSpan ExtractionElapsed, TimeSpan InferenceElapsed, TimeSpan ValidationElapsed, TimeSpan ReassemblyElapsed,
    TimeSpan TotalElapsed, AiChunkHardwareMetrics Hardware, long? MeasuredTemporaryStorageBytes = null,
    TimeSpan FfmpegProcessLaunchElapsed = default, TimeSpan StartupShutdownOverhead = default)
{
    public double EffectiveFramesPerSecond => PerformanceTimingService.CalculateAiFramesPerSecond(FrameCount, TotalElapsed);
    /// <summary>Conservative disk-wait indicator when a disk-bound chunk samples very low process I/O throughput.</summary>
    public TimeSpan? EstimatedDiskWaitElapsed => Hardware.AverageDiskThroughputBytesPerSecond is > 0 and < 20d * 1024 * 1024
        ? ExtractionElapsed + ReassemblyElapsed
        : null;
}

public enum PerformanceTimingStage
{
    SourceAnalysis,
    AiPreparation,
    AiExtraction,
    AiProcessing,
    AiValidation,
    AiReassembly,
    AiIntermediateJoin,
    FfmpegInitialization,
    FinalEncode,
    OutputValidation,
    Finalization,
    TemporaryFileCleanup
}
