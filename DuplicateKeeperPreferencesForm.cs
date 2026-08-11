using MediaFlux.Models;
using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MediaFlux
{
    internal sealed class DuplicateKeeperPreferencesForm : MediaFluxForm
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
        private readonly NumericUpDown _visualQualityFloor = new();
        private readonly NumericUpDown _visualConfidenceFloor = new();
        private readonly Label _profileDescription = new();
        private readonly Label _preview = new();
        private readonly TextBox _exactPreferredLocations = new();
        private readonly DuplicateKeeperScoringContext _scoringContext;

        public DuplicateKeeperPreferences Preferences { get; private set; }

        public DuplicateKeeperPreferencesForm(
            DuplicateKeeperPreferences preferences,
            DuplicateKeeperScoringContext scoringContext = DuplicateKeeperScoringContext.Standard)
        {
            _scoringContext = scoringContext;
            Preferences = preferences.Clone();
            Preferences.Normalize();

            Text = scoringContext == DuplicateKeeperScoringContext.Visual
                ? "Visual Duplicate Keeper Rules"
                : "Duplicate Keeper Preferences";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(760, scoringContext == DuplicateKeeperScoringContext.Visual ? 835 : 730);
            AutoScaleMode = AutoScaleMode.Font;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 17
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _profile.DropDownStyle = ComboBoxStyle.DropDownList;
            object[] profiles = scoringContext == DuplicateKeeperScoringContext.Visual
                ? new object[] { DuplicateKeeperPreferences.PreserveMaximumQuality, DuplicateKeeperPreferences.VisualBalanced,
                    DuplicateKeeperPreferences.StorageOptimized, DuplicateKeeperPreferences.Custom }
                : new object[] { DuplicateKeeperPreferences.QualityFirst, DuplicateKeeperPreferences.Balanced,
                    DuplicateKeeperPreferences.SaveStorage, DuplicateKeeperPreferences.PreferModernCodecs, DuplicateKeeperPreferences.Custom };
            _profile.Items.AddRange(profiles);
            _profile.SelectedItem = scoringContext == DuplicateKeeperScoringContext.Visual
                ? Preferences.VisualKeeperStrategy
                : Preferences.Profile;
            AddRow(layout, 0, scoringContext == DuplicateKeeperScoringContext.Visual
                ? "Automatic Keeper Strategy:"
                : "Keeper preference:", _profile, 31);

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

            _preserveResolution.Text = scoringContext == DuplicateKeeperScoringContext.Visual
                ? "Different resolutions use a quality, confidence, and storage-value tradeoff"
                : "Never prefer a lower resolution solely to save space";
            _preserveResolution.AutoSize = true;
            _preserveResolution.Checked = Preferences.NeverSacrificeResolution;
            layout.Controls.Add(_preserveResolution, 0, 8);
            layout.SetColumnSpan(_preserveResolution, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            bool visualRules = scoringContext == DuplicateKeeperScoringContext.Visual;
            _visualQualityFloor.Minimum = 25;
            _visualQualityFloor.Maximum = 75;
            _visualQualityFloor.Value = Preferences.VisualQualityFloor;
            _visualQualityFloor.Visible = visualRules;
            AddRow(layout, 9, "Estimated quality floor:", _visualQualityFloor, visualRules ? 31 : 0);

            _visualConfidenceFloor.Minimum = 76;
            _visualConfidenceFloor.Maximum = 100;
            _visualConfidenceFloor.DecimalPlaces = 1;
            _visualConfidenceFloor.Value = (decimal)Preferences.VisualConfidenceFloor;
            _visualConfidenceFloor.Visible = visualRules;
            AddRow(layout, 10, "Visual confidence floor:", _visualConfidenceFloor, visualRules ? 31 : 0);

            _minimumMargin.Minimum = 0;
            _minimumMargin.Maximum = 25;
            _minimumMargin.Width = 90;
            _minimumMargin.Value = Preferences.MinimumScoreMargin;
            AddRow(layout, 11, "Minimum winning margin:", _minimumMargin, 31);

            var marginHint = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Text = "When the top scores are closer than this margin, no trash candidate is recommended until the user selects a keeper."
            };
            layout.Controls.Add(marginHint, 0, 12);
            layout.SetColumnSpan(marginHint, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));

            _exactPreferredLocations.Multiline = true;
            _exactPreferredLocations.ScrollBars = ScrollBars.Vertical;
            _exactPreferredLocations.Dock = DockStyle.Fill;
            _exactPreferredLocations.Text = string.Join(Environment.NewLine, Preferences.ExactPreferredLocations);
            AddRow(layout, 13, "Exact preferred roots (highest first):", _exactPreferredLocations, 62);

            var exactHint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Text = "Exact duplicates are byte-identical. These roots are used before filename, folder depth, and file dates; video quality settings are ignored."
            };
            layout.Controls.Add(exactHint, 0, 14);
            layout.SetColumnSpan(exactHint, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            var previewBox = new GroupBox
            {
                Text = "Live example",
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };
            _preview.Dock = DockStyle.Fill;
            _preview.AutoSize = false;
            previewBox.Controls.Add(_preview);
            layout.Controls.Add(previewBox, 0, 15);
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
            layout.Controls.Add(buttons, 0, 16);
            layout.SetColumnSpan(buttons, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowCount = 17;

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
            _visualQualityFloor.ValueChanged += (_, __) => RefreshPreview();
            _visualConfidenceFloor.ValueChanged += (_, __) => RefreshPreview();
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

            bool visual = _scoringContext == DuplicateKeeperScoringContext.Visual;
            bool weighted = visual || !string.Equals(_profile.SelectedItem?.ToString(), DuplicateKeeperPreferences.QualityFirst, StringComparison.Ordinal);
            _codecPreference.Enabled = weighted;
            _preserveResolution.Enabled = !visual && weighted;
            _minimumMargin.Enabled = weighted;
            _visualQualityFloor.Enabled = visual && custom;
            _visualConfidenceFloor.Enabled = visual && custom;
            if (visual && !custom)
            {
                (_visualQualityFloor.Value, _visualConfidenceFloor.Value) = _profile.SelectedItem?.ToString() switch
                {
                    DuplicateKeeperPreferences.PreserveMaximumQuality => (45, 92),
                    DuplicateKeeperPreferences.StorageOptimized => (45, 95),
                    _ => (45, 90)
                };
            }
            _profileDescription.Text = _profile.SelectedItem?.ToString() switch
            {
                DuplicateKeeperPreferences.PreserveMaximumQuality => "Prioritizes supported resolution and quality gains, with diminishing returns and a relatively high tolerance for added storage.",
                DuplicateKeeperPreferences.VisualBalanced when visual => "Preserves meaningful resolution gains when pixel growth is good value for the added storage, while retaining quality, confidence, codec, and score-margin safeguards.",
                DuplicateKeeperPreferences.StorageOptimized => "Favors meaningful space savings after quality and confidence floors, and requires stronger value evidence before paying for higher resolution.",
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
                Profile = _scoringContext == DuplicateKeeperScoringContext.Visual
                    ? Preferences.Profile
                    : _profile.SelectedItem?.ToString() ?? DuplicateKeeperPreferences.QualityFirst,
                ResolutionWeight = (int)_resolution.Value,
                QualityWeight = (int)_quality.Value,
                StorageWeight = (int)_storage.Value,
                CodecWeight = (int)_codec.Value,
                ModifiedDateWeight = (int)_modified.Value,
                CodecPreference = _codecPreference.SelectedItem?.ToString() ?? DuplicateKeeperPreferences.CodecModernFirst,
                NeverSacrificeResolution = _preserveResolution.Checked,
                MinimumScoreMargin = (int)_minimumMargin.Value,
                // Retained only for backward-compatible settings serialization; the
                // quality-aware visual model no longer uses the relative-bitrate rule.
                PreferSmallerComparableVisualCopy = Preferences.PreferSmallerComparableVisualCopy,
                ComparableVisualBitratePercent = Preferences.ComparableVisualBitratePercent,
                VisualKeeperStrategy = _scoringContext == DuplicateKeeperScoringContext.Visual
                    ? _profile.SelectedItem?.ToString() ?? DuplicateKeeperPreferences.VisualBalanced
                    : Preferences.VisualKeeperStrategy,
                VisualQualityFloor = (int)_visualQualityFloor.Value,
                VisualConfidenceFloor = (double)_visualConfidenceFloor.Value,
                ExactPreferredLocations = _exactPreferredLocations.Lines.ToList()
            };
            result.Normalize();
            return result;
        }

        private void RefreshPreview()
        {
            if (_profile.SelectedItem == null || _codecPreference.SelectedItem == null)
                return;

            var preferences = BuildPreferences();
            long mib = 1024L * 1024L;
            var items = new List<DuplicateItem>
            {
                new DuplicateItem("Larger HEVC.mp4", (long)(852 * mib), "hevc", 1920, 1080, 2077, 3280, DateTime.Today, DateTime.Today, false, "", "") { FrameRate = 30 },
                new DuplicateItem("Smaller HEVC.mp4", (long)(462 * mib), "hevc", 1920, 1080, 2077, 1780, DateTime.Today, DateTime.Today, false, "", "") { FrameRate = 30 }
            };
            var evaluation = DuplicateKeeperScoringService.Evaluate(items, preferences, _scoringContext, visualConfidence: 98);
            string result = evaluation.RequiresReview || evaluation.Keeper == null
                ? "Result: Review required"
                : $"Result: keep {Path.GetFileName(evaluation.Keeper.Path)}";
            _preview.Text = "HEVC 1920x1080 at 30 fps, high visual confidence (98%): 852 MB at 3.28 Mbps vs 462 MB at 1.78 Mbps." +
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
