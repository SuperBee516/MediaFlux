using MediaFlux.Services;

namespace MediaFlux;

/// <summary>Read-only view over <see cref="AiRuntimeTelemetryService"/>. It subscribes to
/// producer updates and deliberately does not poll hardware or the restoration pipeline.</summary>
public sealed class AiRuntimeDashboardForm : MediaFluxForm
{
    private readonly AiRuntimeTelemetryService _telemetry;
    private readonly AiHealthService _health;
    private readonly Dictionary<string, Label> _values = new(StringComparer.Ordinal);

    public AiRuntimeDashboardForm(AiRuntimeTelemetryService? telemetry = null, AiHealthService? health = null)
    {
        _telemetry = telemetry ?? AiRuntimeTelemetryService.Shared;
        _health = health ?? new AiHealthService(_telemetry);
        Text = "AI Runtime Dashboard";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(780, 620);
        Size = new Size(940, 720);
        AutoScaleMode = AutoScaleMode.Dpi;

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14), ColumnCount = 2 };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(Section("Backend", "Backend", "Provider", "Model", "Scale", "Runtime version", "Backend status"), 0, 0);
        outer.Controls.Add(Section("Hardware", "GPU", "Driver", "Total VRAM", "Available VRAM", "Runtime", "Device identifier"), 1, 0);
        outer.Controls.Add(Section("Runtime", "Threads", "Tile size", "Precision", "Engine status", "Engine cache", "Engine source", "Chunk size", "Planner", "Auto-tuning", "Cache source"), 0, 1);
        outer.Controls.Add(Section("Performance", "Current FPS", "Average FPS", "Expected FPS", "ETA", "Frames", "Peak VRAM", "GPU utilization", "CPU utilization", "Throughput efficiency"), 1, 1);
        outer.Controls.Add(Section("Benchmark", "Benchmark source", "Benchmark date", "Benchmark age", "Cached/new", "Runtime profile", "Retune recommendation"), 0, 2);
        outer.Controls.Add(Section("AI Health", "Overall health", "Backend availability", "Validation status", "Benchmark status", "Benchmark age", "Runtime tuning", "Engine cache status", "Driver/runtime", "Diagnostics", "Recommendations"), 1, 2);
        Controls.Add(outer);
        _telemetry.SnapshotChanged += OnSnapshotChanged;
        FormClosed += (_, _) => _telemetry.SnapshotChanged -= OnSnapshotChanged;
        Apply(_telemetry.GetSnapshot());
    }

    private Control Section(string title, params string[] names)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Margin = new Padding(5) };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (string name in names)
        {
            var key = new Label { Text = name + ":", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(2, 4, 12, 4) };
            var value = new Label { Text = "Unavailable", AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(2, 4, 2, 4) };
            _values[name] = value;
            table.Controls.Add(key);
            table.Controls.Add(value);
        }
        group.Controls.Add(table);
        return group;
    }

    private void OnSnapshotChanged(AiRuntimeTelemetrySnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => Apply(snapshot))); } catch (InvalidOperationException) { }
    }

    private void Apply(AiRuntimeTelemetrySnapshot s)
    {
        Set("Backend", s.Backend); Set("Provider", s.Provider); Set("Model", s.Model); Set("Scale", $"{(int)s.Scale}x"); Set("Runtime version", s.RuntimeVersion); Set("Backend status", s.Status);
        Set("GPU", s.GpuName); Set("Driver", s.DriverVersion); Set("Total VRAM", Bytes(s.TotalVramBytes)); Set("Available VRAM", Bytes(s.AvailableVramBytes)); Set("Runtime", s.RuntimeApi); Set("Device identifier", s.DeviceIdentifier);
        Set("Threads", s.Threads); Set("Tile size", s.TileSize); Set("Precision", s.Precision); Set("Engine status", s.EngineStatus); Set("Engine cache", s.EngineCacheState); Set("Engine source", s.EngineBuildSource); Set("Chunk size", s.ChunkSize is int size ? size + " frames" : "Unavailable"); Set("Planner", s.PlannerResult); Set("Auto-tuning", s.RuntimeTuningState); Set("Cache source", s.CacheSource);
        Set("Current FPS", Fps(s.CurrentFramesPerSecond)); Set("Average FPS", Fps(s.AverageFramesPerSecond)); Set("Expected FPS", Fps(s.ExpectedFramesPerSecond)); Set("ETA", s.EstimatedRemaining?.ToString(@"h\:mm\:ss") ?? "Unavailable"); Set("Frames", s.TotalFrames > 0 ? $"{s.FramesProcessed:N0} / {s.TotalFrames:N0}" : "Unavailable"); Set("Peak VRAM", Bytes(s.PeakVramBytes)); Set("GPU utilization", Percent(s.GpuUtilizationPercent)); Set("CPU utilization", Percent(s.CpuUtilizationPercent)); Set("Throughput efficiency", Percent(s.ThroughputEfficiencyPercent));
        Set("Benchmark source", s.BenchmarkSource); Set("Benchmark date", s.BenchmarkDate?.ToLocalTime().ToString("g") ?? "Unavailable"); Set("Benchmark age", s.BenchmarkDate is DateTimeOffset date ? Age(DateTimeOffset.UtcNow - date) : "Unavailable"); Set("Cached/new", s.BenchmarkAvailable ? "Cached" : "Unavailable"); Set("Runtime profile", s.RuntimeProfile); Set("Retune recommendation", s.RetuneRecommendation);
        AiHealthEvaluation health = _health.Evaluate();
        Set("Overall health", health.Overall.ToString()); Set("Backend availability", health.BackendAvailability); Set("Validation status", health.ValidationStatus); Set("Benchmark status", health.BenchmarkStatus); Set("Benchmark age", health.BenchmarkAge is TimeSpan age ? Age(age) : "Unavailable"); Set("Runtime tuning", health.RuntimeTuningStatus); Set("Engine cache status", health.EngineCacheStatus); Set("Driver/runtime", health.DriverRuntimeCompatibility); Set("Diagnostics", health.DiagnosticsAvailability); Set("Recommendations", string.Join(Environment.NewLine, health.Recommendations));
    }

    private void Set(string name, string value) => _values[name].Text = string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
    private static string YesNo(bool value) => value ? "Yes" : "No";
    private static string Fps(double? value) => value is double fps ? fps.ToString("0.##") + " FPS" : "Unavailable";
    private static string Percent(double? value) => value is double percent ? percent.ToString("0.#") + "%" : "Unavailable";
    private static string Bytes(long? value) => value is long bytes ? (bytes / 1073741824d).ToString("0.##") + " GiB" : "Unavailable";
    private static string Age(TimeSpan age) => age.TotalDays >= 1 ? $"{age.TotalDays:0.#} days" : age.TotalHours >= 1 ? $"{age.TotalHours:0.#} hours" : $"{Math.Max(0, age.TotalMinutes):0} minutes";
}
