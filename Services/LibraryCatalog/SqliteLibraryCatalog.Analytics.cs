using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        public ExactDuplicateGroupPage QueryDuplicateGroups(DuplicateGroupQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            ThrowIfDisposed();
            int limit = Math.Clamp(query.Limit, 1, 500);
            int offset = Math.Max(0, query.Offset);
            string order = query.SortColumn.ToLowerInvariant() switch
            {
                "copies" => "g.member_count",
                "size" => "g.size_bytes",
                "codec" => "video_codec",
                "resolution" => "resolution_tier",
                "reviewed" => "reviewed",
                _ => "g.reclaimable_bytes"
            };
            string direction = query.Descending ? "DESC" : "ASC";
            string filters =
                " WHERE g.analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$completed)" +
                " AND ($search='' OR EXISTS(SELECT 1 FROM exact_duplicate_members sm JOIN indexed_files sf ON sf.id=sm.file_id WHERE sm.group_id=g.id AND sf.full_path LIKE $search_pattern))" +
                " AND ($location IS NULL OR EXISTS(SELECT 1 FROM exact_duplicate_members lm JOIN file_location_memberships flm ON flm.file_id=lm.file_id WHERE lm.group_id=g.id AND flm.location_id=$location))" +
                " AND ($codec='' OR EXISTS(SELECT 1 FROM exact_duplicate_members cm JOIN media_metadata cmeta ON cmeta.file_id=cm.file_id WHERE cm.group_id=g.id AND cmeta.video_codec=$codec))" +
                " AND ($resolution='' OR EXISTS(SELECT 1 FROM exact_duplicate_members rm LEFT JOIN media_metadata rmeta ON rmeta.file_id=rm.file_id WHERE rm.group_id=g.id AND " + ResolutionTierSql("rmeta") + "=$resolution))" +
                " AND ($reviewed IS NULL OR COALESCE(d.reviewed,0)=$reviewed)" +
                " AND ($ignored IS NULL OR COALESCE(d.ignored,0)=$ignored)" +
                " AND ($protected IS NULL OR (CASE WHEN EXISTS(SELECT 1 FROM exact_duplicate_members pm JOIN indexed_files pf ON pf.id=pm.file_id JOIN duplicate_file_protections p ON p.path_key=pf.path_key WHERE pm.group_id=g.id) THEN 1 ELSE 0 END)=$protected)";

            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM exact_duplicate_groups g LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash" + filters + ";";
            AddDuplicateQueryParameters(count, query);
            long total = Convert.ToInt64(count.ExecuteScalar());

            using SqliteCommand page = connection.CreateCommand();
            page.CommandText =
                """
                SELECT g.id,g.size_bytes,g.full_algorithm,g.full_version,g.full_hash,g.member_count,
                       g.physical_copy_count,g.reclaimable_bytes,g.suggested_keeper_file_id,
                       (SELECT mf.id FROM indexed_files mf WHERE mf.path_key=d.manual_keeper_path_key LIMIT 1),
                       COALESCE(d.reviewed,0),COALESCE(d.ignored,0),
                       (SELECT COUNT(*) FROM exact_duplicate_members pm JOIN indexed_files pf ON pf.id=pm.file_id JOIN duplicate_file_protections p ON p.path_key=pf.path_key WHERE pm.group_id=g.id),
                       COALESCE((SELECT MIN(meta.video_codec) FROM exact_duplicate_members vm JOIN media_metadata meta ON meta.file_id=vm.file_id WHERE vm.group_id=g.id AND meta.video_codec<>''),'') AS video_codec,
                       COALESCE((SELECT MIN(
                """ + ResolutionTierSql("meta") +
                """
                       ) FROM exact_duplicate_members rm JOIN media_metadata meta ON meta.file_id=rm.file_id WHERE rm.group_id=g.id),'Unknown') AS resolution_tier
                FROM exact_duplicate_groups g
                LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash
                """ + filters + $" ORDER BY {order} {direction},g.id {direction} LIMIT $limit OFFSET $offset;";
            AddDuplicateQueryParameters(page, query);
            page.Parameters.AddWithValue("$limit", limit);
            page.Parameters.AddWithValue("$offset", offset);
            using SqliteDataReader reader = page.ExecuteReader();
            var groups = new List<ExactDuplicateGroupRecord>(limit);
            while (reader.Read())
            {
                groups.Add(new ExactDuplicateGroupRecord(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), (byte[])reader[4],
                    reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.GetInt32(10) != 0, reader.GetInt32(11) != 0,
                    reader.GetInt32(12), reader.GetString(13), reader.GetString(14)));
            }
            return new ExactDuplicateGroupPage(total, groups);
        }

        public IReadOnlyList<ExactDuplicateMemberRecord> GetDuplicateGroupMembers(long groupId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT m.group_id,f.id,f.full_path,f.path_key,
                       COALESCE((SELECT MIN(l.path) FROM file_location_memberships fm JOIN library_locations l ON l.id=fm.location_id WHERE fm.file_id=f.id),''),
                       f.size_bytes,f.last_write_utc_ticks,f.volume_id,f.file_identity,m.physical_identity_key,m.is_hard_link_alias,
                       f.availability_state,COALESCE(meta.video_codec,''),meta.width,meta.height,meta.total_bitrate,meta.duration_seconds,
                       CASE WHEN p.path_key IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN g.suggested_keeper_file_id=f.id THEN 1 ELSE 0 END,
                       CASE WHEN d.manual_keeper_path_key=f.path_key AND d.manual_keeper_path_key<>'' THEN 1 ELSE 0 END
                FROM exact_duplicate_members m JOIN indexed_files f ON f.id=m.file_id
                JOIN exact_duplicate_groups g ON g.id=m.group_id
                LEFT JOIN media_metadata meta ON meta.file_id=f.id
                LEFT JOIN duplicate_file_protections p ON p.path_key=f.path_key
                LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash
                WHERE m.group_id=$group ORDER BY m.is_hard_link_alias,f.path_key;
                """;
            command.Parameters.AddWithValue("$group", groupId);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<ExactDuplicateMemberRecord>();
            while (reader.Read())
            {
                result.Add(new ExactDuplicateMemberRecord(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                    FromUtcTicks(reader.GetInt64(6)), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt32(10) != 0,
                    (IndexedFileAvailability)reader.GetInt32(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : reader.GetInt32(14), reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    reader.IsDBNull(16) ? null : reader.GetDouble(16), reader.GetInt32(17) != 0, reader.GetInt32(18) != 0, reader.GetInt32(19) != 0));
            }
            return result;
        }

        public ExactDuplicateGroupRecord? GetDuplicateGroup(long groupId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT g.id,g.size_bytes,g.full_algorithm,g.full_version,g.full_hash,g.member_count,
                       g.physical_copy_count,g.reclaimable_bytes,g.suggested_keeper_file_id,
                       (SELECT mf.id FROM indexed_files mf WHERE mf.path_key=d.manual_keeper_path_key LIMIT 1),
                       COALESCE(d.reviewed,0),COALESCE(d.ignored,0),
                       (SELECT COUNT(*) FROM exact_duplicate_members pm JOIN indexed_files pf ON pf.id=pm.file_id JOIN duplicate_file_protections p ON p.path_key=pf.path_key WHERE pm.group_id=g.id),
                       COALESCE((SELECT MIN(meta.video_codec) FROM exact_duplicate_members vm JOIN media_metadata meta ON meta.file_id=vm.file_id WHERE vm.group_id=g.id AND meta.video_codec<>''),''),
                       COALESCE((SELECT MIN(
                """ + ResolutionTierSql("meta") +
                """
                       ) FROM exact_duplicate_members rm JOIN media_metadata meta ON meta.file_id=rm.file_id WHERE rm.group_id=g.id),'Unknown')
                FROM exact_duplicate_groups g
                LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash
                WHERE g.id=$id AND g.analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$completed);
                """;
            command.Parameters.AddWithValue("$id", groupId);
            command.Parameters.AddWithValue("$completed", (int)DuplicateAnalysisStatus.Completed);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new ExactDuplicateGroupRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), (byte[])reader[4], reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.GetInt32(10) != 0, reader.GetInt32(11) != 0, reader.GetInt32(12), reader.GetString(13), reader.GetString(14));
        }

        public void SetSuggestedKeeper(long groupId, long? fileId)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                if (fileId.HasValue)
                {
                    using SqliteCommand validate = connection.CreateCommand();
                    validate.Transaction = transaction;
                    validate.CommandText = "SELECT COUNT(*) FROM exact_duplicate_members WHERE group_id=$group AND file_id=$file;";
                    validate.Parameters.AddWithValue("$group", groupId);
                    validate.Parameters.AddWithValue("$file", fileId.Value);
                    if (Convert.ToInt64(validate.ExecuteScalar()) != 1)
                        throw new InvalidOperationException("The suggested keeper is not a member of this group.");
                }
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE exact_duplicate_groups SET suggested_keeper_file_id=$file,updated_utc_ticks=$now WHERE id=$group;";
                update.Parameters.AddWithValue("$file", fileId.HasValue ? fileId.Value : DBNull.Value);
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                update.Parameters.AddWithValue("$group", groupId);
                update.ExecuteNonQuery();
                return null;
            });
        }

        public void SaveDuplicateDecision(DuplicateGroupDecision decision)
        {
            ArgumentNullException.ThrowIfNull(decision);
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO duplicate_group_decisions(size_bytes,full_algorithm,full_version,full_hash,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks)
                    SELECT g.size_bytes,g.full_algorithm,g.full_version,g.full_hash,COALESCE(f.path_key,''),$reviewed,$ignored,$now
                    FROM exact_duplicate_groups g LEFT JOIN indexed_files f ON f.id=$keeper
                    WHERE g.id=$group
                    ON CONFLICT(size_bytes,full_algorithm,full_version,full_hash) DO UPDATE SET
                        manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks;
                    """;
                command.Parameters.AddWithValue("$keeper", decision.ManualKeeperFileId.HasValue ? decision.ManualKeeperFileId.Value : DBNull.Value);
                command.Parameters.AddWithValue("$reviewed", decision.Reviewed ? 1 : 0);
                command.Parameters.AddWithValue("$ignored", decision.Ignored ? 1 : 0);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$group", decision.GroupId);
                if (command.ExecuteNonQuery() != 1)
                    throw new KeyNotFoundException($"Duplicate group {decision.GroupId} does not exist.");
                return null;
            });
        }

        public void SetFileProtection(long fileId, bool isProtected, string reason = "")
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = isProtected
                    ? "INSERT INTO duplicate_file_protections(path_key,protected_path,reason,updated_utc_ticks) SELECT path_key,full_path,$reason,$now FROM indexed_files WHERE id=$file ON CONFLICT(path_key) DO UPDATE SET protected_path=excluded.protected_path,reason=excluded.reason,updated_utc_ticks=excluded.updated_utc_ticks;"
                    : "DELETE FROM duplicate_file_protections WHERE path_key=(SELECT path_key FROM indexed_files WHERE id=$file);";
                command.Parameters.AddWithValue("$file", fileId);
                command.Parameters.AddWithValue("$reason", reason ?? "");
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.ExecuteNonQuery();
                return null;
            });
        }

        public LibraryStatistics GetLibraryStatistics(int topCount = 10)
        {
            ThrowIfDisposed();
            topCount = Math.Clamp(topCount, 1, 50);
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            long Scalar(string sql, params (string Name, object Value)[] parameters)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = sql;
                foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
                return Convert.ToInt64(command.ExecuteScalar());
            }
            long totalFiles = Scalar("SELECT COUNT(*) FROM indexed_files;");
            long totalBytes = Scalar("SELECT COALESCE(SUM(size_bytes),0) FROM indexed_files;");
            long present = Scalar("SELECT COUNT(*) FROM indexed_files WHERE availability_state=$v;", ("$v", (int)IndexedFileAvailability.Present));
            long missing = Scalar("SELECT COUNT(*) FROM indexed_files WHERE availability_state=$v;", ("$v", (int)IndexedFileAvailability.Missing));
            long unavailable = Scalar("SELECT COUNT(*) FROM indexed_files WHERE availability_state=$v;", ("$v", (int)IndexedFileAvailability.Unavailable));
            long probeSucceeded = Scalar("SELECT COUNT(*) FROM media_metadata WHERE probe_status=$v;", ("$v", (int)LibraryProbeStatus.Succeeded));
            long probePending = Scalar("SELECT COUNT(*) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id WHERE m.file_id IS NULL OR m.probe_status IN ($p,$i);", ("$p", (int)LibraryProbeStatus.Pending), ("$i", (int)LibraryProbeStatus.InProgress));
            long probeFailed = Scalar("SELECT COUNT(*) FROM media_metadata WHERE probe_status=$v;", ("$v", (int)LibraryProbeStatus.Failed));
            long groups = Scalar("SELECT COUNT(*) FROM exact_duplicate_groups WHERE analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$v);", ("$v", (int)DuplicateAnalysisStatus.Completed));
            long duplicateFiles = Scalar("SELECT COALESCE(SUM(member_count),0) FROM exact_duplicate_groups WHERE analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$v);", ("$v", (int)DuplicateAnalysisStatus.Completed));
            long duplicateBytes = Scalar("SELECT COALESCE(SUM(size_bytes*member_count),0) FROM exact_duplicate_groups WHERE analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$v);", ("$v", (int)DuplicateAnalysisStatus.Completed));
            long reclaimable = Scalar("SELECT COALESCE(SUM(reclaimable_bytes),0) FROM exact_duplicate_groups WHERE analysis_run_id=(SELECT MAX(id) FROM duplicate_analysis_runs WHERE status=$v);", ("$v", (int)DuplicateAnalysisStatus.Completed));

            IReadOnlyList<LibraryStatisticBucket> byLocation = ReadBuckets(connection,
                "SELECT l.path,COUNT(DISTINCT fm.file_id),COALESCE(SUM(f.size_bytes),0) FROM library_locations l LEFT JOIN file_location_memberships fm ON fm.location_id=l.id AND fm.availability_state=0 LEFT JOIN indexed_files f ON f.id=fm.file_id GROUP BY l.id,l.path ORDER BY 3 DESC", topCount);
            IReadOnlyList<LibraryStatisticBucket> byCodec = ReadBuckets(connection,
                "SELECT CASE WHEN m.video_codec='' OR m.video_codec IS NULL THEN 'Unknown' ELSE m.video_codec END,COUNT(*),COALESCE(SUM(f.size_bytes),0) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY 3 DESC", topCount);
            IReadOnlyList<LibraryStatisticBucket> byResolution = ReadBuckets(connection,
                "SELECT " + ResolutionTierSql("m") + ",COUNT(*),COALESCE(SUM(f.size_bytes),0) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY 3 DESC", topCount);
            IReadOnlyList<LibraryStatisticBucket> byContainer = ReadBuckets(connection,
                "SELECT CASE WHEN m.format_name='' OR m.format_name IS NULL THEN 'Unknown' ELSE m.format_name END,COUNT(*),COALESCE(SUM(f.size_bytes),0) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY 3 DESC", topCount);
            IReadOnlyList<LibraryStatisticBucket> byDynamicRange = ReadBuckets(connection,
                "SELECT CASE WHEN m.file_id IS NULL OR (m.color_transfer='' AND m.color_primaries='') THEN 'Unknown' WHEN lower(m.color_transfer) IN ('smpte2084','arib-std-b67') OR lower(m.color_primaries)='bt2020' THEN 'HDR' ELSE 'SDR' END,COUNT(*),COALESCE(SUM(f.size_bytes),0) FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id GROUP BY 1 ORDER BY 3 DESC", topCount);

            using SqliteCommand largestCommand = connection.CreateCommand();
            largestCommand.CommandText = "SELECT f.id,f.file_name,f.full_path,f.size_bytes,COALESCE(m.video_codec,'')," + ResolutionTierSql("m") + " FROM indexed_files f LEFT JOIN media_metadata m ON m.file_id=f.id ORDER BY f.size_bytes DESC,f.id LIMIT $limit;";
            largestCommand.Parameters.AddWithValue("$limit", topCount);
            using SqliteDataReader reader = largestCommand.ExecuteReader();
            var largest = new List<LibraryLargestFile>();
            while (reader.Read()) largest.Add(new LibraryLargestFile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));

            return new LibraryStatistics(totalFiles, totalBytes, present, missing, unavailable, probeSucceeded, probePending, probeFailed,
                groups, duplicateFiles, duplicateBytes, reclaimable, byLocation, byCodec, byResolution, byContainer, byDynamicRange, largest);
        }

        private static IReadOnlyList<LibraryStatisticBucket> ReadBuckets(SqliteConnection connection, string sql, int topCount)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            using SqliteDataReader reader = command.ExecuteReader();
            var all = new List<LibraryStatisticBucket>();
            while (reader.Read()) all.Add(new LibraryStatisticBucket(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            if (all.Count <= topCount) return all;
            var result = all.Take(topCount).ToList();
            result.Add(new LibraryStatisticBucket("Other", all.Skip(topCount).Sum(x => x.FileCount), all.Skip(topCount).Sum(x => x.SizeBytes)));
            return result;
        }

        private static string ResolutionTierSql(string alias) =>
            $"CASE WHEN {alias}.width IS NULL OR {alias}.height IS NULL THEN 'Unknown' WHEN {alias}.width>=7680 OR {alias}.height>=4320 THEN '8K+' WHEN {alias}.width>=3840 OR {alias}.height>=2160 THEN '4K' WHEN {alias}.width>=2560 OR {alias}.height>=1440 THEN '1440p' WHEN {alias}.width>=1920 OR {alias}.height>=1080 THEN '1080p' WHEN {alias}.width>=1280 OR {alias}.height>=720 THEN '720p' ELSE 'SD' END";

        private static void AddDuplicateQueryParameters(SqliteCommand command, DuplicateGroupQuery query)
        {
            string search = (query.Search ?? "").Trim();
            command.Parameters.AddWithValue("$completed", (int)DuplicateAnalysisStatus.Completed);
            command.Parameters.AddWithValue("$search", search);
            command.Parameters.AddWithValue("$search_pattern", $"%{search}%");
            command.Parameters.AddWithValue("$location", query.LocationId.HasValue ? query.LocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$codec", query.Codec ?? "");
            command.Parameters.AddWithValue("$resolution", query.ResolutionTier ?? "");
            command.Parameters.AddWithValue("$reviewed", query.Reviewed.HasValue ? (query.Reviewed.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$ignored", query.Ignored.HasValue ? (query.Ignored.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$protected", query.Protected.HasValue ? (query.Protected.Value ? 1 : 0) : DBNull.Value);
        }
    }
}
