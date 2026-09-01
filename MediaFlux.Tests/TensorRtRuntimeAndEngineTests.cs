using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class TensorRtRuntimeAndEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxTensorRtTests", Guid.NewGuid().ToString("N"));
    public TensorRtRuntimeAndEngineTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task RuntimeDiscoveryReportsVersionsGpuAndSupportedPrecision()
    {
        TensorRtRuntimeService runtime = CreateRuntime();

        TensorRtRuntimeInfo result = await runtime.DiscoverAsync();

        Assert.True(result.IsReady);
        Assert.Equal("NVIDIA Test", result.Gpu.Name);
        Assert.Equal("8.6", result.Gpu.ComputeCapability);
        Assert.Contains(TensorRtPrecision.FP32, result.SupportedPrecisions);
        Assert.Contains(TensorRtPrecision.FP16, result.SupportedPrecisions);
        Assert.Contains(TensorRtPrecision.INT8, result.SupportedPrecisions);
        Assert.Contains(result.Diagnostics, line => line.StartsWith("CUDA runtime:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EngineDiscoveryPersistsAndValidatesMetadata()
    {
        TensorRtRuntimeService runtime = CreateRuntime();
        string engines = Path.Combine(_root, "engines"); Directory.CreateDirectory(engines);
        var manager = new TensorRtEngineManager(engines, runtime);
        string path = WriteEngine(engines, "anime.engine");
        TensorRtRuntimeInfo info = await runtime.DiscoverAsync();
        TensorRtEngineMetadata metadata = Metadata(info);

        await manager.SaveMetadataAsync(path, metadata);
        IReadOnlyList<TensorRtEngineDiscoveryItem> discovered = await manager.DiscoverAsync();
        TensorRtEngineValidationResult validation = await manager.ValidateAsync(path, metadata.Identity);

        Assert.Single(discovered);
        Assert.Equal(metadata, await manager.LoadMetadataAsync(path));
        Assert.True(validation.IsValid);
        Assert.Contains("Validated", validation.Reason);
    }

    [Fact]
    public async Task EngineValidationRejectsVersionCudaGpuAndExpectedIdentityMismatches()
    {
        TensorRtRuntimeService runtime = CreateRuntime();
        string engines = Path.Combine(_root, "engines"); Directory.CreateDirectory(engines);
        var manager = new TensorRtEngineManager(engines, runtime);
        string path = WriteEngine(engines, "test.engine");
        TensorRtRuntimeInfo info = await runtime.DiscoverAsync();

        await manager.SaveMetadataAsync(path, Metadata(info) with { Identity = Metadata(info).Identity with { TensorRtVersion = "different" } });
        Assert.Contains("TensorRT version mismatch", (await manager.ValidateAsync(path)).Reason);

        await manager.SaveMetadataAsync(path, Metadata(info) with { Identity = Metadata(info).Identity with { CudaVersion = "different" } });
        Assert.Contains("CUDA version mismatch", (await manager.ValidateAsync(path)).Reason);

        await manager.SaveMetadataAsync(path, Metadata(info) with { MinimumComputeCapability = "9.0" });
        Assert.Contains("compute capability", (await manager.ValidateAsync(path)).Reason, StringComparison.OrdinalIgnoreCase);

        await manager.SaveMetadataAsync(path, Metadata(info));
        TensorRtEngineIdentity expected = Metadata(info).Identity with { Model = "other-model" };
        Assert.Contains("does not match", (await manager.ValidateAsync(path, expected)).Reason);
    }

    [Fact]
    public async Task CacheIsLazyThreadSafeAndEvictsOnlyIdleReleasedEngines()
    {
        TensorRtRuntimeService runtime = CreateRuntime();
        string engines = Path.Combine(_root, "engines"); Directory.CreateDirectory(engines);
        var manager = new TensorRtEngineManager(engines, runtime, TimeSpan.FromMinutes(1));
        string path = WriteEngine(engines, "cache.engine");
        TensorRtRuntimeInfo info = await runtime.DiscoverAsync();
        await manager.SaveMetadataAsync(path, Metadata(info));

        TensorRtEngineLease first = await manager.AcquireAsync(path);
        TensorRtEngineLease second = await manager.AcquireAsync(path);
        Assert.Equal(1, manager.CachedEngineCount);
        Assert.Equal(0, manager.UnloadIdleEngines(DateTimeOffset.UtcNow.AddHours(1)));
        first.Dispose(); second.Dispose();

        Assert.Equal(1, manager.UnloadIdleEngines(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(first.Engine.IsDisposed);
    }

    [Fact]
    public async Task InvalidOrMissingMetadataIsRejectedAndLoggedConcicely()
    {
        TensorRtRuntimeService runtime = CreateRuntime();
        string engines = Path.Combine(_root, "engines"); Directory.CreateDirectory(engines);
        var logs = new List<string>(); var manager = new TensorRtEngineManager(engines, runtime, log: logs.Add);
        string path = WriteEngine(engines, "missing-metadata.engine");

        TensorRtEngineValidationResult result = await manager.ValidateAsync(path);

        Assert.False(result.IsValid);
        Assert.Contains("metadata", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(logs);
        Assert.Contains("valid=False", logs[0]);
    }

    [Fact]
    public async Task TensorRtBackendRemainsDiscoveryOnlyAfterRuntimeAndEngineValidation()
    {
        string runtimeDirectory = Path.Combine(_root, "runtime"); Directory.CreateDirectory(runtimeDirectory);
        foreach (string file in new[] { "cudart64_130.dll", "nvinfer.dll", "nvinfer_plugin.dll" }) File.WriteAllText(Path.Combine(runtimeDirectory, file), "runtime");
        var backend = new TensorRtAiRestorationBackend(_root, nvidiaGpuPresent: () => true, runtimeDirectories: () => new[] { runtimeDirectory });

        AiBackendMetadata metadata = await backend.GetMetadataAsync(new VideoRestorationSettings());

        Assert.True(metadata.IsAvailable);
        Assert.False(metadata.IsReady);
        Assert.False(metadata.SupportsFullEncode);
        Assert.Contains("inference is not implemented", metadata.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(metadata.Diagnostics, line => line.StartsWith("Available engines:", StringComparison.Ordinal));
    }

    private TensorRtRuntimeService CreateRuntime()
    {
        string directory = Path.Combine(_root, "runtime"); Directory.CreateDirectory(directory);
        foreach (string file in new[] { "cudart64_130.dll", "nvinfer.dll", "nvinfer_plugin.dll" }) File.WriteAllText(Path.Combine(directory, file), "runtime");
        return new TensorRtRuntimeService(_root, nvidiaGpuPresent: () => true, runtimeDirectories: () => new[] { directory }, gpuInfo: () => new("NVIDIA Test", "555.1", "8.6"));
    }
    private static TensorRtEngineMetadata Metadata(TensorRtRuntimeInfo runtime) => new(new("anime", AiRestorationScale.X2, TensorRtPrecision.FP16, runtime.TensorRtVersion, runtime.CudaVersion), DateTimeOffset.UtcNow, "7.0");
    private static string WriteEngine(string directory, string name) { string path = Path.Combine(directory, name); File.WriteAllText(path, "metadata-only-engine"); return path; }
}
