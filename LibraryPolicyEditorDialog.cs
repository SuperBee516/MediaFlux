using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux;

internal sealed class LibraryPolicyEditorDialog : Form
{
    private readonly TextBox _name = new() { Width = 280 };
    private readonly NumericUpDown _minimumSizeGb = Number(0, 100_000, 0.1m);
    private readonly NumericUpDown _minimumDurationMinutes = Number(0, 1_440, 0.5m);
    private readonly TextBox _includedCodecs = new() { Width = 280 };
    private readonly TextBox _excludedCodecs = new() { Width = 280 };
    private readonly NumericUpDown _minimumHeight = Number(0, 8640, 144);
    private readonly NumericUpDown _maximumHeight = Number(0, 8640, 144);
    private readonly ComboBox _codec = Choice();
    private readonly ComboBox _encoder = Choice();
    private readonly TextBox _encoderPreset = new() { Width = 160 };
    private readonly TextBox _namedPreset = new() { Width = 280 };
    private readonly NumericUpDown _quality = Number(0, 51, 1);
    private readonly NumericUpDown _bitDepth = Number(8, 10, 2);
    private readonly NumericUpDown _maximumOutputHeight = Number(0, 8640, 144);
    private readonly ComboBox _container = Choice();
    private readonly NumericUpDown _minimumSavingsPercent = Number(0, 90, 1);
    private readonly NumericUpDown _minimumSavingsMb = Number(0, 1_000_000, 10);
    private readonly ComboBox _confidence = Choice();
    private readonly CheckBox _includeHdr = new() { Text = "Include HDR", AutoSize = true };
    private readonly CheckBox _includeSdr = new() { Text = "Include SDR", AutoSize = true };
    private readonly CheckBox _preserveHdr = new() { Text = "Preserve HDR", AutoSize = true };
    private readonly CheckBox _preserveResolution = new() { Text = "Preserve source resolution", AutoSize = true };
    private readonly CheckBox _excludeProtected = new() { Text = "Exclude protected files", AutoSize = true };
    private readonly CheckBox _excludeDuplicates = new() { Text = "Exclude duplicate cleanup candidates", AutoSize = true };
    private readonly CheckBox _deepMarginal = new() { Text = "Require review for marginal cases", AutoSize = true };
    private readonly CheckBox _allowRemux = new() { Text = "Allow remux-only suggestions", AutoSize = true };
    private readonly CheckBox _skipEfficient = new() { Text = "Accept already-efficient files when savings are insignificant", AutoSize = true };

    public LibraryPolicyDefinition Policy { get; }

