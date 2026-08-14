using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private const int StatisticsTopCount = 10;
        private readonly Label _statisticsFiles = ValueLabel();
        private readonly Label _statisticsStorage = ValueLabel();
        private readonly Label _statisticsHealth = ValueLabel();
        private readonly Label _statisticsDuplicates = ValueLabel();
        private readonly TabControl _statisticsBreakdowns = new() { Dock = DockStyle.Fill };
        private readonly DataGridView _largestFilesGrid = CreateGrid("LargestFilesGrid");
        private bool _loadingStatistics;

        private void BuildStatisticsTab()
        {
            var tab = new TabPage("Statistics") { Padding = new Padding(10) };
            var cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 76, ColumnCount = 4, Padding = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            cards.Controls.Add(StatisticCard("Indexed files", _statisticsFiles), 0, 0);
            cards.Controls.Add(StatisticCard("Indexed storage", _statisticsStorage), 1, 0);
            cards.Controls.Add(StatisticCard("Metadata health", _statisticsHealth), 2, 0);
            cards.Controls.Add(StatisticCard("Exact duplicates", _statisticsDuplicates), 3, 0);

            _statisticsBreakdowns.TabPages.Add(CreateBreakdownTab("Storage by location"));
            _statisticsBreakdowns.TabPages.Add(CreateBreakdownTab("Codec", LibraryStatisticCategory.Codec));
            _statisticsBreakdowns.TabPages.Add(CreateBreakdownTab("Resolution", LibraryStatisticCategory.Resolution));
            _statisticsBreakdowns.TabPages.Add(CreateBreakdownTab("Container", LibraryStatisticCategory.Container));
            _statisticsBreakdowns.TabPages.Add(CreateBreakdownTab("HDR / SDR", LibraryStatisticCategory.DynamicRange));

            AddLargestColumn("Name", "Largest files", 210);
            AddLargestColumn("Path", "Path", 430);
            AddLargestColumn("Size", "Size", 100);
            AddLargestColumn("Codec", "Codec", 100);
            AddLargestColumn("Resolution", "Resolution", 100);
            var largestPage = new TabPage("Largest files") { Padding = new Padding(4) };
            largestPage.Controls.Add(_largestFilesGrid);
            _statisticsBreakdowns.TabPages.Add(largestPage);
            var filesPage = new TabPage("Files") { Name = "StatisticsFilesPage", Padding = new Padding(4) };
            filesPage.Controls.Add(_statisticsFileBrowser);
            _statisticsBreakdowns.TabPages.Add(filesPage);

            var refresh = new Button { Text = "Refresh statistics", AutoSize = true, Dock = DockStyle.Bottom };
            refresh.Click += async (_, _) => await RefreshStatisticsAsync();
            tab.Controls.Add(_statisticsBreakdowns);
            tab.Controls.Add(refresh);
            tab.Controls.Add(cards);
            _tabs.TabPages.Add(tab);
        }

        private async Task RefreshStatisticsAsync()
        {
            if (_loadingStatistics || IsDisposed) return;
            _loadingStatistics = true;
            try
            {
                LibraryStatistics statistics = await Task.Run(() => _runtime.AnalysisCatalog.GetLibraryStatistics(StatisticsTopCount));
                if (IsDisposed) return;
                _statisticsFiles.Text = $"{statistics.TotalFiles:N0} ({statistics.PresentFiles:N0} available)";
                _statisticsStorage.Text = FormatBytes(statistics.TotalBytes);
                _statisticsHealth.Text = $"{statistics.ProbeSucceeded:N0} OK · {statistics.ProbeFailed:N0} failed";
                _statisticsDuplicates.Text = $"{statistics.ExactDuplicateGroups:N0} groups · {FormatBytes(statistics.ExactDuplicateBytes)} total · {FormatBytes(statistics.ReclaimableDuplicateBytes)} reclaimable";
                FillBreakdown((DataGridView)_statisticsBreakdowns.TabPages[0].Controls[0], statistics.ByLocation);
                FillBreakdown((DataGridView)_statisticsBreakdowns.TabPages[1].Controls[0], statistics.ByCodec);
                FillBreakdown((DataGridView)_statisticsBreakdowns.TabPages[2].Controls[0], statistics.ByResolution);
                FillBreakdown((DataGridView)_statisticsBreakdowns.TabPages[3].Controls[0], statistics.ByContainer);
                FillBreakdown((DataGridView)_statisticsBreakdowns.TabPages[4].Controls[0], statistics.ByDynamicRange);
                long[] selectedLargest = SelectedLargestFiles().Select(file => file.FileId).ToArray();
                _largestFilesGrid.Rows.Clear();
                foreach (LibraryLargestFile file in statistics.LargestFiles)
                {
                    int row = _largestFilesGrid.Rows.Add(file.FileName, file.FullPath, FormatBytes(file.SizeBytes), file.VideoCodec, file.ResolutionTier);
                    _largestFilesGrid.Rows[row].Tag = file;
                }
                _largestFilesGrid.ClearSelection();
                if (selectedLargest.Length > 0)
                {
                    var selected = selectedLargest.ToHashSet();
                    foreach (DataGridViewRow row in _largestFilesGrid.Rows)
                        row.Selected = row.Tag is LibraryLargestFile file && selected.Contains(file.FileId);
                }
                if (_statisticsFileBrowser.DrillDown != null)
                    await _statisticsFileBrowser.RefreshAsync();
            }
            catch (Exception ex)
            {
                ShowError("Statistics could not be refreshed.", ex);
            }
            finally { _loadingStatistics = false; }
        }

        private static Panel StatisticCard(string title, Label value)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4) };
            panel.Controls.Add(value);
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 24, Padding = new Padding(7, 5, 0, 0) });
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleLeft;
            return panel;
        }

        private TabPage CreateBreakdownTab(string title, LibraryStatisticCategory? category = null)
        {
            var grid = CreateGrid("Statistics" + new string(title.Where(char.IsLetterOrDigit).ToArray()) + "Grid");
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = title, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Files", HeaderText = "Files", Width = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Storage", HeaderText = "Storage", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Share", HeaderText = "Relative storage", Width = 230 });
            var page = new TabPage(title) { Padding = new Padding(4) };
            page.Controls.Add(grid);
            if (title == "Storage by location")
                ConfigureLocationBreakdownContextMenu(grid);
            else if (category.HasValue)
                ConfigureStatisticsDrillDown(grid, category.Value, title);
            return page;
        }

        private static void FillBreakdown(DataGridView grid, IReadOnlyList<LibraryStatisticBucket> buckets)
        {
            grid.Rows.Clear();
            long maximum = buckets.Count == 0 ? 0 : buckets.Max(item => item.SizeBytes);
            foreach (LibraryStatisticBucket bucket in buckets)
            {
                int bars = maximum == 0 ? 0 : (int)Math.Round(bucket.SizeBytes * 20d / maximum);
                int row = grid.Rows.Add(bucket.Label, bucket.FileCount.ToString("N0"), FormatBytes(bucket.SizeBytes), new string('█', bars));
                grid.Rows[row].Tag = bucket;
            }
            grid.ClearSelection();
        }

        private void ConfigureStatisticsDrillDown(
            DataGridView grid,
            LibraryStatisticCategory category,
            string categoryName)
        {
            var menu = new ContextMenuStrip();
            LibraryAnalyzerGridInteraction.AddMenuItem(menu, "View Files", "ViewFiles",
                () => OpenStatisticsFilesAsync(grid, category, categoryName));
            menu.Opening += (_, _) => LibraryAnalyzerGridInteraction.SetMenuState(
                menu,
                "ViewFiles",
                LibraryAnalyzerGridInteraction.SelectedItems<LibraryStatisticBucket>(grid).Length == 1);
            LibraryAnalyzerGridInteraction.AttachContextMenu(grid, menu);
            grid.CellDoubleClick += async (_, e) =>
            {
                if (e.RowIndex < 0) return;
                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
                await OpenStatisticsFilesAsync(grid, category, categoryName);
            };
        }

        private async Task OpenStatisticsFilesAsync(
            DataGridView grid,
            LibraryStatisticCategory category,
            string categoryName)
        {
            LibraryStatisticBucket? bucket =
                LibraryAnalyzerGridInteraction.SelectedItems<LibraryStatisticBucket>(grid).SingleOrDefault();
            if (bucket == null) return;
            var drillDown = new LibraryStatisticDrillDown(
                category,
                bucket.Label,
                bucket.IsRemainder,
                TopCount: StatisticsTopCount,
                ExcludedLabels: bucket.IsRemainder
                    ? grid.Rows.Cast<DataGridViewRow>()
                        .Select(row => row.Tag)
                        .OfType<LibraryStatisticBucket>()
                        .Where(item => !item.IsRemainder)
                        .Select(item => item.Label)
                        .ToArray()
                    : null);
            _statisticsBreakdowns.SelectedTab = _statisticsBreakdowns.TabPages["StatisticsFilesPage"];
            await _statisticsFileBrowser.OpenAsync(
                drillDown,
                $"{categoryName}: {bucket.Label} — {bucket.FileCount:N0} files");
        }

        private void AddLargestColumn(string name, string header, int width) =>
            _largestFilesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
    }
}
