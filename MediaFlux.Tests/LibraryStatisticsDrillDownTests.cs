using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryStatisticsDrillDownTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MediaFlux-StatisticsDrillDown",
        Guid.NewGuid().ToString("N"));

    public LibraryStatisticsDrillDownTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AggregateBucketsAndCatalogPagedDrillDownUseIdenticalMembershipRules()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "library");
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(library));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        FileFact[] facts =
        {
            new("hdr-hevc-a.mkv", 900, "matroska", "hevc", 3840, 2160, "smpte2084", "bt2020"),
            new("hdr-hevc-b.mkv", 800, "matroska", "hevc", 3840, 2160, "smpte2084", "bt2020"),
            new("sdr-h264.mp4", 700, "mov,mp4", "h264", 1920, 1080, "bt709", "bt709"),
            new("sdr-av1.webm", 600, "webm", "av1", 1280, 720, "bt709", "bt709"),
            new("unknown.mpg", 500, "", "", 640, 480, "", "")
        };
        catalog.UpsertInventoryBatch(scan, facts.Select((fact, index) => new LibraryInventoryEntry(
            Path.Combine(library, fact.Name),
            fact.Name,
            fact.Size,
            DateTime.UtcNow.AddMinutes(index),
            DateTime.UtcNow.AddDays(-index))).ToArray());
        catalog.CompleteScan(scan, new LibraryScanCompletion(
            LibraryScanStatus.Completed, facts.Length, 0, facts.Length, 0, 0, 0));

        foreach (FileFact fact in facts)
        {
            IndexedFileRecord file = catalog.GetFileByPath(Path.Combine(library, fact.Name))!;
            catalog.SaveMediaMetadata(new LibraryMediaMetadata(
                file.Id, 1, "test", LibraryProbeStatus.Succeeded, 1,
                null, DateTime.UtcNow, DateTime.UtcNow, file.SizeBytes, file.LastWriteTimeUtc,
                fact.Container, 60, 2_000_000, fact.Codec, "", null,
                fact.Width, fact.Height, 24, "", 10, "", "", "",
                fact.Transfer, fact.Primaries,
                Array.Empty<LibraryAudioStreamMetadata>(),
                Array.Empty<LibrarySubtitleStreamMetadata>(), 0, 0, ""));
        }

        LibraryStatistics statistics = catalog.GetLibraryStatistics(topCount: 2);
        AssertCategory(catalog, LibraryStatisticCategory.Codec, statistics.ByCodec);
        AssertCategory(catalog, LibraryStatisticCategory.Resolution, statistics.ByResolution);
        AssertCategory(catalog, LibraryStatisticCategory.Container, statistics.ByContainer);
        AssertCategory(catalog, LibraryStatisticCategory.DynamicRange, statistics.ByDynamicRange);

        LibraryStatisticBucket codecOther = Assert.Single(statistics.ByCodec, bucket => bucket.IsRemainder);
        LibraryFilePage firstPage = catalog.QueryFiles(new LibraryFileQuery(
            SortColumn: "size",
            Descending: true,
            Limit: 1,
            Statistic: new LibraryStatisticDrillDown(
                LibraryStatisticCategory.Codec,
                codecOther.Label,
                IsRemainder: true,
                TopCount: 2)));
        Assert.Equal(codecOther.FileCount, firstPage.TotalCount);
        Assert.Single(firstPage.Files);
    }

    [Fact]
    public void QueueSelectionDispatchesOneOrManyAvailableFilesAndSkipsMissingAndDuplicates()
    {
        string one = Path.Combine(_root, "one.mkv");
        string two = Path.Combine(_root, "two.mkv");
        File.WriteAllText(one, "one");
        File.WriteAllText(two, "two");
        var dispatched = new List<IReadOnlyList<string>>();

        LibraryFileQueueResult single = LibraryFileQueueSelection.Dispatch(
            new[] { one },
            paths => dispatched.Add(paths));
        Assert.True(single.Dispatched);
        Assert.Single(single.AvailablePaths);

        LibraryFileQueueResult multiple = LibraryFileQueueSelection.Dispatch(
            new[] { one, two, one, Path.Combine(_root, "missing.mkv") },
            paths => dispatched.Add(paths));
        Assert.True(multiple.Dispatched);
        Assert.Equal(2, multiple.AvailablePaths.Count);
        Assert.Equal(1, multiple.UnavailableCount);
        Assert.Equal(2, dispatched.Count);

        LibraryFileQueueResult missing = LibraryFileQueueSelection.Dispatch(
            new[] { Path.Combine(_root, "missing-only.mkv") },
            paths => dispatched.Add(paths));
        Assert.False(missing.Dispatched);
        Assert.Empty(missing.AvailablePaths);
        Assert.Equal(1, missing.UnavailableCount);
        Assert.Equal(2, dispatched.Count);
    }

    [Fact]
    public void FileMenuPresentationAdaptsToSingleAndMultipleSelection()
    {
        LibraryFileMenuPresentation single =
            LibraryFileMenuPresentation.ForSelection(1, 1, queueAvailable: true);
        Assert.Equal("Copy Path", single.CopyText);
        Assert.Equal("Add to Encode Queue", single.EncodeText);
        Assert.True(single.CanEncode);

        LibraryFileMenuPresentation multiple =
            LibraryFileMenuPresentation.ForSelection(3, 2, queueAvailable: true);
        Assert.Equal("Copy Paths", multiple.CopyText);
        Assert.Equal("Add Selected Files to Encode Queue", multiple.EncodeText);
        Assert.True(multiple.CanEncode);

        Assert.False(LibraryFileMenuPresentation.ForSelection(
            1, 0, queueAvailable: true).CanEncode);
    }

    private static void AssertCategory(
        SqliteLibraryCatalog catalog,
        LibraryStatisticCategory category,
        IReadOnlyList<LibraryStatisticBucket> buckets)
    {
        long total = 0;
        var fileIds = new HashSet<long>();
        foreach (LibraryStatisticBucket bucket in buckets)
        {
            LibraryFilePage page = catalog.QueryFiles(new LibraryFileQuery(
                Limit: 1000,
                Statistic: new LibraryStatisticDrillDown(
                    category,
                    bucket.Label,
                    bucket.IsRemainder,
                    TopCount: 2)));
            Assert.Equal(bucket.FileCount, page.TotalCount);
            Assert.Equal(page.TotalCount, page.Files.Count);
            total += page.TotalCount;
            foreach (LibraryFileViewRecord file in page.Files)
                Assert.True(fileIds.Add(file.FileId), $"File {file.FileId} appeared in more than one {category} bucket.");
        }
        Assert.Equal(5, total);
        Assert.Equal(5, fileIds.Count);
    }

    private SqliteLibraryCatalog CreateCatalog()
    {
        var catalog = new SqliteLibraryCatalog(
            Path.Combine(_root, "library.db"),
            Path.Combine(_root, "backups"),
            Path.Combine(_root, "recovery"));
        catalog.Initialize();
        return catalog;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record FileFact(
        string Name,
        long Size,
        string Container,
        string Codec,
        int Width,
        int Height,
        string Transfer,
        string Primaries);
}

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryStatisticsDrillDownUiTests
{
    [Fact]
    public void FileBrowserSupportsMultiSelectionAndPreservesSelectionOnRefresh()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunSta(() =>
        {
            LibraryFileViewRecord[] files =
            {
                File(1, "one.mkv"),
                File(2, "two.mkv")
            };
            using var browser = new LibraryFileBrowser(query =>
                new LibraryFilePage(files.Length, files.Skip(query.Offset).Take(query.Limit).ToArray()));
            Wait(browser.OpenAsync(
                new LibraryStatisticDrillDown(LibraryStatisticCategory.Codec, "hevc"),
                "Codec: hevc"));
            Assert.True(browser.Grid.MultiSelect);
            Assert.Empty(browser.SelectedFiles());

            browser.Grid.Rows[1].Selected = true;
            Wait(browser.RefreshAsync());
            Assert.Equal(2, Assert.Single(browser.SelectedFiles()).FileId);
        });
    }

    [Fact]
    public void StatisticsViewFilesRoutingOpensBrowserWithoutDispatchingQueueAction()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-StatisticsRouting", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunSta(() =>
            {
                using var catalog = new SqliteLibraryCatalog(
                    Path.Combine(root, "library.db"),
                    Path.Combine(root, "backups"),
                    Path.Combine(root, "recovery"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(
                    catalog,
                    new[] { ".mkv" },
                    new EmptyProbe(),
                    new EmptyVisual());
                int queueDispatches = 0;
                using var form = new LibraryAnalyzerForm(
                    runtime,
                    reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                        AddToEncodeQueue: _ => queueDispatches++));
                DataGridView grid = FindStatisticsGrid(form, "Codec");
                int row = grid.Rows.Add("hevc", "1", "1 GB", "");
                grid.Rows[row].Tag = new LibraryStatisticBucket("hevc", 1, 1024);
                grid.Rows[row].Selected = true;

                MethodInfo open = typeof(LibraryAnalyzerForm).GetMethod(
                    "OpenStatisticsFilesAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Wait((Task)open.Invoke(form, new object[]
                {
                    grid,
                    LibraryStatisticCategory.Codec,
                    "Codec"
                })!);

                LibraryFileBrowser browser = (LibraryFileBrowser)typeof(LibraryAnalyzerForm)
                    .GetField("_statisticsFileBrowser", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(form)!;
                Assert.Equal("hevc", browser.DrillDown?.Label);
                Assert.Equal(0, queueDispatches);
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static DataGridView FindStatisticsGrid(LibraryAnalyzerForm form, string tabText)
    {
        TabControl tabs = (TabControl)typeof(LibraryAnalyzerForm)
            .GetField("_statisticsBreakdowns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;
        return tabs.TabPages.Cast<TabPage>()
            .Single(page => page.Text == tabText)
            .Controls.OfType<DataGridView>()
            .Single();
    }

    private static void Wait(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static LibraryFileViewRecord File(long id, string name) => new(
        id, name, @"P:\Media\" + name, @"P:\Media", 100, DateTime.UtcNow,
        IndexedFileAvailability.Present, "matroska", "hevc", 1920, 1080,
        1_000_000, 60, LibraryProbeStatus.Succeeded, "", false,
        DateTime.UtcNow.AddDays(-1), "SDR");

    private sealed class EmptyProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "test";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeResult { Success = false });
    }

    private sealed class EmptyVisual : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "test";
        public Task<IReadOnlyList<ulong>> ExtractAsync(
            VisualFingerprintCandidate candidate,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }
}
