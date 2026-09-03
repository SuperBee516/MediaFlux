using System.Buffers.Binary;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiBackendBenchmarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxBenchmarkTests", Guid.NewGuid().ToString("N"));
    public AiBackendBenchmarkTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task NcnnBenchmarkUsesBackendContractCollectsMetricsAndLogsConciseSummary()
    {
        string input = CreateFrames(3, 2, 2);
        var logs = new List<string>();
        var service = new AiBackendBenchmarkService(Path.Combine(_root, "benchmarks"), logs.Add,
            sampleResources: () => new HardwareUsageSample(50, 1024, 20, 10, 20), gpuInfo: () => ("NVIDIA Test", "555.1"), history: History(), database: Database());
        var backend = new BenchmarkBackend("ncnn-vulkan", writeValidOutput: true);

        AiBackendBenchmarkResult result = await service.RunAsync(new(backend, Settings(), Directory.EnumerateFiles(input).OrderBy(path => path).ToArray(), 2, 2, 3));

        Assert.True(result.Validation.IsValid);
        Assert.Equal("ncnn-vulkan", result.BackendId);
        Assert.Equal(3, result.FrameCount);
        Assert.True(result.EffectiveFramesPerSecond > 0);
        Assert.Equal(50, result.Resources.AverageGpuPercent);
        Assert.Equal(1024, result.Resources.PeakVramBytes);
        Assert.True(result.Resources.TemporaryStorageBytes > 0);
        Assert.Single(await History().LoadAsync());
        Assert.Contains(logs, line => line.StartsWith("[AI Benchmark] started", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("validation=passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationFailureIsRecordedAndExcludedFromComparison()
    {
        string input = CreateFrames(2, 2, 2);
        var service = new AiBackendBenchmarkService(Path.Combine(_root, "benchmarks"), sampleResources: () => new(null, null, null, null, null), history: History(), database: Database());
        AiBackendBenchmarkResult failed = await service.RunAsync(new(new BenchmarkBackend("ncnn-vulkan", writeValidOutput: false), Settings(), Directory.EnumerateFiles(input).OrderBy(path => path).ToArray(), 2, 2, 2));
        AiBackendBenchmarkResult valid = Result("other", 5, valid: true);

        AiBackendBenchmarkComparison comparison = AiBackendBenchmarkService.Compare(new[] { failed, valid });

        Assert.False(failed.Validation.IsValid);
        Assert.Single(comparison.Results);
        Assert.Same(valid, comparison.Winner);
    }

    [Fact]
    public async Task HistoryIsVersionedAndBounded()
    {
        string history = Path.Combine(_root, "history", "ai-benchmarks.json");
        var store = new AiBackendBenchmarkHistoryStore(history, maximumEntries: 2);
        await store.AppendAsync(Result("first", 1, true) with { Date = DateTimeOffset.UtcNow.AddMinutes(-2) });
        await store.AppendAsync(Result("second", 2, true) with { Date = DateTimeOffset.UtcNow.AddMinutes(-1) });
        IReadOnlyList<AiBackendBenchmarkResult> entries = await store.AppendAsync(Result("third", 3, true));

        Assert.Equal(2, entries.Count);
        Assert.Contains("\"Version\": 1", await File.ReadAllTextAsync(history));
        Assert.Equal("third", (await store.LoadAsync())[0].BackendId);

        await File.WriteAllTextAsync(history, "{\"Version\":99,\"Results\":[]}");
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public void ComparisonAndRecommendationChooseOnlyFastestValidatedMatchingResult()
    {
        AiBackendBenchmarkResult slow = Result("ncnn-vulkan", 10, true);
        AiBackendBenchmarkResult fast = Result("future", 20, true);
        AiBackendBenchmarkResult failed = Result("broken", 500, false);

        AiBackendBenchmarkComparison comparison = AiBackendBenchmarkService.Compare(new[] { slow, fast, failed });
        AiBackendBenchmarkRecommendation recommendation = Assert.IsType<AiBackendBenchmarkRecommendation>(AiBackendBenchmarkService.FindFastestValidated(new[] { slow, fast, failed }, "NVIDIA Test", "SD", "model-x2", AiRestorationScale.X2));

        Assert.Same(fast, comparison.Winner);
        Assert.Same(fast, recommendation.Result);
        Assert.Contains("20", recommendation.Reason);
    }

    [Fact]
    public async Task BenchmarkRejectsWholeVideoSizedOrInsufficientPreviewInputs()
    {
        string input = CreateFrames(1, 2, 2);
        var service = new AiBackendBenchmarkService(Path.Combine(_root, "benchmarks"), history: History());
        var request = new AiBackendBenchmarkRequest(new BenchmarkBackend("ncnn-vulkan", true), Settings(), Directory.EnumerateFiles(input).ToArray(), 2, 2, AiBackendBenchmarkService.MaximumFrameCount + 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunAsync(request));
    }

    private VideoRestorationSettings Settings() => new() { AiMode = AiRestorationMode.Animation, AiModelId = "model", AiScale = AiRestorationScale.X2 };
    private AiBackendBenchmarkHistoryStore History() => new(Path.Combine(_root, "service-history.json"));
    private AiBenchmarkDatabase Database() => new(Path.Combine(_root, "ai-benchmarks.db"));
    private string CreateFrames(int count, int width, int height)
    {
        string directory = Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        for (int index = 0; index < count; index++) WritePng(Path.Combine(directory, $"source-{index:D8}.png"), width, height);
        return directory;
    }
    private static AiBackendBenchmarkResult Result(string backend, double fps, bool valid) => new(DateTimeOffset.UtcNow, backend, backend, "1", "NVIDIA Test", "555.1", "model-x2", AiRestorationScale.X2, "SD", 640, 480, 120, TimeSpan.FromSeconds(1), fps, new(null, null, null, null, null, null, 0), new(valid, valid ? "validated" : "failed"), Array.Empty<string>());
    private static void WritePng(string path, int width, int height)
    {
        byte[] bytes = new byte[64]; bytes[0] = 137; bytes[1] = 80; bytes[2] = 78; bytes[3] = 71; bytes[4] = 13; bytes[5] = 10; bytes[6] = 26; bytes[7] = 10; bytes[12] = 73; bytes[13] = 72; bytes[14] = 68; bytes[15] = 82;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height); File.WriteAllBytes(path, bytes);
    }

    private sealed class BenchmarkBackend : IAiRestorationBackend
    {
        private readonly bool _writeValidOutput;
        public BenchmarkBackend(string id, bool writeValidOutput) { Id = id; _writeValidOutput = writeValidOutput; }
        public string Id { get; }
        public string DisplayName => Id == "ncnn-vulkan" ? "NCNN Vulkan" : Id;
        public Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new AiBackendMetadata(Id, DisplayName, "1.0", true, true, null, true, true, true, true, true, new[] { "test diagnostic" }));
        public Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new AiRestorationSession(new(true, Id, "", "test", true, new[] { "Auto" }, Array.Empty<AiRestorationModel>(), null), new("model", "Model", settings.AiMode, new[] { settings.AiScale }, "", "", "", Id, "model-x2")));
        public Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null) => throw new NotSupportedException();
        public Task<AiDirectoryProcessDiagnostic> ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null)
        {
            if (_writeValidOutput)
                foreach (string output in expectedOutputFrames) WritePng(output, 4, 4);
            return Task.FromResult(new AiDirectoryProcessDiagnostic("test", 0, TimeSpan.Zero, "", "", expectedOutputFrames.Count, _writeValidOutput ? expectedOutputFrames.Count : 0, null, null, null, null));
        }
    }
}
