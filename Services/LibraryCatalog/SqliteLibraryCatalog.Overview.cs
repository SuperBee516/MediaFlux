using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog;

public sealed partial class SqliteLibraryCatalog
{
    private const int OverviewHistoryRetention = 365;

    public LibraryOverviewSnapshot GetOverviewSnapshot(int metadataVersion)
    {
        ThrowIfDisposed();
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),COALESCE(SUM(size_bytes),0) FROM indexed_files;
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN is_enabled=1 AND availability_state=1 THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN availability_state IN (2,3) THEN 1 ELSE 0 END),0),
                   (SELECT COUNT(*) FROM scan_runs WHERE status=$scan_running),
                   (SELECT COUNT(*) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id
                    WHERE f.availability_state=$file_present AND (m.file_id IS NULL OR m.metadata_version<>$metadata_version
                    OR m.source_size_bytes<>f.size_bytes OR m.source_last_write_utc_ticks<>f.last_write_utc_ticks
                    OR m.probe_status IN ($probe_pending,$probe_running))),
                   MAX(last_completed_scan_utc_ticks)
            FROM library_locations;
            SELECT
              (SELECT COUNT(*) FROM exact_duplicate_groups g WHERE g.analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$analysis_completed)),
              COALESCE((SELECT COUNT(DISTINCT m.file_id) FROM exact_duplicate_groups g JOIN exact_duplicate_members m ON m.group_id=g.id WHERE g.analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$analysis_completed)),0),
              (SELECT COUNT(*) FROM visual_similarity_groups g WHERE g.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND NOT EXISTS(SELECT 1 FROM visual_family_edges e JOIN visual_families f ON f.id=e.family_id WHERE e.visual_group_id=g.id AND f.lifecycle_state=0)),
              COALESCE((SELECT COUNT(DISTINCT file_id) FROM (SELECT g.left_file_id AS file_id FROM visual_similarity_groups g WHERE g.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND NOT EXISTS(SELECT 1 FROM visual_family_edges e JOIN visual_families f ON f.id=e.family_id WHERE e.visual_group_id=g.id AND f.lifecycle_state=0) UNION SELECT g.right_file_id AS file_id FROM visual_similarity_groups g WHERE g.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND NOT EXISTS(SELECT 1 FROM visual_family_edges e JOIN visual_families f ON f.id=e.family_id WHERE e.visual_group_id=g.id AND f.lifecycle_state=0))),0),
              (SELECT COUNT(*) FROM visual_families f WHERE f.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed)),
              COALESCE((SELECT COUNT(DISTINCT m.file_id) FROM visual_families f JOIN visual_family_members m ON m.family_id=f.id WHERE f.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed)),0),
              COALESCE((SELECT COUNT(*) FROM exact_duplicate_groups g LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash WHERE g.analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$analysis_completed) AND d.reviewed=1),0)
               + COALESCE((SELECT COUNT(*) FROM visual_similarity_groups g LEFT JOIN visual_group_decisions d ON d.group_key=g.group_key WHERE g.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND d.reviewed=1 AND NOT EXISTS(SELECT 1 FROM visual_family_edges e JOIN visual_families f ON f.id=e.family_id WHERE e.visual_group_id=g.id AND f.lifecycle_state=0)),0)
               + COALESCE((SELECT COUNT(*) FROM visual_families f LEFT JOIN visual_family_decisions d ON d.family_key=f.family_key WHERE f.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND d.reviewed=1),0),
              COALESCE((SELECT COUNT(*) FROM exact_duplicate_groups g LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash WHERE g.analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$analysis_completed) AND COALESCE(d.reviewed,0)=0),0)
               + COALESCE((SELECT COUNT(*) FROM visual_similarity_groups g LEFT JOIN visual_group_decisions d ON d.group_key=g.group_key WHERE g.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND COALESCE(d.reviewed,0)=0 AND NOT EXISTS(SELECT 1 FROM visual_family_edges e JOIN visual_families f ON f.id=e.family_id WHERE e.visual_group_id=g.id AND f.lifecycle_state=0)),0)
               + COALESCE((SELECT COUNT(*) FROM visual_families f LEFT JOIN visual_family_decisions d ON d.family_key=f.family_key WHERE f.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND COALESCE(d.reviewed,0)=0),0),
              COALESCE((SELECT SUM(g.reclaimable_bytes) FROM exact_duplicate_groups g LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash WHERE g.analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$analysis_completed) AND COALESCE(d.ignored,0)=0),0),
              COALESCE((SELECT SUM(MIN(fl.size_bytes,fr.size_bytes)) FROM visual_similarity_groups g JOIN indexed_files fl ON fl.id=g.left_file_id JOIN indexed_files fr ON fr.id=g.right_file_id LEFT JOIN visual_group_decisions d ON d.group_key=g.group_key WHERE g.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND g.lifecycle_state=0 AND COALESCE(d.ignored,0)=0 AND COALESCE(d.not_match,0)=0 AND NOT EXISTS(SELECT 1 FROM visual_family_edges e JOIN visual_families f ON f.id=e.family_id WHERE e.visual_group_id=g.id AND f.lifecycle_state=0)),0),
              COALESCE((SELECT SUM(f.reclaimable_bytes) FROM visual_families f LEFT JOIN visual_family_decisions d ON d.family_key=f.family_key WHERE f.analysis_run_id=(SELECT MAX(id) FROM visual_analysis_runs WHERE status=$analysis_completed) AND f.lifecycle_state=0 AND COALESCE(d.ignored,0)=0),0);
            SELECT l.id,l.path,l.is_enabled,l.availability_state,COUNT(m.file_id),COALESCE(SUM(x.size_bytes),0),l.last_completed_scan_utc_ticks,l.last_error
            FROM library_locations l LEFT JOIN file_location_memberships m ON m.location_id=l.id LEFT JOIN indexed_files x ON x.id=m.file_id
            GROUP BY l.id,l.path,l.is_enabled,l.availability_state,l.last_completed_scan_utc_ticks,l.last_error ORDER BY l.path COLLATE NOCASE;
            SELECT CASE WHEN m.width IS NULL OR m.height IS NULL THEN 'Unknown' WHEN m.width>=7680 OR m.height>=4320 THEN '8K+' WHEN m.width>=3840 OR m.height>=2160 THEN '4K' WHEN m.width>=2560 OR m.height>=1440 THEN '1440p' WHEN m.width>=1920 OR m.height>=1080 THEN '1080p' WHEN m.width>=1280 OR m.height>=720 THEN '720p' ELSE 'SD' END,COUNT(*),COALESCE(SUM(f.size_bytes),0) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY 3 DESC,1;
            SELECT CASE WHEN m.video_codec IS NULL OR m.video_codec='' THEN 'Unknown' ELSE m.video_codec END,COUNT(*),COALESCE(SUM(f.size_bytes),0) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY 3 DESC,1;
            SELECT CASE WHEN m.format_name IS NULL OR m.format_name='' THEN 'Unknown' ELSE m.format_name END,COUNT(*),COALESCE(SUM(f.size_bytes),0) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY 3 DESC,1;
            SELECT (SELECT id FROM indexed_files ORDER BY size_bytes DESC,id LIMIT 1),(SELECT full_path FROM indexed_files ORDER BY size_bytes DESC,id LIMIT 1),(SELECT size_bytes FROM indexed_files ORDER BY size_bytes DESC,id LIMIT 1),AVG(CAST(size_bytes AS REAL)),(SELECT f.id FROM indexed_files f JOIN media_metadata m ON m.file_id=f.id WHERE m.duration_seconds IS NOT NULL ORDER BY m.duration_seconds DESC,f.id LIMIT 1),(SELECT f.full_path FROM indexed_files f JOIN media_metadata m ON m.file_id=f.id WHERE m.duration_seconds IS NOT NULL ORDER BY m.duration_seconds DESC,f.id LIMIT 1),(SELECT m.duration_seconds FROM indexed_files f JOIN media_metadata m ON m.file_id=f.id WHERE m.duration_seconds IS NOT NULL ORDER BY m.duration_seconds DESC,f.id LIMIT 1),AVG(CASE WHEN m.total_bitrate>0 THEN CAST(m.total_bitrate AS REAL) END),(SELECT CASE WHEN m.video_codec IS NULL OR m.video_codec='' THEN 'Unknown' ELSE m.video_codec END FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY COUNT(*) DESC,1 LIMIT 1),(SELECT CASE WHEN m.width IS NULL OR m.height IS NULL THEN 'Unknown' WHEN m.width>=7680 OR m.height>=4320 THEN '8K+' WHEN m.width>=3840 OR m.height>=2160 THEN '4K' WHEN m.width>=2560 OR m.height>=1440 THEN '1440p' WHEN m.width>=1920 OR m.height>=1080 THEN '1080p' WHEN m.width>=1280 OR m.height>=720 THEN '720p' ELSE 'SD' END FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY COUNT(*) DESC,1 LIMIT 1) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id;
            SELECT (SELECT COUNT(*) FROM library_locations WHERE availability_state=2),(SELECT COUNT(*) FROM library_locations WHERE availability_state=3),(SELECT COUNT(*) FROM indexed_files WHERE availability_state=1),(SELECT COUNT(*) FROM indexed_files WHERE availability_state=2),(SELECT COUNT(*) FROM media_metadata WHERE probe_status=3),(SELECT COUNT(*) FROM media_integrity_results WHERE result_state=4),(SELECT COUNT(*) FROM media_integrity_results WHERE result_state=5);
            """;
        command.Parameters.AddWithValue("$scan_running", (int)LibraryScanStatus.Running);
        command.Parameters.AddWithValue("$file_present", (int)IndexedFileAvailability.Present);
        command.Parameters.AddWithValue("$metadata_version", metadataVersion);
        command.Parameters.AddWithValue("$probe_pending", (int)LibraryProbeStatus.Pending);
        command.Parameters.AddWithValue("$probe_running", (int)LibraryProbeStatus.InProgress);
        command.Parameters.AddWithValue("$analysis_completed", (int)DuplicateAnalysisStatus.Completed);
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read(); long files = reader.GetInt64(0), bytes = reader.GetInt64(1);
        reader.NextResult(); reader.Read(); long locations = reader.GetInt64(0), available = reader.GetInt64(1), unavailable = reader.GetInt64(2), scans = reader.GetInt64(3), pending = reader.GetInt64(4); DateTime? lastScan = reader.IsDBNull(5) ? null : FromUtcTicks(reader.GetInt64(5));
        reader.NextResult(); reader.Read(); var duplicates = new LibraryOverviewDuplicateSummary(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10));
        reader.NextResult(); var locationRows = ReadLocations(reader);
        reader.NextResult(); var resolutions = ReadDistribution(reader);
        reader.NextResult(); var codecs = ReadDistribution(reader);
        reader.NextResult(); var containers = ReadDistribution(reader);
        reader.NextResult(); reader.Read(); var insight = new LibraryOverviewInsight(ReadLong(reader,0), ReadString(reader,1), ReadLong(reader,2), ReadDouble(reader,3), ReadLong(reader,4), ReadString(reader,5), ReadDouble(reader,6), ReadDouble(reader,7), ReadString(reader,8), ReadString(reader,9));
        reader.NextResult(); reader.Read(); var health = new LibraryOverviewHealth(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6));
        return new LibraryOverviewSnapshot(DateTime.UtcNow, files, bytes, locations, available, unavailable, scans, pending, lastScan, duplicates, locationRows, resolutions, codecs, containers, insight, health);
    }

    public IReadOnlyList<LibraryOverviewScanHistoryEntry> GetOverviewScanHistory(int limit = OverviewHistoryRetention)
    {
        ThrowIfDisposed();
        using SqliteConnection connection = _database.OpenConnection(readOnly: true); using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT completed_utc_ticks,indexed_file_count,logical_size_bytes,duplicate_group_count,reclaimable_bytes FROM library_overview_scan_history ORDER BY completed_utc_ticks DESC,id DESC LIMIT $limit;"; command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, OverviewHistoryRetention));
        using SqliteDataReader reader = command.ExecuteReader(); var rows = new List<LibraryOverviewScanHistoryEntry>(); while (reader.Read()) rows.Add(new(FromUtcTicks(reader.GetInt64(0)),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4))); return rows;
    }

    private static IReadOnlyList<LibraryOverviewDistribution> ReadDistribution(SqliteDataReader reader) { var rows = new List<LibraryOverviewDistribution>(); while (reader.Read()) rows.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetInt64(2))); return rows; }
    private static IReadOnlyList<LibraryOverviewLocation> ReadLocations(SqliteDataReader reader) { var rows = new List<LibraryOverviewLocation>(); while (reader.Read()) rows.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetInt32(2)!=0,(LibraryLocationAvailability)reader.GetInt32(3),reader.GetInt64(4),reader.GetInt64(5),reader.IsDBNull(6)?null:FromUtcTicks(reader.GetInt64(6)),reader.GetString(7))); return rows; }
    private static long? ReadLong(SqliteDataReader r, int o) => r.IsDBNull(o) ? null : r.GetInt64(o);
    private static double? ReadDouble(SqliteDataReader r, int o) => r.IsDBNull(o) ? null : r.GetDouble(o);
    private static string ReadString(SqliteDataReader r, int o) => r.IsDBNull(o) ? "" : r.GetString(o);

    private static void InsertOverviewHistorySnapshot(SqliteConnection connection, SqliteTransaction transaction, long completedTicks)
    {
        using SqliteCommand insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """INSERT INTO library_overview_scan_history(completed_utc_ticks,indexed_file_count,logical_size_bytes,duplicate_group_count,reclaimable_bytes) SELECT $completed,COUNT(*),COALESCE(SUM(size_bytes),0),(SELECT COUNT(*) FROM exact_duplicate_groups WHERE analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$completed_analysis)),COALESCE((SELECT SUM(reclaimable_bytes) FROM exact_duplicate_groups WHERE analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$completed_analysis)),0) FROM indexed_files;""";
        insert.Parameters.AddWithValue("$completed", completedTicks); insert.Parameters.AddWithValue("$completed_analysis", (int)DuplicateAnalysisStatus.Completed); insert.ExecuteNonQuery();
        using SqliteCommand trim = connection.CreateCommand(); trim.Transaction = transaction; trim.CommandText = "DELETE FROM library_overview_scan_history WHERE id NOT IN (SELECT id FROM library_overview_scan_history ORDER BY completed_utc_ticks DESC,id DESC LIMIT $keep);"; trim.Parameters.AddWithValue("$keep", OverviewHistoryRetention); trim.ExecuteNonQuery();
    }
}
