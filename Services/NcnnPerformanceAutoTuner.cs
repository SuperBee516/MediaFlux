using System.Buffers.Binary;
using System.Diagnostics;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Runs a short, staged NCNN search against owned sample frames. It never changes quality options.</summary>
public sealed class NcnnPerformanceAutoTuner
{
    private const int SampleFrameBudget = 4;
    private static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(90);
    private readonly IAiRestorationBackend _backend;
    private readonly NcnnPerformanceTuningCacheService _cache;
    private readonly AiBenchmarkDatabase _benchmarks;
    private readonly Action<string>? _log;

    public NcnnPerformanceAutoTuner(IAiRestorationBackend backend, NcnnPerformanceTuningCacheService? cache = null, Action<string>? log = null, AiBenchmarkDatabase? benchmarks = null)
    { _backend = backend; _cache = cache ?? new NcnnPerformanceTuningCacheService(); _benchmarks = benchmarks ?? new AiBenchmarkDatabase(); _log = log; }

    public async Task<NcnnRuntimeSelection> SelectAsync(
        AiRestorationSession session,
        VideoRestorationSettings settings,
        string inputDirectory,
        string tuningDirectory,
        int sourceWidth,
        int sourceHeight,
        string gpuIdentity,
        string gpuDriver,
        long? dedicatedGpuVramBytes,
        bool allowTuning,
        CancellationToken cancellationToken)
    {
        NcnnTuningCacheKey key = NcnnTuningCacheKey.Create(gpuIdentity, session.Capabilities.Identity, session.Model.BackendModelName, (int)settings.AiScale, ResolutionClass(sourceWidth, sourceHeight), gpuDriver);
        if (_cache.TryGet(key, out NcnnRuntimeConfiguration cached))
            return new(cached, NcnnRuntimeConfigurationSource.Cached, CacheKey: key.Value);
        var benchmarkKey = new AiBenchmarkDatabaseKey(session.Capabilities.BackendId, session.Capabilities.Identity, session.Model.BackendModelName, gpuIdentity, gpuDriver, "FP32", (int)settings.AiScale, ResolutionClass(sourceWidth, sourceHeight));
        if (_benchmarks.TryGetFastestStable(benchmarkKey, out AiBenchmarkDatabaseEntry benchmark))
        {
            _cache.Store(key, benchmark.Configuration);
            _log?.Invoke($"[NCNN Tuning] Reusing validated benchmark database result: {benchmark.Configuration.ThreadsDisplay}, tile {benchmark.Configuration.TileDisplay}; {benchmark.FramesPerSecond:0.##} FPS; recorded {benchmark.Timestamp:O}.");
            return new(benchmark.Configuration, NcnnRuntimeConfigurationSource.BenchmarkDatabase, SelectedFramesPerSecond: benchmark.FramesPerSecond, CacheKey: key.Value);
        }
        if (!allowTuning || string.IsNullOrWhiteSpace(gpuIdentity) || gpuIdentity.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
            return new(NcnnRuntimeConfiguration.SafeDefault, NcnnRuntimeConfigurationSource.SafeDefault, CacheKey: key.Value);

        string[] sourceFrames = Directory.EnumerateFiles(inputDirectory, "*.png").OrderBy(path => path, StringComparer.Ordinal).Take(SampleFrameBudget).ToArray();
        if (sourceFrames.Length == 0)
            return new(NcnnRuntimeConfiguration.SafeDefault, NcnnRuntimeConfigurationSource.SafeDefault, CacheKey: key.Value);

        Stopwatch budget = Stopwatch.StartNew();
        var results = new List<NcnnTuningBenchmarkResult>();
        NcnnTuningBenchmarkResult baseline = await BenchmarkWithResourceGuardAsync(NcnnRuntimeConfiguration.SafeDefault, session, settings, sourceFrames, tuningDirectory, budget, dedicatedGpuVramBytes, cancellationToken).ConfigureAwait(false);
        results.Add(baseline);

        foreach (NcnnRuntimeConfiguration candidate in ThreadCandidates())
        {
            if (budget.Elapsed >= TotalBudget) break;
            results.Add(await BenchmarkWithResourceGuardAsync(candidate, session, settings, sourceFrames, tuningDirectory, budget, dedicatedGpuVramBytes, cancellationToken).ConfigureAwait(false));
        }

        NcnnRuntimeConfiguration threadWinner = SelectWinner(results)?.Configuration ?? NcnnRuntimeConfiguration.SafeDefault;
        if (threadWinner.Threads is not null)
        {
            foreach (int tile in TileCandidates(sourceWidth, sourceHeight))
            {
                if (budget.Elapsed >= TotalBudget) break;
                results.Add(await BenchmarkWithResourceGuardAsync(threadWinner with { TileSize = tile }, session, settings, sourceFrames, tuningDirectory, budget, dedicatedGpuVramBytes, cancellationToken).ConfigureAwait(false));
            }
        }

        foreach (NcnnTuningBenchmarkResult result in results)
        {
            _benchmarks.Store(new AiBenchmarkDatabaseEntry(benchmarkKey, result.Configuration, result.FramesPerSecond, result.PeakVramBytes, result.IsValid, DateTimeOffset.UtcNow, result.Result));
            _log?.Invoke($"[NCNN Tuning] {result.Configuration.ThreadsDisplay} | {result.Configuration.TileDisplay} | {result.FramesPerSecond:0.##} FPS | {FormatBytes(result.PeakVramBytes)} | {result.Result}");
        }

        NcnnTuningBenchmarkResult selected = SelectWinner(results) ?? baseline;
        NcnnRuntimeConfiguration selectedConfiguration = selected.IsValid ? selected.Configuration : NcnnRuntimeConfiguration.SafeDefault;
        var selection = new NcnnRuntimeSelection(selectedConfiguration, NcnnRuntimeConfigurationSource.AutoTuned, baseline.FramesPerSecond, selected.FramesPerSecond, key.Value);
        if (selected.IsValid) _cache.Store(key, selectedConfiguration);
        _log?.Invoke($"[NCNN Tuning] Selected: {selectedConfiguration.ThreadsDisplay}, tile {selectedConfiguration.TileDisplay}; Baseline FPS: {baseline.FramesPerSecond:0.##}; Selected FPS: {selected.FramesPerSecond:0.##}; Improvement: {selection.ImprovementPercent ?? 0:0.#}%.");
        return selection;
    }

    internal static IReadOnlyList<NcnnRuntimeConfiguration> ThreadCandidates() => new[]
    {
        new NcnnRuntimeConfiguration(NcnnThreadConfiguration.OneTwoTwo),
        new NcnnRuntimeConfiguration(NcnnThreadConfiguration.TwoTwoTwo),
        new NcnnRuntimeConfiguration(NcnnThreadConfiguration.FourFourFour)
    };

    internal static IReadOnlyList<int> TileCandidates(int width, int height) => width * height >= 3840 * 2160 ? new[] { 256, 512 }
        : new[] { 256, 512, 1024 };

    internal static NcnnTuningBenchmarkResult? SelectWinner(IEnumerable<NcnnTuningBenchmarkResult> results) => results
        .Where(result => result.IsValid && result.FramesPerSecond > 0)
        .OrderByDescending(result => result.FramesPerSecond)
        .ThenBy(result => result.Elapsed)
        .FirstOrDefault();

    internal static string ResolutionClass(int width, int height)
    {
        int pixels = Math.Max(1, width) * Math.Max(1, height);
        return pixels <= 640 * 480 ? "SD" : pixels <= 1280 * 720 ? "720p" : pixels <= 1920 * 1080 ? "1080p" : pixels >= 3840 * 2160 ? "4K+" : "1440p";
    }

    private async Task<NcnnTuningBenchmarkResult> BenchmarkAsync(NcnnRuntimeConfiguration configuration, AiRestorationSession session, VideoRestorationSettings settings, IReadOnlyList<string> sourceFrames, string root, Stopwatch budget, CancellationToken token)
    {
        if (budget.Elapsed >= TotalBudget) return new(configuration, 0, TimeSpan.Zero, null, null, false, "budget exhausted");
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string input = Path.Combine(directory, "input"), output = Path.Combine(directory, "output");
        try
        {
            Directory.CreateDirectory(input); Directory.CreateDirectory(output);
            foreach (string source in sourceFrames) File.Copy(source, Path.Combine(input, Path.GetFileName(source)));
            string[] expected = sourceFrames.Select(frame => Path.Combine(output, Path.GetFileName(frame))).ToArray();
            using var hardware = new HardwarePerformanceService();
            HardwareUsageSample before = hardware.Sample();
            Stopwatch stopwatch = Stopwatch.StartNew();
            await _backend.ProcessDirectoryAsync(session, settings, input, output, expected, null, token, configuration, CandidateTimeout).ConfigureAwait(false);
            stopwatch.Stop();
            HardwareUsageSample after = hardware.Sample();
            ValidateOutputDimensions(sourceFrames, expected, settings.AiScale);
            double fps = expected.Length / Math.Max(stopwatch.Elapsed.TotalSeconds, .001);
            return new(configuration, fps, stopwatch.Elapsed, Max(before.VramUsedBytes, after.VramUsedBytes), Average(before.GpuPercent, after.GpuPercent), true, "valid");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(configuration, 0, TimeSpan.Zero, null, null, false, Concise(ex.Message)); }
        finally { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }
    }

    private async Task<NcnnTuningBenchmarkResult> BenchmarkWithResourceGuardAsync(NcnnRuntimeConfiguration configuration, AiRestorationSession session, VideoRestorationSettings settings, IReadOnlyList<string> sourceFrames, string root, Stopwatch budget, long? dedicatedGpuVramBytes, CancellationToken token)
    {
        NcnnTuningBenchmarkResult result = await BenchmarkAsync(configuration, session, settings, sourceFrames, root, budget, token).ConfigureAwait(false);
        return result.IsValid && result.PeakVramBytes is long peak && dedicatedGpuVramBytes is long capacity && peak >= capacity * .9
            ? result with { IsValid = false, Result = "resource limit" }
            : result;
    }

    private static void ValidateOutputDimensions(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, AiRestorationScale scale)
    {
        if (inputs.Count != outputs.Count || outputs.Any(path => !File.Exists(path))) throw new AiRestorationValidationException("NCNN tuning produced missing or extra frames.");
        for (int index = 0; index < inputs.Count; index++)
        {
            (int width, int height) = ReadPngDimensions(inputs[index]);
            (int outputWidth, int outputHeight) = ReadPngDimensions(outputs[index]);
            if (outputWidth != width * (int)scale || outputHeight != height * (int)scale)
                throw new AiRestorationValidationException("NCNN tuning produced invalid frame dimensions.");
        }
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        byte[] header = new byte[24]; using var stream = File.OpenRead(path);
        if (stream.Read(header, 0, header.Length) != header.Length || header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71)
            throw new AiRestorationValidationException("NCNN tuning output is not a readable PNG.");
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }

    private static long? Max(long? first, long? second) => first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);
    private static double? Average(double? first, double? second) => first is null ? second : second is null ? first : (first.Value + second.Value) / 2;
    private static string FormatBytes(long? bytes) => bytes is long value ? $"{value / 1048576d:0.#} MiB" : "Unavailable";
    private static string Concise(string message) => string.IsNullOrWhiteSpace(message) ? "failed" : message.Replace('\r', ' ').Replace('\n', ' ').Trim()[..Math.Min(160, message.Trim().Length)];
}
