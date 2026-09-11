using System.Drawing.Drawing2D;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private void BuildOverviewDashboard()
    {
        var tab = new TabPage("Overview") { Padding = new Padding(12), AutoScroll = true };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(2), GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(4, 2, 4, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "Library Overview", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = LibraryAnalyzerAccentColor, Padding = new Padding(0, 2, 0, 0) }, 0, 0);
        header.Controls.Add(_overviewHealthState, 1, 0);
        header.Controls.Add(_overviewHeaderSummary, 0, 1); header.SetColumnSpan(_overviewHeaderSummary, 2);
        header.Controls.Add(_overviewLiveStatus, 0, 2); header.SetColumnSpan(_overviewLiveStatus, 2);
        root.Controls.Add(header, 0, 0);

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(2) };
        for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.Controls.Add(_overviewVideosCard, 0, 0); cards.Controls.Add(_overviewSizeCard, 1, 0); cards.Controls.Add(_overviewDuplicatesCard, 2, 0); cards.Controls.Add(_overviewReclaimCard, 3, 0);
        root.Controls.Add(cards, 0, 1);

        var panels = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(2) };
        panels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); panels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        panels.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); panels.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); panels.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        panels.Controls.Add(CreateOverviewPanel("Duplicate Analysis", BuildDuplicatePanel()), 0, 0);
        panels.Controls.Add(CreateOverviewPanel("Library Health", BuildHealthPanel()), 1, 0);
        panels.Controls.Add(CreateOverviewPanel("Storage by Location", _overviewLocationChart), 0, 1);
        panels.Controls.Add(CreateOverviewPanel("Library Composition", BuildCompositionPanel()), 1, 1);
        panels.Controls.Add(CreateOverviewPanel("Library Growth", BuildGrowthPanel()), 0, 2);
        panels.Controls.Add(CreateOverviewPanel("Library Insights", _overviewInsights), 1, 2);
        root.Controls.Add(panels, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(2, 4, 0, 3), Margin = new Padding(2, 0, 0, 1) };
        Button refresh = AddButton(actions, "Refresh", async (_, _) => await RefreshAllAsync());
        refresh.AccessibleName = "Refresh library overview";
        var more = new Button { Text = "More", AutoSize = true, AccessibleName = "More maintenance actions" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Backup decisions…", null, BackupUserDecisions_Click);
        menu.Items.Add("Restore decisions…", null, RestoreUserDecisions_Click);
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height));
        actions.Controls.Add(more);
        root.Controls.Add(actions, 0, 3);
        tab.Controls.Add(root); _tabs.TabPages.Add(tab);
        _overviewCompositionSelector.Items.AddRange(new object[] { "Resolution", "Video codec", "Container" });
        _overviewCompositionSelector.SelectedIndex = 0;
        _overviewCompositionSelector.SelectedIndexChanged += (_, _) => RenderOverviewComposition(_overviewSnapshot);
        _overviewCompositionSelector.AccessibleName = "Library composition measure";
        _overviewCompositionSelector.TabIndex = 0;
        _overviewGrowthMetricSelector.Items.AddRange(new object[] { "Files", "Size" });
        _overviewGrowthMetricSelector.SelectedIndex = 0;
        _overviewGrowthMetricSelector.AccessibleName = "Library growth metric";
        _overviewGrowthMetricSelector.SelectedIndexChanged += (_, _) => _overviewGrowthChart.Metric = _overviewGrowthMetricSelector.SelectedIndex == 1 ? OverviewGrowthMetric.Size : OverviewGrowthMetric.Files;
        _overviewLocationChart.IsInteractive = true;
        _overviewLocationChart.ItemClicked += (_, index) => NavigateToOverviewLocation(index);
        _overviewToolTip.SetToolTip(_overviewVideosCard, "Indexed video records in the catalog.");
        _overviewToolTip.SetToolTip(_overviewSizeCard, "Logical size from persisted catalog inventory.");
        _overviewToolTip.SetToolTip(_overviewDuplicatesCard, "Open duplicate review.");
        _overviewToolTip.SetToolTip(_overviewReclaimCard, "Estimated reclaimable storage using existing keeper decisions.");
        MakeOverviewInteractive(_overviewDuplicatesCard, () => NavigateToOverviewTab(4));
        MakeOverviewInteractive(_overviewReclaimCard, () => NavigateToOverviewTab(4));
        _overviewExactLink.Click += (_, _) => NavigateToOverviewTab(4);
        _overviewVisualLink.Click += (_, _) => NavigateToOverviewTab(5);
        _overviewFamilyLink.Click += (_, _) => NavigateToOverviewTab(6);
        _overviewDuplicateProgress.Click += (_, _) => NavigateToOverviewTab(4);
        MakeOverviewInteractive(_overviewHealthSummary, () => NavigateToOverviewTab(7));
        _overviewToolTip.SetToolTip(_overviewExactLink, "Open Exact Duplicates.");
        _overviewToolTip.SetToolTip(_overviewVisualLink, "Open Visual Duplicates.");
        _overviewToolTip.SetToolTip(_overviewFamilyLink, "Open Duplicate Families.");
        _overviewToolTip.SetToolTip(_overviewDuplicateProgress, "Open duplicate review.");
        _overviewHealthSummary.Cursor = Cursors.Default;
        _overviewRefreshDebounceTimer.Tick += async (_, _) =>
        {
            _overviewRefreshDebounceTimer.Stop();
            await RefreshOverviewAsync();
        };
        _overviewToolTip.SetToolTip(_overviewHealthSummary, "Open Health & Recovery when attention is reported.");
        _overviewInsights.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _overviewInsights.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _overviewLocationChart.MinimumSize = new Size(0, 70); _overviewCompositionChart.MinimumSize = new Size(0, 70); _overviewGrowthChart.MinimumSize = new Size(0, 70);
        _overviewLocationChart.AccessibleName = "Storage by location chart";
        _overviewCompositionChart.AccessibleName = "Library composition chart";
        _overviewGrowthChart.AccessibleName = "Library growth chart";
    }

    private Control BuildDuplicatePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(4) };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 22)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 22)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 22)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 8)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        panel.Controls.Add(_overviewExactLink, 0, 0); panel.Controls.Add(_overviewVisualLink, 0, 1); panel.Controls.Add(_overviewFamilyLink, 0, 2); panel.Controls.Add(_overviewDuplicateProgress, 0, 3); panel.Controls.Add(_overviewDuplicateProgressBar, 0, 4);
        panel.Controls.Add(new Label { Text = "Reclaimable space follows existing keeper and review decisions.", AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = DashboardVisuals.MutedText, Padding = new Padding(8, 2, 8, 2) }, 0, 5);
        return panel;
    }

    private Control BuildHealthPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        panel.Controls.Add(_overviewHealthSummary, 0, 0);
        panel.Controls.Add(new Label { Text = "Detailed recovery and decision history remain in Health & Recovery.", Dock = DockStyle.Fill, ForeColor = DashboardVisuals.MutedText, Padding = new Padding(8, 2, 8, 2), AutoEllipsis = true }, 0, 1);
        return panel;
    }

    private Control BuildCompositionPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selector = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false, Padding = new Padding(6, 0, 0, 0) };
        _overviewCompositionSelector.Width = 145; selector.Controls.Add(_overviewCompositionSelector);
        var statistics = new Button { Text = "Open Statistics", AutoSize = true, AccessibleName = "Open Library Statistics" };
        statistics.Click += (_, _) => NavigateToOverviewTab(3); selector.Controls.Add(statistics);
        panel.Controls.Add(selector, 0, 0); panel.Controls.Add(_overviewCompositionChart, 0, 1); return panel;
    }

    private Control BuildGrowthPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(4, 0, 0, 0) };
        _overviewGrowthMetricSelector.Width = 86;
        header.Controls.Add(_overviewGrowthMetricSelector);
        panel.Controls.Add(header, 0, 0);
        var chartHost = new Panel { Dock = DockStyle.Fill };
        chartHost.Controls.Add(_overviewGrowthChart); chartHost.Controls.Add(_overviewGrowthEmpty);
        panel.Controls.Add(chartHost, 0, 1); panel.Controls.Add(_overviewGrowthSummary, 0, 2); return panel;
    }

    private static Control CreateOverviewPanel(string title, Control content) => new OverviewDashboardPanel(title, content);

    private async Task RefreshOverviewAsync()
    {
        if (_loadingOverview || _lifecycleCleanupCompleted || IsDisposed || Disposing) return;
        _loadingOverview = true;
        try
        {
            CancellationToken token = _overviewRefreshCancellation.Token;
            Task<LibraryOverviewSnapshot> snapshotTask = _runtime.Overview.LoadAsync(LibraryEnrichmentCoordinator.CurrentMetadataVersion, token);
            Task<IReadOnlyList<LibraryOverviewScanHistoryEntry>> historyTask = _runtime.Overview.LoadHistoryAsync(cancellationToken: token);
            await Task.WhenAll(snapshotTask, historyTask);
            LibraryOverviewSnapshot snapshot = await snapshotTask;
            IReadOnlyList<LibraryOverviewScanHistoryEntry> history = await historyTask;
            if (_lifecycleCleanupCompleted || IsDisposed || Disposing) return;
            _overviewSnapshot = snapshot; _overviewHistory = history;
            RenderOverview(snapshot, history);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (IsDisposed) { }
        catch (Exception ex) { if (!IsDisposed) ShowError("Library overview could not be refreshed.", ex); }
        finally
        {
            _loadingOverview = false;
            if (_overviewRefreshQueued && !IsDisposed && !Disposing)
            {
                _overviewRefreshQueued = false;
                _overviewRefreshDebounceTimer.Stop();
                _overviewRefreshDebounceTimer.Start();
            }
        }
    }

    private void RenderOverview(LibraryOverviewSnapshot snapshot, IReadOnlyList<LibraryOverviewScanHistoryEntry> history)
    {
        bool scanning = _scanning || snapshot.ActiveScanCount > 0;
        bool enriching = !scanning && (snapshot.PendingEnrichmentCount > 0 || _runtime.Enrichment.IsRunning);
        bool warning = snapshot.Health.UnavailableLocations > 0 || snapshot.Health.ErrorLocations > 0 || snapshot.Health.MissingFiles > 0 || snapshot.Health.MetadataFailures > 0 || snapshot.Health.IntegrityWarnings > 0 || snapshot.Health.IntegrityFailures > 0;
        OverviewStatusPresentation presentation = GetOverviewStatusPresentation(scanning, enriching, warning);
        _overviewHealthState.SetState(presentation.Text, presentation.Kind);
        _overviewHeaderSummary.Text = snapshot.LastCompletedScanUtc.HasValue ? $"{snapshot.IndexedVideoCount:N0} indexed videos · Last completed scan {snapshot.LastCompletedScanUtc.Value.ToLocalTime():g}" : "No completed scan yet · Add a location and scan to build the catalog";
        _overviewVideosCard.SetValue(snapshot.IndexedVideoCount.ToString("N0"), $"{snapshot.Locations.Count(x => x.FileCount > 0):N0} locations with files");
        _overviewSizeCard.SetValue(FormatBytes(snapshot.LogicalSizeBytes), snapshot.IndexedVideoCount == 0 ? "No indexed media" : $"{FormatBytes(snapshot.LogicalSizeBytes / Math.Max(1, snapshot.IndexedVideoCount))} average");
        long duplicateSets = snapshot.Duplicates.ExactGroups + snapshot.Duplicates.VisualGroups + snapshot.Duplicates.FamilyGroups;
        long affectedFiles = snapshot.Duplicates.ExactAffectedFiles + snapshot.Duplicates.VisualAffectedFiles + snapshot.Duplicates.FamilyAffectedFiles;
        _overviewDuplicatesCard.SetValue(duplicateSets.ToString("N0"), $"{affectedFiles:N0} category totals · overlap possible");
        double reclaimablePercent = snapshot.LogicalSizeBytes > 0 ? snapshot.Duplicates.EstimatedReclaimableBytes * 100d / snapshot.LogicalSizeBytes : 0;
        _overviewReclaimCard.SetValue(FormatBytes(snapshot.Duplicates.EstimatedReclaimableBytes), snapshot.LogicalSizeBytes > 0 ? $"{reclaimablePercent:0.#}% of library" : "No reclaimable estimate");
        _overviewReclaimCard.ProgressPercent = reclaimablePercent;
        _overviewExactLink.Text = $"Exact duplicates      {snapshot.Duplicates.ExactGroups:N0} sets · {snapshot.Duplicates.ExactAffectedFiles:N0} files";
        _overviewVisualLink.Text = $"Visual duplicates     {snapshot.Duplicates.VisualGroups:N0} groups · {snapshot.Duplicates.VisualAffectedFiles:N0} files";
        _overviewFamilyLink.Text = $"Duplicate families    {snapshot.Duplicates.FamilyGroups:N0} groups · {snapshot.Duplicates.FamilyAffectedFiles:N0} files";
        // These are navigation links, so they remain available even when analysis
        // has not run or produced no matches. The destination owns its empty state.
        SetOverviewLinkState(_overviewExactLink, true);
        SetOverviewLinkState(_overviewVisualLink, true);
        SetOverviewLinkState(_overviewFamilyLink, true);
        OverviewReviewProgress review = CalculateReviewProgress(snapshot.Duplicates.ReviewedGroups, snapshot.Duplicates.UnreviewedGroups);
        _overviewDuplicateProgress.Text = review.Total == 0 ? "Review duplicates  ·  No duplicate groups are available yet" : $"Review duplicates  ·  Reviewed {review.Reviewed:N0} / {review.Total:N0} ({review.Percent:0.#}%)";
        _overviewDuplicateProgressBar.Percent = review.Percent;
        SetOverviewLinkState(_overviewDuplicateProgress, review.Total > 0);
        RenderOverviewHealth(snapshot);
        _overviewHealthSummary.Cursor = warning ? Cursors.Hand : Cursors.Default;
        _overviewHealthSummary.TabStop = warning;
        _overviewLocationIds = snapshot.Locations.Select(x => x.LocationId).ToArray();
        _overviewLocationChart.SetData(snapshot.Locations.Select(x => new OverviewBarItem(Path.GetFileName(x.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), x.LogicalSizeBytes, $"{x.FileCount:N0} files · {FormatBytes(x.LogicalSizeBytes)}", x.FileCount == 0 ? x.Path : $"{x.Path} · {FormatBytes(x.LogicalSizeBytes / x.FileCount)} average file")).ToArray());
        RenderOverviewComposition(snapshot);
        IReadOnlyList<LibraryOverviewScanHistoryEntry> recentHistory = history.Where(x => x.CompletedUtc >= DateTime.UtcNow.AddDays(-30)).OrderBy(x => x.CompletedUtc).ToArray();
        OverviewPoint[] growthPoints = recentHistory.Select(x => new OverviewPoint(x.CompletedUtc, x.IndexedVideoCount, x.LogicalSizeBytes)).ToArray();
        _overviewGrowthChart.SetData(growthPoints);
        _overviewGrowthChart.Visible = recentHistory.Count > 1; _overviewGrowthEmpty.Visible = recentHistory.Count <= 1;
        _overviewGrowthEmpty.Text = history.Count == 0 ? "History will appear after completed scans." : recentHistory.Count == 0 ? "No completed scans in the last 30 days." : "Run another completed scan to show the 30-day trend.";
        _overviewGrowthSummary.Text = BuildGrowthSummary(growthPoints);
        _overviewToolTip.SetToolTip(_overviewGrowthChart, BuildGrowthSummary(growthPoints));
        RenderOverviewInsights(snapshot.Insights);
    }

    private static LinkLabel OverviewLinkLabel() => new()
    {
        AutoEllipsis = true, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 2), TabStop = false, Enabled = true,
        LinkColor = LibraryAnalyzerAccentColor, ActiveLinkColor = LibraryAnalyzerAccentColor,
        VisitedLinkColor = LibraryAnalyzerAccentColor, Cursor = Cursors.Default
    };

    private static void SetOverviewLinkState(Control control, bool enabled)
    {
        control.Enabled = enabled;
        control.Cursor = enabled ? Cursors.Hand : Cursors.Default;
        control.TabStop = enabled;
        if (control is LinkLabel link)
        {
            link.LinkColor = enabled ? LibraryAnalyzerAccentColor : SystemColors.GrayText;
            link.ActiveLinkColor = enabled ? Color.FromArgb(0, 60, 110) : SystemColors.GrayText;
            link.TabStop = enabled;
        }
    }

    private void MakeOverviewInteractive(Control control, Action action)
    {
        SetOverviewLinkState(control, true);
        control.TabStop = control is Panel || control is LinkLabel;
        control.Click += (_, _) => action();
        control.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.SuppressKeyPress = true;
                action();
            }
        };
        foreach (Control child in control.Controls)
            MakeOverviewInteractive(child, action);
    }

    private void NavigateToOverviewTab(int tabIndex)
    {
        if (IsDisposed || tabIndex < 0 || tabIndex >= _tabs.TabPages.Count) return;
        _tabs.SelectedIndex = tabIndex;
    }

    private void NavigateToOverviewLocation(int itemIndex)
    {
        if (itemIndex < 0 || itemIndex >= _overviewLocationIds.Length) return;
        _preferredLocationSelectionId = _overviewLocationIds[itemIndex];
        if (_tabs.SelectedIndex != 1) _tabs.SelectedIndex = 1;
        else _ = RefreshLocationsAsync();
    }

    private void QueueOverviewRefresh()
    {
        if (IsDisposed || Disposing) return;
        if (_loadingOverview)
        {
            _overviewRefreshQueued = true;
            return;
        }
        _overviewRefreshDebounceTimer.Stop();
        _overviewRefreshDebounceTimer.Start();
    }

    private void RenderOverviewComposition(LibraryOverviewSnapshot? snapshot)
    {
        if (snapshot == null) return;
        IReadOnlyList<LibraryOverviewDistribution> data = _overviewCompositionSelector.SelectedIndex switch { 1 => snapshot.VideoCodecDistribution, 2 => snapshot.ContainerDistribution, _ => snapshot.ResolutionDistribution };
        _overviewCompositionChart.SetData(data.Select(x => new OverviewBarItem(x.Label, x.FileCount, $"{x.FileCount:N0} files · {FormatBytes(x.LogicalSizeBytes)}")).ToArray());
    }

    private static string BuildGrowthSummary(IReadOnlyList<OverviewPoint> points)
    {
        if (points.Count == 0) return "No completed scan history is available.";
        OverviewPoint current = points[^1];
        if (points.Count == 1) return $"{current.When.ToLocalTime():g}: {current.Files:N0} files · {FormatBytes(current.Bytes)}";
        OverviewPoint previous = points[^2];
        return $"{current.When.ToLocalTime():g}: {current.Files:N0} files · {FormatBytes(current.Bytes)} | {current.Files - previous.Files:+#;-#;0} files · {FormatBytes(Math.Abs(current.Bytes - previous.Bytes))} {(current.Bytes >= previous.Bytes ? "added" : "removed")} since previous scan";
    }

    private void RenderOverviewInsights(LibraryOverviewInsight insight)
    {
        _overviewInsights.SuspendLayout();
        foreach (Control child in _overviewInsights.Controls.Cast<Control>().ToArray()) child.Dispose();
        _overviewInsights.Controls.Clear(); _overviewInsights.RowStyles.Clear(); _overviewInsights.RowCount = 5; _overviewInsights.ColumnCount = 2;
        _overviewInsights.AutoScroll = true;
        _overviewInsights.ColumnStyles.Clear(); _overviewInsights.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34)); _overviewInsights.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        AddInsightMetric(0, "Largest file", insight.LargestFileBytes.HasValue ? FormatBytes((long)insight.LargestFileBytes.Value) : "Unavailable", insight.LargestFileBytes.HasValue ? insight.LargestFilePath : null);
        AddInsightMetric(1, "Average size", insight.AverageFileBytes.HasValue ? FormatBytes((long)insight.AverageFileBytes.Value) : "Unavailable", null);
        AddInsightMetric(2, "Longest video", insight.LongestDurationSeconds.HasValue ? FormatDuration(insight.LongestDurationSeconds.Value) : "Unavailable", insight.LongestDurationSeconds.HasValue ? insight.LongestDurationPath : null);
        AddInsightMetric(3, "Average bitrate", insight.AverageBitrate.HasValue ? $"{insight.AverageBitrate.Value / 1_000_000d:0.##} Mbps" : "Unavailable", null);
        AddInsightMetric(4, "Typical media", string.IsNullOrWhiteSpace(insight.MostCommonCodec) ? "Unavailable" : insight.MostCommonCodec, string.IsNullOrWhiteSpace(insight.MostCommonResolution) ? null : insight.MostCommonResolution);
        _overviewInsights.ResumeLayout(true);
    }

    private void AddInsightMetric(int row, string label, string value, string? secondary)
    {
        bool hasSecondary = !string.IsNullOrWhiteSpace(secondary);
        int lineHeight = TextRenderer.MeasureText("Ag", Font).Height;
        int secondaryHeight = hasSecondary ? TextRenderer.MeasureText("Ag", Font).Height : 0;
        _overviewInsights.RowStyles.Add(new RowStyle(SizeType.Absolute, lineHeight + secondaryHeight + 3));
        _overviewInsights.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = DashboardVisuals.MutedText, Padding = new Padding(2, 0, 4, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, AutoSize = false }, 0, row);
        var valuePanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, ColumnCount = 1, RowCount = hasSecondary ? 2 : 1, Padding = new Padding(2, 1, 2, 2), Margin = Padding.Empty };
        valuePanel.RowStyles.Clear();
        valuePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, lineHeight));
        if (hasSecondary) valuePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, secondaryHeight));
        var primary = new Label { Text = value, Dock = DockStyle.Fill, AutoSize = false, Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
        valuePanel.Controls.Add(primary, 0, 0);
        if (hasSecondary)
        {
            var secondaryLabel = new Label { Text = Path.GetFileName(secondary!), Dock = DockStyle.Fill, AutoSize = false, ForeColor = DashboardVisuals.MutedText, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
            _overviewToolTip.SetToolTip(secondaryLabel, secondary); valuePanel.Controls.Add(secondaryLabel, 0, 1); _overviewToolTip.SetToolTip(primary, $"{value} · {secondary}");
        }
        _overviewInsights.Controls.Add(valuePanel, 1, row);
    }

    private void RenderOverviewHealth(LibraryOverviewSnapshot snapshot)
    {
        _overviewHealthSummary.SuspendLayout();
        foreach (Control child in _overviewHealthSummary.Controls.Cast<Control>().ToArray())
            child.Dispose();
        _overviewHealthSummary.Controls.Clear(); _overviewHealthSummary.RowStyles.Clear(); _overviewHealthSummary.RowCount = 3;
        _overviewHealthSummary.RowStyles.Add(new RowStyle(SizeType.Percent, 33)); _overviewHealthSummary.RowStyles.Add(new RowStyle(SizeType.Percent, 33)); _overviewHealthSummary.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        bool locationWarning = snapshot.UnavailableLocationCount > 0;
        bool enrichmentWarning = snapshot.PendingEnrichmentCount > 0 || snapshot.Health.MetadataFailures > 0;
        bool catalogWarning = snapshot.Health.MissingFiles > 0 || snapshot.Health.IntegrityWarnings > 0 || snapshot.Health.IntegrityFailures > 0;
        AddHealthRow(0, "Locations", locationWarning ? $"⚠ {snapshot.AvailableLocationCount:N0} available · {snapshot.UnavailableLocationCount:N0} unavailable" : $"✓ {snapshot.AvailableLocationCount:N0} available");
        AddHealthRow(1, "Enrichment", enrichmentWarning ? $"⚠ {snapshot.PendingEnrichmentCount:N0} pending · {snapshot.Health.MetadataFailures:N0} failed" : "✓ No pending work");
        AddHealthRow(2, "Catalog", catalogWarning ? $"⚠ {snapshot.Health.MissingFiles:N0} missing · {snapshot.Health.IntegrityWarnings + snapshot.Health.IntegrityFailures:N0} integrity warnings" : "✓ No integrity warnings");
        _overviewHealthSummary.ResumeLayout();
    }

    private void AddHealthRow(int row, string label, string value)
    {
        var line = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(4, 1, 4, 1) };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34)); line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        line.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = DashboardVisuals.MutedText, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        bool warning = value.Contains('⚠'); var status = new Label { Text = value, Dock = DockStyle.Fill, ForeColor = warning ? DashboardVisuals.Warning : Color.SeaGreen, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = $"{label}: {value}" };
        line.Controls.Add(status, 1, 0); _overviewHealthSummary.Controls.Add(line, 0, row);
    }

    internal static OverviewReviewProgress CalculateReviewProgress(long reviewed, long unreviewed) => new(Math.Max(0, reviewed), Math.Max(0, reviewed) + Math.Max(0, unreviewed));
    internal static OverviewStatusPresentation GetOverviewStatusPresentation(bool scanning, bool enriching, bool warning) => scanning ? new("◷ Scanning", OverviewStatusKind.Info) : enriching ? new("• Enriching", OverviewStatusKind.Info) : warning ? new("⚠ Attention needed", OverviewStatusKind.Warning) : new("✓ Library healthy", OverviewStatusKind.Success);
}

