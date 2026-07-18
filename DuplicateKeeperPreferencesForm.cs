using MediaFlux.Models;
using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MediaFlux
{
    internal sealed class DuplicateKeeperPreferencesForm : Form
    {
        private readonly ComboBox _profile = new();
        private readonly ComboBox _codecPreference = new();
        private readonly NumericUpDown _resolution = CreateWeightControl();
        private readonly NumericUpDown _quality = CreateWeightControl();
        private readonly NumericUpDown _storage = CreateWeightControl();
        private readonly NumericUpDown _codec = CreateWeightControl();
        private readonly NumericUpDown _modified = CreateWeightControl();
        private readonly CheckBox _preserveResolution = new();
        private readonly NumericUpDown _minimumMargin = new();
        private readonly Label _profileDescription = new();
        private readonly Label _preview = new();

        public DuplicateKeeperPreferences Preferences { get; private set; }

        public DuplicateKeeperPreferencesForm(DuplicateKeeperPreferences preferences)
        {
            Preferences = preferences.Clone();
            Preferences.Normalize();

            Text = "Duplicate Keeper Preferences";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(650, 625);
            AutoScaleMode = AutoScaleMode.Font;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 11
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _profile.DropDownStyle = ComboBoxStyle.DropDownList;
            _profile.Items.AddRange(new object[]
            {
                DuplicateKeeperPreferences.QualityFirst,
                DuplicateKeeperPreferences.Balanced,
                DuplicateKeeperPreferences.SaveStorage,
                DuplicateKeeperPreferences.PreferModernCodecs,
                DuplicateKeeperPreferences.Custom
            });
            _profile.SelectedItem = Preferences.Profile;
            AddRow(layout, 0, "Keeper preference:", _profile, 31);

            _profileDescription.AutoSize = false;
            _profileDescription.Dock = DockStyle.Fill;
            _profileDescription.ForeColor = SystemColors.GrayText;
            layout.Controls.Add(_profileDescription, 0, 1);
            layout.SetColumnSpan(_profileDescription, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

            AddRow(layout, 2, "Resolution weight:", _resolution, 31);
            AddRow(layout, 3, "Reported bitrate quality weight:", _quality, 31);
            AddRow(layout, 4, "Storage savings weight:", _storage, 31);
            AddRow(layout, 5, "Codec preference weight:", _codec, 31);
            AddRow(layout, 6, "Modified date weight:", _modified, 31);

            _codecPreference.DropDownStyle = ComboBoxStyle.DropDownList;
            _codecPreference.Items.AddRange(new object[]
            {
                DuplicateKeeperPreferences.CodecNoPreference,
                DuplicateKeeperPreferences.CodecModernFirst,
                DuplicateKeeperPreferences.CodecH264First
            });
            _codecPreference.SelectedItem = Preferences.CodecPreference;
            AddRow(layout, 7, "Codec order:", _codecPreference, 31);

            _preserveResolution.Text = "Never prefer a lower resolution solely to save space";
            _preserveResolution.AutoSize = true;
            _preserveResolution.Checked = Preferences.NeverSacrificeResolution;
            layout.Controls.Add(_preserveResolution, 0, 8);
            layout.SetColumnSpan(_preserveResolution, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            _minimumMargin.Minimum = 0;
            _minimumMargin.Maximum = 25;
            _minimumMargin.Width = 90;
            _minimumMargin.Value = Preferences.MinimumScoreMargin;
            AddRow(layout, 9, "Minimum winning margin:", _minimumMargin, 31);

            var marginHint = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Text = "When the top scores are closer than this margin, no trash candidate is recommended until the user selects a keeper."
            };
            layout.Controls.Add(marginHint, 0, 10);
            layout.SetColumnSpan(marginHint, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));

            var previewBox = new GroupBox
            {
                Text = "Live example",
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };
            _preview.Dock = DockStyle.Fill;
            _preview.AutoSize = false;
            previewBox.Controls.Add(_preview);
            layout.Controls.Add(previewBox, 0, 11);
            layout.SetColumnSpan(previewBox, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88 };
            var ok = new Button { Text = "OK", Width = 88 };
            ok.Click += SaveAndClose;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons, 0, 12);
            layout.SetColumnSpan(buttons, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowCount = 13;

            AcceptButton = ok;
            CancelButton = cancel;

            _resolution.Value = Preferences.ResolutionWeight;
            _quality.Value = Preferences.QualityWeight;
            _storage.Value = Preferences.StorageWeight;
            _codec.Value = Preferences.CodecWeight;
            _modified.Value = Preferences.ModifiedDateWeight;

            _profile.SelectedIndexChanged += (_, __) => RefreshState();
            _codecPreference.SelectedIndexChanged += (_, __) => RefreshPreview();
            _preserveResolution.CheckedChanged += (_, __) => RefreshPreview();
            _minimumMargin.ValueChanged += (_, __) => RefreshPreview();
            foreach (var control in new[] { _resolution, _quality, _storage, _codec, _modified })
                control.ValueChanged += (_, __) => RefreshPreview();

            RefreshState();
        }

        private static NumericUpDown CreateWeightControl()
        {
            return new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Width = 90
            };
        }

        private static void AddRow(TableLayoutPanel layout, int row, string labelText, Control control, int height)
        {
            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(control, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        }

        private void RefreshState()
        {
            bool custom = string.Equals(_profile.SelectedItem?.ToString(), DuplicateKeeperPreferences.Custom, StringComparison.Ordinal);
            foreach (var control in new Control[] { _resolution, _quality, _storage, _codec, _modified })
                control.Enabled = custom;

            bool weighted = !string.Equals(_profile.SelectedItem?.ToString(), DuplicateKeeperPreferences.QualityFirst, StringComparison.Ordinal);
            _codecPreference.Enabled = weighted;
            _preserveResolution.Enabled = weighted;
            _minimumMargin.Enabled = weighted;
            _profileDescription.Text = _profile.SelectedItem?.ToString() switch
            {
                DuplicateKeeperPreferences.QualityFirst => "Compatibility preset: protected reference, resolution, reported bitrate, larger size, then modified date. It reproduces the existing keeper order.",
                DuplicateKeeperPreferences.Balanced => "Weights: resolution 40, quality 15, storage 30, codec 15. Designed to accept meaningful space savings without casually sacrificing resolution.",
                DuplicateKeeperPreferences.SaveStorage => "Weights: resolution 30, quality 10, storage 45, codec 15. Strongly rewards smaller efficient encodes.",
                DuplicateKeeperPreferences.PreferModernCodecs => "Weights: resolution 35, quality 15, storage 20, codec 30. Favors AV1/HEVC/VP9 while retaining quality guardrails.",
                _ => "Custom weights are normalized automatically, so they do not need to total 100. Modified date should normally remain a weak tie-breaker."
            };
            RefreshPreview();
        }

        private DuplicateKeeperPreferences BuildPreferences()
        {
            var result = new DuplicateKeeperPreferences
            {
                Profile = _profile.SelectedItem?.ToString() ?? DuplicateKeeperPreferences.QualityFirst,
                ResolutionWeight = (int)_resolution.Value,
                QualityWeight = (int)_quality.Value,
                StorageWeight = (int)_storage.Value,
                CodecWeight = (int)_codec.Value,
                ModifiedDateWeight = (int)_modified.Value,
                CodecPreference = _codecPreference.SelectedItem?.ToString() ?? DuplicateKeeperPreferences.CodecModernFirst,
                NeverSacrificeResolution = _preserveResolution.Checked,
                MinimumScoreMargin = (int)_minimumMargin.Value
            };
            result.Normalize();
            return result;
        }

        private void RefreshPreview()
        {
            if (_profile.SelectedItem == null || _codecPreference.SelectedItem == null)
                return;

            var preferences = BuildPreferences();
            long gib = 1024L * 1024L * 1024L;
            var items = new List<DuplicateItem>
            {
                new("Example HEVC.mp4", (long)(1.14 * gib), "hevc", 1920, 1080, 1800, 5000, DateTime.Today, DateTime.Today, false, "", ""),
                new("Example H264.mp4", (long)(2.51 * gib), "h264", 1920, 1080, 1800, 11000, DateTime.Today, DateTime.Today, false, "", "")
            };
            var evaluation = DuplicateKeeperScoringService.Evaluate(items, preferences);
            string result = evaluation.RequiresReview || evaluation.Keeper == null
                ? "Result: Review required"
                : $"Result: keep {Path.GetFileName(evaluation.Keeper.Path)}";
            _preview.Text = "Same 1080p duration: 1.14 GB HEVC at 5,000 kbps vs 2.51 GB H.264 at 11,000 kbps." +
                            Environment.NewLine + result + Environment.NewLine + evaluation.Explanation;
        }

        private void SaveAndClose(object? sender, EventArgs e)
        {
            if (string.Equals(_profile.SelectedItem?.ToString(), DuplicateKeeperPreferences.Custom, StringComparison.Ordinal) &&
                _resolution.Value + _quality.Value + _storage.Value + _codec.Value + _modified.Value == 0)
            {
                MessageBox.Show(this, "Set at least one custom weight above zero.", "Keeper weight required",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Preferences = BuildPreferences();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
