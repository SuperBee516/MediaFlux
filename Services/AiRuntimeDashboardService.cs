namespace MediaFlux.Services;

/// <summary>Composes persistent capability, configured state, diagnostics, and active-session telemetry for the dashboard.</summary>
public sealed class AiRuntimeDashboardService
{
    private readonly AiRuntimeTelemetryService _telemetry;
    private readonly Func<HardwareSnapshot> _hardware;
    private readonly AiBenchmarkDatabase _benchmarks;
    private readonly Func<MediaFlux.Models.VideoRestorationSettings> _configured;

    public AiRuntimeDashboardService(AiRuntimeTelemetryService? telemetry = null, Func<HardwareSnapshot>? hardware = null, AiBenchmarkDatabase? benchmarks = null, Func<MediaFlux.Models.VideoRestorationSettings>? configured = null)
    {
        _telemetry = telemetry ?? AiRuntimeTelemetryService.Shared;
        _hardware = hardware ?? (() => HardwarePerformanceService.Capture("", "", "", ""));
        _benchmarks = benchmarks ?? new AiBenchmarkDatabase();
        _configured = configured ?? (() => MediaFlux.Models.Config.Load(AppPaths.ConfigFile).VideoRestoration);
    }

    public AiRuntimeDashboardSnapshot Capture()
    {
        HardwareSnapshot hardware = _hardware();
        AiRuntimeTelemetrySnapshot current = _telemetry.GetSnapshot();
        IReadOnlyList<AiBenchmarkRecord> records = _benchmarks.List(1);
        MediaFlux.Models.VideoRestorationSettings configured = _configured();
        return new(current, _telemetry.GetLastSession(), hardware,
            AiBackendSelectionDiagnostics.Shared.GetLatest(), TensorRtRuntimeDiagnostics.Shared.GetLatest(),
            records.Count, records.FirstOrDefault()?.Entry, configured.AiBackendSelection.ToString(), configured.AiModelId, DateTimeOffset.UtcNow);
    }
}

public sealed record AiRuntimeDashboardSnapshot(
    AiRuntimeTelemetrySnapshot CurrentSession,
    AiLastSessionSummary? LastSession,
    HardwareSnapshot Hardware,
    AiBackendSelectionDecisionSnapshot? BackendSelection,
    TensorRtRuntimeDiagnosticSnapshot? TensorRt,
    int BenchmarkCount,
    AiBenchmarkDatabaseEntry? LatestBenchmark,
    string ConfiguredBackend,
    string ConfiguredModel,
    DateTimeOffset CapturedAt);