internal sealed class OverviewMetricCard : Panel
{
    private readonly string _title;
    private readonly Label _label;
    private readonly Label _value;
    private readonly Label _secondary;
    private double _progressPercent;

    public OverviewMetricCard(string title)
    {
        _title = title; AccessibleName = title; AccessibleRole = AccessibleRole.Grouping; TabStop = false;
        Margin = new Padding(3); Padding = new Padding(10, 8, 10, 8); BackColor = SystemColors.Window;
        _label = new Label { Text = title, Dock = DockStyle.Top, Height = 19, ForeColor = DashboardVisuals.MutedText, Font = DashboardVisuals.LabelFont, AutoEllipsis = true };
        _value = new Label { Dock = DockStyle.Top, Height = 34, ForeColor = DashboardVisuals.PrimaryText, Font = DashboardVisuals.MetricFont, AutoEllipsis = true };
        _secondary = new Label { Dock = DockStyle.Fill, ForeColor = DashboardVisuals.MutedText, Padding = new Padding(0, 2, 0, 0), AutoEllipsis = true };
        Controls.Add(_secondary); Controls.Add(_value); Controls.Add(_label);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public double ProgressPercent { get => _progressPercent; set { _progressPercent = Math.Clamp(value, 0, 100); Invalidate(); } }
    public void SetValue(string value, string secondary) { _value.Text = value; _secondary.Text = secondary; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        DashboardVisuals.DrawCard(e.Graphics, ClientRectangle, _title == "Reclaimable space" ? DashboardVisuals.Warning : DashboardVisuals.Accent);
        if (_title == "Reclaimable space") DashboardVisuals.DrawProgress(e.Graphics, new Rectangle(10, Height - 12, Math.Max(0, Width - 20), 4), _progressPercent, DashboardVisuals.Warning);
    }
}

internal readonly record struct OverviewBarItem(string Label, long Value, string Detail, string? ExtraDetail = null);
internal readonly record struct OverviewPoint(DateTime When, long Files, long Bytes);
internal enum OverviewGrowthMetric { Files, Size }
internal readonly record struct OverviewReviewProgress(long Reviewed, long Total)
{
    public double Percent => Total <= 0 ? 0 : Reviewed * 100d / Total;
}
internal readonly record struct OverviewStatusPresentation(string Text, OverviewStatusKind Kind);

internal sealed class OverviewBarChart : Control
{
    private IReadOnlyList<OverviewBarItem> _items = Array.Empty<OverviewBarItem>();
    private readonly ToolTip _toolTip = new();
    private int _keyboardIndex = -1;
    private int _hoverIndex = -1;
    private bool _isInteractive;
    public event EventHandler<int>? ItemClicked;
    public bool IsInteractive { get => _isInteractive; set { _isInteractive = value; TabStop = value; if (!value) _keyboardIndex = 0; } }
    public OverviewBarChart() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = SystemColors.Window; AccessibleRole = AccessibleRole.Graphic; }
    public void SetData(IReadOnlyList<OverviewBarItem> items)
    {
        _items = items ?? Array.Empty<OverviewBarItem>();
        _keyboardIndex = Math.Clamp(_keyboardIndex, 0, Math.Max(0, _items.Count - 1));
        Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int index = HitTest(e.Location); if (_hoverIndex != index) { _hoverIndex = index; Invalidate(); } Cursor = IsInteractive && index >= 0 ? Cursors.Hand : Cursors.Default; _toolTip.SetToolTip(this, index >= 0 ? BuildToolTip(_items[index]) : ""); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverIndex = -1; Cursor = Cursors.Default; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && IsInteractive) { int index = HitTest(e.Location); if (index >= 0) ItemClicked?.Invoke(this, index); } }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (!IsInteractive || _items.Count == 0) return; if (e.KeyCode == Keys.Up) { _keyboardIndex = Math.Max(0, _keyboardIndex - 1); e.SuppressKeyPress = true; Invalidate(); } else if (e.KeyCode == Keys.Down) { _keyboardIndex = Math.Min(_items.Count - 1, _keyboardIndex + 1); e.SuppressKeyPress = true; Invalidate(); } else if (e.KeyCode is Keys.Enter or Keys.Space) { ItemClicked?.Invoke(this, _keyboardIndex); e.SuppressKeyPress = true; } }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (_keyboardIndex < 0 && _items.Count > 0) _keyboardIndex = 0; Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    private int HitTest(Point location) { if (_items.Count == 0) return -1; int rowHeight = GetRowHeight(); int index = (location.Y - 4) / rowHeight; return index >= 0 && index < Math.Min(_items.Count, Math.Max(1, (Height - 4) / rowHeight)) ? index : -1; }
    protected override void Dispose(bool disposing) { if (disposing) _toolTip.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_items.Count == 0) { DashboardVisuals.DrawEmptyState(e.Graphics, ClientRectangle, "No catalog data yet"); return; }
        long max = Math.Max(1, _items.Max(x => x.Value)); int rowHeight = GetRowHeight(); int visible = Math.Min(_items.Count, Math.Max(1, (Height - 4) / rowHeight));
        for (int i = 0; i < visible; i++)
        {
            OverviewBarItem item = _items[i]; int y = i * rowHeight + 4; bool selected = Focused && _keyboardIndex == i; bool hovered = _hoverIndex == i;
            if (selected || hovered) DashboardVisuals.DrawRoundedFill(e.Graphics, new Rectangle(1, y - 2, Width - 2, rowHeight - 2), selected ? DashboardVisuals.FocusBackground : DashboardVisuals.HoverBackground, 5);
            int labelWidth = Math.Max(78, Width * 34 / 100); int barLeft = labelWidth + 6; int barWidth = Math.Max(20, Width - barLeft - 8);
            using var title = new SolidBrush(DashboardVisuals.PrimaryText); using var detail = new SolidBrush(DashboardVisuals.MutedText);
            e.Graphics.DrawString(item.Label, DashboardVisuals.LabelFont, title, new RectangleF(5, y + 1, labelWidth - 8, Font.Height + 2), DashboardVisuals.NearStringFormat);
            e.Graphics.DrawString(item.Detail, DashboardVisuals.DetailFont, detail, new RectangleF(5, y + Font.Height + 2, labelWidth - 8, Font.Height + 2), DashboardVisuals.NearStringFormat);
            Rectangle track = new(barLeft, y + 6, barWidth, 9); DashboardVisuals.DrawProgress(e.Graphics, track, item.Value * 100d / max, DashboardVisuals.ChartColor(i));
            string percent = max == 0 ? "0%" : $"{item.Value * 100d / max:0.#}%";
            e.Graphics.DrawString(percent, DashboardVisuals.DetailFont, detail, new RectangleF(barLeft, y + 18, barWidth, Font.Height), DashboardVisuals.FarStringFormat);
        }
    }
    private int GetRowHeight() => Math.Max(31, Math.Min(48, Math.Max(31, (Height - 4) / Math.Max(1, _items.Count))));
    private static string BuildToolTip(OverviewBarItem item) => string.IsNullOrWhiteSpace(item.ExtraDetail) ? $"{item.Label}: {item.Detail}" : $"{item.Label}: {item.Detail} · {item.ExtraDetail}";
}

