using System.Diagnostics;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record AiDirectoryProcessDiagnostic(
    string CommandLine, int ExitCode, TimeSpan Elapsed, string StandardOutput, string StandardError,
    int ExpectedFrames, int RestoredFrames, string? FirstOutputFileName, string? LastOutputFileName,
    DateTimeOffset? FirstOutputTimestamp, DateTimeOffset? LastOutputTimestamp, bool TimedOut = false, string? ExecutablePath = null);

/// <summary>Persisted preference for the optional AI restoration engine.</summary>
public enum AiBackendSelection { Auto, NcnnVulkan, NvidiaTensorRt, DirectMl, Cpu }

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
    Task<AiDirectoryProcessDiagnostic> ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null);
}

/// <summary>TensorRT backend over the versioned MediaFlux native process bridge.</summary>
public sealed class TensorRtAiRestorationBackend : IAiRestorationBackend
{
    private readonly TensorRtRuntimeService _runtime;
    private readonly TensorRtEngineManager _engines;
    private readonly AiModelManager _models;
    private readonly string _onnxDirectory;
    private readonly TensorRtProcessBridge _bridge;
    private readonly Action<string>? _log;

    public TensorRtAiRestorationBackend(string applicationDirectory, Func<bool>? nvidiaGpuPresent = null, Func<IEnumerable<string>>? runtimeDirectories = null, Func<TensorRtGpuInfo>? gpuInfo = null, IMediaToolProcessRunner? runner = null, string? bridgePath = null, string? engineDirectory = null, Action<string>? log = null)
    {
        _runtime = new TensorRtRuntimeService(applicationDirectory, nvidiaGpuPresent, runtimeDirectories, gpuInfo);
        _engines = new TensorRtEngineManager(engineDirectory ?? AppPaths.TensorRtEnginesDirectory, _runtime, log: log);
        _models = new AiModelManager(log: log);
        _onnxDirectory = Path.Combine(applicationDirectory, "tensorrt-models");
        _bridge = new TensorRtProcessBridge(bridgePath ?? Path.Combine(applicationDirectory, "mediaflux-tensorrt.exe"), runner, log);
        _log = log;
    }

    public string Id => "nvidia-tensorrt";
    public string DisplayName => "NVIDIA TensorRT";

    public async Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        TensorRtRuntimeInfo runtime = await _runtime.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        AiModelDiscoverySummary onnxModels = await _models.DiscoverOnnxAsync(_onnxDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TensorRtEngineDiscoveryItem> engines = await _engines.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        int validEngines = engines.Count(engine => engine.Validation.IsValid);
        bool ready = runtime.IsReady && _bridge.IsAvailable && onnxModels.Available.Count > 0;
        string? reason = !runtime.IsReady ? runtime.Reason ?? "TensorRT runtime is unavailable." : !_bridge.IsAvailable ? $"TensorRT provider bridge is missing: '{_bridge.ExecutablePath}'." : onnxModels.Available.Count == 0 ? "No validated TensorRT ONNX models are installed." : null;
        var diagnostics = runtime.Diagnostics.Append($"TensorRT bridge: {(_bridge.IsAvailable ? "Detected" : "Missing")}").Append($"TensorRT ONNX models: {onnxModels.Available.Count} available, {onnxModels.Invalid.Count} invalid").Append($"Available engines: {validEngines}/{engines.Count}").Append($"Ready: {(ready ? "Yes" : "No")}").ToArray();
        return new(Id, DisplayName, runtime.TensorRtVersion, runtime.NvidiaGpuDetected || runtime.CudaRuntimeDetected || runtime.TensorRtRuntimeDetected || _bridge.IsAvailable || engines.Count > 0 || onnxModels.Available.Count > 0, ready, reason, ready, ready, ready, true, runtime.NvidiaGpuDetected, diagnostics);
    }

