namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private void InitializeHelpNavigation()
    {
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F1) return;
            e.Handled = true; e.SuppressKeyPress = true;
            HelpGuideForm.ShowGuide(this, HelpTopicForCurrentTab());
        };
    }

    private string HelpTopicForCurrentTab() => _tabs.SelectedTab?.Text switch
    {
        "Statistics" => "statistics",
        "Storage Optimization" => "storage-optimization",
        "Duplicates — Exact" => "duplicates-exact",
        "Duplicates — Visual" => "duplicates-visual",
        "Duplicates — Families" => "duplicate-families",
        "Scheduled Maintenance" => "scheduled-maintenance",
        "Library Policies" => "storage-optimization",
        _ => "library-analyzer"
    };
}
