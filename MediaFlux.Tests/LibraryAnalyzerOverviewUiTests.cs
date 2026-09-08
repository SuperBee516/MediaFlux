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
        Control panels = root.GetControlFromPosition(0, 2)!;
        Control actions = root.GetControlFromPosition(0, 3)!;
        Assert.True(header.Bottom <= cards.Top, $"Header overlaps KPI row at {form.Size}: {header.Bounds} / {cards.Bounds}");
        Assert.True(cards.Bottom <= panels.Top, $"KPI row overlaps dashboard panels at {form.Size}: {cards.Bounds} / {panels.Bounds}");
        Assert.True(actions.Bottom <= root.ClientSize.Height, $"Actions extend outside root at {form.Size}: {actions.Bounds} / {root.ClientSize}");
        Assert.All(actions.Controls.Cast<Control>(), control => Assert.True(control.Bottom <= actions.ClientSize.Height, $"Action is clipped at {form.Size}: {control.Bounds} / {actions.ClientSize}"));
        Assert.All(header.Controls.Cast<Control>(), control => Assert.True(control.Bottom <= header.ClientSize.Height, $"Header content is clipped at {form.Size}: {control.Bounds} / {header.ClientSize}"));
        Assert.All(cards.Controls.Cast<Control>(), control => Assert.True(control.Bottom <= cards.ClientSize.Height, $"KPI card is clipped at {form.Size}: {control.Bounds} / {cards.ClientSize}"));
        Assert.All(cards.Controls.Cast<Control>().SelectMany(control => control.Controls.Cast<Control>()), control => Assert.True(control.Bottom <= control.Parent!.ClientSize.Height, $"KPI content is clipped at {form.Size}: {control.Bounds} / {control.Parent.ClientSize}"));
        Assert.All(root.Controls.Cast<Control>(), control => Assert.True(control.Right <= root.ClientSize.Width && control.Bottom <= root.ClientSize.Height, $"Dashboard row is clipped at {form.Size}: {control.Bounds} / {root.ClientSize}"));
    }
    private static void Pump(Task task) { DateTime end = DateTime.UtcNow.AddSeconds(20); while (!task.IsCompleted) { if (DateTime.UtcNow >= end) throw new TimeoutException(); Application.DoEvents(); Thread.Sleep(10); } task.GetAwaiter().GetResult(); }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