    public async Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        AiBackendMetadata metadata = await GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false);
        AiModelDiscoverySummary discovery = await _models.DiscoverOnnxAsync(_onnxDirectory, cancellationToken).ConfigureAwait(false);
        AiRestorationModel[] models = discovery.Available.Select(ToRestorationModel).ToArray();
        return new(metadata.IsReady, Id, _bridge.ExecutablePath, metadata.Version, metadata.SupportsGpuAcceleration, new[] { "Auto" }, models, metadata.Reason);
    }
    public async Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        AiRestorationCapabilities capabilities = await GetCapabilitiesAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!capabilities.IsAvailable) throw new AiRestorationValidationException(capabilities.Error ?? "TensorRT is unavailable.");
        AiRestorationModel? model = capabilities.Models.FirstOrDefault(item => item.Id.Equals(settings.AiModelId, StringComparison.OrdinalIgnoreCase) && item.Category == settings.AiMode && item.SupportedScales.Contains(settings.AiScale));
        if (model is null) throw new AiRestorationValidationException($"TensorRT ONNX model '{settings.AiModelId}' is unavailable for {settings.AiMode} {(int)settings.AiScale}x.");
        return model;
    }
    public async Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        AiRestorationModel model = await ValidateSelectionAsync(settings, cancellationToken).ConfigureAwait(false);
        AiModelValidationResult validation = await _models.ValidateOnnxAsync(model.ParamPath, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid) throw new AiRestorationValidationException(validation.Reason);
        TensorRtRuntimeInfo runtime = await _runtime.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        TensorRtPrecision precision = runtime.SupportedPrecisions.Contains(TensorRtPrecision.FP16) ? TensorRtPrecision.FP16 : TensorRtPrecision.FP32;
        TensorRtEngineResolution engine = await _engines.ResolveOrBuildAsync(validation.Model!, precision, new TensorRtDynamicShapeProfile(), _bridge, cancellationToken).ConfigureAwait(false);
        string identity = $"TensorRT {runtime.TensorRtVersion}|CUDA {runtime.CudaVersion}|CC {runtime.Gpu.ComputeCapability}|{validation.Model!.Identity.Hash}|{precision}";
        AiRestorationCapabilities capabilities = new(true, Id, _bridge.ExecutablePath, identity, true, new[] { runtime.Gpu.Name }, new[] { model }, null);
        AiRestorationModel resolved = model with { BinPath = engine.EnginePath, ResolvedModelName = validation.Model.Identity.LogicalModel };
        var descriptor = new AiBackendRuntimeDescriptor(runtime.TensorRtVersion, precision.ToString(), "Validated", engine.CacheState.ToString(), engine.CacheState == TensorRtEngineCacheState.Reused ? "Cached engine" : "Engine build");
        TensorRtRuntimeDiagnostics.Shared.Record(new(runtime.TensorRtVersion, runtime.CudaVersion, runtime.Gpu.Name, precision.ToString(), engine.EnginePath, engine.CacheState.ToString(), "Validated", null, DateTimeOffset.UtcNow));
        return new(capabilities, resolved, descriptor);
    }
    public async Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null)
    {
        string root = Path.Combine(Path.GetDirectoryName(stagingOutput)!, "tensorrt-frame-" + Guid.NewGuid().ToString("N")); string source = Path.Combine(root, "input"), output = Path.Combine(root, "output");
        Directory.CreateDirectory(source); Directory.CreateDirectory(output);
        try { string staged = Path.Combine(source, "frame-00000000.png"); File.Copy(input, staged, true); string restored = Path.Combine(output, "frame-00000000.png"); await ProcessDirectoryAsync(session, settings, source, output, new[] { restored }, null, cancellationToken, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false); File.Move(restored, stagingOutput, true); }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
    }
    public async Task<AiDirectoryProcessDiagnostic> ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null)
    {
        if (!Path.IsPathFullyQualified(inputDirectory) || !Path.IsPathFullyQualified(outputDirectory) || !Directory.Exists(inputDirectory)) throw new AiRestorationValidationException("TensorRT inference requires existing absolute input and owned output directories.");
        if (expectedOutputFrames.Count == 0 || expectedOutputFrames.Any(path => !Path.IsPathFullyQualified(path) || !string.Equals(Path.GetDirectoryName(path), outputDirectory, StringComparison.OrdinalIgnoreCase))) throw new AiRestorationValidationException("TensorRT output paths must belong to the owned output directory.");
        TensorRtEngineIdentity expected = (await _engines.LoadMetadataAsync(session.Model.BinPath, cancellationToken).ConfigureAwait(false))?.Identity ?? throw new AiRestorationValidationException("TensorRT engine metadata is missing.");
        using TensorRtEngineLease lease = await _engines.AcquireAsync(session.Model.BinPath, expected, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory); CleanupOutputs(outputDirectory);
        Stopwatch stopwatch = Stopwatch.StartNew(); MediaToolProcessResult result;
        try { result = await _bridge.RunDirectoryAsync(lease.Engine.Validation.EnginePath, inputDirectory, outputDirectory, cancellationToken, timeout).ConfigureAwait(false); }
        catch (Exception ex) { TensorRtRuntimeDiagnostics.Shared.RecordFailure(ex.Message); CleanupOutputs(outputDirectory); throw; }
        FileInfo[] files = Directory.EnumerateFiles(outputDirectory).Select(path => new FileInfo(path)).OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        var diagnostic = new AiDirectoryProcessDiagnostic($"{_bridge.ExecutablePath} run-directory --engine {session.Model.BinPath} --input {inputDirectory} --output {outputDirectory} --format png", result.ExitCode, stopwatch.Elapsed, result.StandardOutput, result.StandardError, expectedOutputFrames.Count, files.Length, files.FirstOrDefault()?.Name, files.LastOrDefault()?.Name, files.FirstOrDefault()?.LastWriteTimeUtc, files.LastOrDefault()?.LastWriteTimeUtc, result.TimedOut, _bridge.ExecutablePath);
        if (result.TimedOut || result.ExitCode != 0) { string failure = $"TensorRT inference failed. exitCode={result.ExitCode}; timedOut={result.TimedOut}; stderr={result.StandardError}"; TensorRtRuntimeDiagnostics.Shared.RecordFailure(failure); CleanupOutputs(outputDirectory); throw new AiRestorationValidationException(failure); }
        completedFrames?.Invoke(files.Length); _log?.Invoke($"[AI TensorRT Process] Engine: {Path.GetFileName(session.Model.BinPath)}; Precision: {session.Runtime?.Precision}; Expected Frames: {expectedOutputFrames.Count}; Produced: {files.Length}; Elapsed: {stopwatch.Elapsed:g}.");
        TensorRtRuntimeDiagnostics.Shared.RecordValidation("Inference completed; frame validation pending shared pipeline validation.");
        return diagnostic;
    }

    private static AiRestorationModel ToRestorationModel(AiManagedModel model) => new(model.Identity.LogicalModel, model.DisplayName, model.Identity.Mode, new[] { model.Identity.Scale }, Path.GetDirectoryName(model.PrimaryPath)!, model.PrimaryPath, "", "nvidia-tensorrt", model.Identity.LogicalModel);
    private static void CleanupOutputs(string outputDirectory) { try { foreach (string path in Directory.EnumerateFiles(outputDirectory)) File.Delete(path); } catch { } }

}

