using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private readonly Label _statisticsFiles = ValueLabel();
        private readonly Label _statisticsStorage = ValueLabel();
        private readonly Label _statisticsHealth = ValueLabel();
        private readonly Label _statisticsDuplicates = ValueLabel();
        private readonly TabControl _statisticsBreakdowns = new() { Dock = DockStyle.Fill };
        private readonly DataGridView _largestFilesGrid = CreateGrid();
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

            foreach (string name in new[] { "Storage by location", "Codec", "Resolution", "Container", "HDR / SDR" })
                _statisticsBreakdowns.TabPages.Add(CreateBreakdownTab(name));

            AddLargestColumn("Name", "Largest files", 210);
            AddLargestColumn("Path", "Path", 430);
            AddLargestColumn("Size", "Size", 100);
            AddLargestColumn("Codec", "Codec", 100);
            AddLargestColumn("Resolution", "Resolution", 100);
            var largestPage = new TabPage("Largest files") { Padding = new Padding(4) };
            largestPage.Controls.Add(_largestFilesGrid);
            _statisticsBreakdowns.TabPages.Add(largestPage);

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
                LibraryStatistics statistics = await Task.Run(() => _runtime.AnalysisCatalog.GetLibraryStatistics(10));
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
                _largestFilesGrid.Rows.Clear();
                foreach (LibraryLargestFile file in statistics.LargestFiles)
                    _largestFilesGrid.Rows.Add(file.FileName, file.FullPath, FormatBytes(file.SizeBytes), file.VideoCodec, file.ResolutionTier);
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

        private static TabPage CreateBreakdownTab(string title)
        {
            var grid = CreateGrid();
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = title, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Files", HeaderText = "Files", Width = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Storage", HeaderText = "Storage", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Share", HeaderText = "Relative storage", Width = 230 });
            var page = new TabPage(title) { Padding = new Padding(4) };
            page.Controls.Add(grid);
            return page;
        }

        private static void FillBreakdown(DataGridView grid, IReadOnlyList<LibraryStatisticBucket> buckets)
        {
            grid.Rows.Clear();
            long maximum = buckets.Count == 0 ? 0 : buckets.Max(item => item.SizeBytes);
            foreach (LibraryStatisticBucket bucket in buckets)
            {
                int bars = maximum == 0 ? 0 : (int)Math.Round(bucket.SizeBytes * 20d / maximum);
                grid.Rows.Add(bucket.Label, bucket.FileCount.ToString("N0"), FormatBytes(bucket.SizeBytes), new string('█', bars));
            }
        }

        private void AddLargestColumn(string name, string header, int width) =>
            _largestFilesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
    }
}
