using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerIntelligenceUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-AnalyzerIntelligence", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerIntelligenceUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void HealthRecommendationsAndPoliciesLoadThroughSharedDashboardLayout()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "intelligence.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                    PolicyStore: new LibraryPolicyStore(Path.Combine(_root, "policies.json"))));
                form.Show();

                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    form.Size = size;
                    Application.DoEvents();
                    foreach (string tabName in new[] { "Health & Recovery", "Cleanup Recommendations", "Library Policies" })
                    {
                        TabPage tab = Field<TabControl>(form, "_tabs").TabPages.Cast<TabPage>().Single(page => page.Text == tabName);
                        Assert.True(tab.Width > 0 && tab.Height > 0, $"{tabName} should have a usable client area at {size}.");
                    }
                }

                Pump(InvokeTask(form, "RefreshHealthAsync"));
                Pump(InvokeTask(form, "RefreshRecommendationsAsync"));
                Pump(InvokeTask(form, "RefreshStorageOptimizationAsync"));

                Assert.Contains(AllControls(form), control => control is AnalyzerStatusBadge);
                Assert.Contains(AllControls(form), control => control is AnalyzerSectionPanel panel && panel.AccessibleName == "Cleanup opportunities");
                Assert.Contains(AllControls(form), control => control is AnalyzerSectionPanel panel && panel.AccessibleName == "Policy evaluation results");
                Assert.Contains(Field<AnalyzerMetricCard>(form, "_healthOverallMetric").Controls.OfType<Label>(), label => label.Text is "Healthy" or "Attention" or "Problem");
                Assert.Contains(Field<AnalyzerMetricCard>(form, "_recommendationStateMetric").Controls.OfType<Label>(), label => label.Text is "Clear" or "Review");
                Assert.Contains(Field<AnalyzerMetricCard>(form, "_policyConfiguredMetric").Controls.OfType<Label>(), label => int.TryParse(label.Text, out int count) && count > 0);
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Analyzer intelligence UI test timed out.");
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
