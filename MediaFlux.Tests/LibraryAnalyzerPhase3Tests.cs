using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class LibraryAnalyzerPhase3Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MediaFlux-LibraryAnalyzerTests",
        Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LibraryAnalyzerPhase3Tests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InitialAndIncrementalScansClassifyNewChangedAndUnchangedFiles()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
        var fileSystem = new FakeFileSystem(location.Path, Entries(location.Path, ("one.mkv", 100), ("two.mp4", 200)));
        var scanner = CreateScanner(catalog, fileSystem);

        LibraryScanResult initial = await scanner.ScanLocationAsync(location.Id, 1);
        LibraryScanResult unchanged = await scanner.ScanLocationAsync(location.Id, 1);
        fileSystem.SetEntries(Entries(location.Path, ("one.mkv", 101), ("two.mp4", 200), ("three.mkv", 300)));
        LibraryScanResult changed = await scanner.ScanLocationAsync(location.Id, 1);

        Assert.Equal(LibraryScanOutcome.Completed, initial.Outcome);
        Assert.Equal(2, initial.NewFiles);
        Assert.Equal(2, unchanged.UnchangedFiles);
        Assert.Equal(1, changed.NewFiles);
        Assert.Equal(1, changed.ChangedFiles);
        Assert.Equal(1, changed.UnchangedFiles);
        Assert.Equal(3, catalog.GetCounts().Files);
    }

    [Fact]
    public async Task SuccessfulAuthoritativeScanMarksMissingButCanceledScanDoesNot()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
        var fileSystem = new FakeFileSystem(location.Path, Entries(location.Path, ("keep.mkv", 100), ("gone.mkv", 200)));
        var scanner = CreateScanner(catalog, fileSystem);
        await scanner.ScanLocationAsync(location.Id, 1);

        fileSystem.SetEntries(Entries(location.Path, ("keep.mkv", 100)));
        scanner.Pause();
        Task<LibraryScanResult> canceledTask = scanner.ScanLocationAsync(location.Id, 1);
        await Task.Delay(50);
        scanner.Cancel();
        LibraryScanResult canceled = await canceledTask;
        IndexedFileRecord stillPresent = Assert.IsType<IndexedFileRecord>(
            catalog.GetFileByPath(Path.Combine(location.Path, "gone.mkv")));

        LibraryScanResult completed = await scanner.ScanLocationAsync(location.Id, 1);
        IndexedFileRecord missing = Assert.IsType<IndexedFileRecord>(
            catalog.GetFileByPath(Path.Combine(location.Path, "gone.mkv")));

        Assert.Equal(LibraryScanOutcome.Canceled, canceled.Outcome);
        Assert.Equal(IndexedFileAvailability.Present, stillPresent.Availability);
        Assert.Equal(LibraryScanOutcome.Completed, completed.Outcome);
        Assert.Equal(IndexedFileAvailability.Missing, missing.Availability);
        Assert.Equal(1, completed.MissingFiles);
    }

    [Fact]
    public async Task OfflineAndAccessFailedRootsNeverReconcileAsEmpty()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
        var fileSystem = new FakeFileSystem(location.Path, Entries(location.Path, ("video.mkv", 100)));
        var scanner = CreateScanner(catalog, fileSystem);
        await scanner.ScanLocationAsync(location.Id, 1);

        fileSystem.Exists = false;
        LibraryScanResult offline = await scanner.ScanLocationAsync(location.Id, 1);
        Assert.Equal(LibraryScanOutcome.Unavailable, offline.Outcome);
        Assert.Equal(IndexedFileAvailability.Unavailable, catalog.GetFileByPath(Path.Combine(location.Path, "video.mkv"))?.Availability);

        fileSystem.Exists = true;
        fileSystem.SetEntries(Array.Empty<LibraryFileSystemEntry>());
        fileSystem.EnumerationError = new UnauthorizedAccessException("denied");
        LibraryScanResult failed = await scanner.ScanLocationAsync(location.Id, 1);
        Assert.Equal(LibraryScanOutcome.Failed, failed.Outcome);
        Assert.NotEqual(IndexedFileAvailability.Missing, catalog.GetFileByPath(Path.Combine(location.Path, "video.mkv"))?.Availability);
    }

    [Fact]
    public async Task OverlappingRootsKeepAFilePresentWhenOneMembershipBecomesMissing()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string parentPath = Path.Combine(_root, "media");
        string childPath = Path.Combine(parentPath, "movies");
        string sharedPath = Path.Combine(childPath, "film.mkv");
        LibraryLocationRecord parent = catalog.UpsertLocation(new LibraryLocationUpsert(parentPath));
        LibraryLocationRecord child = catalog.UpsertLocation(new LibraryLocationUpsert(childPath));
        var fileSystem = new FakeFileSystem(parentPath, new[] { Entry(sharedPath, 1_000) });
        var scanner = CreateScanner(catalog, fileSystem);

        await scanner.ScanLocationAsync(parent.Id, 1);
        fileSystem.Root = childPath;
        await scanner.ScanLocationAsync(child.Id, 1);
        fileSystem.Root = parentPath;
        fileSystem.SetEntries(Array.Empty<LibraryFileSystemEntry>());
        await scanner.ScanLocationAsync(parent.Id, 1);

        IndexedFileRecord file = Assert.IsType<IndexedFileRecord>(catalog.GetFileByPath(sharedPath));
        Assert.Equal(IndexedFileAvailability.Present, file.Availability);
        Assert.Contains(catalog.GetMembershipsForFile(file.Id), item =>
            item.LocationId == parent.Id && item.Availability == IndexedFileAvailability.Missing);
        Assert.Contains(catalog.GetMembershipsForFile(file.Id), item =>
            item.LocationId == child.Id && item.Availability == IndexedFileAvailability.Present);
    }

    [Fact]
    public async Task NewerCoordinatorSupersedesPausedScanGeneration()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
        var fileSystem = new FakeFileSystem(location.Path, Entries(location.Path, ("one.mkv", 1)));
        LibraryScanCoordinator first = CreateScanner(catalog, fileSystem);
        LibraryScanCoordinator second = CreateScanner(catalog, fileSystem);
        first.Pause();
        Task<LibraryScanResult> staleTask = first.ScanLocationAsync(location.Id, 1);
        await Task.Delay(50);

        LibraryScanResult current = await second.ScanLocationAsync(location.Id, 1);
        first.Resume();
        LibraryScanResult stale = await staleTask;

        Assert.Equal(LibraryScanOutcome.Completed, current.Outcome);
        Assert.Equal(LibraryScanOutcome.Superseded, stale.Outcome);
        Assert.Equal(1, catalog.GetCounts().Files);
    }

    [Fact]
    public void ReopeningCatalogRecoversInterruptedScanAndProbeWork()
    {
        string databasePath = Path.Combine(_root, "catalog.db");
        long fileId;
        using (SqliteLibraryCatalog catalog = CreateCatalog(databasePath))
        {
            LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
            LibraryScanHandle scan = catalog.BeginScan(location.Id);
            LibraryInventoryBatchResult batch = catalog.UpsertInventoryBatchDetailed(
                scan,
                new[] { Inventory(location.Path, "one.mkv", 1) },
                1);
            fileId = Assert.Single(batch.Mutations).FileId;
            Assert.Single(catalog.ClaimEnrichmentBatch(1, 1, "test-tool", DateTime.UtcNow));
        }

        using SqliteLibraryCatalog reopened = CreateCatalog(databasePath);
        Assert.Equal(1, reopened.RecoverInterruptedWork());
        Assert.Equal(0, reopened.GetOverview(1).ActiveScans);
        Assert.Equal(LibraryProbeStatus.Pending, reopened.GetMediaMetadata(fileId)?.ProbeStatus);
    }

    [Fact]
    public async Task PauseResumeAndBoundedQueueKeepDiscoveryControlled()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "large")));
        const int fileCount = 10_000;
        var fileSystem = new FakeFileSystem(location.Path,
            Enumerable.Range(0, fileCount).Select(index => Entry(Path.Combine(location.Path, $"video-{index:D5}.mkv"), index + 1)));
        var scanner = CreateScanner(
            catalog,
            fileSystem,
            new LibraryScanOptions(DiscoveryQueueCapacity: 8, BatchSize: 100, CollectStableFileIdentity: false));
        scanner.Pause();
        Task<LibraryScanResult> task = scanner.ScanLocationAsync(location.Id, 1);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);
        scanner.Resume();

        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();
        LibraryScanResult result = await task;
        stopwatch.Stop();
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        _output.WriteLine(
            "Scanned {0:N0} synthetic files in {1:N2}s; peak bounded in-flight count {2}; retained managed-memory delta {3:N0} bytes.",
            fileCount,
            stopwatch.Elapsed.TotalSeconds,
            result.PeakQueuedFiles,
            memoryAfter - memoryBefore);
        Assert.Equal(LibraryScanOutcome.Completed, result.Outcome);
        // The channel holds at most eight entries; the ninth is the single item
        // already handed to the consumer before its accounting decrement.
        Assert.InRange(result.PeakQueuedFiles, 1, 9);
        Assert.Equal(fileCount, catalog.GetCounts().Files);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.True(memoryAfter - memoryBefore < 64L * 1024 * 1024);
    }

    [Fact]
    public async Task MetadataQueueBackpressureDoesNotBlockInventoryScanWhileStorageIsReserved()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "mapped-library")));
        const int fileCount = 3_000;
        var fileSystem = new FakeFileSystem(location.Path,
            Enumerable.Range(0, fileCount).Select(index => Entry(Path.Combine(location.Path, $"video-{index:D5}.mkv"), index + 1)));
        var scheduler = new LibraryStorageScheduler(new ConstantStorageResolver());
        var probe = new FakeMetadataProbe(_ => SuccessfulProbe());
        await using var enrichment = new LibraryEnrichmentCoordinator(
            catalog,
            probe,
            new LibraryEnrichmentOptions(
                WorkerCount: 2,
                QueueCapacity: 128,
                PendingClaimBatchSize: 64,
                RetryPollInterval: TimeSpan.FromMinutes(5)),
            storageScheduler: scheduler);
        enrichment.Start();
        var updates = new ConcurrentQueue<LibraryScanProgress>();
        var diagnostics = new ConcurrentQueue<(string EventName, string Details)>();
        var scanner = new LibraryScanCoordinator(
            catalog,
            new[] { ".mkv" },
            fileSystem,
            new FakeIdentityProvider(),
            new LibraryScanOptions(DiscoveryQueueCapacity: 2_000, BatchSize: 500, CollectStableFileIdentity: false),
            enrichment,
            storageScheduler: scheduler,
            diagnosticLog: (eventName, details, _) => diagnostics.Enqueue((eventName, details)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        LibraryScanResult result = await scanner.ScanLocationAsync(
            location.Id,
            LibraryEnrichmentCoordinator.CurrentMetadataVersion,
            new InlineProgress<LibraryScanProgress>(updates.Enqueue),
            cancellationToken: timeout.Token);

        Assert.Equal(LibraryScanOutcome.Completed, result.Outcome);
        Assert.Equal(fileCount, result.DiscoveredFiles);
        Assert.Equal(fileCount, catalog.GetCounts().Files);
        await WaitUntilAsync(() => probe.CallCount > 128, TimeSpan.FromSeconds(5));
        Assert.Contains(updates, update => update.Stage == "Discovering files" && !string.IsNullOrWhiteSpace(update.CurrentPath));
        Assert.Contains(updates, update => update.Stage == "Indexing files" && update.WrittenFiles >= 500);
        Assert.Contains(updates, update => update.Stage == "Finalizing scan");
        Assert.Contains(updates, update => update.Stage == "Scan complete" && update.WrittenFiles == fileCount);
        Assert.Contains(updates, update => update.EnrichmentDeferredFiles > 0);
        Assert.Contains(diagnostics, item => item.EventName == "metadata queue saturated" && item.Details.Contains("Metadata work remains durable", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.EventName == "completed");
    }

    [Fact]
    public void LibraryAnalyzerActivityAreaShowsLiveCountsCurrentPathPauseAndCompletion()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SqliteLibraryCatalog catalog = CreateCatalog();
                using var runtime = new LibraryAnalyzerRuntime(
                    catalog,
                    new[] { ".mkv" },
                    new FakeMetadataProbe(_ => SuccessfulProbe()),
                    new EmptyVisualExtractor());
                using var form = new LibraryAnalyzerForm(runtime);
                form.Show();
                GetField<TabControl>(form, "_tabs").SelectedIndex = 1;
                Application.DoEvents();

                SetField(form, "_scanning", true);
                SetField(form, "_latestScanProgress", new LibraryScanProgress(
                    1,
                    "Discovering files",
                    @"Y:\Movies\Example.mkv",
                    2_432,
                    500,
                    500,
                    0,
                    0,
                    0,
                    0,
                    1_932,
                    128,
                    372,
                    false));
                InvokeActivityRefresh(form);

                var status = GetField<Label>(form, "_scanStatus");
                var detail = GetField<Label>(form, "_scanDetail");
                var progress = GetField<ProgressBar>(form, "_scanProgress");
                Assert.Contains("2,432 discovered", status.Text, StringComparison.Ordinal);
                Assert.Contains("500 indexed", status.Text, StringComparison.Ordinal);
                Assert.Contains("1,932 pending", status.Text, StringComparison.Ordinal);
                Assert.Contains(@"Y:\Movies\Example.mkv", detail.Text, StringComparison.Ordinal);
                Assert.Contains("372 metadata items deferred safely", detail.Text, StringComparison.Ordinal);
                Assert.True(progress.Visible);
                Assert.Equal(ProgressBarStyle.Marquee, progress.Style);

                runtime.Scanner.Pause();
                InvokeActivityRefresh(form);
                Assert.StartsWith("Paused:", status.Text, StringComparison.Ordinal);
                runtime.Scanner.Resume();

                SetField(form, "_scanning", false);
                SetField<LibraryScanProgress?>(form, "_latestScanProgress", null);
                SetField(form, "_scanTerminalStatus", "Completed: 2,432 files, 0 missing");
                InvokeActivityRefresh(form);
                Assert.Equal("Completed: 2,432 files, 0 missing", status.Text);
                Assert.False(progress.Visible);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The Library Analyzer UI smoke test did not finish.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException("The Library Analyzer UI smoke test failed.", failure);
    }

    [Fact]
    public async Task MetadataEnrichmentPersistsFullProbeAndIsReusedByUnchangedScan()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
        var fileSystem = new FakeFileSystem(location.Path, Entries(location.Path, ("movie.mkv", 1_000)));
        var probe = new FakeMetadataProbe(_ => SuccessfulProbe());
        await using var enrichment = CreateEnrichment(catalog, probe);
        enrichment.Start();
        var scanner = CreateScanner(catalog, fileSystem, enrichmentSink: enrichment);

        await scanner.ScanLocationAsync(location.Id, LibraryEnrichmentCoordinator.CurrentMetadataVersion);
        try
        {
            await WaitUntilAsync(() => probe.CallCount == 1 && !enrichment.IsRunning);
        }
        catch (TimeoutException)
        {
            LibraryOverview state = catalog.GetOverview(LibraryEnrichmentCoordinator.CurrentMetadataVersion);
            _output.WriteLine(
                "Metadata wait timed out: calls={0}, queued={1}, active={2}, running={3}, pending={4}",
                probe.CallCount,
                enrichment.QueuedCount,
                enrichment.ActiveCount,
                enrichment.IsRunning,
                state.PendingEnrichment);
            throw;
        }
        IndexedFileRecord file = Assert.IsType<IndexedFileRecord>(catalog.GetFileByPath(Path.Combine(location.Path, "movie.mkv")));
        LibraryMediaMetadata metadata = Assert.IsType<LibraryMediaMetadata>(catalog.GetMediaMetadata(file.Id));
        await scanner.ScanLocationAsync(location.Id, LibraryEnrichmentCoordinator.CurrentMetadataVersion);
        await Task.Delay(50);

        Assert.Equal(1, probe.CallCount);
        Assert.Equal(LibraryProbeStatus.Succeeded, metadata.ProbeStatus);
        Assert.Equal("matroska,webm", metadata.FormatName);
        Assert.Equal("hevc", metadata.VideoCodec);
        Assert.Equal("Main 10", metadata.VideoProfile);
        Assert.Equal(3840, metadata.Width);
        Assert.Equal(2160, metadata.Height);
        Assert.Equal(10, metadata.BitDepth);
        Assert.Equal(2, metadata.AudioStreams.Count);
        Assert.Single(metadata.SubtitleStreams);
        Assert.Equal(2, metadata.ChapterCount);
        Assert.Equal(1, metadata.AttachmentCount);
    }

    [Fact]
    public async Task FailedProbeRetriesAndEventuallySucceeds()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
        var fileSystem = new FakeFileSystem(location.Path, Entries(location.Path, ("movie.mkv", 1_000)));
        var probe = new FakeMetadataProbe(call => call == 1
            ? MediaProbeResult.Failed("temporary failure")
            : SuccessfulProbe());
        await using var enrichment = CreateEnrichment(
            catalog,
            probe,
            new LibraryEnrichmentOptions(
                WorkerCount: 1,
                QueueCapacity: 8,
                PendingClaimBatchSize: 4,
                MaxAttempts: 3,
                RetryBaseDelay: TimeSpan.FromMilliseconds(20),
                RetryPollInterval: TimeSpan.FromMilliseconds(10)));
        enrichment.Start();
        var scanner = CreateScanner(catalog, fileSystem, enrichmentSink: enrichment);

        await scanner.ScanLocationAsync(location.Id, 1);
        await WaitUntilAsync(() => probe.CallCount >= 2 && !enrichment.IsRunning, TimeSpan.FromSeconds(5));
        long fileId = Assert.IsType<IndexedFileRecord>(catalog.GetFileByPath(Path.Combine(location.Path, "movie.mkv"))).Id;
        LibraryMediaMetadata metadata = Assert.IsType<LibraryMediaMetadata>(catalog.GetMediaMetadata(fileId));

        Assert.Equal(2, probe.CallCount);
        Assert.Equal(2, metadata.AttemptCount);
        Assert.Equal(LibraryProbeStatus.Succeeded, metadata.ProbeStatus);
        Assert.Equal("", metadata.ErrorMessage);
    }

    [Fact]
    public void MetadataVersionAndSourceFactsInvalidateSuccessfulProbe()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(
            scan,
            new[] { Inventory(location.Path, "one.mkv", 100) },
            1).Mutations);
        DateTime modified = new DateTime(mutation.LastWriteUtcTicks, DateTimeKind.Utc);
        catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(
            new LibraryEnrichmentRequest(mutation.FileId, mutation.FullPath, "", 100, modified),
            SuccessfulProbe(), 1, "tool-v1", DateTime.UtcNow, null));

        Assert.Empty(catalog.ClaimEnrichmentBatch(10, 1, "tool-v1", DateTime.UtcNow));
        Assert.Single(catalog.ClaimEnrichmentBatch(10, 2, "tool-v1", DateTime.UtcNow));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SqliteLibraryCatalog CreateCatalog(string? path = null)
    {
        var catalog = new SqliteLibraryCatalog(
            path ?? Path.Combine(_root, "catalog.db"),
            Path.Combine(_root, "backups"),
            Path.Combine(_root, "recovery"));
        catalog.Initialize();
        return catalog;
    }

    private static LibraryScanCoordinator CreateScanner(
        ILibraryCatalog catalog,
        ILibraryFileSystem fileSystem,
        LibraryScanOptions? options = null,
        ILibraryEnrichmentSink? enrichmentSink = null) =>
        new(catalog, new[] { ".mkv", ".mp4" }, fileSystem, new FakeIdentityProvider(), options, enrichmentSink);

    private static LibraryEnrichmentCoordinator CreateEnrichment(
        ILibraryCatalog catalog,
        ILibraryMetadataProbe probe,
        LibraryEnrichmentOptions? options = null) =>
        new(catalog, probe, options ?? new LibraryEnrichmentOptions(
            WorkerCount: 1,
            QueueCapacity: 8,
            PendingClaimBatchSize: 4,
            RetryPollInterval: TimeSpan.FromSeconds(30)));

    private static IEnumerable<LibraryFileSystemEntry> Entries(
        string root,
        params (string Name, long Size)[] items) =>
        items.Select(item => Entry(Path.Combine(root, item.Name), item.Size));

    private static LibraryFileSystemEntry Entry(string path, long size) => new(
        path,
        size,
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(size));

    private static LibraryInventoryEntry Inventory(string root, string name, long size) => new(
        Path.Combine(root, name),
        name,
        size,
        new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

    private static MediaProbeResult SuccessfulProbe() => new()
    {
        Success = true,
        FormatName = "matroska,webm",
        DurationSeconds = 120,
        BitRate = 12_000_000,
        Chapters = new[] { new MediaProbeChapterInfo(), new MediaProbeChapterInfo() },
        Streams = new[]
        {
            new MediaProbeStreamInfo
            {
                CodecType = "video", CodecName = "hevc", Profile = "Main 10", Level = 153,
                Width = 3840, Height = 2160, FrameRate = 23.976, PixelFormat = "yuv420p10le",
                BitsPerRawSample = 10, FieldOrder = "progressive", ColorSpace = "bt2020nc",
                ColorTransfer = "smpte2084", ColorPrimaries = "bt2020"
            },
            new MediaProbeStreamInfo { CodecType = "audio", CodecName = "aac", Channels = 2, Language = "eng" },
            new MediaProbeStreamInfo { CodecType = "audio", CodecName = "ac3", Channels = 6, Language = "spa" },
            new MediaProbeStreamInfo { CodecType = "subtitle", CodecName = "subrip", Language = "eng" },
            new MediaProbeStreamInfo { CodecType = "attachment", CodecName = "ttf" }
        }
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The asynchronous test condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeFileSystem : ILibraryFileSystem
    {
        private IReadOnlyList<LibraryFileSystemEntry> _entries;
        public FakeFileSystem(string root, IEnumerable<LibraryFileSystemEntry> entries)
        {
            Root = root;
            _entries = entries.ToArray();
        }
        public string Root { get; set; }
        public bool Exists { get; set; } = true;
        public Exception? EnumerationError { get; set; }
        public bool DirectoryExists(string path) => Exists && string.Equals(path, Root, StringComparison.OrdinalIgnoreCase);
        public void SetEntries(IEnumerable<LibraryFileSystemEntry> entries) => _entries = entries.ToArray();
        public IEnumerable<LibraryFileSystemEntry> EnumerateFiles(
            string rootPath, bool recursive, Action<string, Exception> onError, CancellationToken cancellationToken)
        {
            foreach (LibraryFileSystemEntry entry in _entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
            if (EnumerationError != null)
                onError(rootPath, EnumerationError);
        }
    }

    private sealed class FakeIdentityProvider : ILibraryFileIdentityProvider
    {
        public LibraryFileIdentity GetIdentity(string path) =>
            new("fake-volume", Path.GetFileName(path).ToUpperInvariant());
    }

    private sealed class FakeMetadataProbe : ILibraryMetadataProbe
    {
        private readonly Func<int, MediaProbeResult> _result;
        private int _calls;
        public FakeMetadataProbe(Func<int, MediaProbeResult> result) => _result = result;
        public string ToolVersion => "fake-ffprobe-v1";
        public int CallCount => Volatile.Read(ref _calls);
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(_result(Interlocked.Increment(ref _calls)));
    }

    private sealed class ConstantStorageResolver : ILibraryStorageKeyResolver
    {
        public string ResolveStorageKey(string path, string reportedVolumeId = "") => "mapped-drive";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class EmptyVisualExtractor : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "fake-ffmpeg-v1";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }

    private static T GetField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().FullName, name));

    private static void SetField<T>(object instance, string name, T value) =>
        (instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name)).SetValue(instance, value);

    private static void InvokeActivityRefresh(LibraryAnalyzerForm form) =>
        (form.GetType().GetMethod("RefreshActivityDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(form.GetType().FullName, "RefreshActivityDisplay")).Invoke(form, null);
}