internal sealed class OverviewSparkline : Control
{
    private IReadOnlyList<OverviewPoint> _points = Array.Empty<OverviewPoint>();
    private readonly ToolTip _toolTip = new();
    private int _hoverIndex = -1;
    private OverviewGrowthMetric _metric;
    public OverviewGrowthMetric Metric { get => _metric; set { if (_metric == value) return; _metric = value; Invalidate(); } }
    public OverviewSparkline() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = SystemColors.Window; AccessibleRole = AccessibleRole.Graphic; }
    public void SetData(IReadOnlyList<OverviewPoint> points) { _points = points ?? Array.Empty<OverviewPoint>(); Invalidate(); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int index = HitTest(e.Location); if (_hoverIndex != index) { _hoverIndex = index; Invalidate(); } _toolTip.SetToolTip(this, index >= 0 ? TooltipFor(_points[index]) : ""); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverIndex = -1; Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _toolTip.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (_points.Count < 2) return; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF plot = new(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - 32));
        long[] values = _points.Select(p => Metric == OverviewGrowthMetric.Size ? p.Bytes : p.Files).ToArray(); long min = values.Min(), max = Math.Max(values.Max(), min + 1); float range = max - min;
        for (int i = 1; i < 4; i++) { float y = plot.Top + plot.Height * i / 4f; using var grid = new Pen(DashboardVisuals.Grid, 1); e.Graphics.DrawLine(grid, plot.Left, y, plot.Right, y); }
        PointF At(int i) => new(plot.Left + plot.Width * i / (_points.Count - 1f), plot.Bottom - plot.Height * (values[i] - min) / range);
        PointF[] points = Enumerable.Range(0, _points.Count).Select(At).ToArray();
        using var fill = new SolidBrush(Color.FromArgb(38, DashboardVisuals.Accent)); using var area = new GraphicsPath(); area.AddLines(points); area.AddLine(points[^1].X, points[^1].Y, points[^1].X, plot.Bottom); area.AddLine(points[^1].X, plot.Bottom, points[0].X, plot.Bottom); area.CloseFigure(); e.Graphics.FillPath(fill, area);
        using var pen = new Pen(DashboardVisuals.Accent, 2f); e.Graphics.DrawLines(pen, points);
        for (int i = 0; i < points.Length; i++) if (i == 0 || i == points.Length - 1 || i == _hoverIndex) { using var marker = new SolidBrush(i == _hoverIndex ? DashboardVisuals.Warning : DashboardVisuals.Accent); e.Graphics.FillEllipse(marker, points[i].X - 3, points[i].Y - 3, 6, 6); }
        using var text = new SolidBrush(DashboardVisuals.MutedText); string low = Metric == OverviewGrowthMetric.Size ? FormatOverviewBytes(min) : min.ToString("N0"); string high = Metric == OverviewGrowthMetric.Size ? FormatOverviewBytes(max) : max.ToString("N0");
        e.Graphics.DrawString(low, DashboardVisuals.DetailFont, text, new PointF(plot.Left, plot.Bottom + 3)); e.Graphics.DrawString(high, DashboardVisuals.DetailFont, text, new PointF(plot.Right - 54, plot.Top));
        if (plot.Width >= 220) { e.Graphics.DrawString(_points[0].When.ToLocalTime().ToString("M/d"), DashboardVisuals.DetailFont, text, new PointF(plot.Left, plot.Bottom + 3)); string end = _points[^1].When.ToLocalTime().ToString("M/d"); e.Graphics.DrawString(end, DashboardVisuals.DetailFont, text, new PointF(plot.Right - e.Graphics.MeasureString(end, DashboardVisuals.DetailFont).Width, plot.Bottom + 3)); }
    }
    private int HitTest(Point point) { if (_points.Count < 2 || Width <= 16) return -1; int index = (int)Math.Round((point.X - 8) * (_points.Count - 1d) / Math.Max(1, Width - 16)); return Math.Clamp(index, 0, _points.Count - 1); }
    private string TooltipFor(OverviewPoint point) { int index = Enumerable.Range(0, _points.Count).FirstOrDefault(i => _points[i].Equals(point)); OverviewPoint? previous = index > 0 ? _points[index - 1] : null; string delta = previous.HasValue ? $" | {point.Files - previous.Value.Files:+#;-#;0} files · {FormatOverviewBytes(Math.Abs(point.Bytes - previous.Value.Bytes))} {(point.Bytes >= previous.Value.Bytes ? "added" : "removed")}" : ""; return $"{point.When.ToLocalTime():g}: {point.Files:N0} files · {FormatOverviewBytes(point.Bytes)}{delta}"; }
    private static string FormatOverviewBytes(long bytes) { double value = Math.Max(0, bytes); string[] units = { "B", "KB", "MB", "GB", "TB" }; int unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:0.#} {units[unit]}"; }
}
