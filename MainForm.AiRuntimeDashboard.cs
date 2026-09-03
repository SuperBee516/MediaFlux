namespace MediaFlux;

public partial class MainForm
{
    private void InitializeAiRuntimeDashboardMenu()
    {
        var item = new ToolStripMenuItem("AI Runtime Dashboard…");
        item.Click += (_, _) =>
        {
            using var dashboard = new AiRuntimeDashboardForm();
            dashboard.ShowDialog(this);
        };
        toolsToolStripMenuItem.DropDownItems.Insert(6, item);
    }
}
