namespace MediaFlux
{
    internal sealed class RecommendationStartForm : MediaFluxForm
    {
        private RecommendationStartForm(
            int skip,
            int review,
            int remuxOnly,
            int unavailable)
        {
            Text = "Review Smart Encode Recommendations";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(570, 285);
            Padding = new Padding(16);

            var heading = new Label
            {
                Text = "Some requested files may not benefit from the current settings.",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 17)
            };
            var counts = new Label
            {
                Text =
                    $"Skip: {skip:N0}     Review: {review:N0}     " +
                    $"Remux only: {remuxOnly:N0}     Unavailable: {unavailable:N0}",
                AutoSize = true,
                Location = new Point(18, 52)
            };
            var warning = new Label
            {
                Text =
                    "Safety recommendation: encode only Strong and Moderate candidates. " +
                    "You can explicitly override Skip and Review classifications for this run; " +
                    "the source files and saved safety settings are not changed.",
                BackColor = Color.FromArgb(254, 243, 199),
                ForeColor = Color.FromArgb(146, 64, 14),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(8),
                Location = new Point(18, 82),
                Size = new Size(534, 67)
            };

            var candidates = new Button
            {
                Text = "Encode candidates only (Recommended)",
                DialogResult = DialogResult.Yes,
                Location = new Point(18, 177),
                Size = new Size(260, 34)
            };
            var overrideButton = new Button
            {
                Text = "Override Skip and encode requested files",
                DialogResult = DialogResult.No,
                Location = new Point(292, 177),
                Size = new Size(260, 34)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(452, 235),
                Size = new Size(100, 30)
            };

            Controls.Add(heading);
            Controls.Add(counts);
            Controls.Add(warning);
            Controls.Add(candidates);
            Controls.Add(overrideButton);
            Controls.Add(cancel);
            AcceptButton = candidates;
            CancelButton = cancel;
        }

        public static DialogResult ShowRecommendationChoice(
            IWin32Window owner,
            int skip,
            int review,
            int remuxOnly,
            int unavailable)
        {
            using var dialog =
                new RecommendationStartForm(skip, review, remuxOnly, unavailable);
            return dialog.ShowDialog(owner);
        }
    }
}
