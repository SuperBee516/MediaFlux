using System.Buffers;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Versioned, backend-neutral provider SDK. Native ABI rules are documented in Documentation/NativeAiProviderAbi.md.</summary>
public static class AiProviderSdk { public static readonly AiProviderSdkVersion CurrentVersion = new(1, 0); }
public sealed record AiProviderSdkVersion(int Major, int Minor) { public bool IsCompatibleWith(AiProviderSdkVersion requested) => Major == requested.Major && Minor >= requested.Minor; public override string ToString() => $"{Major}.{Minor}"; }
public sealed record AiProviderIdentity(string Id, string DisplayName, string Version, string Vendor);
public sealed record AiProviderCapabilities(bool IsAvailable, bool SupportsImageProcessing, bool SupportsModelEnumeration, bool SupportsCancellation, bool SupportsProgress, bool SupportsFp16, bool SupportsFp32, bool SupportsInt8, IReadOnlyList<string> Features);
public sealed record AiProviderModel(string Id, string DisplayName, string Version, int Scale, string Mode, IReadOnlyList<string> CompatibleProviders, string Hash, IReadOnlyDictionary<string, string> Metadata);
public sealed record AiProviderModelHandle(string Value, AiProviderModel Model);
public enum AiProviderPixelFormat { Unknown, Rgb24, Rgba32, Bgr24, Bgra32, Gray8 }
public enum AiProviderColorSpace { Unknown, Srgb, LinearRgb, Rec709 }
public enum AiProviderMemoryOwnership { Borrowed, ProviderOwned, CallerOwned }
public sealed class AiProviderImage : IDisposable
{
    private readonly IMemoryOwner<byte>? _owner;
    public AiProviderImage(int width, int height, AiProviderPixelFormat pixelFormat, AiProviderColorSpace colorSpace, int stride, ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, string>? metadata = null, AiProviderMemoryOwnership ownership = AiProviderMemoryOwnership.Borrowed, IMemoryOwner<byte>? owner = null)
    { Width = width; Height = height; PixelFormat = pixelFormat; ColorSpace = colorSpace; Stride = stride; Bytes = bytes; Metadata = metadata ?? new Dictionary<string, string>(); Ownership = ownership; _owner = owner; }
    public int Width { get; } public int Height { get; } public AiProviderPixelFormat PixelFormat { get; } public AiProviderColorSpace ColorSpace { get; } public int Stride { get; } public ReadOnlyMemory<byte> Bytes { get; } public IReadOnlyDictionary<string, string> Metadata { get; } public AiProviderMemoryOwnership Ownership { get; }
    public void Dispose() => _owner?.Dispose();
}
public sealed record AiProviderInferenceRequest(AiProviderModelHandle Model, AiProviderImage Input, IProgress<AiProviderProgress>? Progress = null);
public sealed record AiProviderInferenceResult(AiProviderImage? Output, TimeSpan Elapsed, AiProviderError? Error = null) { public bool Success => Error is null && Output is not null; }
public sealed record AiProviderProgress(string Stage, double? Fraction, string Message);
public sealed record AiProviderDiagnostic(string Category, string Message, DateTimeOffset Timestamp);
public enum AiProviderErrorCode { UnsupportedSdkVersion, Unavailable, CapabilityMismatch, InvalidModel, InvalidImage, Cancelled, ProcessingFailed, LifecycleError }
public sealed record AiProviderError(AiProviderErrorCode Code, string Message, bool IsTransient = false);
public sealed record AiProviderInitialization(AiProviderSdkVersion RequestedSdkVersion, IReadOnlyDictionary<string, string>? Options = null);
public sealed record AiProviderHealth(AiProviderIdentity Identity, AiProviderSdkVersion NegotiatedSdkVersion, AiProviderCapabilities Capabilities, bool IsReady, string? Reason, IReadOnlyList<AiProviderDiagnostic> Diagnostics);

