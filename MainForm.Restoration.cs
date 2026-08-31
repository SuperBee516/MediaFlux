using MediaFlux.Models;

namespace MediaFlux;

public partial class MainForm
{
    private ComboBox? _restorationPreset;
    private void AddVideoRestorationControls(TableLayoutPanel options)
    {
        _restorationPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _restorationPreset.Items.AddRange(new object[] { "Off", "Vintage Animation – Light", "Vintage Animation – Restore", "DVD Animation Restore", "VHS / TV Capture Restore", "Custom" });
        _restorationPreset.SelectedIndex = (int)_config.VideoRestoration.Preset;
        var advanced = new Button { Text = "Advanced...", AutoSize = true };
        advanced.Click += (_, __) => { using var form = new VideoRestorationSettingsForm(_config.VideoRestoration); if (form.ShowDialog(this) == DialogResult.OK) { _config.VideoRestoration = form.Settings; _config.VideoRestoration.Preset = VideoRestorationPreset.Custom; _restorationPreset.SelectedIndex = (int)VideoRestorationPreset.Custom; _config.Save(_configPath); } };
        _restorationPreset.SelectedIndexChanged += (_, __) => { if (_applyingEncodeDropdownSettings) return; _config.VideoRestoration.Preset = (VideoRestorationPreset)_restorationPreset.SelectedIndex; _config.Save(_configPath); UpdateEncodePreview(); };
        int row = options.RowCount;
        options.RowCount += 3;
        options.RowStyles.Add(new RowStyle(SizeType.AutoSize)); options.RowStyles.Add(new RowStyle(SizeType.AutoSize)); options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        options.Controls.Add(new Label { Text = "Video restoration / enhancement", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, row);
        options.Controls.Add(_restorationPreset, 0, row + 1);
        options.Controls.Add(advanced, 0, row + 2);
    }
}
