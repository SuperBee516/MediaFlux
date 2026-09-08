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
        _overviewHealthSummary.Click += (_, _) => NavigateToOverviewTab(7);
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
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(4) };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 33)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 33)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 34)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.Controls.Add(_overviewExactLink, 0, 0); panel.Controls.Add(_overviewVisualLink, 0, 1); panel.Controls.Add(_overviewFamilyLink, 0, 2); panel.Controls.Add(_overviewDuplicateProgress, 0, 3);
        panel.Controls.Add(new Label { Text = "Reclaimable space follows existing keeper and review decisions.", AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(8, 2, 8, 2) }, 0, 4);
        return panel;
    }

    private Control BuildHealthPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        panel.Controls.Add(_overviewHealthSummary, 0, 0);
        panel.Controls.Add(new Label { Text = "Detailed recovery and decision history remain in Health & Recovery.", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(8, 2, 8, 2), AutoEllipsis = true }, 0, 1);
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
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        panel.Controls.Add(_overviewGrowthChart); panel.Controls.Add(_overviewGrowthEmpty); return panel;
    }

    private static Control CreateOverviewPanel(string title, Control content)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(1), Margin = new Padding(3), BackColor = SystemColors.Window };
        var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 27, Font = new Font("Segoe UI Semibold", 9F), ForeColor = LibraryAnalyzerAccentColor, Padding = new Padding(8, 5, 4, 2), AutoEllipsis = true };
        panel.Controls.Add(content); panel.Controls.Add(heading); return panel;
    }

    private async Task RefreshOverviewAsync()
    {
        if (_loadingOverview || IsDisposed) return;
        _loadingOverview = true;
        try
        {
            CancellationToken token = _overviewRefreshCancellation.Token;
            Task<LibraryOverviewSnapshot> snapshotTask = _runtime.Overview.LoadAsync(LibraryEnrichmentCoordinator.CurrentMetadataVersion, token);
            Task<IReadOnlyList<LibraryOverviewScanHistoryEntry>> historyTask = _runtime.Overview.LoadHistoryAsync(cancellationToken: token);
            await Task.WhenAll(snapshotTask, historyTask);
            LibraryOverviewSnapshot snapshot = await snapshotTask;
            IReadOnlyList<LibraryOverviewScanHistoryEntry> history = await historyTask;
            if (IsDisposed) return;
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
        string state = scanning ? "Scanning" : enriching ? "Enriching" : warning ? "Attention needed" : "Healthy · Idle";
        _overviewHealthState.Text = state; _overviewHealthState.ForeColor = scanning ? LibraryAnalyzerAccentColor : warning ? Color.DarkOrange : Color.SeaGreen;
        _overviewHeaderSummary.Text = snapshot.LastCompletedScanUtc.HasValue ? $"{snapshot.IndexedVideoCount:N0} indexed videos · Last completed scan {snapshot.LastCompletedScanUtc.Value.ToLocalTime():g}" : "No completed scan yet · Add a location and scan to build the catalog";
        _overviewVideosCard.SetValue(snapshot.IndexedVideoCount.ToString("N0"), $"{snapshot.Locations.Count(x => x.FileCount > 0):N0} locations with files");
        _overviewSizeCard.SetValue(FormatBytes(snapshot.LogicalSizeBytes), snapshot.IndexedVideoCount == 0 ? "No indexed media" : $"{FormatBytes(snapshot.LogicalSizeBytes / Math.Max(1, snapshot.IndexedVideoCount))} average");
        long duplicateSets = snapshot.Duplicates.ExactGroups + snapshot.Duplicates.VisualGroups + snapshot.Duplicates.FamilyGroups;
        long affectedFiles = snapshot.Duplicates.ExactAffectedFiles + snapshot.Duplicates.VisualAffectedFiles + snapshot.Duplicates.FamilyAffectedFiles;
        _overviewDuplicatesCard.SetValue(duplicateSets.ToString("N0"), $"{affectedFiles:N0} category totals · overlap possible");
        _overviewReclaimCard.SetValue(FormatBytes(snapshot.Duplicates.EstimatedReclaimableBytes), snapshot.LogicalSizeBytes > 0 ? $"{snapshot.Duplicates.EstimatedReclaimableBytes * 100d / snapshot.LogicalSizeBytes:0.#}% of library" : "No reclaimable estimate");
        _overviewExactLink.Text = $"Exact duplicates  ·  {snapshot.Duplicates.ExactGroups:N0} sets · {snapshot.Duplicates.ExactAffectedFiles:N0} affected files";
        _overviewVisualLink.Text = $"Visual duplicates  ·  {snapshot.Duplicates.VisualGroups:N0} groups · {snapshot.Duplicates.VisualAffectedFiles:N0} affected files";
        _overviewFamilyLink.Text = $"Duplicate families  ·  {snapshot.Duplicates.FamilyGroups:N0} groups · {snapshot.Duplicates.FamilyAffectedFiles:N0} affected files";
        SetOverviewLinkState(_overviewExactLink, snapshot.Duplicates.ExactGroups > 0);
        SetOverviewLinkState(_overviewVisualLink, snapshot.Duplicates.VisualGroups > 0);
        SetOverviewLinkState(_overviewFamilyLink, snapshot.Duplicates.FamilyGroups > 0);
        long reviewed = snapshot.Duplicates.ReviewedGroups, totalReviewed = reviewed + snapshot.Duplicates.UnreviewedGroups;
        _overviewDuplicateProgress.Text = totalReviewed == 0 ? "Review Duplicates  ·  No duplicate groups are available yet" : $"Review Duplicates  ·  Reviewed {reviewed:N0} of {totalReviewed:N0} category sets ({reviewed * 100d / totalReviewed:0.#}%)";
        SetOverviewLinkState(_overviewDuplicateProgress, totalReviewed > 0);
        _overviewHealthSummary.Text = $"Locations  {snapshot.ConfiguredLocationCount:N0} configured · {snapshot.AvailableLocationCount:N0} available · {snapshot.UnavailableLocationCount:N0} unavailable\r\nEnrichment  {snapshot.PendingEnrichmentCount:N0} pending · {snapshot.Health.MetadataFailures:N0} failed\r\nCatalog attention  {snapshot.Health.MissingFiles:N0} missing · {snapshot.Health.IntegrityWarnings + snapshot.Health.IntegrityFailures:N0} integrity warnings/errors";
        SetOverviewLinkState(_overviewHealthSummary, warning);
        _overviewHealthSummary.TabStop = warning;
        _overviewLocationIds = snapshot.Locations.Select(x => x.LocationId).ToArray();
        _overviewLocationChart.SetData(snapshot.Locations.Select(x => new OverviewBarItem(Path.GetFileName(x.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), x.LogicalSizeBytes, $"{x.FileCount:N0} files · {FormatBytes(x.LogicalSizeBytes)}")).ToArray());
        RenderOverviewComposition(snapshot);
        IReadOnlyList<LibraryOverviewScanHistoryEntry> recentHistory = history.Where(x => x.CompletedUtc >= DateTime.UtcNow.AddDays(-30)).OrderBy(x => x.CompletedUtc).ToArray();
        _overviewGrowthChart.SetData(recentHistory.Select(x => new OverviewPoint(x.CompletedUtc, x.IndexedVideoCount, x.LogicalSizeBytes)).ToArray());
        _overviewGrowthChart.Visible = recentHistory.Count > 1; _overviewGrowthEmpty.Visible = recentHistory.Count <= 1;
        _overviewGrowthEmpty.Text = history.Count == 0 ? "History will appear after completed scans." : recentHistory.Count == 0 ? "No completed scans in the last 30 days." : "Run another completed scan to show the 30-day trend.";
        RenderOverviewInsights(snapshot.Insights);
    }

    private static LinkLabel OverviewLinkLabel() => new()
    {
        AutoEllipsis = true, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 2), TabStop = false, Enabled = false,
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

    private void RenderOverviewInsights(LibraryOverviewInsight insight)
    {
        _overviewInsights.SuspendLayout(); _overviewInsights.Controls.Clear(); _overviewInsights.RowStyles.Clear(); _overviewInsights.RowCount = 0;
        string[][] rows = { new[] { "Largest file", insight.LargestFileBytes.HasValue ? $"{FormatBytes(insight.LargestFileBytes.Value)}\r\n{Path.GetFileName(insight.LargestFilePath)}" : "Unavailable", }, new[] { "Average size", insight.AverageFileBytes.HasValue ? FormatBytes((long)insight.AverageFileBytes.Value) : "Unavailable" }, new[] { "Longest video", insight.LongestDurationSeconds.HasValue ? $"{FormatDuration(insight.LongestDurationSeconds.Value)}\r\n{Path.GetFileName(insight.LongestDurationPath)}" : "Unavailable" }, new[] { "Average bitrate", insight.AverageBitrate.HasValue ? $"{insight.AverageBitrate.Value / 1_000_000d:0.##} Mbps" : "Unavailable" }, new[] { "Typical media", string.IsNullOrWhiteSpace(insight.MostCommonCodec) ? "Unavailable" : $"{insight.MostCommonCodec} · {insight.MostCommonResolution}" } };
        for (int i = 0; i < rows.Length; i++) { _overviewInsights.RowStyles.Add(new RowStyle(SizeType.Percent, 25)); _overviewInsights.Controls.Add(new Label { Text = rows[i][0], Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(2, 4, 2, 2), AutoEllipsis = true }, 0, i); _overviewInsights.Controls.Add(new Label { Text = rows[i][1], Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(2, 4, 2, 2), AutoEllipsis = true }, 1, i); }
        _overviewInsights.RowCount = rows.Length; _overviewInsights.ResumeLayout();
    }
}

