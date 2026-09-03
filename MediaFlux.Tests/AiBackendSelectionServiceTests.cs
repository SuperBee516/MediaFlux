using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiBackendSelectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxBackendSelectionTests", Guid.NewGuid().ToString("N"));
    public AiBackendSelectionServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task AutoSelectsFastestVerifiedCompatibleBenchmark()
    {
        AiBenchmarkDatabase database = new(Path.Combine(_root, "benchmarks.db"));
        var ncnn = new ReadyBackend("ncnn-vulkan", "NCNN Vulkan", "ncnn-v1", "model-x2");
        var tensorRt = new ReadyBackend("nvidia-tensorrt", "NVIDIA TensorRT", "tensorrt-v1", "model-x2");
        Store(database, ncnn, 18.5); Store(database, tensorRt, 31.25);
        var selector = new AiBackendSelectionService(database, Hardware, new AiRuntimeTelemetryService());

        AiBackendSelectionDecision result = await selector.SelectAsync(AiBackendSelection.Auto, Settings(), Candidates(ncnn, tensorRt), 1920, 1080);

        Assert.Same(tensorRt, result.Backend);
        Assert.Equal(AiBackendSelection.NvidiaTensorRt, result.Selected);
        Assert.Equal(31.25, result.VerifiedFramesPerSecond);
        Assert.Contains("fastest verified", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AiBackendSelection.NvidiaTensorRt, AiBackendSelectionDiagnostics.Shared.GetLatest()!.Selected);
    }

    [Fact]
    public async Task AutoFallsBackToNcnnWhenNoVerifiedBenchmarkExists()
    {
        var ncnn = new ReadyBackend("ncnn-vulkan", "NCNN Vulkan", "ncnn-v1", "model-x2");
        var selector = new AiBackendSelectionService(new AiBenchmarkDatabase(Path.Combine(_root, "empty.db")), Hardware, new AiRuntimeTelemetryService());

        AiBackendSelectionDecision result = await selector.SelectAsync(AiBackendSelection.Auto, Settings(), Candidates(ncnn), 1920, 1080);

        Assert.Same(ncnn, result.Backend);
        Assert.Equal(AiBackendSelection.NcnnVulkan, result.Selected);
        Assert.Null(result.VerifiedFramesPerSecond);
        Assert.Contains("highest-priority", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitUnavailableBackendFailsWithTheProviderReason()
    {
        var ncnn = new ReadyBackend("ncnn-vulkan", "NCNN Vulkan", "ncnn-v1", "model-x2");
        AiBackendMetadata directMl = new("directml", "DirectML", "Unavailable", false, false, "DirectML inference is not implemented in this MediaFlux phase.", false, false, false, false, false, Array.Empty<string>());
        var selector = new AiBackendSelectionService(new AiBenchmarkDatabase(Path.Combine(_root, "unavailable.db")), Hardware, new AiRuntimeTelemetryService());

        VideoRestorationSettings settings = Settings();
        settings.AiBackendSelection = AiBackendSelection.DirectMl;
        AiRestorationValidationException error = await Assert.ThrowsAsync<AiRestorationValidationException>(() => selector.SelectAsync(AiBackendSelection.DirectMl, settings, new[] { new AiBackendCandidate(AiBackendSelection.NcnnVulkan, ncnn, ncnn.Metadata), new AiBackendCandidate(AiBackendSelection.DirectMl, null, directMl) }));

        Assert.Contains("not implemented", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static VideoRestorationSettings Settings() => new() { AiMode = AiRestorationMode.General, AiModelId = "model-x2", AiScale = AiRestorationScale.X2 };
    private static HardwareSnapshot Hardware() => new("CPU", 8, "GPU", "driver", 8_000_000_000, null, "C:", "C:", "C:", "Windows", "ffmpeg");
    private static AiBackendCandidate[] Candidates(params ReadyBackend[] backends) => backends.Select(backend => new AiBackendCandidate(backend.Id == "ncnn-vulkan" ? AiBackendSelection.NcnnVulkan : AiBackendSelection.NvidiaTensorRt, backend, backend.Metadata)).ToArray();
    private static void Store(AiBenchmarkDatabase database, ReadyBackend backend, double fps) => database.Store(new(new(backend.Id, backend.Identity, backend.ModelName, "GPU", "driver", "FP32", 2, "1080p"), NcnnRuntimeConfiguration.SafeDefault, fps, null, true, DateTimeOffset.UtcNow, "validated"));

    private sealed class ReadyBackend : IAiRestorationBackend
    {
        public ReadyBackend(string id, string displayName, string identity, string modelName) { Id = id; DisplayName = displayName; Identity = identity; ModelName = modelName; Metadata = new(id, displayName, "test", true, true, null, true, true, true, true, true, Array.Empty<string>()); }
        public string Id { get; } public string DisplayName { get; } public string Identity { get; } public string ModelName { get; } public AiBackendMetadata Metadata { get; }
        public Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(Metadata);
        public Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new AiRestorationCapabilities(true, Id, "", Identity, true, new[] { "Auto" }, Array.Empty<AiRestorationModel>(), null));
        public Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(Model());
        public Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new AiRestorationSession(new(true, Id, "", Identity, true, new[] { "Auto" }, new[] { Model() }, null), Model()));
        public Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null) => throw new NotSupportedException();
        public Task<AiDirectoryProcessDiagnostic> ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null) => throw new NotSupportedException();
        private AiRestorationModel Model() => new(ModelName, ModelName, AiRestorationMode.General, new[] { AiRestorationScale.X2 }, "", "", "", Id, ModelName);
    }
}