/// <summary>Auto-only failover wrapper. Explicit provider choices never use it.</summary>
public sealed class AutoFallbackAiRestorationBackend : IAiRestorationBackend
{
    private readonly IAiRestorationBackend _primary, _fallback; private readonly VideoRestorationSettings _settings; private readonly Action<string>? _log; private volatile bool _usingFallback; private AiRestorationSession? _fallbackSession;
    public AutoFallbackAiRestorationBackend(IAiRestorationBackend primary, IAiRestorationBackend fallback, VideoRestorationSettings settings, Action<string>? log) { _primary = primary; _fallback = fallback; _settings = settings.Clone(); _log = log; }
    public string Id => _usingFallback ? _fallback.Id : _primary.Id; public string DisplayName => _usingFallback ? _fallback.DisplayName : _primary.DisplayName;
    public Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => (_usingFallback ? _fallback : _primary).GetMetadataAsync(settings, cancellationToken);
    public Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => (_usingFallback ? _fallback : _primary).GetCapabilitiesAsync(settings, cancellationToken);
    public Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => (_usingFallback ? _fallback : _primary).ValidateSelectionAsync(settings, cancellationToken);
    public async Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        if (_usingFallback) return _fallbackSession ??= await _fallback.CreateSessionAsync(settings, cancellationToken).ConfigureAwait(false);
        try { return await _primary.CreateSessionAsync(settings, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return await ActivateFallbackAsync("TensorRT initialization failed: " + ex.Message, settings, cancellationToken).ConfigureAwait(false); }
    }
    public async Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null)
    {
        if (_usingFallback || session.Capabilities.BackendId.Equals(_fallback.Id, StringComparison.OrdinalIgnoreCase)) { AiRestorationSession fallback = _fallbackSession ??= session.Capabilities.BackendId.Equals(_fallback.Id, StringComparison.OrdinalIgnoreCase) ? session : await _fallback.CreateSessionAsync(settings, cancellationToken).ConfigureAwait(false); await _fallback.ProcessFrameAsync(fallback, settings, input, stagingOutput, cancellationToken, runtimeConfiguration).ConfigureAwait(false); return; }
        try { await _primary.ProcessFrameAsync(session, settings, input, stagingOutput, cancellationToken, runtimeConfiguration).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { AiRestorationSession fallback = await ActivateFallbackAsync("TensorRT inference failed: " + ex.Message, settings, cancellationToken).ConfigureAwait(false); await _fallback.ProcessFrameAsync(fallback, settings, input, stagingOutput, cancellationToken, runtimeConfiguration).ConfigureAwait(false); }
    }
    public async Task<AiDirectoryProcessDiagnostic> ProcessDirectoryAsync(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, IReadOnlyList<string> expectedOutputFrames, Action<int>? completedFrames, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null, TimeSpan? timeout = null)
    {
        if (_usingFallback || session.Capabilities.BackendId.Equals(_fallback.Id, StringComparison.OrdinalIgnoreCase)) { AiRestorationSession fallback = _fallbackSession ??= session.Capabilities.BackendId.Equals(_fallback.Id, StringComparison.OrdinalIgnoreCase) ? session : await _fallback.CreateSessionAsync(settings, cancellationToken).ConfigureAwait(false); return await _fallback.ProcessDirectoryAsync(fallback, settings, inputDirectory, outputDirectory, expectedOutputFrames, completedFrames, cancellationToken, runtimeConfiguration, timeout).ConfigureAwait(false); }
        try { return await _primary.ProcessDirectoryAsync(session, settings, inputDirectory, outputDirectory, expectedOutputFrames, completedFrames, cancellationToken, runtimeConfiguration, timeout).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { AiRestorationSession fallback = await ActivateFallbackAsync("TensorRT inference failed: " + ex.Message, settings, cancellationToken).ConfigureAwait(false); return await _fallback.ProcessDirectoryAsync(fallback, settings, inputDirectory, outputDirectory, expectedOutputFrames, completedFrames, cancellationToken, runtimeConfiguration, timeout).ConfigureAwait(false); }
    }
    private async Task<AiRestorationSession> ActivateFallbackAsync(string reason, VideoRestorationSettings settings, CancellationToken token)
    {
        _usingFallback = true; AiRestorationSession session = _fallbackSession ??= await _fallback.CreateSessionAsync(settings, token).ConfigureAwait(false); AiBackendSelectionDiagnostics.Shared.RecordFallback(AiBackendSelection.NcnnVulkan, _fallback.Id, reason); AiRuntimeTelemetryService.Shared.SwitchBackend(session, "Running (TensorRT fallback)"); _log?.Invoke($"[AI Backend] {reason} Falling back to {_fallback.DisplayName} because Auto was requested."); return session;
    }
}

/// <summary>Discovers engines, applies Auto/manual selection rules, and emits concise startup health diagnostics.</summary>
public sealed class AiBackendManager
{
    private readonly IAiRestorationBackend _ncnn;
    private readonly IAiRestorationBackend _tensorRt;
    private readonly Action<string>? _log;
    public AiBackendManager(string applicationDirectory, IMediaToolProcessRunner? runner = null, Action<string>? log = null, IAiRestorationBackend? ncnn = null, IAiRestorationBackend? tensorRt = null)
    { _log = log; _ncnn = ncnn ?? new AiRestorationBackendService(applicationDirectory, runner, log); _tensorRt = tensorRt ?? new TensorRtAiRestorationBackend(applicationDirectory, runner: runner, log: log); }

    public async Task<IReadOnlyList<AiBackendMetadata>> DiscoverAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) => new[] { await _ncnn.GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false), await _tensorRt.GetMetadataAsync(settings, cancellationToken).ConfigureAwait(false) };
    public async Task<IAiRestorationBackend> SelectAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default) =>
        await SelectAsync(settings, 0, 0, cancellationToken).ConfigureAwait(false);

    public async Task<IAiRestorationBackend> SelectAsync(VideoRestorationSettings settings, int sourceWidth, int sourceHeight, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AiBackendMetadata> backends = await DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
        AiBackendMetadata ncnn = backends[0], tensorRt = backends[1];
        AiBackendMetadata directMl = new("directml", "DirectML", "Unavailable", false, false, "DirectML inference is not implemented in this MediaFlux phase.", false, false, false, false, false, Array.Empty<string>());
        AiBackendMetadata cpu = new("cpu", "CPU", "Unavailable", false, false, "CPU inference is not implemented in this MediaFlux phase.", false, false, false, false, false, Array.Empty<string>());
        AiBackendSelectionDecision decision = await new AiBackendSelectionService().SelectAsync(settings.AiBackendSelection, settings, new[] { new AiBackendCandidate(AiBackendSelection.NcnnVulkan, _ncnn, ncnn), new AiBackendCandidate(AiBackendSelection.NvidiaTensorRt, _tensorRt, tensorRt), new AiBackendCandidate(AiBackendSelection.DirectMl, null, directMl), new AiBackendCandidate(AiBackendSelection.Cpu, null, cpu) }, sourceWidth, sourceHeight, cancellationToken).ConfigureAwait(false);
        _log?.Invoke(FormatStartup(decision.Backend.DisplayName, ncnn, tensorRt) + Environment.NewLine + $"Requested: {decision.Requested}; Selected: {decision.Selected}; Reason: {decision.Reason}" + (decision.FallbackReason is null ? "" : Environment.NewLine + "Fallback: " + decision.FallbackReason));
        return settings.AiBackendSelection == AiBackendSelection.Auto && decision.Selected == AiBackendSelection.NvidiaTensorRt ? new AutoFallbackAiRestorationBackend(decision.Backend, _ncnn, settings, _log) : decision.Backend;
    }
    public static string FormatStartup(string selected, AiBackendMetadata ncnn, AiBackendMetadata tensorRt) => $"AI Backend{Environment.NewLine}Selected: {selected}{Environment.NewLine}Available:{Environment.NewLine}{(ncnn.IsReady ? "✓" : "✗")} {ncnn.DisplayName}{Environment.NewLine}TensorRT: {(tensorRt.IsAvailable ? "Detected" : "Not Installed")}{Environment.NewLine}TensorRT Runtime{Environment.NewLine}{string.Join(Environment.NewLine, tensorRt.Diagnostics)}{Environment.NewLine}Ready: {(tensorRt.IsReady ? "Yes" : "No")}{Environment.NewLine}Reason: {tensorRt.Reason}";
}
