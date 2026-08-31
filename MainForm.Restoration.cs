using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

public partial class MainForm
{
    private ComboBox? _restorationPreset;
    private Label? _restorationAnalysis;
    private VideoRestorationRecommendation? _pendingRestorationRecommendation;
    private VideoRestorationAnalysisResult? _lastRestorationAnalysis;
    private void AddVideoRestorationControls(TableLayoutPanel options)
    {
        _restorationPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _restorationPreset.Items.AddRange(new object[] { "Off", "Vintage Animation – Light", "Vintage Animation – Restore", "DVD Animation Restore", "VHS / TV Capture Restore", "Custom" });
        _restorationPreset.SelectedIndex = (int)_config.VideoRestoration.Preset;
        var advanced = new Button { Text = "Advanced...", AutoSize = true };
        var analyze = new Button { Text = "Analyze / Recommend", AutoSize = true };
        var preview = new Button { Text = "Preview Restoration...", AutoSize = true };
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
                DataGridViewRow? selectedRow = dgvEncodeQueue.CurrentRow;
                var result = await AnalyzeRestorationAsync(path, selectedRow);
                bool animationHint = result.AnimationHint == true;
                VideoRestorationRecommendation recommendation = _pendingRestorationRecommendation!;
                _restorationAnalysis.Text = $"Detected: {result.Width}x{result.Height} {result.Codec}; scan: {result.ScanType}; noise: {result.Noise}; blocking: {result.Blocking}; banding: {result.Banding}; content hint: {(animationHint ? "Animation" : "None")}.\r\nRecommended: {recommendation.Settings.Preset} — {recommendation.Reason}";
                apply.Enabled = recommendation.Settings.Preset != VideoRestorationPreset.Off;
            }
            catch (Exception ex) { _restorationAnalysis.Text = $"Analysis unavailable: {ex.Message}"; }
            finally { analyze.Enabled = true; }
        };
        preview.Click += async (_, __) => await OpenRestorationPreviewAsync();
        apply.Click += (_, __) => { if (_pendingRestorationRecommendation == null) return; _config.VideoRestoration = _pendingRestorationRecommendation.Settings.Clone(); _restorationPreset.SelectedIndex = (int)_config.VideoRestoration.Preset; _config.Save(_configPath); _restorationAnalysis!.Text += "\r\nRecommendation applied; review Advanced settings before encoding if needed."; ErrorLogService.Append(AppPaths.UserDataDirectory, "Restoration recommendation accepted", details: _pendingRestorationRecommendation.Reason); };
        _restorationPreset.SelectedIndexChanged += (_, __) => { if (_applyingEncodeDropdownSettings) return; _config.VideoRestoration.Preset = (VideoRestorationPreset)_restorationPreset.SelectedIndex; _config.Save(_configPath); UpdateEncodePreview(); };
        int row = options.RowCount;
        options.RowCount += 5;
        for (int i = 0; i < 5; i++) options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.Controls.Add(new Label { Text = "Video restoration / enhancement", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, row);
        options.Controls.Add(_restorationPreset, 0, row + 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; actions.Controls.Add(analyze); actions.Controls.Add(preview); actions.Controls.Add(apply); actions.Controls.Add(advanced);
        options.Controls.Add(actions, 0, row + 2);
        options.Controls.Add(_restorationAnalysis, 0, row + 3);
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
        _pendingRestorationRecommendation = VideoRestorationRecommendationService.Recommend(result, animationHint, _config.VideoRestoration.Preset == VideoRestorationPreset.Off ? null : _config.VideoRestoration);
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
            settings => { _config.VideoRestoration = settings.Clone(); _restorationPreset!.SelectedIndex = (int)settings.Preset; _config.Save(_configPath); UpdateEncodePreview(); },
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