public interface IAiProvider : IAsyncDisposable
{
    Task<AiProviderError?> InitializeAsync(AiProviderInitialization initialization, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
    AiProviderSdkVersion QueryVersion();
    Task<AiProviderCapabilities> QueryCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiProviderModel>> EnumerateModelsAsync(CancellationToken cancellationToken = default);
    Task<AiProviderError?> ValidateModelAsync(AiProviderModel model, CancellationToken cancellationToken = default);
    Task<AiProviderModelHandle> LoadModelAsync(AiProviderModel model, CancellationToken cancellationToken = default);
    Task UnloadModelAsync(AiProviderModelHandle model, CancellationToken cancellationToken = default);
    Task<AiProviderInferenceResult> ProcessImageAsync(AiProviderInferenceRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
    Task ReleaseResourcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiProviderDiagnostic>> QueryDiagnosticsAsync(CancellationToken cancellationToken = default);
    AiProviderIdentity Identity { get; }
}

/// <summary>Provider discovery, SDK/capability negotiation, lifecycle, health, and diagnostics.</summary>
public sealed class ProviderManager : IAsyncDisposable
{
    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly Action<string>? _log;
    private IAiProvider? _active;
    public ProviderManager(IEnumerable<IAiProvider> providers, Action<string>? log = null) { _providers = providers.ToArray(); _log = log; }
    public IAiProvider? ActiveProvider => _active;
    public async Task<IReadOnlyList<AiProviderIdentity>> DiscoverAsync() => await Task.FromResult(_providers.Select(provider => provider.Identity).ToArray()).ConfigureAwait(false);
    public async Task<AiProviderHealth> InitializeAsync(string providerId, AiProviderInitialization initialization, bool requireImageProcessing = false, CancellationToken cancellationToken = default)
    {
        IAiProvider provider = _providers.FirstOrDefault(item => item.Identity.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)) ?? throw new AiRestorationValidationException($"AI provider '{providerId}' was not found.");
        AiProviderSdkVersion version = provider.QueryVersion();
        if (!version.IsCompatibleWith(initialization.RequestedSdkVersion)) return await HealthAsync(provider, version, new(false, false, false, false, false, false, false, false, Array.Empty<string>()), false, $"Provider SDK {version} is incompatible with requested SDK {initialization.RequestedSdkVersion}.", cancellationToken).ConfigureAwait(false);
        AiProviderError? error = await provider.InitializeAsync(initialization, cancellationToken).ConfigureAwait(false);
        AiProviderCapabilities capabilities = await provider.QueryCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        string? reason = error?.Message ?? (!capabilities.IsAvailable ? "Provider is unavailable." : requireImageProcessing && !capabilities.SupportsImageProcessing ? "Provider does not support image processing." : null);
        bool ready = reason is null;
        if (ready) _active = provider;
        AiProviderHealth health = await HealthAsync(provider, version, capabilities, ready, reason, cancellationToken).ConfigureAwait(false);
        _log?.Invoke(FormatStartup(health)); return health;
    }
    public async Task ShutdownAsync(CancellationToken cancellationToken = default) { if (_active is null) return; await _active.ShutdownAsync(cancellationToken).ConfigureAwait(false); _active = null; }
    public async Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) { if (_active is not null) await _active.ReleaseResourcesAsync(cancellationToken).ConfigureAwait(false); }
    private static async Task<AiProviderHealth> HealthAsync(IAiProvider provider, AiProviderSdkVersion version, AiProviderCapabilities capabilities, bool ready, string? reason, CancellationToken token) => new(provider.Identity, version, capabilities, ready, reason, await provider.QueryDiagnosticsAsync(token).ConfigureAwait(false));
    public static string FormatStartup(AiProviderHealth health) => $"AI Provider SDK Version: {AiProviderSdk.CurrentVersion}{Environment.NewLine}Provider: {health.Identity.DisplayName}{Environment.NewLine}Capabilities: {string.Join(", ", health.Capabilities.Features)}{Environment.NewLine}Negotiated SDK: {health.NegotiatedSdkVersion}{Environment.NewLine}Health: {(health.IsReady ? "Healthy" : "Unavailable")}{Environment.NewLine}Ready: {(health.IsReady ? "Yes" : "No")}{(health.Reason is null ? "" : Environment.NewLine + "Reason: " + health.Reason)}";
    public async ValueTask DisposeAsync() { await ShutdownAsync().ConfigureAwait(false); foreach (IAiProvider provider in _providers) await provider.DisposeAsync().ConfigureAwait(false); }
}

