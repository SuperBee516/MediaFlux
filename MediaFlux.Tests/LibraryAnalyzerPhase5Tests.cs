using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class LibraryAnalyzerPhase5Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-LibraryPhase5Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LibraryAnalyzerPhase5Tests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void MigrationCreatesVersionedVisualAndAccelerationSchema()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        Assert.Equal(5, catalog.GetDiagnostics().SchemaVersion);
        using var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}");
        connection.Open();
        string[] tables = { "visual_fingerprints", "visual_hash_bands", "visual_analysis_runs", "visual_candidate_pairs", "visual_similarity_groups", "visual_group_decisions", "location_scan_accelerators" };
        foreach (string table in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
        }
    }

    [Fact]
    public async Task VisualPipelineUsesBandedCandidatesAndPublishesReviewOnlyPairs()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual"); Directory.CreateDirectory(library);
        string a = Write(library, "original.mkv", 1000), b = Write(library, "reencode.mp4", 700), c = Write(library, "other.mkv", 900);
        AddInventoryAndMetadata(catalog, library, new[] { a, b, c }, path => path == b ? ("h264", 1280, 720, 120.5) : ("hevc", 1920, 1080, path == c ? 300d : 120d));
        ulong[] baseHashes = { 0x1234567890abcdef, 0x2234567890abcdef, 0x3234567890abcdef, 0x4234567890abcdef, 0x5234567890abcdef, 0x6234567890abcdef };
        var extractor = new FakeVisualExtractor(path => path == c ? Enumerable.Repeat(0xf0f0f0f00f0f0f0fUL, 6).ToArray() : path == b ? baseHashes.Select(hash => hash ^ 1UL).ToArray() : baseHashes);
        using var coordinator = new LibraryVisualAnalysisCoordinator(catalog, extractor, new LibraryVisualAnalysisOptions(2, 3, 16, 128, 3, 70));

        LibraryVisualAnalysisResult result = await coordinator.AnalyzeAsync();
        VisualSimilarityGroupRecord group = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);

        Assert.Equal(DuplicateAnalysisStatus.Completed, result.Status);
        Assert.Equal(3, result.FingerprintedFiles);
        Assert.Equal(1, result.MatchPairs);
        Assert.True(group.ConfidenceScore > 90);
        Assert.True(group.CodecDiffers);
        Assert.True(group.ResolutionDiffers);
        Assert.Contains("indexed band matches", group.EvidenceText);
        Assert.Equal(2, catalog.GetVisualGroupMembers(group.GroupId).Count);
    }

    [Fact]
    public async Task UnchangedVisualFingerprintsAreReusedAndChangedFilesInvalidateVisualResults()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "reuse"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", 800), b = Write(library, "b.mkv", 800);
        LibraryLocationRecord location = AddInventoryAndMetadata(catalog, library, new[] { a, b }, _ => ("hevc", 1920, 1080, 60d));
        ulong[] hashes = Enumerable.Range(0, 6).Select(index => (ulong)(0x1111111111111111 + index)).ToArray();
        var extractor = new FakeVisualExtractor(_ => hashes);
        using var coordinator = new LibraryVisualAnalysisCoordinator(catalog, extractor, new LibraryVisualAnalysisOptions(1, 8, 16, 128, 3, 70));

        await coordinator.AnalyzeAsync();
        Assert.Equal(2, extractor.CallCount);
        LibraryVisualAnalysisResult reused = await coordinator.AnalyzeAsync();
        Assert.Equal(0, reused.FingerprintedFiles);
        Assert.Equal(2, extractor.CallCount);

        var upgradedTool = new FakeVisualExtractor(_ => hashes, "fake-ffmpeg-v2");
        using (var upgradedCoordinator = new LibraryVisualAnalysisCoordinator(catalog, upgradedTool, new LibraryVisualAnalysisOptions(1, 8, 16, 128, 3, 70)))
            Assert.Equal(2, (await upgradedCoordinator.AnalyzeAsync()).FingerprintedFiles);

        File.AppendAllText(a, "changed");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddSeconds(2));
        FileInfo info = new(a);
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        catalog.UpsertInventoryBatchDetailed(scan, new[] { new LibraryInventoryEntry(a, "a.mkv", info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) }, 1);
        Assert.Null(catalog.GetVisualFingerprint(catalog.GetFileByPath(a)!.Id));
        Assert.Empty(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
    }

    [Fact]
    public async Task UsnQuietVolumeShortcutSkipsTraversalButResetFallsBackAuthoritatively()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string root = Path.Combine(_root, "journal"); Directory.CreateDirectory(root);
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        var fileSystem = new CountingFileSystem(root, new[] { Entry(root, "one.mkv", 100) });
        var journal = new FakeChangeJournal(new LibraryChangeJournalCheckpoint("C:", "NTFS", 7, 100, 0));
        var scanner = new LibraryScanCoordinator(catalog, new[] { ".mkv" }, fileSystem, new EmptyIdentityProvider(), accelerationCatalog: catalog, changeJournal: journal, storageScheduler: new LibraryStorageScheduler(new ConstantStorageResolver("disk")));

        Assert.Equal(LibraryScanOutcome.Completed, (await scanner.ScanLocationAsync(location.Id, 1)).Outcome);
        Assert.Equal(1, fileSystem.EnumerationCount);
        Assert.Equal(LibraryScanOutcome.Completed, (await scanner.ScanLocationAsync(location.Id, 1)).Outcome);
        Assert.Equal(1, fileSystem.EnumerationCount);

        journal.Checkpoint = journal.Checkpoint with { JournalId = 8, NextUsn = 1 };
        Assert.Equal(LibraryScanOutcome.Completed, (await scanner.ScanLocationAsync(location.Id, 1)).Outcome);
        Assert.Equal(2, fileSystem.EnumerationCount);
    }

    [Fact]
    public async Task JournalFallbackEnumerationFailureNeverReconcilesFilesMissing()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string root = Path.Combine(_root, "journal-failure"); Directory.CreateDirectory(root);
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        var fileSystem = new CountingFileSystem(root, new[] { Entry(root, "one.mkv", 100), Entry(root, "two.mkv", 200) });
        var journal = new FakeChangeJournal(new LibraryChangeJournalCheckpoint("C:", "NTFS", 9, 10, 0));
        var scanner = new LibraryScanCoordinator(catalog, new[] { ".mkv" }, fileSystem, new EmptyIdentityProvider(), accelerationCatalog: catalog, changeJournal: journal);
        await scanner.ScanLocationAsync(location.Id, 1);
        fileSystem.Entries = new[] { Entry(root, "one.mkv", 100) };
        fileSystem.Error = new UnauthorizedAccessException("permission changed");
        journal.Checkpoint = journal.Checkpoint with { NextUsn = 11 };

        LibraryScanResult result = await scanner.ScanLocationAsync(location.Id, 1);

        Assert.Equal(LibraryScanOutcome.Failed, result.Outcome);
        Assert.NotEqual(IndexedFileAvailability.Missing, catalog.GetFileByPath(Path.Combine(root, "two.mkv"))!.Availability);
    }

    [Fact]
    public async Task PhysicalStorageSchedulerSerializesAliasesAndAllowsIndependentDevices()
    {
        var scheduler = new LibraryStorageScheduler(new PrefixStorageResolver());
        int activeDiskA = 0, peakDiskA = 0;
        async Task Work(string path)
        {
            await using (await scheduler.AcquireAsync(path))
            {
                int now = Interlocked.Increment(ref activeDiskA);
                peakDiskA = Math.Max(peakDiskA, now);
                await Task.Delay(40);
                Interlocked.Decrement(ref activeDiskA);
            }
        }
        await Task.WhenAll(Work("A:one"), Work("A:two"));
        Assert.Equal(1, peakDiskA);

        var entered = new ConcurrentBag<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Independent(string path)
        {
            await using (await scheduler.AcquireAsync(path))
            {
                entered.Add(path);
                await release.Task;
            }
        }
        Task left = Independent("A:three"), right = Independent("B:one");
        await WaitUntilAsync(() => entered.Count == 2);
        release.SetResult();
        await Task.WhenAll(left, right);
    }

    [Fact]
    public void NetworkStorageAndUsnProvidersFailOverConservatively()
    {
        var resolver = new WindowsLibraryStorageKeyResolver();
        Assert.Equal("NETWORK:SERVER\\SHARE", resolver.ResolveStorageKey(@"\\server\share\movies\one.mkv"));
        var journal = new WindowsUsnChangeJournalProvider();
        Assert.False(journal.TryGetCheckpoint(@"\\server\share\movies", out _, out string error));
        Assert.Contains("network", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VisualCancellationPersistsReusableWorkAndRestartMarksRunningAnalysisInterrupted()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-cancel"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", 500);
        AddInventoryAndMetadata(catalog, library, new[] { a }, _ => ("hevc", 1920, 1080, 30d));
        var extractor = new BlockingVisualExtractor();
        using var coordinator = new LibraryVisualAnalysisCoordinator(catalog, extractor, new LibraryVisualAnalysisOptions(1, 8, 8));
        Task<LibraryVisualAnalysisResult> analysis = coordinator.AnalyzeAsync();
        await extractor.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.Cancel();
        LibraryVisualAnalysisResult canceled = await analysis;
        Assert.Equal(DuplicateAnalysisStatus.Canceled, canceled.Status);

        VisualAnalysisHandle interrupted = catalog.BeginVisualAnalysis(LibraryVisualAnalysisCoordinator.Algorithm, LibraryVisualAnalysisCoordinator.AlgorithmVersion);
        Assert.True(interrupted.RunId > 0);
        Assert.True(catalog.RecoverInterruptedVisualWork() >= 1);
        using var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM visual_analysis_runs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", interrupted.RunId);
        Assert.Equal((int)DuplicateAnalysisStatus.Interrupted, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void UserDecisionBackupRestoresOnlyDurableDecisionsWithoutTouchingMedia()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        using (var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText =
                """
                INSERT INTO duplicate_group_decisions VALUES(10,'sha256',1,X'0102','KEEP',1,1,1);
                INSERT INTO duplicate_file_protections VALUES('PATH','P:\\protected.mkv','test',1);
                INSERT INTO visual_group_decisions VALUES('VISUAL','KEEP',1,0,1);
                """;
            seed.ExecuteNonQuery();
        }
        string backup = catalog.CreateUserDataBackup(Path.Combine(_root, "decisions.db"));
        using (var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM duplicate_group_decisions; DELETE FROM duplicate_file_protections; DELETE FROM visual_group_decisions;";
            clear.ExecuteNonQuery();
        }

        LibraryUserDataRestoreResult restored = catalog.RestoreUserDataBackup(backup);

        Assert.Equal(1, restored.DuplicateDecisions);
        Assert.Equal(1, restored.FileProtections);
        Assert.Equal(1, restored.VisualDecisions);
        Assert.Contains(restored.Warnings, warning => warning.Contains("not re-executed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DecisionRestoreReattachesProtectionManualKeeperAndReviewState()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "decision-roundtrip"); Directory.CreateDirectory(library);
        string keeper = Write(library, "keeper.mkv", 90_000), duplicate = Write(library, "duplicate.mkv", 90_000);
        AddInventoryAndMetadata(catalog, library, new[] { keeper, duplicate }, _ => ("hevc", 1920, 1080, 60d));
        using var analysis = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 16 * 1024));
        await analysis.AnalyzeAsync();
        ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        long keeperId = catalog.GetFileByPath(keeper)!.Id;
        catalog.SetFileProtection(keeperId, true, "release validation");
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, keeperId, true, true));
        string backup = catalog.CreateUserDataBackup(Path.Combine(_root, "decision-roundtrip.db"));
        using (var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM duplicate_group_decisions; DELETE FROM duplicate_file_protections;";
            clear.ExecuteNonQuery();
        }

        catalog.RestoreUserDataBackup(backup);

        ExactDuplicateGroupRecord restored = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Reviewed: true, Ignored: true, Protected: true)).Groups);
        Assert.Equal(keeperId, restored.ManualKeeperFileId);
        Assert.Single(catalog.GetDuplicateGroupMembers(restored.GroupId), member => member.FileId == keeperId && member.IsProtected && member.IsManualKeeper);
        Assert.True(File.Exists(keeper));
        Assert.True(File.Exists(duplicate));
    }

    [Fact]
    public async Task CatalogWriteFailureFailsClosedWithoutMissingReconciliation()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string root = Path.Combine(_root, "write-failure"); Directory.CreateDirectory(root);
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        var fileSystem = new CountingFileSystem(root, new[] { Entry(root, "one.mkv", 100), Entry(root, "two.mkv", 200) });
        var scanner = new LibraryScanCoordinator(catalog, new[] { ".mkv" }, fileSystem, new EmptyIdentityProvider(), accelerationCatalog: null, changeJournal: null);
        Assert.Equal(LibraryScanOutcome.Completed, (await scanner.ScanLocationAsync(location.Id, 1)).Outcome);
        using (var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_inventory_write BEFORE INSERT ON indexed_files BEGIN SELECT RAISE(FAIL,'simulated catalog write failure'); END;";
            trigger.ExecuteNonQuery();
        }
        fileSystem.Entries = new[] { Entry(root, "one.mkv", 100) };

        LibraryScanResult failed = await scanner.ScanLocationAsync(location.Id, 1);

        Assert.Equal(LibraryScanOutcome.Failed, failed.Outcome);
        Assert.NotEqual(IndexedFileAvailability.Missing, catalog.GetFileByPath(Path.Combine(root, "two.mkv"))!.Availability);
        Assert.True(catalog.CheckIntegrity().IsHealthy);
    }

    [Fact]
    public void CatalogMillionRecordStressWhenEnabled()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("MEDIAFLUX_LIBRARY_STRESS_RECORDS"), out int count) || count < 100_000)
            return;
        using SqliteLibraryCatalog catalog = CreateCatalog(Path.Combine(_root, "stress.db"));
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "stress-root")));
        var stopwatch = Stopwatch.StartNew();
        using (var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandTimeout = 300;
            command.CommandText =
                """
                BEGIN IMMEDIATE;
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$count)
                INSERT INTO indexed_files(full_path,path_key,file_name,extension,size_bytes,last_write_utc_ticks,creation_utc_ticks,volume_id,file_identity,availability_state,last_seen_utc_ticks,created_utc_ticks,updated_utc_ticks)
                SELECT 'P:\\stress\\f'||x||'.mkv','P:\\STRESS\\F'||x||'.MKV','f'||x||'.mkv','.mkv',100000+x,1,1,'V',CAST(x AS TEXT),0,1,1,1 FROM n;
                INSERT INTO file_location_memberships(location_id,file_id,relative_path,relative_path_key,last_seen_generation,availability_state,last_seen_utc_ticks)
                SELECT $location,id,'f'||id||'.mkv','F'||id||'.MKV',1,0,1 FROM indexed_files;
                INSERT INTO visual_hash_bands(file_id,algorithm_version,band_index,band_key)
                SELECT id,1,0,id%65536 FROM indexed_files;
                COMMIT;
                """;
            command.Parameters.AddWithValue("$count", count);
            command.Parameters.AddWithValue("$location", location.Id);
            command.ExecuteNonQuery();
        }
        TimeSpan insertElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        LibraryStatistics statistics = catalog.GetLibraryStatistics(10);
        TimeSpan aggregateElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        LibraryFilePage page = catalog.QueryFiles(new LibraryFileQuery(SortColumn: "size", Descending: true, Offset: 500_000, Limit: 100));
        TimeSpan pagingElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        ExactDuplicateGroupPage exact = catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Limit: 100));
        TimeSpan exactElapsed = stopwatch.Elapsed;
        VisualAnalysisHandle visualRun = catalog.BeginVisualAnalysis(LibraryVisualAnalysisCoordinator.Algorithm, LibraryVisualAnalysisCoordinator.AlgorithmVersion);
        stopwatch.Restart();
        long visualCandidates = catalog.BuildVisualCandidatePairs(visualRun, 1, 128, 3);
        TimeSpan visualElapsed = stopwatch.Elapsed;
        catalog.CompleteVisualAnalysis(visualRun, new VisualAnalysisCompletion(DuplicateAnalysisStatus.Completed, count, 0, visualCandidates, 0, 0));
        catalog.Checkpoint(LibraryCatalogCheckpointMode.Truncate);
        long databaseBytes = new FileInfo(catalog.DatabasePath).Length;
        string metrics = $"{count:N0} files / {count * 3L:N0} rows: insert {insertElapsed.TotalSeconds:0.00}s, aggregates {aggregateElapsed.TotalMilliseconds:0}ms, offset page {pagingElapsed.TotalMilliseconds:0}ms, exact query {exactElapsed.TotalMilliseconds:0}ms, visual candidate query {visualElapsed.TotalMilliseconds:0}ms, database {databaseBytes / 1024d / 1024d:0.0} MiB";
        _output.WriteLine(metrics);
        Console.WriteLine(metrics);
        Assert.Equal(count, statistics.TotalFiles);
        Assert.Equal(100, page.Files.Count);
        Assert.Empty(exact.Groups);
        Assert.Equal(0, visualCandidates);
        Assert.True(insertElapsed < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task RealFfmpegVisualCalibrationWhenConfigured()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("MEDIAFLUX_FFMPEG_PATH") ?? "";
        string ffprobe = Environment.GetEnvironmentVariable("MEDIAFLUX_FFPROBE_PATH") ?? "";
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            return;

        string media = Path.Combine(_root, "real-ffmpeg");
        Directory.CreateDirectory(media);
        string original = Path.Combine(media, "original.mp4");
        string reencoded = Path.Combine(media, "reencoded.mp4");
        string unrelated = Path.Combine(media, "unrelated.mp4");
        await RunToolAsync(ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-t", "8", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", original);
        await RunToolAsync(ffmpeg, "-y", "-i", original, "-vf", "scale=256:144", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p", reencoded);
        await RunToolAsync(ffmpeg, "-y", "-f", "lavfi", "-i", "smptebars=size=320x180:rate=24", "-t", "8", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", unrelated);

        using SqliteLibraryCatalog catalog = CreateCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(media));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        var metadataProbe = new FfprobeLibraryMetadataProbe(Path.GetDirectoryName(ffprobe)!, ffprobe, timeout: TimeSpan.FromSeconds(30));
        foreach (string path in new[] { original, reencoded, unrelated })
        {
            FileInfo file = new(path);
            LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(scan,
                new[] { new LibraryInventoryEntry(path, Path.GetFileName(path), file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) }, 1).Mutations);
            MediaProbeResult probe = await metadataProbe.ProbeAsync(path, CancellationToken.None);
            Assert.True(probe.Success, probe.ErrorMessage);
            catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(
                new LibraryEnrichmentRequest(mutation.FileId, mutation.FullPath, "", file.Length, file.LastWriteTimeUtc),
                probe, 1, metadataProbe.ToolVersion, DateTime.UtcNow, null));
        }
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, 3, 0, 3, 0, 0, 0));

        var extractor = new FfmpegVisualFingerprintExtractor(Path.GetDirectoryName(ffmpeg)!, ffmpeg, TimeSpan.FromSeconds(30));
        using var coordinator = new LibraryVisualAnalysisCoordinator(catalog, extractor, new LibraryVisualAnalysisOptions(1, 3, 32));
        LibraryVisualAnalysisResult result = await coordinator.AnalyzeAsync();
        Assert.Equal(DuplicateAnalysisStatus.Completed, result.Status);
        string fingerprintErrors = string.Join(" | ", new[] { original, reencoded, unrelated }
            .Select(path => catalog.GetVisualFingerprint(catalog.GetFileByPath(path)!.Id)?.ErrorMessage)
            .Where(error => !string.IsNullOrWhiteSpace(error)));
        Assert.True(result.FingerprintedFiles == 3,
            $"Expected three real FFmpeg fingerprints but completed {result.FingerprintedFiles}; errors={result.ErrorCount}: {fingerprintErrors}");
        foreach (string path in new[] { original, reencoded, unrelated })
            Assert.Equal(6, catalog.GetVisualFingerprint(catalog.GetFileByPath(path)!.Id)!.FrameHashes.Count);

        VisualSimilarityGroupPage groups = catalog.QueryVisualGroups(new VisualGroupQuery(MinimumConfidence: 0));
        VisualSimilarityGroupRecord similar = Assert.Single(groups.Groups);
        string[] matchedPaths = catalog.GetVisualGroupMembers(similar.GroupId).Select(member => member.FullPath).ToArray();
        Assert.Contains(original, matchedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(reencoded, matchedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(unrelated, matchedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(similar.ConfidenceScore >= 76, $"Real re-encode confidence was {similar.ConfidenceScore:0.0}%.");
        Assert.Contains("aligned samples", similar.EvidenceText);

        VisualFingerprintCandidate cancellationCandidate = new(
            catalog.GetFileByPath(original)!.Id, original, original.ToUpperInvariant(), new FileInfo(original).Length,
            File.GetLastWriteTimeUtc(original), "", "", 8);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(cancellationCandidate, new CancellationToken(canceled: true)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private SqliteLibraryCatalog CreateCatalog(string? path = null)
    {
        var catalog = new SqliteLibraryCatalog(path ?? Path.Combine(_root, "catalog.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        catalog.Initialize();
        return catalog;
    }

    private static LibraryLocationRecord AddInventoryAndMetadata(SqliteLibraryCatalog catalog, string root, IReadOnlyList<string> paths, Func<string, (string Codec, int Width, int Height, double Duration)> metadata)
    {
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        foreach (string path in paths)
        {
            FileInfo file = new(path);
            LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(scan, new[] { new LibraryInventoryEntry(path, Path.GetRelativePath(root, path), file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) }, 1).Mutations);
            var values = metadata(path);
            var probe = new MediaProbeResult
            {
                Success = true,
                FormatName = Path.GetExtension(path),
                DurationSeconds = values.Duration,
                BitRate = 5_000_000,
                Streams = new[] { new MediaProbeStreamInfo { CodecType = "video", CodecName = values.Codec, Width = values.Width, Height = values.Height } }
            };
            catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(new LibraryEnrichmentRequest(mutation.FileId, mutation.FullPath, "", file.Length, file.LastWriteTimeUtc), probe, 1, "probe", DateTime.UtcNow, null));
        }
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, paths.Count, 0, paths.Count, 0, 0, 0));
        return location;
    }

    private static string Write(string root, string name, int bytes)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(index => (byte)index).ToArray());
        return path;
    }

    private static LibraryFileSystemEntry Entry(string root, string name, long size) =>
        new(Path.Combine(root, name), size, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(-2));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static async Task RunToolAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
        string stderr = await error;
        _ = await output;
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(executable)} exited with {process.ExitCode}: {stderr}");
    }

    private sealed class FakeVisualExtractor : ILibraryVisualFingerprintExtractor
    {
        private readonly Func<string, IReadOnlyList<ulong>> _factory;
        private int _calls;
        public FakeVisualExtractor(Func<string, IReadOnlyList<ulong>> factory, string toolVersion = "fake-ffmpeg-v1") { _factory = factory; ToolVersion = toolVersion; }
        public string ToolVersion { get; }
        public int CallCount => Volatile.Read(ref _calls);
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_factory(candidate.FullPath));
        }
    }

    private sealed class BlockingVisualExtractor : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "blocking-v1";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Array.Empty<ulong>();
        }
    }

    private sealed class CountingFileSystem : ILibraryFileSystem
    {
        private readonly string _root;
        public CountingFileSystem(string root, IReadOnlyList<LibraryFileSystemEntry> entries) { _root = root; Entries = entries; }
        public IReadOnlyList<LibraryFileSystemEntry> Entries { get; set; }
        public Exception? Error { get; set; }
        public int EnumerationCount { get; private set; }
        public bool DirectoryExists(string path) => string.Equals(path, _root, StringComparison.OrdinalIgnoreCase);
        public IEnumerable<LibraryFileSystemEntry> EnumerateFiles(string rootPath, bool recursive, Action<string, Exception> onError, CancellationToken cancellationToken)
        {
            EnumerationCount++;
            foreach (LibraryFileSystemEntry entry in Entries) yield return entry;
            if (Error != null) onError(rootPath, Error);
        }
    }

    private sealed class FakeChangeJournal : ILibraryChangeJournalProvider
    {
        public FakeChangeJournal(LibraryChangeJournalCheckpoint checkpoint) => Checkpoint = checkpoint;
        public LibraryChangeJournalCheckpoint Checkpoint { get; set; }
        public bool TryGetCheckpoint(string rootPath, out LibraryChangeJournalCheckpoint checkpoint, out string error) { checkpoint = Checkpoint; error = ""; return true; }
    }

    private sealed class EmptyIdentityProvider : ILibraryFileIdentityProvider
    {
        public LibraryFileIdentity GetIdentity(string path) => LibraryFileIdentity.Empty;
    }

    private sealed class ConstantStorageResolver : ILibraryStorageKeyResolver
    {
        private readonly string _key;
        public ConstantStorageResolver(string key) => _key = key;
        public string ResolveStorageKey(string path, string reportedVolumeId = "") => _key;
    }

    private sealed class PrefixStorageResolver : ILibraryStorageKeyResolver
    {
        public string ResolveStorageKey(string path, string reportedVolumeId = "") => path[..1];
    }
}
