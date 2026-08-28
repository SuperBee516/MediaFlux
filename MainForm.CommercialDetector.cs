using MediaFlux.Models;

namespace MediaFlux;

public partial class MainForm
{
    private void InitializeCommercialDetectorMenu()
    {
        var item = new ToolStripMenuItem("Commercial Detector && Splitter…")
        {
            Name = "commercialDetectorToolStripMenuItem"
        };
        item.Click += (_, _) =>
        {
            using var form = new CommercialDetectorForm(_config, _configPath);
            form.ShowDialog(this);
        };
        toolsToolStripMenuItem.DropDownItems.Add(item);
    }
}
