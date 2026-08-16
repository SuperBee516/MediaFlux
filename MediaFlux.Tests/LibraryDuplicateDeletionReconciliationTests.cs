using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryDuplicateDeletionReconciliationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MediaFlux-DeletionReconciliation", Guid.NewGuid().ToString("N"));

    public LibraryDuplicateDeletionReconciliationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExactDeletionImmediatelyRemovesVisualParticipation()
    {
        using AnalyzedLibrary library = await CreateAnalyzedLibraryAsync(2);
        ExactDuplicateGroupRecord exact = Assert.Single(library.Catalog.QueryDuplicateGroups(new()).Groups);
        VisualSimilarityGroupRecord visual = Assert.Single(library.Catalog.QueryVisualGroups(new()).Groups);
        long keeperId = library.FileId(0);
        library.Catalog.SaveDuplicateDecision(new(exact.GroupId, keeperId, true, false));
        library.Catalog.SaveVisualDecision(new(visual.GroupId, keeperId, true, false));
        var cleanup = new LibraryDuplicateCleanupService(
            library.Catalog, library.Catalog, null, new FileActions(), new EmptyIdentityProvider());
        DuplicateCleanupPlanSummary plan = cleanup.CreatePlan(new[] { exact.GroupId }, DuplicateCleanupAction.PermanentDelete);
        long deletedId = Assert.Single(library.Catalog.GetCleanupPlanItemsBatch(plan.PlanId, 0, 0, 10)).FileId;

        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);

        Assert.Equal(1, result.Succeeded);
        Assert.Empty(library.Catalog.QueryVisualGroups(new(IncludeFamilyPairs: true)).Groups);
        Assert.Null(library.Catalog.GetVisualGroup(visual.GroupId));
        Assert.Null(library.Catalog.GetVisualFingerprint(deletedId));
        Assert.Equal(IndexedFileAvailability.Missing, library.Catalog.GetFileByPath(library.PathFor(deletedId))!.Availability);
    }

    [Fact]
    public async Task VisualDeletionImmediatelyRemovesExactParticipation()
    {
        using AnalyzedLibrary library = await CreateAnalyzedLibraryAsync(2);
        ExactDuplicateGroupRecord exact = Assert.Single(library.Catalog.QueryDuplicateGroups(new()).Groups);
        VisualSimilarityGroupRecord visual = Assert.Single(library.Catalog.QueryVisualGroups(new()).Groups);
        long keeperId = library.FileId(0);
        library.Catalog.SaveDuplicateDecision(new(exact.GroupId, keeperId, true, false));
        library.Catalog.SaveVisualDecision(new(visual.GroupId, keeperId, true, false));

        DuplicateCleanupExecutionResult result = await ExecuteVisualCleanupAsync(library, visual.GroupId);

        Assert.Equal(1, result.Succeeded);
        Assert.Empty(library.Catalog.QueryDuplicateGroups(new()).Groups);
        Assert.Contains(library.Catalog.GetDuplicateGroupMembers(exact.GroupId),
            member => member.Availability == IndexedFileAvailability.Missing);
    }

    [Fact]
    public async Task TwoFileGroupCollapsesAfterOneSuccessfulDeletion()
    {
        using AnalyzedLibrary library = await CreateAnalyzedLibraryAsync(2);
        ExactDuplicateGroupRecord group = Assert.Single(library.Catalog.QueryDuplicateGroups(new()).Groups);
        long keeperId = library.FileId(0);
        library.Catalog.SaveDuplicateDecision(new(group.GroupId, keeperId, true, false));
        var cleanup = new LibraryDuplicateCleanupService(
            library.Catalog, library.Catalog, null, new FileActions(), new EmptyIdentityProvider());
        DuplicateCleanupPlanSummary plan = cleanup.CreatePlan(new[] { group.GroupId }, DuplicateCleanupAction.PermanentDelete);

        Assert.Equal(1, (await cleanup.ExecutePlanAsync(plan.PlanId)).Succeeded);

        Assert.Empty(library.Catalog.QueryDuplicateGroups(new()).Groups);
        Assert.Equal(2, library.Catalog.GetDuplicateGroupMembers(group.GroupId).Count);
        Assert.Single(library.Catalog.GetDuplicateGroupMembers(group.GroupId),
            member => member.Availability == IndexedFileAvailability.Missing);
    }

    [Fact]
    public async Task MultiMemberExactGroupAndVisualFamilyRemainCorrectAfterDeletion()
    {
        using AnalyzedLibrary library = await CreateAnalyzedLibraryAsync(4);
        ExactDuplicateGroupRecord exact = Assert.Single(library.Catalog.QueryDuplicateGroups(new()).Groups);
        VisualFamilyRecord family = Assert.Single(library.Catalog.QueryVisualFamilies(new()).Families);
        long keeperId = library.FileId(0);
        long deleteId = library.FileId(1);
        library.Catalog.SaveDuplicateDecision(new(exact.GroupId, keeperId, true, false));
        var cleanup = new LibraryDuplicateCleanupService(
            library.Catalog, library.Catalog, null, new FileActions(), new EmptyIdentityProvider());
        ExactCleanupCandidate candidate = cleanup.GetEligibleCandidates().Single(item => item.FileId == deleteId);
        DuplicateCleanupPlanSummary plan = cleanup.CreatePlanForCandidates(
            new[] { candidate }, DuplicateCleanupAction.PermanentDelete);

        Assert.Equal(1, (await cleanup.ExecutePlanAsync(plan.PlanId)).Succeeded);

        ExactDuplicateGroupRecord remainingExact = library.Catalog.GetDuplicateGroup(exact.GroupId)!;
        Assert.Equal(3, remainingExact.MemberCount);
        Assert.Equal(3, library.Catalog.GetDuplicateGroupMembers(exact.GroupId).Count);
        Assert.DoesNotContain(library.Catalog.GetDuplicateGroupMembers(exact.GroupId), item => item.FileId == deleteId);
        VisualFamilyRecord remainingFamily = Assert.Single(library.Catalog.QueryVisualFamilies(new()).Families);
        Assert.Equal(family.FamilyId, remainingFamily.FamilyId);
        Assert.Equal(3, remainingFamily.MemberCount);
        Assert.Equal(3, library.Catalog.GetVisualFamilyMembers(family.FamilyId).Count);
        Assert.Equal(3, library.Catalog.GetVisualFamilyEdges(family.FamilyId).Count);
        Assert.DoesNotContain(library.Catalog.GetVisualFamilyMembers(family.FamilyId), item => item.FileId == deleteId);
    }

    [Fact]
    public async Task FailedFilesystemDeletionLeavesCatalogAndBothAnalyzersIntact()
    {
        using AnalyzedLibrary library = await CreateAnalyzedLibraryAsync(2);
        ExactDuplicateGroupRecord exact = Assert.Single(library.Catalog.QueryDuplicateGroups(new()).Groups);
        VisualSimilarityGroupRecord visual = Assert.Single(library.Catalog.QueryVisualGroups(new()).Groups);
        long keeperId = library.FileId(0);
        library.Catalog.SaveDuplicateDecision(new(exact.GroupId, keeperId, true, false));
        library.Catalog.SaveVisualDecision(new(visual.GroupId, keeperId, true, false));
        var actions = new FileActions { FailAll = true };
        var cleanup = new LibraryDuplicateCleanupService(
            library.Catalog, library.Catalog, null, actions, new EmptyIdentityProvider());
        DuplicateCleanupPlanSummary plan = cleanup.CreatePlan(new[] { exact.GroupId }, DuplicateCleanupAction.PermanentDelete);
        long failedId = Assert.Single(library.Catalog.GetCleanupPlanItemsBatch(plan.PlanId, 0, 0, 10)).FileId;

        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(IndexedFileAvailability.Present, library.Catalog.GetFileByPath(library.PathFor(failedId))!.Availability);
        Assert.Equal(2, library.Catalog.GetDuplicateGroup(exact.GroupId)!.MemberCount);
        Assert.NotNull(library.Catalog.GetVisualGroup(visual.GroupId));
        Assert.True(File.Exists(library.PathFor(failedId)));
    }

    [Fact]
    public async Task SurvivingKeeperCannotBeDeletedByAStaleCrossAnalyzerPlan()
    {
        ulong[] matching = Enumerable.Range(0, 6).Select(index => 0x1111111111111000UL + (ulong)index).ToArray();
        ulong[] different = Enumerable.Range(0, 6).Select(index => 0xeeeeeeeeeeee0000UL + (ulong)index).ToArray();
        using AnalyzedLibrary library = await CreateAnalyzedLibraryAsync(3, path =>
            Path.GetFileName(path) == "copy-02.mkv" ? different : matching);
        ExactDuplicateGroupRecord exact = Assert.Single(library.Catalog.QueryDuplicateGroups(new()).Groups);
        VisualSimilarityGroupRecord visual = Assert.Single(library.Catalog.QueryVisualGroups(new()).Groups);
        long deletedExactKeeper = library.FileId(0);
        long survivingVisualKeeper = library.FileId(1);
        library.Catalog.SetSuggestedKeeper(exact.GroupId, deletedExactKeeper);
        library.Catalog.SaveDuplicateDecision(new(exact.GroupId, deletedExactKeeper, true, false));
        var exactCleanup = new LibraryDuplicateCleanupService(
            library.Catalog, library.Catalog, null, new FileActions(), new EmptyIdentityProvider());
        DuplicateCleanupPlanSummary stalePlan = exactCleanup.CreatePlan(
            new[] { exact.GroupId }, DuplicateCleanupAction.PermanentDelete);
        Assert.Contains(library.Catalog.GetCleanupPlanItemsBatch(stalePlan.PlanId, 0, 0, 10),
            item => item.FileId == survivingVisualKeeper);
        library.Catalog.SaveVisualDecision(new(visual.GroupId, survivingVisualKeeper, true, false));

        DuplicateCleanupExecutionResult visualResult = await ExecuteVisualCleanupAsync(library, visual.GroupId);

        Assert.Equal(1, visualResult.Succeeded);
        Assert.True(File.Exists(library.PathFor(survivingVisualKeeper)));
        ExactDuplicateGroupRecord remaining = library.Catalog.GetDuplicateGroup(exact.GroupId)!;
        Assert.Equal(2, remaining.MemberCount);
        Assert.Null(remaining.ManualKeeperFileId);
        Assert.Null(remaining.SuggestedKeeperFileId);
        Assert.Empty(library.Catalog.GetCleanupPlanItemsBatch(
            stalePlan.PlanId, 0, 0, 10, DuplicateCleanupItemStatus.Planned));
        Assert.Equal(2, library.Catalog.GetCleanupPlanItemsBatch(
            stalePlan.PlanId, 0, 0, 10, DuplicateCleanupItemStatus.Excluded).Count);
        ExactCleanupCandidate replacement = Assert.Single(exactCleanup.GetEligibleCandidates());
        Assert.Equal(survivingVisualKeeper, replacement.KeeperFileId);
        Assert.NotEqual(survivingVisualKeeper, replacement.FileId);
    }

    private async Task<DuplicateCleanupExecutionResult> ExecuteVisualCleanupAsync(AnalyzedLibrary library, long groupId)
    {
        var cleanup = new LibraryVisualDuplicateCleanupService(
            library.Catalog, library.Catalog, library.Catalog, new FileActions(), new EmptyIdentityProvider());
        VisualCleanupProposal proposal = cleanup.BuildProposal(groupIds: new[] { groupId }, includeFamilyPairs: true);
        VisualCleanupPlanRecord plan = cleanup.CreatePlan(proposal.Items, DuplicateCleanupAction.PermanentDelete);
        return await cleanup.ExecutePlanAsync(plan.PlanId);
    }

    private async Task<AnalyzedLibrary> CreateAnalyzedLibraryAsync(
        int fileCount, Func<string, IReadOnlyList<ulong>>? visualHashes = null)
    {
        string testRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var catalog = new SqliteLibraryCatalog(Path.Combine(testRoot, "catalog.db"),
            Path.Combine(testRoot, "backups"), Path.Combine(testRoot, "recovery"));
        catalog.Initialize();
        byte[] content = Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray();
        string[] paths = Enumerable.Range(0, fileCount).Select(index =>
        {
            string path = Path.Combine(testRoot, $"copy-{index:D2}.mkv");
            File.WriteAllBytes(path, content);
            return path;
        }).ToArray();
        AddInventoryAndMetadata(catalog, testRoot, paths);
        using (var exact = new LibraryDuplicateAnalysisCoordinator(
                   catalog, new LibraryDuplicateAnalysisOptions(1, 8, 1024)))
            Assert.Equal(DuplicateAnalysisStatus.Completed, (await exact.AnalyzeAsync()).Status);
        ulong[] shared = Enumerable.Range(0, 6).Select(index => 0x1234567890abc000UL + (ulong)index).ToArray();
        using (var visual = new LibraryVisualAnalysisCoordinator(catalog,
                   new FakeVisualExtractor(visualHashes ?? (_ => shared)),
                   new LibraryVisualAnalysisOptions(1, 8, 32, 128, 3, 70)))
            Assert.Equal(DuplicateAnalysisStatus.Completed, (await visual.AnalyzeAsync()).Status);
        return new AnalyzedLibrary(catalog, paths);
    }

    private static void AddInventoryAndMetadata(SqliteLibraryCatalog catalog, string root, IReadOnlyList<string> paths)
    {
        LibraryLocationRecord location = catalog.UpsertLocation(new(root));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        foreach (string path in paths)
        {
            var file = new FileInfo(path);
            LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(scan,
                new[] { new LibraryInventoryEntry(path, Path.GetRelativePath(root, path), file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) }, 1).Mutations);
            var probe = new MediaProbeResult
            {
                Success = true,
                FormatName = "matroska",
                DurationSeconds = 60,
                BitRate = 5_000_000,
                Streams = new[] { new MediaProbeStreamInfo { CodecType = "video", CodecName = "hevc", Width = 1920, Height = 1080 } }
            };
            catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(
                new(mutation.FileId, mutation.FullPath, "", file.Length, file.LastWriteTimeUtc),
                probe, 1, "test-probe", DateTime.UtcNow, null));
        }
        catalog.CompleteScan(scan, new(LibraryScanStatus.Completed, paths.Count, 0, paths.Count, 0, 0, 0));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class AnalyzedLibrary(SqliteLibraryCatalog catalog, string[] paths) : IDisposable
    {
        public SqliteLibraryCatalog Catalog { get; } = catalog;
        public long FileId(int index) => Catalog.GetFileByPath(paths[index])!.Id;
        public string PathFor(long fileId) => paths.Single(path => Catalog.GetFileByPath(path)!.Id == fileId);
        public string PathAt(int index) => paths[index];
        public void Dispose() => Catalog.Dispose();
    }

    private sealed class FakeVisualExtractor(Func<string, IReadOnlyList<ulong>> hashes) : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "cross-analyzer-test-v1";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(hashes(candidate.FullPath));
    }

    private sealed class EmptyIdentityProvider : ILibraryFileIdentityProvider
    {
        public LibraryFileIdentity GetIdentity(string path) => LibraryFileIdentity.Empty;
    }

    private sealed class FileActions : ILibraryDuplicateFileActions
    {
        public bool FailAll { get; init; }
        public void Recycle(string path) => DeletePermanent(path);
        public void DeletePermanent(string path)
        {
            if (FailAll) throw new IOException("simulated deletion failure");
            File.Delete(path);
        }
        public string Quarantine(string path, string quarantineRoot, long groupId, long fileId)
        {
            DeletePermanent(path);
            return "test quarantine";
        }
    }
}
