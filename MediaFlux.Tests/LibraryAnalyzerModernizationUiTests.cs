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
