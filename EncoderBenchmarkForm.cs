using MediaFlux.Services;
using MediaFlux.Models;

namespace MediaFlux;

internal sealed class EncoderBenchmarkForm : MediaFluxForm
{
    private readonly EncoderBenchmarkDefinition _definition;
    private readonly EncoderBenchmarkService _service;
    private readonly CheckedListBox _presets = new() { CheckOnClick = true, Height = 105, Dock = DockStyle.Fill };
    private readonly CheckedListBox _concurrency = new() { CheckOnClick = true, Height = 70, Dock = DockStyle.Fill };
    private readonly NumericUpDown _sampleSeconds = new() { Minimum = 5, Maximum = 120, Value = 25, Width = 70 };
    private readonly DataGridView _results = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoGenerateColumns = false
    };
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F)
    };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0), Text = "Ready." };
    private readonly Button _run = new() { Text = "Run Benchmark", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private CancellationTokenSource? _cancellation;
    private EncoderBenchmarkReport? _report;

    internal int ResultCount => _results.Rows.Count;
    internal bool IsRunning => _cancellation != null;

    public EncoderBenchmarkForm(EncoderBenchmarkDefinition definition, EncoderBenchmarkService service)
    {
        _definition = definition;
        _service = service;
        Text = "Encoder Benchmark & Diagnostics";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1180, 760);
        MinimumSize = new Size(900, 600);

        var source = new Label
        {
            Dock = DockStyle.Top, Height = 52, Padding = new Padding(10, 8, 10, 4),
            Text = $"Source: {Path.GetFileName(definition.SourcePath)}\r\n" +
                   $"{definition.SourceCodec} · {definition.SourceResolution} · {definition.SourceDuration:g} · " +
                   $"{definition.Settings.EncoderDisplayName}"
        };
        var options = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 145, Padding = new Padding(8),
            ColumnCount = 4, RowCount = 2
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        options.Controls.Add(new Label { Text = "Presets", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        options.Controls.Add(_presets, 1, 0);
        options.Controls.Add(new Label { Text = "Concurrency", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 2, 0);
        options.Controls.Add(_concurrency, 3, 0);
        var durationPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        durationPanel.Controls.Add(new Label { Text = "Sample duration", AutoSize = true, Padding = new Padding(0, 6, 3, 0) });
        durationPanel.Controls.Add(_sampleSeconds);
        durationPanel.Controls.Add(new Label { Text = "seconds (representative middle section; short files use the full duration)", AutoSize = true, Padding = new Padding(3, 6, 0, 0) });
        options.Controls.Add(durationPanel, 0, 1);
        options.SetColumnSpan(durationPanel, 4);

        foreach (EncoderPresetOption preset in definition.AvailablePresets)
        {
            int index = _presets.Items.Add(preset);
            if (preset.Value.Equals(definition.Settings.CurrentPreset, StringComparison.OrdinalIgnoreCase))
                _presets.SetItemChecked(index, true);
        }
        if (_presets.Items.Count > 0 && _presets.CheckedItems.Count == 0) _presets.SetItemChecked(0, true);
        foreach (int value in definition.AvailableConcurrency)
        {
            int index = _concurrency.Items.Add(value);
            _concurrency.SetItemChecked(index, value == 1);
        }

        AddColumn("Preset", 80); AddColumn("Jobs", 55); AddColumn("Status", 80);
        AddColumn("Job FPS", 75); AddColumn("Job speed", 80); AddColumn("Aggregate FPS", 100);
        AddColumn("Aggregate speed", 105); AddColumn("Elapsed", 90); AddColumn("Estimated full file", 125);
        AddColumn("Source media read", 120); AddColumn("Output write", 100); AddColumn("CPU", 65);
        AddColumn("GPU", 65); AddColumn("GPU encode", 85); AddColumn("GPU decode", 85); AddColumn("VRAM", 80);
        _results.SelectionChanged += (_, _) => RefreshDetails();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 250, Panel1MinSize = 150, Panel2MinSize = 130 };
        split.Panel1.Controls.Add(_results); split.Panel2.Controls.Add(_details);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 5, 8, 4), WrapContents = false };
        _run.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        var copy = new Button { Text = "Copy Technical Details", AutoSize = true };
        copy.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_details.Text)) Clipboard.SetText(_details.Text); };
        commands.Controls.AddRange(new Control[] { _run, _cancel, copy, _status });
        Controls.Add(split); Controls.Add(commands); Controls.Add(options); Controls.Add(source);
        FormClosing += (_, e) =>
        {
            if (_cancellation == null) return;
            _cancellation.Cancel();
            _status.Text = "Canceling benchmark and cleaning temporary outputs…";
            e.Cancel = true;
        };
    }

    private async Task RunAsync()
    {
        string[] presets = _presets.CheckedItems.Cast<EncoderPresetOption>().Select(x => x.Value).ToArray();
        int[] concurrency = _concurrency.CheckedItems.Cast<int>().ToArray();
        if (presets.Length == 0 || concurrency.Length == 0)
        {
            MessageBox.Show(this, "Select at least one preset and one concurrency value.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _cancellation = new CancellationTokenSource();
        _run.Enabled = false; _cancel.Enabled = true; _results.Rows.Clear(); _details.Clear();
        try
        {
            var progress = new Progress<string>(message => _status.Text = Compact(message));
            _report = await _service.RunAsync(new EncoderBenchmarkRequest(
                _definition, presets, concurrency, (int)_sampleSeconds.Value), progress, _cancellation.Token);
            foreach (EncoderBenchmarkConfigurationResult result in _report.Results)
            {
                int row = _results.Rows.Add(result.Preset, result.Concurrency, result.Success ? "Passed" : "Failed",
                    Number(result.AverageJobFps), $"{result.AverageJobRealtimeMultiplier:0.00}x", Number(result.AggregateFps),
                    $"{result.AggregateRealtimeMultiplier:0.00}x", result.Elapsed.ToString("g"),
                    result.EstimatedFullFileTime?.ToString("g") ?? "Unavailable",
                    Rate(result.EstimatedSourceReadMbps), Rate(result.OutputWriteMbps), Percent(result.CpuPercent),
                    Percent(result.GpuPercent), Percent(result.GpuEncodePercent), Percent(result.GpuDecodePercent),
                    result.PeakVramBytes.HasValue ? $"{result.PeakVramBytes.Value / 1048576d:0} MiB" : "Unavailable");
                _results.Rows[row].Tag = result;
            }
            if (_results.Rows.Count > 0) { _results.Rows[0].Selected = true; _results.CurrentCell = _results.Rows[0].Cells[0]; }
            _status.Text = $"Benchmark complete: {_report.Results.Count:N0} configuration(s). Temporary outputs were removed.";
        }
        catch (OperationCanceledException) { _status.Text = "Benchmark canceled. Temporary outputs were removed."; }
        catch (Exception ex) { _status.Text = "Benchmark failed."; MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally
        {
            _cancellation.Dispose(); _cancellation = null; _run.Enabled = true; _cancel.Enabled = false;
        }
    }

    private void RefreshDetails()
    {
        if (_report != null && _results.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is EncoderBenchmarkConfigurationResult result)
            _details.Text = EncoderBenchmarkService.BuildTechnicalDetails(_definition, _report.Sample, result);
    }

    private void AddColumn(string name, int width) => _results.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, Width = width, SortMode = DataGridViewColumnSortMode.Automatic });
    private static string Percent(double? value) => value.HasValue ? $"{value:0.#}%" : "Unavailable";
    private static string Rate(double? value) => value.HasValue ? $"{value:0.00} Mbit/s" : "Unavailable";
    private static string Number(double value) => value > 0 ? value.ToString("0.0") : "Unavailable";
    private static string Compact(string value) => value.Length <= 100 ? value : value[..97] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _service.Dispose();
        }
        base.Dispose(disposing);
    }
}
