using MediaFlux.Models;

namespace MediaFlux
{
    internal sealed class StorageSavingsSettingsForm : MediaFluxForm
    {
        private readonly CheckBox _enabled = new();
        private readonly RadioButton _qualityTarget = new();
        private readonly RadioButton _bitrateTarget = new();
        private readonly NumericUpDown _qualityValue = new();
        private readonly NumericUpDown _bitratePercent = new();

        public StorageSavingsOptions Options { get; private set; }

        public StorageSavingsSettingsForm(StorageSavingsOptions options)
        {
            Options = (options ?? new StorageSavingsOptions()).CloneNormalized();

            Text = "Storage Savings Mode";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(570, 315);
            Padding = new Padding(14);

            var title = new Label
            {
                Text = "Storage savings mode (HEVC only)",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 15)
            };
            _enabled.Text = "Enable more aggressive HEVC compression";
            _enabled.AutoSize = true;
            _enabled.Location = new Point(16, 47);
            _enabled.Checked = Options.Enabled;

            var warning = new Label
            {
                Text =
                    "Warning: stronger compression can reduce visual quality. " +
                    "Conservative estimation and encoding remain the default when this mode is off.",
                ForeColor = Color.FromArgb(146, 64, 14),
                BackColor = Color.FromArgb(254, 243, 199),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(16, 76),
                Size = new Size(538, 48),
                Padding = new Padding(7)
            };

            _qualityTarget.Text = "Quality target (CQ/CRF/ICQ):";
            _qualityTarget.AutoSize = true;
            _qualityTarget.Location = new Point(28, 145);
            _qualityTarget.Checked = Options.UsesQualityTarget;
            _qualityValue.Minimum = 0;
            _qualityValue.Maximum = 51;
            _qualityValue.Value = Options.QualityValue;
            _qualityValue.Location = new Point(225, 142);
            _qualityValue.Size = new Size(70, 23);

            var qualityHint = new Label
            {
                Text = "Higher values usually create smaller files with lower visual quality.",
                ForeColor = SystemColors.GrayText,
                AutoSize = true,
                Location = new Point(310, 146)
            };

            _bitrateTarget.Text = "Source video bitrate target:";
            _bitrateTarget.AutoSize = true;
            _bitrateTarget.Location = new Point(28, 184);
            _bitrateTarget.Checked = !Options.UsesQualityTarget;
            _bitratePercent.Minimum = 10;
            _bitratePercent.Maximum = 95;
            _bitratePercent.DecimalPlaces = 1;
            _bitratePercent.Increment = 5;
            _bitratePercent.Value =
                (decimal)Options.SourceVideoBitratePercent;
            _bitratePercent.Location = new Point(225, 181);
            _bitratePercent.Size = new Size(70, 23);

            var percent = new Label
            {
                Text = "% (50% targets half of the measured/derived source video bitrate)",
                ForeColor = SystemColors.GrayText,
                AutoSize = true,
                Location = new Point(300, 185)
            };

            var scope = new Label
            {
                Text =
                    "Explicit per-file targets and manual target sizes still take priority. " +
                    "Audio, subtitles, and mapped data are budgeted separately.",
                ForeColor = SystemColors.GrayText,
                Location = new Point(28, 220),
                Size = new Size(515, 38)
            };

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(388, 274),
                Size = new Size(78, 27)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(476, 274),
                Size = new Size(78, 27)
            };

            void ToggleInputs()
            {
                bool enabled = _enabled.Checked;
                _qualityTarget.Enabled = enabled;
                _bitrateTarget.Enabled = enabled;
                _qualityValue.Enabled = enabled && _qualityTarget.Checked;
                _bitratePercent.Enabled = enabled && _bitrateTarget.Checked;
            }

            _enabled.CheckedChanged += (_, _) => ToggleInputs();
            _qualityTarget.CheckedChanged += (_, _) => ToggleInputs();
            _bitrateTarget.CheckedChanged += (_, _) => ToggleInputs();
            ok.Click += (_, _) =>
            {
                Options = new StorageSavingsOptions
                {
                    Enabled = _enabled.Checked,
                    TargetMode = _qualityTarget.Checked
                        ? StorageSavingsOptions.QualityTarget
                        : StorageSavingsOptions.SourceBitrateTarget,
                    QualityValue = (int)_qualityValue.Value,
                    SourceVideoBitratePercent = (double)_bitratePercent.Value
                };
                Options.Normalize();
            };

            Controls.Add(title);
            Controls.Add(_enabled);
            Controls.Add(warning);
            Controls.Add(_qualityTarget);
            Controls.Add(_qualityValue);
            Controls.Add(qualityHint);
            Controls.Add(_bitrateTarget);
            Controls.Add(_bitratePercent);
            Controls.Add(percent);
            Controls.Add(scope);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ToggleInputs();
        }
    }
}
