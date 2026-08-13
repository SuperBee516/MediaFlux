using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryIntegrityUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-IntegrityUiTests", Guid.NewGuid().ToString("N"));
    public LibraryIntegrityUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DedicatedIntegrityTabShowsPagedEmptyStateWithoutStartingWork()
    {
        if (!OperatingSystem.IsWindows()) return; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "ui.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery")); catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                using var form = new LibraryAnalyzerForm(runtime); form.Show();
                TabControl tabs = Field<TabControl>(form, "_tabs"); tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Media Integrity");
                Task refresh = InvokeTask(form, "RefreshIntegrityAsync"); Pump(refresh);
                Assert.DoesNotContain(Field<DataGridView>(form, "_integrityGrid").Rows.Cast<DataGridViewRow>(), row => !row.IsNewRow);
                Assert.Contains("never checked", Field<Label>(form, "_integritySummary").Text, StringComparison.OrdinalIgnoreCase);
                form.Close(); Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Media Integrity UI smoke test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    private sealed class EmptyProbe : ILibraryMetadataProbe { public string ToolVersion => "unused"; public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken token) => Task.FromResult(new MediaProbeResult { Success = false }); }
    private sealed class EmptyVisual : ILibraryVisualFingerprintExtractor { public string ToolVersion => "unused"; public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken token) => Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>()); }
    private static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) ?? throw new MissingFieldException(name));
    private static Task InvokeTask(object value, string name) => (Task)(value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(value, null) ?? throw new MissingMethodException(name));
    private static void Pump(Task task) { DateTime end = DateTime.UtcNow.AddSeconds(20); while (!task.IsCompleted) { if (DateTime.UtcNow >= end) throw new TimeoutException(); Application.DoEvents(); Thread.Sleep(10); } task.GetAwaiter().GetResult(); }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
