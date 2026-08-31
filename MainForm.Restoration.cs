using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

public partial class MainForm
{
    private ComboBox? _restorationPreset;
    private Label? _restorationAnalysis;
    private VideoRestorationRecommendation? _pendingRestorationRecommendation;
    private void AddVideoRestorationControls(TableLayoutPanel options)
    {
        _restorationPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _restorationPreset.Items.AddRange(new object[] { "Off", "Vintage Animation – Light", "Vintage Animation – Restore", "DVD Animation Restore", "VHS / TV Capture Restore", "Custom" });
        _restorationPreset.SelectedIndex = (int)_config.VideoRestoration.Preset;
        var advanced = new Button { Text = "Advanced...", AutoSize = true };
        var analyze = new Button { Text = "Analyze / Recommend", AutoSize = true };
        var apply = new Button { Text = "Apply Recommendation", AutoSize = true, Enabled = false };
        _restorationAnalysis = new Label { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText, Text = "Restoration is off unless you select or apply a setting." };
        advanced.Click += (_, __) => { using var form = new VideoRestorationSettingsForm(_config.VideoRestoration); if (form.ShowDialog(this) == DialogResult.OK) { _config.VideoRestoration = form.Settings; _config.VideoRestoration.Preset = VideoRestorationPreset.Custom; _restorationPreset.SelectedIndex = (int)VideoRestorationPreset.Custom; _config.Save(_configPath); } };
        analyze.Click += async (_, __) =>
        {
            string? path = dgvEncodeQueue.CurrentRow == null ? null : GetPathFromRow(dgvEncodeQueue.CurrentRow);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _restorationAnalysis.Text = "Select an available encode-queue file to analyze."; return; }
            analyze.Enabled = false; _restorationAnalysis.Text = "Analyzing source characteristics…";
            try
            {
                var tools = FfmpegToolResolver.Resolve(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath);
                if (tools.HasFfmpeg)
                {
                    FfmpegRestorationCapabilities capabilities = await new FfmpegRestorationCapabilityService(log: message => ErrorLogService.Append(AppPaths.UserDataDirectory, message)).GetAsync(tools.FfmpegPath);
                    if (capabilities.State == FfmpegFilterInventoryState.Available)
                        VideoRestorationPipeline.SetAvailableFilters(capabilities.Filters);
                }
                var service = new Services.VideoRestorationAnalysisService(AppPaths.InstallDirectory, _config.FfprobePath, _config.FfmpegPath, message => ErrorLogService.Append(AppPaths.UserDataDirectory, message));
                DataGridViewRow? selectedRow = dgvEncodeQueue.CurrentRow;
                bool animationHint = selectedRow?.Tag is RowMeta meta && meta.ContentHint == SmartEncodeContentHint.Animation;
                var result = await service.AnalyzeAsync(path, animationHint);
                _pendingRestorationRecommendation = Services.VideoRestorationRecommendationService.Recommend(result, animationHint, _config.VideoRestoration.Preset == VideoRestorationPreset.Off ? null : _config.VideoRestoration);
                _restorationAnalysis.Text = $"Detected: {result.Width}x{result.Height} {result.Codec}; scan: {result.ScanType}; noise: {result.Noise}; blocking: {result.Blocking}; banding: {result.Banding}; content hint: {(animationHint ? "Animation" : "None")}.\r\nRecommended: {_pendingRestorationRecommendation.Settings.Preset} — {_pendingRestorationRecommendation.Reason}";
                apply.Enabled = _pendingRestorationRecommendation.Settings.Preset != VideoRestorationPreset.Off;
            }
            catch (Exception ex) { _restorationAnalysis.Text = $"Analysis unavailable: {ex.Message}"; }
            finally { analyze.Enabled = true; }
        };
        apply.Click += (_, __) => { if (_pendingRestorationRecommendation == null) return; _config.VideoRestoration = _pendingRestorationRecommendation.Settings.Clone(); _restorationPreset.SelectedIndex = (int)_config.VideoRestoration.Preset; _config.Save(_configPath); _restorationAnalysis!.Text += "\r\nRecommendation applied; review Advanced settings before encoding if needed."; ErrorLogService.Append(AppPaths.UserDataDirectory, "Restoration recommendation accepted", details: _pendingRestorationRecommendation.Reason); };
        _restorationPreset.SelectedIndexChanged += (_, __) => { if (_applyingEncodeDropdownSettings) return; _config.VideoRestoration.Preset = (VideoRestorationPreset)_restorationPreset.SelectedIndex; _config.Save(_configPath); UpdateEncodePreview(); };
        int row = options.RowCount;
        options.RowCount += 5;
        for (int i = 0; i < 5; i++) options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.Controls.Add(new Label { Text = "Video restoration / enhancement", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, row);
        options.Controls.Add(_restorationPreset, 0, row + 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; actions.Controls.Add(analyze); actions.Controls.Add(apply); actions.Controls.Add(advanced);
        options.Controls.Add(actions, 0, row + 2);
        options.Controls.Add(_restorationAnalysis, 0, row + 3);
    }
}
