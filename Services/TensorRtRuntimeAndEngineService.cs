using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

public enum TensorRtPrecision { FP32, FP16, INT8 }

public sealed record TensorRtGpuInfo(string Name, string DriverVersion, string? ComputeCapability)
{
    public static TensorRtGpuInfo Unavailable { get; } = new("Unavailable", "Unavailable", null);
}

public sealed record TensorRtRuntimeInfo(
    bool NvidiaGpuDetected,
    bool CudaRuntimeDetected,
    bool TensorRtRuntimeDetected,
    bool TensorRtPluginsDetected,
    string CudaVersion,
    string TensorRtVersion,
    TensorRtGpuInfo Gpu,
    IReadOnlyList<TensorRtPrecision> SupportedPrecisions,
    IReadOnlyList<string> RuntimeDirectories,
    IReadOnlyList<string> Diagnostics,
    string? Reason)
{
    public bool IsReady => NvidiaGpuDetected && CudaRuntimeDetected && TensorRtRuntimeDetected && TensorRtPluginsDetected;
}

/// <summary>Discovery and validation data for TensorRT only. It neither loads a TensorRT runtime nor executes an engine.</summary>
public sealed class TensorRtRuntimeService
{
    private readonly string _applicationDirectory;
    private readonly Func<bool> _nvidiaGpuPresent;
    private readonly Func<IEnumerable<string>> _runtimeDirectories;
    private readonly Func<TensorRtGpuInfo> _gpuInfo;

    public TensorRtRuntimeService(string applicationDirectory, Func<bool>? nvidiaGpuPresent = null, Func<IEnumerable<string>>? runtimeDirectories = null, Func<TensorRtGpuInfo>? gpuInfo = null)
    {
        _applicationDirectory = applicationDirectory;
        _nvidiaGpuPresent = nvidiaGpuPresent ?? (() => !HardwarePerformanceService.DetectGpuIdentity().Equals("Unavailable", StringComparison.OrdinalIgnoreCase));
        _runtimeDirectories = runtimeDirectories ?? DefaultRuntimeDirectories;
        _gpuInfo = gpuInfo ?? QueryGpu;
    }