    public LibraryPolicyEditorDialog(LibraryPolicyDefinition source)
    {
        Policy = source.CloneAsCustom(source.Name);
        Policy.Id = source.IsBuiltIn ? Guid.NewGuid().ToString("N") : source.Id;
        Text = source.IsBuiltIn ? "Clone library policy" : "Edit library policy";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(620, 700);
        Size = new Size(700, 760);
        Font = new Font("Segoe UI", 9F);

        _codec.Items.AddRange(Enum.GetValues<VideoCodecFamily>().Cast<object>().ToArray());
        foreach (EncoderCapabilities item in EncoderRegistry.Default.GetCapabilities()) _encoder.Items.Add(new EncoderChoice(item.Id, item.DisplayName));
        _container.Items.AddRange(Enum.GetValues<OutputContainerSelection>().Cast<object>().ToArray());
        _confidence.Items.AddRange(Enum.GetValues<LibraryPolicyConfidence>().Cast<object>().ToArray());

        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(table, "Policy name", _name);
        Add(table, "Minimum file size (GiB)", _minimumSizeGb);
        Add(table, "Minimum duration (minutes)", _minimumDurationMinutes);
        Add(table, "Included source codecs (comma-separated)", _includedCodecs);
        Add(table, "Excluded source codecs (comma-separated)", _excludedCodecs);
        Add(table, "Minimum source height (0 = any)", _minimumHeight);
        Add(table, "Maximum source height (0 = any)", _maximumHeight);
        Add(table, "Preferred video codec", _codec);
        Add(table, "Encoder", _encoder);
        Add(table, "Encoder preset", _encoderPreset);
        Add(table, "Named MediaFlux preset (optional)", _namedPreset);
        Add(table, "Quality value", _quality);
        Add(table, "Output bit depth", _bitDepth);
        Add(table, "Maximum output height (0 = policy default)", _maximumOutputHeight);
        Add(table, "Target container", _container);
        Add(table, "Minimum savings (%)", _minimumSavingsPercent);
        Add(table, "Minimum savings (MiB)", _minimumSavingsMb);
        Add(table, "Minimum confidence", _confidence);
        Add(table, "Source types", Row(_includeHdr, _includeSdr));
        Add(table, "Preservation", Row(_preserveHdr, _preserveResolution));
        Add(table, "Safety exclusions", Row(_excludeProtected, _excludeDuplicates));
        Add(table, "Optimization behavior", Row(_skipEfficient, _deepMarginal, _allowRemux));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var save = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        Controls.Add(new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { table } });
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
        LoadPolicy(source);
    }

    private void LoadPolicy(LibraryPolicyDefinition source)
    {
        _name.Text = source.IsBuiltIn ? $"{source.Name} copy" : source.Name;
        _minimumSizeGb.Value = Clamp((decimal)source.MinimumFileSizeBytes / (1024 * 1024 * 1024), _minimumSizeGb);
        _minimumDurationMinutes.Value = Clamp((decimal)source.MinimumDurationSeconds / 60, _minimumDurationMinutes);
        _includedCodecs.Text = string.Join(", ", source.IncludedSourceCodecs);
        _excludedCodecs.Text = string.Join(", ", source.ExcludedSourceCodecs);
        _minimumHeight.Value = source.MinimumHeight ?? 0;
        _maximumHeight.Value = source.MaximumHeight ?? 0;
        _codec.SelectedItem = source.PreferredCodec;
        _encoder.SelectedItem = _encoder.Items.Cast<EncoderChoice>().FirstOrDefault(item => item.Id.Equals(source.EncoderId, StringComparison.OrdinalIgnoreCase));
        _encoderPreset.Text = source.EncoderPreset;
        _namedPreset.Text = source.EncodingPresetName;
        _quality.Value = Clamp(source.QualityValue, _quality);
        _bitDepth.Value = source.PreferredBitDepth >= 10 ? 10 : 8;
        _maximumOutputHeight.Value = source.MaximumOutputHeight ?? 0;
        _container.SelectedItem = source.TargetContainer;
        _minimumSavingsPercent.Value = Clamp((decimal)source.MinimumExpectedSavingsPercent, _minimumSavingsPercent);
        _minimumSavingsMb.Value = Clamp(source.MinimumExpectedSavingsBytes / (1024 * 1024), _minimumSavingsMb);
        _confidence.SelectedItem = source.MinimumConfidence;
        _includeHdr.Checked = source.IncludeHdr;
        _includeSdr.Checked = source.IncludeSdr;
        _preserveHdr.Checked = source.PreserveHdr;
        _preserveResolution.Checked = source.PreserveSourceResolution;
        _excludeProtected.Checked = source.ExcludeProtectedFiles;
        _excludeDuplicates.Checked = source.ExcludeDuplicateCleanupCandidates;
        _deepMarginal.Checked = source.RequireDeepAnalysisForMarginalCases;
        _allowRemux.Checked = source.AllowRemuxOnly;
        _skipEfficient.Checked = source.SkipAlreadyEfficientFiles;
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Enter a policy name.", "Library Policy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_codec.SelectedItem is not VideoCodecFamily codec || _encoder.SelectedItem is not EncoderChoice encoder)
        {
            MessageBox.Show(this, "Select a preferred codec and encoder.", "Library Policy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Policy.Name = _name.Text.Trim();
        Policy.MinimumFileSizeBytes = (long)(_minimumSizeGb.Value * 1024 * 1024 * 1024);
        Policy.MinimumDurationSeconds = (double)_minimumDurationMinutes.Value * 60;
        Policy.IncludedSourceCodecs = Split(_includedCodecs.Text);
        Policy.ExcludedSourceCodecs = Split(_excludedCodecs.Text);
        Policy.MinimumHeight = _minimumHeight.Value > 0 ? (int)_minimumHeight.Value : null;
        Policy.MaximumHeight = _maximumHeight.Value > 0 ? (int)_maximumHeight.Value : null;
        Policy.PreferredCodec = codec;
        Policy.EncoderId = encoder.Id;
        Policy.EncoderPreset = _encoderPreset.Text.Trim();
        Policy.EncodingPresetName = _namedPreset.Text.Trim();
        Policy.QualityValue = (int)_quality.Value;
        Policy.PreferredBitDepth = (int)_bitDepth.Value;
        Policy.MaximumOutputHeight = _maximumOutputHeight.Value > 0 ? (int)_maximumOutputHeight.Value : null;
        Policy.TargetContainer = (OutputContainerSelection)(_container.SelectedItem ?? OutputContainerSelection.Auto);
        Policy.MinimumExpectedSavingsPercent = (double)_minimumSavingsPercent.Value;
        Policy.MinimumExpectedSavingsBytes = (long)_minimumSavingsMb.Value * 1024 * 1024;
        Policy.MinimumConfidence = (LibraryPolicyConfidence)(_confidence.SelectedItem ?? LibraryPolicyConfidence.Medium);
        Policy.IncludeHdr = _includeHdr.Checked;
        Policy.IncludeSdr = _includeSdr.Checked;
        Policy.PreserveHdr = _preserveHdr.Checked;
        Policy.PreserveSourceResolution = _preserveResolution.Checked;
        Policy.ExcludeProtectedFiles = _excludeProtected.Checked;
        Policy.ExcludeDuplicateCleanupCandidates = _excludeDuplicates.Checked;
        Policy.RequireDeepAnalysisForMarginalCases = _deepMarginal.Checked;
        Policy.AllowRemuxOnly = _allowRemux.Checked;
        Policy.SkipAlreadyEfficientFiles = _skipEfficient.Checked;
        Policy.IsBuiltIn = false;
        Policy.Normalize();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void Add(TableLayoutPanel table, string label, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 7, 8, 0) }, 0, row);
        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, 1, row);
    }
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        panel.Controls.AddRange(controls);
        return panel;
    }
    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal increment) =>
        new() { Minimum = minimum, Maximum = maximum, Increment = increment, DecimalPlaces = increment < 1 ? 1 : 0, Width = 140 };
    private static decimal Clamp(decimal value, NumericUpDown control) => Math.Clamp(value, control.Minimum, control.Maximum);
    private static List<string> Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private sealed record EncoderChoice(string Id, string Name) { public override string ToString() => Name; }
}
