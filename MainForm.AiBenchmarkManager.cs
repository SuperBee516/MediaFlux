namespace MediaFlux;

public partial class MainForm
{
    private void InitializeAiBenchmarkManagerMenu()
    {
        var item = new ToolStripMenuItem("AI Benchmark Manager…");
        item.Click += (_, _) => { using var manager = new AiBenchmarkManagerForm(); manager.ShowDialog(this); };
        toolsToolStripMenuItem.DropDownItems.Insert(7, item);
    }
}