    public Task<TensorRtRuntimeInfo> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] directories = _runtimeDirectories().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool gpu = _nvidiaGpuPresent();
        TensorRtGpuInfo gpuInfo = gpu ? _gpuInfo() : TensorRtGpuInfo.Unavailable;
        string? cudaPath = FindLibrary(directories, "cudart64*.dll");
        string? tensorRtPath = FindLibrary(directories, "nvinfer.dll");
        string? pluginPath = FindLibrary(directories, "nvinfer_plugin.dll");
        bool cuda = cudaPath is not null, tensorRt = tensorRtPath is not null, plugins = pluginPath is not null;
        IReadOnlyList<TensorRtPrecision> precision = SupportedPrecisions(gpuInfo.ComputeCapability, gpu && cuda && tensorRt);
        string reason = !gpu ? "NVIDIA GPU not detected." : !cuda ? "CUDA runtime libraries are missing." : !tensorRt || !plugins ? "Required TensorRT runtime libraries are missing." : "TensorRT runtime discovered.";
        var diagnostics = new[]
        {
            $"CUDA runtime: {(cuda ? "Detected (" + Version(cudaPath!) + ")" : "Not installed")}",
            $"TensorRT runtime: {(tensorRt ? "Detected (" + Version(tensorRtPath!) + ")" : "Not installed")}",
            $"TensorRT plugins: {(plugins ? "Detected" : "Not installed")}",
            $"GPU: {gpuInfo.Name}",
            $"Compute capability: {gpuInfo.ComputeCapability ?? "Unavailable"}",
            "Supported precision: " + (precision.Count == 0 ? "Unavailable" : string.Join(", ", precision))
        };
        return Task.FromResult(new TensorRtRuntimeInfo(gpu, cuda, tensorRt, plugins, cuda ? Version(cudaPath!) : "Unavailable", tensorRt ? Version(tensorRtPath!) : "Unavailable", gpuInfo, precision, directories, diagnostics, reason));
    }

    private IEnumerable<string> DefaultRuntimeDirectories()
    {
        yield return _applicationDirectory;
        foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) yield return part;
        string? cuda = Environment.GetEnvironmentVariable("CUDA_PATH"); if (!string.IsNullOrWhiteSpace(cuda)) yield return Path.Combine(cuda, "bin");
    }
    private static string? FindLibrary(IEnumerable<string> directories, string pattern) => directories.SelectMany(directory => SafeEnumerate(directory, pattern)).FirstOrDefault();
    private static IEnumerable<string> SafeEnumerate(string directory, string pattern) { try { return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray(); } catch { return Array.Empty<string>(); } }
    private static string Version(string path)
    {
        try
        {
            string? version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }
        catch { }
        string digits = new string(Path.GetFileNameWithoutExtension(path).Where(character => char.IsDigit(character) || character == '.' || character == '_').ToArray()).Replace('_', '.');
        return string.IsNullOrWhiteSpace(digits) ? "Unknown" : digits.Trim('.');
    }
    private static IReadOnlyList<TensorRtPrecision> SupportedPrecisions(string? capability, bool runtime)
    {
        if (!runtime || !TryCapability(capability, out double value)) return Array.Empty<TensorRtPrecision>();
        var supported = new List<TensorRtPrecision> { TensorRtPrecision.FP32 };
        if (value >= 5.3) supported.Add(TensorRtPrecision.FP16);
        if (value >= 6.1) supported.Add(TensorRtPrecision.INT8);
        return supported;
    }
    internal static bool TryCapability(string? value, out double capability) => double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out capability);
    private static TensorRtGpuInfo QueryGpu()
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "nvidia-smi.exe", Arguments = "--query-gpu=name,driver_version,compute_cap --format=csv,noheader,nounits", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
            if (!process.Start() || !process.WaitForExit(1000) || process.ExitCode != 0) return TensorRtGpuInfo.Unavailable;
            string[] values = (process.StandardOutput.ReadLine() ?? "").Split(',').Select(value => value.Trim()).ToArray();
            return new(values.ElementAtOrDefault(0) ?? "Unavailable", values.ElementAtOrDefault(1) ?? "Unavailable", values.ElementAtOrDefault(2));
        }
        catch { return TensorRtGpuInfo.Unavailable; }
    }
}

public sealed record TensorRtEngineIdentity(string Model, AiRestorationScale Scale, TensorRtPrecision Precision, string TensorRtVersion, string CudaVersion, int SchemaVersion = TensorRtEngineManager.EngineSchemaVersion);
public sealed record TensorRtEngineMetadata(TensorRtEngineIdentity Identity, DateTimeOffset CreatedUtc, string MinimumComputeCapability, string? MaximumComputeCapability = null);
public sealed record TensorRtEngineValidationResult(string EnginePath, TensorRtEngineMetadata? Metadata, bool IsValid, string Reason)
{
    public static TensorRtEngineValidationResult Invalid(string path, string reason) => new(path, null, false, reason);
}
public sealed record TensorRtEngineDiscoveryItem(string EnginePath, TensorRtEngineMetadata? Metadata, TensorRtEngineValidationResult Validation);

/// <summary>Metadata-only loaded-engine representation. It deliberately contains no TensorRT execution object.</summary>
public sealed class TensorRtLoadedEngine : IDisposable
{
    internal TensorRtLoadedEngine(TensorRtEngineValidationResult validation) { Validation = validation; }
    public TensorRtEngineValidationResult Validation { get; }
    public bool IsDisposed { get; private set; }
    public void Dispose() { IsDisposed = true; }
}

public sealed class TensorRtEngineLease : IDisposable
{
    private readonly TensorRtEngineCache _cache;
    private readonly string _key;
    private int _disposed;
    internal TensorRtEngineLease(TensorRtEngineCache cache, string key, TensorRtLoadedEngine engine) { _cache = cache; _key = key; Engine = engine; }
    public TensorRtLoadedEngine Engine { get; }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _cache.Release(_key); }
}