/// <summary>NCNN adapter for the provider SDK. Its existing command execution remains in AiRestorationBackendService.</summary>
public sealed class NcnnAiProvider : IAiProvider
{
    private readonly IAiRestorationBackend _backend; private readonly VideoRestorationSettings _settings; private readonly List<AiProviderDiagnostic> _diagnostics = new(); private CancellationTokenSource? _cancel; private bool _initialized;
    public NcnnAiProvider(IAiRestorationBackend backend, VideoRestorationSettings settings) { _backend = backend; _settings = settings.Clone(); }
    public AiProviderIdentity Identity => new("ncnn-vulkan", "NCNN Vulkan", "Adapter", "MediaFlux");
    public Task<AiProviderError?> InitializeAsync(AiProviderInitialization initialization, CancellationToken cancellationToken = default) { _initialized = true; _cancel = new(); _diagnostics.Add(new("Lifecycle", "NCNN provider initialized.", DateTimeOffset.UtcNow)); return Task.FromResult<AiProviderError?>(null); }
    public Task ShutdownAsync(CancellationToken cancellationToken = default) { _initialized = false; _cancel?.Dispose(); _cancel = null; _diagnostics.Add(new("Lifecycle", "NCNN provider shut down.", DateTimeOffset.UtcNow)); return Task.CompletedTask; }
    public AiProviderSdkVersion QueryVersion() => AiProviderSdk.CurrentVersion;
    public async Task<AiProviderCapabilities> QueryCapabilitiesAsync(CancellationToken cancellationToken = default) { AiBackendMetadata metadata = await _backend.GetMetadataAsync(_settings, cancellationToken).ConfigureAwait(false); return new(metadata.IsAvailable, true, true, true, true, false, true, false, new[] { "Image processing", "Model enumeration", "Cancellation", "Progress" }); }
    public async Task<IReadOnlyList<AiProviderModel>> EnumerateModelsAsync(CancellationToken cancellationToken = default)
    {
        AiRestorationCapabilities capabilities = await _backend.GetCapabilitiesAsync(_settings, cancellationToken).ConfigureAwait(false);
        return capabilities.Models.Select(model => new AiProviderModel(model.Id, model.DisplayName, "ncnn-model-pair-v1", (int)model.SupportedScales[0], model.Category.ToString(), new[] { "ncnn-vulkan" }, "", new Dictionary<string, string> { ["backendModelName"] = model.BackendModelName })).ToArray();
    }
    public async Task<AiProviderError?> ValidateModelAsync(AiProviderModel model, CancellationToken cancellationToken = default) { AiProviderModel? found = (await EnumerateModelsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase) && item.Scale == model.Scale && item.Mode.Equals(model.Mode, StringComparison.OrdinalIgnoreCase)); return found is null ? new(AiProviderErrorCode.InvalidModel, "NCNN model is unavailable.") : null; }
    public async Task<AiProviderModelHandle> LoadModelAsync(AiProviderModel model, CancellationToken cancellationToken = default) { AiProviderError? error = await ValidateModelAsync(model, cancellationToken).ConfigureAwait(false); if (error is not null) throw new AiRestorationValidationException(error.Message); return new(model.Id + "|" + model.Scale, model); }
    public Task UnloadModelAsync(AiProviderModelHandle model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task<AiProviderInferenceResult> ProcessImageAsync(AiProviderInferenceRequest request, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return new(null, TimeSpan.Zero, new(AiProviderErrorCode.LifecycleError, "Provider is not initialized."));
        if (!request.Input.Metadata.TryGetValue("mediaflux.png.path", out string? input) || !request.Input.Metadata.TryGetValue("mediaflux.png.output-path", out string? output)) return new(null, TimeSpan.Zero, new(AiProviderErrorCode.InvalidImage, "NCNN provider requires MediaFlux-owned PNG staging paths."));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel?.Token ?? CancellationToken.None);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew(); AiRestorationSession session = await _backend.CreateSessionAsync(_settings, linked.Token).ConfigureAwait(false); await _backend.ProcessFrameAsync(session, _settings, input, output, linked.Token).ConfigureAwait(false); stopwatch.Stop();
            byte[] bytes = await File.ReadAllBytesAsync(output, linked.Token).ConfigureAwait(false); var image = new AiProviderImage(request.Input.Width * (int)_settings.AiScale, request.Input.Height * (int)_settings.AiScale, request.Input.PixelFormat, request.Input.ColorSpace, request.Input.Stride * (int)_settings.AiScale, bytes, new Dictionary<string, string> { ["mediaflux.png.path"] = output }, AiProviderMemoryOwnership.ProviderOwned);
            return new(image, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) { return new(null, TimeSpan.Zero, new(AiProviderErrorCode.Cancelled, "NCNN image processing was cancelled.")); }
        catch (Exception ex) { return new(null, TimeSpan.Zero, new(AiProviderErrorCode.ProcessingFailed, ex.Message)); }
    }
    public Task CancelAsync(CancellationToken cancellationToken = default) { _cancel?.Cancel(); return Task.CompletedTask; }
    public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) { _diagnostics.Add(new("Lifecycle", "NCNN provider resources released.", DateTimeOffset.UtcNow)); return Task.CompletedTask; }
    public Task<IReadOnlyList<AiProviderDiagnostic>> QueryDiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AiProviderDiagnostic>>(_diagnostics.ToArray());
    public async ValueTask DisposeAsync() { await ShutdownAsync().ConfigureAwait(false); }
}

