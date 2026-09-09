using System.Drawing;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerFinalAuditUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-AnalyzerFinalAudit", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerFinalAuditUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AllAnalyzerTabsConstructAtSupportedSizesAndExposeSharedSemantics()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "audit.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                using var form = new LibraryAnalyzerForm(runtime);
                form.Show();
                var tabs = AllControls(form).OfType<TabControl>().Single(tab => tab.Name == "LibraryAnalyzerTabs");
                string[] expected =
                {
                    "Overview", "Locations", "Files", "Statistics", "Duplicates — Exact", "Duplicates — Visual",
                    "Duplicates — Families", "Health & Recovery", "Cleanup Recommendations", "Library Policies",
                    "Storage Optimization", "Media Integrity", "Scheduled Maintenance"
                };

                Assert.Equal(expected, tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
                Assert.All(tabs.TabPages.Cast<TabPage>(), page => Assert.False(string.IsNullOrWhiteSpace(page.AccessibleName)));

                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    form.Size = size;
                    Application.DoEvents();
                    Assert.All(tabs.TabPages.Cast<TabPage>(), page =>
                        Assert.True(page.Width > 0 && page.Height > 0, $"{page.Text} should have a client area at {size}."));
                    AssertMetricRowsHaveClearance(form, size);
                    AssertDuplicateFilterLayout(tabs, "Duplicates — Visual", size);
                }

                Assert.Equal(new Size(1100, 700), form.MinimumSize);
                Assert.Contains(tabs.TabPages.Cast<TabPage>(),
                    page => page.Controls.OfType<AnalyzerSectionPanel>().Any());
                Assert.Contains(tabs.TabPages.Cast<TabPage>(),
                    page => page.Controls.Cast<Control>().SelectMany(AllControls).OfType<FlowLayoutPanel>().Any(panel => panel.AccessibleRole == AccessibleRole.ToolBar));
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Analyzer final audit UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<Control> AllControls(Control root) =>
        new[] { root }.Concat(root.Controls.Cast<Control>().SelectMany(AllControls));

    private static void AssertDuplicateFilterLayout(TabControl tabs, string tabName, Size size)
    {
        TabPage tab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == tabName);
        tabs.SelectedTab = tab;
        Application.DoEvents();
        GroupBox filters = AllControls(tab).OfType<GroupBox>().Single(group => group.Text == "Filters");
        Rectangle filterBounds = filters.RectangleToScreen(filters.ClientRectangle);
        foreach (Control child in AllControls(filters).Where(control => control != filters))
        {
            Assert.True(
                filterBounds.Contains(child.RectangleToScreen(child.ClientRectangle)),
                $"{tabName} filter control {(string.IsNullOrWhiteSpace(child.Name) ? child.GetType().Name : child.Name)} is outside the Filters panel at {size}: filter={filterBounds}, child={child.Bounds}.");
        }

        DataGridView results = AllControls(tab).OfType<DataGridView>().First();
        Control controlArea = AllControls(tab).Single(control => control.Name == "VisualControlArea");
        SplitContainer split = AllControls(tab).OfType<SplitContainer>().Single(container => container.Orientation == Orientation.Horizontal);
        Rectangle resultsBounds = results.RectangleToScreen(results.ClientRectangle);
        Assert.True(
            resultsBounds.Top >= filterBounds.Bottom,
            $"{tabName} Filters intersects the results grid at {size}: filter={filterBounds}, results={resultsBounds}.");
        Assert.True(controlArea.Height < tab.ClientSize.Height / 2,
            $"{tabName} top controls consume too much vertical space at {size}: controls={controlArea.Height}, tab={tab.ClientSize.Height}.");
        Assert.True(split.Height >= tab.ClientSize.Height / 8,
            $"{tabName} review workspace has insufficient total allocation at {size}: split={split.Height}, tab={tab.ClientSize}.");
    }

    private static void AssertMetricRowsHaveClearance(Control root, Size size)
    {
        foreach (TableLayoutPanel row in AllControls(root)
            .OfType<TableLayoutPanel>()
            .Where(panel => panel.Controls.OfType<AnalyzerMetricCard>().Any()))
        {
            foreach (AnalyzerMetricCard card in row.Controls.OfType<AnalyzerMetricCard>())
            {
                if (row.Name == "AnalyzerMetricRow")
                {
                    Assert.True(
                        card.Bottom <= row.ClientSize.Height - row.Padding.Bottom,
                        $"{card.AccessibleName} is clipped within its metric row at {size}: card bottom {card.Bottom}, row height {row.ClientSize.Height}.");
                    Assert.True(
                        row.Height >= card.GetPreferredSize(new Size(0, 0)).Height + card.Margin.Vertical + row.Padding.Vertical,
                        $"{card.AccessibleName} does not have measured margin/padding clearance at {size}.");
                }
            }

            if (row.Parent is not null)
            {
                foreach (Control sibling in row.Parent.Controls.Cast<Control>().Where(control => control != row))
                {
                    Assert.False(
                        sibling.Top < row.Bottom && sibling.Bottom > row.Top,
                        $"{(string.IsNullOrWhiteSpace(sibling.Name) ? sibling.GetType().Name : sibling.Name)} overlaps the metric row at {size}; sibling={sibling.Bounds}, row={row.Bounds}, parent={row.Parent.GetType().Name}.");
                }
            }
        }
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
