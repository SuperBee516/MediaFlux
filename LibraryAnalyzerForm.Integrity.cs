using System.Diagnostics;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly DataGridView _integrityGrid = CreateGrid();
    private readonly ComboBox _integrityState = DropDown();
    private readonly ComboBox _integrityLocation = DropDown();
    private readonly TextBox _integritySearch = new() { Width = 220, PlaceholderText = "Filename or path" };
    private readonly Label _integritySummary = new() { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8, 5, 0, 0) };
    private readonly Label _integrityStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 7, 0, 0) };
    private readonly Label _integrityPageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    private int _integrityPage;
    private long _integrityTotal;

    private void BuildMediaIntegrityTab()
    {
        var tab = new TabPage("Media Integrity") { Padding = new Padding(10) };
        var intro = new Label
        {
            Name = "MediaIntegrityIntro", Dock = DockStyle.Top, Height = 46, Padding = new Padding(4),
            ForeColor = LibraryAnalyzerAccentColor,
            Text = "Verify whether the current file version can actually be decoded. Quick Scrub samples representative regions; Full Scrub explicitly decodes complete media streams. Both are diagnostic and never modify media."
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, WrapContents = true, AutoScroll = true };
        actions.Controls.Add(new Label { Text = "Show:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        _integrityState.Name = "IntegrityStateFilter"; _integrityState.Width = 145;
        _integrityState.Items.Add(new IntegrityStateChoice("All files", null));
        foreach (LibraryIntegrityResultState state in Enum.GetValues<LibraryIntegrityResultState>())
            if (state is not LibraryIntegrityResultState.Pending and not LibraryIntegrityResultState.Running)
                _integrityState.Items.Add(new IntegrityStateChoice(IntegrityStateLabel(state), state));
        _integrityState.SelectedIndex = 0; _integrityState.SelectedIndexChanged += async (_, _) => { _integrityPage = 0; await RefreshIntegrityAsync(); };
        actions.Controls.Add(_integrityState);
        actions.Controls.Add(new Label { Text = "Location:", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        _integrityLocation.Name = "IntegrityLocationFilter"; _integrityLocation.Width = 210;
        _integrityLocation.SelectedIndexChanged += async (_, _) => { _integrityPage = 0; await RefreshIntegrityAsync(); };
        actions.Controls.Add(_integrityLocation); actions.Controls.Add(_integritySearch);
        AddButton(actions, "Search", async (_, _) => { _integrityPage = 0; await RefreshIntegrityAsync(); });
        AddButton(actions, "Quick Scrub selected", (_, _) => QueueSelectedIntegrity(LibraryIntegrityScrubType.Quick));
        AddButton(actions, "Full Scrub selected…", (_, _) => QueueSelectedIntegrity(LibraryIntegrityScrubType.Full));
        AddButton(actions, "Quick Scrub location", async (_, _) => await QueueIntegrityLocationAsync());
        AddButton(actions, "Quick Scrub stale/unverified", async (_, _) => await QueueIntegrityStaleOrUnverifiedAsync());
        AddButton(actions, "Retry selected", (_, _) => RetrySelectedIntegrity());
        AddButton(actions, "Cancel running", (_, _) => _runtime.Integrity.CancelRunning());
        AddButton(actions, "Open in Explorer", (_, _) => OpenSelectedIntegrityInExplorer());
        AddButton(actions, "Previous", async (_, _) => { if (_integrityPage > 0) { _integrityPage--; await RefreshIntegrityAsync(); } });
        AddButton(actions, "Next", async (_, _) => { if ((_integrityPage + 1L) * PageSize < _integrityTotal) { _integrityPage++; await RefreshIntegrityAsync(); } });
        actions.Controls.Add(_integrityPageLabel);

        AddIntegrityColumn("Status", "Integrity status", 115); AddIntegrityColumn("File", "File", 220);
        AddIntegrityColumn("Location", "Location", 220); AddIntegrityColumn("Size", "Size", 90);
        AddIntegrityColumn("Codec", "Codec", 85); AddIntegrityColumn("Checked", "Last checked", 135);
        AddIntegrityColumn("Type", "Scrub type", 80); AddIntegrityColumn("Performance", "Verified / performance", 180);
        AddIntegrityColumn("Details", "Result / details", 420, true); AddIntegrityColumn("Path", "Path", 300);
        _integrityGrid.MultiSelect = true;
        tab.Controls.Add(_integrityGrid); tab.Controls.Add(_integrityStatus); tab.Controls.Add(_integritySummary); tab.Controls.Add(intro); tab.Controls.Add(actions);
        _tabs.TabPages.Add(tab); ReloadIntegrityLocations();
    }

    private async Task RefreshIntegrityAsync()
    {
        try
        {
            LibraryIntegrityResultState? state = (_integrityState.SelectedItem as IntegrityStateChoice)?.State;
            long? location = (_integrityLocation.SelectedItem as LocationChoice)?.Id is > 0 and var selectedLocation ? selectedLocation : null;
            var query = new LibraryIntegrityQuery(state, location, _integritySearch.Text, _integrityPage * PageSize, PageSize);
            (LibraryIntegrityPage page, LibraryIntegritySummary summary) = await Task.Run(() =>
                (_runtime.IntegrityCatalog.QueryIntegrity(query), _runtime.IntegrityCatalog.GetIntegritySummary()));
            if (IsDisposed) return;
            _integrityGrid.Rows.Clear();
            foreach (LibraryIntegrityResult item in page.Results)
            {
                string performance = item.ElapsedSeconds > 0
                    ? $"{FormatBytes(item.BytesChecked)} in {TimeSpan.FromSeconds(item.ElapsedSeconds):g} · {FormatBytes((long)(item.BytesChecked / item.ElapsedSeconds))}/s"
                    : "Not measured";
                int row = _integrityGrid.Rows.Add(IntegrityStateLabel(item.State), item.FileName, item.LocationPath, FormatBytes(item.SizeBytes),
                    item.VideoCodec, item.CheckedUtc?.ToLocalTime().ToString("g") ?? "Never", item.State == LibraryIntegrityResultState.NeverChecked ? "—" : item.ScrubType,
                    performance, item.Details, item.FullPath);
                _integrityGrid.Rows[row].Tag = item;
                if (item.State == LibraryIntegrityResultState.Failed) _integrityGrid.Rows[row].DefaultCellStyle.ForeColor = Color.Firebrick;
                else if (item.State is LibraryIntegrityResultState.Warning or LibraryIntegrityResultState.Stale or LibraryIntegrityResultState.Cancelled)
                    _integrityGrid.Rows[row].DefaultCellStyle.ForeColor = Color.DarkOrange;
            }
            _integrityTotal = page.TotalCount; long first = page.TotalCount == 0 ? 0 : (long)_integrityPage * PageSize + 1;
            long last = Math.Min(page.TotalCount, (long)(_integrityPage + 1) * PageSize);
            _integrityPageLabel.Text = page.TotalCount == 0 ? "No rows" : $"{first:N0}–{last:N0} of {page.TotalCount:N0}";
            _integritySummary.Text = $"{summary.Passed:N0} passed · {summary.Warnings:N0} warning · {summary.Failed:N0} failed · {summary.NeverChecked:N0} never checked · " +
                $"{summary.Stale:N0} stale · {summary.Pending:N0} pending · {summary.Running:N0} running · {summary.Cancelled:N0} cancelled";
        }
        catch (Exception ex) { if (!IsDisposed) ShowError("Media Integrity results could not be refreshed.", ex); }
    }

    private void QueueSelectedIntegrity(LibraryIntegrityScrubType type)
    {
        long[] ids = _integrityGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<LibraryIntegrityResult>().Select(item => item.FileId).Distinct().ToArray();
        if (ids.Length == 0) return;
        if (type == LibraryIntegrityScrubType.Full && MessageBox.Show(this,
                $"Queue Full Scrub for {ids.Length:N0} selected file(s)?\r\n\r\nFull Scrub decodes complete streams and can take a long time. It will not modify media or start encoding.",
                "Full Media Integrity Scrub", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _runtime.Integrity.QueueFiles(ids, type); _integrityStatus.Text = $"Queued {ids.Length:N0} {type} Scrub check(s).";
    }

    private async Task QueueIntegrityLocationAsync()
    {
        long locationId = (_integrityLocation.SelectedItem as LocationChoice)?.Id ?? 0;
        if (locationId <= 0) { MessageBox.Show(this, "Select a specific location first.", "Media Integrity", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        _integrityStatus.Text = "Finding files and queuing Quick Scrub checks…";
        int count = await Task.Run(() =>
        {
            IReadOnlyList<long> ids = _runtime.IntegrityCatalog.GetIntegrityFileIds(locationId, null);
            _runtime.Integrity.QueueFiles(ids, LibraryIntegrityScrubType.Quick);
            return ids.Count;
        });
        if (!IsDisposed) _integrityStatus.Text = $"Queued Quick Scrub for {count:N0} file(s) in the selected location.";
    }

    private async Task QueueIntegrityStaleOrUnverifiedAsync()
    {
        _integrityStatus.Text = "Finding stale or unverified files and queuing Quick Scrub checks…";
        int count = await Task.Run(() =>
        {
            long[] ids = _runtime.IntegrityCatalog.GetIntegrityFileIds(null, LibraryIntegrityResultState.NeverChecked)
                .Concat(_runtime.IntegrityCatalog.GetIntegrityFileIds(null, LibraryIntegrityResultState.Stale)).Distinct().ToArray();
            _runtime.Integrity.QueueFiles(ids, LibraryIntegrityScrubType.Quick);
            return ids.Length;
        });
        if (!IsDisposed) _integrityStatus.Text = $"Queued Quick Scrub for {count:N0} stale or unverified file(s).";
    }

    private void RetrySelectedIntegrity()
    {
        LibraryIntegrityResult[] items = _integrityGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<LibraryIntegrityResult>()
            .Where(item => item.State is LibraryIntegrityResultState.Failed or LibraryIntegrityResultState.Cancelled or LibraryIntegrityResultState.Stale or LibraryIntegrityResultState.Unavailable).ToArray();
        foreach (IGrouping<LibraryIntegrityScrubType, LibraryIntegrityResult> group in items.GroupBy(item => item.ScrubType))
            _runtime.Integrity.QueueFiles(group.Select(item => item.FileId), group.Key);
        _integrityStatus.Text = $"Queued {items.Length:N0} integrity retry check(s).";
    }

    private void OpenSelectedIntegrityInExplorer()
    {
        string? path = _integrityGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<LibraryIntegrityResult>().FirstOrDefault()?.FullPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Explorer could not open the selected file.", ex); }
    }

    private void Integrity_ProgressChanged(LibraryIntegrityProgress progress)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => _integrityStatus.Text = progress.Percent.HasValue
            ? $"{progress.ScrubType} Scrub · {Path.GetFileName(progress.FullPath)} · {progress.Percent:0.#}% · elapsed {progress.Elapsed:g}" +
              (progress.EstimatedRemaining.HasValue ? $" · remaining {progress.EstimatedRemaining:g}" : "")
            : $"{progress.ScrubType} Scrub · {Path.GetFileName(progress.FullPath)} · {progress.Status}");
    }

    private void ReloadIntegrityLocations()
    {
        _integrityLocation.Items.Clear(); _integrityLocation.Items.Add(new LocationChoice(0, "All locations"));
        foreach (LibraryLocationRecord location in _runtime.Catalog.GetLocations()) _integrityLocation.Items.Add(new LocationChoice(location.Id, location.Path));
        _integrityLocation.SelectedIndex = 0;
    }

    private void AddIntegrityColumn(string name, string header, int width, bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true };
        if (fill) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; _integrityGrid.Columns.Add(column);
    }
    private static string IntegrityStateLabel(LibraryIntegrityResultState state) => state switch
    {
        LibraryIntegrityResultState.NeverChecked => "Never checked", LibraryIntegrityResultState.Pending => "Pending",
        LibraryIntegrityResultState.Running => "Running", LibraryIntegrityResultState.Passed => "Passed",
        LibraryIntegrityResultState.Warning => "Warning", LibraryIntegrityResultState.Failed => "Failed",
        LibraryIntegrityResultState.Stale => "Stale", LibraryIntegrityResultState.Unavailable => "Unavailable", _ => "Cancelled"
    };
    private sealed record IntegrityStateChoice(string Label, LibraryIntegrityResultState? State) { public override string ToString() => Label; }
}
