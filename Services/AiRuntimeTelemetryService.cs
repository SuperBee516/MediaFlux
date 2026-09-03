using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Read-only, in-memory projection of the active AI restoration operation. Producers publish
/// existing pipeline observations; this service never samples hardware or controls execution.
/// </summary>
public sealed class AiRuntimeTelemetryService
{
    private readonly object _gate = new();
    private readonly AiBenchmarkDatabase _benchmarks;
    private AiRuntimeTelemetrySnapshot _snapshot = AiRuntimeTelemetrySnapshot.Idle;

    public static AiRuntimeTelemetryService Shared { get; } = new();

    public AiRuntimeTelemetryService(AiBenchmarkDatabase? benchmarks = null) =>
        _benchmarks = benchmarks ?? new AiBenchmarkDatabase();

    public event Action<AiRuntimeTelemetrySnapshot>? SnapshotChanged;

    public AiRuntimeTelemetrySnapshot GetSnapshot()
    {
        lock (_gate) return _snapshot;
    }

    public void Begin(AiRestorationSession session, VideoRestorationSettings settings, int totalFrames, int width, int height, HardwareSnapshot? hardware)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        (AiBenchmarkDatabaseEntry? benchmark, string benchmarkDriverVersion, DateTimeOffset? benchmarkDate) = FindBenchmark(session, settings, width, height, hardware);
        Update(_ => AiRuntimeTelemetrySnapshot.Idle with
        {
            IsActive = true,
            Status = "Preparing",
            Backend = session.Capabilities.BackendId,
            Provider = Provider(session.Capabilities.BackendId),
            Model = session.Model.BackendModelName,
            Scale = settings.AiScale,
            RuntimeVersion = session.Runtime?.RuntimeVersion ?? session.Capabilities.Identity,
            BackendReady = session.Capabilities.IsAvailable && session.Capabilities.VulkanAvailable,
            GpuName = Available(hardware?.Gpu),
            DriverVersion = Available(hardware?.GpuDriver),
            TotalVramBytes = hardware?.DedicatedVramBytes,
            DeviceIdentifier = Available(hardware?.Gpu),
            RuntimeApi = session.Capabilities.BackendId.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase) ? "TensorRT" : session.Capabilities.VulkanAvailable ? "Vulkan available" : "Unavailable",
            Precision = session.Runtime?.Precision ?? "FP32",
            EngineStatus = session.Runtime?.EngineStatus ?? "Unavailable",
            EngineCacheState = session.Runtime?.CacheState ?? "Unavailable",
            EngineBuildSource = session.Runtime?.BuildSource ?? "Unavailable",
            TotalFrames = Math.Max(0, totalFrames),
            ValidationEnabled = true,
            Benchmark = benchmark,
            BenchmarkSource = benchmark is null ? "Unavailable" : "Benchmark database",
            BenchmarkAvailable = benchmark is not null,
            ExpectedFramesPerSecond = benchmark?.FramesPerSecond,
            BenchmarkDate = benchmarkDate,
            BenchmarkDriverVersion = benchmarkDriverVersion,
            RuntimeProfile = benchmark is null ? "Unavailable" : Profile(benchmark.Configuration),
            RetuneRecommendation = "Unavailable"
        });
    }

    public void SetPlanner(AiChunkPlannerDecision planner) => Update(snapshot => snapshot with
    {
        ChunkSize = planner.FinalSelectedChunkSize,
        PlannerResult = planner.DecisionReason
    });

    public void SetRuntime(NcnnRuntimeSelection selection) => Update(snapshot => snapshot with
    {
        Threads = selection.Configuration.ThreadsDisplay,
        TileSize = selection.Configuration.TileDisplay,
        RuntimeTuningState = selection.Source switch
        {
            NcnnRuntimeConfigurationSource.AutoTuned => "Auto-tuned",
            NcnnRuntimeConfigurationSource.Cached => "Cached",
            NcnnRuntimeConfigurationSource.BenchmarkDatabase => "Benchmark database",
            _ => "Safe default"
        },
        CacheSource = selection.Source switch
        {
            NcnnRuntimeConfigurationSource.Cached => "Tuning cache",
            NcnnRuntimeConfigurationSource.BenchmarkDatabase => "Benchmark database",
            NcnnRuntimeConfigurationSource.AutoTuned => "New auto-tune",
            _ => "Unavailable"
        },
        BenchmarkSource = selection.Source == NcnnRuntimeConfigurationSource.BenchmarkDatabase ? "Benchmark database" : snapshot.BenchmarkSource,
        RuntimeProfile = Profile(selection.Configuration)
    });

    public void SetRuntime(AiBackendRuntimeDescriptor runtime) => Update(snapshot => snapshot with
    {
        RuntimeVersion = runtime.RuntimeVersion,
        Precision = runtime.Precision,
        EngineStatus = runtime.EngineStatus,
        EngineCacheState = runtime.CacheState,
        EngineBuildSource = runtime.BuildSource,
        Threads = "Unavailable",
        TileSize = "Dynamic",
        RuntimeTuningState = runtime.CacheState == "Reused" ? "Cached engine" : "Engine built",
        CacheSource = runtime.CacheState == "Reused" ? "TensorRT engine cache" : "New TensorRT engine",
        RuntimeProfile = $"{runtime.Precision}; dynamic shapes; {runtime.CacheState}"
    });

    public void ReportProgress(AiIntermediateProgress progress) => Update(snapshot =>
    {
        double? average = progress.AverageAiFramesPerSecond ?? snapshot.AverageFramesPerSecond;
        double? efficiency = average is > 0 && snapshot.ExpectedFramesPerSecond is > 0
            ? average.Value * 100d / snapshot.ExpectedFramesPerSecond.Value : null;
        return snapshot with
        {
            Status = progress.Stage.ToString(),
            FramesProcessed = Math.Max(snapshot.FramesProcessed, progress.Current),
            TotalFrames = Math.Max(snapshot.TotalFrames, progress.Total),
            CurrentFramesPerSecond = progress.CurrentAiFramesPerSecond,
            AverageFramesPerSecond = average,
            EstimatedRemaining = progress.EstimatedRemaining,
            ThroughputEfficiencyPercent = efficiency
        };
    });

    public void RecordHardwareSample(HardwareUsageSample sample) => Update(snapshot => snapshot with
    {
        // Existing NVIDIA telemetry exposes VRAM used, not free/available VRAM. Do not
        // relabel it: the dashboard must prefer an explicit Unavailable value to a lie.
        PeakVramBytes = sample.VramUsedBytes is long used ? Math.Max(snapshot.PeakVramBytes ?? 0, used) : snapshot.PeakVramBytes,
        GpuUtilizationPercent = sample.GpuPercent,
        CpuUtilizationPercent = sample.CpuPercent
    });

    public void Complete() => Update(_ => AiRuntimeTelemetrySnapshot.Idle);
    public void Fail(string status = "Failed") => Update(snapshot => snapshot with { IsActive = false, Status = status });
    public void SwitchBackend(AiRestorationSession session, string status) => Update(snapshot => snapshot with
    {
        Backend = session.Capabilities.BackendId,
        Provider = Provider(session.Capabilities.BackendId),
        Model = session.Model.BackendModelName,
        RuntimeVersion = session.Runtime?.RuntimeVersion ?? session.Capabilities.Identity,
        Precision = session.Runtime?.Precision ?? "FP32",
        EngineStatus = session.Runtime?.EngineStatus ?? "Unavailable",
        EngineCacheState = session.Runtime?.CacheState ?? "Unavailable",
        EngineBuildSource = session.Runtime?.BuildSource ?? "Unavailable",
        Status = status
    });

    private (AiBenchmarkDatabaseEntry? Entry, string DriverVersion, DateTimeOffset? Date) FindBenchmark(AiRestorationSession session, VideoRestorationSettings settings, int width, int height, HardwareSnapshot? hardware)
    {
        string gpu = Available(hardware?.Gpu), driver = Available(hardware?.GpuDriver);
        if (gpu == "Unavailable" || driver == "Unavailable") return (null, "Unavailable", null);
        var key = new AiBenchmarkDatabaseKey(session.Capabilities.BackendId, session.Capabilities.Identity, session.Model.BackendModelName, gpu, driver, session.Runtime?.Precision ?? "FP32", (int)settings.AiScale, ResolutionClass(width, height));
        if (_benchmarks.TryGetFastestStable(key, out AiBenchmarkDatabaseEntry entry)) return (entry, entry.Key.DriverVersion, entry.Timestamp);
        return _benchmarks.TryGetLatestStableWithDifferentDriver(key, out AiBenchmarkDatabaseEntry prior) ? (null, prior.Key.DriverVersion, prior.Timestamp) : (null, "Unavailable", null);
    }

    private void Update(Func<AiRuntimeTelemetrySnapshot, AiRuntimeTelemetrySnapshot> mutation)
    {
        AiRuntimeTelemetrySnapshot snapshot;
        lock (_gate) { _snapshot = mutation(_snapshot) with { UpdatedAt = DateTimeOffset.UtcNow }; snapshot = _snapshot; }
        try { SnapshotChanged?.Invoke(snapshot); } catch { /* Observers must not affect restoration. */ }
    }

    private static string Provider(string backend) => backend.Equals("ncnn-vulkan", StringComparison.OrdinalIgnoreCase) ? "NCNN" : backend;
    private static string Available(string? value) => string.IsNullOrWhiteSpace(value) || value.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) ? "Unavailable" : value;
    private static string Profile(NcnnRuntimeConfiguration configuration) => $"Threads {configuration.ThreadsDisplay}; Tile {configuration.TileDisplay}; FP32";
    private static string ResolutionClass(int width, int height) => width <= 0 || height <= 0 ? "unknown" : Math.Max(width, height) >= 3840 ? "4k" : Math.Max(width, height) >= 1920 ? "1080p" : "sd";
}

