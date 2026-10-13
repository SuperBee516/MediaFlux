using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerQueueIntegrationUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-QueueIntegration", Guid.NewGuid().ToString("N"));
    private string _stage = "starting";

    public LibraryAnalyzerQueueIntegrationUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExplicitFilesSelectionQueuesFromOrdinaryAdvancedAndSavedResultsWithoutChangingSearchState()
    {
        var submissions = new List<string[]>();
        var firstHandoff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        Func<IReadOnlyList<string>, Task> queue = paths =>
        {
            submissions.Add(paths.ToArray());
            callbackCount++;
            return callbackCount == 1 ? firstHandoff.Task : Task.CompletedTask;
        };

        WithForm(queue, (catalog, form, probe, store) =>
        {
            Dictionary<string, long> fileIds = SeedFiles(catalog);
            MarkMissing(catalog.DatabasePath, fileIds["ordinary-missing.mkv"]);

            TabControl tabs = Field<TabControl>(form, "_tabs");
            tabs.SelectedIndex = 2;
            Field<TextBox>(form, "_search").Text = "ordinary-";
            Pump(InvokeTask(form, "RefreshFilesAsync"));

            DataGridView grid = Field<DataGridView>(form, "_filesGrid");
            Button queueButton = Field<Button>(form, "_addSelectedToEncodeQueueButton");
            Assert.True(grid.MultiSelect);
            Assert.Equal(DataGridViewSelectionMode.FullRowSelect, grid.SelectionMode);
            Assert.False(queueButton.Enabled);
            Assert.Equal(4, grid.Rows.Count);
            AssertEncodeMenuEnabled(form, grid, expected: false);

            DataGridViewRow present = RowNamed(grid, "ordinary-present.mkv");
            DataGridViewRow unavailable = RowNamed(grid, "ordinary-missing.mkv");
            DataGridViewRow secondEligible = RowNamed(grid, "ordinary-unselected.mkv");
            DataGridViewRow unselectedMatch = RowNamed(grid, "ordinary-other.mkv");
            Assert.Equal(IndexedFileAvailability.Present, ((LibraryFileViewRecord)present.Tag!).Availability);
            Assert.Equal(IndexedFileAvailability.Missing, ((LibraryFileViewRecord)unavailable.Tag!).Availability);
            Assert.False(File.Exists(((LibraryFileViewRecord)present.Tag!).FullPath));

            SelectRows(grid, unavailable);
            Assert.False(queueButton.Enabled);
            AssertEncodeMenuEnabled(form, grid, expected: false);
            SelectRows(grid, present);
            Assert.True(queueButton.Enabled);
            AssertEncodeMenuEnabled(form, grid, expected: true);
            SelectRows(grid, present, secondEligible);
            Assert.Equal(2, grid.SelectedRows.Count);
            Assert.True(queueButton.Enabled);
            SelectRows(grid, present, secondEligible, unavailable);
            Assert.Equal(3, grid.SelectedRows.Count);
            Assert.True(queueButton.Enabled);
            LibraryAnalyzerGridInteraction.UpdateRightClickSelection(grid, unselectedMatch.Index, 0);
            Assert.Single(grid.SelectedRows);
            AssertEncodeMenuEnabled(form, grid, expected: true);
            SelectRows(grid, present, secondEligible, unavailable);
            LibraryAnalyzerGridInteraction.UpdateRightClickSelection(grid, secondEligible.Index, 0);
            Assert.Equal(3, grid.SelectedRows.Count);
            ContextMenuStrip menu = Field<ContextMenuStrip>(form, "_filesMenu");
            ToolStripMenuItem encode = menu.Items.Find("Encode", true).OfType<ToolStripMenuItem>().Single();
            menu.Show(grid, 0, 0);
            Application.DoEvents();
            Assert.Equal("Add Selected to Encode Queue", encode.Text);
            Assert.True(encode.Enabled);
            menu.Close();
            encode.PerformClick();
            Assert.Equal(1, callbackCount);
            Assert.False(queueButton.Enabled);
            Assert.Equal(new[]
            {
                ((LibraryFileViewRecord)present.Tag!).FullPath,
                ((LibraryFileViewRecord)secondEligible.Tag!).FullPath
            }, Assert.Single(submissions));
            Assert.DoesNotContain(((LibraryFileViewRecord)unselectedMatch.Tag!).FullPath, submissions[0]);
            queueButton.PerformClick();
            Assert.Equal(1, callbackCount);

            // A new search generation must not cancel or replace the captured queue handoff.
            Field<TextBox>(form, "_search").Clear();
            Invoke(form, "AddAdvancedCondition", "file.name");
            LibraryAnalyzerSearchConditionRow advancedRow = Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows").Single();
            advancedRow.SelectOperator(CatalogSearchOperator.Contains);
            advancedRow.ValueText.Text = "advanced-only";
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            DataGridViewRow advancedResult = Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("advanced-only.mkv", ((LibraryFileViewRecord)advancedResult.Tag!).FileName);
            SelectRows(grid, advancedResult);
            Assert.False(queueButton.Enabled, "A second handoff must remain blocked until the first completes.");

            firstHandoff.SetResult(true);
            PumpUntil(() => !Field<bool>(form, "_queueHandoffInProgress"));
            Assert.Contains("2 file(s) sent to Encode Queue", Field<Label>(form, "_queueHandoffStatusLabel").Text);
            Assert.Contains("1 unavailable", Field<Label>(form, "_queueHandoffStatusLabel").Text);
            Assert.True(queueButton.Enabled);
            Assert.Equal("advanced-only", advancedRow.ValueText.Text);

            long generationBeforeAdvancedQueue = RequestGeneration(form);
            queueButton.PerformClick();
            Assert.Equal(2, callbackCount);
            Assert.Equal(generationBeforeAdvancedQueue, RequestGeneration(form));
            Assert.Equal(((LibraryFileViewRecord)advancedResult.Tag!).FullPath, Assert.Single(submissions[1]));
            Assert.Equal("advanced-only", Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows").Single().ValueText.Text);

            // Loading a saved definition changes only the grid contents; the Files action uses the same row contract.
            ComboBox savedSearches = Field<ComboBox>(form, "_savedSearchPicker");
            savedSearches.SelectedItem = savedSearches.Items.Cast<object>().Single(item => item.ToString() == "Saved only");
            WaitForRequestIdle(form);
            Assert.Equal(200, grid.Rows.Count);
            DataGridViewRow firstPageSavedResult = grid.Rows[0];
            SelectRows(grid, firstPageSavedResult);
            Assert.True(queueButton.Enabled);

            Field<Button>(form, "_next").PerformClick();
            WaitForRequestIdle(form);
            Assert.Equal(1, Field<int>(form, "_page"));
            DataGridViewRow savedResult = Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("saved-only.mkv", ((LibraryFileViewRecord)savedResult.Tag!).FileName);
            Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.False(queueButton.Enabled, "Selection must not carry over from the previous page.");
            SelectRows(grid, savedResult);
            Assert.True(queueButton.Enabled);
            object selectedSavedSearch = savedSearches.SelectedItem!;
            LibraryFileQuery savedQuery = InvokeQuery(form);
            long generationBeforeSavedQueue = RequestGeneration(form);
            int pageBeforeSavedQueue = Field<int>(form, "_page");

            queueButton.PerformClick();

            Assert.Equal(3, callbackCount);
            Assert.Equal(((LibraryFileViewRecord)savedResult.Tag!).FullPath, Assert.Single(submissions[2]));
            Assert.Same(selectedSavedSearch, savedSearches.SelectedItem);
            Assert.Equal(pageBeforeSavedQueue, Field<int>(form, "_page"));
            Assert.Equal(generationBeforeSavedQueue, RequestGeneration(form));
            Assert.Equal(savedQuery.Search, InvokeQuery(form).Search);
            Assert.True(CatalogSearchDefinitionEquivalence.AreEquivalent(
                savedQuery.AdvancedSearch!, InvokeQuery(form).AdvancedSearch!));
            Assert.Equal(0, probe.Calls);
            Assert.All(submissions.SelectMany(paths => paths), path => Assert.False(File.Exists(path)));
            LibrarySavedSearch savedSearch = Assert.Single(store.Load());
            Assert.Equal("Saved only", savedSearch.Name);
        });
    }

    private void WithForm(
        Func<IReadOnlyList<string>, Task> queue,
        Action<SqliteLibraryCatalog, LibraryAnalyzerForm, CountingProbe, LibraryAdvancedSearchStore> action)
    {
        RunSta(() =>
        {
            string databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
            using var catalog = new SqliteLibraryCatalog(databasePath, Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
            catalog.Initialize();
            var store = new LibraryAdvancedSearchStore(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json"), (_, _) => { });
            store.SaveNew("Saved only", new CatalogSearchDefinition(1, new[]
            {
                new CatalogSearchCondition(
                    "file.name",
                    CatalogSearchOperator.Contains,
                    new CatalogSearchValue(Text: "saved-only"))
            }));
            var probe = new CountingProbe();
            using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, probe, new EmptyVisual(),
                startBackgroundWork: false, startMaintenanceScheduler: false);
            var options = new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(AddToEncodeQueueAsync: queue);
            using var form = new LibraryAnalyzerForm(runtime, reviewOptions: options, advancedSearchStore: store);
            form.Show();
            Application.DoEvents();
            try { action(catalog, form, probe, store); }
            finally { form.Close(); }
        });
    }

    private Dictionary<string, long> SeedFiles(SqliteLibraryCatalog catalog)
    {
        string locationPath = Path.Combine(_root, "library");
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(locationPath));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        string[] names = new[]
        {
            "ordinary-present.mkv",
            "ordinary-missing.mkv",
            "ordinary-unselected.mkv",
            "ordinary-other.mkv",
            "advanced-only.mkv",
            "saved-only.mkv"
        }.Concat(Enumerable.Range(0, 200).Select(index => $"saved-only-{index:D3}.mkv")).ToArray();
        DateTime modified = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        LibraryInventoryMutation[] mutations = catalog.UpsertInventoryBatchDetailed(
            scan,
            names.Select(name => new LibraryInventoryEntry(Path.Combine(locationPath, name), name, 1_000, modified)).ToArray(),
            32).Mutations.ToArray();
        return mutations.ToDictionary(mutation => Path.GetFileName(mutation.FullPath), mutation => mutation.FileId,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void MarkMissing(string databasePath, long fileId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE indexed_files SET availability_state=$missing WHERE id=$id;";
        command.Parameters.AddWithValue("$missing", (int)IndexedFileAvailability.Missing);
        command.Parameters.AddWithValue("$id", fileId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static DataGridViewRow RowNamed(DataGridView grid, string fileName) => grid.Rows.Cast<DataGridViewRow>()
        .Single(row => row.Tag is LibraryFileViewRecord file && file.FileName == fileName);

    private static void AssertEncodeMenuEnabled(LibraryAnalyzerForm form, DataGridView grid, bool expected)
    {
        ContextMenuStrip menu = Field<ContextMenuStrip>(form, "_filesMenu");
        ToolStripMenuItem encode = menu.Items.Find("Encode", true).OfType<ToolStripMenuItem>().Single();
        menu.Show(grid, 0, 0);
        Application.DoEvents();
        try { Assert.Equal(expected, encode.Enabled); }
        finally { menu.Close(); }
    }

    private static void SelectRows(DataGridView grid, params DataGridViewRow[] rows)
    {
        grid.ClearSelection();
        if (rows.Length == 0) return;
        grid.CurrentCell = rows[0].Cells["Name"];
        foreach (DataGridViewRow row in rows)
            row.Selected = true;
    }

    private static long RequestGeneration(LibraryAnalyzerForm form) =>
        Field<long>(Field<object>(form, "_fileRequests"), "_generation");

    private static LibraryFileQuery InvokeQuery(LibraryAnalyzerForm form) =>
        (LibraryFileQuery)(Invoke(form, "BuildFileQuery") ?? throw new InvalidOperationException());

    private static Task InvokeTask(object value, string method) =>
        (Task)(Invoke(value, method) ?? throw new InvalidOperationException());

    private static object? Invoke(object value, string method, params object?[] arguments) =>
        (value.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(method)).Invoke(value, arguments);

    private static T Field<T>(object value, string name) =>
        (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new MissingFieldException(name));

    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("A Library Analyzer query did not finish.");
            Application.DoEvents();
            Thread.Yield();
        }
        task.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("The queue handoff did not finish.");
            Application.DoEvents();
            Thread.Yield();
        }
        Application.DoEvents();
    }

    private static void WaitForRequestIdle(LibraryAnalyzerForm form)
    {
        object coordinator = Field<object>(form, "_fileRequests");
        var timer = Stopwatch.StartNew();
        while (coordinator.GetType().GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(coordinator) != null)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("The saved-search query did not finish.");
            Application.DoEvents();
            Thread.Yield();
        }
        Application.DoEvents();
    }

    private void RunSta(Action action)
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"Queue integration UI test timed out at {_stage}.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class CountingProbe : ILibraryMetadataProbe
    {
        public int Calls { get; private set; }
        public string ToolVersion => "test";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new MediaProbeResult { Success = false });
        }
    }

    private sealed class EmptyVisual : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "test";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