/// <summary>Provider SDK adapter for the TensorRT backend and its validated engine lifecycle.</summary>
public sealed class TensorRtAiProvider : IAiProvider
{
    private readonly IAiRestorationBackend _backend; private readonly VideoRestorationSettings _settings; private readonly List<AiProviderDiagnostic> _diagnostics = new(); private readonly Dictionary<string, AiRestorationSession> _sessions = new(StringComparer.OrdinalIgnoreCase); private CancellationTokenSource? _cancel; private bool _initialized;
    public TensorRtAiProvider(IAiRestorationBackend backend, VideoRestorationSettings settings) { _backend = backend; _settings = settings.Clone(); }
    public AiProviderIdentity Identity => new("nvidia-tensorrt", "NVIDIA TensorRT", "Adapter", "NVIDIA/MediaFlux");
    public AiProviderSdkVersion QueryVersion() => AiProviderSdk.CurrentVersion;
    public async Task<AiProviderError?> InitializeAsync(AiProviderInitialization initialization, CancellationToken cancellationToken = default)
    {
        AiBackendMetadata metadata = await _backend.GetMetadataAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!metadata.IsReady) return new(AiProviderErrorCode.Unavailable, metadata.Reason ?? "TensorRT provider is unavailable.");
        _cancel = new(); _initialized = true; _diagnostics.Add(new("Lifecycle", $"TensorRT provider initialized; runtime={metadata.Version}.", DateTimeOffset.UtcNow)); return null;
    }
    public async Task<AiProviderCapabilities> QueryCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        AiBackendMetadata metadata = await _backend.GetMetadataAsync(_settings, cancellationToken).ConfigureAwait(false);
        bool fp16 = metadata.Diagnostics.Any(line => line.Contains("FP16", StringComparison.OrdinalIgnoreCase));
        return new(metadata.IsReady, true, true, true, true, fp16, true, false, new[] { "Image processing", "Engine build/load/cache", "Dynamic shapes", fp16 ? "FP16" : "FP32", "Cancellation", "Progress" });
    }
    public async Task<IReadOnlyList<AiProviderModel>> EnumerateModelsAsync(CancellationToken cancellationToken = default)
    {
        AiRestorationCapabilities capabilities = await _backend.GetCapabilitiesAsync(_settings, cancellationToken).ConfigureAwait(false);
        return capabilities.Models.Select(model => new AiProviderModel(model.Id, model.DisplayName, "onnx", (int)model.SupportedScales[0], model.Category.ToString(), new[] { Identity.Id }, "", new Dictionary<string, string> { ["onnxPath"] = model.ParamPath })).ToArray();
    }
    public async Task<AiProviderError?> ValidateModelAsync(AiProviderModel model, CancellationToken cancellationToken = default) => (await EnumerateModelsAsync(cancellationToken).ConfigureAwait(false)).Any(item => item.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase) && item.Scale == model.Scale && item.Mode.Equals(model.Mode, StringComparison.OrdinalIgnoreCase)) ? null : new(AiProviderErrorCode.InvalidModel, "TensorRT ONNX model is unavailable or invalid.");
    public async Task<AiProviderModelHandle> LoadModelAsync(AiProviderModel model, CancellationToken cancellationToken = default)
    {
        if (!_initialized) throw new AiRestorationValidationException("TensorRT provider is not initialized.");
        AiProviderError? error = await ValidateModelAsync(model, cancellationToken).ConfigureAwait(false); if (error is not null) throw new AiRestorationValidationException(error.Message);
        VideoRestorationSettings settings = _settings.Clone(); settings.AiModelId = model.Id; settings.AiMode = Enum.Parse<AiRestorationMode>(model.Mode); settings.AiScale = (AiRestorationScale)model.Scale;
        AiRestorationSession session = await _backend.CreateSessionAsync(settings, cancellationToken).ConfigureAwait(false); string key = model.Id + "|" + model.Scale + "|" + Guid.NewGuid().ToString("N"); _sessions[key] = session;
        _diagnostics.Add(new("Engine", $"Loaded {model.Id}; precision={session.Runtime?.Precision}; cache={session.Runtime?.CacheState}.", DateTimeOffset.UtcNow)); return new(key, model);
    }
    public Task UnloadModelAsync(AiProviderModelHandle model, CancellationToken cancellationToken = default) { _sessions.Remove(model.Value); return Task.CompletedTask; }
    public async Task<AiProviderInferenceResult> ProcessImageAsync(AiProviderInferenceRequest request, CancellationToken cancellationToken = default)
    {
        if (!_initialized || !_sessions.TryGetValue(request.Model.Value, out AiRestorationSession? session)) return new(null, TimeSpan.Zero, new(AiProviderErrorCode.LifecycleError, "TensorRT model is not loaded."));
        if (!request.Input.Metadata.TryGetValue("mediaflux.png.path", out string? input) || !request.Input.Metadata.TryGetValue("mediaflux.png.output-path", out string? output)) return new(null, TimeSpan.Zero, new(AiProviderErrorCode.InvalidImage, "TensorRT provider requires MediaFlux-owned PNG staging paths."));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel?.Token ?? CancellationToken.None);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew(); VideoRestorationSettings settings = _settings.Clone(); settings.AiModelId = request.Model.Model.Id; settings.AiMode = Enum.Parse<AiRestorationMode>(request.Model.Model.Mode); settings.AiScale = (AiRestorationScale)request.Model.Model.Scale;
            await _backend.ProcessFrameAsync(session, settings, input, output, linked.Token).ConfigureAwait(false); stopwatch.Stop(); byte[] bytes = await File.ReadAllBytesAsync(output, linked.Token).ConfigureAwait(false);
            return new(new AiProviderImage(request.Input.Width * request.Model.Model.Scale, request.Input.Height * request.Model.Model.Scale, request.Input.PixelFormat, request.Input.ColorSpace, request.Input.Stride * request.Model.Model.Scale, bytes, new Dictionary<string, string> { ["mediaflux.png.path"] = output }, AiProviderMemoryOwnership.ProviderOwned), stopwatch.Elapsed);
        }
        catch (OperationCanceledException) { return new(null, TimeSpan.Zero, new(AiProviderErrorCode.Cancelled, "TensorRT inference was cancelled.")); }
        catch (Exception ex) { return new(null, TimeSpan.Zero, new(AiProviderErrorCode.ProcessingFailed, ex.Message)); }
    }
    public Task CancelAsync(CancellationToken cancellationToken = default) { _cancel?.Cancel(); return Task.CompletedTask; }
    public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) { _sessions.Clear(); _diagnostics.Add(new("Lifecycle", "TensorRT provider resources released.", DateTimeOffset.UtcNow)); return Task.CompletedTask; }
    public async Task ShutdownAsync(CancellationToken cancellationToken = default) { await ReleaseResourcesAsync(cancellationToken).ConfigureAwait(false); _initialized = false; _cancel?.Dispose(); _cancel = null; }
    public Task<IReadOnlyList<AiProviderDiagnostic>> QueryDiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AiProviderDiagnostic>>(_diagnostics.ToArray());
    public async ValueTask DisposeAsync() { await ShutdownAsync().ConfigureAwait(false); }
}
