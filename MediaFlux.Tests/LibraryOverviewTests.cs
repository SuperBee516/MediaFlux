using Microsoft.Data.Sqlite;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryOverviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-OverviewTests", Guid.NewGuid().ToString("N"));
    public LibraryOverviewTests() => Directory.CreateDirectory(_root);
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void EmptyCatalogReportsExplicitlyUnavailableInsights()
    {
        using SqliteLibraryCatalog catalog = Create();
        LibraryOverviewSnapshot snapshot = catalog.GetOverviewSnapshot(1);
        Assert.Equal(0, snapshot.IndexedVideoCount); Assert.Equal(0, snapshot.LogicalSizeBytes);
        Assert.Null(snapshot.Insights.AverageFileBytes); Assert.Null(snapshot.Insights.AverageBitrate);
        Assert.Empty(snapshot.Locations); Assert.Empty(snapshot.VideoCodecDistribution);
    }

    [Fact]
    public void SnapshotAggregatesLocationsAndPersistedInventoryWithoutAnalysis()
    {
        using SqliteLibraryCatalog catalog = Create();
        LibraryLocationRecord available = catalog.UpsertLocation(new(Path.Combine(_root, "available"), Availability: LibraryLocationAvailability.Available));
        LibraryLocationRecord offline = catalog.UpsertLocation(new(Path.Combine(_root, "offline"), Availability: LibraryLocationAvailability.Unavailable, LastError: "offline"));
        LibraryScanHandle scan = catalog.BeginScan(available.Id);
        catalog.UpsertInventoryBatch(scan, new[] { new LibraryInventoryEntry(Path.Combine(_root,"available","film.mkv"),"film.mkv",100,DateTime.UtcNow) });
        catalog.CompleteScan(scan, new(LibraryScanStatus.Completed,1,0,1,0,0,0));

        LibraryOverviewSnapshot snapshot = catalog.GetOverviewSnapshot(1);
        Assert.Equal(1, snapshot.IndexedVideoCount); Assert.Equal(100, snapshot.LogicalSizeBytes);
        Assert.Equal(2, snapshot.ConfiguredLocationCount); Assert.Equal(1, snapshot.AvailableLocationCount); Assert.Equal(1, snapshot.UnavailableLocationCount);
        Assert.Equal(2, snapshot.Locations.Count); Assert.Equal(100, snapshot.Locations.Single(x => x.LocationId == available.Id).LogicalSizeBytes);
        Assert.Equal("offline", snapshot.Locations.Single(x => x.LocationId == offline.Id).LastError);
    }

    [Fact]
    public void SnapshotUsesPersistedExactDecisionAndReclaimableFacts()
    {
        using SqliteLibraryCatalog catalog = Create();
        LibraryLocationRecord location = catalog.UpsertLocation(new(Path.Combine(_root, "library")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        catalog.UpsertInventoryBatch(scan, new[]
        {
            new LibraryInventoryEntry(Path.Combine(_root,"library","a.mkv"),"a.mkv",100,DateTime.UtcNow),
            new LibraryInventoryEntry(Path.Combine(_root,"library","b.mkv"),"b.mkv",100,DateTime.UtcNow)
        });
        catalog.CompleteScan(scan, new(LibraryScanStatus.Completed,2,0,2,0,0,0));
        long first = catalog.GetFileByPath(Path.Combine(_root, "library", "a.mkv"))!.Id;
        long second = catalog.GetFileByPath(Path.Combine(_root, "library", "b.mkv"))!.Id;
        Execute(catalog.DatabasePath, $"INSERT INTO duplicate_analysis_runs(status,quick_algorithm,quick_version,full_algorithm,full_version,started_utc_ticks,completed_utc_ticks) VALUES(1,'q',1,'sha256',1,1,1); INSERT INTO exact_duplicate_groups(size_bytes,full_algorithm,full_version,full_hash,member_count,physical_copy_count,reclaimable_bytes,analysis_run_id,updated_utc_ticks) VALUES(100,'sha256',1,X'01',2,2,100,1,1); INSERT INTO exact_duplicate_members(group_id,file_id,physical_identity_key,is_hard_link_alias) VALUES(1,{first},'a',0),(1,{second},'b',0); INSERT INTO duplicate_group_decisions(size_bytes,full_algorithm,full_version,full_hash,reviewed,ignored,updated_utc_ticks) VALUES(100,'sha256',1,X'01',1,0,1);");
        LibraryOverviewSnapshot snapshot = catalog.GetOverviewSnapshot(1);
        Assert.Equal(1, snapshot.Duplicates.ExactGroups); Assert.Equal(2, snapshot.Duplicates.ExactAffectedFiles);
        Assert.Equal(1, snapshot.Duplicates.ReviewedGroups); Assert.Equal(100, snapshot.Duplicates.ExactReclaimableBytes);
    }

    [Fact]
    public void SnapshotBuildsMetadataDistributionsAndMeaningfulInsights()
    {
        using SqliteLibraryCatalog catalog = Create();
        LibraryLocationRecord location = catalog.UpsertLocation(new(Path.Combine(_root, "library")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        DateTime written = DateTime.UtcNow;
        string firstPath = Path.Combine(_root, "library", "one.mkv"), secondPath = Path.Combine(_root, "library", "two.mp4");
        catalog.UpsertInventoryBatch(scan, new[] { new LibraryInventoryEntry(firstPath,"one.mkv",100,written), new LibraryInventoryEntry(secondPath,"two.mp4",300,written) });
        catalog.CompleteScan(scan, new(LibraryScanStatus.Completed,2,0,2,0,0,0));
        long first = catalog.GetFileByPath(firstPath)!.Id, second = catalog.GetFileByPath(secondPath)!.Id;
        Execute(catalog.DatabasePath, $"INSERT INTO media_metadata(file_id,metadata_version,probe_tool_version,probe_status,source_size_bytes,source_last_write_utc_ticks,format_name,duration_seconds,total_bitrate,video_codec,width,height,updated_utc_ticks) VALUES({first},1,'test',2,100,{written.Ticks},'matroska',10,1000,'h264',1920,1080,1),({second},1,'test',2,300,{written.Ticks},'mp4',20,2000,'hevc',3840,2160,1);");
        LibraryOverviewSnapshot snapshot = catalog.GetOverviewSnapshot(1);
        Assert.Equal(2, snapshot.VideoCodecDistribution.Sum(x => x.FileCount));
        Assert.Contains(snapshot.ResolutionDistribution, x => x.Label == "4K" && x.FileCount == 1);
        Assert.Contains(snapshot.ContainerDistribution, x => x.Label == "mp4" && x.FileCount == 1);
        Assert.Equal(second, snapshot.Insights.LargestFileId); Assert.Equal(1500d, snapshot.Insights.AverageBitrate);
    }

    [Fact]
    public void SuccessfulScansPersistBoundedHistory()
    {
        using SqliteLibraryCatalog catalog = Create();
        LibraryLocationRecord location = catalog.UpsertLocation(new(Path.Combine(_root, "library")));
        for (int i = 0; i < 367; i++)
        {
            LibraryScanHandle scan = catalog.BeginScan(location.Id, DateTime.UtcNow.AddMinutes(-500 + i));
            catalog.CompleteScan(scan, new(LibraryScanStatus.Completed,0,0,0,0,0,0, CompletedUtc: DateTime.UtcNow.AddMinutes(-500 + i)));
        }
        Assert.Equal(365, catalog.GetOverviewScanHistory(500).Count);
    }

    [Fact]
    public void CanceledOrFailedScansDoNotPersistHistory()
    {
        using SqliteLibraryCatalog catalog = Create();
        LibraryLocationRecord location = catalog.UpsertLocation(new(Path.Combine(_root, "library")));

        LibraryScanHandle canceled = catalog.BeginScan(location.Id);
        catalog.CompleteScan(canceled, new(LibraryScanStatus.Canceled, 0, 0, 0, 0, 0, 0));
        LibraryScanHandle failed = catalog.BeginScan(location.Id);
        catalog.CompleteScan(failed, new(LibraryScanStatus.Failed, 0, 0, 0, 0, 0, 1, "probe failed"));

        Assert.Empty(catalog.GetOverviewScanHistory());
    }

    [Fact]
    public async Task AsyncFacadeUsesOnlyTheReadOnlyOverviewContract()
    {
        var catalog = new ThrowingCatalog();
        using var service = new LibraryOverviewQueryService(catalog);
        LibraryOverviewSnapshot snapshot = await service.LoadAsync(1);
        Assert.Equal(1, catalog.SnapshotCalls); Assert.Equal(0, snapshot.IndexedVideoCount);
    }

    [Fact]
    public async Task DisposingAsyncFacadeCancelsAndDrainsAnInFlightRead()
    {
        using var service = new LibraryOverviewQueryService(new SlowCatalog());
        Task<LibraryOverviewSnapshot> load = service.LoadAsync(1);
        await Task.Delay(20);
        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await load);
    }

    private SqliteLibraryCatalog Create() { var catalog = new SqliteLibraryCatalog(Path.Combine(_root, Guid.NewGuid() + ".db")); catalog.Initialize(); return catalog; }
    private static void Execute(string path, string sql) { using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()); c.Open(); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }

    private class ThrowingCatalog : ILibraryOverviewCatalog
    {
        public int SnapshotCalls { get; private set; }
        public virtual LibraryOverviewSnapshot GetOverviewSnapshot(int metadataVersion) { SnapshotCalls++; return new(DateTime.UtcNow,0,0,0,0,0,0,0,null,new(0,0,0,0,0,0,0,0,0,0,0),Array.Empty<LibraryOverviewLocation>(),Array.Empty<LibraryOverviewDistribution>(),Array.Empty<LibraryOverviewDistribution>(),Array.Empty<LibraryOverviewDistribution>(),new(null,"",null,null,null,"",null,null,"",""),new(0,0,0,0,0,0,0)); }
        public virtual IReadOnlyList<LibraryOverviewScanHistoryEntry> GetOverviewScanHistory(int limit = 365) => Array.Empty<LibraryOverviewScanHistoryEntry>();
    }

    private sealed class SlowCatalog : ThrowingCatalog
    {
        public override LibraryOverviewSnapshot GetOverviewSnapshot(int metadataVersion) { Thread.Sleep(100); return base.GetOverviewSnapshot(metadataVersion); }
    }
}
