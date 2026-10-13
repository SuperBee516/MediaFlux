using MediaFlux.Services;

namespace MediaFlux;

public partial class MainForm
{
    private void InitializeAiBenchmarkManagerMenu()
    {
        var item = new ToolStripMenuItem("AI Benchmark Manager…");
        item.Click += (_, _) =>
        {
            try
            {
                using var manager = new AiBenchmarkManagerForm(config: _config, configPath: _configPath);
                manager.ShowDialog(this);
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(AppPaths.UserDataDirectory, "Open AI Benchmark Manager failed", exception: ex);
                MessageBox.Show(this, "The AI Benchmark Manager could not be opened. The error was logged.", "AI Benchmark Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        toolsToolStripMenuItem.DropDownItems.Insert(7, item);
    }
}
