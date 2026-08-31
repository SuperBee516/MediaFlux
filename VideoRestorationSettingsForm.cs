using MediaFlux.Models;

namespace MediaFlux;

/// <summary>Progressive-disclosure editor for Phase 1 manual restoration controls.</summary>
internal sealed class VideoRestorationSettingsForm : MediaFluxForm
{
    private readonly VideoRestorationSettings _working;
    private readonly ComboBox _denoise = Choice<VideoRestorationStrength>();
    private readonly ComboBox _deblock = Choice<VideoRestorationStrength>();
    private readonly ComboBox _deband = Choice<VideoRestorationStrength>();
    private readonly ComboBox _sharpen = Choice<VideoRestorationStrength>();
    private readonly ComboBox _deinterlace = Choice<VideoRestorationDeinterlace>();
    private readonly ComboBox _resize = Choice<VideoRestorationResize>();
    private readonly NumericUpDown _brightness = Number(-1, 1, .05m);
    private readonly NumericUpDown _contrast = Number(.5m, 2, .05m);
    private readonly NumericUpDown _saturation = Number(0, 2, .05m);
    private readonly NumericUpDown _width = new() { Minimum = 64, Maximum = 7680, Value = 1920, Dock = DockStyle.Left, Width = 100 };
    private readonly NumericUpDown _height = new() { Minimum = 64, Maximum = 4320, Value = 1080, Dock = DockStyle.Left, Width = 100 };
    private readonly CheckBox _aspect = new() { Text = "Preserve aspect ratio", AutoSize = true };
    private readonly ComboBox _aiMode = Choice<AiRestorationMode>();
    private readonly TextBox _aiModel = new() { Dock = DockStyle.Fill, PlaceholderText = "Model ID (detected local model)" };
    private readonly ComboBox _aiScale = Choice<AiRestorationScale>();
    private readonly TextBox _aiDevice = new() { Dock = DockStyle.Fill, Text = "Auto" };
    private readonly TextBox _aiBackend = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional local NCNN/Vulkan executable" };
    private readonly TextBox _aiModels = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional models directory" };

    public VideoRestorationSettingsForm(VideoRestorationSettings settings)
    {
        _working = settings.Clone(); Text = "Video Restoration Advanced Settings"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(620, 690);
        Set(_denoise, _working.Denoise); Set(_deblock, _working.Deblock); Set(_deband, _working.Deband); Set(_sharpen, _working.Sharpen); Set(_deinterlace, _working.Deinterlace); Set(_resize, _working.Resize);
        _brightness.Value = _working.Brightness; _contrast.Value = _working.Contrast; _saturation.Value = _working.Saturation; _width.Value = Math.Clamp(_working.CustomWidth == 0 ? 1920 : _working.CustomWidth, 64, 7680); _height.Value = Math.Clamp(_working.CustomHeight == 0 ? 1080 : _working.CustomHeight, 64, 4320); _aspect.Checked = _working.PreserveAspectRatio;
        Set(_aiMode, _working.AiMode); Set(_aiScale, _working.AiScale); _aiModel.Text = _working.AiModelId; _aiDevice.Text = string.IsNullOrWhiteSpace(_working.AiDevice) ? "Auto" : _working.AiDevice; _aiBackend.Text = _working.AiBackendPath; _aiModels.Text = _working.AiModelsDirectory;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(grid, "Denoise", _denoise); Add(grid, "Deblock / artifacts", _deblock); Add(grid, "Deband", _deband); Add(grid, "Sharpen", _sharpen); Add(grid, "Deinterlace", _deinterlace); Add(grid, "Brightness", _brightness); Add(grid, "Contrast", _contrast); Add(grid, "Saturation", _saturation); Add(grid, "Restoration resize", _resize); Add(grid, "Custom width", _width); Add(grid, "Custom height", _height); Add(grid, "", _aspect);
        Add(grid, "AI Restoration", _aiMode); Add(grid, "AI model", _aiModel); Add(grid, "AI scale", _aiScale); Add(grid, "AI device", _aiDevice); Add(grid, "AI backend path", _aiBackend); Add(grid, "AI models directory", _aiModels);
        Add(grid, "", new Label { Text = "AI scale is an intermediate enhancement scale. Final output resolution remains controlled by Restoration resize or the normal Encode resolution.", AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText });
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(cancel); buttons.Controls.Add(ok); Add(grid, "", buttons); Controls.Add(grid); AcceptButton = ok; CancelButton = cancel;
        FormClosing += (_, e) => { if (DialogResult != DialogResult.OK) return; Commit(); try { Services.VideoRestorationPipeline.Validate(_working, Services.EncodingService.ScaleMode.None); } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); e.Cancel = true; } };
    }
    public VideoRestorationSettings Settings => _working.Clone();
    private void Commit() { _working.Denoise = Get<VideoRestorationStrength>(_denoise); _working.Deblock = Get<VideoRestorationStrength>(_deblock); _working.Deband = Get<VideoRestorationStrength>(_deband); _working.Sharpen = Get<VideoRestorationStrength>(_sharpen); _working.Deinterlace = Get<VideoRestorationDeinterlace>(_deinterlace); _working.Brightness = _brightness.Value; _working.Contrast = _contrast.Value; _working.Saturation = _saturation.Value; _working.Resize = Get<VideoRestorationResize>(_resize); _working.CustomWidth = (int)_width.Value; _working.CustomHeight = (int)_height.Value; _working.PreserveAspectRatio = _aspect.Checked; _working.AiMode = Get<AiRestorationMode>(_aiMode); _working.AiModelId = _aiModel.Text.Trim(); _working.AiScale = Get<AiRestorationScale>(_aiScale); _working.AiDevice = string.IsNullOrWhiteSpace(_aiDevice.Text) ? "Auto" : _aiDevice.Text.Trim(); _working.AiBackendPath = _aiBackend.Text.Trim(); _working.AiModelsDirectory = _aiModels.Text.Trim(); }
    private static NumericUpDown Number(decimal min, decimal max, decimal increment) => new() { Minimum = min, Maximum = max, Increment = increment, DecimalPlaces = 2, Dock = DockStyle.Left, Width = 100 };
    private static ComboBox Choice<T>() where T : struct, Enum { var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; combo.Items.AddRange(Enum.GetValues<T>().Cast<object>().ToArray()); return combo; }
    private static void Set<T>(ComboBox combo, T value) where T : struct, Enum => combo.SelectedItem = value;
    private static T Get<T>(ComboBox combo) where T : struct, Enum => combo.SelectedItem is T value ? value : default;
    private static void Add(TableLayoutPanel grid, string label, Control value) { int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); grid.Controls.Add(value, 1, row); }
}
