using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly ContextMenuStrip _locationsMenu = new();
    private readonly ContextMenuStrip _filesMenu = new();
    private readonly ContextMenuStrip _largestFilesMenu = new();

    private void ConfigurePrimaryContextMenus()
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(_locationsMenu, "Scan selected location(s)", "Scan", () =>
        {
            ScanSelected_Click(null, EventArgs.Empty);
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(_locationsMenu, "Enable", "Toggle", SetSelectedLocationsEnabledAsync);
        LibraryAnalyzerGridInteraction.AddMenuItem(_locationsMenu, "Open location", "Open", () =>
        {
            OpenSelectedLocations();
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(_locationsMenu, "Remove location", "Remove", () =>
        {
            RemoveLocation_Click(null, EventArgs.Empty);
            return Task.CompletedTask;
        });
        _locationsMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_locationsMenu, "Refresh", "Refresh", RefreshAllAsync);
        _locationsMenu.Opening += (_, _) => UpdateLocationsMenuState();
        LibraryAnalyzerGridInteraction.AttachContextMenu(_locationsGrid, _locationsMenu);

        LibraryAnalyzerGridInteraction.AddMenuItem(_filesMenu, "Play Video", "Play", () =>
        {
            if (SelectedFiles().FirstOrDefault() is { } file) PlayLibraryVideo(file.FullPath);
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(_filesMenu, "Open Containing Folder", "Folder", () =>
        {
            OpenContainingFolders(SelectedFiles().Select(file => file.FullPath));
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(_filesMenu, "Copy File Path", "CopyPath", () =>
        {
            CopyPaths(SelectedFiles().Select(file => file.FullPath));
            return Task.CompletedTask;
        });
        _filesMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_filesMenu, "Protect", "Protect", ToggleSelectedFileProtectionAsync);
        _filesMenu.Items.Add(new ToolStripSeparator());
        AddReanalysisMenuItems(_filesMenu, () => SelectedFiles().Select(file => file.FileId));
        _filesMenu.Opening += (_, _) => UpdateFilesMenuState();
        LibraryAnalyzerGridInteraction.AttachContextMenu(_filesGrid, _filesMenu);

        ConfigureCommonLibraryFileMenu(_largestFilesMenu, () => SelectedLargestFiles()
            .Select(file => (file.FileId, file.FullPath)).ToArray());
        LibraryAnalyzerGridInteraction.AddMenuItem(_largestFilesMenu, "Locate in Files", "Locate", LocateLargestFileInFilesAsync);
        _largestFilesMenu.Opening += (_, _) =>
        {
            UpdateCommonLibraryFileMenu(_largestFilesMenu, SelectedLargestFiles()
                .Select(file => (file.FileId, file.FullPath)).ToArray());
            LibraryAnalyzerGridInteraction.SetMenuState(_largestFilesMenu, "Locate", SelectedLargestFiles().Length == 1);
        };
        LibraryAnalyzerGridInteraction.AttachContextMenu(_largestFilesGrid, _largestFilesMenu);

        ContextMenuStrip drillDownMenu = new();
        ConfigureCommonLibraryFileMenu(drillDownMenu, () => _statisticsFileBrowser.SelectedFiles()
            .Select(file => (file.FileId, file.FullPath)).ToArray());
        drillDownMenu.Opening += (_, _) => UpdateCommonLibraryFileMenu(
            drillDownMenu,
            _statisticsFileBrowser.SelectedFiles().Select(file => (file.FileId, file.FullPath)).ToArray());
        LibraryAnalyzerGridInteraction.AttachContextMenu(_statisticsFileBrowser.Grid, drillDownMenu);
    }

    private void AddReanalysisMenuItems(ContextMenuStrip menu, Func<IEnumerable<long>> selectedFileIds)
    {
        void Queue(LibraryReanalysisWork work)
        {
            long[] ids = selectedFileIds().Distinct().ToArray();
            if (ids.Length > 0) _runtime.Reanalysis.QueueFiles(ids, work);
        }
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Re-analyze Metadata", "Metadata", () => { Queue(LibraryReanalysisWork.Metadata); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Re-analyze Exact", "Exact", () => { Queue(LibraryReanalysisWork.ExactHash); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Re-analyze Visual", "Visual", () => { Queue(LibraryReanalysisWork.VisualFingerprint); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Re-analyze All", "All", () => { Queue(LibraryReanalysisWork.All); return Task.CompletedTask; });
    }

    private void UpdateLocationsMenuState()
    {
        LibraryLocationRecord[] locations = SelectedLocations();
        bool selected = locations.Length > 0;
        bool canOpen = selected && locations.All(location => Directory.Exists(location.Path));
        bool enable = locations.Any(location => !location.IsEnabled);
        LibraryAnalyzerGridInteraction.SetMenuState(_locationsMenu, "Scan", selected && !_scanning);
        LibraryAnalyzerGridInteraction.SetMenuState(_locationsMenu, "Toggle", selected, enable ? "Enable" : "Disable");
        LibraryAnalyzerGridInteraction.SetMenuState(_locationsMenu, "Open", canOpen);
        LibraryAnalyzerGridInteraction.SetMenuState(_locationsMenu, "Remove", selected);
    }

    private void UpdateFilesMenuState()
    {
        LibraryFileViewRecord[] files = SelectedFiles();
        LibraryFileActionState state = LibraryFileActionState.Evaluate(files, file => file.FullPath, file => file.IsProtected);
        LibraryAnalyzerGridInteraction.SetMenuState(_filesMenu, "Play", state.CanPlay);
        LibraryAnalyzerGridInteraction.SetMenuState(_filesMenu, "Folder", state.CanOpenFolders);
        LibraryAnalyzerGridInteraction.SetMenuState(_filesMenu, "CopyPath", state.CanCopyPaths,
            files.Length > 1 ? "Copy File Paths" : "Copy File Path");
        LibraryAnalyzerGridInteraction.SetMenuState(_filesMenu, "Protect", state.HasSelection,
            state.AllProtected ? "Unprotect" : "Protect");
        foreach (string name in new[] { "Metadata", "Exact", "Visual", "All" })
            LibraryAnalyzerGridInteraction.SetMenuState(_filesMenu, name, state.HasSelection);
    }

    private void ConfigureCommonLibraryFileMenu(
        ContextMenuStrip menu,
        Func<(long FileId, string FullPath)[]> selection)
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Play", "Play", () =>
        {
            (long FileId, string FullPath)[] files = selection();
            if (files.Length == 1) PlayLibraryVideo(files[0].FullPath);
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Show in Explorer", "Folder", () =>
        {
            OpenContainingFolders(selection().Select(file => file.FullPath));
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "View Media Information", "MediaDetails", () =>
        {
            (long FileId, string FullPath)[] files = selection();
            if (files.Length == 1) ShowMediaDetails(files[0].FileId, files[0].FullPath);
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Copy Path", "CopyPath", () =>
        {
            CopyPaths(selection().Select(file => file.FullPath));
            return Task.CompletedTask;
        });
        menu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Add to Encode Queue", "Encode", () =>
        {
            QueueLibraryFiles(selection());
            return Task.CompletedTask;
        });
    }

    private void UpdateCommonLibraryFileMenu(
        ContextMenuStrip menu,
        (long FileId, string FullPath)[] files)
    {
        LibraryFileActionState state = LibraryFileActionState.Evaluate(
            files,
            file => file.FullPath,
            _ => false);
        int available = files.Count(file => File.Exists(file.FullPath));
        LibraryFileMenuPresentation presentation = LibraryFileMenuPresentation.ForSelection(
            files.Length,
            available,
            _reviewOptions.AddToEncodeQueue != null);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Play", state.CanPlay);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Folder", state.CanOpenFolders,
            presentation.ExplorerText);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "MediaDetails", files.Length == 1);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "CopyPath", state.CanCopyPaths,
            presentation.CopyText);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Encode",
            presentation.CanEncode,
            presentation.EncodeText);
    }

    private void QueueLibraryFiles((long FileId, string FullPath)[] files)
    {
        LibraryFileQueueResult result = LibraryFileQueueSelection.Dispatch(
            files.Select(file => file.FullPath),
            _reviewOptions.AddToEncodeQueue);
        if (result.AvailablePaths.Count == 0)
        {
            MessageBox.Show(
                this,
                "The selected file is missing or unavailable and was not added to the Encode queue.",
                "Add to Encode Queue",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (result.UnavailableCount > 0)
        {
            MessageBox.Show(
                this,
                $"{result.AvailablePaths.Count:N0} available file(s) were sent to the Encode queue. " +
                $"{result.UnavailableCount:N0} missing or unavailable file(s) were skipped.",
                "Add to Encode Queue",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private LibraryLocationRecord[] SelectedLocations() =>
        LibraryAnalyzerGridInteraction.SelectedItems<LibraryLocationRecord>(_locationsGrid);

    private LibraryFileViewRecord[] SelectedFiles() =>
        LibraryAnalyzerGridInteraction.SelectedItems<LibraryFileViewRecord>(_filesGrid);

    private LibraryLargestFile[] SelectedLargestFiles() =>
        LibraryAnalyzerGridInteraction.SelectedItems<LibraryLargestFile>(_largestFilesGrid);

    private async Task ToggleSelectedFileProtectionAsync()
    {
        LibraryFileViewRecord[] files = SelectedFiles();
        if (files.Length == 0) return;
        bool protect = files.Any(file => !file.IsProtected);
        await Task.Run(() =>
        {
            foreach (LibraryFileViewRecord file in files)
                _runtime.AnalysisCatalog.SetFileProtection(file.FileId, protect,
                    protect ? "Protected in Library Analyzer files" : "");
        });
        await RefreshFilesAsync();
    }

    private async Task SetSelectedLocationsEnabledAsync()
    {
        LibraryLocationRecord[] locations = SelectedLocations();
        if (locations.Length == 0) return;
        bool enabled = locations.Any(location => !location.IsEnabled);
        await Task.Run(() =>
        {
            foreach (LibraryLocationRecord location in locations)
                _runtime.Catalog.UpsertLocation(new LibraryLocationUpsert(
                    location.Path, location.IncludeSubfolders, enabled, location.Availability, location.LastError));
        });
        await RefreshAllAsync();
    }

    private void OpenSelectedLocations()
    {
        foreach (string path in SelectedLocations().Select(location => location.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            OpenFolder(path);
    }

    private void OpenContainingFolders(IEnumerable<string> paths)
    {
        foreach (string path in paths.GroupBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key)).Select(group => group.First()))
            OpenLibraryFileLocation(path);
    }

    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { ShowError("The selected location could not be opened.", ex); }
    }

    private void CopyPaths(IEnumerable<string> paths)
    {
        string value = string.Join(Environment.NewLine,
            paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (value.Length == 0) return;
        try { Clipboard.SetText(value); }
        catch (Exception ex) { ShowError("The selected path could not be copied.", ex); }
    }

    private async Task LocateLargestFileInFilesAsync()
    {
        if (SelectedLargestFiles().FirstOrDefault() is not { } file) return;
        await LocateFileInFilesAsync(file.FileId, file.FullPath);
    }

    private async Task LocateFileInFilesAsync(long fileId, string path)
    {
        _search.Text = path;
        _fileLocation.SelectedIndex = 0;
        _availability.SelectedIndex = 0;
        _probeStatus.SelectedIndex = 0;
        _page = 0;
        _tabs.SelectedTab = _tabs.TabPages.Cast<TabPage>().First(page => page.Text == "Files");
        await RefreshFilesAsync();
        DataGridViewRow? row = _filesGrid.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(value => (value.Tag as LibraryFileViewRecord)?.FileId == fileId);
        if (row == null) return;
        _filesGrid.ClearSelection();
        row.Selected = true;
        _filesGrid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
    }

    private void ConfigureLocationBreakdownContextMenu(DataGridView grid)
    {
        var menu = new ContextMenuStrip();
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "View matching files", "ViewFiles", async () =>
        {
            if (LibraryAnalyzerGridInteraction.SelectedItems<LibraryStatisticBucket>(grid).FirstOrDefault() is not { } bucket) return;
            LocationChoice? choice = _fileLocation.Items.Cast<LocationChoice>()
                .FirstOrDefault(item => item.Id > 0 && item.Name.Equals(bucket.Label, StringComparison.OrdinalIgnoreCase));
            if (choice == null) return;
            _search.Clear();
            _fileLocation.SelectedItem = choice;
            _availability.SelectedIndex = 0;
            _probeStatus.SelectedIndex = 0;
            _page = 0;
            _tabs.SelectedTab = _tabs.TabPages.Cast<TabPage>().First(page => page.Text == "Files");
            await RefreshFilesAsync();
        });
        menu.Opening += (_, _) => LibraryAnalyzerGridInteraction.SetMenuState(menu, "ViewFiles",
            LibraryAnalyzerGridInteraction.SelectedItems<LibraryStatisticBucket>(grid).Length == 1);
        LibraryAnalyzerGridInteraction.AttachContextMenu(grid, menu);
    }

    private void ConfigureSharedGridLayouts()
    {
        DataGridView[] grids = Descendants<DataGridView>(this).ToArray();
        foreach (IGrouping<TabPage?, DataGridView> group in grids.GroupBy(FindOuterTab))
        {
            int index = 0;
            foreach (DataGridView grid in group)
            {
                string name = string.IsNullOrWhiteSpace(grid.Name) ? $"Grid{index++}" : grid.Name;
                _layoutController.RegisterGrid(grid, $"{group.Key?.Text ?? "Analyzer"}.{name}");
            }
        }
        foreach (IGrouping<TabPage?, SplitContainer> group in Descendants<SplitContainer>(this).GroupBy(FindOuterTab))
        {
            int splitIndex = 0;
            foreach (SplitContainer split in group)
                _layoutController.RegisterSplitter(split, $"{group.Key?.Text ?? "Analyzer"}.Split{splitIndex++}");
        }
    }

    private static TabPage? FindOuterTab(Control control)
    {
        TabPage? result = null;
        for (Control? current = control.Parent; current != null; current = current.Parent)
            if (current is TabPage page) result = page;
        return result;
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
