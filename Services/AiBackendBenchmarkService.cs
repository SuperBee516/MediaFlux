using System.Diagnostics;
using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record AiBenchmarkResourceUsage(
    double? AverageGpuPercent,
    double? PeakGpuPercent,
    long? AverageVramBytes,
    long? PeakVramBytes,
    double? AverageCpuPercent,
    double? AverageDiskBytesPerSecond,
    long TemporaryStorageBytes);

public sealed record AiBenchmarkValidationResult(bool IsValid, string Summary);

public sealed record AiBackendBenchmarkResult(
    DateTimeOffset Date,
    string BackendId,
    string BackendName,
    string BackendVersion,
    string GpuIdentity,
    string GpuDriver,
    string Model,
    AiRestorationScale Scale,
    string ResolutionClass,
    int Width,
    int Height,
    int FrameCount,
    TimeSpan Elapsed,
    double EffectiveFramesPerSecond,
    AiBenchmarkResourceUsage Resources,
    AiBenchmarkValidationResult Validation,
    IReadOnlyList<string> BackendDiagnostics)
{
    public bool IsSuccessfulValidated => Validation.IsValid;
}

/// <summary>Bounded benchmark input. Frames must originate from the existing preview extraction path; this service never opens an entire video.</summary>
public sealed record AiBackendBenchmarkRequest(
    IAiRestorationBackend Backend,
    VideoRestorationSettings Settings,
    IReadOnlyList<string> PreviewFrames,
    int SourceWidth,
    int SourceHeight,
    int RequestedFrameCount = AiBackendBenchmarkService.DefaultFrameCount,
    string SourceDescription = "preview sample",
    ProviderManager? ProviderManager = null,
    string? ProviderId = null);

public sealed record AiBackendBenchmarkComparisonItem(AiBackendBenchmarkResult Result, bool IsWinner);
public sealed record AiBackendBenchmarkComparison(IReadOnlyList<AiBackendBenchmarkComparisonItem> Results, AiBackendBenchmarkResult? Winner);
public sealed record AiBackendBenchmarkRecommendation(AiBackendBenchmarkResult Result, string Reason);

/// <summary>
/// Runs bounded frame benchmarks through the common backend contract. It intentionally owns
/// neither extraction, planning, backend selection, nor any encode behavior.
/// </summary>
public sealed class AiBackendBenchmarkService
{
    public const int DefaultFrameCount = 120;
    public const int MaximumFrameCount = 240;
    private readonly string _stagingRoot;
    private readonly Func<HardwareUsageSample> _sampleResources;
    private readonly Func<(string Gpu, string Driver)> _gpuInfo;
    private readonly AiBackendBenchmarkHistoryStore _history;
    private readonly AiBenchmarkDatabase _database;
    private readonly Action<string>? _log;

    public AiBackendBenchmarkService(string stagingRoot, Action<string>? log = null, Func<HardwareUsageSample>? sampleResources = null, Func<(string Gpu, string Driver)>? gpuInfo = null, AiBackendBenchmarkHistoryStore? history = null, AiBenchmarkDatabase? database = null)
    {
        _stagingRoot = stagingRoot;
        _log = log;
        _sampleResources = sampleResources ?? (() => { using var hardware = new HardwarePerformanceService(); return hardware.Sample(); });
        _gpuInfo = gpuInfo ?? (() => { HardwareSnapshot snapshot = HardwarePerformanceService.Capture("", "", "", ""); return (snapshot.Gpu, snapshot.GpuDriver); });
        _history = history ?? new AiBackendBenchmarkHistoryStore(Path.Combine(AppPaths.DataDirectory, "ai-benchmark-history.json"));
        _database = database ?? new AiBenchmarkDatabase();
    }

