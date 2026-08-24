namespace MediaFlux
{
    internal sealed class EncodingScopeForm : MediaFluxForm
    {
        private EncodingScopeForm(int selectedCount, int eligibleCount)
        {
            Text = "Choose Encoding Scope";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(440, 155);
            Padding = new Padding(16);

            Controls.Add(new Label
            {
                Text = $"{selectedCount:N0} of {eligibleCount:N0} queued files are selected.",
                AutoSize = true,
                Location = new Point(18, 18)
            });

            var selected = new Button
            {
                Text = $"Encode Selected ({selectedCount:N0})",
                DialogResult = DialogResult.Yes,
                Location = new Point(18, 58),
                Size = new Size(190, 34)
            };
            var entireQueue = new Button
            {
                Text = $"Encode Entire Queue ({eligibleCount:N0})",
                DialogResult = DialogResult.No,
                Location = new Point(222, 58),
                Size = new Size(200, 34)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(322, 110),
                Size = new Size(100, 30)
            };

            Controls.Add(selected);
            Controls.Add(entireQueue);
            Controls.Add(cancel);
            AcceptButton = selected;
            CancelButton = cancel;
        }

        public static DialogResult ShowChoice(IWin32Window owner, int selectedCount, int eligibleCount)
        {
            using var dialog = new EncodingScopeForm(selectedCount, eligibleCount);
            return dialog.ShowDialog(owner);
        }
    }
}
