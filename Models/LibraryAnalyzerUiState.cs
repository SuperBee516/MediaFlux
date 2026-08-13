namespace MediaFlux.Models;

public sealed class LibraryAnalyzerUiState
{
    public Dictionary<string, LibraryAnalyzerGridLayout> GridLayouts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> SplitterDistances { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        GridLayouts = new Dictionary<string, LibraryAnalyzerGridLayout>(
            GridLayouts ?? new Dictionary<string, LibraryAnalyzerGridLayout>(),
            StringComparer.OrdinalIgnoreCase);
        SplitterDistances = new Dictionary<string, int>(
            SplitterDistances ?? new Dictionary<string, int>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (LibraryAnalyzerGridLayout layout in GridLayouts.Values)
            layout.Normalize();
    }
}

public sealed class LibraryAnalyzerGridLayout
{
    public Dictionary<string, LibraryAnalyzerColumnLayout> Columns { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Normalize() => Columns = new Dictionary<string, LibraryAnalyzerColumnLayout>(
        Columns ?? new Dictionary<string, LibraryAnalyzerColumnLayout>(),
        StringComparer.OrdinalIgnoreCase);
}

public sealed class LibraryAnalyzerColumnLayout
{
    public int Width { get; set; }
    public int DisplayIndex { get; set; }
    public bool Visible { get; set; } = true;
}