    public async Task<AiBackendBenchmarkResult> RunAsync(AiBackendBenchmarkRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ProviderManager manager = request.ProviderManager ?? new ProviderManager(new[] { new NcnnAiProvider(request.Backend, request.Settings) }, _log);
        AiProviderHealth providerHealth = await manager.InitializeAsync(request.ProviderId ?? "ncnn-vulkan", new(AiProviderSdk.CurrentVersion), requireImageProcessing: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!providerHealth.IsReady) throw new AiRestorationValidationException(providerHealth.Reason ?? "AI provider is unavailable for benchmarking.");
        IReadOnlyList<string> frames = request.PreviewFrames.Take(request.RequestedFrameCount).ToArray();
        AiBackendMetadata metadata = await request.Backend.GetMetadataAsync(request.Settings, cancellationToken).ConfigureAwait(false);
        if (!metadata.IsReady)
            throw new AiRestorationValidationException(metadata.Reason ?? $"{metadata.DisplayName} is unavailable for benchmarking.");
        AiRestorationSession session = await request.Backend.CreateSessionAsync(request.Settings, cancellationToken).ConfigureAwait(false);
        (string gpu, string driver) = _gpuInfo();
        string root = Path.Combine(_stagingRoot, "ai-benchmark-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input"), output = Path.Combine(root, "output");
        Directory.CreateDirectory(input); Directory.CreateDirectory(output);
        string[] copied = frames.Select((frame, index) => Path.Combine(input, $"frame-{index:D8}.png")).ToArray();
        string[] expected = AiRestorationIntermediateVideoService.ExpectedFrames(output, copied.Length);
        foreach ((string source, string destination) in frames.Zip(copied)) File.Copy(source, destination, overwrite: true);

        _log?.Invoke($"[AI Benchmark] started; backend={metadata.DisplayName}; model={session.Model.BackendModelName}; scale={(int)request.Settings.AiScale}x; frames={frames.Count}; resolution={request.SourceWidth}x{request.SourceHeight}; source={request.SourceDescription}.");
        var samples = new List<HardwareUsageSample> { _sampleResources() };
        var samplesGate = new object();
        using var timer = new System.Threading.Timer(_ => { try { lock (samplesGate) samples.Add(_sampleResources()); } catch { } }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Stopwatch stopwatch = Stopwatch.StartNew();
        AiBenchmarkValidationResult validation;
        try
        {
            await request.Backend.ProcessDirectoryAsync(session, request.Settings, input, output, expected, null, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            validation = Validate(copied, expected, request.Settings.AiScale);
        }
        catch (OperationCanceledException) { try { Directory.Delete(root, true); } catch { } throw; }
        catch (Exception ex)
        {
            stopwatch.Stop();
            validation = new(false, ex.Message);
        }
        finally { lock (samplesGate) samples.Add(_sampleResources()); timer.Dispose(); }

        long temporaryStorage = MeasureDirectory(root);
        AiBenchmarkResourceUsage resources;
        lock (samplesGate) resources = Summarize(samples.ToArray(), temporaryStorage);
        var result = new AiBackendBenchmarkResult(DateTimeOffset.UtcNow, metadata.Id, metadata.DisplayName, metadata.Version, gpu, driver,
            session.Model.BackendModelName, request.Settings.AiScale, ResolutionClass(request.SourceWidth, request.SourceHeight), request.SourceWidth, request.SourceHeight,
            frames.Count, stopwatch.Elapsed, frames.Count / Math.Max(stopwatch.Elapsed.TotalSeconds, .001), resources, validation, metadata.Diagnostics);
        _database.Store(new AiBenchmarkDatabaseEntry(
            new(metadata.Id, session.Capabilities.Identity, session.Model.BackendModelName, gpu, driver, "FP32", (int)request.Settings.AiScale, result.ResolutionClass),
            NcnnRuntimeConfiguration.SafeDefault,
            result.EffectiveFramesPerSecond,
            result.Resources.PeakVramBytes,
            result.Validation.IsValid,
            result.Date,
            result.Validation.Summary));
        _log?.Invoke($"[AI Benchmark] completed; backend={result.BackendName}; elapsed={result.Elapsed:g}; fps={result.EffectiveFramesPerSecond:0.##}; validation={(result.Validation.IsValid ? "passed" : "failed")}; {result.Validation.Summary}");
        await _history.AppendAsync(result, cancellationToken).ConfigureAwait(false);
        try { Directory.Delete(root, true); } catch { }
        if (request.ProviderManager is null) await manager.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    public static AiBackendBenchmarkComparison Compare(IEnumerable<AiBackendBenchmarkResult> results)
    {
        AiBackendBenchmarkResult[] valid = results.Where(result => result.IsSuccessfulValidated).OrderByDescending(result => result.EffectiveFramesPerSecond).ThenBy(result => result.Elapsed).ToArray();
        AiBackendBenchmarkResult? winner = valid.FirstOrDefault();
        return new(valid.Select(result => new AiBackendBenchmarkComparisonItem(result, ReferenceEquals(result, winner))).ToArray(), winner);
    }

    public static AiBackendBenchmarkRecommendation? FindFastestValidated(IEnumerable<AiBackendBenchmarkResult> history, string gpuIdentity, string resolutionClass, string model, AiRestorationScale scale)
    {
        AiBackendBenchmarkResult? result = Compare(history.Where(item => item.GpuIdentity.Equals(gpuIdentity, StringComparison.OrdinalIgnoreCase) && item.ResolutionClass.Equals(resolutionClass, StringComparison.OrdinalIgnoreCase) && item.Model.Equals(model, StringComparison.OrdinalIgnoreCase) && item.Scale == scale)).Winner;
        return result is null ? null : new(result, $"Fastest validated result: {result.BackendName} at {result.EffectiveFramesPerSecond:0.##} FPS.");
    }

    internal static string ResolutionClass(int width, int height) => NcnnPerformanceAutoTuner.ResolutionClass(width, height);
    private static void ValidateRequest(AiBackendBenchmarkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestedFrameCount is < 1 or > MaximumFrameCount) throw new ArgumentOutOfRangeException(nameof(request.RequestedFrameCount), $"AI benchmarks are limited to {MaximumFrameCount} preview frames.");
        if (request.PreviewFrames.Count < request.RequestedFrameCount) throw new AiRestorationValidationException("The selected preview sample does not contain the requested benchmark frame count.");
        if (request.SourceWidth <= 0 || request.SourceHeight <= 0 || request.PreviewFrames.Take(request.RequestedFrameCount).Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path))) throw new AiRestorationValidationException("AI benchmarks require existing absolute preview frame paths and a known resolution.");
    }
    private static AiBenchmarkValidationResult Validate(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, AiRestorationScale scale)
    {
        try { AiRestorationIntermediateVideoService.ValidateRestoredFrameSet(inputs, outputs, scale); return new(true, "Output frame set and dimensions validated."); }
        catch (Exception ex) { return new(false, ex.Message); }
    }
    private static AiBenchmarkResourceUsage Summarize(IReadOnlyList<HardwareUsageSample> samples, long temporaryStorage) => new(
        Average(samples.Select(sample => sample.GpuPercent)), Max(samples.Select(sample => sample.GpuPercent)),
        AverageLong(samples.Select(sample => sample.VramUsedBytes)), MaxLong(samples.Select(sample => sample.VramUsedBytes)),
        Average(samples.Select(sample => sample.CpuPercent)),
        Average(samples.Select(sample => (double?)((sample.DiskReadBytesPerSecond ?? 0) + (sample.DiskWriteBytesPerSecond ?? 0)))),
        temporaryStorage);
    private static double? Average(IEnumerable<double?> values) { double[] populated = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray(); return populated.Length == 0 ? null : populated.Average(); }
    private static long? AverageLong(IEnumerable<long?> values) { long[] populated = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray(); return populated.Length == 0 ? null : (long)populated.Average(); }
    private static double? Max(IEnumerable<double?> values) => values.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max();
    private static long? MaxLong(IEnumerable<long?> values) => values.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max();
    private static long MeasureDirectory(string root) { try { return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length); } catch { return 0; } }
}