/// <summary>Thread-safe, lazy metadata cache with lease-aware idle eviction and deterministic disposal.</summary>
public sealed class TensorRtEngineCache : IDisposable
{
    private sealed class Entry
    {
        public Entry(TensorRtEngineValidationResult validation) { Loader = new(() => new TensorRtLoadedEngine(validation), LazyThreadSafetyMode.ExecutionAndPublication); LastUsedUtc = DateTimeOffset.UtcNow; }
        public Lazy<TensorRtLoadedEngine> Loader { get; }
        public int Leases;
        public DateTimeOffset LastUsedUtc;
    }
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _idleExpiration;
    private int _disposed;
    public TensorRtEngineCache(TimeSpan? idleExpiration = null) { _idleExpiration = idleExpiration ?? TimeSpan.FromMinutes(10); }
    public TensorRtEngineLease Acquire(TensorRtEngineValidationResult validation)
    {
        if (!validation.IsValid) throw new AiRestorationValidationException(validation.Reason);
        ThrowIfDisposed(); string key = CacheKey(validation);
        Entry entry = _entries.GetOrAdd(key, _ => new Entry(validation));
        lock (entry) { ThrowIfDisposed(); entry.Leases++; entry.LastUsedUtc = DateTimeOffset.UtcNow; return new(this, key, entry.Loader.Value); }
    }
    internal void Release(string key) { if (_entries.TryGetValue(key, out Entry? entry)) lock (entry) { entry.Leases = Math.Max(0, entry.Leases - 1); entry.LastUsedUtc = DateTimeOffset.UtcNow; } }
    public int UnloadIdle(DateTimeOffset? now = null)
    {
        DateTimeOffset cutoff = (now ?? DateTimeOffset.UtcNow) - _idleExpiration; int removed = 0;
        foreach ((string key, Entry entry) in _entries)
            lock (entry)
                if (entry.Leases == 0 && entry.LastUsedUtc <= cutoff && _entries.TryRemove(key, out Entry? evicted)) { if (evicted.Loader.IsValueCreated) evicted.Loader.Value.Dispose(); removed++; }
        return removed;
    }
    public int Count => _entries.Count;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (KeyValuePair<string, Entry> pair in _entries)
            if (_entries.TryRemove(pair.Key, out Entry? removed) && removed.Loader.IsValueCreated)
                removed.Loader.Value.Dispose();
    }
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(TensorRtEngineCache)); }
    private static string CacheKey(TensorRtEngineValidationResult validation)
    {
        TensorRtEngineIdentity identity = validation.Metadata!.Identity;
        return string.Join("|", Path.GetFullPath(validation.EnginePath), identity.Model, identity.Scale, identity.Precision, identity.TensorRtVersion, identity.CudaVersion, identity.SchemaVersion);
    }
}

