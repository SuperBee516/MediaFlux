using MediaFlux.Models;
using MediaFlux.Services;

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
    private readonly ComboBox _aiModel = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _aiScale = Choice<AiRestorationScale>();
    private readonly ComboBox _aiBackendSelection = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly TextBox _aiDevice = new() { Dock = DockStyle.Fill, Text = "Auto" };
    private readonly TextBox _aiBackend = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional local NCNN/Vulkan executable" };
    private readonly TextBox _aiModels = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional models directory" };
    private readonly Label _aiModelStatus = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText };
    private readonly ToolTip _toolTip = new();
    private string _persistedModelId = "";
    private AiBackendSelection _lastAvailableBackendSelection;
    private bool _refreshingModels;
    private sealed record ModelChoice(AiRestorationModel Model)
    {
        public override string ToString() => $"{Model.DisplayName} · {(int)Model.SupportedScales[0]}x ({Model.BackendModelName})";
    }
    private sealed record BackendChoice(AiBackendSelection Selection, string DisplayName, bool IsEnabled, string? UnavailableReason)
    {
        public override string ToString() => IsEnabled ? DisplayName : $"{DisplayName} (Unavailable)";
    }

    public VideoRestorationSettingsForm(VideoRestorationSettings settings)
    {
        _working = settings.Clone(); Text = "Video Restoration Advanced Settings"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(620, 690);
        Set(_denoise, _working.Denoise); Set(_deblock, _working.Deblock); Set(_deband, _working.Deband); Set(_sharpen, _working.Sharpen); Set(_deinterlace, _working.Deinterlace); Set(_resize, _working.Resize);
        _brightness.Value = _working.Brightness; _contrast.Value = _working.Contrast; _saturation.Value = _working.Saturation; _width.Value = Math.Clamp(_working.CustomWidth == 0 ? 1920 : _working.CustomWidth, 64, 7680); _height.Value = Math.Clamp(_working.CustomHeight == 0 ? 1080 : _working.CustomHeight, 64, 4320); _aspect.Checked = _working.PreserveAspectRatio;
        Set(_aiMode, _working.AiMode); Set(_aiScale, _working.AiScale); SetBackendChoices(null); SelectBackend(_working.AiBackendSelection); _lastAvailableBackendSelection = _working.AiBackendSelection; _persistedModelId = _working.AiModelId; _aiDevice.Text = string.IsNullOrWhiteSpace(_working.AiDevice) ? "Auto" : _working.AiDevice; _aiBackend.Text = _working.AiBackendPath; _aiModels.Text = _working.AiModelsDirectory;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(grid, "Denoise", _denoise); Add(grid, "Deblock / artifacts", _deblock); Add(grid, "Deband", _deband); Add(grid, "Sharpen", _sharpen); Add(grid, "Deinterlace", _deinterlace); Add(grid, "Brightness", _brightness); Add(grid, "Contrast", _contrast); Add(grid, "Saturation", _saturation); Add(grid, "Restoration resize", _resize); Add(grid, "Custom width", _width); Add(grid, "Custom height", _height); Add(grid, "", _aspect);
        Add(grid, "AI Restoration", _aiMode); Add(grid, "AI backend", _aiBackendSelection); Add(grid, "AI model", _aiModel); Add(grid, "AI scale", _aiScale); Add(grid, "AI device", _aiDevice); Add(grid, "AI backend path", _aiBackend); Add(grid, "AI models directory", _aiModels);
        var refreshModels = new Button { Text = "Refresh detected AI models", AutoSize = true }; refreshModels.Click += async (_, _) => await RefreshModelsAsync(); Add(grid, "", refreshModels); Add(grid, "", _aiModelStatus);
        Add(grid, "", new Label { Text = "AI scale is an intermediate enhancement scale. Final output resolution remains controlled by Restoration resize or the normal Encode resolution.", AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText });
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(cancel); buttons.Controls.Add(ok); Add(grid, "", buttons); Controls.Add(grid); AcceptButton = ok; CancelButton = cancel;
        Shown += async (_, _) => await RefreshModelsAsync();
        _aiMode.SelectedIndexChanged += async (_, _) => await RefreshModelsAsync(); _aiScale.SelectedIndexChanged += async (_, _) => await RefreshModelsAsync();
        _aiBackendSelection.SelectedIndexChanged += async (_, _) =>
        {
            if (_refreshingModels) return;
            if (_aiBackendSelection.SelectedItem is BackendChoice choice && !choice.IsEnabled)
            {
                _toolTip.Show(choice.UnavailableReason ?? "This provider is unavailable.", _aiBackendSelection, 3000);
                SelectBackend(_lastAvailableBackendSelection);
                return;
            }
            if (_aiBackendSelection.SelectedItem is BackendChoice available) _lastAvailableBackendSelection = available.Selection;
            await RefreshModelsAsync();
        };
        _aiBackendSelection.DrawItem += DrawBackendChoice;
        FormClosing += (_, e) => { if (DialogResult != DialogResult.OK) return; if (Get<AiRestorationMode>(_aiMode) != AiRestorationMode.Off && _aiModel.SelectedItem is not ModelChoice) { MessageBox.Show(this, "AI model unavailable: configure the backend/models directory and select a detected compatible model.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); e.Cancel = true; return; } Commit(); try { Services.VideoRestorationPipeline.Validate(_working, Services.EncodingService.ScaleMode.None); } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); e.Cancel = true; } };
    }
    public VideoRestorationSettings Settings => _working.Clone();
    private void Commit() { _working.Denoise = Get<VideoRestorationStrength>(_denoise); _working.Deblock = Get<VideoRestorationStrength>(_deblock); _working.Deband = Get<VideoRestorationStrength>(_deband); _working.Sharpen = Get<VideoRestorationStrength>(_sharpen); _working.Deinterlace = Get<VideoRestorationDeinterlace>(_deinterlace); _working.Brightness = _brightness.Value; _working.Contrast = _contrast.Value; _working.Saturation = _saturation.Value; _working.Resize = Get<VideoRestorationResize>(_resize); _working.CustomWidth = (int)_width.Value; _working.CustomHeight = (int)_height.Value; _working.PreserveAspectRatio = _aspect.Checked; _working.AiMode = Get<AiRestorationMode>(_aiMode); _working.AiModelId = (_aiModel.SelectedItem as ModelChoice)?.Model.Id ?? _persistedModelId; _working.AiScale = Get<AiRestorationScale>(_aiScale); _working.AiDevice = string.IsNullOrWhiteSpace(_aiDevice.Text) ? "Auto" : _aiDevice.Text.Trim(); _working.AiBackendPath = _aiBackend.Text.Trim(); _working.AiModelsDirectory = _aiModels.Text.Trim(); _working.AiBackendSelection = GetBackendSelection(); }
    private async Task RefreshModelsAsync()
    {
        if (_refreshingModels) return;
        _refreshingModels = true;
        try
        {
        string desiredModelId = (_aiModel.SelectedItem as ModelChoice)?.Model.Id ?? _persistedModelId;
        AiRestorationMode mode = Get<AiRestorationMode>(_aiMode); _aiModel.Items.Clear(); _aiModel.Enabled = false; _aiScale.Enabled = false;
        if (mode == AiRestorationMode.Off) { _aiModelStatus.Text = "Active Provider: Not active" + Environment.NewLine + "Status: AI restoration is Off" + Environment.NewLine + "Models: Unavailable" + Environment.NewLine + "Scale: Unavailable" + Environment.NewLine + "Version: Unavailable"; return; }
        _aiModelStatus.Text = "Refreshing provider status and compatible models…";
        var probe = _working.Clone(); probe.AiMode = mode; probe.AiScale = Get<AiRestorationScale>(_aiScale); probe.AiDevice = string.IsNullOrWhiteSpace(_aiDevice.Text) ? "Auto" : _aiDevice.Text.Trim(); probe.AiBackendPath = _aiBackend.Text.Trim(); probe.AiModelsDirectory = _aiModels.Text.Trim(); probe.AiBackendSelection = GetBackendSelection();
        try
        {
            var manager = new AiBackendManager(AppPaths.InstallDirectory);
            IReadOnlyList<AiBackendMetadata> metadata = await manager.DiscoverAsync(probe);
            SetBackendChoices(metadata);
            AiBackendMetadata selectedMetadata = AiConfigurationUiPresentation.ResolveProvider(probe.AiBackendSelection, metadata);
            if (!selectedMetadata.IsReady)
            {
                _aiModelStatus.Text = AiConfigurationUiPresentation.BuildSummary(probe.AiBackendSelection, selectedMetadata, 0, null).ToDisplayText();
                return;
            }

            AiRestorationCapabilities capabilities = await (await manager.SelectAsync(probe)).GetCapabilitiesAsync(probe);
            IReadOnlyList<AiRestorationScale> scales = AiConfigurationUiPresentation.CompatibleScales(mode, capabilities.Models);
            AiRestorationScale? selectedScale = AiConfigurationUiPresentation.SelectNearestScale(probe.AiScale, scales);
            ReplaceScales(scales, selectedScale);
            if (selectedScale is null)
            {
                _aiModelStatus.Text = AiConfigurationUiPresentation.BuildSummary(probe.AiBackendSelection, selectedMetadata, 0, null).ToDisplayText();
                return;
            }

            probe.AiScale = selectedScale.Value;
            ModelChoice[] choices = AiConfigurationUiPresentation.CompatibleModels(mode, probe.AiScale, capabilities.Models).Select(model => new ModelChoice(model)).ToArray();
            _aiModel.Items.AddRange(choices.Cast<object>().ToArray());
            string logical = Services.AiRestorationBackendService.NormalizeLogicalModelId(desiredModelId);
            _aiModel.SelectedItem = choices.FirstOrDefault(choice => choice.Model.Id.Equals(logical, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
            _aiModel.Enabled = choices.Length > 0;
            if (_aiModel.SelectedItem is ModelChoice selectedModel) _persistedModelId = selectedModel.Model.Id;
            _aiModelStatus.Text = AiConfigurationUiPresentation.BuildSummary(probe.AiBackendSelection, selectedMetadata, choices.Length, selectedScale).ToDisplayText();
        }
        catch (Exception ex) { _aiModelStatus.Text = "Active Provider: Unavailable" + Environment.NewLine + "Status: ✖ " + ex.Message + Environment.NewLine + "Models: Unavailable" + Environment.NewLine + "Scale: Unavailable" + Environment.NewLine + "Version: Unavailable"; }
        }
        finally { _refreshingModels = false; }
    }
    private void ReplaceScales(IReadOnlyList<AiRestorationScale> scales, AiRestorationScale? selected)
    {
        _aiScale.Items.Clear(); _aiScale.Items.AddRange(scales.Cast<object>().ToArray()); _aiScale.Enabled = scales.Count > 0;
        if (selected is not null) _aiScale.SelectedItem = selected.Value;
    }
    private void SetBackendChoices(IReadOnlyList<AiBackendMetadata>? metadata)
    {
        AiBackendSelection selected = GetBackendSelection();
        AiBackendMetadata? ncnn = metadata?.FirstOrDefault(item => item.Id == "ncnn-vulkan");
        AiBackendMetadata? tensorRt = metadata?.FirstOrDefault(item => item.Id == "nvidia-tensorrt");
        _aiBackendSelection.Items.Clear();
        _aiBackendSelection.Items.AddRange(new object[]
        {
            new BackendChoice(AiBackendSelection.Auto, "Auto", true, null),
            new BackendChoice(AiBackendSelection.NcnnVulkan, "NCNN Vulkan", ncnn?.IsAvailable ?? true, ncnn?.Reason),
            new BackendChoice(AiBackendSelection.NvidiaTensorRt, "NVIDIA TensorRT", tensorRt?.IsAvailable ?? true, tensorRt?.Reason)
        });
        SelectBackend(selected);
        if (_aiBackendSelection.SelectedItem is BackendChoice choice && choice.IsEnabled) _lastAvailableBackendSelection = choice.Selection;
        string unavailable = string.Join(Environment.NewLine, _aiBackendSelection.Items.Cast<BackendChoice>().Where(choice => !choice.IsEnabled).Select(choice => $"{choice.DisplayName}: {choice.UnavailableReason ?? "Unavailable"}"));
        _toolTip.SetToolTip(_aiBackendSelection, string.IsNullOrWhiteSpace(unavailable) ? "Select the AI provider. Auto resolves to the best ready provider." : unavailable);
    }
    private void SelectBackend(AiBackendSelection selection) => _aiBackendSelection.SelectedItem = _aiBackendSelection.Items.Cast<BackendChoice>().FirstOrDefault(choice => choice.Selection == selection) ?? _aiBackendSelection.Items.Cast<BackendChoice>().First();
    private AiBackendSelection GetBackendSelection() => (_aiBackendSelection.SelectedItem as BackendChoice)?.Selection ?? AiBackendSelection.Auto;
    private void DrawBackendChoice(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        e.DrawBackground();
        if (_aiBackendSelection.Items[e.Index] is not BackendChoice choice) return;
        Color color = choice.IsEnabled ? e.ForeColor : SystemColors.GrayText;
        TextRenderer.DrawText(e.Graphics, choice.ToString(), e.Font, e.Bounds, color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        e.DrawFocusRectangle();
    }
    private static NumericUpDown Number(decimal min, decimal max, decimal increment) => new() { Minimum = min, Maximum = max, Increment = increment, DecimalPlaces = 2, Dock = DockStyle.Left, Width = 100 };
    private static ComboBox Choice<T>() where T : struct, Enum { var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; combo.Items.AddRange(Enum.GetValues<T>().Cast<object>().ToArray()); return combo; }
    private static void Set<T>(ComboBox combo, T value) where T : struct, Enum => combo.SelectedItem = value;
    private static T Get<T>(ComboBox combo) where T : struct, Enum => combo.SelectedItem is T value ? value : default;
    private static void Add(TableLayoutPanel grid, string label, Control value) { int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); grid.Controls.Add(value, 1, row); }
}