internal sealed class OverviewMetricCard : Panel
{
    private readonly Label _value = new() { Dock = DockStyle.Top, Height = 35, Font = new Font("Segoe UI Semibold", 18F), Padding = new Padding(10, 5, 8, 0), AutoEllipsis = true };
    private readonly Label _secondary = new() { Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(10, 0, 8, 5), AutoEllipsis = true };
    public OverviewMetricCard(string title) { AccessibleName = title; BorderStyle = BorderStyle.FixedSingle; Margin = new Padding(3); Padding = new Padding(1); BackColor = SystemColors.Window; var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 22, ForeColor = Color.FromArgb(70,70,70), Padding = new Padding(9, 3, 4, 0), AutoEllipsis = true }; Controls.Add(_secondary); Controls.Add(_value); Controls.Add(heading); }
    public void SetValue(string value, string secondary) { _value.Text = value; _secondary.Text = secondary; }
}

internal readonly record struct OverviewBarItem(string Label, long Value, string Detail);
internal readonly record struct OverviewPoint(DateTime When, long Files, long Bytes);

internal sealed class OverviewBarChart : Control
{
    private IReadOnlyList<OverviewBarItem> _items = Array.Empty<OverviewBarItem>();
    private readonly ToolTip _toolTip = new();
    private int _keyboardIndex;
    private bool _isInteractive;
    public event EventHandler<int>? ItemClicked;
    public bool IsInteractive { get => _isInteractive; set { _isInteractive = value; TabStop = value; if (!value) _keyboardIndex = 0; } }
    public OverviewBarChart() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = SystemColors.Window; }
    public void SetData(IReadOnlyList<OverviewBarItem> items)
    {
        _items = items ?? Array.Empty<OverviewBarItem>();
        _keyboardIndex = Math.Clamp(_keyboardIndex, 0, Math.Max(0, _items.Count - 1));
        Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int index = HitTest(e.Location); Cursor = IsInteractive && index >= 0 ? Cursors.Hand : Cursors.Default; _toolTip.SetToolTip(this, index >= 0 ? $"{_items[index].Label}: {_items[index].Detail}" : ""); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Cursor = Cursors.Default; }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && IsInteractive) { int index = HitTest(e.Location); if (index >= 0) ItemClicked?.Invoke(this, index); } }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (!IsInteractive || _items.Count == 0) return; if (e.KeyCode == Keys.Up) { _keyboardIndex = Math.Max(0, _keyboardIndex - 1); e.SuppressKeyPress = true; } else if (e.KeyCode == Keys.Down) { _keyboardIndex = Math.Min(_items.Count - 1, _keyboardIndex + 1); e.SuppressKeyPress = true; } else if (e.KeyCode is Keys.Enter or Keys.Space) { ItemClicked?.Invoke(this, _keyboardIndex); e.SuppressKeyPress = true; } }
    private int HitTest(Point location) { if (_items.Count == 0) return -1; int rowHeight = Math.Max(22, Height / Math.Max(1, _items.Count)); int index = (location.Y - 3) / rowHeight; return index >= 0 && index < Math.Min(_items.Count, Math.Max(1, Height / rowHeight)) ? index : -1; }
    protected override void Dispose(bool disposing) { if (disposing) _toolTip.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; if (_items.Count == 0) { using var empty = new SolidBrush(SystemColors.GrayText); e.Graphics.DrawString("No catalog data yet", Font, empty, new PointF(8, Math.Max(4, Height / 2f - Font.Height / 2f))); return; } long max = Math.Max(1, _items.Max(x => x.Value)); int rowHeight = Math.Max(22, Height / Math.Max(1, _items.Count)); int visible = Math.Min(_items.Count, Math.Max(1, Height / rowHeight)); for (int i = 0; i < visible; i++) { OverviewBarItem item = _items[i]; int y = i * rowHeight + 3; using var text = new SolidBrush(ForeColor); e.Graphics.DrawString(item.Label, Font, text, new RectangleF(4, y, Math.Max(65, Width / 3f), rowHeight - 3)); int left = Math.Max(70, Width / 3); int width = Math.Max(2, Width - left - 8); using var bar = new SolidBrush(Color.FromArgb(80, 145, 195)); e.Graphics.FillRectangle(bar, left, y + 3, (int)(width * (double)item.Value / max), Math.Max(8, rowHeight - 10)); e.Graphics.DrawString(item.Detail, Font, text, new RectangleF(left + 5, y + 3, Math.Max(0, width - 5), rowHeight - 5)); } }
}

