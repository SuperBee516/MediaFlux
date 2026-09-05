using System.Buffers.Binary;
using System.Data;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

/// <summary>Read-mostly manager for the existing AI benchmark SQLite records.</summary>
public sealed class AiBenchmarkManagerForm : MediaFluxForm
{
    private readonly AiBenchmarkManagementService _service;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = true };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly TextBox _comparison = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly TextBox _filter = new() { Width = 220, PlaceholderText = "Filter GPU, model, backend…" };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready" };
    private IReadOnlyList<AiBenchmarkRecord> _records = Array.Empty<AiBenchmarkRecord>();

    public AiBenchmarkManagerForm(AiBenchmarkManagementService? service = null)
    {
        _service = service ?? new AiBenchmarkManagementService();
        Text = "AI Benchmark Manager";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 650);
        Size = new Size(1180, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        var commands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 6, 8, 4), WrapContents = false };
        commands.Controls.AddRange(new Control[] { Button("Refresh", (_, _) => RefreshRecords()), Button("Compare", (_, _) => CompareSelected()), Button("Re-run", async (_, _) => await RerunSelectedAsync()), Button("Delete selected", (_, _) => DeleteSelected()), Button("Delete obsolete", (_, _) => DeleteObsolete()), Button("Export selected", async (_, _) => await ExportAsync(false)), Button("Export all", async (_, _) => await ExportAsync(true)), Button("Import", async (_, _) => await ImportAsync()), _filter, _status });
        _filter.TextChanged += (_, _) => Bind();
        _grid.SelectionChanged += (_, _) => ShowDetails();
        var lower = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 560, Panel1MinSize = 300, Panel2MinSize = 300 };
        lower.Panel1.Controls.Add(_details); lower.Panel1.Controls.Add(new Label { Text = "Details", Dock = DockStyle.Top, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(6) });
        lower.Panel2.Controls.Add(_comparison); lower.Panel2.Controls.Add(new Label { Text = "Comparison", Dock = DockStyle.Top, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(6) });
        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 380, Panel1MinSize = 200, Panel2MinSize = 180 };
        main.Panel1.Controls.Add(_grid); main.Panel2.Controls.Add(lower);
        Controls.Add(main); Controls.Add(commands);
        Shown += (_, _) => RefreshRecords();
    }

    private static Button Button(string text, EventHandler click) { var button = new Button { Text = text, AutoSize = true, Margin = new Padding(2, 0, 2, 0) }; button.Click += click; return button; }
    private void RefreshRecords() { _records = _service.List(); Bind(); _status.Text = _records.Count == 0 ? "No benchmark results." : $"{_records.Count:N0} benchmark result(s)"; }
    private void Bind()
    {
        string filter = _filter.Text.Trim();
        AiRuntimeTelemetrySnapshot runtime = AiRuntimeTelemetryService.Shared.GetSnapshot();
        IEnumerable<AiBenchmarkRecord> visible = _records;
        if (!string.IsNullOrWhiteSpace(filter)) visible = visible.Where(record => Search(record, filter));
        var table = new DataTable();
        table.Columns.Add("Id", typeof(long)); table.Columns.Add("Date/time"); table.Columns.Add("GPU"); table.Columns.Add("Backend"); table.Columns.Add("Provider"); table.Columns.Add("AI model"); table.Columns.Add("Scale"); table.Columns.Add("Runtime profile"); table.Columns.Add("Tile size"); table.Columns.Add("Threads"); table.Columns.Add("Precision"); table.Columns.Add("Average FPS", typeof(double)); table.Columns.Add("Benchmark duration"); table.Columns.Add("Cache status"); table.Columns.Add("MediaFlux version");
        foreach (AiBenchmarkRecord record in visible)
        {
            AiBenchmarkDatabaseEntry entry = record.Entry;
            string cacheStatus = IsActive(runtime, entry) ? "Active" : entry.IsStable ? "Validated" : "Obsolete";
            table.Rows.Add(record.Id, entry.Timestamp.LocalDateTime, entry.Key.GpuIdentity, entry.Key.BackendId, Provider(entry.Key.BackendId), entry.Key.Model, entry.Key.Scale + "x", Profile(entry.Configuration, entry.Key.Precision), entry.Configuration.TileDisplay, entry.Configuration.ThreadsDisplay, entry.Key.Precision, entry.FramesPerSecond, "Unavailable", cacheStatus, "Unavailable");
        }
        _grid.DataSource = table;
        if (_grid.Columns.Contains("Id")) _grid.Columns["Id"].Visible = false;
        foreach (DataGridViewColumn column in _grid.Columns) { column.SortMode = DataGridViewColumnSortMode.Automatic; column.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells; }
        ShowDetails();
    }

    private static bool Search(AiBenchmarkRecord record, string value)
    {
        AiBenchmarkDatabaseEntry entry = record.Entry;
        return new[] { entry.Key.GpuIdentity, entry.Key.BackendId, entry.Key.Model, entry.Key.DriverVersion, entry.Key.ResolutionClass, entry.Key.Precision }.Any(candidate => candidate.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsActive(AiRuntimeTelemetrySnapshot runtime, AiBenchmarkDatabaseEntry entry) => runtime.IsActive &&
        runtime.Backend.Equals(entry.Key.BackendId, StringComparison.OrdinalIgnoreCase) &&
        runtime.Model.Equals(entry.Key.Model, StringComparison.OrdinalIgnoreCase) &&
        (int)runtime.Scale == entry.Key.Scale && runtime.GpuName.Equals(entry.Key.GpuIdentity, StringComparison.OrdinalIgnoreCase) &&
        runtime.DriverVersion.Equals(entry.Key.DriverVersion, StringComparison.OrdinalIgnoreCase);
    private IReadOnlyList<AiBenchmarkRecord> Selected() => _grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Cells["Id"].Value).OfType<long>().Select(id => _records.FirstOrDefault(record => record.Id == id)).Where(record => record is not null).Cast<AiBenchmarkRecord>().DistinctBy(record => record.Id).ToArray();
    private void ShowDetails()
    {
        AiBenchmarkRecord? record = Selected().FirstOrDefault();
        if (record is null) { _details.Text = "Select a benchmark to view its stored metadata."; return; }
        AiBenchmarkDatabaseEntry e = record.Entry;
        _details.Text = $"Hardware{Environment.NewLine}GPU: {e.Key.GpuIdentity}{Environment.NewLine}Driver: {e.Key.DriverVersion}{Environment.NewLine}VRAM: {Bytes(e.PeakVramBytes)}{Environment.NewLine}CPU: Unavailable{Environment.NewLine}{Environment.NewLine}Runtime{Environment.NewLine}Backend: {e.Key.BackendId}{Environment.NewLine}Provider: {Provider(e.Key.BackendId)}{Environment.NewLine}Model: {e.Key.Model}{Environment.NewLine}Tile: {e.Configuration.TileDisplay}{Environment.NewLine}Threads: {e.Configuration.ThreadsDisplay}{Environment.NewLine}Precision: {e.Key.Precision}{Environment.NewLine}{Environment.NewLine}Results{Environment.NewLine}Average FPS: {e.FramesPerSecond:0.##}{Environment.NewLine}Peak FPS: Unavailable{Environment.NewLine}Benchmark duration: Unavailable{Environment.NewLine}Chunk configuration: Unavailable{Environment.NewLine}Benchmark source: SQLite benchmark database{Environment.NewLine}Benchmark timestamp: {e.Timestamp.LocalDateTime:g}{Environment.NewLine}Validation: {(e.IsStable ? "Validated" : "Obsolete")}{Environment.NewLine}Summary: {e.Summary}";
    }
    private void CompareSelected()
    {
        AiBenchmarkRecord[] records = Selected().ToArray();
        if (records.Length < 2) { _comparison.Text = "Select two or more benchmarks to compare."; return; }
        AiBenchmarkDatabaseEntry baseline = records[0].Entry;
        var lines = new List<string> { "Comparison (differences marked *)", Header() };
        foreach (AiBenchmarkRecord record in records)
        {
            AiBenchmarkDatabaseEntry e = record.Entry;
            string profile = Profile(e.Configuration, e.Key.Precision), baselineProfile = Profile(baseline.Configuration, baseline.Key.Precision);
            lines.Add($"{Mark(e.FramesPerSecond, baseline.FramesPerSecond)} {e.FramesPerSecond,7:0.##} FPS | {Mark(e.Key.GpuIdentity, baseline.Key.GpuIdentity)} {e.Key.GpuIdentity} | {Mark(e.Key.BackendId, baseline.Key.BackendId)} {e.Key.BackendId} | {Mark(e.Key.Model, baseline.Key.Model)} {e.Key.Model} | {Mark(e.Configuration.TileDisplay, baseline.Configuration.TileDisplay)} tile {e.Configuration.TileDisplay} | {Mark(e.Configuration.ThreadsDisplay, baseline.Configuration.ThreadsDisplay)} threads {e.Configuration.ThreadsDisplay} | {Mark(e.Key.Precision, baseline.Key.Precision)} {e.Key.Precision} | {Mark(e.Key.DriverVersion, baseline.Key.DriverVersion)} driver {e.Key.DriverVersion} | {Mark(profile, baselineProfile)} profile {profile}");
        }
        _comparison.Text = string.Join(Environment.NewLine, lines);
    }
    private static string Header() => "  FPS       GPU | Backend | Model | Runtime";
    private static string Mark<T>(T value, T baseline) => EqualityComparer<T>.Default.Equals(value, baseline) ? " " : "*";
    private void DeleteSelected() { AiBenchmarkRecord[] records = Selected().ToArray(); if (records.Length == 0) return; if (MessageBox.Show(this, $"Delete {records.Length} selected benchmark(s)? Tuning cache files are not changed.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _status.Text = $"Deleted {_service.DeleteSelected(records)} benchmark(s)."; RefreshRecords(); }
    private void DeleteObsolete() { if (MessageBox.Show(this, "Delete failed/obsolete benchmark rows? Validated results and tuning cache files are not changed.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _status.Text = $"Deleted {_service.DeleteObsolete()} obsolete benchmark(s)."; RefreshRecords(); }
    private async Task ExportAsync(bool all)
    {
        AiBenchmarkRecord[] records = (all ? _records : Selected()).ToArray(); if (records.Length == 0) { _status.Text = "No benchmark records selected."; return; }
        using var dialog = new SaveFileDialog { Filter = "MediaFlux AI benchmarks (*.mfai-benchmarks.json)|*.mfai-benchmarks.json", FileName = "mediaflux-ai-benchmarks.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _service.ExportAsync(dialog.FileName, records); _status.Text = $"Exported {records.Length} benchmark(s).";
    }
    private async Task ImportAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "MediaFlux AI benchmarks (*.mfai-benchmarks.json;*.json)|*.mfai-benchmarks.json;*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AiBenchmarkImportResult result = await _service.ImportAsync(dialog.FileName); _status.Text = $"{result.Message} Imported: {result.Imported}; rejected: {result.Rejected}."; RefreshRecords();
    }
    private async Task RerunSelectedAsync()
    {
        AiBenchmarkRecord? record = Selected().FirstOrDefault(); if (record is null) { _status.Text = "Select a benchmark to re-run."; return; }
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder containing at least 120 extracted PNG preview frames." };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string[] frames = Directory.EnumerateFiles(dialog.SelectedPath, "*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (frames.Length < AiBackendBenchmarkService.DefaultFrameCount) { _status.Text = "A re-run requires at least 120 PNG preview frames."; return; }
        if (!TryReadPngSize(frames[0], out int width, out int height)) { _status.Text = "The selected preview frames are not readable PNG files."; return; }
        _status.Text = "Re-running benchmark…"; Enabled = false;
        try
        {
            AiBenchmarkDatabaseEntry entry = record.Entry;
            var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.General, AiModelId = entry.Key.Model, AiScale = (AiRestorationScale)entry.Key.Scale, AiBackendSelection = AiBackendSelection.NcnnVulkan };
            IAiRestorationBackend backend = await new AiBackendManager(AppPaths.InstallDirectory).SelectAsync(settings);
            AiBackendBenchmarkResult result = await new AiBackendBenchmarkService(AppPaths.AiBenchmarkRerunsDirectory).RunAsync(new(backend, settings, frames, width, height));
            _status.Text = $"Re-run completed: {result.EffectiveFramesPerSecond:0.##} FPS."; RefreshRecords();
        }
        catch (Exception ex) { _status.Text = "Re-run failed: " + ex.Message; }
        finally { Enabled = true; }
    }
    private static bool TryReadPngSize(string path, out int width, out int height) { width = height = 0; try { byte[] bytes = File.ReadAllBytes(path); if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71) return false; width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)); height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)); return width > 0 && height > 0; } catch { return false; } }
    private static string Provider(string backend) => backend.Equals("ncnn-vulkan", StringComparison.OrdinalIgnoreCase) ? "NCNN" : backend;
    private static string Profile(NcnnRuntimeConfiguration config, string precision) => $"{config.ThreadsDisplay}; Tile {config.TileDisplay}; {precision}";
    private static string Bytes(long? value) => value is long bytes ? (bytes / 1073741824d).ToString("0.##") + " GiB" : "Unavailable";
}
