using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
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
        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, catalog.GetDiagnostics().SchemaVersion);
        using var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}");
        connection.Open();
        string[] tables = { "visual_fingerprints", "visual_hash_bands", "visual_analysis_runs", "visual_candidate_pairs", "visual_similarity_groups", "visual_group_decisions", "location_scan_accelerators", "visual_cleanup_plans", "visual_cleanup_plan_items", "visual_cleanup_audit" };
        foreach (string table in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
        }
        using SqliteCommand columns = connection.CreateCommand();
        columns.CommandText = "SELECT (SELECT COUNT(*) FROM pragma_table_info('visual_group_decisions') WHERE name='not_match') + (SELECT COUNT(*) FROM pragma_table_info('visual_cleanup_plan_items') WHERE name='cleanup_intent');";
        Assert.Equal(2, Convert.ToInt32(columns.ExecuteScalar()));
    }

    [Fact]
    public void VersionFiveCatalogMigratesCleanupHistoryAndAllowsPermanentActions()
    {
        string path = Path.Combine(_root, "v5-upgrade.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            LibraryCatalogMigrations.Apply(connection, 0, 5, LibraryCatalogDatabase.ApplicationId);
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = "INSERT INTO duplicate_cleanup_plans(action,status,created_utc_ticks) VALUES(0,2,1);";
            seed.ExecuteNonQuery();
        }
        using SqliteLibraryCatalog catalog = CreateCatalog(path);
        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, catalog.GetDiagnostics().SchemaVersion);
        using var verify = new SqliteConnection($"Data Source={path}"); verify.Open();
        using SqliteCommand query = verify.CreateCommand();
        query.CommandText = "INSERT INTO duplicate_cleanup_plans(action,status,created_utc_ticks) VALUES(2,2,2); SELECT action FROM duplicate_cleanup_plans ORDER BY id;";
        using SqliteDataReader reader = query.ExecuteReader();
        var values = new List<long>(); while (reader.Read()) values.Add(reader.GetInt64(0));
        Assert.Equal(new long[] { 0, 2 }, values);
    }

    [Fact]
    public async Task VisualCleanupDefaultsToReviewedOnlyAndPermanentDeleteRevalidatesEvidence()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-cleanup"); Directory.CreateDirectory(library);
        string a = Write(library, "a-original.mkv", 1200), b = Write(library, "a-reencode.mp4", 800);
        string c = Write(library, "b-original.mkv", 1100), d = Write(library, "b-reencode.mp4", 700);
        AddInventoryAndMetadata(catalog, library, new[] { a, b, c, d }, path => path == a || path == c ? ("hevc", 1920, 1080, 60d) : ("h264", 1280, 720, 60d));
        ulong[] first = Enumerable.Range(0, 6).Select(i => 0x1111111111111111UL + (ulong)i).ToArray();
        ulong[] second = Enumerable.Range(0, 6).Select(i => 0xaaaaaaaaaaaaaaaaUL + (ulong)i).ToArray();
        using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(path => path == a || path == b ? first : second), new LibraryVisualAnalysisOptions(1, 4, 16, 128, 3, 70)))
            Assert.Equal(2, (await analysis.AnalyzeAsync()).MatchPairs);
        VisualSimilarityGroupRecord[] groups = catalog.QueryVisualGroups(new VisualGroupQuery()).Groups.ToArray();
        VisualSimilarityGroupRecord reviewed = groups[0];
        IReadOnlyList<VisualSimilarityMemberRecord> reviewedMembers = catalog.GetVisualGroupMembers(reviewed.GroupId);
        VisualSimilarityMemberRecord keeper = reviewedMembers.OrderByDescending(x => (x.Width ?? 0) * (x.Height ?? 0)).First();
        catalog.SaveVisualDecision(new VisualGroupDecision(reviewed.GroupId, keeper.FileId, true, false));
        var cleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog);

        VisualCleanupProposal conservative = cleanup.BuildProposal();
        Assert.Single(conservative.Items);
        Assert.True(conservative.Items[0].Group.Reviewed);
        Assert.Equal(2, cleanup.BuildProposal(includeUnreviewed: true, minimumConfidence: 90).Items.Count);

        VisualCleanupPlanRecord plan = cleanup.CreatePlan(conservative.Items, DuplicateCleanupAction.PermanentDelete);
        VisualCleanupPlanItemRecord item = Assert.Single(plan.Items);
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);
        Assert.Equal(1, result.Succeeded);
        Assert.True(File.Exists(item.KeeperPath));
        Assert.False(File.Exists(item.SourcePath));
        using var db = new SqliteConnection($"Data Source={catalog.DatabasePath}"); db.Open();
        using SqliteCommand audit = db.CreateCommand(); audit.CommandText = "SELECT COUNT(*) FROM visual_cleanup_audit WHERE plan_id=$id;"; audit.Parameters.AddWithValue("$id", plan.PlanId);
        Assert.Equal(1, Convert.ToInt32(audit.ExecuteScalar()));
    }

    [Fact]
    public async Task VisualCleanupExcludesProtectedAndChangedCandidatesWithoutDeletingKeeper()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-cleanup-revalidate"); Directory.CreateDirectory(library);
        string keeperPath = Write(library, "keeper.mkv", 1000), candidatePath = Write(library, "candidate.mp4", 800);
        AddInventoryAndMetadata(catalog, library, new[] { keeperPath, candidatePath }, path => path == keeperPath ? ("hevc", 1920, 1080, 60d) : ("h264", 1280, 720, 60d));
        ulong[] hashes = Enumerable.Range(0, 6).Select(i => 0x1234567890abcdefUL + (ulong)i).ToArray();
        using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes), new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70))) await analysis.AnalyzeAsync();
        VisualSimilarityGroupRecord group = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
        catalog.SaveVisualDecision(new VisualGroupDecision(group.GroupId, catalog.GetFileByPath(keeperPath)!.Id, true, false));
        var cleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog);
        VisualCleanupProposal proposal = cleanup.BuildProposal();
        VisualCleanupPlanRecord plan = cleanup.CreatePlan(proposal.Items, DuplicateCleanupAction.PermanentDelete);
        File.AppendAllText(candidatePath, "changed");
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);
        Assert.Equal(0, result.Succeeded); Assert.Equal(1, result.Excluded);
        Assert.True(File.Exists(keeperPath)); Assert.True(File.Exists(candidatePath));

        catalog.SetFileProtection(catalog.GetFileByPath(candidatePath)!.Id, true, "test");
        Assert.Empty(cleanup.BuildProposal().Items);
    }

    [Fact]
    public async Task VisualRecycleBatchAuditsPartialFailureAndRevalidatesEachRemainingItem()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-recycle-partial"); Directory.CreateDirectory(library);
        string a = Write(library, "a1.mkv", 1000), b = Write(library, "a2.mp4", 800), c = Write(library, "b1.mkv", 900), d = Write(library, "b2.mp4", 700);
        AddInventoryAndMetadata(catalog, library, new[] { a, b, c, d }, path => path == a || path == c ? ("hevc", 1920, 1080, 60d) : ("h264", 1280, 720, 60d));
        ulong[] first = Enumerable.Range(0, 6).Select(i => 0x0101010101010101UL + (ulong)i).ToArray();
        ulong[] second = Enumerable.Range(0, 6).Select(i => 0xf0f0f0f0f0f0f0f0UL + (ulong)i).ToArray();
        using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(path => path == a || path == b ? first : second), new LibraryVisualAnalysisOptions(1, 4, 16, 128, 3, 70))) await analysis.AnalyzeAsync();
        foreach (VisualSimilarityGroupRecord group in catalog.QueryVisualGroups(new VisualGroupQuery()).Groups)
        {
            VisualSimilarityMemberRecord keeper = catalog.GetVisualGroupMembers(group.GroupId).OrderByDescending(x => (x.Width ?? 0) * (x.Height ?? 0)).First();
            catalog.SaveVisualDecision(new VisualGroupDecision(group.GroupId, keeper.FileId, true, false));
        }
        var actions = new FakeCleanupActions();
        var cleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog, actions, new EmptyIdentityProvider());
        VisualCleanupProposal proposal = cleanup.BuildProposal();
        Assert.Equal(2, proposal.Items.Count);
        actions.FailPath = proposal.Items[0].Candidate.FullPath;
        VisualCleanupPlanRecord plan = cleanup.CreatePlan(proposal.Items, DuplicateCleanupAction.RecycleBin);
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);
        Assert.Equal(1, result.Succeeded); Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(actions.FailPath));
        Assert.False(File.Exists(proposal.Items[1].Candidate.FullPath));
        Assert.All(proposal.Items, item => Assert.True(File.Exists(item.Keeper.FullPath)));
        using var db = new SqliteConnection($"Data Source={catalog.DatabasePath}"); db.Open();
        using SqliteCommand audit = db.CreateCommand(); audit.CommandText = "SELECT COUNT(*) FROM visual_cleanup_audit WHERE plan_id=$id;"; audit.Parameters.AddWithValue("$id", plan.PlanId);
        Assert.Equal(2, Convert.ToInt32(audit.ExecuteScalar()));
    }

    [Fact]
    public async Task VisualCleanupReusesCurrentExactHashEvidenceWithoutWeakeningVisualRules()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-exact-evidence"); Directory.CreateDirectory(library);
        string a = Write(library, "copy-a.mkv", 150_000), b = Path.Combine(library, "copy-b.mkv"); File.Copy(a, b);
        AddInventoryAndMetadata(catalog, library, new[] { a, b }, _ => ("hevc", 1920, 1080, 60d));
        using (var exact = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024))) await exact.AnalyzeAsync();
        ulong[] hashes = Enumerable.Range(0, 6).Select(i => 0xabcdef1234567890UL + (ulong)i).ToArray();
        using (var visual = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes), new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70))) await visual.AnalyzeAsync();
        VisualSimilarityGroupRecord group = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
        catalog.SaveVisualDecision(new VisualGroupDecision(group.GroupId, catalog.GetFileByPath(a)!.Id, true, false));
        var cleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog);
        VisualCleanupProposalItem item = Assert.Single(cleanup.BuildProposal().Items);
        Assert.True(item.HasExactEvidence);
        Assert.NotNull(item.ExactHash);
    }

    [Fact]
    public async Task NotMatchDecisionSurvivesVisualReanalysisAndCanBeRestored()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-not-match"); Directory.CreateDirectory(library);
        string first = Write(library, "first.mkv", 1000), second = Write(library, "second.mp4", 800);
        AddInventoryAndMetadata(catalog, library, new[] { first, second }, _ => ("hevc", 1920, 1080, 60d));
        ulong[] hashes = Enumerable.Range(0, 6).Select(i => 0x1010101010101010UL + (ulong)i).ToArray();
        using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes), new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70)))
            await analysis.AnalyzeAsync();
        VisualSimilarityGroupRecord group = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery(NotMatch: false)).Groups);
        catalog.SaveVisualDecision(new VisualGroupDecision(group.GroupId, null, true, false, true));

        Assert.Empty(catalog.QueryVisualGroups(new VisualGroupQuery(NotMatch: false)).Groups);
        Assert.True(Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery(NotMatch: true)).Groups).NotMatch);
        Assert.Empty(new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog).BuildProposal(includeUnreviewed: true).Items);

        using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes), new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70)))
            await analysis.AnalyzeAsync();
        VisualSimilarityGroupRecord restored = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery(NotMatch: true)).Groups);
        catalog.SaveVisualDecision(new VisualGroupDecision(restored.GroupId, restored.ManualKeeperFileId, restored.Reviewed, restored.Ignored, false));
        Assert.False(Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery(NotMatch: false)).Groups).NotMatch);
    }

    [Fact]
    public async Task DeleteBothUsesDurablePlanRevalidatesBothFilesAndAuditsEachAction()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-delete-both"); Directory.CreateDirectory(library);
        string first = Write(library, "unwanted-a.mkv", 1000), second = Write(library, "unwanted-b.mp4", 800);
        AddInventoryAndMetadata(catalog, library, new[] { first, second }, _ => ("hevc", 1920, 1080, 60d));
        ulong[] hashes = Enumerable.Range(0, 6).Select(i => 0x2020202020202020UL + (ulong)i).ToArray();
        using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes), new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70)))
            await analysis.AnalyzeAsync();
        VisualSimilarityGroupRecord group = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
        var actions = new FakeCleanupActions();
        var cleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog, actions, new EmptyIdentityProvider());

        long protectedId = catalog.GetFileByPath(second)!.Id;
        catalog.SetFileProtection(protectedId, true, "test protection");
        Assert.Empty(cleanup.BuildDeleteBothProposal(group.GroupId).Items);
        catalog.SetFileProtection(protectedId, false, "");

        VisualCleanupProposalItem proposal = Assert.Single(cleanup.BuildDeleteBothProposal(group.GroupId).Items);
        Assert.Equal(VisualCleanupIntent.DeleteBoth, proposal.Intent);
        Assert.Equal(new FileInfo(first).Length + new FileInfo(second).Length, proposal.ReclaimableBytes);
        VisualCleanupPlanRecord plan = cleanup.CreatePlan(new[] { proposal }, DuplicateCleanupAction.RecycleBin);
        Assert.Equal(VisualCleanupIntent.DeleteBoth, Assert.Single(plan.Items).Intent);
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);

        Assert.Equal(2, result.Succeeded);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        using var db = new SqliteConnection($"Data Source={catalog.DatabasePath}"); db.Open();
        using SqliteCommand audit = db.CreateCommand(); audit.CommandText = "SELECT COUNT(*) FROM visual_cleanup_audit WHERE plan_id=$id;"; audit.Parameters.AddWithValue("$id", plan.PlanId);
        Assert.Equal(2, Convert.ToInt32(audit.ExecuteScalar()));
    }

    [Fact]
    public async Task DeleteBothPreflightFailureLeavesBothFilesInPlace()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "visual-delete-both-preflight"); Directory.CreateDirectory(library);
        string first = Write(library, "first.mkv", 900), second = Write(library, "second.mp4", 700);
        AddInventoryAndMetadata(catalog, library, new[] { first, second }, _ => ("h264", 1920, 1080, 60d));
        ulong[] hashes = Enumerable.Range(0, 6).Select(i => 0x3030303030303030UL + (ulong)i).ToArray();
        using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes), new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70)))
            await analysis.AnalyzeAsync();
        VisualSimilarityGroupRecord group = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
        var cleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog, new FakeCleanupActions(), new EmptyIdentityProvider());
        VisualCleanupPlanRecord plan = cleanup.CreatePlan(cleanup.BuildDeleteBothProposal(group.GroupId).Items, DuplicateCleanupAction.RecycleBin);
        File.AppendAllText(second, "changed after preview");

        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Excluded);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
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
                INSERT INTO visual_group_decisions(group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks,not_match) VALUES('VISUAL','KEEP',1,0,1,1);
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
        using var verify = new SqliteConnection($"Data Source={catalog.DatabasePath}"); verify.Open();
        using SqliteCommand notMatch = verify.CreateCommand(); notMatch.CommandText = "SELECT not_match FROM visual_group_decisions WHERE group_key='VISUAL';";
        Assert.Equal(1, Convert.ToInt32(notMatch.ExecuteScalar()));
    }

    [Fact]
    public void VersionSixDecisionBackupRestoresVisualDecisionsAsActiveMatches()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string backup = Path.Combine(_root, "v6-user-decisions.db");
        using (var connection = new SqliteConnection($"Data Source={backup}"))
        {
            connection.Open();
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText =
                """
                CREATE TABLE backup_manifest(created_utc_ticks INTEGER NOT NULL,source_schema_version INTEGER NOT NULL);
                INSERT INTO backup_manifest VALUES(1,6);
                CREATE TABLE duplicate_group_decisions(size_bytes INTEGER,full_algorithm TEXT,full_version INTEGER,full_hash BLOB,manual_keeper_path_key TEXT,reviewed INTEGER,ignored INTEGER,updated_utc_ticks INTEGER);
                CREATE TABLE duplicate_file_protections(path_key TEXT,protected_path TEXT,reason TEXT,updated_utc_ticks INTEGER);
                CREATE TABLE visual_group_decisions(group_key TEXT,manual_keeper_path_key TEXT,reviewed INTEGER,ignored INTEGER,updated_utc_ticks INTEGER);
                INSERT INTO visual_group_decisions VALUES('OLD-VISUAL','',1,0,1);
                """;
            seed.ExecuteNonQuery();
        }

        LibraryUserDataRestoreResult result = catalog.RestoreUserDataBackup(backup);

        Assert.Equal(1, result.VisualDecisions);
        using var verify = new SqliteConnection($"Data Source={catalog.DatabasePath}"); verify.Open();
        using SqliteCommand query = verify.CreateCommand();
        query.CommandText = "SELECT reviewed,ignored,not_match FROM visual_group_decisions WHERE group_key='OLD-VISUAL';";
        using SqliteDataReader reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
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

    [Fact]
    public void VisualReviewWorkflowSupportsComparisonPlaybackNavigationMenusAndDurableDecisions()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                SqliteLibraryCatalog catalog = CreateCatalog(Path.Combine(_root, "visual-review.db"));
                string library = Path.Combine(_root, "visual-review");
                Directory.CreateDirectory(library);
                string a = Write(library, "first-original.mkv", 800);
                string b = Write(library, "first-reencode.mp4", 700);
                string c = Write(library, "second-original.mkv", 900);
                string d = Write(library, "second-reencode.mp4", 650);
                AddInventoryAndMetadata(catalog, library, new[] { a, b, c, d }, path =>
                    path == a || path == b ? ("h264", 1920, 1080, 60d) : ("hevc", 1280, 720, 120d));
                ulong[] firstHashes = { 0x1111111111111111, 0x2111111111111111, 0x3111111111111111, 0x4111111111111111, 0x5111111111111111, 0x6111111111111111 };
                ulong[] secondHashes = { 0xaaaaaaaaaaaaaaaa, 0xbaaaaaaaaaaaaaaa, 0xcaaaaaaaaaaaaaaa, 0xdaaaaaaaaaaaaaaa, 0xeaaaaaaaaaaaaaaa, 0xfaaaaaaaaaaaaaaa };
                var extractor = new FakeVisualExtractor(path => path == a || path == b ? firstHashes : secondHashes);
                using (var analysis = new LibraryVisualAnalysisCoordinator(catalog, extractor, new LibraryVisualAnalysisOptions(1, 3, 16, 128, 3, 70)))
                    Assert.Equal(2, analysis.AnalyzeAsync().GetAwaiter().GetResult().MatchPairs);

                var played = new ConcurrentQueue<string>();
                using var runtime = new LibraryAnalyzerRuntime(
                    catalog,
                    new[] { ".mkv", ".mp4" },
                    new EmptyMetadataProbe(),
                    extractor);
                using var form = new LibraryAnalyzerForm(
                    runtime,
                    reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(VideoLauncher: played.Enqueue, PreviewCacheRoot: _root));
                form.Show();
                TabControl tabs = GetPrivateField<TabControl>(form, "_tabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Duplicates — Visual");
                form.Size = form.MinimumSize;
                Application.DoEvents();
                Button apply = GetPrivateField<Button>(form, "_visualApplyButton");
                TableLayoutPanel controlArea = GetPrivateField<TableLayoutPanel>(form, "_visualControlArea");
                Rectangle tabBounds = tabs.SelectedTab.RectangleToScreen(tabs.SelectedTab.ClientRectangle);
                Assert.True(apply.Visible);
                Assert.True(tabBounds.Contains(apply.RectangleToScreen(apply.ClientRectangle)));
                Assert.False(controlArea.AutoScroll);
                PumpTask(InvokePrivateTask(form, "RefreshVisualGroupsAsync", new object?[] { null }));

                DataGridView groups = GetPrivateField<DataGridView>(form, "_visualGroupsGrid");
                DataGridView members = GetPrivateField<DataGridView>(form, "_visualMembersGrid");
                Assert.Equal(2, groups.Rows.Count);
                PumpUntil(() => members.Rows.Count == 2);
                long initialGroupId = ((VisualSimilarityGroupRecord)groups.SelectedRows[0].Tag!).GroupId;

                ContextMenuStrip groupMenu = GetPrivateField<ContextMenuStrip>(form, "_visualGroupsMenu");
                ContextMenuStrip memberMenu = GetPrivateField<ContextMenuStrip>(form, "_visualMembersMenu");
                InvokePrivate(form, "VisualGroupsMenu_Opening", groupMenu, new CancelEventArgs());
                InvokePrivate(form, "VisualMembersMenu_Opening", memberMenu, new CancelEventArgs());
                Assert.True(groupMenu.Items.Find("Review", false).Single().Enabled);
                Assert.True(groupMenu.Items.Find("Cleanup", false).Single().Enabled);
                Assert.True(groupMenu.Items.Find("DeleteBoth", false).Single().Enabled);
                Assert.True(groupMenu.Items.Find("NotMatch", false).Single().Enabled);
                Assert.True(groupMenu.Items.Find("Next", false).Single().Enabled);
                Assert.True(memberMenu.Items.Find("Play", false).Single().Enabled);
                Assert.True(memberMenu.Items.Find("Keeper", false).Single().Enabled);
                Assert.True(memberMenu.Items.Find("KeepDeleteOther", false).Single().Enabled);
                Assert.Equal("Protect", memberMenu.Items.Find("Protect", false).Single().Text);

                bool sawComparison = false;
                bool navigated = false;
                bool ignoreClicked = false;
                bool restoreClicked = false;
                string? firstReviewTitle = null;
                using var timer = new System.Windows.Forms.Timer { Interval = 50 };
                timer.Tick += (_, _) =>
                {
                    Form? review = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open != form && open.Text.StartsWith("Review Visual Match", StringComparison.Ordinal));
                    if (review == null)
                        return;
                    Panel[] cards = Descendants<Panel>(review).Where(panel => panel.AccessibleName?.StartsWith("Visual review file:", StringComparison.Ordinal) == true).ToArray();
                    if (cards.Length != 2)
                        return;
                    if (!sawComparison)
                    {
                        sawComparison = true;
                        firstReviewTitle = review.Text;
                        foreach (Panel card in cards)
                            Descendants<Button>(card).Single(button => button.Text == "Play video").PerformClick();
                        Descendants<Button>(review).Single(button => button.Text == "Next >").PerformClick();
                        return;
                    }
                    if (!navigated && review.Text != firstReviewTitle)
                    {
                        navigated = true;
                        Descendants<Button>(review).Single(button => button.Text == "Ignore").PerformClick();
                        ignoreClicked = true;
                        return;
                    }
                    if (ignoreClicked && !restoreClicked && Descendants<Button>(review).FirstOrDefault(button => button.Text == "Restore") is { } restore)
                    {
                        restore.PerformClick();
                        restoreClicked = true;
                        return;
                    }
                    if (restoreClicked && Descendants<Button>(review).Any(button => button.Text == "Ignore"))
                    {
                        review.Close();
                    }
                };
                timer.Start();
                Task reviewTask = InvokePrivateTask(form, "OpenVisualReviewAsync");
                PumpTask(reviewTask, TimeSpan.FromSeconds(10));
                timer.Stop();
                Assert.True(sawComparison);
                Assert.True(navigated);
                Assert.True(ignoreClicked);
                Assert.True(restoreClicked);
                Assert.Equal(2, played.Count);
                Assert.Contains(a, played.Concat(new[] { "" }), StringComparer.OrdinalIgnoreCase);
                Assert.Contains(b, played.Concat(new[] { "" }), StringComparer.OrdinalIgnoreCase);

                DataGridViewRow initialRow = groups.Rows.Cast<DataGridViewRow>().Single(row => ((VisualSimilarityGroupRecord)row.Tag!).GroupId == initialGroupId);
                groups.ClearSelection();
                initialRow.Selected = true;
                groups.CurrentCell = initialRow.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                PumpUntil(() => members.Rows.Count == 2 && members.SelectedRows.Count == 1);
                VisualSimilarityMemberRecord selectedMember = (VisualSimilarityMemberRecord)members.SelectedRows[0].Tag!;
                PumpTask(InvokePrivateTask(form, "SetSelectedVisualKeeperAsync"));
                Assert.Equal(selectedMember.FileId, catalog.GetVisualGroup(initialGroupId)!.ManualKeeperFileId);
                Assert.True(catalog.GetVisualGroup(initialGroupId)!.Reviewed);

                PumpTask(InvokePrivateTask(form, "ToggleSelectedVisualProtectionAsync"));
                Assert.True(catalog.GetVisualGroupMembers(initialGroupId).Single(member => member.FileId == selectedMember.FileId).IsProtected);
                PumpTask(InvokePrivateTask(form, "ToggleSelectedVisualIgnoredAsync"));
                Assert.True(catalog.GetVisualGroup(initialGroupId)!.Ignored);
                PumpTask(InvokePrivateTask(form, "ToggleSelectedVisualIgnoredAsync"));
                Assert.False(catalog.GetVisualGroup(initialGroupId)!.Ignored);

                bool sawCleanupPreview = false;
                using (var cleanupTimer = new System.Windows.Forms.Timer { Interval = 50 })
                {
                    cleanupTimer.Tick += (_, _) =>
                    {
                        Form? preview = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open.Text == "Review Visual Duplicate Cleanup Plan");
                        if (preview == null) return;
                        DataGridView previewGrid = Descendants<DataGridView>(preview).Single(grid => grid.Name == "VisualCleanupPreviewGrid");
                        Assert.Single(previewGrid.Rows.Cast<DataGridViewRow>());
                        Assert.Contains("PERMANENT DELETE", Descendants<Label>(preview).Select(label => label.Text).First(text => text.Contains("PERMANENT DELETE")));
                        sawCleanupPreview = true;
                        Descendants<Button>(preview).Single(button => button.Text == "Cancel").PerformClick();
                    };
                    cleanupTimer.Start();
                    PumpTask(InvokePrivateTask(form, "PreviewVisualCleanupAsync", new long[] { initialGroupId }, null));
                    cleanupTimer.Stop();
                }
                Assert.True(sawCleanupPreview);

                PumpTask(InvokePrivateTask(form, "ToggleSelectedVisualProtectionAsync"));
                bool sawDeleteBothPreview = false;
                using (var deleteBothTimer = new System.Windows.Forms.Timer { Interval = 50 })
                {
                    deleteBothTimer.Tick += (_, _) =>
                    {
                        Form? preview = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open.Text == "Review Visual Duplicate Cleanup Plan");
                        if (preview == null) return;
                        DataGridView previewGrid = Descendants<DataGridView>(preview).Single(grid => grid.Name == "VisualCleanupPreviewGrid");
                        DataGridViewRow row = Assert.Single(previewGrid.Rows.Cast<DataGridViewRow>());
                        Assert.Equal("DELETE BOTH", row.Cells["Intent"].Value);
                        Assert.Contains("NO KEEPER", Convert.ToString(row.Cells["Keeper"].Value), StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("DELETE BOTH", Descendants<Label>(preview).Select(label => label.Text).First(text => text.Contains("DELETE BOTH")));
                        sawDeleteBothPreview = true;
                        Descendants<Button>(preview).Single(button => button.Text == "Cancel").PerformClick();
                    };
                    deleteBothTimer.Start();
                    PumpTask(InvokePrivateTask(form, "PreviewDeleteBothAsync", initialGroupId));
                    deleteBothTimer.Stop();
                }
                Assert.True(sawDeleteBothPreview);

                PumpTask(InvokePrivateTask(form, "ToggleSelectedVisualNotMatchAsync"));
                Assert.True(catalog.GetVisualGroup(initialGroupId)!.NotMatch);
                Assert.Single(groups.Rows.Cast<DataGridViewRow>());
                ComboBox reviewFilter = GetPrivateField<ComboBox>(form, "_visualReview");
                reviewFilter.SelectedIndex = 4;
                PumpTask(InvokePrivateTask(form, "RefreshVisualGroupsAsync", new object?[] { null }));
                Assert.Single(groups.Rows.Cast<DataGridViewRow>());
                PumpTask(InvokePrivateTask(form, "ToggleSelectedVisualNotMatchAsync"));
                Assert.False(catalog.GetVisualGroup(initialGroupId)!.NotMatch);
                reviewFilter.SelectedIndex = 0;
                PumpTask(InvokePrivateTask(form, "RefreshVisualGroupsAsync", new object?[] { null }));
                Assert.Equal(2, groups.Rows.Count);

                long beforeNavigation = ((VisualSimilarityGroupRecord)groups.SelectedRows[0].Tag!).GroupId;
                PumpTask(InvokePrivateTask(form, "NavigateVisualSelectionAsync", 1));
                long afterNavigation = ((VisualSimilarityGroupRecord)groups.SelectedRows[0].Tag!).GroupId;
                Assert.NotEqual(beforeNavigation, afterNavigation);
                PumpTask(InvokePrivateTask(form, "NavigateVisualSelectionAsync", -1));
                Assert.Equal(beforeNavigation, ((VisualSimilarityGroupRecord)groups.SelectedRows[0].Tag!).GroupId);
                form.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The visual review UI test did not finish.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException("The visual review UI workflow failed.", failure);
    }

    [Fact]
    public void RealVisualReviewThumbnailUsesConfiguredFfmpegWhenAvailable()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("MEDIAFLUX_FFMPEG_PATH") ?? "";
        if (!File.Exists(ffmpeg))
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                string media = Path.Combine(_root, "visual-review-ffmpeg");
                Directory.CreateDirectory(media);
                string clip = Path.Combine(media, "review-source.mp4");
                RunToolAsync(ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", clip).GetAwaiter().GetResult();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                FileInfo file = new(clip);
                var member = new VisualSimilarityMemberRecord(
                    1, 1, clip, media, file.Length, file.LastWriteTimeUtc, IndexedFileAvailability.Present,
                    "h264", 320, 180, 1_000_000, 2, false, true, false);
                SqliteLibraryCatalog catalog = CreateCatalog(Path.Combine(_root, "visual-review-ffmpeg.db"));
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mp4" }, new EmptyMetadataProbe(), new FakeVisualExtractor(_ => Array.Empty<ulong>()));
                using var form = new LibraryAnalyzerForm(
                    runtime,
                    reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(FfmpegPath: ffmpeg, PreviewCacheRoot: _root));
                form.Show();
                Application.DoEvents();
                MethodInfo method = typeof(LibraryAnalyzerForm).GetMethod("CreateVisualReviewThumbnailAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(LibraryAnalyzerForm).FullName, "CreateVisualReviewThumbnailAsync");
                var thumbnailTask = (Task<string?>)method.Invoke(form, new object[] { member, CancellationToken.None })!;
                PumpTask(thumbnailTask, TimeSpan.FromSeconds(30));
                string? thumbnail = thumbnailTask.Result;
                Assert.NotNull(thumbnail);
                Assert.True(File.Exists(thumbnail));
                using (Image image = Image.FromFile(thumbnail))
                {
                    Assert.True(image.Width > 0);
                    Assert.True(image.Height > 0);
                }
                File.Delete(thumbnail);
                form.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "The real FFmpeg visual review thumbnail test did not finish.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException("The real FFmpeg visual review thumbnail test failed.", failure);
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

    private static T GetPrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().FullName, name));

    private static object? InvokePrivate(object instance, string name, params object?[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, name);
        return method.Invoke(instance, arguments);
    }

    private static Task InvokePrivateTask(object instance, string name, params object?[] arguments) =>
        (Task)(instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, arguments)
            ?? throw new MissingMethodException(instance.GetType().FullName, name));

    private static void PumpTask(Task task, TimeSpan? timeout = null)
    {
        PumpUntil(() => task.IsCompleted, timeout);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The WinForms operation did not complete in time.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static IEnumerable<T> Descendants<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match)
                yield return match;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
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

    private sealed class EmptyMetadataProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "empty-probe";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeResult { Success = false, ErrorMessage = "Not used by this test." });
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

    private sealed class FakeCleanupActions : ILibraryDuplicateFileActions
    {
        public string FailPath { get; set; } = "";
        public void Recycle(string path)
        {
            if (string.Equals(path, FailPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("simulated Recycle Bin failure");
            File.Delete(path);
        }
        public void DeletePermanent(string path) => File.Delete(path);
        public string Quarantine(string path, string quarantineRoot, long groupId, long fileId)
        {
            string destination = Path.Combine(quarantineRoot, Path.GetFileName(path)); Directory.CreateDirectory(quarantineRoot); File.Move(path, destination); return destination;
        }
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