internal sealed class OverviewSparkline : Control
{
    private IReadOnlyList<OverviewPoint> _points = Array.Empty<OverviewPoint>();
    public OverviewSparkline() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = SystemColors.Window; }
    public void SetData(IReadOnlyList<OverviewPoint> points) { _points = points ?? Array.Empty<OverviewPoint>(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); if (_points.Count < 2) return; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; long minFiles = _points.Min(x => x.Files), maxFiles = Math.Max(_points.Max(x => x.Files), minFiles + 1); long minBytes = _points.Min(x => x.Bytes), maxBytes = Math.Max(_points.Max(x => x.Bytes), minBytes + 1); float left = 8, top = 8, width = Math.Max(1, Width - 16), height = Math.Max(1, Height - 30); PointF At(int i, long value, long min, long max) => new(left + width * i / (float)(_points.Count - 1), top + height - height * (value - min) / (float)(max - min)); PointF[] files = _points.Select((p, i) => At(i, p.Files, minFiles, maxFiles)).ToArray(); PointF[] bytes = _points.Select((p, i) => At(i, p.Bytes, minBytes, maxBytes)).ToArray(); using var filePen = new Pen(Color.FromArgb(0, 92, 160), 2f); using var sizePen = new Pen(Color.FromArgb(46, 125, 80), 2f); e.Graphics.DrawLines(filePen, files); e.Graphics.DrawLines(sizePen, bytes); using var text = new SolidBrush(SystemColors.GrayText); e.Graphics.DrawString($"Files {minFiles:N0}–{maxFiles:N0}   Size {FormatOverviewBytes(minBytes)}–{FormatOverviewBytes(maxBytes)}", Font, text, new PointF(8, Height - Font.Height - 2)); }
    private static string FormatOverviewBytes(long bytes) { double value = Math.Max(0, bytes); string[] units = { "B", "KB", "MB", "GB", "TB" }; int unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:0.#} {units[unit]}"; }
}
