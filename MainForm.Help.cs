namespace MediaFlux;

public partial class MainForm
{
    private void InitializeHelpMenu()
    {
        var guide = new ToolStripMenuItem("User Guide") { Name = "userGuideToolStripMenuItem", ShortcutKeys = Keys.F1 };
        guide.Click += (_, _) => ShowUserGuide();
        helpToolStripMenuItem.DropDownItems.Insert(0, guide);
        helpToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());
        KeyPreview = true;
        KeyDown += MainForm_HelpKeyDown;
    }

    private void MainForm_HelpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F1) return;
        e.Handled = true; e.SuppressKeyPress = true; ShowUserGuide();
    }

    private void ShowUserGuide(string? topicId = null) => HelpGuideForm.ShowGuide(this, topicId);
}
