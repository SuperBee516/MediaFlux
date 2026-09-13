using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerOverviewUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-OverviewUi", Guid.NewGuid().ToString("N"));
    public LibraryAnalyzerOverviewUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void OverviewDashboardHasResponsivePanelsAndUsefulEmptyStates()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "overview.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                using var form = new LibraryAnalyzerForm(runtime);
                form.Show();
                TabControl tabs = Field<TabControl>(form, "_tabs");
                TabPage overview = tabs.TabPages.Cast<TabPage>().Single(x => x.Text == "Overview");
                Assert.True(overview.AutoScroll);
                Assert.Contains(overview.Controls.OfType<TableLayoutPanel>(), x => x.RowCount >= 4);
                Task refresh = (Task)(form.GetType().GetMethod("RefreshOverviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, null) ?? throw new MissingMethodException());
                Pump(refresh);
                form.Size = new Size(1100, 700); Application.DoEvents(); AssertOverviewGeometry(form, overview);
                form.Size = new Size(1360, 840); Application.DoEvents(); AssertOverviewGeometry(form, overview);
                form.Size = new Size(1800, 1100); Application.DoEvents(); AssertOverviewGeometry(form, overview);
                ComboBox selector = Field<ComboBox>(form, "_overviewCompositionSelector");
                Assert.Equal(new[] { "Resolution", "Video codec", "Container" }, selector.Items.Cast<string>());
                ComboBox growthSelector = Field<ComboBox>(form, "_overviewGrowthMetricSelector");
                Assert.Equal(new[] { "Files", "Size" }, growthSelector.Items.Cast<string>());
                growthSelector.SelectedIndex = 1;
                Assert.Equal(OverviewGrowthMetric.Size, Field<OverviewSparkline>(form, "_overviewGrowthChart").Metric);
                Assert.Equal(AccessibleRole.Grouping, Field<OverviewMetricCard>(form, "_overviewReclaimCard").AccessibleRole);
                Assert.Equal("✓ Library healthy", Field<Label>(form, "_overviewHealthState").Text);
                OverviewReviewProgress progress = LibraryAnalyzerForm.CalculateReviewProgress(3, 7);
                Assert.Equal(10, progress.Total);
                Assert.Equal(30, progress.Percent);
                OverviewStatusPresentation warningStatus = LibraryAnalyzerForm.GetOverviewStatusPresentation(false, false, true);
                Assert.Equal("⚠ Attention needed", warningStatus.Text);
                Assert.Equal(OverviewStatusKind.Warning, warningStatus.Kind);
                AssertInsightsLayout(Field<TableLayoutPanel>(form, "_overviewInsights"));
                Assert.Equal(10, Field<TableLayoutPanel>(form, "_overviewInsights").Controls.Count);
                form.GetType().GetMethod("RenderOverviewInsights", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { new LibraryOverviewInsight(null, "", null, null, null, "", null, null, "", "") });
                Assert.Equal(10, Field<TableLayoutPanel>(form, "_overviewInsights").Controls.Count);
                AssertInsightsLayout(Field<TableLayoutPanel>(form, "_overviewInsights"));
                var populatedInsight = new LibraryOverviewInsight(1, @"P:\\Media\\Star Wars The Clone Wars - Season 01 - Episode 20 - A very long title.mkv", 433_290_000, 12_345_678, 2, @"P:\\Media\\Speed Racer - 01x20 - The Long Episode Name.mp4", 1_508, 1_240_000, "h264", "1920×1080");
                form.GetType().GetMethod("RenderOverviewInsights", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { populatedInsight });
                AssertInsightsLayout(Field<TableLayoutPanel>(form, "_overviewInsights"));
                TableLayoutPanel largestValue = (TableLayoutPanel)Field<TableLayoutPanel>(form, "_overviewInsights").GetControlFromPosition(1, 0)!;
                Assert.Equal("413.22 MB", largestValue.GetControlFromPosition(0, 0)!.Text);
                Assert.Contains("Star Wars", largestValue.GetControlFromPosition(0, 1)!.Text);
                Assert.Equal("No completed scan yet · Add a location and scan to build the catalog", Field<Label>(form, "_overviewHeaderSummary").Text);
                Assert.Contains("History will appear", Field<Label>(form, "_overviewGrowthEmpty").Text);
                form.GetType().GetMethod("SetActivity", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { "Scanning: 3 indexed", "Current: movie.mkv", true, 0L, 0L, false });
                Assert.Contains("Live: Scanning", Field<Label>(form, "_overviewLiveStatus").Text);
                form.GetType().GetMethod("SetActivity", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { "Ready", "", false, 0L, 0L, false });
                Assert.Equal("Live: Idle", Field<Label>(form, "_overviewLiveStatus").Text);
                form.Close(); Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Overview UI smoke test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void OverviewActionsNavigateOnlyToExistingAnalyzerDestinations()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "actions.db"), Path.Combine(_root, "backups2"), Path.Combine(_root, "recovery2"));
                catalog.Initialize();
                LibraryLocationRecord location = catalog.UpsertLocation(new(Path.Combine(_root, "media"), Availability: LibraryLocationAvailability.Available));
                LibraryScanHandle scan = catalog.BeginScan(location.Id);
                catalog.UpsertInventoryBatch(scan, new[] { new LibraryInventoryEntry(Path.Combine(_root, "media", "movie.mkv"), "movie.mkv", 100, DateTime.UtcNow) });
                catalog.CompleteScan(scan, new(LibraryScanStatus.Completed, 1, 0, 1, 0, 0, 0));
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                using var form = new LibraryAnalyzerForm(runtime); form.Show();
                TabControl tabs = Field<TabControl>(form, "_tabs");
                Pump((Task)(form.GetType().GetMethod("RefreshOverviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, null) ?? throw new MissingMethodException()));
                Button statistics = AllControls(form).OfType<Button>().Single(x => x.Text == "Open Statistics");
                statistics.PerformClick();
                Assert.Equal("Statistics", tabs.SelectedTab?.Text);
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(x => x.Text == "Overview"); Application.DoEvents();
                OverviewBarChart chart = Field<OverviewBarChart>(form, "_overviewLocationChart");
                typeof(OverviewBarChart).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chart, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 6, 6, 0) });
                Assert.Equal("Locations", tabs.SelectedTab?.Text);
                form.Close(); Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Overview action UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class EmptyProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "test";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken token) => Task.FromResult(new MediaProbeResult { Success = false });
    }
    private sealed class EmptyVisual : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "test";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken token) => Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }
    private static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) ?? throw new MissingFieldException(name));
    private static IEnumerable<Control> AllControls(Control root) => new[] { root }.Concat(root.Controls.Cast<Control>().SelectMany(AllControls));
    private static void AssertOverviewGeometry(Form form, TabPage overview)
    {
        TableLayoutPanel root = overview.Controls.OfType<TableLayoutPanel>().Single();
        Control header = root.GetControlFromPosition(0, 0)!;
        Control cards = root.GetControlFromPosition(0, 1)!;
        TableLayoutPanel panels = (TableLayoutPanel)root.GetControlFromPosition(0, 2)!;
        Control actions = root.GetControlFromPosition(0, 3)!;
        Assert.True(header.Bottom <= cards.Top, $"Header overlaps KPI row at {form.Size}: {header.Bounds} / {cards.Bounds}");
        Assert.True(cards.Bottom <= panels.Top, $"KPI row overlaps dashboard panels at {form.Size}: {cards.Bounds} / {panels.Bounds}");
        Assert.True(actions.Bottom <= root.ClientSize.Height, $"Actions extend outside root at {form.Size}: {actions.Bounds} / {root.ClientSize}");
        Assert.All(actions.Controls.Cast<Control>(), control => Assert.True(control.Bottom <= actions.ClientSize.Height, $"Action is clipped at {form.Size}: {control.Bounds} / {actions.ClientSize}"));
        Assert.All(header.Controls.Cast<Control>(), control => Assert.True(control.Bottom <= header.ClientSize.Height, $"Header content is clipped at {form.Size}: {control.Bounds} / {header.ClientSize}"));
        Assert.All(cards.Controls.Cast<Control>(), control => Assert.True(control.Bottom <= cards.ClientSize.Height, $"KPI card is clipped at {form.Size}: {control.Bounds} / {cards.ClientSize}"));
        Assert.All(cards.Controls.Cast<Control>().SelectMany(control => control.Controls.Cast<Control>()), control => Assert.True(control.Bottom <= control.Parent!.ClientSize.Height, $"KPI content is clipped at {form.Size}: {control.Bounds} / {control.Parent.ClientSize}"));
        foreach (Control section in panels.Controls.Cast<Control>())
        {
            Assert.Equal(DockStyle.Fill, section.Dock);
            Assert.True(section.Width >= panels.GetColumnWidths()[panels.GetColumn(section)] * 0.85, $"Dashboard panel collapsed horizontally at {form.Size}: {section.Bounds} / {panels.ClientSize}");
            Assert.True(section.Height >= panels.GetRowHeights()[panels.GetRow(section)] * 0.85, $"Dashboard panel collapsed vertically at {form.Size}: {section.Bounds} / {panels.ClientSize}");
        }
        Assert.All(root.Controls.Cast<Control>(), control => Assert.True(control.Right <= root.ClientSize.Width && control.Bottom <= root.ClientSize.Height, $"Dashboard row is clipped at {form.Size}: {control.Bounds} / {root.ClientSize}"));
    }
    private static void AssertInsightsLayout(TableLayoutPanel insights)
    {
        Assert.Equal(5, insights.RowCount);
        for (int row = 0; row < insights.RowCount; row++)
        {
            Control label = insights.GetControlFromPosition(0, row)!;
            Control value = insights.GetControlFromPosition(1, row)!;
            Rectangle contentBounds = insights.DisplayRectangle;
            Assert.True(contentBounds.Contains(label.Bounds) && contentBounds.Contains(value.Bounds), $"Insight row {row} is outside its content bounds.");
            Assert.True(label.Right <= value.Left, $"Insight label/value columns overlap at row {row}: {label.Bounds} / {value.Bounds}");
            Assert.True(value.Top >= label.Top && value.Bottom <= insights.ClientSize.Height, $"Insight value row {row} has invalid bounds: {value.Bounds}");
            if (value is TableLayoutPanel valuePanel)
            {
                Control primary = valuePanel.GetControlFromPosition(0, 0)!;
                Assert.True(valuePanel.ClientRectangle.Contains(primary.Bounds), $"Insight primary value is clipped at row {row}: {primary.Bounds} / {valuePanel.ClientSize}");
                if (valuePanel.RowCount > 1)
                {
                    Control secondary = valuePanel.GetControlFromPosition(0, 1)!;
                    Assert.True(primary.Height > 0 && secondary.Height > 0, $"Insight secondary row collapsed at row {row}: {primary.Bounds} / {secondary.Bounds}");
                    Assert.True(secondary.Top >= primary.Bottom - 1, $"Insight secondary overlaps primary at row {row}: {primary.Bounds} / {secondary.Bounds}");
                    Assert.True(valuePanel.ClientRectangle.Contains(secondary.Bounds), $"Insight secondary exceeds value bounds at row {row}: {secondary.Bounds} / {valuePanel.ClientSize}");
                }
                else Assert.True(primary.Height > 0, $"Insight primary row collapsed at row {row}: {primary.Bounds}");
            }
        }
    }
    private static void Pump(Task task) { DateTime end = DateTime.UtcNow.AddSeconds(20); while (!task.IsCompleted) { if (DateTime.UtcNow >= end) throw new TimeoutException(); Application.DoEvents(); Thread.Sleep(10); } task.GetAwaiter().GetResult(); }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
