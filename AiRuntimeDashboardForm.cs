using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

/// <summary>Operational, read-only view of AI capability, health, diagnostics, and live sessions.</summary>
public sealed class AiRuntimeDashboardForm : MediaFluxForm
{
    private readonly AiRuntimeTelemetryService _telemetry;
    private readonly AiHealthService _health;
    private readonly AiRuntimeDashboardService _dashboard;
    private readonly Config _config;
    private readonly string _configPath;
    private readonly Panel _surface = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(243, 246, 249), Name = "dashboardSurface" };
    private readonly TableLayoutPanel _content = new() { AutoSize = true, ColumnCount = 1, Name = "dashboardContent", Padding = new Padding(18, 14, 18, 18) };
    private readonly Label _stateBadge = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Padding = new Padding(9, 4, 9, 4), Name = "dashboardOverallState" };
    private readonly TableLayoutPanel _summaryCards = new() { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Name = "dashboardSummaryCards" };
    private readonly TableLayoutPanel _detailGrid = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Name = "dashboardDetailGrid" };
    private bool _applying;

    public AiRuntimeDashboardForm(AiRuntimeTelemetryService? telemetry = null, AiHealthService? health = null, Config? config = null, string? configPath = null)
    {
        _telemetry = telemetry ?? AiRuntimeTelemetryService.Shared;
        _health = health ?? new AiHealthService(_telemetry);
        _config = config ?? Config.Load(configPath ?? AppPaths.ConfigFile);
        _configPath = configPath ?? AppPaths.ConfigFile;
        _dashboard = new AiRuntimeDashboardService(_telemetry, configured: () => _config.VideoRestoration);
        Text = "AI Runtime Dashboard"; StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(243, 246, 249); AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(980, 680); Size = RestoreSize();
        Controls.Add(_surface); _surface.Controls.Add(_content);
        ConfigureSurface();
        _telemetry.SnapshotChanged += OnSnapshotChanged;
        FormClosed += (_, _) => { _telemetry.SnapshotChanged -= OnSnapshotChanged; PersistSize(); };
        ResizeEnd += (_, _) => PersistSize();
        Apply(_dashboard.Capture());
    }

    private void ConfigureSurface()
    {
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.Absolute, Scale(116)));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.Controls.Add(BuildHeader(), 0, 0);
        _content.Controls.Add(BuildSubtitle(), 0, 1);
        ConfigureSummaryCards(); _content.Controls.Add(_summaryCards, 0, 2);
        _content.Controls.Add(BuildCommandBar(), 0, 3);
        _content.Controls.Add(_detailGrid, 0, 4);
        _surface.Resize += (_, _) => ResizeSurfaceContent();
        ResizeSurfaceContent();
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Name = "dashboardHeader" };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "AI Runtime", AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), Name = "dashboardTitle" }, 0, 0);
        header.Controls.Add(_stateBadge, 1, 0); return header;
    }

    private static Control BuildSubtitle() => new Label { Text = "Runtime readiness, hardware acceleration, AI health and live performance", AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Padding = new Padding(0, 2, 0, 8), Name = "dashboardSubtitle" };

    private void ConfigureSummaryCards()
    {
        foreach (float weight in new[] { 25f, 25f, 25f, 25f }) _summaryCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, weight));
    }

    private Control BuildCommandBar()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 10, 0, 10), Name = "dashboardCommands" };
        bar.Controls.Add(Command("Refresh status", "dashboardRefreshButton", "arrow-round-left-flat.svg", (_, _) => Apply(_dashboard.Capture())));
        bar.Controls.Add(Command("Open Benchmark Manager", "dashboardBenchmarkButton", "graph-bar-increase-flat.svg", (_, _) => OpenBenchmarkManager()));
        bar.Controls.Add(Command("Create Diagnostics Package…", "dashboardDiagnosticsButton", "database-flat.svg", async (_, _) => await CreateDiagnosticsAsync())); return bar;
    }

    private Button Command(string text, string name, string icon, EventHandler click)
    {
        var button = new Button { Text = text, Name = name, AutoSize = true, Height = Scale(34), Margin = new Padding(0, 0, Scale(8), 0), Padding = new Padding(8, 0, 8, 0), Image = AiBenchmarkIconService.Get(icon, 18, DeviceDpi), ImageAlign = ContentAlignment.MiddleLeft, TextImageRelation = TextImageRelation.ImageBeforeText, UseVisualStyleBackColor = true };
        button.Click += click; return button;
    }

    private void OpenBenchmarkManager()
    {
        try { using var manager = new AiBenchmarkManagerForm(config: _config, configPath: _configPath); manager.ShowDialog(this); Apply(_dashboard.Capture()); }
        catch (Exception ex) { ErrorLogService.Append(AppPaths.UserDataDirectory, "Open AI Benchmark Manager failed", exception: ex); MessageBox.Show(this, "The AI Benchmark Manager could not be opened. The error was logged.", "AI Benchmark Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task CreateDiagnosticsAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a destination folder for the AI diagnostics package." }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { AiDiagnosticsPackageResult result = await new AiDiagnosticsPackageService(_telemetry).CreateAsync(dialog.SelectedPath); MessageBox.Show(this, $"Diagnostics package created:\n{result.PackagePath}", "AI Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { ErrorLogService.Append(AppPaths.UserDataDirectory, "Create AI diagnostics package failed", exception: ex); MessageBox.Show(this, "The diagnostics package could not be created. The error was logged.", "AI Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ResizeSurfaceContent()
    {
        int available = Math.Max(0, _surface.ClientSize.Width - _surface.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
        _content.Width = Math.Max(Scale(940), available);
    }

    private void OnSnapshotChanged(AiRuntimeTelemetrySnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => Apply(_dashboard.Capture()))); } catch (InvalidOperationException) { }
    }

    private void Apply(AiRuntimeDashboardSnapshot state)
    {
        if (_applying || IsDisposed) return; _applying = true;
        try
        {
            AiHealthEvaluation health = _health.Evaluate(); AiRuntimeTelemetrySnapshot current = state.CurrentSession;
            string overall = health.Overall switch { AiHealthStatus.Error => "Error", AiHealthStatus.Warning or AiHealthStatus.Degraded => "Attention", _ => current.IsActive ? "Active" : "Ready" };
            _stateBadge.Text = overall; _stateBadge.BackColor = StateColor(overall); _stateBadge.ForeColor = overall is "Error" or "Attention" ? Color.DarkRed : Color.DarkGreen;
            SetSummaryCards(health, current, state); SetDetailCards(health, current, state);
        }
        finally { _applying = false; }
    }

    private void SetSummaryCards(AiHealthEvaluation health, AiRuntimeTelemetrySnapshot current, AiRuntimeDashboardSnapshot state)
    {
        _summaryCards.Controls.Clear();
        _summaryCards.Controls.Add(SummaryCard("AI Health", health.Overall.ToString(), health.Recommendations.FirstOrDefault() ?? "No action needed."), 0, 0);
        _summaryCards.Controls.Add(SummaryCard("GPU / Hardware", Value(state.Hardware.Gpu), $"{Bytes(state.Hardware.DedicatedVramBytes)} VRAM · Driver {Value(state.Hardware.GpuDriver)}"), 1, 0);
        _summaryCards.Controls.Add(SummaryCard("Runtime / Backend", Current(current.Backend, state.ConfiguredBackend), Current(current.Provider, state.BackendSelection is null ? "Provider not detected" : current.Provider)), 2, 0);
        _summaryCards.Controls.Add(SummaryCard("Performance / Session", current.IsActive ? Fps(current.CurrentFramesPerSecond) : "Idle", current.IsActive ? current.Status : "No active session"), 3, 0);
    }

    private Control SummaryCard(string title, string primary, string secondary)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, ColumnCount = 1, RowCount = 3, Padding = new Padding(11, 9, 11, 8), Margin = new Padding(4, 0, 4, 0), Name = "dashboardCard" + title.Replace(" ", "") };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, Scale(20))); card.RowStyles.Add(new RowStyle(SizeType.Absolute, Scale(30))); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(new Label { Text = title.ToUpperInvariant(), Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        card.Controls.Add(new Label { Text = primary, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(30, 41, 59), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        card.Controls.Add(new Label { Text = secondary, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(100, 116, 139), TextAlign = ContentAlignment.TopLeft }, 0, 2); return card;
    }

    private void SetDetailCards(AiHealthEvaluation health, AiRuntimeTelemetrySnapshot current, AiRuntimeDashboardSnapshot state)
    {
        _detailGrid.SuspendLayout(); _detailGrid.Controls.Clear(); _detailGrid.RowStyles.Clear(); _detailGrid.RowCount = 4;
        for (int i = 0; i < 4; i++) _detailGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _detailGrid.ColumnStyles.Clear(); _detailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _detailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddDetailCard("Runtime & Model", new[] { ("Backend", Current(current.Backend, state.ConfiguredBackend == "Auto" ? state.BackendSelection?.BackendId ?? "Auto" : state.ConfiguredBackend)), ("Provider", Current(current.Provider, "Not detected")), ("Model", Current(current.Model, string.IsNullOrWhiteSpace(state.ConfiguredModel) ? "Not configured" : state.ConfiguredModel)), ("Runtime", Current(current.RuntimeVersion, state.TensorRt?.TensorRtVersion ?? "Unknown")), ("Precision", Current(current.Precision, "Unknown")) }, 0, 0);
        AddDetailCard("Hardware", new[] { ("GPU", Value(state.Hardware.Gpu)), ("Driver", Value(state.Hardware.GpuDriver)), ("Dedicated VRAM", Bytes(state.Hardware.DedicatedVramBytes)), ("Device source", Value(state.Hardware.Gpu).Equals("Not detected", StringComparison.OrdinalIgnoreCase) ? "Not detected" : "nvidia-smi") }, 1, 0);
        var live = new List<(string, string)> { ("Status", current.IsActive ? current.Status : "No active session"), ("Current FPS", current.IsActive ? Fps(current.CurrentFramesPerSecond) : "—"), ("Average FPS", current.IsActive ? Fps(current.AverageFramesPerSecond) : "—"), ("ETA", current.IsActive ? current.EstimatedRemaining?.ToString(@"h\:mm\:ss") ?? "Unknown" : "—"), ("Frames", current.IsActive && current.TotalFrames > 0 ? $"{current.FramesProcessed:N0} / {current.TotalFrames:N0}" : "—") };
        if (current.IsActive) live.AddRange(new[] { ("Expected FPS", Fps(current.ExpectedFramesPerSecond)), ("GPU utilization", Percent(current.GpuUtilizationPercent)), ("CPU utilization", Percent(current.CpuUtilizationPercent)), ("Peak VRAM", Bytes(current.PeakVramBytes)), ("Efficiency", Percent(current.ThroughputEfficiencyPercent)) });
        AddDetailCard("Live Performance", live.ToArray(), 0, 1);
        AddDetailCard("Optimization & Benchmark", new[] { ("Benchmark records", state.BenchmarkCount == 0 ? "No benchmark" : state.BenchmarkCount.ToString("N0")), ("Latest benchmark", state.LatestBenchmark?.Timestamp.ToLocalTime().ToString("g") ?? "No benchmark"), ("Runtime profile", Current(current.RuntimeProfile, "Unknown")), ("Planner", Current(current.PlannerResult, "Not applicable")), ("Tuning", Current(current.RuntimeTuningState, "Not configured")), ("Cache", Current(current.CacheSource, "Not configured")) }, 1, 1);
        AddDetailCard("Health & Diagnostics", new[] { ("Health", health.Overall.ToString()), ("Recommendation", string.Join(Environment.NewLine, health.Recommendations)), ("Backend", health.BackendAvailability), ("Validation", health.ValidationStatus), ("TensorRT", health.EngineStatus), ("Diagnostics", health.DiagnosticsAvailability) }, 0, 2, true);
        AddLastSessionCard(state.LastSession, 0, 3); _detailGrid.ResumeLayout(true);
    }

    private void AddDetailCard(string title, (string Label, string Value)[] pairs, int column, int row, bool span = false)
    {
        Control card = DetailCard(title, pairs); _detailGrid.Controls.Add(card, column, row); if (span) _detailGrid.SetColumnSpan(card, 2);
    }

    private Control DetailCard(string title, (string Label, string Value)[] pairs)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.White, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, ColumnCount = 1, RowCount = pairs.Length + 1, Padding = new Padding(11, 7, 11, 8), Margin = new Padding(4), Name = "dashboardDetailCard" + title.Replace(" ", "") };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, Scale(28))); for (int i = 0; i < pairs.Length; i++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), TextAlign = ContentAlignment.MiddleLeft, Name = "dashboardDetailTitle" + title.Replace(" ", "") }, 0, 0);
        for (int i = 0; i < pairs.Length; i++) card.Controls.Add(Pair(pairs[i].Label, pairs[i].Value), 0, i + 1); return card;
    }

    private Control Pair(string label, string value)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(2, 2, 2, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(126))); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = label, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), ForeColor = Color.FromArgb(71, 85, 105), Margin = Padding.Empty }, 0, 0);
        row.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Color.FromArgb(30, 41, 59), Margin = Padding.Empty }, 1, 0); return row;
    }

    private void AddLastSessionCard(AiLastSessionSummary? session, int column, int row)
    {
        var pairs = session is null ? new[] { ("Status", "No completed AI session in this process.") } : new[] { ("Status", session.Status), ("Backend", session.Backend), ("Provider", session.Provider), ("Model", session.Model), ("Average FPS", Fps(session.AverageFramesPerSecond)), ("Frames", session.FramesProcessed > 0 ? $"{session.FramesProcessed:N0} / {session.TotalFrames:N0}" : "Unknown"), ("Peak VRAM", Bytes(session.PeakVramBytes)), ("Completed", session.CompletedAt.ToLocalTime().ToString("g")) };
        Control card = DetailCard("Last Session", pairs); _detailGrid.Controls.Add(card, column, row); _detailGrid.SetColumnSpan(card, 2);
    }

    private Size RestoreSize() => _config.AiRuntimeDashboardWindowWidth <= 0 || _config.AiRuntimeDashboardWindowHeight <= 0 ? new Size(1180, 820) : new Size(Math.Clamp(_config.AiRuntimeDashboardWindowWidth, 980, 3000), Math.Clamp(_config.AiRuntimeDashboardWindowHeight, 680, 2400));
    private void PersistSize() { if (WindowState != FormWindowState.Normal || IsDisposed) return; _config.AiRuntimeDashboardWindowWidth = Math.Max(MinimumSize.Width, Width); _config.AiRuntimeDashboardWindowHeight = Math.Max(MinimumSize.Height, Height); try { _config.Save(_configPath); } catch (Exception ex) { ErrorLogService.Append(AppPaths.UserDataDirectory, "Save AI Runtime Dashboard window size failed", exception: ex); } }
    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private static string Current(string current, string fallback) => !string.IsNullOrWhiteSpace(current) && !current.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) ? current : fallback;
    private static string Value(string value) => string.IsNullOrWhiteSpace(value) || value.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) ? "Not detected" : value;
    private static string Fps(double? value) => value is double fps ? fps.ToString("0.##") + " FPS" : "Unknown";
    private static string Percent(double? value) => value is double percent ? percent.ToString("0.#") + "%" : "Unknown";
    private static string Bytes(long? value) => value is long bytes ? (bytes / 1073741824d).ToString("0.##") + " GiB" : "Unknown";
    private static Color StateColor(string state) => state switch { "Error" => Color.FromArgb(254, 226, 226), "Attention" => Color.FromArgb(254, 243, 199), "Active" => Color.FromArgb(219, 234, 254), _ => Color.FromArgb(220, 252, 231) };
}
