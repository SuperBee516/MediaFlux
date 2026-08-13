using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog;

public sealed partial class SqliteLibraryCatalog : ILibraryIntegrityCatalog
{
    private const int CurrentIntegrityMethodVersion = 1;

    public long EnqueueIntegrity(long fileId, LibraryIntegrityScrubType scrubType, string batchId = "", int maximumAttempts = 3)
    {
        ThrowIfDisposed();
        maximumAttempts = Math.Clamp(maximumAttempts, 1, 10);
        return WithWriteTransaction((connection, transaction) =>
        {
            using SqliteCommand existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id,scrub_type FROM media_integrity_queue WHERE file_id=$file AND status IN(0,1) LIMIT 1;";
            existing.Parameters.AddWithValue("$file", fileId);
            using SqliteDataReader reader = existing.ExecuteReader();
            if (reader.Read())
            {
                long id = reader.GetInt64(0);
                LibraryIntegrityScrubType current = (LibraryIntegrityScrubType)reader.GetInt32(1);
                reader.Close();
                if (scrubType > current)
                {
                    using SqliteCommand upgrade = connection.CreateCommand();
                    upgrade.Transaction = transaction;
                    upgrade.CommandText = "UPDATE media_integrity_queue SET scrub_type=$type,updated_utc_ticks=$now WHERE id=$id AND status=0;";
                    upgrade.Parameters.AddWithValue("$type", (int)scrubType); upgrade.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); upgrade.Parameters.AddWithValue("$id", id);
                    upgrade.ExecuteNonQuery();
                }
                return id;
            }
            reader.Close();
            long now = DateTime.UtcNow.Ticks;
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO media_integrity_queue(file_id,scrub_type,status,maximum_attempts,batch_id,created_utc_ticks,updated_utc_ticks) VALUES($file,$type,0,$max,$batch,$now,$now) RETURNING id;";
            command.Parameters.AddWithValue("$file", fileId); command.Parameters.AddWithValue("$type", (int)scrubType);
            command.Parameters.AddWithValue("$max", maximumAttempts); command.Parameters.AddWithValue("$batch", batchId ?? ""); command.Parameters.AddWithValue("$now", now);
            long queueId = Convert.ToInt64(command.ExecuteScalar());
            UpsertIntegrityState(connection, transaction, fileId, scrubType, LibraryIntegrityResultState.Pending, LibraryIntegrityErrorCategory.None, "Queued for integrity verification.");
            return queueId;
        });
    }

    public IReadOnlyList<LibraryIntegrityQueueItem> ClaimIntegrityBatch(int limit, DateTime utcNow)
    {
        ThrowIfDisposed(); limit = Math.Clamp(limit, 1, 16);
        return WithWriteTransaction((connection, transaction) =>
        {
            var ids = new List<long>();
            using (SqliteCommand select = connection.CreateCommand())
            {
                select.Transaction = transaction; select.CommandText = "SELECT id FROM media_integrity_queue WHERE status=0 AND attempt_count<maximum_attempts ORDER BY scrub_type,id LIMIT $limit;";
                select.Parameters.AddWithValue("$limit", limit);
                using SqliteDataReader reader = select.ExecuteReader(); while (reader.Read()) ids.Add(reader.GetInt64(0));
            }
            var result = new List<LibraryIntegrityQueueItem>();
            foreach (long id in ids)
            {
                using SqliteCommand update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE media_integrity_queue SET status=1,attempt_count=attempt_count+1,updated_utc_ticks=$now WHERE id=$id AND status=0;";
                update.Parameters.AddWithValue("$now", utcNow.Ticks); update.Parameters.AddWithValue("$id", id); if (update.ExecuteNonQuery() == 0) continue;
                using SqliteCommand read = connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = """
                    SELECT q.id,q.file_id,f.full_path,f.volume_id,f.size_bytes,f.last_write_utc_ticks,f.file_identity,
                           COALESCE(m.video_codec,''),m.duration_seconds,COALESCE(json_array_length(m.audio_streams_json),0),
                           q.scrub_type,q.status,q.attempt_count,q.maximum_attempts,q.batch_id,q.error_text,q.created_utc_ticks,q.updated_utc_ticks
                    FROM media_integrity_queue q JOIN indexed_files f ON f.id=q.file_id LEFT JOIN media_metadata m ON m.file_id=f.id WHERE q.id=$id;
                    """;
                read.Parameters.AddWithValue("$id", id); using SqliteDataReader row = read.ExecuteReader(); if (!row.Read()) continue;
                result.Add(ReadQueueItem(row));
            }
            foreach (LibraryIntegrityQueueItem item in result)
                UpsertIntegrityState(connection, transaction, item.FileId, item.ScrubType, LibraryIntegrityResultState.Running, LibraryIntegrityErrorCategory.None, "Integrity verification is running.");
            return result;
        });
    }

    public void CompleteIntegrityItem(long queueId, LibraryIntegrityResultWrite result, string errorText = "") =>
        FinishIntegrityItem(queueId, result, result.State == LibraryIntegrityResultState.Cancelled ? LibraryIntegrityQueueStatus.Cancelled :
            result.State is LibraryIntegrityResultState.Failed or LibraryIntegrityResultState.Unavailable ? LibraryIntegrityQueueStatus.Failed : LibraryIntegrityQueueStatus.Completed, errorText);

    public void CancelIntegrityItem(long queueId, LibraryIntegrityResultWrite result) =>
        FinishIntegrityItem(queueId, result, LibraryIntegrityQueueStatus.Cancelled, result.Details);

    public int RecoverInterruptedIntegrity()
    {
        ThrowIfDisposed();
        return WithWriteTransaction((connection, transaction) =>
        {
            var running = new List<(long Id, long FileId, LibraryIntegrityScrubType Type)>();
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction; read.CommandText = "SELECT id,file_id,scrub_type FROM media_integrity_queue WHERE status=1;";
                using SqliteDataReader reader = read.ExecuteReader(); while (reader.Read()) running.Add((reader.GetInt64(0), reader.GetInt64(1), (LibraryIntegrityScrubType)reader.GetInt32(2)));
            }
            foreach (var item in running)
            {
                bool restart = item.Type == LibraryIntegrityScrubType.Quick;
                using SqliteCommand update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE media_integrity_queue SET status=$status,error_text=$error,updated_utc_ticks=$now WHERE id=$id;";
                update.Parameters.AddWithValue("$status", restart ? 0 : 4); update.Parameters.AddWithValue("$error", restart ? "Recovered interrupted Quick Scrub." : "Full Scrub was interrupted and was not automatically restarted.");
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); update.Parameters.AddWithValue("$id", item.Id); update.ExecuteNonQuery();
                UpsertIntegrityState(connection, transaction, item.FileId, item.Type,
                    restart ? LibraryIntegrityResultState.Pending : LibraryIntegrityResultState.Cancelled,
                    restart ? LibraryIntegrityErrorCategory.None : LibraryIntegrityErrorCategory.Cancelled,
                    restart ? "Recovered interrupted Quick Scrub for retry." : "Full Scrub was interrupted; retry explicitly.");
            }
            return running.Count;
        });
    }

    public LibraryIntegrityPage QueryIntegrity(LibraryIntegrityQuery query)
    {
        ThrowIfDisposed(); int limit = Math.Clamp(query.Limit, 1, 500); int offset = Math.Max(0, query.Offset);
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        string where = BuildIntegrityWhere(query);
        using SqliteCommand count = connection.CreateCommand(); count.CommandText = IntegrityCte + $" SELECT COUNT(*) FROM integrity_view WHERE {where};"; AddIntegrityParameters(count, query);
        long total = Convert.ToInt64(count.ExecuteScalar());
        using SqliteCommand command = connection.CreateCommand(); command.CommandText = IntegrityCte + $" SELECT * FROM integrity_view WHERE {where} ORDER BY effective_state DESC,COALESCE(checked_ticks,0) DESC,file_id LIMIT $limit OFFSET $offset;";
        AddIntegrityParameters(command, query); command.Parameters.AddWithValue("$limit", limit); command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<LibraryIntegrityResult>(); using SqliteDataReader reader = command.ExecuteReader(); while (reader.Read()) rows.Add(ReadIntegrityResult(reader));
        return new LibraryIntegrityPage(total, rows);
    }

    public LibraryIntegritySummary GetIntegritySummary()
    {
        ThrowIfDisposed(); using SqliteConnection connection = _database.OpenConnection(readOnly: true); using SqliteCommand command = connection.CreateCommand();
        command.CommandText = IntegrityCte + " SELECT COUNT(*),SUM(effective_state=3),SUM(effective_state=4),SUM(effective_state=5),SUM(effective_state=0),SUM(effective_state=6),SUM(effective_state=1),SUM(effective_state=2),SUM(effective_state=8) FROM integrity_view;";
        using SqliteDataReader reader = command.ExecuteReader(); reader.Read(); long V(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt64(i);
        return new(V(0), V(1), V(2), V(3), V(4), V(5), V(6), V(7), V(8));
    }

    public IReadOnlyList<long> GetIntegrityFileIds(long? locationId, LibraryIntegrityResultState? state, int limit = 50_000)
    {
        LibraryIntegrityQuery query = new(state, locationId, Limit: Math.Clamp(limit, 1, 50_000));
        using SqliteConnection connection = _database.OpenConnection(readOnly: true); using SqliteCommand command = connection.CreateCommand();
        command.CommandText = IntegrityCte + $" SELECT file_id FROM integrity_view WHERE {BuildIntegrityWhere(query)} ORDER BY file_id LIMIT $limit;";
        AddIntegrityParameters(command, query); command.Parameters.AddWithValue("$limit", query.Limit); var ids = new List<long>();
        using SqliteDataReader reader = command.ExecuteReader(); while (reader.Read()) ids.Add(reader.GetInt64(0)); return ids;
    }

    public LibraryIntegrityResult? GetIntegrityResult(long fileId)
    {
        LibraryIntegrityPage page = QueryIntegrity(new LibraryIntegrityQuery(Search: $"__fileid:{fileId}", Limit: 1));
        return page.Results.FirstOrDefault();
    }

    private void FinishIntegrityItem(long queueId, LibraryIntegrityResultWrite result, LibraryIntegrityQueueStatus status, string errorText)
    {
        ThrowIfDisposed(); WithWriteTransaction<object?>((connection, transaction) =>
        {
            using SqliteCommand queue = connection.CreateCommand(); queue.Transaction = transaction;
            queue.CommandText = "UPDATE media_integrity_queue SET status=$status,error_text=$error,updated_utc_ticks=$now WHERE id=$id;";
            queue.Parameters.AddWithValue("$status", (int)status); queue.Parameters.AddWithValue("$error", Bound(errorText, 2000)); queue.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); queue.Parameters.AddWithValue("$id", queueId); queue.ExecuteNonQuery();
            SaveIntegrityResult(connection, transaction, result); return null;
        });
    }

    private static void SaveIntegrityResult(SqliteConnection connection, SqliteTransaction transaction, LibraryIntegrityResultWrite result)
    {
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO media_integrity_results(file_id,method_version,scrub_type,result_state,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,checked_utc_ticks,bytes_checked,media_duration_checked_seconds,elapsed_seconds,error_category,details,tool_version,updated_utc_ticks)
            VALUES($file,$version,$type,$state,$size,$write,$volume,$identity,$checked,$bytes,$duration,$elapsed,$category,$details,$tool,$now)
            ON CONFLICT(file_id) DO UPDATE SET method_version=excluded.method_version,scrub_type=excluded.scrub_type,result_state=excluded.result_state,source_size_bytes=excluded.source_size_bytes,source_last_write_utc_ticks=excluded.source_last_write_utc_ticks,source_volume_id=excluded.source_volume_id,source_file_identity=excluded.source_file_identity,checked_utc_ticks=excluded.checked_utc_ticks,bytes_checked=excluded.bytes_checked,media_duration_checked_seconds=excluded.media_duration_checked_seconds,elapsed_seconds=excluded.elapsed_seconds,error_category=excluded.error_category,details=excluded.details,tool_version=excluded.tool_version,updated_utc_ticks=excluded.updated_utc_ticks;
            """;
        command.Parameters.AddWithValue("$file", result.FileId); command.Parameters.AddWithValue("$version", result.MethodVersion); command.Parameters.AddWithValue("$type", (int)result.ScrubType); command.Parameters.AddWithValue("$state", (int)result.State);
        command.Parameters.AddWithValue("$size", result.SourceSizeBytes); command.Parameters.AddWithValue("$write", result.SourceLastWriteUtc.Ticks); command.Parameters.AddWithValue("$volume", result.SourceVolumeId ?? ""); command.Parameters.AddWithValue("$identity", result.SourceFileIdentity ?? "");
        command.Parameters.AddWithValue("$checked", result.CheckedUtc?.Ticks ?? (object)DBNull.Value); command.Parameters.AddWithValue("$bytes", Math.Max(0, result.BytesChecked)); command.Parameters.AddWithValue("$duration", Math.Max(0, result.MediaDurationCheckedSeconds)); command.Parameters.AddWithValue("$elapsed", Math.Max(0, result.ElapsedSeconds));
        command.Parameters.AddWithValue("$category", (int)result.ErrorCategory); command.Parameters.AddWithValue("$details", Bound(result.Details, 2000)); command.Parameters.AddWithValue("$tool", result.ToolVersion ?? ""); command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); command.ExecuteNonQuery();
    }

    private static void UpsertIntegrityState(SqliteConnection connection, SqliteTransaction transaction, long fileId, LibraryIntegrityScrubType type, LibraryIntegrityResultState state, LibraryIntegrityErrorCategory category, string details)
    {
        using SqliteCommand facts = connection.CreateCommand(); facts.Transaction = transaction;
        facts.CommandText = "SELECT size_bytes,last_write_utc_ticks,volume_id,file_identity FROM indexed_files WHERE id=$file;"; facts.Parameters.AddWithValue("$file", fileId);
        using SqliteDataReader reader = facts.ExecuteReader(); if (!reader.Read()) throw new KeyNotFoundException($"Library file {fileId} does not exist.");
        var write = new LibraryIntegrityResultWrite(fileId, CurrentIntegrityMethodVersion, type, state, null, reader.GetInt64(0), FromUtcTicks(reader.GetInt64(1)), reader.GetString(2), reader.GetString(3), 0, 0, 0, category, details, ""); reader.Close(); SaveIntegrityResult(connection, transaction, write);
    }

    private const string IntegrityCte = """
        WITH integrity_view AS (
          SELECT f.id file_id,f.full_path,f.file_name,
                 COALESCE((SELECT l.path FROM file_location_memberships membership JOIN library_locations l ON l.id=membership.location_id WHERE membership.file_id=f.id AND membership.availability_state=0 ORDER BY l.path LIMIT 1),'' ) location_path,
                 f.size_bytes,COALESCE(m.video_codec,'' ) video_codec,m.duration_seconds,
                 COALESCE(r.method_version,0) method_version,COALESCE(r.scrub_type,0) scrub_type,
                 CASE WHEN r.file_id IS NULL THEN 0 WHEN r.result_state IN(3,4,5) AND (r.method_version<>1 OR r.source_size_bytes<>f.size_bytes OR r.source_last_write_utc_ticks<>f.last_write_utc_ticks OR (r.source_volume_id<>'' AND r.source_volume_id<>f.volume_id) OR (r.source_file_identity<>'' AND r.source_file_identity<>f.file_identity)) THEN 6 ELSE r.result_state END effective_state,
                 r.checked_utc_ticks checked_ticks,COALESCE(r.source_size_bytes,f.size_bytes) source_size,COALESCE(r.source_last_write_utc_ticks,f.last_write_utc_ticks) source_write,COALESCE(r.source_volume_id,f.volume_id) source_volume,COALESCE(r.source_file_identity,f.file_identity) source_identity,
                 COALESCE(r.bytes_checked,0) bytes_checked,COALESCE(r.media_duration_checked_seconds,0) duration_checked,COALESCE(r.elapsed_seconds,0) elapsed,COALESCE(r.error_category,0) error_category,COALESCE(r.details,'' ) details,COALESCE(r.tool_version,'' ) tool_version,
                 CASE WHEN r.file_id IS NOT NULL AND (r.method_version<>1 OR r.source_size_bytes<>f.size_bytes OR r.source_last_write_utc_ticks<>f.last_write_utc_ticks OR (r.source_volume_id<>'' AND r.source_volume_id<>f.volume_id) OR (r.source_file_identity<>'' AND r.source_file_identity<>f.file_identity)) THEN 1 ELSE 0 END is_stale,
                 (SELECT q.id FROM media_integrity_queue q WHERE q.file_id=f.id AND q.status IN(0,1) ORDER BY q.id DESC LIMIT 1) queue_id,
                 (SELECT membership.location_id FROM file_location_memberships membership WHERE membership.file_id=f.id AND membership.availability_state=0 ORDER BY membership.location_id LIMIT 1) location_id
          FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id LEFT JOIN media_integrity_results r ON r.file_id=f.id
          WHERE f.availability_state=0)
        """;

    private static string BuildIntegrityWhere(LibraryIntegrityQuery query)
    {
        var clauses = new List<string> { "1=1" };
        if (query.State.HasValue) clauses.Add("effective_state=$state");
        if (query.LocationId.HasValue) clauses.Add("EXISTS(SELECT 1 FROM file_location_memberships membership_filter WHERE membership_filter.file_id=file_id AND membership_filter.availability_state=0 AND membership_filter.location_id=$location)");
        if (query.Search.StartsWith("__fileid:", StringComparison.Ordinal) && long.TryParse(query.Search[9..], out _)) clauses.Add("file_id=$fileid");
        else if (!string.IsNullOrWhiteSpace(query.Search)) clauses.Add("(full_path LIKE $search OR file_name LIKE $search)");
        return string.Join(" AND ", clauses);
    }

    private static void AddIntegrityParameters(SqliteCommand command, LibraryIntegrityQuery query)
    {
        if (query.State.HasValue) command.Parameters.AddWithValue("$state", (int)query.State.Value);
        if (query.LocationId.HasValue) command.Parameters.AddWithValue("$location", query.LocationId.Value);
        if (query.Search.StartsWith("__fileid:", StringComparison.Ordinal) && long.TryParse(query.Search[9..], out long fileId)) command.Parameters.AddWithValue("$fileid", fileId);
        else if (!string.IsNullOrWhiteSpace(query.Search)) command.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
    }

    private static LibraryIntegrityQueueItem ReadQueueItem(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4), FromUtcTicks(r.GetInt64(5)), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetDouble(8), r.GetInt32(9), (LibraryIntegrityScrubType)r.GetInt32(10), (LibraryIntegrityQueueStatus)r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetString(14), r.GetString(15), FromUtcTicks(r.GetInt64(16)), FromUtcTicks(r.GetInt64(17)));
    private static LibraryIntegrityResult ReadIntegrityResult(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.GetInt32(7), (LibraryIntegrityScrubType)r.GetInt32(8), (LibraryIntegrityResultState)r.GetInt32(9), r.IsDBNull(10) ? null : FromUtcTicks(r.GetInt64(10)), r.GetInt64(11), FromUtcTicks(r.GetInt64(12)), r.GetString(13), r.GetString(14), r.GetInt64(15), r.GetDouble(16), r.GetDouble(17), (LibraryIntegrityErrorCategory)r.GetInt32(18), r.GetString(19), r.GetString(20), r.GetInt32(21) != 0, r.IsDBNull(22) ? null : r.GetInt64(22));
    private static string Bound(string? value, int max) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
