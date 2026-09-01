using MediaFlux.Models;

namespace MediaFlux;

internal sealed class EncodeJobSettingsForm : MediaFluxForm
{
    private readonly EncodeJobSettings _working;
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _profile = Choice("Medium Quality (Default)", "Very High Quality (Largest File)", "High Quality", "Medium Quality (Default)", "Low Quality (Smaller File)", "Very Low Quality (Smallest File)");
    private readonly ComboBox _codec = Choice(nameof(VideoCodecFamily.Hevc), nameof(VideoCodecFamily.H264), nameof(VideoCodecFamily.Hevc), nameof(VideoCodecFamily.Av1));
    private readonly ComboBox _encoder = Choice(VideoEncoderIds.Nvenc, VideoEncoderIds.Nvenc, VideoEncoderIds.Qsv, VideoEncoderIds.Libx264, VideoEncoderIds.Libx265, VideoEncoderIds.SvtAv1);
    private readonly TextBox _preset = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _autoTarget = new() { Text = "Auto-determine best target size", AutoSize = true };
    private readonly TextBox _target = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _quality = new() { Minimum = 12, Maximum = 35, Dock = DockStyle.Left, Width = 100 };
    private readonly ComboBox _resolution = Choice("", "", "Original resolution", "720p", "1080p", "1440p", "4K");
    private readonly CheckBox _tenBit = new() { Text = "Use 10-bit for HEVC/AV1", AutoSize = true };
    private readonly ComboBox _audio = Choice("Keep source layout", "Keep source layout", "Stereo (2.0)", "5.1 (5.1)");
    private readonly ComboBox _container = Choice(nameof(OutputContainerSelection.Mp4), nameof(OutputContainerSelection.Auto), nameof(OutputContainerSelection.Matroska), nameof(OutputContainerSelection.Mp4));
    private readonly CheckBox _deleteSource = new() { Text = "Delete source after successful compression", AutoSize = true };
    private readonly CheckBox _outputSuffix = new() { Text = "Enable output suffix", AutoSize = true };
    private readonly CheckBox _codecSuffix = new() { Text = "Enable codec suffix", AutoSize = true };
    private readonly TextBox _suffix = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _restoration = Choice("Off", "Off", "Vintage Animation – Light", "Vintage Animation – Restore", "DVD Animation Restore", "VHS / TV Capture Restore", "Custom");

    public EncodeJobSettingsForm(string jobName, EncodeJobSettings settings)
    {
        _working = settings.Clone();
        Text = $"Encode Settings — {jobName}"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(640, 630);
        _output.Text = _working.OutputFolder; Set(_profile, _working.CompressionProfile); Set(_codec, _working.VideoCodec); Set(_encoder, _working.EncoderId); _preset.Text = _working.EncoderPreset;
        _autoTarget.Checked = _working.AutoTargetSize; _target.Text = _working.TargetSize; _quality.Value = Math.Clamp(_working.QualityValue == 0 ? 22 : _working.QualityValue, 12, 35);
        Set(_resolution, _working.Resolution); _tenBit.Checked = _working.TenBit; Set(_audio, _working.AudioChannels); Set(_container, _working.OutputContainer);
        _deleteSource.Checked = _working.DeleteSourceAfterCompression; _outputSuffix.Checked = _working.EnableOutputSuffix; _codecSuffix.Checked = _working.EnableCodecSuffix; _suffix.Text = _working.OutputSuffix;
        _restoration.Items.Clear(); _restoration.Items.AddRange(new object[] { "Off", "Auto", "Custom" });
        _restoration.SelectedIndex = (int)(_working.Restoration?.Mode ?? VideoRestorationMode.Off);
        _autoTarget.CheckedChanged += (_, __) => _target.Enabled = !_autoTarget.Checked;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), ColumnCount = 2, RowCount = 18 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(grid, "Output location", _output); Add(grid, "Quality / file size", _profile); Add(grid, "Video codec", _codec); Add(grid, "Encoder", _encoder); Add(grid, "Encoder preset", _preset);
        Add(grid, "Target size (MB)", _target); Add(grid, "Auto quality (CRF/CQ)", _quality); Add(grid, "Resolution", _resolution); Add(grid, "Restoration", _restoration); Add(grid, "Audio layout", _audio); Add(grid, "Output container", _container);
        Add(grid, "", _autoTarget); Add(grid, "", _tenBit); Add(grid, "", _deleteSource); Add(grid, "", _outputSuffix); Add(grid, "", _codecSuffix); Add(grid, "Output suffix", _suffix);
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(cancel); buttons.Controls.Add(save); grid.Controls.Add(buttons, 1, 17);
        Controls.Add(grid); AcceptButton = save; CancelButton = cancel; _target.Enabled = !_autoTarget.Checked;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK)
                return;
            if (!_autoTarget.Checked && (!double.TryParse(_target.Text, out double target) || target <= 0))
            {
                MessageBox.Show(this, "Enter a positive target size or enable automatic target sizing.", Text);
                e.Cancel = true;
                return;
            }
            Commit();
        };
    }

    public EncodeJobSettings EditedSettings => _working.Clone();
    private void Commit() { _working.OutputFolder = _output.Text.Trim(); _working.CompressionProfile = _profile.Text; _working.VideoCodec = _codec.Text; _working.VideoFormat = _codec.Text; _working.EncoderId = _encoder.Text; _working.EncoderPreset = _preset.Text.Trim(); _working.AutoTargetSize = _autoTarget.Checked; _working.TargetSize = _target.Text.Trim(); _working.QualityValue = (int)_quality.Value; _working.Resolution = _resolution.Text; _working.TenBit = _tenBit.Checked; _working.AudioChannels = _audio.Text; _working.OutputContainer = _container.Text; _working.DeleteSourceAfterCompression = _deleteSource.Checked; _working.EnableOutputSuffix = _outputSuffix.Checked; _working.EnableCodecSuffix = _codecSuffix.Checked; _working.OutputSuffix = _suffix.Text.Trim(); _working.Restoration ??= new VideoRestorationSettings(); _working.Restoration.Mode = (VideoRestorationMode)_restoration.SelectedIndex; }
    private static ComboBox Choice(string selected, params string[] values) { var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; combo.Items.AddRange(values.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray()); Set(combo, selected); return combo; }
    private static void Set(ComboBox combo, string? value) { if (!string.IsNullOrWhiteSpace(value) && !combo.Items.Cast<object>().Any(item => string.Equals(item.ToString(), value, StringComparison.OrdinalIgnoreCase))) combo.Items.Add(value); combo.SelectedItem = combo.Items.Cast<object>().FirstOrDefault(item => string.Equals(item.ToString(), value, StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0]; }
    private static void Add(TableLayoutPanel grid, string label, Control value) { int row = grid.Controls.Count / 2; grid.Controls.Add(new Label { Text = label.Length == 0 ? "" : label + ":", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); grid.Controls.Add(value, 1, row); }
}
