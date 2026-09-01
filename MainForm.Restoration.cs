using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

public partial class MainForm
{
    private ComboBox? _restorationPreset;
    private ComboBox? _restorationBuiltInPreset, _restorationProfile;
    private Label? _restorationAnalysis;
    private FlowLayoutPanel? _restorationCustomSettings;
    private Button? _restorationAdvanced, _restorationAnalyze, _restorationPreview, _restorationApply, _restorationApplyBuiltInPreset, _restorationSaveProfile, _restorationManageProfiles;
    private ToolTip? _restorationTips;
    private VideoRestorationRecommendation? _pendingRestorationRecommendation;
    private VideoRestorationAnalysisResult? _lastRestorationAnalysis;
    private bool _updatingRestorationProfiles;
    private sealed record RestorationPresetChoice(BuiltInRestorationPreset Preset, string DisplayName) { public override string ToString() => DisplayName; }
    private void AddVideoRestorationControls(TableLayoutPanel options)
    {
        _restorationPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _restorationPreset.Name = "RestorationMode";
        _restorationPreset.Items.AddRange(new object[] { "Off", "Auto", "Custom" });
        _restorationPreset.SelectedIndex = (int)_config.VideoRestoration.Mode;
        _restorationAdvanced = new Button { Name = "RestorationAdvanced", Text = "Advanced...", AutoSize = true };
        _restorationAnalyze = new Button { Name = "RestorationAnalyze", Text = "Analyze / Recommend", AutoSize = true };
        _restorationPreview = new Button { Name = "RestorationPreview", Text = "Preview Restoration...", AutoSize = true };
        _restorationApply = new Button { Name = "RestorationApply", Text = "Apply Recommendation", AutoSize = true };
        _restorationBuiltInPreset = new ComboBox { Name = "RestorationBuiltInPreset", DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        _restorationBuiltInPreset.Items.AddRange(new object[]
        {
            new RestorationPresetChoice(BuiltInRestorationPreset.ClassicCartoon, "Classic Cartoon"),
            new RestorationPresetChoice(BuiltInRestorationPreset.Anime, "Anime"),
            new RestorationPresetChoice(BuiltInRestorationPreset.DvdUpscale, "DVD Upscale"),
            new RestorationPresetChoice(BuiltInRestorationPreset.VhsCleanup, "VHS Cleanup"),
            new RestorationPresetChoice(BuiltInRestorationPreset.FilmPreservation, "Film Preservation"),
            new RestorationPresetChoice(BuiltInRestorationPreset.LiveActionHdCleanup, "Live Action HD Cleanup"),
            new RestorationPresetChoice(BuiltInRestorationPreset.LightCleanup, "Light Cleanup"),
            new RestorationPresetChoice(BuiltInRestorationPreset.HeavyRestoration, "Heavy Restoration"),
            new RestorationPresetChoice(BuiltInRestorationPreset.AiGeneralEnhancement, "AI General Enhancement")
        });
        _restorationProfile = new ComboBox { Name = "RestorationProfile", DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        _restorationApplyBuiltInPreset = new Button { Name = "RestorationApplyPreset", Text = "Apply Preset", AutoSize = true };
        _restorationSaveProfile = new Button { Name = "RestorationSaveProfile", Text = "Save Profile", AutoSize = true };
        _restorationManageProfiles = new Button { Name = "RestorationManageProfiles", Text = "Manage Profiles...", AutoSize = true };
        _restorationTips = new ToolTip();
        _restorationAnalysis = new Label { Name = "RestorationStatus", AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText };
        _restorationAdvanced.Click += (_, __) => { using var form = new VideoRestorationSettingsForm(_config.VideoRestoration); if (form.ShowDialog(this) == DialogResult.OK) { _config.VideoRestoration = form.Settings; _config.VideoRestoration.Mode = VideoRestorationMode.Custom; _config.Save(_configPath); UpdateRestorationControlState(); UpdateEncodePreview(); } };
        _restorationAnalyze.Click += async (_, __) =>
        {
            string? path = dgvEncodeQueue.CurrentRow == null ? null : GetPathFromRow(dgvEncodeQueue.CurrentRow);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _restorationAnalysis.Text = "Select an available encode-queue file to analyze."; return; }
            _restorationAnalyze.Enabled = false; _restorationAnalysis.Text = "Analyzing source characteristics…";
            try
            {
                DataGridViewRow? selectedRow = dgvEncodeQueue.CurrentRow;
                var result = await AnalyzeRestorationAsync(path, selectedRow);
                bool animationHint = result.AnimationHint == true;
                VideoRestorationRecommendation recommendation = _pendingRestorationRecommendation!;
                _restorationAnalysis.Text = $"Detected: {result.Width}x{result.Height} {result.Codec}; scan: {result.ScanType}; noise: {result.Noise}; blocking: {result.Blocking}; banding: {result.Banding}; content hint: {(animationHint ? "Animation" : "None")}.\r\nRecommended: {recommendation.Settings.Preset} — {recommendation.Reason}";
                UpdateRestorationControlState();
            }
            catch (Exception ex) { _restorationAnalysis.Text = $"Analysis unavailable: {ex.Message}"; }
            finally { if (_config.VideoRestoration.Mode != VideoRestorationMode.Off) _restorationAnalyze.Enabled = true; }
        };
        _restorationPreview.Click += async (_, __) => await OpenRestorationPreviewAsync();
        _restorationApply.Click += (_, __) => ApplyRestorationRecommendation();
        _restorationBuiltInPreset.SelectedIndexChanged += (_, __) => { if (!_updatingRestorationProfiles) ApplySelectedBuiltInRestorationPreset(); };
        _restorationApplyBuiltInPreset.Click += (_, __) => ApplySelectedBuiltInRestorationPreset();
        _restorationProfile.SelectedIndexChanged += (_, __) => { if (!_updatingRestorationProfiles) LoadSelectedRestorationProfile(); };
        _restorationSaveProfile.Click += (_, __) => SaveRestorationProfile();
        _restorationManageProfiles.Click += (_, __) => ManageRestorationProfiles();
        _restorationPreset.SelectedIndexChanged += (_, __) => { if (_applyingEncodeDropdownSettings) return; _config.VideoRestoration.Mode = (VideoRestorationMode)_restorationPreset.SelectedIndex; _config.Save(_configPath); UpdateRestorationControlState(); UpdateEncodePreview(); };
        int row = options.RowCount;
        options.RowCount += 7;
        for (int i = 0; i < 7; i++) options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.Controls.Add(new Label { Text = "Video restoration / enhancement", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, row);
        options.Controls.Add(_restorationPreset, 0, row + 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; actions.Controls.Add(_restorationAnalyze); actions.Controls.Add(_restorationPreview); actions.Controls.Add(_restorationApply); actions.Controls.Add(_restorationAdvanced);
        options.Controls.Add(actions, 0, row + 2);
        _restorationCustomSettings = new FlowLayoutPanel { Name = "RestorationCustomSettings", AutoSize = true, Dock = DockStyle.Fill };
        _restorationCustomSettings.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(0, 7, 3, 0) }); _restorationCustomSettings.Controls.Add(_restorationBuiltInPreset); _restorationCustomSettings.Controls.Add(_restorationApplyBuiltInPreset);
        _restorationCustomSettings.Controls.Add(new Label { Text = "Profile:", AutoSize = true, Margin = new Padding(10, 7, 3, 0) }); _restorationCustomSettings.Controls.Add(_restorationProfile); _restorationCustomSettings.Controls.Add(_restorationSaveProfile); _restorationCustomSettings.Controls.Add(_restorationManageProfiles);
        options.Controls.Add(_restorationCustomSettings, 0, row + 3);
        options.Controls.Add(_restorationAnalysis, 0, row + 4);
        RefreshRestorationProfileItems();
        UpdateRestorationControlState();
    }

    private void UpdateRestorationControlState()
    {
        if (_restorationAnalysis == null || _restorationAnalyze == null || _restorationPreview == null || _restorationApply == null || _restorationAdvanced == null || _restorationBuiltInPreset == null || _restorationProfile == null || _restorationApplyBuiltInPreset == null || _restorationSaveProfile == null || _restorationManageProfiles == null || _restorationCustomSettings == null) return;
        RestorationModeControlState state = VideoRestorationModeResolver.ControlState(_config.VideoRestoration.Mode);
        _restorationAnalyze.Enabled = state.AnalyzeEnabled;
        _restorationPreview.Enabled = state.PreviewEnabled;
        _restorationApply.Enabled = state.ApplyEnabled;
        _restorationAdvanced.Enabled = state.AdvancedEnabled;
        bool custom = _config.VideoRestoration.Mode == VideoRestorationMode.Custom;
        _restorationCustomSettings.Visible = custom;
        _restorationBuiltInPreset.Enabled = custom; _restorationProfile.Enabled = custom;
        _restorationApplyBuiltInPreset.Enabled = custom && _restorationBuiltInPreset.SelectedItem is RestorationPresetChoice;
        _restorationSaveProfile.Enabled = custom; _restorationManageProfiles.Enabled = custom;
        _restorationAnalysis.Text = state.StatusText;
        _restorationTips?.SetToolTip(_restorationAnalyze, state.AnalyzeEnabled ? "Analyze the selected source and create a restoration recommendation." : state.DisabledToolTip);
        _restorationTips?.SetToolTip(_restorationPreview, state.PreviewEnabled ? "Preview the active restoration plan." : state.DisabledToolTip);
        _restorationTips?.SetToolTip(_restorationApply, state.ApplyEnabled ? "Apply the current recommendation." : state.DisabledToolTip);
        _restorationTips?.SetToolTip(_restorationAdvanced, state.AdvancedEnabled ? "Edit the Advanced restoration settings." : state.DisabledToolTip);
    }

    private RestorationProfileService RestorationProfiles() => new(AppPaths.RestorationProfilesDirectory);

    private void RefreshRestorationProfileItems(string? selectedName = null)
    {
        if (_restorationProfile == null) return;
        string? selected = selectedName ?? _restorationProfile.SelectedItem as string;
        _updatingRestorationProfiles = true;
        try
        {
            _restorationProfile.Items.Clear();
            _restorationProfile.Items.AddRange(RestorationProfiles().LoadAll().Select(profile => (object)profile.Name).ToArray());
            if (!string.IsNullOrWhiteSpace(selected)) _restorationProfile.SelectedItem = _restorationProfile.Items.Cast<object>().OfType<string>().FirstOrDefault(name => string.Equals(name, selected, StringComparison.OrdinalIgnoreCase));
            if (_restorationProfile.SelectedIndex < 0) _restorationProfile.Text = "";
        }
        finally { _updatingRestorationProfiles = false; }
    }

    private void ApplySelectedBuiltInRestorationPreset()
    {
        if (_config.VideoRestoration.Mode != VideoRestorationMode.Custom || _restorationBuiltInPreset?.SelectedItem is not RestorationPresetChoice choice) return;
        VideoRestorationSettings settings = BuiltInRestorationPresetService.Apply(choice.Preset, _config.VideoRestoration);
        settings.AutoRecommendation = _config.VideoRestoration.AutoRecommendation?.Clone();
        ApplyRestorationProfileSettings(settings);
    }

    private void LoadSelectedRestorationProfile()
    {
        if (_config.VideoRestoration.Mode != VideoRestorationMode.Custom || _restorationProfile?.SelectedItem is not string name) return;
        RestorationProfileDocument? profile = RestorationProfiles().LoadAll().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (profile != null) ApplyRestorationProfileSettings(profile.Settings);
    }

    private void ApplyRestorationProfileSettings(VideoRestorationSettings settings)
    {
        _config.VideoRestoration = settings.Clone();
        _applyingEncodeDropdownSettings = true;
        try { if (_restorationPreset != null) _restorationPreset.SelectedIndex = (int)_config.VideoRestoration.Mode; }
        finally { _applyingEncodeDropdownSettings = false; }
        _config.Save(_configPath); UpdateRestorationControlState(); UpdateEncodePreview();
    }

    private void SaveRestorationProfile()
    {
        if (!TryPromptRestorationProfileName("Save Restoration Profile", out string name)) return;
        try { RestorationProfiles().Save(name, _config.VideoRestoration); RefreshRestorationProfileItems(name); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save Restoration Profile", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ManageRestorationProfiles()
    {
        using var dialog = new MediaFluxForm { Text = "Manage Restoration Profiles", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(400, 260), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var profiles = new ListBox { Location = new Point(12, 12), Size = new Size(376, 180) };
        var rename = new Button { Text = "Rename...", Location = new Point(12, 212), Size = new Size(85, 28) };
        var delete = new Button { Text = "Delete", Location = new Point(103, 212), Size = new Size(85, 28) };
        var close = new Button { Text = "Close", Location = new Point(303, 212), Size = new Size(85, 28), DialogResult = DialogResult.OK };
        void Refresh() { string? selected = profiles.SelectedItem as string; profiles.Items.Clear(); profiles.Items.AddRange(RestorationProfiles().LoadAll().Select(profile => (object)profile.Name).ToArray()); profiles.SelectedItem = selected; }
        rename.Click += (_, _) => { if (profiles.SelectedItem is not string current || !TryPromptRestorationProfileName("Rename Restoration Profile", out string name, current)) return; try { RestorationProfiles().Rename(current, name); Refresh(); profiles.SelectedItem = name; } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        delete.Click += (_, _) => { if (profiles.SelectedItem is not string selected || MessageBox.Show(dialog, $"Delete restoration profile '{selected}'?", dialog.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return; RestorationProfiles().Delete(selected); Refresh(); };
        dialog.Controls.AddRange(new Control[] { profiles, rename, delete, close }); dialog.AcceptButton = close; Refresh(); dialog.ShowDialog(this);
        RefreshRestorationProfileItems();
    }

    private bool TryPromptRestorationProfileName(string title, out string name, string initial = "")
    {
        name = "";
        using var dialog = new MediaFluxForm { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(360, 120), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var label = new Label { Text = "Profile name:", AutoSize = true, Location = new Point(12, 16) };
        var input = new TextBox { Location = new Point(100, 12), Size = new Size(248, 23), Text = initial };
        var ok = new Button { Text = "OK", Location = new Point(188, 72), Size = new Size(75, 26), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(273, 72), Size = new Size(75, 26), DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange(new Control[] { label, input, ok, cancel }); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        name = input.Text.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return true;
        MessageBox.Show(this, "Enter a profile name.", title, MessageBoxButtons.OK, MessageBoxIcon.Information); return false;
    }

    private void ApplyRestorationRecommendation()
    {
        if (_pendingRestorationRecommendation == null) return;
        VideoRestorationSettings recommendation = _pendingRestorationRecommendation.Settings.Clone();
        if (_config.VideoRestoration.Mode == VideoRestorationMode.Auto)
            _config.VideoRestoration.AutoRecommendation = recommendation;
        else if (_config.VideoRestoration.Mode == VideoRestorationMode.Custom)
        {
            recommendation.Mode = VideoRestorationMode.Custom;
            recommendation.AutoRecommendation = _config.VideoRestoration.AutoRecommendation?.Clone();
            _config.VideoRestoration = recommendation;
        }
        _config.Save(_configPath);
        UpdateRestorationControlState();
        UpdateEncodePreview();
        ErrorLogService.Append(AppPaths.UserDataDirectory, "Restoration recommendation accepted", details: _pendingRestorationRecommendation.Reason);
    }

    private async Task<VideoRestorationAnalysisResult> AnalyzeRestorationAsync(string path, DataGridViewRow? selectedRow, CancellationToken token = default)
    {
        var tools = FfmpegToolResolver.Resolve(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath);
        if (tools.HasFfmpeg)
        {
            FfmpegRestorationCapabilities capabilities = await new FfmpegRestorationCapabilityService(log: message => ErrorLogService.Append(AppPaths.UserDataDirectory, message)).GetAsync(tools.FfmpegPath, token);
            if (capabilities.State == FfmpegFilterInventoryState.Available) VideoRestorationPipeline.SetAvailableFilters(capabilities.Filters);
        }
        bool animationHint = selectedRow?.Tag is RowMeta meta && meta.ContentHint == SmartEncodeContentHint.Animation;
        var service = new VideoRestorationAnalysisService(AppPaths.InstallDirectory, _config.FfprobePath, _config.FfmpegPath, message => ErrorLogService.Append(AppPaths.UserDataDirectory, message));
        VideoRestorationAnalysisResult result = await service.AnalyzeAsync(path, animationHint, token);
        _lastRestorationAnalysis = result;
        VideoRestorationSettings? explicitSettings = _config.VideoRestoration.Mode == VideoRestorationMode.Custom ? _config.VideoRestoration : null;
        _pendingRestorationRecommendation = VideoRestorationRecommendationService.Recommend(result, animationHint, explicitSettings);
        return result;
    }

    private async Task OpenRestorationPreviewAsync()
    {
        DataGridViewRow? row = dgvEncodeQueue.CurrentRow; string? path = row == null ? null : GetPathFromRow(row);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _restorationAnalysis!.Text = "Select an available encode-queue file to preview restoration."; return; }
        double duration = row?.Tag is RowMeta meta ? meta.DurationSec : 0;
        if (duration <= 0) duration = await Task.Run(() => _mediaInfoService.GetDurationSeconds(path));
        if (duration <= 0) { _restorationAnalysis!.Text = "MediaFlux could not determine the selected video's duration for preview."; return; }
        var selection = new VideoRestorationPreviewSelection(_config.VideoRestoration, _pendingRestorationRecommendation);
        var service = new VideoRestorationPreviewService(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath, message => ErrorLogService.Append(AppPaths.UserDataDirectory, message));
        using var form = new VideoRestorationPreviewForm(path, TimeSpan.FromSeconds(duration), GetSelectedScaleMode(), service, selection, _lastRestorationAnalysis, _config.ExternalPlayerPath,
            settings => { _config.VideoRestoration = settings.Clone(); _restorationPreset!.SelectedIndex = (int)settings.Mode; _config.Save(_configPath); UpdateRestorationControlState(); UpdateEncodePreview(); },
            async token =>
            {
                VideoRestorationAnalysisResult result = await AnalyzeRestorationAsync(path, row, token);
                VideoRestorationRecommendation recommendation = _pendingRestorationRecommendation!;
                selection.SetRecommendation(recommendation);
                return new VideoRestorationPreviewAnalysisUpdate(result, recommendation);
            });
        form.ShowDialog(this);
    }
}
