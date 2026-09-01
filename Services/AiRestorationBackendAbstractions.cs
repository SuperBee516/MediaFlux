using System.Diagnostics;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Persisted preference for the optional AI restoration engine.</summary>
public enum AiBackendSelection { Auto, NcnnVulkan, NvidiaTensorRt }

/// <summary>Stable, UI-facing description of a discovered AI backend.</summary>
public sealed record AiBackendMetadata(
    string Id,
    string DisplayName,
    string Version,
    bool IsAvailable,
    bool IsReady,
    string? Reason,
    bool SupportsStillPreview,
    bool SupportsMotionPreview,
    bool SupportsFullEncode,
    bool SupportsModelDiscovery,
    bool SupportsGpuAcceleration,
    IReadOnlyList<string> Diagnostics);

/// <summary>Typed contract used by preview and encode orchestration. Implementations own their runtime and command details.</summary>
public interface IAiRestorationBackend
{
    string Id { get; }
    string DisplayName { get; }
    Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default);
    Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default);
    Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default);
    Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default);
    Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null);
    Task ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null);
}

/// <summary>Discovery-only TensorRT placeholder. It never executes inference.</summary>
public sealed class TensorRtAiRestorationBackend : IAiRestorationBackend
{
    private readonly TensorRtRuntimeService _runtime;
    private readonly TensorRtEngineManager _engines;
    private readonly AiModelManager _models;
    private readonly string _onnxDirectory;

    public TensorRtAiRestorationBackend(string applicationDirectory, Func<bool>? nvidiaGpuPresent = null, Func<IEnumerable<string>>? runtimeDirectories = null)
    {
        _runtime = new TensorRtRuntimeService(applicationDirectory, nvidiaGpuPresent, runtimeDirectories);
        _engines = new TensorRtEngineManager(Path.Combine(applicationDirectory, "tensorrt-engines"), _runtime);
        _models = new AiModelManager();
        _onnxDirectory = Path.Combine(applicationDirectory, "tensorrt-models");
    }

    public string Id => "nvidia-tensorrt";
    public string DisplayName => "NVIDIA TensorRT";

    public async Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        TensorRtRuntimeInfo runtime = await _runtime.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TensorRtEngineDiscoveryItem> engines = await _engines.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        AiModelDiscoverySummary onnxModels = await _models.DiscoverOnnxAsync(_onnxDirectory, cancellationToken).ConfigureAwait(false);
        int validEngines = engines.Count(engine => engine.Validation.IsValid);
        string reason = !runtime.IsReady ? runtime.Reason ?? "TensorRT runtime is unavailable." : "TensorRT inference is not implemented in this MediaFlux phase.";
        var diagnostics = runtime.Diagnostics.Append($"TensorRT ONNX models: {onnxModels.Available.Count} available, {onnxModels.Invalid.Count} invalid").Append($"Available engines: {validEngines}/{engines.Count}").Append("Ready: No (inference is not implemented)").ToArray();
        return new(Id, DisplayName, runtime.TensorRtVersion, runtime.NvidiaGpuDetected || runtime.CudaRuntimeDetected || runtime.TensorRtRuntimeDetected || engines.Count > 0 || onnxModels.Available.Count > 0, false, reason, false, false, false, onnxModels.Available.Count > 0, runtime.NvidiaGpuDetected, diagnostics);
    }

    public async Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        AiBackendMetadata metadata = await GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false);
        return new(metadata.IsAvailable, Id, "", "tensorrt-discovery", false, Array.Empty<string>(), Array.Empty<AiRestorationModel>(), metadata.Reason);
    }
    public async Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new AiRestorationValidationException((await GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false)).Reason!);
    public async Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => throw new AiRestorationValidationException((await GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false)).Reason!);
    public Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null) => throw new AiRestorationValidationException("TensorRT inference is not implemented.");
    public Task ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null) => throw new AiRestorationValidationException("TensorRT inference is not implemented.");

}

/// <summary>Discovers engines, applies Auto/manual selection rules, and emits concise startup health diagnostics.</summary>
public sealed class AiBackendManager
{
    private readonly IAiRestorationBackend _ncnn;
    private readonly IAiRestorationBackend _tensorRt;
    private readonly Action<string>? _log;
    public AiBackendManager(string applicationDirectory, IMediaToolProcessRunner? runner = null, Action<string>? log = null, IAiRestorationBackend? ncnn = null, IAiRestorationBackend? tensorRt = null)
    { _log = log; _ncnn = ncnn ?? new AiRestorationBackendService(applicationDirectory, runner, log); _tensorRt = tensorRt ?? new TensorRtAiRestorationBackend(applicationDirectory); }

    public async Task<IReadOnlyList<AiBackendMetadata>> DiscoverAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => new[] { await _ncnn.GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false), await _tensorRt.GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false) };
    public async Task<IAiRestorationBackend> SelectAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AiBackendMetadata> backends = await DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
        AiBackendMetadata ncnn = backends[0], tensorRt = backends[1];
        IAiRestorationBackend selected = settings.AiBackendSelection switch
        {
            AiBackendSelection.NcnnVulkan when ncnn.IsReady => _ncnn,
            AiBackendSelection.NvidiaTensorRt when tensorRt.IsReady => _tensorRt,
            AiBackendSelection.NcnnVulkan => throw new AiRestorationValidationException(ncnn.Reason ?? "NCNN Vulkan is unavailable."),
            AiBackendSelection.NvidiaTensorRt => throw new AiRestorationValidationException(tensorRt.Reason ?? "NVIDIA TensorRT is unavailable."),
            _ when tensorRt.IsReady => _tensorRt,
            _ => _ncnn
        };
        _log?.Invoke(FormatStartup(selected.DisplayName, ncnn, tensorRt));
        return selected;
    }
    public static string FormatStartup(string selected, AiBackendMetadata ncnn, AiBackendMetadata tensorRt) => $"AI Backend{Environment.NewLine}Selected: {selected}{Environment.NewLine}Available:{Environment.NewLine}{(ncnn.IsReady ? "✓" : "✗")} {ncnn.DisplayName}{Environment.NewLine}TensorRT: {(tensorRt.IsAvailable ? "Detected" : "Not Installed")}{Environment.NewLine}TensorRT Runtime{Environment.NewLine}{string.Join(Environment.NewLine, tensorRt.Diagnostics)}{Environment.NewLine}Ready: {(tensorRt.IsReady ? "Yes" : "No")}{Environment.NewLine}Reason: {tensorRt.Reason}";
}
