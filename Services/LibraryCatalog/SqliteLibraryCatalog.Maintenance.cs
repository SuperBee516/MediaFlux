using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog;

public sealed partial class SqliteLibraryCatalog : ILibraryMaintenanceCatalog
{
    public LibraryMaintenanceProfile GetMaintenanceProfile(long locationId)
    {
        ThrowIfDisposed(); using SqliteConnection c = _database.OpenConnection(readOnly: true); using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT profile_version,enabled,cadence,days,start_minute,end_minute,missed_run,actions,periodic_quick_scrub_days,created_utc_ticks,updated_utc_ticks,last_scheduled_utc_ticks FROM library_maintenance_profiles WHERE location_id=$id;";
        cmd.Parameters.AddWithValue("$id", locationId); using SqliteDataReader r = cmd.ExecuteReader();
        if (r.Read()) return ReadProfile(locationId, r);
        if (GetLocation(locationId) == null) throw new KeyNotFoundException($"Library location {locationId} does not exist.");
        DateTime now = DateTime.UtcNow;
        return new(locationId, 1, false, LibraryMaintenanceCadence.ManualOnly, LibraryMaintenanceDays.All,
            TimeSpan.FromHours(1), TimeSpan.FromHours(6), LibraryMaintenanceMissedRun.RunAtNextWindow,
            LibraryMaintenanceActions.Default, 0, now, now);
    }