/// <summary>Versioned, bounded JSON history for user-visible benchmark results.</summary>
public sealed class AiBackendBenchmarkHistoryStore
{
    public const int CurrentVersion = 1;
    private readonly string _path;
    private readonly int _maximumEntries;
    public AiBackendBenchmarkHistoryStore(string path, int maximumEntries = 100) { _path = path; _maximumEntries = Math.Max(1, maximumEntries); }
    public async Task<IReadOnlyList<AiBackendBenchmarkResult>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return Array.Empty<AiBackendBenchmarkResult>();
        try
        {
            await using FileStream stream = File.OpenRead(_path);
            AiBackendBenchmarkHistoryDocument? document = await JsonSerializer.DeserializeAsync<AiBackendBenchmarkHistoryDocument>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document?.Version == CurrentVersion ? document.Results.OrderByDescending(result => result.Date).Take(_maximumEntries).ToArray() : Array.Empty<AiBackendBenchmarkResult>();
        }
        catch (JsonException) { return Array.Empty<AiBackendBenchmarkResult>(); }
    }
    public async Task<IReadOnlyList<AiBackendBenchmarkResult>> AppendAsync(AiBackendBenchmarkResult result, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AiBackendBenchmarkResult> results = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Append(result).OrderByDescending(item => item.Date).Take(_maximumEntries).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string staging = _path + ".staging";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(new AiBackendBenchmarkHistoryDocument(CurrentVersion, results), new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        File.Move(staging, _path, true);
        return results;
    }
    private sealed record AiBackendBenchmarkHistoryDocument(int Version, IReadOnlyList<AiBackendBenchmarkResult> Results);
}
