using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux.Services;

public sealed record LibraryCrossNavigationState(bool Files, bool ExactDuplicates, bool VisualDuplicates, bool VisualFamily)
{
    public static LibraryCrossNavigationState For(StorageReclamationPlanItem item) =>
        new(true, item.ExactGroupId.HasValue, item.VisualGroupId.HasValue, item.VisualFamilyId.HasValue);
}

public sealed record LibraryFileActionState(
    bool HasSelection,
    bool CanPlay,
    bool CanOpenFolders,
    bool CanCopyPaths,
    bool AllProtected)
{
    public static LibraryFileActionState Evaluate<T>(
        IReadOnlyCollection<T> items,
        Func<T, string> path,
        Func<T, bool> isProtected,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        string[] paths = items.Select(path).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        bool allPathsPresent = items.Count > 0 && paths.Length == items.Count;
        bool foldersResolve = allPathsPresent && paths.All(value =>
        {
            string? folder = Path.GetDirectoryName(value);
            return !string.IsNullOrWhiteSpace(folder) && directoryExists(folder);
        });
        return new LibraryFileActionState(
            items.Count > 0,
            items.Count == 1 && paths.Length == 1 && fileExists(paths[0]),
            foldersResolve,
            allPathsPresent,
            items.Count > 0 && items.All(isProtected));
    }
}

public sealed record LibraryFileQueueResult(
    IReadOnlyList<string> AvailablePaths,
    int UnavailableCount,
    bool Dispatched);

public static class LibraryFileQueueSelection
{
    public static LibraryFileQueueResult Dispatch(
        IEnumerable<string> paths,
        Action<IReadOnlyList<string>>? addToEncodeQueue,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        fileExists ??= File.Exists;
        string[] selected = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] available = selected.Where(fileExists).ToArray();
        bool dispatched = available.Length > 0 && addToEncodeQueue != null;
        if (dispatched) addToEncodeQueue!(available);
        return new LibraryFileQueueResult(available, selected.Length - available.Length, dispatched);
    }
}

public sealed record LibraryFileMenuPresentation(
    string ExplorerText,
    string CopyText,
    string EncodeText,
    bool CanEncode)
{
    public static LibraryFileMenuPresentation ForSelection(
        int selectionCount,
        int availableCount,
        bool queueAvailable) => new(
        selectionCount > 1 ? "Show Selected in Explorer" : "Show in Explorer",
        selectionCount > 1 ? "Copy Paths" : "Copy Path",
        selectionCount > 1 ? "Add Selected Files to Encode Queue" : "Add to Encode Queue",
        availableCount > 0 && queueAvailable);
}

public static class LibraryAnalyzerGridInteraction
{
    public static void SelectRows<T>(DataGridView grid, Func<T, bool> predicate, bool invert = false)
    {
        foreach (DataGridViewRow row in grid.Rows)
            row.Selected = row.Tag is T item && (invert ? !row.Selected && predicate(item) : predicate(item));
    }

    public static void ClearSelection(DataGridView grid) => grid.ClearSelection();

    public static ToolStripMenuItem AddMenuItem(
        ContextMenuStrip menu,
        string text,
        string name,
        Func<Task> action)
    {
        var item = new ToolStripMenuItem(text) { Name = name };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
        return item;
    }

    public static void AttachContextMenu(DataGridView grid, ContextMenuStrip menu)
    {
        grid.ContextMenuStrip = menu;
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                UpdateRightClickSelection(grid, e.RowIndex, e.ColumnIndex);
        };
    }

    public static void UpdateRightClickSelection(DataGridView grid, int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= grid.Rows.Count)
            return;
        bool alreadySelected = grid.Rows[rowIndex].Selected;
        int[] selectedRows = alreadySelected
            ? grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).ToArray()
            : Array.Empty<int>();
        if (!alreadySelected)
            grid.ClearSelection();
        if (columnIndex >= 0 && columnIndex < grid.Columns.Count && grid.Columns[columnIndex].Visible)
            grid.CurrentCell = grid.Rows[rowIndex].Cells[columnIndex];
        if (alreadySelected)
            foreach (int selectedRow in selectedRows) grid.Rows[selectedRow].Selected = true;
        else
            grid.Rows[rowIndex].Selected = true;
    }

    public static void SetMenuState(ContextMenuStrip menu, string name, bool enabled, string? text = null)
    {
        if (menu.Items.Find(name, true).FirstOrDefault() is not ToolStripItem item)
            return;
        item.Enabled = enabled;
        if (!string.IsNullOrWhiteSpace(text))
            item.Text = text;
    }

    public static T[] SelectedItems<T>(DataGridView grid) => grid.SelectedRows
        .Cast<DataGridViewRow>()
        .OrderBy(row => row.Index)
        .Select(row => row.Tag)
        .OfType<T>()
        .ToArray();
}