public sealed record AiRuntimeTelemetrySnapshot(
    bool IsActive, string Status, string Backend, string Provider, string Model, AiRestorationScale Scale,
    string RuntimeVersion, bool BackendReady, string GpuName, string DriverVersion, long? TotalVramBytes,
    long? AvailableVramBytes, long? PeakVramBytes, string RuntimeApi, string DeviceIdentifier, string Threads,
    string TileSize, string Precision, int? ChunkSize, string PlannerResult, string RuntimeTuningState, string CacheSource,
    string BenchmarkSource, AiBenchmarkDatabaseEntry? Benchmark, bool BenchmarkAvailable, DateTimeOffset? BenchmarkDate, string BenchmarkDriverVersion,
    string RuntimeProfile, string RetuneRecommendation, bool ValidationEnabled, string EngineStatus, string EngineCacheState, string EngineBuildSource, int FramesProcessed, int TotalFrames,
    double? CurrentFramesPerSecond, double? AverageFramesPerSecond, double? ExpectedFramesPerSecond,
    TimeSpan? EstimatedRemaining, double? GpuUtilizationPercent, double? CpuUtilizationPercent,
    double? ThroughputEfficiencyPercent, DateTimeOffset UpdatedAt)
{
    public static AiRuntimeTelemetrySnapshot Idle { get; } = new(false, "Idle", "Unavailable", "Unavailable", "Unavailable", AiRestorationScale.X1,
        "Unavailable", false, "Unavailable", "Unavailable", null, null, null, "Unavailable", "Unavailable",
        "Unavailable", "Unavailable", "Unavailable", null, "Unavailable", "Unavailable", "Unavailable", "Unavailable", null,
        false, null, "Unavailable", "Unavailable", "Unavailable", false, "Unavailable", "Unavailable", "Unavailable", 0, 0, null, null, null, null, null, null, null, DateTimeOffset.MinValue);
}
