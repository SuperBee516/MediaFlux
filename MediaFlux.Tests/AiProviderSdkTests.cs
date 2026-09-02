using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiProviderSdkTests
{
    [Fact]
    public async Task ProviderManagerNegotiatesSdkCapabilitiesLifecycleAndDiagnostics()
    {
        var provider = new TestProvider(AiProviderSdk.CurrentVersion, available: true, imageProcessing: true);
        await using var manager = new ProviderManager(new[] { provider });

        AiProviderHealth health = await manager.InitializeAsync("test", new(AiProviderSdk.CurrentVersion), requireImageProcessing: true);
        IReadOnlyList<AiProviderIdentity> discovered = await manager.DiscoverAsync();
        await manager.ReleaseResourcesAsync();
        await manager.ShutdownAsync();

        Assert.True(health.IsReady);
        Assert.Single(discovered);
        Assert.Null(manager.ActiveProvider); // Shutdown clears active provider.
        Assert.Equal(1, provider.InitializeCalls);
        Assert.Equal(1, provider.ReleaseCalls);
        Assert.True(provider.ShutdownCalls >= 1);
        Assert.Contains("Provider: Test Provider", ProviderManager.FormatStartup(health));
    }

    [Fact]
    public async Task ProviderManagerRejectsSdkAndCapabilityMismatchesGracefully()
    {
        await using var versionManager = new ProviderManager(new[] { new TestProvider(new(2, 0), available: true, imageProcessing: true) });
        AiProviderHealth version = await versionManager.InitializeAsync("test", new(AiProviderSdk.CurrentVersion));
        Assert.False(version.IsReady);
        Assert.Contains("incompatible", version.Reason!, StringComparison.OrdinalIgnoreCase);

        await using var capabilityManager = new ProviderManager(new[] { new TestProvider(AiProviderSdk.CurrentVersion, available: true, imageProcessing: false) });
        AiProviderHealth capability = await capabilityManager.InitializeAsync("test", new(AiProviderSdk.CurrentVersion), requireImageProcessing: true);
        Assert.False(capability.IsReady);
        Assert.Contains("image processing", capability.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderContractsSupportModelsErrorsProgressCancellationAndImageOwnership()
    {
        var provider = new TestProvider(AiProviderSdk.CurrentVersion, available: true, imageProcessing: true);
        await provider.InitializeAsync(new(AiProviderSdk.CurrentVersion));
        AiProviderModel model = Assert.Single(await provider.EnumerateModelsAsync());
        AiProviderModelHandle handle = await provider.LoadModelAsync(model);
        var progress = new List<AiProviderProgress>();
        using var image = new AiProviderImage(2, 2, AiProviderPixelFormat.Rgb24, AiProviderColorSpace.Srgb, 6, new byte[12], new Dictionary<string, string>(), AiProviderMemoryOwnership.CallerOwned);
        AiProviderInferenceResult result = await provider.ProcessImageAsync(new(handle, image, new Progress<AiProviderProgress>(progress.Add)));
        await provider.CancelAsync();

        Assert.True(result.Success);
        Assert.Equal(AiProviderMemoryOwnership.ProviderOwned, result.Output!.Ownership);
        Assert.NotEmpty(progress);
        Assert.True(provider.CancelCalls > 0);
        result.Output.Dispose();
    }

    [Fact]
    public async Task NcnnProviderMigrationKeepsUnsupportedImageInputsOutOfTheExistingPipeline()
    {
        var backend = new FakeBackend();
        var provider = new NcnnAiProvider(backend, new VideoRestorationSettings { AiMode = AiRestorationMode.Animation });
        await provider.InitializeAsync(new(AiProviderSdk.CurrentVersion));
        AiProviderModel model = Assert.Single(await provider.EnumerateModelsAsync());
        AiProviderInferenceResult result = await provider.ProcessImageAsync(new(await provider.LoadModelAsync(model), new AiProviderImage(1, 1, AiProviderPixelFormat.Rgb24, AiProviderColorSpace.Srgb, 3, new byte[3])));

        Assert.False(result.Success);
        Assert.Equal(AiProviderErrorCode.InvalidImage, result.Error!.Code);
        Assert.Equal(0, backend.ProcessCalls);
        await provider.DisposeAsync();
    }

    private sealed class TestProvider : IAiProvider
    {
        private readonly AiProviderSdkVersion _version; private readonly bool _available, _imageProcessing;
        public TestProvider(AiProviderSdkVersion version, bool available, bool imageProcessing) { _version = version; _available = available; _imageProcessing = imageProcessing; }
        public int InitializeCalls, ShutdownCalls, ReleaseCalls, CancelCalls;
        public AiProviderIdentity Identity => new("test", "Test Provider", "1", "Tests");
        public Task<AiProviderError?> InitializeAsync(AiProviderInitialization initialization, CancellationToken cancellationToken = default) { InitializeCalls++; return Task.FromResult<AiProviderError?>(null); }
        public Task ShutdownAsync(CancellationToken cancellationToken = default) { ShutdownCalls++; return Task.CompletedTask; }
        public AiProviderSdkVersion QueryVersion() => _version;
        public Task<AiProviderCapabilities> QueryCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiProviderCapabilities(_available, _imageProcessing, true, true, true, true, true, false, new[] { "Image processing" }));
        public Task<IReadOnlyList<AiProviderModel>> EnumerateModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AiProviderModel>>(new[] { new AiProviderModel("model", "Model", "1", 2, "Animation", new[] { "test" }, "hash", new Dictionary<string, string>()) });
        public Task<AiProviderError?> ValidateModelAsync(AiProviderModel model, CancellationToken cancellationToken = default) => Task.FromResult<AiProviderError?>(null);
        public Task<AiProviderModelHandle> LoadModelAsync(AiProviderModel model, CancellationToken cancellationToken = default) => Task.FromResult(new AiProviderModelHandle("model", model));
        public Task UnloadModelAsync(AiProviderModelHandle model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AiProviderInferenceResult> ProcessImageAsync(AiProviderInferenceRequest request, CancellationToken cancellationToken = default) { request.Progress?.Report(new("process", 1, "done")); return Task.FromResult(new AiProviderInferenceResult(new AiProviderImage(request.Input.Width, request.Input.Height, request.Input.PixelFormat, request.Input.ColorSpace, request.Input.Stride, request.Input.Bytes.ToArray(), ownership: AiProviderMemoryOwnership.ProviderOwned), TimeSpan.Zero)); }
        public Task CancelAsync(CancellationToken cancellationToken = default) { CancelCalls++; return Task.CompletedTask; }
        public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) { ReleaseCalls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<AiProviderDiagnostic>> QueryDiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AiProviderDiagnostic>>(new AiProviderDiagnostic[] { new("test", "diagnostic", DateTimeOffset.UtcNow) });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBackend : IAiRestorationBackend
    {
        public int ProcessCalls;
        public string Id => "ncnn-vulkan"; public string DisplayName => "NCNN Vulkan";
        public Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new AiBackendMetadata(Id, DisplayName, "1", true, true, null, true, true, true, true, true, Array.Empty<string>()));
        public Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
        {
            var model = new AiRestorationModel("model", "Model", AiRestorationMode.Animation, new[] { AiRestorationScale.X2 }, "C:\\models", "C:\\models\\model.param", "C:\\models\\model.bin", Id, "model");
            return Task.FromResult(new AiRestorationCapabilities(true, Id, "", "test", true, new[] { "Auto" }, new[] { model }, null));
        }
        public Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null) { ProcessCalls++; return Task.CompletedTask; }
        public Task<AiDirectoryProcessDiagnostic> ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null) => throw new NotSupportedException();
    }
}
