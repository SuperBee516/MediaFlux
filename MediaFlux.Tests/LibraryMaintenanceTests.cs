using MediaFlux.Services.LibraryCatalog;
using MediaFlux.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryMaintenanceTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"MediaFlux-MaintenanceTests",Guid.NewGuid().ToString("N"));
    public LibraryMaintenanceTests()=>Directory.CreateDirectory(_root);

    [Fact]
    public void NewLocationsDefaultToDisabledManualMaintenance()
    {
        using SqliteLibraryCatalog c=Create();LibraryLocationRecord l=c.UpsertLocation(new(Path.Combine(_root,"media")));LibraryMaintenanceProfile p=c.GetMaintenanceProfile(l.Id);
        Assert.False(p.Enabled);Assert.Equal(LibraryMaintenanceCadence.ManualOnly,p.Cadence);Assert.True(p.Actions.HasFlag(LibraryMaintenanceActions.IncrementalScan));Assert.False(p.Actions.HasFlag(LibraryMaintenanceActions.VisualDuplicates));
    }

    [Fact]
    public void ProfileAndBoundedRunFactsSurviveRestart()
    {
        string db=Path.Combine(_root,"persist.db");long id;
        using(var c=Create(db)){id=c.UpsertLocation(new(Path.Combine(_root,"persist"))).Id;LibraryMaintenanceProfile p=c.GetMaintenanceProfile(id) with{Enabled=true,Cadence=LibraryMaintenanceCadence.Weekly,Days=LibraryMaintenanceDays.Monday,PeriodicQuickScrubDays=90};c.SaveMaintenanceProfile(p);long run=c.BeginMaintenanceRun(id,LibraryMaintenanceTrigger.Manual,DateTime.UtcNow);c.CompleteMaintenanceRun(new(run,id,LibraryMaintenanceTrigger.Manual,LibraryMaintenanceOutcome.Completed,"Complete",DateTime.UtcNow.AddMinutes(-1),DateTime.UtcNow,1,2,0,3,0,0,1,0,"ok"));}
        using(var c=Create(db)){LibraryMaintenanceProfile p=c.GetMaintenanceProfile(id);Assert.True(p.Enabled);Assert.Equal(90,p.PeriodicQuickScrubDays);Assert.Equal(LibraryMaintenanceOutcome.Completed,Assert.Single(c.GetMaintenanceHistory(id)).Outcome);Assert.Equal(LibraryCatalogMigrations.CurrentVersion,c.GetDiagnostics().SchemaVersion);}
    }

    [Fact]
    public void PhaseSixSchemaMigratesWithSchedulingDisabled()
    {
        string db=Path.Combine(_root,"v11.db");var old=new LibraryCatalogDatabase(db,Path.Combine(_root,"b11"),Path.Combine(_root,"r11"));old.InitializeForTesting(11);string path=Path.Combine(_root,"legacy");using(var connection=new SqliteConnection($"Data Source={db}")){connection.Open();using SqliteCommand command=connection.CreateCommand();command.CommandText="INSERT INTO library_locations(path,path_key,include_subfolders,is_enabled,availability_state,last_error,current_generation,created_utc_ticks,updated_utc_ticks) VALUES($path,$key,1,1,0,'',0,$now,$now);";command.Parameters.AddWithValue("$path",path);command.Parameters.AddWithValue("$key",path.ToUpperInvariant());command.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);command.ExecuteNonQuery();}
        SqliteConnection.ClearAllPools();using SqliteLibraryCatalog current=Create(db);Assert.Equal(LibraryCatalogMigrations.CurrentVersion,current.GetDiagnostics().SchemaVersion);long id=Assert.Single(current.GetLocations()).Id;Assert.False(current.GetMaintenanceProfile(id).Enabled);Assert.Empty(current.GetMaintenanceHistory(id));
    }

    [Fact]
    public void DailyWeeklyAndCrossMidnightCalculationsAreDeterministic()
    {
        TimeZoneInfo utc=TimeZoneInfo.Utc;DateTime now=new(2026,8,13,12,0,0,DateTimeKind.Utc);LibraryMaintenanceProfile daily=Profile(LibraryMaintenanceCadence.Daily,TimeSpan.FromHours(13),TimeSpan.FromHours(15));
        Assert.Equal(new DateTime(2026,8,13,13,0,0,DateTimeKind.Utc),LibraryMaintenanceScheduleCalculator.GetNextRunUtc(daily,now,utc));
        LibraryMaintenanceProfile weekly=daily with{Cadence=LibraryMaintenanceCadence.Weekly,Days=LibraryMaintenanceDays.Monday};Assert.Equal(new DateTime(2026,8,17,13,0,0,DateTimeKind.Utc),LibraryMaintenanceScheduleCalculator.GetNextRunUtc(weekly,now,utc));
        Assert.True(LibraryMaintenanceScheduleCalculator.IsWithinWindow(new DateTime(2026,8,14,1,0,0),TimeSpan.FromHours(22),TimeSpan.FromHours(4)));Assert.False(LibraryMaintenanceScheduleCalculator.IsWithinWindow(new DateTime(2026,8,14,12,0,0),TimeSpan.FromHours(22),TimeSpan.FromHours(4)));
    }

    [Fact]
    public void DisabledMissedAndStartupPoliciesDoNotCreateDuplicateDueWork()
    {
        DateTime now=new(2026,8,13,13,30,0,DateTimeKind.Utc);LibraryMaintenanceProfile p=Profile(LibraryMaintenanceCadence.Daily,TimeSpan.FromHours(13),TimeSpan.FromHours(15));
        Assert.False(LibraryMaintenanceScheduleCalculator.IsDue(p with{Enabled=false},now,false,TimeZoneInfo.Utc));Assert.True(LibraryMaintenanceScheduleCalculator.IsDue(p,now,false,TimeZoneInfo.Utc));Assert.False(LibraryMaintenanceScheduleCalculator.IsDue(p with{MissedRun=LibraryMaintenanceMissedRun.Skip},now,false,TimeZoneInfo.Utc));Assert.True(LibraryMaintenanceScheduleCalculator.IsDue(p with{MissedRun=LibraryMaintenanceMissedRun.RunOnNextStartup},now,true,TimeZoneInfo.Utc));
        Assert.False(LibraryMaintenanceScheduleCalculator.IsDue(p with{LastScheduledUtc=new DateTime(2026,8,13,13,0,0,DateTimeKind.Utc)},now,false,TimeZoneInfo.Utc));
    }

    [Fact]
    public void DstInvalidStartAdvancesToFirstValidLocalTime()
    {
        TimeZoneInfo zone=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");LibraryMaintenanceProfile p=Profile(LibraryMaintenanceCadence.Daily,TimeSpan.FromHours(2.5),TimeSpan.FromHours(5));DateTime now=new(2026,3,8,5,0,0,DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026,3,8,7,30,0,DateTimeKind.Utc),LibraryMaintenanceScheduleCalculator.GetNextRunUtc(p,now,zone));
    }

    [Fact]
    public void ChangedCandidatesArePersistedAndIntegritySelectionIsTargeted()
    {
        using SqliteLibraryCatalog c=Create();string folder=Path.Combine(_root,"target");Directory.CreateDirectory(folder);LibraryLocationRecord l=c.UpsertLocation(new(folder));LibraryScanHandle scan=c.BeginScan(l.Id);DateTime write=DateTime.UtcNow;var entry=new LibraryInventoryEntry(Path.Combine(folder,"new.mkv"),"new.mkv",100,write,VolumeId:"v",FileIdentity:"i");LibraryInventoryBatchResult batch=c.UpsertInventoryBatchDetailed(scan,new[]{entry},1);c.CompleteScan(scan,new(LibraryScanStatus.Completed,1,0,1,0,0,0));
        long run=c.BeginMaintenanceRun(l.Id,LibraryMaintenanceTrigger.Manual,DateTime.UtcNow);c.RecordMaintenanceCandidates(run,batch.Mutations);Assert.Single(c.GetMaintenanceCandidateFileIds(run,LibraryInventoryChangeKind.New));LibraryMaintenanceProfile p=c.GetMaintenanceProfile(l.Id) with{Actions=LibraryMaintenanceActions.QuickScrubNew};Assert.Single(c.GetMaintenanceIntegrityFileIds(run,p,DateTime.UtcNow));Assert.Empty(c.GetMaintenanceIntegrityFileIds(run,p with{Actions=LibraryMaintenanceActions.None},DateTime.UtcNow));
    }

    [Fact]
    public void ActiveRunIsUniqueAndInterruptedRunRecoversConservatively()
    {
        using SqliteLibraryCatalog c=Create();long id=c.UpsertLocation(new(Path.Combine(_root,"recover"))).Id;DateTime now=DateTime.UtcNow;long first=c.BeginMaintenanceRun(id,LibraryMaintenanceTrigger.Scheduled,now);Assert.Equal(first,c.BeginMaintenanceRun(id,LibraryMaintenanceTrigger.Scheduled,now));Assert.Equal(1,c.RecoverInterruptedMaintenance(now.AddMinutes(1)));Assert.Equal(LibraryMaintenanceOutcome.Interrupted,Assert.Single(c.GetMaintenanceHistory(id)).Outcome);
    }

    [Fact]
    public void PeriodicReverificationDoesNotMarkUnchangedPassStale()
    {
        using SqliteLibraryCatalog c=Create();(long location,long file,long run)=AddMaintenanceFile(c,"periodic.mkv");LibraryIntegrityQueueItem item=Claim(c,file);c.CompleteIntegrityItem(item.Id,IntegrityResult(item,LibraryIntegrityResultState.Passed) with{CheckedUtc=DateTime.UtcNow.AddDays(-100)});LibraryMaintenanceProfile p=c.GetMaintenanceProfile(location) with{Actions=LibraryMaintenanceActions.None,PeriodicQuickScrubDays=90};
        Assert.Equal(LibraryIntegrityResultState.Passed,c.GetIntegrityResult(file)!.State);Assert.False(c.GetIntegrityResult(file)!.IsStale);Assert.Single(c.GetMaintenanceIntegrityFileIds(run,p,DateTime.UtcNow));
    }

    [Fact]
    public void ScheduledFailureRetryStopsAfterThreeFailedAttempts()
    {
        using SqliteLibraryCatalog c=Create();(long location,long file,long run)=AddMaintenanceFile(c,"failed.mkv");for(int i=0;i<3;i++){LibraryIntegrityQueueItem item=Claim(c,file);c.CompleteIntegrityItem(item.Id,IntegrityResult(item,LibraryIntegrityResultState.Failed) with{ErrorCategory=LibraryIntegrityErrorCategory.VideoDecodeError,Details="bad"});}
        LibraryMaintenanceProfile p=c.GetMaintenanceProfile(location) with{Actions=LibraryMaintenanceActions.QuickScrubFailed};Assert.Empty(c.GetMaintenanceIntegrityFileIds(run,p,DateTime.UtcNow));
    }

    [Fact]
    public void HundredsOfProfilesRemainPagedBySmallRowsWithoutFileEnumeration()
    {
        using SqliteLibraryCatalog c=Create();for(int i=0;i<500;i++){long id=c.UpsertLocation(new(Path.Combine(_root,"many",i.ToString()))).Id;if(i%50==0)c.SaveMaintenanceProfile(c.GetMaintenanceProfile(id) with{Enabled=true,Cadence=LibraryMaintenanceCadence.Daily});}IReadOnlyList<LibraryMaintenanceProfileView> rows=c.GetMaintenanceProfiles(DateTime.UtcNow,TimeZoneInfo.Utc);Assert.Equal(500,rows.Count);Assert.Equal(10,rows.Count(x=>x.Profile.Enabled));
    }

    [Fact]
    public void LargeChangedSetIsReadInBoundedPages()
    {
        using SqliteLibraryCatalog c=Create();string folder=Path.Combine(_root,"large");LibraryLocationRecord location=c.UpsertLocation(new(folder));LibraryScanHandle scan=c.BeginScan(location.Id);LibraryInventoryEntry[] entries=Enumerable.Range(0,2505).Select(i=>new LibraryInventoryEntry(Path.Combine(folder,$"{i:D5}.mkv"),$"{i:D5}.mkv",100+i,DateTime.UtcNow,VolumeId:"large",FileIdentity:i.ToString())).ToArray();LibraryInventoryBatchResult batch=c.UpsertInventoryBatchDetailed(scan,entries,1);c.CompleteScan(scan,new(LibraryScanStatus.Completed,entries.Length,0,entries.Length,0,0,0));long run=c.BeginMaintenanceRun(location.Id,LibraryMaintenanceTrigger.Manual,DateTime.UtcNow);c.RecordMaintenanceCandidates(run,batch.Mutations);
        long after=0;int total=0,pages=0;while(true){IReadOnlyList<long> page=c.GetMaintenanceCandidateFileIdsPage(run,null,after,1000);if(page.Count==0)break;Assert.InRange(page.Count,1,1000);total+=page.Count;pages++;after=page[^1];}Assert.Equal(2505,total);Assert.Equal(3,pages);
    }

    [Fact]
    public async Task PipelineTargetsNewFilesAndLeavesUnchangedRunCheap()
    {
        string folder=Path.Combine(_root,"pipeline");Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"movie.mkv"),new byte[64]);
        var catalog=Create();LibraryLocationRecord location=catalog.UpsertLocation(new(folder));LibraryMaintenanceProfile profile=catalog.GetMaintenanceProfile(location.Id) with{Actions=LibraryMaintenanceActions.IncrementalScan|LibraryMaintenanceActions.Metadata|LibraryMaintenanceActions.QuickScrubNew};catalog.SaveMaintenanceProfile(profile);
        using var runtime=new LibraryAnalyzerRuntime(catalog,new[]{".mkv"},new SuccessfulProbe(),new EmptyVisual());
        await runtime.Maintenance.RunNowAsync(location.Id);LibraryMaintenanceRun first=runtime.MaintenanceCatalog.GetMaintenanceHistory(location.Id).First();Assert.Equal(LibraryMaintenanceOutcome.Completed,first.Outcome);Assert.Equal(1,first.NewFiles);Assert.Equal(1,first.MetadataQueued);Assert.Equal(1,first.IntegrityQueued);
        await runtime.Maintenance.RunNowAsync(location.Id);LibraryMaintenanceRun second=runtime.MaintenanceCatalog.GetMaintenanceHistory(location.Id).First();Assert.Equal(0,second.NewFiles);Assert.Equal(0,second.ChangedFiles);Assert.Equal(0,second.IntegrityQueued);
    }

    [Fact]
    public async Task OfflineLocationDefersWithoutMassMissingAndRecoversLater()
    {
        string folder=Path.Combine(_root,"offline");Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"movie.mkv"),new byte[32]);var catalog=Create();LibraryLocationRecord location=catalog.UpsertLocation(new(folder));catalog.SaveMaintenanceProfile(catalog.GetMaintenanceProfile(location.Id) with{Actions=LibraryMaintenanceActions.IncrementalScan});
        using var runtime=new LibraryAnalyzerRuntime(catalog,new[]{".mkv"},new SuccessfulProbe(),new EmptyVisual());await runtime.Maintenance.RunNowAsync(location.Id);Directory.Delete(folder,true);await runtime.Maintenance.RunNowAsync(location.Id);Assert.Equal(LibraryMaintenanceOutcome.Unavailable,runtime.MaintenanceCatalog.GetMaintenanceHistory(location.Id).First().Outcome);Assert.Equal(LibraryLocationAvailability.Unavailable,runtime.Catalog.GetLocation(location.Id)!.Availability);Assert.Empty(runtime.Catalog.QueryFiles(new LibraryFileQuery(LocationId:location.Id,Availability:IndexedFileAvailability.Missing)).Files);
        Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"movie.mkv"),new byte[32]);await runtime.Maintenance.RunNowAsync(location.Id);Assert.Equal(LibraryMaintenanceOutcome.Completed,runtime.MaintenanceCatalog.GetMaintenanceHistory(location.Id).First().Outcome);Assert.Equal(LibraryLocationAvailability.Available,runtime.Catalog.GetLocation(location.Id)!.Availability);
    }

    [Fact]
    public async Task EnabledPipelineStagesRunInOrderAndDisabledStagesAreSkipped()
    {
        string folder=Path.Combine(_root,"ordering");Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"movie.mkv"),new byte[48]);var catalog=Create();LibraryLocationRecord location=catalog.UpsertLocation(new(folder));catalog.SaveMaintenanceProfile(catalog.GetMaintenanceProfile(location.Id) with{Actions=LibraryMaintenanceActions.IncrementalScan|LibraryMaintenanceActions.Metadata|LibraryMaintenanceActions.ExactDuplicates|LibraryMaintenanceActions.VisualDuplicates|LibraryMaintenanceActions.QuickScrubNew});
        using var runtime=new LibraryAnalyzerRuntime(catalog,new[]{".mkv"},new SuccessfulProbe(),new EmptyVisual());var stages=new List<string>();runtime.Maintenance.ProgressChanged+=p=>stages.Add(p.Stage);await runtime.Maintenance.RunNowAsync(location.Id);
        int scan=stages.IndexOf("Scanning"),metadata=stages.IndexOf("Metadata"),exact=stages.IndexOf("Exact duplicates"),visual=stages.IndexOf("Visual analysis"),integrity=stages.IndexOf("Quick Scrub");Assert.True(scan>=0&&scan<metadata&&metadata<exact&&exact<visual&&visual<integrity,string.Join(", ",stages));
        stages.Clear();runtime.MaintenanceCatalog.SaveMaintenanceProfile(runtime.MaintenanceCatalog.GetMaintenanceProfile(location.Id) with{Actions=LibraryMaintenanceActions.IncrementalScan});await runtime.Maintenance.RunNowAsync(location.Id);Assert.DoesNotContain("Metadata",stages);Assert.DoesNotContain("Exact duplicates",stages);Assert.DoesNotContain("Visual analysis",stages);Assert.DoesNotContain("Quick Scrub",stages);
    }

    private SqliteLibraryCatalog Create(string? db=null){var c=new SqliteLibraryCatalog(db??Path.Combine(_root,Guid.NewGuid()+".db"),Path.Combine(_root,"backups"),Path.Combine(_root,"recovery"));c.Initialize();return c;}
    private (long Location,long File,long Run) AddMaintenanceFile(SqliteLibraryCatalog c,string name){string folder=Path.Combine(_root,"facts",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);LibraryLocationRecord location=c.UpsertLocation(new(folder));LibraryScanHandle scan=c.BeginScan(location.Id);LibraryInventoryBatchResult batch=c.UpsertInventoryBatchDetailed(scan,new[]{new LibraryInventoryEntry(Path.Combine(folder,name),name,100,DateTime.UtcNow,VolumeId:"v",FileIdentity:name)},1);c.CompleteScan(scan,new(LibraryScanStatus.Completed,1,0,1,0,0,0));long run=c.BeginMaintenanceRun(location.Id,LibraryMaintenanceTrigger.Manual,DateTime.UtcNow);c.RecordMaintenanceCandidates(run,batch.Mutations);return(location.Id,batch.Mutations.Single().FileId,run);}
    private static LibraryIntegrityQueueItem Claim(SqliteLibraryCatalog c,long file){c.EnqueueIntegrity(file,LibraryIntegrityScrubType.Quick);return Assert.Single(c.ClaimIntegrityBatch(1,DateTime.UtcNow));}
    private static LibraryIntegrityResultWrite IntegrityResult(LibraryIntegrityQueueItem item,LibraryIntegrityResultState state)=>new(item.FileId,1,item.ScrubType,state,DateTime.UtcNow,item.SizeBytes,item.LastWriteUtc,item.VolumeId,item.FileIdentity,item.SizeBytes,item.DurationSeconds??0,1,LibraryIntegrityErrorCategory.None,"ok","test");
    private static LibraryMaintenanceProfile Profile(LibraryMaintenanceCadence cadence,TimeSpan start,TimeSpan end)=>new(1,1,true,cadence,LibraryMaintenanceDays.All,start,end,LibraryMaintenanceMissedRun.RunAtNextWindow,LibraryMaintenanceActions.Default,0,DateTime.UtcNow,DateTime.UtcNow);
    private sealed class SuccessfulProbe:ILibraryMetadataProbe{public string ToolVersion=>"test";public Task<MediaProbeResult> ProbeAsync(string path,CancellationToken token)=>Task.FromResult(new MediaProbeResult{Success=true,FormatName="matroska",DurationSeconds=60,Streams=new[]{new MediaProbeStreamInfo{CodecType="video",CodecName="h264",Width=1920,Height=1080}}});}
    private sealed class EmptyVisual:ILibraryVisualFingerprintExtractor{public string ToolVersion=>"test";public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate,CancellationToken token)=>Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
