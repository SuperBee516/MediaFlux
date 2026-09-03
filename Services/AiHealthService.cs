namespace MediaFlux.Services;

public enum AiHealthStatus { Healthy, Warning, Degraded, Error }

/// <summary>Read-only, deterministic health projection over existing AI runtime observations.</summary>
public sealed class AiHealthService
{
    public static readonly TimeSpan BenchmarkStaleAfter = TimeSpan.FromDays(30);
    private readonly AiRuntimeTelemetryService _telemetry;
    private readonly Func<AiBackendSelectionDecisionSnapshot?> _selection;
    private readonly Func<TensorRtRuntimeDiagnosticSnapshot?> _tensorRt;
    private readonly Func<bool> _diagnosticsAvailable;

    public AiHealthService(AiRuntimeTelemetryService? telemetry = null, Func<AiBackendSelectionDecisionSnapshot?>? selection = null, Func<TensorRtRuntimeDiagnosticSnapshot?>? tensorRt = null, Func<bool>? diagnosticsAvailable = null)
    {
        _telemetry = telemetry ?? AiRuntimeTelemetryService.Shared;
        _selection = selection ?? AiBackendSelectionDiagnostics.Shared.GetLatest;
        _tensorRt = tensorRt ?? TensorRtRuntimeDiagnostics.Shared.GetLatest;
        _diagnosticsAvailable = diagnosticsAvailable ?? (() => true); // The package service can collect partial data even without an error log.
    }

    public AiHealthEvaluation Evaluate() => Evaluate(_telemetry.GetSnapshot(), _selection(), _tensorRt(), _diagnosticsAvailable(), DateTimeOffset.UtcNow);

    internal static AiHealthEvaluation Evaluate(AiRuntimeTelemetrySnapshot runtime, AiBackendSelectionDecisionSnapshot? selection, TensorRtRuntimeDiagnosticSnapshot? tensorRt, bool diagnosticsAvailable, DateTimeOffset now)
    {
        var recommendations = new List<string>(); AiHealthStatus status = AiHealthStatus.Healthy;
        void Add(AiHealthStatus severity, string recommendation) { status = (AiHealthStatus)Math.Max((int)status, (int)severity); recommendations.Add(recommendation); }

        bool validationFailed = runtime.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) || tensorRt?.ValidationStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) == true;
        string validation = validationFailed ? "Failed" : runtime.ValidationEnabled ? "Enabled" : "Disabled";
        if (validationFailed) Add(AiHealthStatus.Error, "Validation failure detected; inspect the AI diagnostics package before retrying.");
        else if (!runtime.ValidationEnabled) Add(AiHealthStatus.Warning, "Enable validation before running AI restoration.");

        bool activeBackend = !runtime.Backend.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);
        string backendAvailability = activeBackend ? runtime.BackendReady ? "Ready" : "Unavailable" : "No active session";
        if (activeBackend && !runtime.BackendReady) Add(AiHealthStatus.Degraded, "Backend unavailable; verify the selected provider runtime and model installation.");

        TimeSpan? benchmarkAge = runtime.BenchmarkDate is DateTimeOffset date ? now - date : null;
        string benchmarkStatus = runtime.BenchmarkAvailable ? "Available" : "Unavailable";
        if (activeBackend && !runtime.BenchmarkAvailable) Add(AiHealthStatus.Warning, "Benchmark recommended for the active backend and runtime profile.");
        else if (benchmarkAge > BenchmarkStaleAfter) Add(AiHealthStatus.Warning, "Benchmark stale; re-run the benchmark for current hardware and drivers.");

        string driverCompatibility = "Unavailable";
        if (!runtime.BenchmarkDriverVersion.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) && !runtime.DriverVersion.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            driverCompatibility = runtime.BenchmarkDriverVersion.Equals(runtime.DriverVersion, StringComparison.OrdinalIgnoreCase) ? "Compatible" : "Changed";
            if (driverCompatibility == "Changed") Add(AiHealthStatus.Warning, "Driver changed since the benchmark; re-benchmark the active backend.");
        }

        bool fallback = selection?.FallbackReason is not null || runtime.Status.Contains("fallback", StringComparison.OrdinalIgnoreCase);
        if (fallback) Add(AiHealthStatus.Warning, "Using fallback backend; review the primary backend failure in diagnostics.");

        bool tensorRtRequested = selection?.Requested == AiBackendSelection.NvidiaTensorRt || selection?.Selected == AiBackendSelection.NvidiaTensorRt || runtime.Backend.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase);
        string engineStatus = tensorRt?.ValidationStatus ?? runtime.EngineStatus;
        string engineCache = tensorRt?.CacheState ?? runtime.EngineCacheState;
        if (tensorRtRequested && (engineStatus.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) || engineStatus.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            Add(runtime.Backend.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase) ? AiHealthStatus.Degraded : AiHealthStatus.Warning, tensorRt?.FailureReason is null ? "TensorRT unavailable; install a compatible runtime, provider bridge, and validated model." : "TensorRT unavailable; inspect the engine/runtime failure in diagnostics.");
        else if (tensorRtRequested && !engineStatus.Contains("validated", StringComparison.OrdinalIgnoreCase) && !engineStatus.Contains("passed", StringComparison.OrdinalIgnoreCase))
            Add(AiHealthStatus.Warning, "TensorRT engine rebuild recommended; the cached engine has not completed validation.");

        if (!diagnosticsAvailable) Add(AiHealthStatus.Warning, "Diagnostics collection is unavailable; verify MediaFlux can create an AI diagnostics package.");
        if (recommendations.Count == 0) recommendations.Add("No action needed.");

        return new(status, runtime.Backend, backendAvailability, validation, benchmarkStatus, benchmarkAge, runtime.RuntimeTuningState, engineStatus, engineCache, driverCompatibility, diagnosticsAvailable ? "Available" : "Unavailable", fallback, recommendations);
    }
}

public sealed record AiHealthEvaluation(
    AiHealthStatus Overall,
    string ActiveBackend,
    string BackendAvailability,
    string ValidationStatus,
    string BenchmarkStatus,
    TimeSpan? BenchmarkAge,
    string RuntimeTuningStatus,
    string EngineStatus,
    string EngineCacheStatus,
    string DriverRuntimeCompatibility,
    string DiagnosticsAvailability,
    bool UsingFallbackBackend,
    IReadOnlyList<string> Recommendations);
