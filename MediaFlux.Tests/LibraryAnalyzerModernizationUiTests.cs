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
public sealed class LibraryAnalyzerModernizationUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-AnalyzerModernization", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerModernizationUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LocationsFilesAndStatisticsUseSharedFoundationAndRemainUsableAtMinimumSize()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "analyzer.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                using var form = new LibraryAnalyzerForm(runtime);
                form.Show();
                form.Size = new Size(1100, 700);
                Application.DoEvents();

                TabControl tabs = Field<TabControl>(form, "_tabs");
                Assert.Equal(new[] { "Overview", "Locations", "Files", "Statistics" }, tabs.TabPages.Cast<TabPage>().Take(4).Select(page => page.Text));
                Assert.Contains(AllControls(form), control => control is AnalyzerMetricCard card && card.AccessibleName == "Sources");
                Assert.Contains(AllControls(form), control => control is AnalyzerMetricCard card && card.AccessibleName == "Indexed files");
                Assert.Contains(AllControls(form), control => control is AnalyzerSectionPanel);

                Pump(InvokeTask(form, "RefreshLocationsAsync"));
                Assert.Contains(Field<AnalyzerMetricCard>(form, "_locationsSourcesCard").Controls.OfType<Label>(), label => label.Text == "0");

                Pump(InvokeTask(form, "RefreshFilesAsync"));
                Assert.Contains("No indexed files", Field<Label>(form, "_filesSummary").Text, StringComparison.OrdinalIgnoreCase);
                Assert.False(AllControls(form).OfType<Button>().Single(button => button.Text == "Metadata").Enabled);
                AnalyzerNavigationRail rail = Field<AnalyzerNavigationRail>(form, "_navigationRail");
                Assert.All(AllControls(rail).OfType<AnalyzerNavigationItem>(), item => Assert.True(item.Enabled));
                Assert.False(Field<Button>(form, "_reanalysisExact").Enabled);
                Assert.False(Field<Button>(form, "_reanalysisVisual").Enabled);

                Pump(InvokeTask(form, "RefreshStatisticsAsync"));
                Assert.Contains(AllControls(form), control => control.Name == "StatisticsCodecGrid");
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Analyzer modernization UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void NavigationRailPreservesTabOrderAndSynchronizesSelection()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            LibraryAnalyzerForm? form = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "navigation.db"), Path.Combine(_root, "navigation-backups"), Path.Combine(_root, "navigation-recovery"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                form = new LibraryAnalyzerForm(runtime);
                form.Show();
                form.Size = new Size(1100, 700);
                Application.DoEvents();

                AnalyzerNavigationRail rail = Field<AnalyzerNavigationRail>(form, "_navigationRail");
                TabControl tabs = Field<TabControl>(form, "_tabs");
                Assert.Equal(13, rail.Entries.Count);
                Assert.Equal(new[] { "Overview", "Locations", "Files", "Statistics", "Exact", "Visual", "Families", "Health", "Recommendations", "Policies", "Storage", "Integrity", "Maintenance" }, rail.Entries.Select(entry => entry.Label));
                Assert.Equal(Enumerable.Range(0, 13), rail.Entries.Select(entry => entry.PageIndex));
                Assert.Equal(13, tabs.TabPages.Count);
                Assert.Equal(1, tabs.ItemSize.Height);
                Assert.Equal(TabSizeMode.Fixed, tabs.SizeMode);
                Assert.All(tabs.TabPages.Cast<TabPage>(), page => Assert.Equal(AccessibleRole.Pane, page.AccessibleRole));
                Assert.InRange(rail.Width, 165, 190);
                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    form.Size = size;
                    Application.DoEvents();
                    Assert.True(rail.Bounds.Right <= tabs.Bounds.Left);
                    Assert.True(tabs.Width > 0 && tabs.Height > 0);
                    Assert.False(rail.VerticalScrollVisible, $"Navigation should fit without scrolling at {size}: rail={rail.Bounds}, tabs={tabs.Bounds}, content={rail.NavigationContentHeight}, viewport={rail.NavigationViewportHeight}.");
                }

                using var constrainedRail = new AnalyzerNavigationRail { Size = new Size(180, 200) };
                constrainedRail.SetEntries(rail.Entries);
                constrainedRail.CreateControl();
                constrainedRail.PerformLayout();
                Assert.True(constrainedRail.VerticalScrollVisible);

                AnalyzerNavigationItem visual = AllControls(rail).OfType<AnalyzerNavigationItem>().Single(item => item.Text == "Visual");
                rail.Activate(5);
                Application.DoEvents();
                Assert.Equal(5, tabs.SelectedIndex);
                Assert.Equal(5, rail.SelectedIndex);
                Assert.True(visual.Selected);

                tabs.SelectedIndex = 7;
                Application.DoEvents();
                Assert.Equal(7, rail.SelectedIndex);
                Assert.True(AllControls(rail).OfType<AnalyzerNavigationItem>().Single(item => item.Text == "Health").Selected);
                Assert.Equal(AccessibleRole.PageTab, visual.AccessibleRole);
                Assert.True(AllControls(rail).OfType<AnalyzerNavigationItem>().All(item => item.TabStop));
            }
            catch (Exception ex) { failure = ex; }
            finally { WinFormsTestLifecycle.CloseAndDispose(form); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Analyzer navigation UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static Task InvokeTask(object form, string method) =>
        (Task)(form.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, null)
            ?? throw new MissingMethodException(method));

    private static T Field<T>(object value, string name) =>
        (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new MissingFieldException(name));

    private static IEnumerable<Control> AllControls(Control root) =>
        new[] { root }.Concat(root.Controls.Cast<Control>().SelectMany(AllControls));

    private static void Pump(Task task)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException();
            Application.DoEvents();
            Thread.Sleep(10);
        }
        task.GetAwaiter().GetResult();
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