    public void SaveMaintenanceProfile(LibraryMaintenanceProfile p)
    {
        ThrowIfDisposed(); if (p.Version < 1 || p.StartTime < TimeSpan.Zero || p.StartTime >= TimeSpan.FromDays(1) || p.EndTime < TimeSpan.Zero || p.EndTime >= TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(p));
        WithWriteTransaction<object?>((c,t) => { using SqliteCommand cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="""
            INSERT INTO library_maintenance_profiles(location_id,profile_version,enabled,cadence,days,start_minute,end_minute,missed_run,actions,periodic_quick_scrub_days,last_scheduled_utc_ticks,created_utc_ticks,updated_utc_ticks)
            VALUES($id,$version,$enabled,$cadence,$days,$start,$end,$missed,$actions,$periodic,$last,$created,$updated)
            ON CONFLICT(location_id) DO UPDATE SET profile_version=excluded.profile_version,enabled=excluded.enabled,cadence=excluded.cadence,days=excluded.days,start_minute=excluded.start_minute,end_minute=excluded.end_minute,missed_run=excluded.missed_run,actions=excluded.actions,periodic_quick_scrub_days=excluded.periodic_quick_scrub_days,last_scheduled_utc_ticks=excluded.last_scheduled_utc_ticks,updated_utc_ticks=excluded.updated_utc_ticks;
            """;
            cmd.Parameters.AddWithValue("$id",p.LocationId); cmd.Parameters.AddWithValue("$version",p.Version); cmd.Parameters.AddWithValue("$enabled",p.Enabled?1:0); cmd.Parameters.AddWithValue("$cadence",(int)p.Cadence); cmd.Parameters.AddWithValue("$days",(int)p.Days); cmd.Parameters.AddWithValue("$start",(int)p.StartTime.TotalMinutes); cmd.Parameters.AddWithValue("$end",(int)p.EndTime.TotalMinutes); cmd.Parameters.AddWithValue("$missed",(int)p.MissedRun); cmd.Parameters.AddWithValue("$actions",(int)p.Actions); cmd.Parameters.AddWithValue("$periodic",Math.Clamp(p.PeriodicQuickScrubDays,0,3650)); cmd.Parameters.AddWithValue("$last",p.LastScheduledUtc?.Ticks ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$created",p.CreatedUtc.Ticks); cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.Ticks); cmd.ExecuteNonQuery(); return null; });
    }

    public IReadOnlyList<LibraryMaintenanceProfileView> GetMaintenanceProfiles(DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var history = GetMaintenanceHistory(limit: 1000).GroupBy(x=>x.LocationId).ToDictionary(g=>g.Key,g=>g.OrderByDescending(x=>x.StartedUtc).First());
        return GetLocations().Select(l => { LibraryMaintenanceProfile p=GetMaintenanceProfile(l.Id); history.TryGetValue(l.Id,out LibraryMaintenanceRun? run); DateTime? next=LibraryMaintenanceScheduleCalculator.IsDue(p,utcNow,false,timeZone)?utcNow:LibraryMaintenanceScheduleCalculator.GetNextRunUtc(p,utcNow,timeZone); return new LibraryMaintenanceProfileView(p,l.Path,l.Availability,run?.CompletedUtc,run?.Outcome,run?.Details ?? "Never run",next); }).ToArray();
    }

    public long BeginMaintenanceRun(long locationId, LibraryMaintenanceTrigger trigger, DateTime utcNow) => WithWriteTransaction((c,t) => { using SqliteCommand check=c.CreateCommand(); check.Transaction=t; check.CommandText="SELECT id FROM library_maintenance_runs WHERE location_id=$location AND outcome=0 LIMIT 1;"; check.Parameters.AddWithValue("$location",locationId); object? active=check.ExecuteScalar(); if(active!=null) return Convert.ToInt64(active); using SqliteCommand cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="INSERT INTO library_maintenance_runs(location_id,trigger_kind,outcome,stage,started_utc_ticks,details) VALUES($location,$trigger,0,'Starting',$now,'Starting maintenance.') RETURNING id;"; cmd.Parameters.AddWithValue("$location",locationId); cmd.Parameters.AddWithValue("$trigger",(int)trigger); cmd.Parameters.AddWithValue("$now",utcNow.Ticks); return Convert.ToInt64(cmd.ExecuteScalar()); });

    public void UpdateMaintenanceRunStage(long runId,string stage,string details)
    { WithWriteTransaction<object?>((c,t)=>{using SqliteCommand cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE library_maintenance_runs SET stage=$stage,details=$details WHERE id=$id AND outcome=0;";cmd.Parameters.AddWithValue("$stage",Bound(stage,100));cmd.Parameters.AddWithValue("$details",Bound(details,2000));cmd.Parameters.AddWithValue("$id",runId);cmd.ExecuteNonQuery();return null;}); }

    public void RecordMaintenanceCandidates(long runId, IReadOnlyCollection<LibraryInventoryMutation> mutations)
    {
        if (mutations.Count==0) return; WithWriteTransaction<object?>((c,t)=>{ using SqliteCommand cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="INSERT INTO library_maintenance_candidates(run_id,file_id,change_kind) VALUES($run,$file,$kind) ON CONFLICT(run_id,file_id) DO UPDATE SET change_kind=MAX(change_kind,excluded.change_kind);"; cmd.Parameters.Add("$run",SqliteType.Integer).Value=runId; cmd.Parameters.Add("$file",SqliteType.Integer); cmd.Parameters.Add("$kind",SqliteType.Integer); foreach(var m in mutations.Where(x=>x.ChangeKind!=LibraryInventoryChangeKind.Unchanged)){cmd.Parameters["$file"].Value=m.FileId;cmd.Parameters["$kind"].Value=(int)m.ChangeKind;cmd.ExecuteNonQuery();} return null;});
    }

    public IReadOnlyList<long> GetMaintenanceCandidateFileIds(long runId, LibraryInventoryChangeKind? kind, int limit=50_000)
        =>GetMaintenanceCandidateFileIdsPage(runId,kind,0,limit);

    public IReadOnlyList<long> GetMaintenanceCandidateFileIdsPage(long runId,LibraryInventoryChangeKind? kind,long afterFileId,int limit=1_000)
    { using SqliteConnection c=_database.OpenConnection(readOnly:true);using SqliteCommand cmd=c.CreateCommand();cmd.CommandText="SELECT file_id FROM library_maintenance_candidates WHERE run_id=$run AND file_id>$after AND ($kind IS NULL OR change_kind=$kind) ORDER BY file_id LIMIT $limit;";cmd.Parameters.AddWithValue("$run",runId);cmd.Parameters.AddWithValue("$after",afterFileId);cmd.Parameters.AddWithValue("$kind",kind.HasValue?(int)kind.Value:(object)DBNull.Value);cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,50_000));var ids=new List<long>();using SqliteDataReader r=cmd.ExecuteReader();while(r.Read())ids.Add(r.GetInt64(0));return ids; }

    public IReadOnlyList<LibraryEnrichmentCandidate> GetMaintenanceEnrichmentCandidates(long runId,long afterFileId,int limit=1_000)
    { using SqliteConnection c=_database.OpenConnection(readOnly:true);using SqliteCommand cmd=c.CreateCommand();cmd.CommandText="SELECT f.id,f.full_path,f.volume_id,f.size_bytes,f.last_write_utc_ticks,COALESCE(m.attempt_count,0)+1 FROM library_maintenance_candidates mc JOIN indexed_files f ON f.id=mc.file_id LEFT JOIN media_metadata m ON m.file_id=f.id WHERE mc.run_id=$run AND f.id>$after AND f.availability_state=0 AND (m.file_id IS NULL OR m.source_size_bytes<>f.size_bytes OR m.source_last_write_utc_ticks<>f.last_write_utc_ticks OR m.metadata_version<>$version) ORDER BY f.id LIMIT $limit;";cmd.Parameters.AddWithValue("$run",runId);cmd.Parameters.AddWithValue("$after",afterFileId);cmd.Parameters.AddWithValue("$version",LibraryEnrichmentCoordinator.CurrentMetadataVersion);cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,5000));var rows=new List<LibraryEnrichmentCandidate>();using SqliteDataReader r=cmd.ExecuteReader();while(r.Read())rows.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3),FromUtcTicks(r.GetInt64(4)),r.GetInt32(5)));return rows; }

    public IReadOnlyList<long> GetMaintenanceIntegrityFileIds(long runId, LibraryMaintenanceProfile profile, DateTime utcNow, int limit=50_000)
        =>GetMaintenanceIntegrityFileIdsPage(runId,profile,utcNow,0,limit);

    public IReadOnlyList<long> GetMaintenanceIntegrityFileIdsPage(long runId, LibraryMaintenanceProfile profile, DateTime utcNow,long afterFileId,int limit=1_000)
    {
        using SqliteConnection c=_database.OpenConnection(readOnly:true);using SqliteCommand cmd=c.CreateCommand(); var parts=new List<string>();
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubNew)) parts.Add("mc.change_kind=1");
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubNeverChecked)) parts.Add("r.file_id IS NULL");
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubStale)) parts.Add("r.file_id IS NOT NULL AND (r.method_version<>1 OR r.source_size_bytes<>f.size_bytes OR r.source_last_write_utc_ticks<>f.last_write_utc_ticks OR (r.source_volume_id<>'' AND r.source_volume_id<>f.volume_id) OR (r.source_file_identity<>'' AND r.source_file_identity<>f.file_identity))");
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubFailed)) parts.Add("r.result_state IN(5,8) AND (SELECT COUNT(*) FROM media_integrity_queue q WHERE q.file_id=f.id AND q.status=3)<3");
        if(profile.PeriodicQuickScrubDays>0) parts.Add("r.result_state=3 AND r.checked_utc_ticks<$due");
        if(parts.Count==0)return Array.Empty<long>();
        cmd.CommandText=$"SELECT DISTINCT f.id FROM indexed_files f LEFT JOIN media_integrity_results r ON r.file_id=f.id LEFT JOIN library_maintenance_candidates mc ON mc.file_id=f.id AND mc.run_id=$run JOIN file_location_memberships m ON m.file_id=f.id JOIN library_maintenance_runs mr ON mr.id=$run AND mr.location_id=m.location_id WHERE f.id>$after AND f.availability_state=0 AND m.availability_state=0 AND ({string.Join(" OR ",parts)}) ORDER BY f.id LIMIT $limit;";
        cmd.Parameters.AddWithValue("$run",runId);cmd.Parameters.AddWithValue("$after",afterFileId);cmd.Parameters.AddWithValue("$due",utcNow.AddDays(-profile.PeriodicQuickScrubDays).Ticks);cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,50_000));var ids=new List<long>();using SqliteDataReader r=cmd.ExecuteReader();while(r.Read())ids.Add(r.GetInt64(0));return ids;
    }

    public void CompleteMaintenanceRun(LibraryMaintenanceRun run)
    { WithWriteTransaction<object?>((c,t)=>{using SqliteCommand cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE library_maintenance_runs SET outcome=$outcome,stage=$stage,completed_utc_ticks=$completed,new_files=$new,changed_files=$changed,missing_files=$missing,metadata_queued=$metadata,exact_processed=$exact,visual_processed=$visual,integrity_queued=$integrity,warning_count=$warnings,details=$details WHERE id=$id; UPDATE library_maintenance_profiles SET last_scheduled_utc_ticks=CASE WHEN $scheduled=1 THEN $started ELSE last_scheduled_utc_ticks END,updated_utc_ticks=$completed WHERE location_id=$location; DELETE FROM library_maintenance_runs WHERE location_id=$location AND id NOT IN(SELECT id FROM library_maintenance_runs WHERE location_id=$location ORDER BY started_utc_ticks DESC LIMIT 100);";cmd.Parameters.AddWithValue("$outcome",(int)run.Outcome);cmd.Parameters.AddWithValue("$stage",run.Stage);cmd.Parameters.AddWithValue("$completed",(run.CompletedUtc??DateTime.UtcNow).Ticks);cmd.Parameters.AddWithValue("$new",run.NewFiles);cmd.Parameters.AddWithValue("$changed",run.ChangedFiles);cmd.Parameters.AddWithValue("$missing",run.MissingFiles);cmd.Parameters.AddWithValue("$metadata",run.MetadataQueued);cmd.Parameters.AddWithValue("$exact",run.ExactProcessed);cmd.Parameters.AddWithValue("$visual",run.VisualProcessed);cmd.Parameters.AddWithValue("$integrity",run.IntegrityQueued);cmd.Parameters.AddWithValue("$warnings",run.WarningCount);cmd.Parameters.AddWithValue("$details",Bound(run.Details,2000));cmd.Parameters.AddWithValue("$id",run.Id);cmd.Parameters.AddWithValue("$scheduled",run.Trigger==LibraryMaintenanceTrigger.Manual?0:1);cmd.Parameters.AddWithValue("$started",run.StartedUtc.Ticks);cmd.Parameters.AddWithValue("$location",run.LocationId);cmd.ExecuteNonQuery();return null;}); }

    public IReadOnlyList<LibraryMaintenanceRun> GetMaintenanceHistory(long? locationId=null,int limit=100)
    { using SqliteConnection c=_database.OpenConnection(readOnly:true);using SqliteCommand cmd=c.CreateCommand();cmd.CommandText="SELECT id,location_id,trigger_kind,outcome,stage,started_utc_ticks,completed_utc_ticks,new_files,changed_files,missing_files,metadata_queued,exact_processed,visual_processed,integrity_queued,warning_count,details FROM library_maintenance_runs WHERE ($location IS NULL OR location_id=$location) ORDER BY started_utc_ticks DESC LIMIT $limit;";cmd.Parameters.AddWithValue("$location",locationId??(object)DBNull.Value);cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,1000));var list=new List<LibraryMaintenanceRun>();using SqliteDataReader r=cmd.ExecuteReader();while(r.Read())list.Add(ReadRun(r));return list; }
    public int RecoverInterruptedMaintenance(DateTime utcNow) => WithWriteTransaction((c,t)=>{using SqliteCommand cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE library_maintenance_runs SET outcome=6,stage='Interrupted',completed_utc_ticks=$now,details='Application closed before maintenance reached a safe completion point; underlying durable queues retain completed work.' WHERE outcome=0;";cmd.Parameters.AddWithValue("$now",utcNow.Ticks);return cmd.ExecuteNonQuery();});

    private static LibraryMaintenanceProfile ReadProfile(long id,SqliteDataReader r)=>new(id,r.GetInt32(0),r.GetInt32(1)!=0,(LibraryMaintenanceCadence)r.GetInt32(2),(LibraryMaintenanceDays)r.GetInt32(3),TimeSpan.FromMinutes(r.GetInt32(4)),TimeSpan.FromMinutes(r.GetInt32(5)),(LibraryMaintenanceMissedRun)r.GetInt32(6),(LibraryMaintenanceActions)r.GetInt32(7),r.GetInt32(8),FromUtcTicks(r.GetInt64(9)),FromUtcTicks(r.GetInt64(10)),r.IsDBNull(11)?null:FromUtcTicks(r.GetInt64(11)));
    private static LibraryMaintenanceRun ReadRun(SqliteDataReader r)=>new(r.GetInt64(0),r.GetInt64(1),(LibraryMaintenanceTrigger)r.GetInt32(2),(LibraryMaintenanceOutcome)r.GetInt32(3),r.GetString(4),FromUtcTicks(r.GetInt64(5)),r.IsDBNull(6)?null:FromUtcTicks(r.GetInt64(6)),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9),r.GetInt64(10),r.GetInt64(11),r.GetInt64(12),r.GetInt64(13),r.GetInt64(14),r.GetString(15));
}
