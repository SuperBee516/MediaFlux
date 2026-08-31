using MediaFlux.Models;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private void InitializePresetMenu()
        {
            var savePreset = new ToolStripMenuItem("Save Current Encode Preset…", null, SaveCurrentPreset_Click);
            _applyPresetToolStripMenuItem = new ToolStripMenuItem("Apply Encode Preset");
            var managePresets = new ToolStripMenuItem("Manage Encode Presets…", null, ManagePresets_Click);

            toolsToolStripMenuItem.DropDownItems.Insert(0, new ToolStripSeparator());
            toolsToolStripMenuItem.DropDownItems.Insert(0, managePresets);
            toolsToolStripMenuItem.DropDownItems.Insert(0, _applyPresetToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(0, savePreset);

            RefreshPresetMenu();
        }

        private void RefreshPresetMenu()
        {
            if (_applyPresetToolStripMenuItem == null)
                return;

            _applyPresetToolStripMenuItem.DropDownItems.Clear();
            var presets = _presetService.LoadAll();
            _applyPresetToolStripMenuItem.Enabled = presets.Count > 0;

            foreach (var preset in presets)
            {
                var item = new ToolStripMenuItem(preset.Name) { Tag = preset };
                item.Click += (_, __) =>
                {
                    if (item.Tag is EncodingPreset p)
                        ApplyPresetToUi(p);
                };
                _applyPresetToolStripMenuItem.DropDownItems.Add(item);
            }
        }

        private void SaveCurrentPreset_Click(object? sender, EventArgs e)
        {
            if (!TryPromptPresetName(out var name))
                return;

            var preset = CapturePresetFromUi(name);
            _presetService.SaveOrReplace(preset);
            RefreshPresetMenu();
            toolStripStatusLabel1.Text = $"Saved encode preset \"{preset.Name}\".";
        }

        private void ManagePresets_Click(object? sender, EventArgs e)
        {
            using var dlg = new MediaFluxForm
            {
                Text = "Manage Encode Presets",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ClientSize = new Size(360, 320),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false
            };

            var list = new ListBox
            {
                Location = new Point(12, 12),
                Size = new Size(336, 230),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var btnApply = new Button { Text = "Apply", Location = new Point(12, 260), Size = new Size(80, 28) };
            var btnDelete = new Button { Text = "Delete", Location = new Point(100, 260), Size = new Size(80, 28) };
            var btnClose = new Button { Text = "Close", Location = new Point(268, 260), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };

            void LoadList()
            {
                list.Items.Clear();
                foreach (var preset in _presetService.LoadAll())
                    list.Items.Add(preset.Name);
            }

            btnApply.Click += (_, __) =>
            {
                if (list.SelectedItem is not string selected)
                    return;

                var preset = _presetService.LoadAll()
                    .FirstOrDefault(p => string.Equals(p.Name, selected, StringComparison.OrdinalIgnoreCase));
                if (preset != null)
                    ApplyPresetToUi(preset);
            };

            btnDelete.Click += (_, __) =>
            {
                if (list.SelectedItem is not string selected)
                    return;

                if (MessageBox.Show(this, $"Delete preset \"{selected}\"?", "Delete Preset",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                _presetService.Delete(selected);
                LoadList();
                RefreshPresetMenu();
            };

            dlg.Controls.AddRange(new Control[] { list, btnApply, btnDelete, btnClose });
            dlg.CancelButton = btnClose;
            LoadList();
            dlg.ShowDialog(this);
            RefreshPresetMenu();
        }

        private EncodingPreset CapturePresetFromUi(string name)
        {
            double? manualTarget = null;
            if (!chkAutoTargetSize.Checked && double.TryParse(txtTargetSize.Text, out var mb) && mb > 0)
                manualTarget = mb;

            return new EncodingPreset
            {
                Name = name,
                AutoTargetSize = chkAutoTargetSize.Checked,
                ManualTargetMb = manualTarget,
                CompressionProfile = comboCompressionProfile.Text,
                EncoderMode = comboEncoderMode.Text,
                VideoFormat = comboVideoFormat.Text,
                EncoderId = GetSelectedEncoderId(),
                VideoCodec = GetSelectedVideoCodecFamily().ToString(),
                ScaleMode = comboResolution?.Text ?? "",
                EncoderPreset = GetSelectedEncoderPreset(),
                QualityValue = nudAutoQuality == null
                    ? null
                    : (int)nudAutoQuality.Value,
                TenBit = chkTenBit?.Checked == true,
                AudioChannels = comboAudioChannels?.Text ?? "",
                LimitGpuEncodingQueueToOneJob = _config.LimitGpuEncodingQueueToOneJob,
                EnableOutputSuffix = _config.EnableOutputSuffix,
                EnableCodecSuffix = _config.EnableCodecSuffix,
                OutputSuffix = _config.OutputSuffix,
                OutputContainer = GetSelectedOutputContainer().ToString(),
                Restoration = _config.VideoRestoration.Clone()
            };
        }

        private void ApplyPresetToUi(EncodingPreset preset)
        {
            _applyingEncodeDropdownSettings = true;
            try
            {
                chkAutoTargetSize.Checked = preset.AutoTargetSize;
                if (!preset.AutoTargetSize && preset.ManualTargetMb.HasValue)
                    txtTargetSize.Text = preset.ManualTargetMb.Value.ToString("0.##");

                SelectComboText(comboCompressionProfile, preset.CompressionProfile);

                VideoCodecFamily codecFamily =
                    VideoEncoderCompatibility.ParseCodecFamily(
                        string.IsNullOrWhiteSpace(preset.VideoCodec)
                            ? preset.VideoFormat
                            : preset.VideoCodec);
                string encoderId =
                    VideoEncoderCompatibility.ResolveEncoderId(
                        string.IsNullOrWhiteSpace(preset.EncoderId)
                            ? preset.EncoderMode
                            : preset.EncoderId,
                        codecFamily);
                SelectEncoderById(encoderId);
                RefreshVideoFormatItems(codecFamily);
                RefreshEncoderPresetItems(
                    string.IsNullOrWhiteSpace(preset.EncoderPreset)
                        ? preset.NvencPreset
                        : preset.EncoderPreset);
                if (comboResolution != null)
                    SelectComboText(comboResolution, preset.ScaleMode);
                if (comboAudioChannels != null)
                    SelectComboText(comboAudioChannels, preset.AudioChannels);
                if (nudAutoQuality != null && preset.QualityValue.HasValue)
                {
                    nudAutoQuality.Value = Math.Clamp(
                        preset.QualityValue.Value,
                        (int)nudAutoQuality.Minimum,
                        (int)nudAutoQuality.Maximum);
                }
                if (chkTenBit != null)
                    chkTenBit.Checked = preset.TenBit;

                _config.LimitGpuEncodingQueueToOneJob =
                    preset.LimitGpuEncodingQueueToOneJob ?? !preset.DualNvenc;

                _config.EnableOutputSuffix = preset.EnableOutputSuffix;
                _config.EnableCodecSuffix = preset.EnableCodecSuffix;
                _config.OutputSuffix = preset.OutputSuffix ?? "";
                _config.VideoRestoration = preset.Restoration?.Clone() ?? new VideoRestorationSettings();
                SelectOutputContainer(preset.OutputContainer);
                _config.LastOutputContainer = GetSelectedOutputContainer().ToString();
                _config.LastCompressionProfile = comboCompressionProfile.Text;
                _config.LastEncoderId = GetSelectedEncoderId();
                _config.LastVideoCodec =
                    GetSelectedVideoCodecFamily().ToString();
                _config.LastEncoderPreset = GetSelectedEncoderPreset();
                _config.LastEncodingSpeedPreset =
                    comboEncoderPreset?.Text ??
                    _config.LastEncodingSpeedPreset;
                if (nudAutoQuality != null)
                    _config.LastQualityValue = (int)nudAutoQuality.Value;
                _config.Save(_configPath);
            }
            finally
            {
                _applyingEncodeDropdownSettings = false;
            }

            UpdateEncoderUiState();
            UpdateEncodePreview();
            SafeRefreshEstimates();
            toolStripStatusLabel1.Text = $"Applied encode preset \"{preset.Name}\".";
        }

        private bool TryPromptPresetName(out string name)
        {
            name = "";
            using var dlg = new MediaFluxForm
            {
                Text = "Save Encode Preset",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ClientSize = new Size(360, 120),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false
            };

            var label = new Label { Text = "Preset name:", AutoSize = true, Location = new Point(12, 16) };
            var input = new TextBox { Location = new Point(100, 12), Size = new Size(248, 23) };
            var ok = new Button { Text = "OK", Location = new Point(188, 72), Size = new Size(75, 26), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Location = new Point(273, 72), Size = new Size(75, 26), DialogResult = DialogResult.Cancel };

            dlg.Controls.AddRange(new Control[] { label, input, ok, cancel });
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return false;

            name = input.Text.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                return true;

            MessageBox.Show(this, "Enter a preset name.", "Save Preset",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
    }
}