public sealed class LibraryAnalyzerLayoutController
{
    private readonly LibraryAnalyzerUiState _state;
    private readonly Dictionary<DataGridView, GridRegistration> _grids = new();
    private readonly Dictionary<SplitContainer, SplitRegistration> _splitters = new();

    public LibraryAnalyzerLayoutController(LibraryAnalyzerUiState state) => _state = state;

    public void RegisterGrid(DataGridView grid, string key)
    {
        if (_grids.ContainsKey(grid)) return;
        var defaults = grid.Columns.Cast<DataGridViewColumn>().ToDictionary(
            column => column.Name,
            column => new ColumnDefault(column.Width, column.DisplayIndex, column.Visible, column.AutoSizeMode),
            StringComparer.OrdinalIgnoreCase);
        _grids.Add(grid, new GridRegistration(key, defaults));
        ApplyGridLayout(grid, key);
        AddLayoutMenu(grid, key);
        grid.ColumnWidthChanged += (_, _) => CaptureGridLayout(grid, key);
        grid.ColumnDisplayIndexChanged += (_, _) => CaptureGridLayout(grid, key);
        grid.ColumnStateChanged += (_, e) =>
        {
            if (e.StateChanged == DataGridViewElementStates.Visible)
                CaptureGridLayout(grid, key);
        };
    }

    public void RegisterSplitter(SplitContainer splitter, string key)
    {
        if (_splitters.ContainsKey(splitter)) return;
        _splitters.Add(splitter, new SplitRegistration(key));
        splitter.SplitterMoved += (_, _) => _state.SplitterDistances[key] = splitter.SplitterDistance;
    }

    public void ApplySplitterLayouts()
    {
        foreach ((SplitContainer splitter, SplitRegistration registration) in _splitters)
        {
            if (!_state.SplitterDistances.TryGetValue(registration.Key, out int distance)) continue;
            int maximum = Math.Max(splitter.Panel1MinSize,
                (splitter.Orientation == Orientation.Horizontal ? splitter.ClientSize.Height : splitter.ClientSize.Width) -
                splitter.Panel2MinSize - splitter.SplitterWidth);
            splitter.SplitterDistance = Math.Clamp(distance, splitter.Panel1MinSize, maximum);
        }
    }

    public void CaptureAll()
    {
        foreach ((DataGridView grid, GridRegistration registration) in _grids)
            CaptureGridLayout(grid, registration.Key);
        foreach ((SplitContainer splitter, SplitRegistration registration) in _splitters)
            _state.SplitterDistances[registration.Key] = splitter.SplitterDistance;
    }

