using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MediaFlux.Services.LibraryCatalog;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class LibraryAnalyzerPhase4Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-LibraryPhase4Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LibraryAnalyzerPhase4Tests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_root); }

    [Fact]
    public async Task PipelineFiltersBySizeThenQuickFingerprintAndConfirmsOnlySha256Survivors()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "pipeline"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", Repeated(1, 220_000));
        string b = Write(library, "b.mkv", Repeated(1, 220_000));
        string eliminated = Write(library, "eliminated.mkv", Repeated(2, 220_000));
        string unique = Write(library, "unique.mkv", Repeated(3, 123_456));
        AddInventory(catalog, library, new[] { a, b, eliminated, unique });
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(2, 8, 64 * 1024));

        LibraryDuplicateAnalysisResult result = await coordinator.AnalyzeAsync();
        ExactDuplicateGroupPage groups = catalog.QueryDuplicateGroups(new DuplicateGroupQuery());

        Assert.Equal(DuplicateAnalysisStatus.Completed, result.Status);
        Assert.Equal(3, result.SizeCandidates);
        Assert.Equal(3, result.QuickHashed);
        Assert.Equal(2, result.FullHashed);
        Assert.Single(groups.Groups);
        Assert.Equal(2, groups.Groups[0].MemberCount);
        Assert.NotNull(catalog.GetFileHashFact(catalog.GetFileByPath(eliminated)!.Id)?.QuickHash);
        Assert.Null(catalog.GetFileHashFact(catalog.GetFileByPath(eliminated)!.Id)?.FullHash);
        Assert.Null(catalog.GetFileHashFact(catalog.GetFileByPath(unique)!.Id));
    }

    [Fact]
    public async Task UnchangedFilesReuseHashesAndChangedInventoryInvalidatesFactsAndGroup()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "reuse"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", Repeated(7, 160_000));
        string b = Write(library, "b.mkv", Repeated(7, 160_000));
        LibraryLocationRecord location = AddInventory(catalog, library, new[] { a, b });
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024));

        Assert.Equal(DuplicateAnalysisStatus.Completed, (await coordinator.AnalyzeAsync()).Status);
        LibraryDuplicateAnalysisResult reused = await coordinator.AnalyzeAsync();
        Assert.Equal(0, reused.QuickHashed);
        Assert.Equal(0, reused.FullHashed);

        AppendByte(a, 9);
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddSeconds(2));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        FileInfo info = new(a);
        catalog.UpsertInventoryBatchDetailed(scan, new[] { new LibraryInventoryEntry(a, "a.mkv", info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) }, 1);

        Assert.Null(catalog.GetFileHashFact(catalog.GetFileByPath(a)!.Id));
        Assert.Empty(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
    }

    [Fact]
    public async Task HardLinkIdentityDoesNotInflateReclaimableStorage()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "hardlinks"); Directory.CreateDirectory(library);
        byte[] content = Repeated(4, 140_000);
        string a = Write(library, "a.mkv", content), alias = Write(library, "alias.mkv", content), copy = Write(library, "copy.mkv", content);
        AddInventory(catalog, library, new[] { a, alias, copy }, path => path == copy ? ("V", "2") : ("V", "1"));
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024));
        await coordinator.AnalyzeAsync();

        ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        Assert.Equal(3, group.MemberCount);
        Assert.Equal(2, group.PhysicalCopyCount);
        Assert.Equal(content.Length, group.ReclaimableBytes);
        Assert.Single(catalog.GetDuplicateGroupMembers(group.GroupId), member => member.IsHardLinkAlias);
    }

    [Fact]
    public async Task DecisionsProtectionsAndPagedQueriesAreDurableAcrossRegrouping()
    {
        string database = Path.Combine(_root, "decisions.db");
        long groupId, fileId;
        using (var catalog = CreateCatalog(database))
        {
            string library = Path.Combine(_root, "decisions"); Directory.CreateDirectory(library);
            string a = Write(library, "a.mkv", Repeated(8, 120_000)), b = Write(library, "b.mkv", Repeated(8, 120_000));
            AddInventory(catalog, library, new[] { a, b });
            using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024));
            await coordinator.AnalyzeAsync();
            ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
            groupId = group.GroupId; fileId = catalog.GetFileByPath(b)!.Id;
            catalog.SetFileProtection(fileId, true, "test protection");
            catalog.SaveDuplicateDecision(new DuplicateGroupDecision(groupId, fileId, true, true));
            await coordinator.AnalyzeAsync();
            var cleanup = new LibraryDuplicateCleanupService(catalog, catalog);
            Assert.Throws<InvalidOperationException>(() => cleanup.CreatePlan(new[] { groupId }, DuplicateCleanupAction.Quarantine, Path.Combine(_root, "ignored-q")));
            string userBackup = catalog.CreateUserDataBackup();
            Assert.True(File.Exists(userBackup));
        }
        using (var reopened = CreateCatalog(database))
        {
            ExactDuplicateGroupRecord group = Assert.Single(reopened.QueryDuplicateGroups(new DuplicateGroupQuery(Reviewed: true, Ignored: true, Protected: true)).Groups);
            Assert.True(group.Reviewed); Assert.True(group.Ignored); Assert.Equal(fileId, group.ManualKeeperFileId); Assert.Equal(1, group.ProtectedMemberCount);
        }
    }

    [Fact]
    public async Task CleanupPlanKeepsOneCopyAndQuarantineRevalidatesSha256()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "cleanup"); Directory.CreateDirectory(library);
        string keeper = Write(library, "keeper.mkv", Repeated(5, 150_000)), candidate = Write(library, "candidate.mkv", Repeated(5, 150_000));
        AddInventory(catalog, library, new[] { keeper, candidate });
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024));
        await coordinator.AnalyzeAsync();
        ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, catalog.GetFileByPath(keeper)!.Id, true, false));
        string quarantine = Path.Combine(_root, "quarantine");
        var cleanup = new LibraryDuplicateCleanupService(catalog, catalog);

        DuplicateCleanupPlanSummary plan = cleanup.CreatePlan(new[] { group.GroupId }, DuplicateCleanupAction.Quarantine, quarantine);
        DuplicateCleanupPlanItemRecord item = Assert.Single(catalog.GetCleanupPlanItemsBatch(plan.PlanId, 0, 0, 10));
        Assert.Equal(candidate, item.SourcePath);
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);

        Assert.Equal(1, result.Succeeded); Assert.Equal(0, result.Excluded); Assert.True(File.Exists(keeper)); Assert.False(File.Exists(candidate));
        Assert.Single(Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories));
        using var auditConnection = new SqliteConnection($"Data Source={catalog.DatabasePath}"); auditConnection.Open();
        using SqliteCommand audit = auditConnection.CreateCommand(); audit.CommandText = "SELECT COUNT(*) FROM duplicate_cleanup_audit WHERE plan_id=$plan;"; audit.Parameters.AddWithValue("$plan", plan.PlanId);
        Assert.Equal(1, Convert.ToInt32(audit.ExecuteScalar()));
    }

    [Fact]
    public async Task CleanupExcludesChangedCandidateAndNeverRemovesKeeper()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "cleanup-stale"); Directory.CreateDirectory(library);
        string keeper = Write(library, "keeper.mkv", Repeated(6, 130_000)), candidate = Write(library, "candidate.mkv", Repeated(6, 130_000));
        AddInventory(catalog, library, new[] { keeper, candidate });
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024)); await coordinator.AnalyzeAsync();
        ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, catalog.GetFileByPath(keeper)!.Id, true, false));
        var cleanup = new LibraryDuplicateCleanupService(catalog, catalog);
        DuplicateCleanupPlanSummary plan = cleanup.CreatePlan(new[] { group.GroupId }, DuplicateCleanupAction.Quarantine, Path.Combine(_root, "stale-q"));
        AppendByte(candidate, 1);

        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);

        Assert.Equal(0, result.Succeeded); Assert.Equal(1, result.Excluded); Assert.True(File.Exists(keeper)); Assert.True(File.Exists(candidate));
        Assert.Equal(DuplicateCleanupItemStatus.Excluded, Assert.Single(catalog.GetCleanupPlan(plan.PlanId)!.Items).Status);
    }

    [Fact]
    public async Task ExactCleanupSupportsPermanentDeleteWithoutWeakeningShaValidation()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "cleanup-permanent"); Directory.CreateDirectory(library);
        string keeper = Write(library, "keeper.mkv", Repeated(16, 120_000)), candidate = Write(library, "candidate.mkv", Repeated(16, 120_000));
        AddInventory(catalog, library, new[] { keeper, candidate });
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024)); await coordinator.AnalyzeAsync();
        ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, catalog.GetFileByPath(keeper)!.Id, true, false));
        var cleanup = new LibraryDuplicateCleanupService(catalog, catalog);
        DuplicateCleanupPlanSummary plan = cleanup.CreatePlan(new[] { group.GroupId }, DuplicateCleanupAction.PermanentDelete);
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);
        Assert.Equal(1, result.Succeeded);
        Assert.True(File.Exists(keeper)); Assert.False(File.Exists(candidate));
    }

    [Fact]
    public void InterruptedDuplicateRunIsRecoveredAndSchemaContainsDecisionTables()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        catalog.BeginDuplicateAnalysis(ExactDuplicateHashService.QuickAlgorithm, 1, ExactDuplicateHashService.FullAlgorithm, 1);
        Assert.Equal(1, catalog.RecoverInterruptedDuplicateWork());
        Assert.Equal(0, catalog.RecoverInterruptedDuplicateWork());
        LibraryCatalogDiagnostics diagnostics = catalog.GetDiagnostics();
        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, diagnostics.SchemaVersion);
        Assert.True(catalog.CheckIntegrity(fullCheck: true).IsHealthy);
    }

    [Fact]
    public async Task PausedAnalysisCancelsWithoutLosingReusableWorkAndCanResume()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "cancel"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", Repeated(2, 200_000)), b = Write(library, "b.mkv", Repeated(2, 200_000));
        AddInventory(catalog, library, new[] { a, b });
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 2, 32 * 1024));
        coordinator.Pause();
        Task<LibraryDuplicateAnalysisResult> pending = coordinator.AnalyzeAsync();
        await Task.Delay(50);
        coordinator.Cancel();
        LibraryDuplicateAnalysisResult canceled = await pending;
        Assert.Equal(DuplicateAnalysisStatus.Canceled, canceled.Status);
        Assert.Empty(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);

        coordinator.Resume();
        LibraryDuplicateAnalysisResult resumed = await coordinator.AnalyzeAsync();
        Assert.Equal(DuplicateAnalysisStatus.Completed, resumed.Status);
        Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
    }

    [Fact]
    public async Task AnalyticsAggregatesCatalogFactsAndExactDuplicateStorageCorrectly()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "analytics"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", Repeated(3, 1000)), b = Write(library, "b.mkv", Repeated(3, 1000)), c = Write(library, "c.mp4", Repeated(9, 2000));
        AddInventory(catalog, library, new[] { a, b, c });
        foreach (string path in new[] { a, b, c })
        {
            IndexedFileRecord file = catalog.GetFileByPath(path)!;
            bool hevc = path != c;
            catalog.SaveMediaMetadata(new LibraryMediaMetadata(file.Id, 1, "test", LibraryProbeStatus.Succeeded, 1, null, DateTime.UtcNow, DateTime.UtcNow, file.SizeBytes, file.LastWriteTimeUtc,
                hevc ? "matroska" : "mov,mp4", 60, 1_000_000, hevc ? "hevc" : "h264", "", null, hevc ? 3840 : 1920, hevc ? 2160 : 1080, 24, "", hevc ? 10 : 8, "", "", "", hevc ? "smpte2084" : "bt709", hevc ? "bt2020" : "bt709", Array.Empty<LibraryAudioStreamMetadata>(), Array.Empty<LibrarySubtitleStreamMetadata>(), 0, 0, ""));
        }
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 1024));
        await coordinator.AnalyzeAsync();

        LibraryStatistics stats = catalog.GetLibraryStatistics();
        Assert.Equal(3, stats.TotalFiles); Assert.Equal(4000, stats.TotalBytes); Assert.Equal(1, stats.ExactDuplicateGroups); Assert.Equal(1000, stats.ReclaimableDuplicateBytes);
        Assert.Contains(stats.ByCodec, x => x.Label == "hevc" && x.FileCount == 2 && x.SizeBytes == 2000);
        Assert.Contains(stats.ByResolution, x => x.Label == "4K" && x.FileCount == 2);
        Assert.Contains(stats.ByDynamicRange, x => x.Label == "HDR" && x.FileCount == 2);
    }

    [Fact]
    public void LargeSyntheticCandidateAndAggregateQueriesRemainSetBasedAndBounded()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "synthetic");
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(library));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        const int count = 20_000;
        var stopwatch = Stopwatch.StartNew();
        for (int offset = 0; offset < count; offset += 500)
        {
            var batch = Enumerable.Range(offset, Math.Min(500, count - offset)).Select(i => new LibraryInventoryEntry(
                Path.Combine(library, $"video-{i:D6}.mkv"), $"video-{i:D6}.mkv", i % 10 == 0 ? 999_999 : i + 10, new DateTime(638900000000000000L + i, DateTimeKind.Utc))).ToArray();
            catalog.UpsertInventoryBatch(scan, batch);
        }
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, count, 0, count, 0, 0, 0));
        long candidates = catalog.CountSizeCandidates();
        LibraryStatistics stats = catalog.GetLibraryStatistics(10);
        stopwatch.Stop();
        _output.WriteLine("Inserted and aggregated {0:N0} files in {1:N2}s; database {2:N2} MB.", count, stopwatch.Elapsed.TotalSeconds, new FileInfo(catalog.DatabasePath).Length / 1024d / 1024d);
        Assert.Equal(2000, candidates); Assert.Equal(count, stats.TotalFiles); Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void LargeSyntheticDuplicateGroupsUseBatchedEvidenceAndPagedResults()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "duplicate-pages");
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(library));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        const int fileCount = 5_000;
        var candidates = new List<LibraryHashCandidate>(fileCount);
        for (int offset = 0; offset < fileCount; offset += 500)
        {
            var entries = Enumerable.Range(offset, Math.Min(500, fileCount - offset)).Select(i => new LibraryInventoryEntry(
                Path.Combine(library, $"copy-{i:D5}.mkv"), $"copy-{i:D5}.mkv", 10_000 + i / 2, new DateTime(638910000000000000L + i, DateTimeKind.Utc))).ToArray();
            LibraryInventoryBatchResult result = catalog.UpsertInventoryBatchDetailed(scan, entries, 1);
            candidates.AddRange(result.Mutations.Select(m => new LibraryHashCandidate(m.FileId, m.FullPath, m.FullPath.ToUpperInvariant(), m.SizeBytes, new DateTime(m.LastWriteUtcTicks, DateTimeKind.Utc), "", "")));
        }
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, fileCount, 0, fileCount, 0, 0, 0));
        long memoryBefore = GC.GetTotalMemory(true);
        var stopwatch = Stopwatch.StartNew();
        var writes = candidates.Select((candidate, index) => new LibraryHashWrite(candidate, System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(index / 2)))).ToArray();
        catalog.SaveHashBatch(writes, LibraryHashKind.FullSha256, ExactDuplicateHashService.FullAlgorithm, ExactDuplicateHashService.FullVersion);
        DuplicateAnalysisHandle run = catalog.BeginDuplicateAnalysis(ExactDuplicateHashService.QuickAlgorithm, 1, ExactDuplicateHashService.FullAlgorithm, 1);
        long groups = catalog.RebuildExactDuplicateGroups(run, ExactDuplicateHashService.FullAlgorithm, 1);
        catalog.CompleteDuplicateAnalysis(run, new DuplicateAnalysisCompletion(DuplicateAnalysisStatus.Completed, fileCount, 0, fileCount, groups, 0));
        ExactDuplicateGroupPage first = catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Limit: 100));
        ExactDuplicateGroupPage second = catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Offset: 100, Limit: 100));
        stopwatch.Stop();
        long memoryAfter = GC.GetTotalMemory(true);
        _output.WriteLine("Persisted and grouped {0:N0} hash facts into {1:N0} paged groups in {2:N2}s; retained memory delta {3:N0} bytes; logical database {4:N2} MB.", fileCount, groups, stopwatch.Elapsed.TotalSeconds, memoryAfter - memoryBefore, catalog.GetDiagnostics().DatabaseBytes / 1024d / 1024d);
        Assert.Equal(2_500, groups); Assert.Equal(2_500, first.TotalCount); Assert.Equal(100, first.Groups.Count); Assert.Equal(100, second.Groups.Count);
        Assert.Empty(first.Groups.Select(x => x.GroupId).Intersect(second.Groups.Select(x => x.GroupId)));
        Assert.True(memoryAfter - memoryBefore < 64L * 1024 * 1024); Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task BoundedHashWorkersReportRealFileThroughputWithoutHashingEliminatedCandidateFully()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "hash-throughput"); Directory.CreateDirectory(library);
        const int bytes = 8 * 1024 * 1024;
        byte[] duplicate = Repeated(11, bytes);
        byte[] different = Repeated(12, bytes);
        string a = Write(library, "a.mkv", duplicate), b = Write(library, "b.mkv", duplicate), c = Write(library, "c.mkv", different);
        AddInventory(catalog, library, new[] { a, b, c });
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(2, 8, 64 * 1024));
        long memoryBefore = GC.GetTotalMemory(true);
        var stopwatch = Stopwatch.StartNew();
        LibraryDuplicateAnalysisResult result = await coordinator.AnalyzeAsync();
        stopwatch.Stop();
        long memoryAfter = GC.GetTotalMemory(true);
        double fullMiB = 2d * bytes / 1024 / 1024;
        _output.WriteLine("Quick-filtered 3 x 8 MiB candidates and full-hashed {0:N0} MiB in {1:N2}s ({2:N1} MiB/s end-to-end); retained memory delta {3:N0} bytes.", fullMiB, stopwatch.Elapsed.TotalSeconds, fullMiB / stopwatch.Elapsed.TotalSeconds, memoryAfter - memoryBefore);
        Assert.Equal(3, result.QuickHashed); Assert.Equal(2, result.FullHashed); Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        Assert.True(memoryAfter - memoryBefore < 32L * 1024 * 1024);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private SqliteLibraryCatalog CreateCatalog(string? path = null) { var c = new SqliteLibraryCatalog(path ?? Path.Combine(_root, $"{Guid.NewGuid():N}.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery")); c.Initialize(); return c; }
    private LibraryLocationRecord AddInventory(SqliteLibraryCatalog catalog, string root, IReadOnlyList<string> paths, Func<string, (string Volume, string Identity)>? identity = null)
    {
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root)); LibraryScanHandle scan = catalog.BeginScan(location.Id);
        var entries = paths.Select(path => { FileInfo f = new(path); (string volume, string id) = identity?.Invoke(path) ?? ("", ""); return new LibraryInventoryEntry(path, Path.GetRelativePath(root, path), f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc, volume, id); }).ToArray();
        catalog.UpsertInventoryBatchDetailed(scan, entries, 1); catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, entries.Length, 0, entries.Length, 0, 0, 0)); return location;
    }
    private static string Write(string root, string name, byte[] content) { string path = Path.Combine(root, name); File.WriteAllBytes(path, content); return path; }
    private static byte[] Repeated(byte value, int count) => Enumerable.Repeat(value, count).ToArray();
    private static void AppendByte(string path, byte value) { using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None); stream.WriteByte(value); }
}
