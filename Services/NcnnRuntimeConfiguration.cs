namespace MediaFlux.Services;

/// <summary>Validated NCNN command-line runtime options. Null values preserve backend defaults.</summary>
public sealed record NcnnRuntimeConfiguration(NcnnThreadConfiguration? Threads = null, int? TileSize = null)
{
    public static NcnnRuntimeConfiguration SafeDefault { get; } = new();

    public bool UsesBackendDefaults => Threads is null && TileSize is null;
    public string ThreadsDisplay => Threads?.ToString() ?? "Backend default";
    public string TileDisplay => TileSize?.ToString() ?? "Auto";

    public void Validate()
    {
        if (TileSize is not null and (< 32 or > 2048))
            throw new ArgumentOutOfRangeException(nameof(TileSize), "NCNN tile size must be between 32 and 2048.");
    }
}

public sealed record NcnnThreadConfiguration(int Load, int Process, int Save)
{
    public static NcnnThreadConfiguration OneTwoTwo { get; } = new(1, 2, 2);
    public static NcnnThreadConfiguration TwoTwoTwo { get; } = new(2, 2, 2);
    public static NcnnThreadConfiguration FourFourFour { get; } = new(4, 4, 4);

    public override string ToString() => $"{Load}:{Process}:{Save}";

    public void Validate()
    {
        if (Load is < 1 or > 16 || Process is < 1 or > 16 || Save is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(Load), "NCNN thread counts must be between 1 and 16.");
    }
}

public enum NcnnRuntimeConfigurationSource { SafeDefault, Cached, AutoTuned }

public sealed record NcnnRuntimeSelection(
    NcnnRuntimeConfiguration Configuration,
    NcnnRuntimeConfigurationSource Source,
    double? BaselineFramesPerSecond = null,
    double? SelectedFramesPerSecond = null,
    string? CacheKey = null)
{
    public double? ImprovementPercent => BaselineFramesPerSecond is > 0 && SelectedFramesPerSecond is double selected
        ? ((selected / BaselineFramesPerSecond.Value) - 1) * 100d
        : null;
}

public sealed record NcnnTuningBenchmarkResult(
    NcnnRuntimeConfiguration Configuration,
    double FramesPerSecond,
    TimeSpan Elapsed,
    long? PeakVramBytes,
    double? AverageGpuPercent,
    bool IsValid,
    string Result);
