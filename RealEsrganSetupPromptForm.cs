namespace MediaFlux;

internal enum RealEsrganSetupPromptAction { Install, Locate, Cancel }

internal sealed class RealEsrganSetupPromptForm : MediaFluxForm
{
    public RealEsrganSetupPromptAction Action { get; private set; } = RealEsrganSetupPromptAction.Cancel;
    public RealEsrganSetupPromptForm(string message)
    {
        Text = "AI Restoration Setup"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(500, 190); MinimizeBox = false; MaximizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(450, 0) }, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        Button cancel = new() { Text = "Cancel", AutoSize = true }; Button locate = new() { Text = "Locate Existing Installation", AutoSize = true }; Button install = new() { Text = "Install Real-ESRGAN", AutoSize = true };
        cancel.Click += (_, _) => { Action = RealEsrganSetupPromptAction.Cancel; DialogResult = DialogResult.Cancel; }; locate.Click += (_, _) => { Action = RealEsrganSetupPromptAction.Locate; DialogResult = DialogResult.OK; }; install.Click += (_, _) => { Action = RealEsrganSetupPromptAction.Install; DialogResult = DialogResult.OK; };
        buttons.Controls.Add(cancel); buttons.Controls.Add(locate); buttons.Controls.Add(install); layout.Controls.Add(buttons, 0, 1); Controls.Add(layout); CancelButton = cancel;
    }
}
