using MediaFlux.Services;

namespace MediaFlux;

internal sealed class RealEsrganInstallationProgressForm : MediaFluxForm
{
    private readonly Func<IProgress<AiProvisioningProgress>, CancellationToken, Task<AiProvisioningResult>> _operation;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Label _stage = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Label _detail = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 22, Style = ProgressBarStyle.Marquee };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    private bool _finished;

    public RealEsrganInstallationProgressForm(Func<IProgress<AiProvisioningProgress>, CancellationToken, Task<AiProvisioningResult>> operation)
    {
        _operation = operation; Text = "Install Real-ESRGAN"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(500, 165); MinimizeBox = false; MaximizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _stage.Text = "Preparing official Real-ESRGAN package…"; _cancel.Click += (_, _) => Cancel();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(_cancel);
        layout.Controls.Add(_stage, 0, 0); layout.Controls.Add(_progress, 0, 1); layout.Controls.Add(_detail, 0, 2); layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        Shown += RunAsync; FormClosing += (_, e) => { if (!_finished) { Cancel(); e.Cancel = true; } };
    }

    public AiProvisioningResult? Result { get; private set; }
    public bool WasCanceled { get; private set; }
    private async void RunAsync(object? sender, EventArgs e)
    {
        try { Result = await _operation(new Progress<AiProvisioningProgress>(Update), _cancellation.Token); DialogResult = Result.Succeeded ? DialogResult.OK : DialogResult.Abort; }
        catch (OperationCanceledException) { WasCanceled = true; DialogResult = DialogResult.Cancel; }
        finally { _finished = true; Close(); }
    }
    private void Update(AiProvisioningProgress progress)
    {
        _stage.Text = progress.Stage switch { AiProvisioningStage.Downloading => "Downloading official Real-ESRGAN package…", AiProvisioningStage.Verifying => "Verifying package…", AiProvisioningStage.Extracting => "Extracting package…", AiProvisioningStage.Validating => "Validating installation…", AiProvisioningStage.Installing => "Installing Real-ESRGAN…", _ => "Installation completed" };
        if (progress.Percentage.HasValue) { _progress.Style = ProgressBarStyle.Continuous; _progress.Value = Math.Clamp((int)Math.Round(progress.Percentage.Value), 0, 100); } else _progress.Style = ProgressBarStyle.Marquee;
        _detail.Text = progress.TotalBytes is > 0 ? $"{progress.BytesTransferred / 1024d / 1024d:0.0} MB of {progress.TotalBytes.Value / 1024d / 1024d:0.0} MB" : "";
    }
    private void Cancel() { if (_finished || _cancellation.IsCancellationRequested) return; _cancellation.Cancel(); _cancel.Enabled = false; _stage.Text = "Canceling and cleaning temporary files…"; _progress.Style = ProgressBarStyle.Marquee; }
    protected override void Dispose(bool disposing) { if (disposing) _cancellation.Dispose(); base.Dispose(disposing); }
}
