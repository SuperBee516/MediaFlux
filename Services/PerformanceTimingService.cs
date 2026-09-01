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
        {
            _aiFrames += frameCount;
            _aiChunks++;
            _aiChunkElapsed += elapsed;
            if (elapsed < _fastestAiChunk) _fastestAiChunk = elapsed;
            if (elapsed > _slowestAiChunk) _slowestAiChunk = elapsed;
        }
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
