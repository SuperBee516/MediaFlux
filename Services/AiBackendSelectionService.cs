using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Ranks ready providers without owning discovery, benchmark execution, or hardware sampling.</summary>
public sealed class AiBackendSelectionService
{
    private readonly AiBenchmarkDatabase _benchmarks;
    private readonly Func<HardwareSnapshot> _hardware;
    private readonly AiRuntimeTelemetryService _telemetry;

    public AiBackendSelectionService(AiBenchmarkDatabase? benchmarks = null, Func<HardwareSnapshot>? hardware = null, AiRuntimeTelemetryService? telemetry = null)
    { _benchmarks = benchmarks ?? new AiBenchmarkDatabase(); _hardware = hardware ?? (() => HardwarePerformanceService.Capture("", "", "", "")); _telemetry = telemetry ?? AiRuntimeTelemetryService.Shared; }

    public async Task<AiBackendSelectionDecision> SelectAsync(AiBackendSelection requested, VideoRestorationSettings settings, IReadOnlyList<AiBackendCandidate> candidates, int sourceWidth = 0, int sourceHeight = 0, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AiBackendCandidate[] ready = candidates.Where(candidate => candidate.Metadata.IsReady && candidate.Backend is not null).ToArray();
        if (requested != AiBackendSelection.Auto)
        {
            AiBackendCandidate? explicitCandidate = candidates.FirstOrDefault(candidate => candidate.Selection == requested);
            if (explicitCandidate?.Backend is null || !explicitCandidate.Metadata.IsReady)
                throw new AiRestorationValidationException(explicitCandidate?.Metadata.Reason ?? $"{requested} is not implemented or unavailable.");
            var explicitDecision = new AiBackendSelectionDecision(requested, explicitCandidate.Selection, explicitCandidate.Backend, $"Explicit {explicitCandidate.Metadata.DisplayName} selection.", null, null);
            AiBackendSelectionDiagnostics.Shared.Record(explicitDecision);
            return explicitDecision;
        }
        if (ready.Length == 0) throw new AiRestorationValidationException("No AI backend is ready. " + string.Join(" ", candidates.Select(candidate => candidate.Metadata.DisplayName + ": " + (candidate.Metadata.Reason ?? "Unavailable."))));
        HardwareSnapshot hardware = _hardware();
        var ranked = new List<(AiBackendCandidate Candidate, double? Fps)>();
        foreach (AiBackendCandidate candidate in ready)
        {
            token.ThrowIfCancellationRequested();
            double? fps = await FindVerifiedFpsAsync(candidate.Backend!, settings, hardware, sourceWidth, sourceHeight, token).ConfigureAwait(false);
            ranked.Add((candidate, fps));
        }
        (AiBackendCandidate Candidate, double? Fps) selected = ranked.OrderByDescending(item => item.Fps.HasValue).ThenByDescending(item => item.Fps ?? double.MinValue).ThenBy(item => Priority(item.Candidate.Selection)).First();
        string reason = selected.Fps is double selectedFps ? $"Auto selected the fastest verified compatible backend ({selectedFps:0.##} FPS benchmark)." : "Auto selected the highest-priority ready compatible backend; no matching verified benchmark was available.";
        string? fallback = ranked.Where(item => item.Candidate.Selection != selected.Candidate.Selection).Select(item => $"{item.Candidate.Metadata.DisplayName}: {(item.Candidate.Metadata.IsReady ? "lower-ranked" : item.Candidate.Metadata.Reason ?? "unavailable")}").FirstOrDefault();
        var decision = new AiBackendSelectionDecision(requested, selected.Candidate.Selection, selected.Candidate.Backend!, reason, fallback, selected.Fps);
        AiBackendSelectionDiagnostics.Shared.Record(decision);
        return decision;
    }

    private async Task<double?> FindVerifiedFpsAsync(IAiRestorationBackend backend, VideoRestorationSettings settings, HardwareSnapshot hardware, int width, int height, CancellationToken token)
    {
        try
        {
            AiRestorationSession session = await backend.CreateSessionAsync(settings, token).ConfigureAwait(false);
            string gpu = hardware.Gpu, driver = hardware.GpuDriver;
            if (gpu.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) || driver.Equals("Unavailable", StringComparison.OrdinalIgnoreCase)) return backend.Id.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase) ? null : ActiveRuntimeFps(backend.Id, session.Model.BackendModelName);
            var key = new AiBenchmarkDatabaseKey(backend.Id, session.Capabilities.Identity, session.Model.BackendModelName, gpu, driver, session.Runtime?.Precision ?? "FP32", (int)settings.AiScale, ResolutionClass(width, height));
            return _benchmarks.TryGetFastestStable(key, out AiBenchmarkDatabaseEntry entry) ? entry.FramesPerSecond : backend.Id.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase) ? null : ActiveRuntimeFps(backend.Id, session.Model.BackendModelName);
        }
        catch { return null; }
    }
    private double? ActiveRuntimeFps(string backend, string model) { AiRuntimeTelemetrySnapshot runtime = _telemetry.GetSnapshot(); return runtime.IsActive && runtime.Backend.Equals(backend, StringComparison.OrdinalIgnoreCase) && runtime.Model.Equals(model, StringComparison.OrdinalIgnoreCase) ? runtime.ExpectedFramesPerSecond : null; }
    private static int Priority(AiBackendSelection selection) => selection switch { AiBackendSelection.NcnnVulkan => 0, AiBackendSelection.NvidiaTensorRt => 1, AiBackendSelection.DirectMl => 2, AiBackendSelection.Cpu => 3, _ => 9 };
    private static string ResolutionClass(int width, int height) => width <= 0 || height <= 0 ? "unknown" : Math.Max(width, height) >= 3840 ? "4k" : Math.Max(width, height) >= 1920 ? "1080p" : "sd";
}

public sealed record AiBackendCandidate(AiBackendSelection Selection, IAiRestorationBackend? Backend, AiBackendMetadata Metadata);
public sealed record AiBackendSelectionDecision(AiBackendSelection Requested, AiBackendSelection Selected, IAiRestorationBackend Backend, string Reason, string? FallbackReason, double? VerifiedFramesPerSecond);

public sealed class AiBackendSelectionDiagnostics
{
    private readonly object _gate = new(); private AiBackendSelectionDecisionSnapshot? _latest;
    public static AiBackendSelectionDiagnostics Shared { get; } = new();
    public void Record(AiBackendSelectionDecision decision) { lock (_gate) _latest = new(decision.Requested, decision.Selected, decision.Backend.Id, decision.Reason, decision.FallbackReason, decision.VerifiedFramesPerSecond, DateTimeOffset.UtcNow); }
    public void RecordFallback(AiBackendSelection selected, string backendId, string reason) { lock (_gate) _latest = new(AiBackendSelection.Auto, selected, backendId, "Auto runtime fallback selected a compatible backend.", reason, null, DateTimeOffset.UtcNow); }
    public AiBackendSelectionDecisionSnapshot? GetLatest() { lock (_gate) return _latest; }
}
public sealed record AiBackendSelectionDecisionSnapshot(AiBackendSelection Requested, AiBackendSelection Selected, string BackendId, string Reason, string? FallbackReason, double? VerifiedFramesPerSecond, DateTimeOffset Timestamp);
