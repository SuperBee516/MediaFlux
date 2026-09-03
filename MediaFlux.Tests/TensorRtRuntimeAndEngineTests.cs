using MediaFlux.Models;
using MediaFlux.Services;
using System.Drawing;
using System.Drawing.Imaging;
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
        TensorRtEngineMetadata persisted = Assert.IsType<TensorRtEngineMetadata>(await manager.LoadMetadataAsync(path));
        Assert.Equal(metadata.Identity, persisted.Identity);
        Assert.False(string.IsNullOrWhiteSpace(persisted.EngineHash));
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
    public async Task TensorRtBackendIsUnavailableWithoutTheNativeBridge()
    {
        string runtimeDirectory = Path.Combine(_root, "runtime"); Directory.CreateDirectory(runtimeDirectory);
        foreach (string file in new[] { "cudart64_130.dll", "nvinfer.dll", "nvinfer_plugin.dll" }) File.WriteAllText(Path.Combine(runtimeDirectory, file), "runtime");
        var backend = new TensorRtAiRestorationBackend(_root, nvidiaGpuPresent: () => true, runtimeDirectories: () => new[] { runtimeDirectory });

        AiBackendMetadata metadata = await backend.GetMetadataAsync(new VideoRestorationSettings());

        Assert.True(metadata.IsAvailable);
        Assert.False(metadata.IsReady);
        Assert.False(metadata.SupportsFullEncode);
        Assert.Contains("bridge", metadata.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(metadata.Diagnostics, line => line.StartsWith("Available engines:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EngineBuildIsReusedAndIncompatibleMetadataTriggersAutomaticRebuild()
    {
        TensorRtRuntimeService runtime = CreateRuntime(); string engines = Path.Combine(_root, "managed-engines"); string bridgePath = Path.Combine(_root, "mediaflux-tensorrt.exe"); File.WriteAllText(bridgePath, "bridge");
        var runner = new TensorRtFakeRunner(); var bridge = new TensorRtProcessBridge(bridgePath, runner); var manager = new TensorRtEngineManager(engines, runtime);
        string onnx = Path.Combine(_root, "model.onnx"); File.WriteAllText(onnx, "onnx");
        var identity = new AiModelIdentity("model", "nvidia-tensorrt", AiRestorationScale.X2, AiRestorationMode.General, "1", new[] { "nvidia-tensorrt" }, "MODEL-HASH");
        var model = new AiManagedModel(identity, AiModelFormat.Onnx, "Model", onnx, null, null);

        TensorRtEngineResolution built = await manager.ResolveOrBuildAsync(model, TensorRtPrecision.FP16, new(), bridge);
        TensorRtEngineResolution reused = await manager.ResolveOrBuildAsync(model, TensorRtPrecision.FP16, new(), bridge);
        await manager.SaveMetadataAsync(built.EnginePath, built.Metadata with { Identity = built.Metadata.Identity with { TensorRtVersion = "incompatible" } });
        TensorRtEngineResolution rebuilt = await manager.ResolveOrBuildAsync(model, TensorRtPrecision.FP16, new(), bridge);

        Assert.Equal(TensorRtEngineCacheState.Built, built.CacheState);
        Assert.Equal(TensorRtEngineCacheState.Reused, reused.CacheState);
        Assert.Equal(TensorRtEngineCacheState.Rebuilt, rebuilt.CacheState);
        Assert.Equal(2, runner.BuildCount);
        Assert.True(rebuilt.Validation.IsValid);
    }

    [Fact]
    public async Task TensorRtBackendBuildsSessionRunsInferenceAndPassesSharedFrameValidation()
    {
        string runtimeDirectory = Path.Combine(_root, "runtime-live"); Directory.CreateDirectory(runtimeDirectory);
        foreach (string file in new[] { "cudart64_130.dll", "nvinfer.dll", "nvinfer_plugin.dll" }) File.WriteAllText(Path.Combine(runtimeDirectory, file), "runtime");
        string bridgePath = Path.Combine(_root, "mediaflux-tensorrt.exe"); File.WriteAllText(bridgePath, "bridge");
        string modelDirectory = Path.Combine(_root, "tensorrt-models"); Directory.CreateDirectory(modelDirectory); string onnx = Path.Combine(modelDirectory, "general.onnx"); File.WriteAllText(onnx, "onnx-model");
        var modelManager = new AiModelManager(); await modelManager.SaveMetadataAsync(onnx, new(new("general", "nvidia-tensorrt", AiRestorationScale.X2, AiRestorationMode.General, "1", new[] { "nvidia-tensorrt" }, ""), DateTimeOffset.UtcNow, new[] { "dynamic-shapes" }, "General"));
        var runner = new TensorRtFakeRunner();
        var backend = new TensorRtAiRestorationBackend(_root, () => true, () => new[] { runtimeDirectory }, () => new("NVIDIA Test", "555.1", "8.6"), runner, bridgePath, Path.Combine(_root, "engines"));
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.General, AiModelId = "general", AiScale = AiRestorationScale.X2, AiBackendSelection = AiBackendSelection.NvidiaTensorRt };
        AiRestorationSession session = await backend.CreateSessionAsync(settings);
        string inputDirectory = Path.Combine(_root, "frames-in"), outputDirectory = Path.Combine(_root, "frames-out"); Directory.CreateDirectory(inputDirectory); Directory.CreateDirectory(outputDirectory);
        string input = Path.Combine(inputDirectory, "frame-00000000.png"), output = Path.Combine(outputDirectory, "frame-00000000.png"); using (var bitmap = new Bitmap(2, 2)) { bitmap.SetPixel(0, 0, Color.Red); bitmap.Save(input, ImageFormat.Png); }

        AiDirectoryProcessDiagnostic diagnostic = await backend.ProcessDirectoryAsync(session, settings, inputDirectory, outputDirectory, new[] { output }, null);
        AiRestorationIntermediateVideoService.ValidateRestoredFrameSet(new[] { input }, new[] { output }, AiRestorationScale.X2);

        Assert.Equal("FP16", session.Runtime!.Precision);
        Assert.Equal(1, diagnostic.RestoredFrames);
        Assert.Equal(1, runner.InferenceCount);
        Assert.Contains("validation pending", TensorRtRuntimeDiagnostics.Shared.GetLatest()!.ValidationStatus, StringComparison.OrdinalIgnoreCase);

        var database = new AiBenchmarkDatabase(Path.Combine(_root, "benchmark.db"));
        var benchmark = new AiBackendBenchmarkService(Path.Combine(_root, "benchmark-staging"), sampleResources: () => new HardwareUsageSample(50, 1024, 20, null, null), gpuInfo: () => ("NVIDIA Test", "555.1"), history: new AiBackendBenchmarkHistoryStore(Path.Combine(_root, "benchmark-history.json")), database: database);
        AiBackendBenchmarkResult benchmarkResult = await benchmark.RunAsync(new(backend, settings, new[] { input }, 2, 2, 1, "TensorRT test"));
        Assert.True(benchmarkResult.Validation.IsValid);
        Assert.Contains(database.List(), record => record.Entry.Key.BackendId == "nvidia-tensorrt" && record.Entry.Key.Precision == "FP16");
    }

    private TensorRtRuntimeService CreateRuntime()
    {
        string directory = Path.Combine(_root, "runtime"); Directory.CreateDirectory(directory);
        foreach (string file in new[] { "cudart64_130.dll", "nvinfer.dll", "nvinfer_plugin.dll" }) File.WriteAllText(Path.Combine(directory, file), "runtime");
        return new TensorRtRuntimeService(_root, nvidiaGpuPresent: () => true, runtimeDirectories: () => new[] { directory }, gpuInfo: () => new("NVIDIA Test", "555.1", "8.6"));
    }
    private static TensorRtEngineMetadata Metadata(TensorRtRuntimeInfo runtime) => new(new("anime", AiRestorationScale.X2, TensorRtPrecision.FP16, runtime.TensorRtVersion, runtime.CudaVersion), DateTimeOffset.UtcNow, "7.0");
    private static string WriteEngine(string directory, string name) { string path = Path.Combine(directory, name); File.WriteAllText(path, "metadata-only-engine"); return path; }

    private sealed class TensorRtFakeRunner : IMediaToolProcessRunner
    {
        public int BuildCount { get; private set; } public int InferenceCount { get; private set; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            string command = request.Arguments[0];
            if (command == "build") { BuildCount++; string engine = Value(request.Arguments, "--engine"); Directory.CreateDirectory(Path.GetDirectoryName(engine)!); File.WriteAllBytes(engine, Enumerable.Repeat((byte)7, 128).ToArray()); }
            else if (command == "run-directory")
            {
                InferenceCount++; string input = Value(request.Arguments, "--input"), output = Value(request.Arguments, "--output"); Directory.CreateDirectory(output);
                foreach (string source in Directory.EnumerateFiles(input, "*.png")) using (var original = new Bitmap(source)) using (var restored = new Bitmap(original.Width * 2, original.Height * 2)) { using Graphics graphics = Graphics.FromImage(restored); graphics.DrawImage(original, 0, 0, restored.Width, restored.Height); restored.Save(Path.Combine(output, Path.GetFileName(source)), ImageFormat.Png); }
            }
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        }
        private static string Value(IReadOnlyList<string> arguments, string name) { for (int index = 0; index < arguments.Count - 1; index++) if (arguments[index] == name) return arguments[index + 1]; throw new InvalidOperationException($"Missing argument {name}."); }
    }
}