    private void AddLayoutMenu(DataGridView grid, string key)
    {
        ContextMenuStrip menu = grid.ContextMenuStrip ?? new ContextMenuStrip();
        if (grid.ContextMenuStrip == null)
            LibraryAnalyzerGridInteraction.AttachContextMenu(grid, menu);
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        var layout = new ToolStripMenuItem("Column layout") { Name = "ColumnLayout" };
        layout.DropDownItems.Add("Auto-size selected column", null, (_, _) => AutoSizeSelectedColumn(grid, key));
        layout.DropDownItems.Add("Auto-size all columns", null, (_, _) => AutoSizeAllColumns(grid, key));
        var columns = new ToolStripMenuItem("Columns");
        foreach (DataGridViewColumn column in grid.Columns.Cast<DataGridViewColumn>())
        {
            var item = new ToolStripMenuItem(column.HeaderText)
            {
                Checked = column.Visible,
                CheckOnClick = true,
                Tag = column
            };
            item.Click += (_, _) =>
            {
                if (grid.Columns.Cast<DataGridViewColumn>().Count(value => value.Visible) == 1 && column.Visible)
                {
                    item.Checked = true;
                    return;
                }
                column.Visible = item.Checked;
                CaptureGridLayout(grid, key);
            };
            columns.DropDownItems.Add(item);
        }
        columns.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in columns.DropDownItems.OfType<ToolStripMenuItem>())
                item.Checked = ((DataGridViewColumn)item.Tag!).Visible;
        };
        layout.DropDownItems.Add(columns);
        layout.DropDownItems.Add(new ToolStripSeparator());
        layout.DropDownItems.Add("Reset column layout", null, (_, _) => ResetGridLayout(grid, key));
        menu.Items.Add(layout);
    }

    private void AutoSizeSelectedColumn(DataGridView grid, string key)
    {
        if (grid.CurrentCell?.OwningColumn is { Visible: true } column)
            AutoSizeColumn(grid, column, key);
    }

    private void AutoSizeAllColumns(DataGridView grid, string key)
    {
        foreach (DataGridViewColumn column in grid.Columns.Cast<DataGridViewColumn>().Where(value => value.Visible))
            AutoSizeColumn(grid, column, key, capture: false);
        CaptureGridLayout(grid, key);
    }

    private void AutoSizeColumn(DataGridView grid, DataGridViewColumn column, string key, bool capture = true)
    {
        int width = column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true);
        column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        column.Width = Math.Clamp(width, column.MinimumWidth, 1200);
        if (capture) CaptureGridLayout(grid, key);
    }

    private void ResetGridLayout(DataGridView grid, string key)
    {
        if (!_grids.TryGetValue(grid, out GridRegistration? registration)) return;
        _state.GridLayouts.Remove(key);
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (!registration.Defaults.TryGetValue(column.Name, out ColumnDefault? value)) continue;
            column.AutoSizeMode = value.AutoSizeMode;
            column.Visible = value.Visible;
            column.Width = value.Width;
        }
        foreach ((string name, ColumnDefault value) in registration.Defaults.OrderBy(pair => pair.Value.DisplayIndex))
            if (grid.Columns.Contains(name)) grid.Columns[name].DisplayIndex = value.DisplayIndex;
    }

    private void ApplyGridLayout(DataGridView grid, string key)
    {
        if (!_state.GridLayouts.TryGetValue(key, out LibraryAnalyzerGridLayout? layout)) return;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (!layout.Columns.TryGetValue(column.Name, out LibraryAnalyzerColumnLayout? value)) continue;
            column.Visible = value.Visible;
            if (value.Width >= column.MinimumWidth)
            {
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                column.Width = Math.Min(value.Width, 1200);
            }
        }
        var ordered = grid.Columns.Cast<DataGridViewColumn>()
            .OrderBy(column => layout.Columns.TryGetValue(column.Name, out LibraryAnalyzerColumnLayout? value) ? value.DisplayIndex : column.DisplayIndex)
            .ToArray();
        for (int index = 0; index < ordered.Length; index++) ordered[index].DisplayIndex = index;
    }

    private void CaptureGridLayout(DataGridView grid, string key)
    {
        var layout = new LibraryAnalyzerGridLayout();
        foreach (DataGridViewColumn column in grid.Columns)
            layout.Columns[column.Name] = new LibraryAnalyzerColumnLayout
            {
                Width = column.Width,
                DisplayIndex = column.DisplayIndex,
                Visible = column.Visible
            };
        _state.GridLayouts[key] = layout;
    }

    private sealed record ColumnDefault(int Width, int DisplayIndex, bool Visible, DataGridViewAutoSizeColumnMode AutoSizeMode);
    private sealed record GridRegistration(string Key, IReadOnlyDictionary<string, ColumnDefault> Defaults);
    private sealed record SplitRegistration(string Key);
}
