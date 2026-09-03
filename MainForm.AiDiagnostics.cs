using MediaFlux.Services;

namespace MediaFlux;

public partial class MainForm
{
    private void InitializeAiDiagnosticsMenu()
    {
        var item = new ToolStripMenuItem("Create AI Diagnostics Package…");
        item.Click += async (_, _) => await CreateAiDiagnosticsPackageAsync(item);
        toolsToolStripMenuItem.DropDownItems.Insert(8, item);
    }

    private async Task CreateAiDiagnosticsPackageAsync(ToolStripMenuItem item)
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a destination folder for the AI diagnostics package." };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        item.Enabled = false;
        try
        {
            var progress = new Progress<string>(message => ShowStatusInfo(message));
            AiDiagnosticsPackageResult result = await new AiDiagnosticsPackageService().CreateAsync(dialog.SelectedPath, progress);
            ShowStatusInfo($"AI diagnostics package created: {result.PackagePath}");
        }
        catch (Exception ex) { ShowStatusInfo("AI diagnostics package failed: " + ex.Message); }
        finally { if (!IsDisposed) item.Enabled = true; }
    }
}
