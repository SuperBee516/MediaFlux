using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerInteractionTests
{
    [Fact]
    public void FileActionStateRequiresResolvablePathsAndReflectsProtectionAcrossSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-ActionState", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string firstPath = Path.Combine(root, "first.mkv");
            string secondPath = Path.Combine(root, "second.mkv");
            File.WriteAllText(firstPath, "first");
            File.WriteAllText(secondPath, "second");
            TestFile first = new(firstPath, true);
            TestFile second = new(secondPath, true);

            LibraryFileActionState one = LibraryFileActionState.Evaluate(new[] { first }, item => item.Path, item => item.Protected);
            Assert.True(one.CanPlay);
            Assert.True(one.CanOpenFolders);
            Assert.True(one.AllProtected);

            LibraryFileActionState multiple = LibraryFileActionState.Evaluate(new[] { first, second }, item => item.Path, item => item.Protected);
            Assert.False(multiple.CanPlay);
            Assert.True(multiple.CanOpenFolders);
            Assert.True(multiple.CanCopyPaths);
            Assert.True(multiple.AllProtected);

            LibraryFileActionState unavailable = LibraryFileActionState.Evaluate(
                new[] { first with { Path = Path.Combine(root, "missing", "missing.mkv"), Protected = false } },
                item => item.Path, item => item.Protected);
            Assert.False(unavailable.CanPlay);
            Assert.False(unavailable.CanOpenFolders);
            Assert.False(unavailable.AllProtected);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RightClickSelectionPreservesExistingMultiSelectionAndTargetsUnselectedRow()
    {
        RunSta(() =>
        {
            using var grid = Grid();
            grid.Columns.Add("Value", "Value");
            grid.Rows.Add("one"); grid.Rows.Add("two"); grid.Rows.Add("three");
            grid.Rows[0].Tag = 1L; grid.Rows[1].Tag = 2L; grid.Rows[2].Tag = 3L;
            grid.Rows[0].Selected = true;
            grid.Rows[1].Selected = true;

            LibraryAnalyzerGridInteraction.UpdateRightClickSelection(grid, 1, 0);
            Assert.Equal(new long[] { 1, 2 }, LibraryAnalyzerGridInteraction.SelectedItems<long>(grid));

            LibraryAnalyzerGridInteraction.UpdateRightClickSelection(grid, 2, 0);
            Assert.Equal(new long[] { 3 }, LibraryAnalyzerGridInteraction.SelectedItems<long>(grid));
        });
    }

    [Fact]
    public void GridLayoutControllerCapturesAndRestoresWidthOrderAndVisibility()
    {
        RunSta(() =>
        {
            var state = new LibraryAnalyzerUiState();
            using (var first = Grid())
            {
                first.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", Width = 180 });
                first.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", Width = 360 });
                var controller = new LibraryAnalyzerLayoutController(state);
                controller.RegisterGrid(first, "Files.FilesGrid");
                first.Columns["Name"].Width = 245;
                first.Columns["Path"].DisplayIndex = 0;
                first.Columns["Name"].Visible = false;
                controller.CaptureAll();
            }

            using var restored = Grid();
            restored.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", Width = 180 });
            restored.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", Width = 360 });
            new LibraryAnalyzerLayoutController(state).RegisterGrid(restored, "Files.FilesGrid");
            Assert.Equal(245, restored.Columns["Name"].Width);
            Assert.False(restored.Columns["Name"].Visible);
            Assert.Equal(0, restored.Columns["Path"].DisplayIndex);
        });
    }

    [Fact]
    public void SplitterLayoutControllerAppliesAndCapturesPersistedDistance()
    {
        RunSta(() =>
        {
            var state = new LibraryAnalyzerUiState
            {
                SplitterDistances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Exact duplicates.Split0"] = 280
                }
            };
            using var form = new Form { ClientSize = new Size(800, 600) };
            using var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 100,
                Panel2MinSize = 100
            };
            form.Controls.Add(split);
            form.Show();
            split.SplitterDistance = 220;
            var controller = new LibraryAnalyzerLayoutController(state);
            controller.RegisterSplitter(split, "Exact duplicates.Split0");
            controller.ApplySplitterLayouts();
            Assert.Equal(280, split.SplitterDistance);

            split.SplitterDistance = 315;
            controller.CaptureAll();
            Assert.Equal(315, state.SplitterDistances["exact duplicates.split0"]);
            form.Close();
        });
    }

    [Fact]
    public void FilesQueryReturnsExistingProtectionState()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-ProtectedFilesQuery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var catalog = new SqliteLibraryCatalog(Path.Combine(root, "library.db"), Path.Combine(root, "backups"), Path.Combine(root, "recovery")))
            {
                catalog.Initialize();
                LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
                LibraryScanHandle scan = catalog.BeginScan(location.Id);
                string path = Path.Combine(root, "movie.mkv");
                catalog.UpsertInventoryBatch(scan, new[]
                {
                    new LibraryInventoryEntry(path, "movie.mkv", 100, DateTime.UtcNow)
                });
                long fileId = catalog.GetFileByPath(path)!.Id;
                catalog.SetFileProtection(fileId, true, "test");

                LibraryFileViewRecord file = Assert.Single(catalog.QueryFiles(new LibraryFileQuery()).Files);
                Assert.True(file.IsProtected);
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ComparisonRoutingRequiresTwoDistinctPresentAvailableMembers()
    {
        VisualSimilarityMemberRecord first = VisualMember(1, "first.mkv", IndexedFileAvailability.Present);
        VisualSimilarityMemberRecord duplicate = first with { FullPath = "duplicate-row.mkv" };
        VisualSimilarityMemberRecord unavailable = VisualMember(2, "missing.mkv", IndexedFileAvailability.Unavailable);
        VisualSimilarityMemberRecord second = VisualMember(3, "second.mkv", IndexedFileAvailability.Present);

        VisualSimilarityMemberRecord[] result = LibraryAnalyzerForm.ResolveEligibleComparisonMembers(
            new[] { first, duplicate, unavailable, second },
            path => path is "first.mkv" or "second.mkv");

        Assert.Equal(new long[] { 1, 3 }, result.Select(member => member.FileId));
    }

    private static DataGridView Grid() => new()
    {
        AllowUserToAddRows = false,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    private static void RunSta(Action action)
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed record TestFile(string Path, bool Protected);

    private static VisualSimilarityMemberRecord VisualMember(long id, string path, IndexedFileAvailability availability) => new(
        1, id, path, "", 100, DateTime.UtcNow, availability, "h264", 1920, 1080, 5_000_000, 60,
        false, false, false, false, "Stereo");
}