/// <summary>Discovers, persists, validates, and caches TensorRT engine metadata without loading TensorRT or running inference.</summary>
public sealed class TensorRtEngineManager : IDisposable
{
    public const int EngineSchemaVersion = 1;
    private readonly string _engineDirectory;
    private readonly TensorRtRuntimeService _runtime;
    private readonly TensorRtEngineCache _cache;
    private readonly Action<string>? _log;
    public TensorRtEngineManager(string engineDirectory, TensorRtRuntimeService runtime, TimeSpan? idleExpiration = null, Action<string>? log = null) { _engineDirectory = engineDirectory; _runtime = runtime; _cache = new(idleExpiration); _log = log; }
    public async Task<IReadOnlyList<TensorRtEngineDiscoveryItem>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_engineDirectory)) return Array.Empty<TensorRtEngineDiscoveryItem>();
        TensorRtRuntimeInfo runtime = await _runtime.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<TensorRtEngineDiscoveryItem>();
        foreach (string path in Directory.EnumerateFiles(_engineDirectory, "*.engine", SearchOption.TopDirectoryOnly))
        {
            TensorRtEngineMetadata? metadata = await LoadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            TensorRtEngineValidationResult validation = Validate(path, metadata, runtime);
            results.Add(new(path, metadata, validation));
        }
        return results;
    }
    public async Task<TensorRtEngineValidationResult> ValidateAsync(string path, TensorRtEngineIdentity? expected = null, CancellationToken cancellationToken = default)
    {
        TensorRtRuntimeInfo runtime = await _runtime.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        TensorRtEngineMetadata? metadata = await LoadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        TensorRtEngineValidationResult result = Validate(path, metadata, runtime, expected);
        _log?.Invoke($"[TensorRT Engine] validation; engine={Path.GetFileName(path)}; valid={result.IsValid}; reason={result.Reason}");
        return result;
    }
    public async Task<TensorRtEngineLease> AcquireAsync(string path, TensorRtEngineIdentity? expected = null, CancellationToken cancellationToken = default) => _cache.Acquire(await ValidateAsync(path, expected, cancellationToken).ConfigureAwait(false));
    public int UnloadIdleEngines(DateTimeOffset? now = null) => _cache.UnloadIdle(now);
    public int CachedEngineCount => _cache.Count;
    public async Task<TensorRtEngineMetadata?> LoadMetadataAsync(string enginePath, CancellationToken cancellationToken = default)
    {
        string path = MetadataPath(enginePath); if (!File.Exists(path)) return null;
        try { await using FileStream stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<TensorRtEngineMetadata>(stream, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { return null; }
    }
    public async Task SaveMetadataAsync(string enginePath, TensorRtEngineMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (metadata.Identity.SchemaVersion != EngineSchemaVersion) throw new AiRestorationValidationException("Unsupported TensorRT engine metadata schema.");
        Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath(enginePath))!);
        string staging = MetadataPath(enginePath) + ".staging";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        File.Move(staging, MetadataPath(enginePath), true);
    }
    public static string MetadataPath(string enginePath) => enginePath + ".mediaflux.json";
    internal static TensorRtEngineValidationResult Validate(string path, TensorRtEngineMetadata? metadata, TensorRtRuntimeInfo runtime, TensorRtEngineIdentity? expected = null)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return TensorRtEngineValidationResult.Invalid(path, "Engine file is missing or unreadable.");
        if (metadata is null) return TensorRtEngineValidationResult.Invalid(path, "Engine metadata is missing or invalid.");
        TensorRtEngineIdentity identity = metadata.Identity;
        if (identity.SchemaVersion != EngineSchemaVersion) return TensorRtEngineValidationResult.Invalid(path, "Engine schema version is unsupported.");
        if (!runtime.IsReady) return TensorRtEngineValidationResult.Invalid(path, runtime.Reason ?? "TensorRT runtime is unavailable.");
        if (!VersionsMatch(identity.TensorRtVersion, runtime.TensorRtVersion)) return TensorRtEngineValidationResult.Invalid(path, $"TensorRT version mismatch: engine={identity.TensorRtVersion}; runtime={runtime.TensorRtVersion}.");
        if (!VersionsMatch(identity.CudaVersion, runtime.CudaVersion)) return TensorRtEngineValidationResult.Invalid(path, $"CUDA version mismatch: engine={identity.CudaVersion}; runtime={runtime.CudaVersion}.");
        if (!runtime.SupportedPrecisions.Contains(identity.Precision)) return TensorRtEngineValidationResult.Invalid(path, $"GPU does not support required {identity.Precision} precision.");
        if (!CompatibleCapability(runtime.Gpu.ComputeCapability, metadata.MinimumComputeCapability, metadata.MaximumComputeCapability)) return TensorRtEngineValidationResult.Invalid(path, "GPU compute capability is incompatible with this engine.");
        if (expected is not null && (!identity.Model.Equals(expected.Model, StringComparison.OrdinalIgnoreCase) || identity.Scale != expected.Scale || identity.Precision != expected.Precision)) return TensorRtEngineValidationResult.Invalid(path, "Engine model, scale, or precision does not match the expected identity.");
        return new(path, metadata, true, "Validated.");
    }
    private static bool VersionsMatch(string engine, string runtime) => !string.IsNullOrWhiteSpace(engine) && !string.IsNullOrWhiteSpace(runtime) && engine.Equals(runtime, StringComparison.OrdinalIgnoreCase);
    private static bool CompatibleCapability(string? actual, string minimum, string? maximum)
    {
        if (!TensorRtRuntimeService.TryCapability(actual, out double gpu) || !TensorRtRuntimeService.TryCapability(minimum, out double min)) return false;
        return gpu >= min && (!TensorRtRuntimeService.TryCapability(maximum, out double max) || gpu <= max);
    }
    public void Dispose() => _cache.Dispose();
}
