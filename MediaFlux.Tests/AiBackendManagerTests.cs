using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiBackendManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxBackendTests", Guid.NewGuid().ToString("N"));
    public AiBackendManagerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task AutoSelectsNcnnWhenTensorRtIsDiscoveryOnly()
    {
        var ncnn = new FakeBackend("ncnn-vulkan", "NCNN Vulkan", ready: true);
        var tensorRt = new FakeBackend("nvidia-tensorrt", "NVIDIA TensorRT", ready: false);
        var manager = new AiBackendManager(_root, ncnn: ncnn, tensorRt: tensorRt);

        IAiRestorationBackend selected = await manager.SelectAsync(new VideoRestorationSettings());

        Assert.Same(ncnn, selected);
    }

    [Fact]
    public async Task ManualUnavailableBackendIsRejectedWithItsReason()
    {
        var manager = new AiBackendManager(_root, ncnn: new FakeBackend("ncnn-vulkan", "NCNN Vulkan", ready: true), tensorRt: new FakeBackend("nvidia-tensorrt", "NVIDIA TensorRT", ready: false, reason: "Missing runtime libraries"));
        var settings = new VideoRestorationSettings { AiBackendSelection = AiBackendSelection.NvidiaTensorRt };

        AiRestorationValidationException error = await Assert.ThrowsAsync<AiRestorationValidationException>(() => manager.SelectAsync(settings));

        Assert.Contains("Missing runtime libraries", error.Message);
    }

    [Fact]
    public async Task ManualNcnnSelectionUsesTheNcnnPassthrough()
    {
        var ncnn = new FakeBackend("ncnn-vulkan", "NCNN Vulkan", ready: true);
        var manager = new AiBackendManager(_root, ncnn: ncnn, tensorRt: new FakeBackend("nvidia-tensorrt", "NVIDIA TensorRT", ready: false));

        Assert.Same(ncnn, await manager.SelectAsync(new VideoRestorationSettings { AiBackendSelection = AiBackendSelection.NcnnVulkan }));
    }

    [Fact]
    public async Task TensorRtDiscoveryReportsEveryRequiredCapabilityWithoutInference()
    {
        string runtime = Path.Combine(_root, "runtime"); Directory.CreateDirectory(runtime);
        foreach (string file in new[] { "cudart64_130.dll", "nvinfer.dll", "nvinfer_plugin.dll", "model.engine" }) File.WriteAllText(Path.Combine(runtime, file), "test");
        var backend = new TensorRtAiRestorationBackend(_root, nvidiaGpuPresent: () => true, runtimeDirectories: () => new[] { runtime });

        AiBackendMetadata metadata = await backend.GetMetadataAsync(new VideoRestorationSettings());

        Assert.True(metadata.IsAvailable);
        Assert.False(metadata.IsReady);
        Assert.False(metadata.SupportsFullEncode);
        Assert.Contains("not implemented", metadata.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(metadata.Diagnostics, line => line.StartsWith("CUDA runtime: Detected", StringComparison.Ordinal));
        Assert.Contains(metadata.Diagnostics, line => line.StartsWith("TensorRT runtime: Detected", StringComparison.Ordinal));
    }

    [Fact]
    public void StartupDiagnosticsShowSelectedAndUnavailableTensorRtReason()
    {
        string message = AiBackendManager.FormatStartup("NCNN Vulkan", new("ncnn-vulkan", "NCNN Vulkan", "1", true, true, null, true, true, true, true, true, Array.Empty<string>()), new("nvidia-tensorrt", "NVIDIA TensorRT", "Unavailable", false, false, "Missing runtime libraries", false, false, false, false, false, Array.Empty<string>()));
        Assert.Contains("Selected: NCNN Vulkan", message);
        Assert.Contains("Not Installed", message);
        Assert.Contains("Missing runtime libraries", message);
    }

    [Fact]
    public void CompletedChunkSummaryIsAvailableBeforeCancellation()
    {
        var timing = new PerformanceTimingService();
        timing.SetAiChunkPlannerDecision(new AiChunkPlannerDecision(640, 480, AiRestorationScale.X2, 1, 60, 100, null, 60, 60, 60, 60, "GPU VRAM", "test"));
        timing.RecordAiChunk(new AiChunkPerformanceMetrics(1, 60, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), new AiChunkHardwareMetrics(null, null, null, null, null, null)));

        string summary = timing.BuildSummary();

        Assert.Contains("AI Chunks: 1", summary);
        Assert.Contains("AI Chunk Planner Summary", summary);
        Assert.Contains("AI Planner Calibration Summary", summary);
    }

    private sealed class FakeBackend : IAiRestorationBackend
    {
        private readonly AiBackendMetadata _metadata;
        public FakeBackend(string id, string name, bool ready, string? reason = null) { Id = id; DisplayName = name; _metadata = new(id, name, "test", ready, ready, reason ?? (ready ? null : "Unavailable"), ready, ready, ready, ready, ready, Array.Empty<string>()); }
        public string Id { get; }
        public string DisplayName { get; }
        public Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(_metadata);
        public Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new AiRestorationCapabilities(_metadata.IsAvailable, Id, "", "test", true, new[] { "Auto" }, Array.Empty<AiRestorationModel>(), _metadata.Reason));
        public Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null) => throw new NotSupportedException();
        public Task ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null) => throw new NotSupportedException();
    }
}
