using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerSavedSearchUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-SavedSearchUi", Guid.NewGuid().ToString("N"));
    private string _stage = "starting";

    public LibraryAnalyzerSavedSearchUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SavedSearchControlsLoadEquivalentDefinitionOnceAndPreserveOrdinaryFilesFilters()
    {
        WithForm((store, form) =>
        {
            Mark("open Files page");
            Field<TabControl>(form, "_tabs").SelectedIndex = 2;
            Application.DoEvents();
            WaitForRequestIdle(form);
            Field<TextBox>(form, "_search").Text = "ordinary filter";
            SetField(form, "_page", 4);

            ComboBox picker = Field<ComboBox>(form, "_savedSearchPicker");
            Assert.Contains(picker.Items.Cast<object>(), item => item.ToString() == "1080p mixed search");
            Assert.True(Field<Button>(form, "_saveSavedSearchButton").Visible);
            Assert.True(Field<Button>(form, "_manageSavedSearchesButton").Visible);
            object savedItem = picker.Items.Cast<object>().Single(item => item.ToString() == "1080p mixed search");
            long generationBefore = RequestGeneration(form);

            Mark("select saved search");
            picker.SelectedItem = savedItem;
            Assert.Equal(generationBefore + 2, RequestGeneration(form));
            WaitForRequestIdle(form);

            LibraryFileQuery query = InvokeQuery(form);
            Assert.Equal(0, query.Offset);
            Assert.Equal("ordinary filter", query.Search);
            Assert.Equal("definition-only text", query.AdvancedSearch?.Search);
            Assert.Equal(812, query.AdvancedSearch?.LocationId);
            Assert.Equal(IndexedFileAvailability.Present, query.AdvancedSearch?.Availability);
            Assert.Equal(LibraryProbeStatus.Succeeded, query.AdvancedSearch?.ProbeStatus);
            Assert.True(CatalogSearchDefinitionEquivalence.AreEquivalent(_savedDefinition, query.AdvancedSearch!));

            ComboBox codec = Field<ComboBox>(form, "_quickCodec");
            Assert.Equal("h264", codec.SelectedItem?.GetType().GetProperty("Value")?.GetValue(codec.SelectedItem));
            Assert.Equal(5, Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows").Count);
            LibraryAnalyzerSearchConditionRow bitrate = Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows")
                .Single(row => row.SelectedProperty.Id == "video.bitrate");
            Assert.Equal("mbps", bitrate.Unit.SelectedItem);
            Assert.Equal("kbps", bitrate.UpperUnit.SelectedItem);
            Assert.Equal("13", bitrate.ValueText.Text);
            Assert.Equal("15000", bitrate.UpperText.Text);
            LibraryAnalyzerSearchConditionRow oneOf = Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows")
                .Single(row => row.SelectedProperty.Id == "file.extension");
            Assert.True(CatalogSearchDefinitionEquivalence.AreEquivalent(
                new CatalogSearchDefinition(1, new[] { _savedDefinition.Conditions.Single(item => item.PropertyId == "file.extension") }),
                new CatalogSearchDefinition(1, new[] { oneOf.BuildCondition() })));
            Assert.DoesNotContain("*", picker.SelectedItem!.ToString());
            Assert.Equal(0, _probeCalls);
        }, store => store.SaveNew("1080p mixed search", _savedDefinition));
    }

    [Fact]
    public void SaveUpdateRenameDeleteAndClearKeepDirtyStateBoundToAdvancedDefinition()
    {
        WithForm((store, form) =>
        {
            Field<TabControl>(form, "_tabs").SelectedIndex = 2;
            Application.DoEvents();
            WaitForRequestIdle(form);
            ComboBox picker = Field<ComboBox>(form, "_savedSearchPicker");
            picker.SelectedItem = picker.Items.Cast<object>().Single(item => item.ToString() == "Original");
            WaitForRequestIdle(form);

            Field<TextBox>(form, "_search").Text = "ordinary only";
            Assert.DoesNotContain("*", picker.SelectedItem!.ToString());
            SetChoice(Field<ComboBox>(form, "_quickBitDepth"), "10-bit");
            Assert.Contains("*", picker.SelectedItem!.ToString());
            Assert.True(Field<ToolStripMenuItem>(form, "_updateSavedSearchMenuItem").Enabled);

            Invoke(form, "UpdateSelectedSavedSearch");
            Assert.DoesNotContain("*", picker.SelectedItem!.ToString());
            Assert.Contains(store.Load().Single().Definition.Conditions,
                condition => condition.PropertyId == "video.bit_depth" && condition.Value?.Number == 10m);

            Invoke(form, "SaveCurrentSearchAs", "Saved Copy");
            Assert.Equal(2, store.Load().Count);
            Assert.Equal("Saved Copy", picker.SelectedItem?.ToString());
            Invoke(form, "RenameSelectedSavedSearch", "Renamed Copy");
            Assert.Equal("Renamed Copy", picker.SelectedItem?.ToString());
            Assert.Contains(store.Load(), item => item.Name == "Renamed Copy");

            Field<Button>(form, "_advancedSearchClear").PerformClick();
            WaitForRequestIdle(form);
            Assert.Equal(2, store.Load().Count);
            Assert.Equal("ordinary only", Field<TextBox>(form, "_search").Text);
            Assert.Null(InvokeQuery(form).AdvancedSearch);
            Assert.Equal("Saved searches", picker.SelectedItem?.ToString());

            picker.SelectedItem = picker.Items.Cast<object>().Single(item => item.ToString() == "Renamed Copy");
            WaitForRequestIdle(form);
            int rowCount = Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows").Count;
            Invoke(form, "DeleteSelectedSavedSearch");
            Assert.Single(store.Load());
            Assert.Equal("Saved searches", picker.SelectedItem?.ToString());
            Assert.Equal(rowCount, Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows").Count);
        }, store => store.SaveNew("Original", new CatalogSearchDefinition(1,
            new[] { Is("video.codec", "h264") })));
    }

    [Fact]
    public void CorruptSavedSearchFileDoesNotPreventLibraryAnalyzerOpening()
    {
        string savedSearchPath = Path.Combine(_root, "broken.json");
        const string broken = "{ broken";
        File.WriteAllText(savedSearchPath, broken);

        WithForm((store, form) =>
        {
            Assert.True(form.IsHandleCreated);
            Assert.Single(Field<ComboBox>(form, "_savedSearchPicker").Items.Cast<object>());
            Assert.Equal(broken, File.ReadAllText(savedSearchPath));
        }, storagePath: savedSearchPath);
    }

    private CatalogSearchDefinition _savedDefinition => new(1, new CatalogSearchCondition[]
    {
        Is("video.codec", "h264"),
        new("video.fps", CatalogSearchOperator.Between,
            CatalogSearchValue.ParseNumber("29", "fps"), CatalogSearchValue.ParseNumber("30", "fps")),
            new("video.bitrate", CatalogSearchOperator.Between,
            CatalogSearchValue.ParseNumber("13", "mbps"), CatalogSearchValue.ParseNumber("15000", "kbps")),
        new("video.width", CatalogSearchOperator.Equal, CatalogSearchValue.ParseNumber("1920", "pixels")),
        new("video.height", CatalogSearchOperator.Equal, CatalogSearchValue.ParseNumber("1080", "pixels")),
        Is("video.scan_type", "progressive"),
        new("video.bit_depth", CatalogSearchOperator.Equal, CatalogSearchValue.ParseNumber("8", "count")),
        Is("file.availability", "present"),
        new("file.extension", CatalogSearchOperator.OneOf, Values: new[]
        {
            new CatalogSearchValue(Text: "mkv"),
            new CatalogSearchValue(Text: "part,one"),
            new CatalogSearchValue(Text: "folder\\clip"),
            new CatalogSearchValue(Text: "drive\\,part")
        })
    })
    {
        Search = "definition-only text",
        LocationId = 812,
        Availability = IndexedFileAvailability.Present,
        ProbeStatus = LibraryProbeStatus.Succeeded,
        Sort = new CatalogSearchSort("video.width", Descending: true)
    };

    private int _probeCalls;

    private void WithForm(Action<LibraryAdvancedSearchStore, LibraryAnalyzerForm> action,
        Action<LibraryAdvancedSearchStore>? seed = null, string? storagePath = null)
    {
        RunSta(() =>
        {
            string databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
            using var catalog = new SqliteLibraryCatalog(databasePath, Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
            catalog.Initialize();
            var store = new LibraryAdvancedSearchStore(storagePath ?? Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json"),
                (message, exception) => { });
            seed?.Invoke(store);
            var probe = new CountingProbe(() => Interlocked.Increment(ref _probeCalls));
            using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, probe, new EmptyVisual(),
                startBackgroundWork: false, startMaintenanceScheduler: false);
            using var form = new LibraryAnalyzerForm(runtime, advancedSearchStore: store);
            form.Show();
            Application.DoEvents();
            try { action(store, form); }
            finally { form.Close(); }
        });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), $"Saved-search UI test timed out at {_stage}.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static long RequestGeneration(LibraryAnalyzerForm form)
    {
        object coordinator = Field<object>(form, "_fileRequests");
        return Field<long>(coordinator, "_generation");
    }

    private void WaitForRequestIdle(LibraryAnalyzerForm form)
    {
        var timer = Stopwatch.StartNew();
        object coordinator = Field<object>(form, "_fileRequests");
        while (ReadField<CancellationTokenSource?>(coordinator, "_active") != null)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"Query did not finish at {_stage}.");
            Application.DoEvents();
            Thread.Yield();
        }
        Application.DoEvents();
    }

    private void Mark(string stage) => _stage = stage;

    private static void SetChoice(ComboBox combo, string label) =>
        combo.SelectedItem = combo.Items.Cast<object>().Single(item => item.ToString() == label);

    private static T Field<T>(object value, string name) =>
        (T)(ReadField<T?>(value, name) ?? throw new MissingFieldException(name));

    private static T? ReadField<T>(object value, string name) =>
        (T?)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);

    private static void SetField(object value, string name, object fieldValue) =>
        (value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(value, fieldValue);

    private static object? Invoke(object value, string method, params object?[] arguments) =>
        (value.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(method)).Invoke(value, arguments);

    private static LibraryFileQuery InvokeQuery(LibraryAnalyzerForm form) =>
        (LibraryFileQuery)(Invoke(form, "BuildFileQuery") ?? throw new InvalidOperationException());

    private static CatalogSearchCondition Is(string propertyId, string value) =>
        new(propertyId, CatalogSearchOperator.Is, new CatalogSearchValue(Text: value));

    private sealed class CountingProbe(Action called) : ILibraryMetadataProbe
    {
        public string ToolVersion => "test";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken token)
        {
            called();
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
