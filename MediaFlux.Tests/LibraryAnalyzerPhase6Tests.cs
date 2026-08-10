using Microsoft.Data.Sqlite;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryAnalyzerPhase6Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-LibraryPhase6Tests", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerPhase6Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public void VersionEightAddsRecoverySchemaAndDecisionBackupSupport()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        Assert.Equal(9, catalog.GetDiagnostics().SchemaVersion);
        using var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"); connection.Open();
        foreach (string table in new[] { "library_presence_observations", "library_reanalysis_queue", "library_decision_events" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
        }
        using SqliteCommand lifecycle = connection.CreateCommand();
        lifecycle.CommandText = "SELECT COUNT(*) FROM pragma_table_info('visual_similarity_groups') WHERE name IN('lifecycle_state','lifecycle_reason','lifecycle_updated_utc_ticks');";
        Assert.Equal(3, Convert.ToInt32(lifecycle.ExecuteScalar()));
        string library = Path.Combine(_root, "backup-history"); Directory.CreateDirectory(library);
        string file = Write(library, "protected.mkv", 64); AddInventory(catalog, library, new[] { file });
        catalog.SetFileProtection(catalog.GetFileByPath(file)!.Id, true, "backup test");
        string backup = catalog.CreateUserDataBackup();
        using var backupConnection = new SqliteConnection($"Data Source={backup}"); backupConnection.Open();
        using SqliteCommand history = backupConnection.CreateCommand(); history.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='library_decision_events';";
        Assert.Equal(1, Convert.ToInt32(history.ExecuteScalar()));
        using SqliteLibraryCatalog restored = CreateCatalog(Path.Combine(_root, "restored.db"));
        LibraryUserDataRestoreResult result = restored.RestoreUserDataBackup(backup);
        Assert.Equal(1, result.DecisionEvents);
        LibraryDecisionEvent restoredEvent = Assert.Single(restored.GetDecisionHistory());
        Assert.Equal("restored-history", restoredEvent.Source);
        Assert.False(restored.UndoDecision(restoredEvent.Id).Succeeded);
    }

    [Fact]
    public async Task SuspectedMissingRequiresAuthoritativeScanAndOfflineStateWins()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "presence"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", 40_000), b = Write(library, "b.mkv", 40_000);
        AddInventory(catalog, library, new[] { a, b });
        using var exact = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 8 * 1024));
        await exact.AnalyzeAsync();
        long groupId = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups).GroupId;
        File.Delete(b);
        var eligibility = new LibraryMatchEligibilityService(catalog, catalog);

        LibraryMatchEligibility suspected = eligibility.EvaluateExactGroup(groupId);
        Assert.Equal(LibraryMatchEligibilityState.SuspectedMissing, suspected.State);
        Assert.Equal(IndexedFileAvailability.Present, catalog.GetFileByPath(b)!.Availability);

        LibraryLocationRecord location = Assert.Single(catalog.GetLocations());
        catalog.SetLocationAvailability(location.Id, LibraryLocationAvailability.Unavailable, "drive offline", markMembershipsUnavailable: true);
        LibraryMatchEligibility unavailable = eligibility.EvaluateExactGroup(groupId);
        Assert.Equal(LibraryMatchEligibilityState.Unavailable, unavailable.State);
        Assert.NotEqual(IndexedFileAvailability.Missing, catalog.GetFileByPath(b)!.Availability);
    }

    [Fact]
    public void CompletedScanConfirmsMissingButFailedScanDoesNot()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "authoritative"); Directory.CreateDirectory(library);
        string file = Write(library, "gone.mkv", 128);
        LibraryLocationRecord location = AddInventory(catalog, library, new[] { file });
        LibraryScanHandle failed = catalog.BeginScan(location.Id);
        catalog.CompleteScan(failed, new LibraryScanCompletion(LibraryScanStatus.Failed, 0, 0, 0, 0, 0, 1, "denied"));
        Assert.Equal(IndexedFileAvailability.Present, catalog.GetFileByPath(file)!.Availability);

        LibraryScanHandle completed = catalog.BeginScan(location.Id);
        LibraryReconciliationResult reconciled = catalog.ReconcileCompletedScan(completed);
        catalog.CompleteScan(completed, new LibraryScanCompletion(LibraryScanStatus.Completed, 0, 0, 0, 0, reconciled.MissingFiles, 0));
        Assert.Equal(IndexedFileAvailability.Missing, catalog.GetFileByPath(file)!.Availability);
        Assert.Contains(catalog.GetPresenceObservations(catalog.GetFileByPath(file)!.Id), x => x.State == LibraryPresenceObservationState.ConfirmedMissing);
    }

    [Fact]
    public void StableIdentityClassifiesMoveWithoutLeavingOldMatchEligible()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "move"); Directory.CreateDirectory(library);
        string oldPath = Write(library, "old.mkv", 256);
        LibraryLocationRecord location = AddInventory(catalog, library, new[] { oldPath }, "VOL", "FILE-1");
        long oldId = catalog.GetFileByPath(oldPath)!.Id;
        string newPath = Path.Combine(library, "renamed.mkv"); File.Move(oldPath, newPath);
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        FileInfo info = new(newPath);
        catalog.UpsertInventoryBatchDetailed(scan, new[] { new LibraryInventoryEntry(newPath, "renamed.mkv", info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, "VOL", "FILE-1") }, 1);
        LibraryReconciliationResult result = catalog.ReconcileCompletedScan(scan);
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, 1, 0, 1, 0, result.MissingFiles, 0));

        LibraryPresenceObservation moved = Assert.Single(catalog.GetPresenceObservations(oldId));
        Assert.Equal(LibraryPresenceObservationState.MovedOrRenamed, moved.State);
        Assert.Equal(catalog.GetFileByPath(newPath)!.Id, moved.RelatedFileId);
    }

    [Fact]
    public async Task CleanupImmediatelyRemovesExactGroupFromNormalReviewAndCanRestoreQuarantineSafely()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "cleanup"); Directory.CreateDirectory(library);
        string keeper = Write(library, "keeper.mkv", 80_000), candidate = Path.Combine(library, "candidate.mkv"); File.Copy(keeper, candidate);
        AddInventory(catalog, library, new[] { keeper, candidate });
        using var exact = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 16 * 1024));
        await exact.AnalyzeAsync();
        ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, catalog.GetFileByPath(keeper)!.Id, true, false));
        var cleanup = new LibraryDuplicateCleanupService(catalog, catalog);
        DuplicateCleanupPlanRecord plan = cleanup.CreatePlan(new[] { group.GroupId }, DuplicateCleanupAction.Quarantine, Path.Combine(_root, "quarantine"));
        Assert.Equal(1, (await cleanup.ExecutePlanAsync(plan.PlanId)).Succeeded);
        Assert.Empty(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
        Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery(IncludeInactive: true)).Groups);

        LibraryQuarantineRestoreItem restore = Assert.Single(catalog.GetQuarantineRestoreCandidates());
        var decisions = new LibraryDecisionService(catalog, (_, _, _) => { });
        LibraryDecisionUndoResult restored = decisions.RestoreQuarantine(restore);
        Assert.True(restored.Succeeded, restored.Message);
        Assert.True(File.Exists(candidate));
        Assert.Contains(catalog.GetDecisionHistory(), x => x.EventKind == LibraryDecisionEventKind.CleanupRestored);
        Assert.False(decisions.RestoreQuarantine(restore).Succeeded);
    }

    [Fact]
    public void ReanalysisQueueIsDurableBoundedAndRecoversInterruptedWork()
    {
        string database = Path.Combine(_root, "queue.db"); long itemId;
        using (SqliteLibraryCatalog catalog = CreateCatalog(database))
        {
            string library = Path.Combine(_root, "queue"); Directory.CreateDirectory(library);
            string file = Write(library, "file.mkv", 128); AddInventory(catalog, library, new[] { file });
            long fileId = catalog.GetFileByPath(file)!.Id;
            itemId = catalog.EnqueueReanalysis(fileId, LibraryReanalysisWork.All, maximumAttempts: 2);
            Assert.Equal(itemId, catalog.EnqueueReanalysis(fileId, LibraryReanalysisWork.Metadata, maximumAttempts: 2));
            LibraryReanalysisItem first = Assert.Single(catalog.ClaimReanalysisBatch(50, DateTime.UtcNow));
            Assert.Equal(1, first.AttemptCount);
        }
        using (SqliteLibraryCatalog reopened = CreateCatalog(database))
        {
            Assert.Equal(1, reopened.RecoverInterruptedReanalysis());
            LibraryReanalysisItem second = Assert.Single(reopened.ClaimReanalysisBatch(1, DateTime.UtcNow.AddMinutes(1)));
            Assert.Equal(2, second.AttemptCount);
            reopened.CompleteReanalysisItem(second.Id, LibraryReanalysisWork.None, "still failing");
            Assert.Empty(reopened.ClaimReanalysisBatch(1, DateTime.UtcNow.AddDays(1)));
            Assert.Contains(reopened.QueryHealthIssues(), x => x.Kind == LibraryHealthIssueKind.ReanalysisFailure);
        }
    }

    [Fact]
    public void DecisionHistorySupportsGuardedUndoAndBlocksSupersededState()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "history"); Directory.CreateDirectory(library);
        string file = Write(library, "file.mkv", 128); AddInventory(catalog, library, new[] { file });
        long fileId = catalog.GetFileByPath(file)!.Id;
        catalog.SetFileProtection(fileId, true, "first");
        LibraryDecisionEvent first = Assert.Single(catalog.GetDecisionHistory());
        LibraryDecisionUndoResult undone = catalog.UndoDecision(first.Id);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.NotNull(undone.ReversalEventId);

        catalog.SetFileProtection(fileId, true, "second");
        LibraryDecisionEvent second = catalog.GetDecisionHistory().First(x => x.Source == "library-analyzer" && x.AfterState.Contains("second"));
        catalog.SetFileProtection(fileId, true, "newer conflict");
        LibraryDecisionUndoResult blocked = catalog.UndoDecision(second.Id);
        Assert.False(blocked.Succeeded);
        Assert.Contains("newer", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeMaintenancePrunesObsoleteDerivedGroupsAndKeepsIntegrityHealthy()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        using (var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"))
        {
            connection.Open(); using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = "INSERT INTO duplicate_analysis_runs(status,quick_algorithm,quick_version,full_algorithm,full_version,started_utc_ticks) VALUES(1,'q',1,'f',1,1); " +
                               "INSERT INTO exact_duplicate_groups(analysis_run_id,size_bytes,full_algorithm,full_version,full_hash,member_count,physical_copy_count,reclaimable_bytes,updated_utc_ticks) VALUES(last_insert_rowid(),1,'f',1,X'01',2,2,1,1); " +
                               "INSERT INTO duplicate_analysis_runs(status,quick_algorithm,quick_version,full_algorithm,full_version,started_utc_ticks) VALUES(1,'q',1,'f',1,2);";
            seed.ExecuteNonQuery();
        }
        LibraryMaintenanceResult result = catalog.RunSafeMaintenance();
        Assert.Equal(1, result.PrunedExactGroups);
        Assert.True(result.Integrity.IsHealthy);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private SqliteLibraryCatalog CreateCatalog(string? path = null)
    {
        var catalog = new SqliteLibraryCatalog(path ?? Path.Combine(_root, $"{Guid.NewGuid():N}.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        catalog.Initialize(); return catalog;
    }

    private static LibraryLocationRecord AddInventory(SqliteLibraryCatalog catalog, string root, IReadOnlyList<string> paths,
        string volume = "", string identity = "")
    {
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        LibraryInventoryEntry[] entries = paths.Select(path => { FileInfo info = new(path); return new LibraryInventoryEntry(path,
            Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, volume, identity); }).ToArray();
        catalog.UpsertInventoryBatchDetailed(scan, entries, 1);
        LibraryReconciliationResult reconciliation = catalog.ReconcileCompletedScan(scan);
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, entries.Length, 0, entries.Length, 0, reconciliation.MissingFiles, 0));
        return location;
    }

    private static string Write(string root, string name, int bytes)
    { string path = Path.Combine(root, name); File.WriteAllBytes(path, Enumerable.Repeat((byte)7, bytes).ToArray()); return path; }
}
