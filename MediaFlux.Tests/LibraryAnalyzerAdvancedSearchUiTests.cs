using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerAdvancedSearchUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-AdvancedSearchUi", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private string _stage = "starting";

    public LibraryAnalyzerAdvancedSearchUiTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void RequestCoordinatorCancelsAndRejectsSupersededGenerations()
    {
        using var requests = new LibraryFilesRequestCoordinator();
        LibraryFilesRequestCoordinator.Request first = requests.Start();
        Assert.True(requests.IsCurrent(first));
        LibraryFilesRequestCoordinator.Request second = requests.Start();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(requests.IsCurrent(first));
        Assert.True(requests.IsCurrent(second));
        requests.Complete(first);
        Assert.True(requests.IsCurrent(second));
        requests.Invalidate();
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(requests.IsCurrent(second));
        requests.Complete(second);
    }

    [Fact]
    public void ConditionEditorFollowsRegistryForNumbersChoicesDatesAndValueFreeOperators()
    {
        RunSta(() =>
        {
            using var host = new Form();
            using var row = new LibraryAnalyzerSearchConditionRow("video.bitrate") { Dock = DockStyle.Top };
            host.Controls.Add(row);
            host.Show();
            Application.DoEvents();

            Assert.Equal("video.bitrate", row.SelectedProperty.Id);
            Assert.Contains(row.Operator.Items.Cast<object>(), choice => choice.ToString() == "Between");
            row.SelectOperator(CatalogSearchOperator.Between);
            Assert.True(row.ValueText.Visible);
            Assert.True(row.UpperText.Visible);
            Assert.True(row.Unit.Visible);
            row.ValueText.Text = "13";
            row.UpperText.Text = "15";
            row.Unit.SelectedItem = "mbps";
            CatalogSearchCondition bitrate = row.BuildCondition();
            Assert.Equal("video.bitrate", bitrate.PropertyId);
            Assert.Equal("mbps", bitrate.Value?.Unit);
            Assert.Equal(15m, bitrate.UpperValue?.Number);

            SelectProperty(row, "video.dynamic_range");
            Assert.True(row.ValueChoice.Visible);
            Assert.False(row.Unit.Visible);
            row.ValueChoice.SelectedItem = "HDR";
            Assert.Equal("HDR", row.BuildCondition().Value?.Text);
            row.SelectOperator(CatalogSearchOperator.Unknown);
            Assert.False(row.ValueChoice.Visible);
            Assert.Null(row.BuildCondition().Value);

            SelectProperty(row, "stream.audio_present");
            Assert.Contains(row.Operator.Items.Cast<object>(), choice => choice.ToString() == "Yes");
            Assert.False(row.ValueText.Visible);
            Assert.False(row.ValueChoice.Visible);
            Assert.Null(row.BuildCondition().Value);

            SelectProperty(row, "file.modified_utc");
            Assert.True(row.ValueDate.Visible);
            row.SelectOperator(CatalogSearchOperator.Between);
            Assert.True(row.UpperDate.Visible);
            Assert.NotNull(row.BuildCondition().Value?.Date);
            host.Close();
        });
    }

    [Fact]
    public void FilesPagePanelBuildsAcceptanceDefinitionAndClearPreservesOrdinaryFilters()
    {
        WithForm((catalog, form, _) =>
        {
            LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
            Pump(InvokeTask(form, "RefreshLocationsAsync"));
            TabControl tabs = Field<TabControl>(form, "_tabs");
            tabs.SelectedIndex = 2;
            form.Size = new Size(1100, 700);
            Application.DoEvents();
            TabPage filesPage = tabs.TabPages[2];
            Assert.Single(Descendants<DataGridView>(filesPage), grid => grid.Name == "FilesGrid");
            Panel panel = Field<Panel>(form, "_advancedSearchPanel");
            Button toggle = Field<Button>(form, "_advancedSearchToggle");
            Button clear = Field<Button>(form, "_advancedSearchClear");
            Assert.Contains(Descendants<Panel>(filesPage), item => ReferenceEquals(item, panel));
            Assert.Equal(36, panel.Height);
            toggle.PerformClick();
            Application.DoEvents();
            Assert.True(Field<TableLayoutPanel>(form, "_advancedSearchBody").Visible);
            Assert.True(panel.Height > 36);
            DataGridView filesGrid = Field<DataGridView>(form, "_filesGrid");
            Assert.True(filesGrid.Height >= 100,
                $"Files grid should remain usable at the minimum form size: grid={filesGrid.Bounds}, advanced={panel.Bounds}, tab={filesPage.ClientSize}.");

            SetChoice(Field<ComboBox>(form, "_quickCodec"), "H.264");
            SetChoice(Field<ComboBox>(form, "_quickResolution"), "1080p");
            Field<TextBox>(form, "_quickFpsMin").Text = "29";
            Field<TextBox>(form, "_quickFpsMax").Text = "30";
            Field<TextBox>(form, "_quickBitrateMin").Text = "13";
            Field<TextBox>(form, "_quickBitrateMax").Text = "15";
            SetChoice(Field<ComboBox>(form, "_quickBitDepth"), "8-bit");
            SetChoice(Field<ComboBox>(form, "_quickScanType"), "progressive");
            Field<ComboBox>(form, "_availability").SelectedIndex = 1;
            Field<TextBox>(form, "_search").Text = "film";
            ComboBox locationFilter = Field<ComboBox>(form, "_fileLocation");
            locationFilter.SelectedItem = locationFilter.Items.Cast<object>().Single(item => item.ToString() == location.Path);

            Invoke(form, "AddAdvancedCondition", "video.width");
            Invoke(form, "AddAdvancedCondition", "video.height");
            LibraryAnalyzerSearchConditionRow[] rows = Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows").ToArray();
            rows[0].ValueText.Text = "1920";
            rows[1].ValueText.Text = "1080";
            LibraryFileQuery query = InvokeQuery(form);
            Assert.Equal("film", query.Search);
            Assert.Equal(location.Id, query.LocationId);
            Assert.Equal(IndexedFileAvailability.Present, query.Availability);
            Assert.Equal(300, Field<System.Windows.Forms.Timer>(form, "_advancedSearchDebounceTimer").Interval);
            Assert.Equal("8 advanced filters", Field<Label>(form, "_advancedSearchCount").Text);
            CatalogSearchDefinition definition = Assert.IsType<CatalogSearchDefinition>(query.AdvancedSearch);
            CatalogSearchDefinitionValidator.Validate(definition);
            Assert.Equal(8, definition.Conditions.Count);
            Assert.Contains(definition.Conditions, item => item.PropertyId == "video.bitrate" &&
                item.Operator == CatalogSearchOperator.Between && item.Value?.Number == 13m &&
                item.UpperValue?.Number == 15m && item.Value.Unit == "mbps");
            Assert.Contains(definition.Conditions, item => item.PropertyId == "video.width" && item.Value?.Number == 1920m);
            Assert.Contains(definition.Conditions, item => item.PropertyId == "video.height" && item.Value?.Number == 1080m);
            Assert.Contains(definition.Conditions, item => item.PropertyId == "video.scan_type" && item.Value?.Text == "progressive");
            Assert.DoesNotContain(definition.Conditions, item => item.PropertyId == "media.total_bitrate");
            Assert.Contains(CatalogSearchSqlCompiler.Compile(definition).Parameters, item => Equals(item.Value, 13_000_000L));
            Assert.Contains(CatalogSearchSqlCompiler.Compile(definition).Parameters, item => Equals(item.Value, 15_000_000L));

            toggle.PerformClick();
            Assert.Equal(36, panel.Height);
            Assert.Equal(8, InvokeQuery(form).AdvancedSearch?.Conditions.Count);
            toggle.PerformClick();
            rows[0].Remove.PerformClick();
            Assert.Single(Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows"));
            clear.PerformClick();
            Assert.Null(InvokeQuery(form).AdvancedSearch);
            Assert.Equal("film", InvokeQuery(form).Search);
            Assert.Equal(location.Id, InvokeQuery(form).LocationId);
            Assert.Equal(IndexedFileAvailability.Present, InvokeQuery(form).Availability);
            Assert.Empty(Field<List<LibraryAnalyzerSearchConditionRow>>(form, "_advancedRows"));
            ComboBox sort = Field<ComboBox>(form, "_sort");
            sort.SelectedItem = "Total bitrate";
            Assert.Equal("bitrate", InvokeQuery(form).SortColumn);
        });
    }

    [Fact]
    public void InvalidInputPreventsQueryAndDefinitionChangeResetsPaging()
    {
        WithForm((_, form, _) =>
        {
            TabControl tabs = Field<TabControl>(form, "_tabs");
            tabs.SelectedIndex = 2;
            Application.DoEvents();
            SetChoice(Field<ComboBox>(form, "_quickCodec"), "H.264");
            Field<TextBox>(form, "_quickFpsMin").Text = "29";
            LibraryFileQuery first = InvokeQuery(form);
            SetField(form, "_page", 2);
            LibraryFileQuery second = InvokeQuery(form);
            Assert.Equal(400, second.Offset);
            Assert.Equal(first.AdvancedSearch?.Conditions, second.AdvancedSearch?.Conditions);

            Field<TextBox>(form, "_quickFpsMax").Text = "bad";
            Assert.Equal(0, Field<int>(form, "_page"));
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            Assert.Contains("Fix the Advanced Search", Field<Label>(form, "_filesSummary").Text);
            Assert.NotEmpty(Field<Label>(form, "_advancedSearchValidation").Text);
            Assert.Throws<TargetInvocationException>(() => InvokeQuery(form));

            Field<TextBox>(form, "_quickFpsMax").Text = "30";
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            Assert.Empty(Field<Label>(form, "_advancedSearchValidation").Text);
            Assert.Equal(0, InvokeQuery(form).Offset);
        });
    }

    [Fact]
    public void FilesGridUsesVideoProjectionAndRestoresSelectionByFileId()
    {
        WithForm((catalog, form, probe) =>
        {
            Mark("create location");
            LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
            Mark("begin scan");
            LibraryScanHandle scan = catalog.BeginScan(location.Id);
            Mark("add first file");
            long firstId = AddFile(catalog, scan, "first.mkv", 14_000_000, 16_000_000);
            Mark("add second file");
            long secondId = AddFile(catalog, scan, "second.mkv", 15_000_000, 18_000_000);
            Mark("add unknown file");
            AddFile(catalog, scan, "unknown.mkv", null, 19_000_000);
            Mark("open Files tab");
            TabControl tabs = Field<TabControl>(form, "_tabs");
            tabs.SelectedIndex = 2;
            Application.DoEvents();
            Mark("default Files refresh");
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            DataGridView grid = Field<DataGridView>(form, "_filesGrid");
            Assert.Equal(3, grid.Rows.Count);
            DataGridViewRow defaultSecond = grid.Rows.Cast<DataGridViewRow>().Single(row =>
                row.Tag is LibraryFileViewRecord file && file.FileId == secondId);
            Assert.Equal("29.97", defaultSecond.Cells["EffectiveFps"].Value);
            Assert.Equal("15 Mbps", defaultSecond.Cells["VideoBitrate"].Value);
            Assert.Equal("8 bit", defaultSecond.Cells["BitDepth"].Value);
            DataGridViewRow defaultUnknown = grid.Rows.Cast<DataGridViewRow>().Single(row =>
                ((LibraryFileViewRecord)row.Tag!).FileName == "unknown.mkv");
            Assert.Equal("--", defaultUnknown.Cells["VideoBitrate"].Value);
            Assert.Equal("19 Mbps", defaultUnknown.Cells["Bitrate"].Value);
            Assert.Equal(0, probe.Calls);

            SetChoice(Field<ComboBox>(form, "_quickCodec"), "H.264");
            Field<TextBox>(form, "_quickBitrateMin").Text = "13";
            Field<TextBox>(form, "_quickBitrateMax").Text = "15";
            Mark("initial Files refresh");
            Pump(InvokeTask(form, "RefreshFilesAsync"));

            Assert.Equal(2, grid.Rows.Count);
            Assert.Equal(2, Field<long>(form, "_totalFiles"));
            Assert.DoesNotContain("could not complete", Field<Label>(form, "_filesSummary").Text,
                StringComparison.OrdinalIgnoreCase);
            DataGridViewRow second = grid.Rows.Cast<DataGridViewRow>().Single(row =>
                row.Tag is LibraryFileViewRecord file && file.FileId == secondId);
            Assert.Equal("15 Mbps", second.Cells["VideoBitrate"].Value);
            Assert.Equal("18 Mbps", second.Cells["Bitrate"].Value);
            Assert.Equal("8 bit", second.Cells["BitDepth"].Value);
            Assert.NotEqual("--", second.Cells["EffectiveFps"].Value);
            grid.ClearSelection();
            grid.CurrentCell = second.Cells["Name"];
            second.Selected = true;
            Mark("refresh selected page");
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            Assert.Equal(secondId, Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>())
                .Tag is LibraryFileViewRecord refreshed ? refreshed.FileId : -1);

            Field<TextBox>(form, "_search").Text = "first";
            Mark("filtered refresh");
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.Equal(firstId, ((LibraryFileViewRecord)grid.Rows[0].Tag!).FileId);
            Assert.False(File.Exists(((LibraryFileViewRecord)grid.Rows[0].Tag!).FullPath));
            Assert.Equal(0, probe.Calls);

            Field<TextBox>(form, "_search").Clear();
            Field<TextBox>(form, "_quickBitrateMin").Clear();
            Field<TextBox>(form, "_quickBitrateMax").Clear();
            Mark("unknown video bitrate refresh");
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            Assert.Equal(3, grid.Rows.Count);
            DataGridViewRow unknown = grid.Rows.Cast<DataGridViewRow>().Single(row =>
                ((LibraryFileViewRecord)row.Tag!).FileName == "unknown.mkv");
            Assert.Equal("--", unknown.Cells["VideoBitrate"].Value);
            Assert.Equal("19 Mbps", unknown.Cells["Bitrate"].Value);

            Field<Button>(form, "_advancedSearchClear").PerformClick();
            Mark("ordinary refresh");
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            Assert.Equal(3, grid.Rows.Count);
            DataGridViewRow ordinarySecond = grid.Rows.Cast<DataGridViewRow>().Single(row =>
                row.Tag is LibraryFileViewRecord file && file.FileId == secondId);
            Assert.Equal("29.97", ordinarySecond.Cells["EffectiveFps"].Value);
            Assert.Equal("15 Mbps", ordinarySecond.Cells["VideoBitrate"].Value);
            Assert.Equal("8 bit", ordinarySecond.Cells["BitDepth"].Value);
            DataGridViewRow ordinaryUnknown = grid.Rows.Cast<DataGridViewRow>().Single(row =>
                ((LibraryFileViewRecord)row.Tag!).FileName == "unknown.mkv");
            Assert.Equal("29.97", ordinaryUnknown.Cells["EffectiveFps"].Value);
            Assert.Equal("--", ordinaryUnknown.Cells["VideoBitrate"].Value);
            Assert.Equal("8 bit", ordinaryUnknown.Cells["BitDepth"].Value);
            Assert.Equal("19 Mbps", ordinaryUnknown.Cells["Bitrate"].Value);
            Assert.Equal(0, probe.Calls);
        });
    }

    [Fact]
    public void TechnicalSearchExplainsEmptyResultsWhileMetadataEnrichmentIsPending()
    {
        WithForm((catalog, form, _) =>
        {
            LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
            LibraryScanHandle scan = catalog.BeginScan(location.Id);
            string path = Path.Combine(_root, "library", "pending.mkv");
            DateTime modified = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            catalog.UpsertInventoryBatchDetailed(scan,
                new[] { new LibraryInventoryEntry(path, "pending.mkv", 1_000_000, modified) }, 2);
            SetField(form, "_overviewSnapshot",
                catalog.GetOverviewSnapshot(LibraryEnrichmentCoordinator.CurrentMetadataVersion));

            TabControl tabs = Field<TabControl>(form, "_tabs");
            tabs.SelectedIndex = 2;
            Field<TextBox>(form, "_quickBitrateMin").Text = "13";
            Pump(InvokeTask(form, "RefreshFilesAsync"));

            Assert.Equal(0, Field<long>(form, "_totalFiles"));
            string technicalSummary = Field<Label>(form, "_filesSummary").Text;
            Assert.Contains("no current matches", technicalSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("metadata is still being enriched", technicalSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("More matches may appear", technicalSummary, StringComparison.OrdinalIgnoreCase);

            Field<TextBox>(form, "_quickBitrateMin").Clear();
            Field<TextBox>(form, "_search").Text = "not-present";
            Pump(InvokeTask(form, "RefreshFilesAsync"));
            string ordinarySummary = Field<Label>(form, "_filesSummary").Text;
            Assert.Contains("No indexed files match", ordinarySummary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("More matches may appear", ordinarySummary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void FilesGridNormalPageRenderMeasurement()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MEDIAFLUX_RUN_CATALOG_SEARCH_PERFORMANCE"), "1", StringComparison.Ordinal))
            return;

        WithForm((catalog, form, _) =>
        {
            string locationPath = Path.Combine(_root, "grid-library");
            LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(locationPath));
            LibraryScanHandle scan = catalog.BeginScan(location.Id);
            DateTime modified = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            LibraryInventoryEntry[] entries = Enumerable.Range(0, 200)
                .Select(index =>
                {
                    string name = $"clip-{index:D4}.mkv";
                    return new LibraryInventoryEntry(Path.Combine(locationPath, name), name, 1_000_000 + index, modified);
                })
                .ToArray();
            catalog.UpsertInventoryBatchDetailed(scan, entries, 2);
            LibraryFilePage page = catalog.QueryFiles(new LibraryFileQuery(Limit: 200));
            Assert.Equal(200, page.TotalCount);

            TabControl tabs = Field<TabControl>(form, "_tabs");
            tabs.SelectedIndex = 2;
            Application.DoEvents();
            long[] selectedIds = { page.Files[0].FileId };
            Invoke(form, "RenderFilesPage", page, selectedIds, null);
            Application.DoEvents();
            var renderSamples = new double[5];
            var allocationSamples = new long[5];
            for (int index = 0; index < renderSamples.Length; index++)
            {
                var timer = Stopwatch.StartNew();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                Invoke(form, "RenderFilesPage", page, selectedIds, null);
                Application.DoEvents();
                allocationSamples[index] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                timer.Stop();
                renderSamples[index] = timer.Elapsed.TotalMilliseconds;
            }

            DataGridView grid = Field<DataGridView>(form, "_filesGrid");
            Assert.Equal(200, grid.Rows.Count);
            Assert.Equal(page.Files[0].FileId,
                ((LibraryFileViewRecord)Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>()).Tag!).FileId);
            Assert.Equal(DataGridViewAutoSizeColumnsMode.None, grid.AutoSizeColumnsMode);
            Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column => Assert.True(column.Width > 0));
            double renderMedian = renderSamples.OrderBy(value => value).ElementAt(renderSamples.Length / 2);
            long allocationMedian = allocationSamples.OrderBy(value => value).ElementAt(allocationSamples.Length / 2);
            _output.WriteLine(
                $"GRID rows={grid.Rows.Count}; render+selection-restore-median={renderMedian:F1}ms; " +
                $"samples=[{string.Join(",", renderSamples.Select(value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]ms; " +
                $"ui-thread-allocated-median={allocationMedian:N0} bytes; autosizing=off; fixed-width-columns={grid.Columns.Count}");
        });
    }

    private long AddFile(SqliteLibraryCatalog catalog, LibraryScanHandle scan, string name, long? videoBitrate, long totalBitrate)
    {
        string path = Path.Combine(_root, "library", name);
        DateTime modified = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(scan,
            new[] { new LibraryInventoryEntry(path, name, 1_000_000, modified) }, 2).Mutations);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalog.DatabasePath, Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_metadata(file_id,metadata_version,probe_tool_version,probe_status,
                source_size_bytes,source_last_write_utc_ticks,format_name,duration_seconds,
                total_bitrate,video_codec,width,height,frame_rate,bit_depth,field_order,
                video_bitrate_bps,updated_utc_ticks)
            VALUES($id,2,'test',2,1000000,$modified,'matroska',120,
                $total,'h264',1920,1080,29.97,8,'progressive',$video,$modified)
            """;
        command.Parameters.AddWithValue("$id", mutation.FileId);
        command.Parameters.AddWithValue("$modified", mutation.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$total", totalBitrate);
        command.Parameters.AddWithValue("$video", (object?)videoBitrate ?? DBNull.Value);
        command.ExecuteNonQuery();
        return mutation.FileId;
    }

    private void WithForm(Action<SqliteLibraryCatalog, LibraryAnalyzerForm, CountingProbe> action)
    {
        RunSta(() =>
        {
            string databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
            Mark("create catalog");
            using var catalog = new SqliteLibraryCatalog(databasePath, Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
            catalog.Initialize();
            var probe = new CountingProbe();
            Mark("create runtime");
            using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, probe, new EmptyVisual(),
                startBackgroundWork: false, startMaintenanceScheduler: false);
            Mark("create form");
            using var form = new LibraryAnalyzerForm(runtime);
            Mark("show form");
            form.Show();
            Application.DoEvents();
            Mark("run form assertions");
            try { action(catalog, form, probe); }
            finally { Mark("close form"); form.Close(); }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), $"Advanced Search UI test timed out at {Volatile.Read(ref _stage)}.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private void Mark(string stage) => Volatile.Write(ref _stage, stage);

    private static void SelectProperty(LibraryAnalyzerSearchConditionRow row, string propertyId)
    {
        string display = CatalogSearchRegistry.Properties.Single(property => property.Id == propertyId).DisplayName;
        row.Property.SelectedItem = row.Property.Items.Cast<object>().Single(item => item.ToString() == display);
    }

    private static void SetChoice(ComboBox combo, string display) =>
        combo.SelectedItem = combo.Items.Cast<object>().Single(item => item.ToString() == display);

    private static T Field<T>(object value, string name) =>
        (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new MissingFieldException(name));

    private static void SetField(object value, string name, object fieldValue) =>
        (value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(value, fieldValue);

    private static object? Invoke(object value, string method, params object?[] arguments) =>
        (value.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(method)).Invoke(value, arguments);

    private static LibraryFileQuery InvokeQuery(LibraryAnalyzerForm form) =>
        (LibraryFileQuery)(Invoke(form, "BuildFileQuery") ?? throw new InvalidOperationException());

    private static Task InvokeTask(LibraryAnalyzerForm form, string method) =>
        (Task)(Invoke(form, method) ?? throw new InvalidOperationException());

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(child =>
            (child is T match ? new[] { match } : Array.Empty<T>()).Concat(Descendants<T>(child)));

    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            Application.DoEvents();
            Thread.Yield();
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class CountingProbe : ILibraryMetadataProbe
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string ToolVersion => "test";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
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
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
