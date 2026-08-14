using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class StorageReclamationUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-ReclamationUiTests", Guid.NewGuid().ToString("N"));
    public StorageReclamationUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DedicatedPlannerTabBuildsAndPersistsEmptyAdvisoryPlanWithoutActions()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "ui.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                string planPath = Path.Combine(_root, "saved-plan.json");
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                    PolicyStore: new LibraryPolicyStore(Path.Combine(_root, "policies.json")),
                    PolicyCapabilities: new LibraryPolicyCapabilitySnapshot(),
                    ReclamationPlanStore: new StorageReclamationPlanStore(planPath)));
                form.Show();
                TabControl tabs = Field<TabControl>(form, "_tabs");
                TabPage tab = tabs.TabPages.Cast<TabPage>().Single(item => item.Text == "Storage Optimization");
                tabs.SelectedTab = tab;
                NumericUpDown target = Field<NumericUpDown>(form, "_reclamationTarget");
                target.Value = 0;
                Task build = InvokeTask(form, "BuildStorageReclamationPlanAsync");
                Pump(build);
                DataGridView grid = Field<DataGridView>(form, "_reclamationGrid");
                Assert.DoesNotContain(grid.Rows.Cast<DataGridViewRow>(), row => !row.IsNewRow);
                Assert.True(grid.MultiSelect);
                Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),column=>column.Name=="PostSize");
                DataGridView summary=Field<DataGridView>(form,"_reclamationOpportunitySummary");
                Assert.Contains(summary.Columns.Cast<DataGridViewColumn>(),column=>column.Name=="Current");
                Assert.Contains(summary.Columns.Cast<DataGridViewColumn>(),column=>column.Name=="Savings");
                ComboBox policy=Field<ComboBox>(form,"_reclamationPolicy");Assert.Contains("General Archive",policy.SelectedItem?.ToString());
                ContextMenuStrip menu=Field<ContextMenuStrip>(form,"_reclamationMenu");string[] names=menu.Items.Cast<ToolStripItem>().Select(item=>item.Name??"").ToArray();
                Assert.Contains("MediaDetails",names);Assert.Contains("Encode",names);Assert.Contains("PresetEncode",names);Assert.Contains("LocateExact",names);Assert.Contains("LocateVisual",names);Assert.Contains("LocateFamily",names);Assert.DoesNotContain(names,name=>name.Contains("Cleanup",StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(tab.Controls.OfType<Control>().SelectMany(Descendants).OfType<Button>(),button=>button.Text.Contains("cleanup",StringComparison.OrdinalIgnoreCase));
                Assert.True(File.Exists(planPath));
                StorageReclamationPlan saved = Assert.IsType<StorageReclamationPlan>(new StorageReclamationPlanStore(planPath).Load());
                Assert.Equal(0, saved.RequestedReclaimBytes);
                Assert.Equal(0, saved.ReadyReclaimBytes);
                Assert.Contains(saved.Warnings, warning => warning.Contains("advisory", StringComparison.OrdinalIgnoreCase));
                form.Close(); Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Storage Optimization UI smoke test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class EmptyProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "unused";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(new MediaProbeResult { Success = false });
    }
    private sealed class EmptyVisual : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "unused";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) ?? throw new MissingFieldException(name));
    private static IEnumerable<Control> Descendants(Control root){foreach(Control child in root.Controls){yield return child;foreach(Control nested in Descendants(child))yield return nested;}}
    private static Task InvokeTask(object instance, string name) => (Task)(instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, null) ?? throw new MissingMethodException(name));
    private static void Pump(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); Application.DoEvents(); Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
    }
}
