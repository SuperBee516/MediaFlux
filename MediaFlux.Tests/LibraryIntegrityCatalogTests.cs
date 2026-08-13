using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryIntegrityCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-IntegrityCatalogTests", Guid.NewGuid().ToString("N"));
    public LibraryIntegrityCatalogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResultSurvivesRestartAndChangedFileInvalidatesPass()
    {
        string database = Path.Combine(_root, "catalog.db"); long fileId; DateTime write;
        using (SqliteLibraryCatalog catalog = Create(database))
        {
            (fileId, write) = AddFile(catalog, "movie.mkv", 1000);
            long queue = catalog.EnqueueIntegrity(fileId, LibraryIntegrityScrubType.Quick);
            LibraryIntegrityQueueItem claimed = Assert.Single(catalog.ClaimIntegrityBatch(1, DateTime.UtcNow));
            catalog.CompleteIntegrityItem(queue, Result(claimed, LibraryIntegrityResultState.Passed));
            Assert.Equal(LibraryIntegrityResultState.Passed, catalog.GetIntegrityResult(fileId)!.State);
        }
        using (SqliteLibraryCatalog reopened = Create(database))
        {
            Assert.Equal(LibraryIntegrityResultState.Passed, reopened.GetIntegrityResult(fileId)!.State);
            string path = Path.Combine(_root, "media", "movie.mkv"); File.AppendAllText(path, "changed");
            LibraryLocationRecord location = reopened.GetLocations().Single(); LibraryScanHandle scan = reopened.BeginScan(location.Id);
            var info = new FileInfo(path); reopened.UpsertInventoryBatch(scan, new[] { new LibraryInventoryEntry(path, "movie.mkv", info.Length, info.LastWriteTimeUtc, VolumeId: "vol", FileIdentity: "id") });
            Assert.Equal(LibraryIntegrityResultState.Stale, reopened.GetIntegrityResult(fileId)!.State);
            LibraryHealthIssue stale = Assert.Single(reopened.QueryHealthIssues(), value => value.FileId == fileId && value.Kind == LibraryHealthIssueKind.IntegrityResultStale);
            Assert.Equal(LibraryIntegrityScrubType.Quick, stale.SuggestedIntegrityScrub);
            Assert.Equal(LibraryReanalysisWork.Metadata, stale.SuggestedReanalysis);
        }
    }

    [Fact]
    public void InterruptedQuickRetriesButFullRequiresExplicitRetry()
    {
        using SqliteLibraryCatalog catalog = Create(); (long quick, _) = AddFile(catalog, "quick.mkv", 100); (long full, _) = AddFile(catalog, "full.mkv", 100);
        catalog.EnqueueIntegrity(quick, LibraryIntegrityScrubType.Quick); Assert.Single(catalog.ClaimIntegrityBatch(1, DateTime.UtcNow));
        catalog.EnqueueIntegrity(full, LibraryIntegrityScrubType.Full); Assert.Single(catalog.ClaimIntegrityBatch(1, DateTime.UtcNow));
        Assert.Equal(2, catalog.RecoverInterruptedIntegrity());
        Assert.Equal(LibraryIntegrityResultState.Pending, catalog.GetIntegrityResult(quick)!.State);
        Assert.Equal(LibraryIntegrityResultState.Cancelled, catalog.GetIntegrityResult(full)!.State);
        Assert.Contains(catalog.QueryHealthIssues(), value => value.FileId == full && value.Kind == LibraryHealthIssueKind.IntegrityCheckInterrupted && value.SuggestedIntegrityScrub == LibraryIntegrityScrubType.Full);
        Assert.True(catalog.EnqueueIntegrity(full, LibraryIntegrityScrubType.Full) > 0);
    }

    [Fact]
    public void DifferentIntegrityMethodVersionIsReportedAsStale()
    {
        using SqliteLibraryCatalog catalog = Create(); (long fileId, _) = AddFile(catalog, "old-method.mkv", 100);
        LibraryIntegrityQueueItem item = Assert.Single(QueueAndClaim(catalog, fileId, LibraryIntegrityScrubType.Quick));
        catalog.CompleteIntegrityItem(item.Id, Result(item, LibraryIntegrityResultState.Passed) with { MethodVersion = 2 });

        LibraryIntegrityResult result = catalog.GetIntegrityResult(fileId)!;
        Assert.Equal(LibraryIntegrityResultState.Stale, result.State);
        Assert.True(result.IsStale);
        Assert.Contains(catalog.QueryHealthIssues(), value => value.FileId == fileId && value.Kind == LibraryHealthIssueKind.IntegrityResultStale);
    }

    [Fact]
    public void FailedAndStaleIntegrityResultsBecomeActionableHealthWithoutMissingClassification()
    {
        using SqliteLibraryCatalog catalog = Create(); (long fileId, _) = AddFile(catalog, "bad.mkv", 100);
        LibraryIntegrityQueueItem item = Assert.Single(QueueAndClaim(catalog, fileId, LibraryIntegrityScrubType.Full));
        catalog.CompleteIntegrityItem(item.Id, Result(item, LibraryIntegrityResultState.Failed) with
        { ErrorCategory = LibraryIntegrityErrorCategory.VideoDecodeError, Details = "Synthetic decode failure" });
        LibraryHealthIssue issue = Assert.Single(catalog.QueryHealthIssues(), value => value.FileId == fileId && value.Kind == LibraryHealthIssueKind.IntegrityCheckFailed);
        Assert.Equal(LibraryIntegrityScrubType.Full, issue.SuggestedIntegrityScrub);
        Assert.DoesNotContain(catalog.QueryHealthIssues(), value => value.FileId == fileId && value.Kind == LibraryHealthIssueKind.Missing);
    }

    [Fact]
    public void PagingAndSummaryRemainBoundedForLargeSyntheticCatalog()
    {
        using SqliteLibraryCatalog catalog = Create(); LibraryLocationRecord location = catalog.GetLocations().FirstOrDefault() ?? catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "bulk")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id); var entries = Enumerable.Range(0, 5000).Select(index =>
            new LibraryInventoryEntry(Path.Combine(_root, "bulk", $"{index:D5}.mkv"), $"{index:D5}.mkv", 100 + index, DateTime.UtcNow.AddSeconds(index), VolumeId: "bulk", FileIdentity: index.ToString())).ToArray();
        foreach (LibraryInventoryEntry[] batch in entries.Chunk(500)) catalog.UpsertInventoryBatch(scan, batch);
        LibraryIntegrityPage page = catalog.QueryIntegrity(new LibraryIntegrityQuery(Limit: 200)); LibraryIntegritySummary summary = catalog.GetIntegritySummary();
        Assert.Equal(5000, page.TotalCount); Assert.Equal(200, page.Results.Count); Assert.Equal(5000, summary.NeverChecked);
    }

    private SqliteLibraryCatalog Create(string? path = null)
    {
        var catalog = new SqliteLibraryCatalog(path ?? Path.Combine(_root, Guid.NewGuid() + ".db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery")); catalog.Initialize(); return catalog;
    }
    private (long Id, DateTime Write) AddFile(SqliteLibraryCatalog catalog, string name, int bytes)
    {
        string folder = Path.Combine(_root, "media"); Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); File.WriteAllBytes(path, new byte[bytes]); var info = new FileInfo(path);
        LibraryLocationRecord location = catalog.GetLocations().FirstOrDefault(value => value.Path == folder) ?? catalog.UpsertLocation(new LibraryLocationUpsert(folder));
        LibraryScanHandle scan = catalog.BeginScan(location.Id); catalog.UpsertInventoryBatch(scan, new[] { new LibraryInventoryEntry(path, name, info.Length, info.LastWriteTimeUtc, VolumeId: "vol", FileIdentity: name) });
        IndexedFileRecord file = catalog.GetFileByPath(path)!; catalog.SaveMediaMetadata(new LibraryMediaMetadata(file.Id, 1, "test", LibraryProbeStatus.Succeeded, 1, null, DateTime.UtcNow, DateTime.UtcNow,
            info.Length, info.LastWriteTimeUtc, "matroska", 60, 1000, "h264", "main", null, 1920, 1080, 24, "yuv420p", 8, "progressive", "", "", "", "", Array.Empty<LibraryAudioStreamMetadata>(), Array.Empty<LibrarySubtitleStreamMetadata>(), 0, 0, ""));
        return (file.Id, info.LastWriteTimeUtc);
    }
    private static IReadOnlyList<LibraryIntegrityQueueItem> QueueAndClaim(SqliteLibraryCatalog catalog, long fileId, LibraryIntegrityScrubType type) { catalog.EnqueueIntegrity(fileId, type); return catalog.ClaimIntegrityBatch(1, DateTime.UtcNow); }
    private static LibraryIntegrityResultWrite Result(LibraryIntegrityQueueItem item, LibraryIntegrityResultState state) => new(item.FileId, 1, item.ScrubType, state, DateTime.UtcNow,
        item.SizeBytes, item.LastWriteUtc, item.VolumeId, item.FileIdentity, item.SizeBytes, item.DurationSeconds ?? 0, 1, LibraryIntegrityErrorCategory.None, "Passed", "test");
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
