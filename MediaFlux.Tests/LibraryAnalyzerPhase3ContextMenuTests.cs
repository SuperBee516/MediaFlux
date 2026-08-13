using System.Reflection;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerPhase3ContextMenuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-Phase3Menus", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerPhase3ContextMenuTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RemainingActionableGridsExposeScopedSharedActions()
    {
        RunSta(() =>
        {
            using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "menus.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
            catalog.Initialize();
            using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new Probe(), new Visual());
            using var form = new LibraryAnalyzerForm(runtime,
                new LibraryAnalyzerForm.LibraryAnalyzerCleanupOptions(AllowRecycleBin: true, AllowQuarantine: true, QuarantineFolder: _root, AllowPermanentDelete: true));

            AssertNames(form, "_healthMenu", "Execute", "Integrity", "Restore", "Play", "CopyDetails", "MediaDetails", "Locate");
            AssertNames(form, "_recommendationsMenu", "OpenResults", "Reclamation");
            AssertNames(form, "_policiesMenu", "Play", "Folder", "CopyFullPath", "Encode", "All", "Locate");
            AssertNames(form, "_reclamationMenu", "Protect", "LocateFiles", "LocateExact", "LocateVisual", "LocateFamily");
            AssertNames(form, "_integrityMenu", "Quick", "Full", "Retry", "RemoveRecycle", "RemoveQuarantine", "RemovePermanent");
            AssertNames(form, "_maintenanceMenu", "Run", "Toggle", "Edit", "Refresh");
            AssertNames(form, "_filesMenu", "CopyFilename", "CopyFullPath", "CopyFolderPath", "CopyDetails", "MediaDetails", "RemoveRecycle");
        });
    }

    private static void AssertNames(LibraryAnalyzerForm form, string fieldName, params string[] expected)
    {
        var menu = (ContextMenuStrip)(form.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form)
            ?? throw new MissingFieldException(fieldName));
        HashSet<string> names = Items(menu.Items).Select(item => item.Name ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (string name in expected) Assert.Contains(name, names);
    }

    private static IEnumerable<ToolStripItem> Items(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            yield return item;
            if (item is ToolStripDropDownItem dropDown)
                foreach (ToolStripItem child in Items(dropDown.DropDownItems)) yield return child;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class Probe : ILibraryMetadataProbe
    {
        public string ToolVersion => "test";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(new MediaProbeResult { Success = false });
    }

    private sealed class Visual : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "test";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
