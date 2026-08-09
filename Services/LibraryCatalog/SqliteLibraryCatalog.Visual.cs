using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        public VisualAnalysisHandle BeginVisualAnalysis(string algorithm, int algorithmVersion)
        {
            if (string.IsNullOrWhiteSpace(algorithm)) throw new ArgumentException("An algorithm name is required.", nameof(algorithm));
            if (algorithmVersion < 1) throw new ArgumentOutOfRangeException(nameof(algorithmVersion));
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                long now = DateTime.UtcNow.Ticks;
                using (SqliteCommand interrupt = connection.CreateCommand())
                {
                    interrupt.Transaction = transaction;
                    interrupt.CommandText = "UPDATE visual_analysis_runs SET status=$interrupted,completed_utc_ticks=$now,error_text=CASE WHEN error_text='' THEN 'Superseded by a newer analysis.' ELSE error_text END WHERE status=$running; UPDATE visual_fingerprints SET status=$pending WHERE status=$in_progress;";
                    interrupt.Parameters.AddWithValue("$interrupted", (int)DuplicateAnalysisStatus.Interrupted);
                    interrupt.Parameters.AddWithValue("$running", (int)DuplicateAnalysisStatus.Running);
                    interrupt.Parameters.AddWithValue("$pending", (int)VisualFingerprintStatus.Pending);
                    interrupt.Parameters.AddWithValue("$in_progress", (int)VisualFingerprintStatus.InProgress);
                    interrupt.Parameters.AddWithValue("$now", now);
                    interrupt.ExecuteNonQuery();
                }
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO visual_analysis_runs(status,algorithm,algorithm_version,started_utc_ticks) VALUES($status,$algorithm,$version,$now) RETURNING id;";
                insert.Parameters.AddWithValue("$status", (int)DuplicateAnalysisStatus.Running);
                insert.Parameters.AddWithValue("$algorithm", algorithm);
                insert.Parameters.AddWithValue("$version", algorithmVersion);
                insert.Parameters.AddWithValue("$now", now);
                return new VisualAnalysisHandle(Convert.ToInt64(insert.ExecuteScalar()), FromUtcTicks(now));
            });
        }

        public void CompleteVisualAnalysis(VisualAnalysisHandle run, VisualAnalysisCompletion completion)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(completion);
            if (completion.Status == DuplicateAnalysisStatus.Running) throw new ArgumentException("A completed analysis cannot remain running.", nameof(completion));
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE visual_analysis_runs SET status=$status,completed_utc_ticks=$now,eligible_files=$eligible,fingerprinted_files=$fingerprinted,candidate_pairs=$candidates,match_pairs=$matches,error_count=$errors,error_text=$error WHERE id=$id AND status=$running;";
                command.Parameters.AddWithValue("$status", (int)completion.Status);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$eligible", completion.EligibleFiles);
                command.Parameters.AddWithValue("$fingerprinted", completion.FingerprintedFiles);
                command.Parameters.AddWithValue("$candidates", completion.CandidatePairs);
                command.Parameters.AddWithValue("$matches", completion.MatchPairs);
                command.Parameters.AddWithValue("$errors", completion.ErrorCount);
                command.Parameters.AddWithValue("$error", completion.ErrorText ?? "");
                command.Parameters.AddWithValue("$id", run.RunId);
                command.Parameters.AddWithValue("$running", (int)DuplicateAnalysisStatus.Running);
                command.ExecuteNonQuery();
                if (completion.Status is DuplicateAnalysisStatus.Completed or DuplicateAnalysisStatus.Canceled or DuplicateAnalysisStatus.Failed)
                {
                    using SqliteCommand trim = connection.CreateCommand();
                    trim.Transaction = transaction;
                    trim.CommandText = "DELETE FROM visual_candidate_pairs WHERE run_id<>$id;";
                    trim.Parameters.AddWithValue("$id", run.RunId);
                    trim.ExecuteNonQuery();
                }
                return null;
            });
        }

        public int RecoverInterruptedVisualWork()
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                long now = DateTime.UtcNow.Ticks;
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE visual_analysis_runs SET status=$interrupted,completed_utc_ticks=$now,error_text=CASE WHEN error_text='' THEN 'Interrupted by application shutdown.' ELSE error_text END WHERE status=$running; UPDATE visual_fingerprints SET status=$pending WHERE status=$in_progress;";
                command.Parameters.AddWithValue("$interrupted", (int)DuplicateAnalysisStatus.Interrupted);
                command.Parameters.AddWithValue("$running", (int)DuplicateAnalysisStatus.Running);
                command.Parameters.AddWithValue("$pending", (int)VisualFingerprintStatus.Pending);
                command.Parameters.AddWithValue("$in_progress", (int)VisualFingerprintStatus.InProgress);
                command.Parameters.AddWithValue("$now", now);
                return command.ExecuteNonQuery();
            });
        }

        public long CountVisualFingerprintCandidates(int algorithmVersion, string toolVersion)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = CreateVisualCandidateCommand(connection, countOnly: true);
            command.Parameters.AddWithValue("$version", algorithmVersion);
            command.Parameters.AddWithValue("$tool", toolVersion ?? "");
            command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
            command.Parameters.AddWithValue("$succeeded", (int)LibraryProbeStatus.Succeeded);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public IReadOnlyList<VisualFingerprintCandidate> GetVisualFingerprintCandidates(int algorithmVersion, string toolVersion, int limit)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = CreateVisualCandidateCommand(connection, countOnly: false);
            command.Parameters.AddWithValue("$version", algorithmVersion);
            command.Parameters.AddWithValue("$tool", toolVersion ?? "");
            command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
            command.Parameters.AddWithValue("$succeeded", (int)LibraryProbeStatus.Succeeded);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaximumPageSize));
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<VisualFingerprintCandidate>();
            while (reader.Read())
                result.Add(new VisualFingerprintCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), FromUtcTicks(reader.GetInt64(4)), reader.GetString(5), reader.GetString(6), reader.GetDouble(7)));
            return result;
        }

        private static SqliteCommand CreateVisualCandidateCommand(SqliteConnection connection, bool countOnly)
        {
            SqliteCommand command = connection.CreateCommand();
            string select = countOnly ? "SELECT COUNT(*)" : "SELECT f.id,f.full_path,f.path_key,f.size_bytes,f.last_write_utc_ticks,f.volume_id,f.file_identity,m.duration_seconds";
            string limit = countOnly ? "" : " ORDER BY f.id LIMIT $limit";
            command.CommandText = $"""
                {select}
                FROM indexed_files f
                JOIN media_metadata m ON m.file_id=f.id
                LEFT JOIN visual_fingerprints v ON v.file_id=f.id
                WHERE f.availability_state=$present AND m.probe_status=$succeeded AND m.duration_seconds>0
                  AND (v.file_id IS NULL OR v.source_size_bytes<>f.size_bytes OR v.source_last_write_utc_ticks<>f.last_write_utc_ticks
                       OR v.source_volume_id<>f.volume_id OR v.source_file_identity<>f.file_identity
                       OR v.algorithm_version<>$version OR v.tool_version<>$tool OR v.status<>2)
                  AND COALESCE(v.attempt_count,0)<3{limit};
                """;
            return command;
        }

        public VisualFingerprintFact? GetVisualFingerprint(long fileId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT file_id,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,algorithm,algorithm_version,frame_hashes,status,attempt_count,tool_version,error_message FROM visual_fingerprints WHERE file_id=$id;";
            command.Parameters.AddWithValue("$id", fileId);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadVisualFingerprint(reader) : null;
        }

        public void SaveVisualFingerprintBatch(IReadOnlyCollection<VisualFingerprintWrite> writes, string algorithm, int algorithmVersion)
        {
            ArgumentNullException.ThrowIfNull(writes);
            if (writes.Count == 0) return;
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                foreach (VisualFingerprintWrite write in writes)
                {
                    VisualFingerprintCandidate candidate = write.Candidate;
                    byte[]? bytes = write.FrameHashes.Count == 0 ? null : SerializeHashes(write.FrameHashes);
                    using SqliteCommand save = connection.CreateCommand();
                    save.Transaction = transaction;
                    save.CommandText =
                        """
                        INSERT INTO visual_fingerprints(file_id,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,
                            algorithm,algorithm_version,sample_count,frame_hashes,status,attempt_count,tool_version,error_message,updated_utc_ticks)
                        VALUES($id,$size,$modified,$volume,$identity,$algorithm,$version,$count,$hashes,$status,1,$tool,$error,$now)
                        ON CONFLICT(file_id) DO UPDATE SET
                            source_size_bytes=excluded.source_size_bytes,source_last_write_utc_ticks=excluded.source_last_write_utc_ticks,
                            source_volume_id=excluded.source_volume_id,source_file_identity=excluded.source_file_identity,
                            algorithm=excluded.algorithm,algorithm_version=excluded.algorithm_version,sample_count=excluded.sample_count,
                            frame_hashes=excluded.frame_hashes,status=excluded.status,
                            attempt_count=CASE WHEN excluded.status=2 THEN 1 ELSE visual_fingerprints.attempt_count+1 END,
                            tool_version=excluded.tool_version,error_message=excluded.error_message,updated_utc_ticks=excluded.updated_utc_ticks;
                        """;
                    save.Parameters.AddWithValue("$id", candidate.FileId);
                    save.Parameters.AddWithValue("$size", candidate.SizeBytes);
                    save.Parameters.AddWithValue("$modified", candidate.LastWriteUtc.Ticks);
                    save.Parameters.AddWithValue("$volume", candidate.VolumeId ?? "");
                    save.Parameters.AddWithValue("$identity", candidate.FileIdentity ?? "");
                    save.Parameters.AddWithValue("$algorithm", algorithm);
                    save.Parameters.AddWithValue("$version", algorithmVersion);
                    save.Parameters.AddWithValue("$count", write.FrameHashes.Count);
                    save.Parameters.Add("$hashes", SqliteType.Blob).Value = (object?)bytes ?? DBNull.Value;
                    save.Parameters.AddWithValue("$status", bytes == null ? (int)VisualFingerprintStatus.Failed : (int)VisualFingerprintStatus.Succeeded);
                    save.Parameters.AddWithValue("$tool", write.ToolVersion ?? "");
                    save.Parameters.AddWithValue("$error", write.ErrorMessage ?? "");
                    save.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    save.ExecuteNonQuery();

                    using SqliteCommand clearBands = connection.CreateCommand();
                    clearBands.Transaction = transaction;
                    clearBands.CommandText = "DELETE FROM visual_hash_bands WHERE file_id=$id;";
                    clearBands.Parameters.AddWithValue("$id", candidate.FileId);
                    clearBands.ExecuteNonQuery();
                    if (bytes == null) continue;

                    using SqliteCommand band = connection.CreateCommand();
                    band.Transaction = transaction;
                    band.CommandText = "INSERT INTO visual_hash_bands(file_id,algorithm_version,band_index,band_key) VALUES($file,$version,$index,$key);";
                    band.Parameters.Add("$file", SqliteType.Integer);
                    band.Parameters.Add("$version", SqliteType.Integer);
                    band.Parameters.Add("$index", SqliteType.Integer);
                    band.Parameters.Add("$key", SqliteType.Integer);
                    for (int sample = 0; sample < write.FrameHashes.Count; sample++)
                    {
                        ulong hash = write.FrameHashes[sample];
                        for (int part = 0; part < 4; part++)
                        {
                            band.Parameters["$file"].Value = candidate.FileId;
                            band.Parameters["$version"].Value = algorithmVersion;
                            band.Parameters["$index"].Value = sample * 4 + part;
                            band.Parameters["$key"].Value = (long)((hash >> (part * 16)) & 0xffff);
                            band.ExecuteNonQuery();
                        }
                    }
                }
                return null;
            });
        }

        public long BuildVisualCandidatePairs(VisualAnalysisHandle run, int algorithmVersion, int maximumBandBucket, int minimumBandMatches)
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    DELETE FROM visual_candidate_pairs WHERE run_id=$run;
                    INSERT INTO visual_candidate_pairs(run_id,left_file_id,right_file_id,band_matches)
                    WITH usable_bands AS (
                        SELECT algorithm_version,band_index,band_key
                        FROM visual_hash_bands WHERE algorithm_version=$version
                        GROUP BY algorithm_version,band_index,band_key
                        HAVING COUNT(*) BETWEEN 2 AND $max_bucket
                    ), collisions AS (
                        SELECT a.file_id AS left_id,b.file_id AS right_id,COUNT(*) AS matches
                        FROM usable_bands u
                        JOIN visual_hash_bands a ON a.algorithm_version=u.algorithm_version AND a.band_index=u.band_index AND a.band_key=u.band_key
                        JOIN visual_hash_bands b ON b.algorithm_version=u.algorithm_version AND b.band_index=u.band_index AND b.band_key=u.band_key AND a.file_id<b.file_id
                        JOIN media_metadata ma ON ma.file_id=a.file_id
                        JOIN media_metadata mb ON mb.file_id=b.file_id
                        JOIN indexed_files fa ON fa.id=a.file_id AND fa.availability_state=$present
                        JOIN indexed_files fb ON fb.id=b.file_id AND fb.availability_state=$present
                        WHERE ABS(ma.duration_seconds-mb.duration_seconds)<=MAX(3.0,MIN(ma.duration_seconds,mb.duration_seconds)*0.03)
                        GROUP BY a.file_id,b.file_id HAVING COUNT(*) >= $minimum
                    )
                    SELECT $run,left_id,right_id,matches FROM collisions;
                    SELECT COUNT(*) FROM visual_candidate_pairs WHERE run_id=$run;
                    """;
                command.Parameters.AddWithValue("$run", run.RunId);
                command.Parameters.AddWithValue("$version", algorithmVersion);
                command.Parameters.AddWithValue("$max_bucket", Math.Clamp(maximumBandBucket, 8, 1024));
                command.Parameters.AddWithValue("$minimum", Math.Clamp(minimumBandMatches, 1, 24));
                command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                return Convert.ToInt64(command.ExecuteScalar());
            });
        }

        public IReadOnlyList<VisualCandidatePair> GetVisualCandidatePairs(long runId, long afterLeftFileId, long afterRightFileId, int limit)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT p.left_file_id,p.right_file_id,p.band_matches,
                       vl.file_id,vl.source_size_bytes,vl.source_last_write_utc_ticks,vl.source_volume_id,vl.source_file_identity,vl.algorithm,vl.algorithm_version,vl.frame_hashes,vl.status,vl.attempt_count,vl.tool_version,vl.error_message,
                       vr.file_id,vr.source_size_bytes,vr.source_last_write_utc_ticks,vr.source_volume_id,vr.source_file_identity,vr.algorithm,vr.algorithm_version,vr.frame_hashes,vr.status,vr.attempt_count,vr.tool_version,vr.error_message,
                       ml.duration_seconds,mr.duration_seconds
                FROM visual_candidate_pairs p
                JOIN visual_fingerprints vl ON vl.file_id=p.left_file_id
                JOIN visual_fingerprints vr ON vr.file_id=p.right_file_id
                JOIN media_metadata ml ON ml.file_id=p.left_file_id
                JOIN media_metadata mr ON mr.file_id=p.right_file_id
                WHERE p.run_id=$run AND (p.left_file_id>$left OR (p.left_file_id=$left AND p.right_file_id>$right))
                ORDER BY p.left_file_id,p.right_file_id LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$left", afterLeftFileId);
            command.Parameters.AddWithValue("$right", afterRightFileId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaximumPageSize));
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<VisualCandidatePair>();
            while (reader.Read())
            {
                result.Add(new VisualCandidatePair(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                    ReadVisualFingerprint(reader, 3), ReadVisualFingerprint(reader, 15),
                    reader.GetDouble(27), reader.GetDouble(28)));
            }
            return result;
        }

        public void PrepareVisualSimilarityGroups(VisualAnalysisHandle run)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM visual_similarity_groups WHERE analysis_run_id=$run;";
                clear.Parameters.AddWithValue("$run", run.RunId);
                clear.ExecuteNonQuery();
                return null;
            });
        }

        public void AppendVisualSimilarityGroups(VisualAnalysisHandle run, IReadOnlyCollection<VisualMatchWrite> matches)
        {
            ArgumentNullException.ThrowIfNull(matches);
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand paths = connection.CreateCommand();
                paths.Transaction = transaction;
                paths.CommandText = "SELECT path_key FROM indexed_files WHERE id IN ($left,$right) ORDER BY path_key;";
                paths.Parameters.Add("$left", SqliteType.Integer);
                paths.Parameters.Add("$right", SqliteType.Integer);
                foreach (VisualMatchWrite match in matches)
                {
                    paths.Parameters["$left"].Value = match.LeftFileId;
                    paths.Parameters["$right"].Value = match.RightFileId;
                    var keys = new List<string>(2);
                    using (SqliteDataReader reader = paths.ExecuteReader()) while (reader.Read()) keys.Add(reader.GetString(0));
                    if (keys.Count != 2) continue;
                    string groupKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys))));
                    using SqliteCommand insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO visual_similarity_groups(group_key,analysis_run_id,left_file_id,right_file_id,confidence_score,
                            frame_matches,frame_comparisons,average_hash_distance,duration_delta_seconds,evidence_text,suggested_keeper_file_id,updated_utc_ticks)
                        SELECT $key,$run,$left,$right,$confidence,$matches,$comparisons,$distance,$duration,$evidence,
                               CASE WHEN (COALESCE(ml.width,0)*COALESCE(ml.height,0)*1000000+COALESCE(ml.total_bitrate,0)+fl.size_bytes)
                                           >=(COALESCE(mr.width,0)*COALESCE(mr.height,0)*1000000+COALESCE(mr.total_bitrate,0)+fr.size_bytes)
                                    THEN fl.id ELSE fr.id END,$now
                        FROM indexed_files fl,indexed_files fr
                        LEFT JOIN media_metadata ml ON ml.file_id=fl.id
                        LEFT JOIN media_metadata mr ON mr.file_id=fr.id
                        WHERE fl.id=$left AND fr.id=$right;
                        """;
                    insert.Parameters.AddWithValue("$key", groupKey);
                    insert.Parameters.AddWithValue("$run", run.RunId);
                    insert.Parameters.AddWithValue("$left", match.LeftFileId);
                    insert.Parameters.AddWithValue("$right", match.RightFileId);
                    insert.Parameters.AddWithValue("$confidence", match.ConfidenceScore);
                    insert.Parameters.AddWithValue("$matches", match.FrameMatches);
                    insert.Parameters.AddWithValue("$comparisons", match.FrameComparisons);
                    insert.Parameters.AddWithValue("$distance", match.AverageHashDistance);
                    insert.Parameters.AddWithValue("$duration", match.DurationDeltaSeconds);
                    insert.Parameters.AddWithValue("$evidence", match.EvidenceText);
                    insert.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    insert.ExecuteNonQuery();
                }
                return null;
            });
        }

        public void PublishVisualSimilarityGroups(VisualAnalysisHandle run)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM visual_similarity_groups WHERE analysis_run_id<>$run;";
                clear.Parameters.AddWithValue("$run", run.RunId);
                clear.ExecuteNonQuery();
                return null;
            });
        }

        public VisualSimilarityGroupPage QueryVisualGroups(VisualGroupQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            ThrowIfDisposed();
            int limit = Math.Clamp(query.Limit, 1, MaximumPageSize);
            int offset = Math.Max(0, query.Offset);
            string order = query.SortColumn.ToLowerInvariant() switch
            {
                "reclaimable" => "reclaimable_bytes",
                "duration" => "g.duration_delta_seconds",
                "reviewed" => "reviewed",
                _ => "g.confidence_score"
            };
            string direction = query.Descending ? "DESC" : "ASC";
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            string common =
                """
                FROM visual_similarity_groups g
                JOIN indexed_files fl ON fl.id=g.left_file_id
                JOIN indexed_files fr ON fr.id=g.right_file_id
                LEFT JOIN media_metadata ml ON ml.file_id=fl.id
                LEFT JOIN media_metadata mr ON mr.file_id=fr.id
                LEFT JOIN visual_group_decisions d ON d.group_key=g.group_key
                WHERE g.analysis_run_id=(SELECT id FROM visual_analysis_runs WHERE status=1 ORDER BY id DESC LIMIT 1)
                  AND ($group_id IS NULL OR g.id=$group_id)
                  AND g.confidence_score >= $confidence
                  AND ($search='' OR fl.full_path LIKE $like ESCAPE '\' OR fr.full_path LIKE $like ESCAPE '\')
                  AND ($reviewed<0 OR COALESCE(d.reviewed,0)=$reviewed)
                  AND ($ignored<0 OR COALESCE(d.ignored,0)=$ignored)
                  AND ($not_match<0 OR COALESCE(d.not_match,0)=$not_match)
                  AND ($codec_differs<0 OR (COALESCE(ml.video_codec,'')<>COALESCE(mr.video_codec,''))=$codec_differs)
                  AND ($resolution_differs<0 OR (COALESCE(ml.width,0)<>COALESCE(mr.width,0) OR COALESCE(ml.height,0)<>COALESCE(mr.height,0))=$resolution_differs)
                  AND ($location IS NULL OR EXISTS(SELECT 1 FROM file_location_memberships x WHERE x.location_id=$location AND x.file_id IN(g.left_file_id,g.right_file_id)))
                """;
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) " + common;
            AddVisualQueryParameters(count, query);
            long total = Convert.ToInt64(count.ExecuteScalar());
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT g.id,g.group_key,g.confidence_score,g.frame_matches,g.frame_comparisons,g.average_hash_distance,
                       g.duration_delta_seconds,g.evidence_text,g.left_file_id,g.right_file_id,g.suggested_keeper_file_id,
                       CASE WHEN d.manual_keeper_path_key=fl.path_key THEN fl.id WHEN d.manual_keeper_path_key=fr.path_key THEN fr.id END,
                       COALESCE(d.reviewed,0),COALESCE(d.ignored,0),COALESCE(d.not_match,0),
                       COALESCE(ml.video_codec,'')<>COALESCE(mr.video_codec,''),
                       COALESCE(ml.width,0)<>COALESCE(mr.width,0) OR COALESCE(ml.height,0)<>COALESCE(mr.height,0),
                       CASE WHEN fl.volume_id<>'' AND fl.volume_id=fr.volume_id AND fl.file_identity<>'' AND fl.file_identity=fr.file_identity THEN 0 ELSE MIN(fl.size_bytes,fr.size_bytes) END AS reclaimable_bytes
                """ + " " + common + $" ORDER BY {order} {direction},g.id LIMIT $limit OFFSET $offset;";
            AddVisualQueryParameters(command, query);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using SqliteDataReader reader = command.ExecuteReader();
            var groups = new List<VisualSimilarityGroupRecord>();
            while (reader.Read())
                groups.Add(new VisualSimilarityGroupRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetString(7), reader.GetInt64(8), reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.GetInt32(12) != 0, reader.GetInt32(13) != 0, reader.GetInt32(14) != 0, reader.GetInt32(15) != 0, reader.GetInt32(16) != 0, reader.GetInt64(17)));
            return new VisualSimilarityGroupPage(total, groups);
        }

        public VisualSimilarityGroupRecord? GetVisualGroup(long groupId)
        {
            VisualSimilarityGroupPage page = QueryVisualGroups(new VisualGroupQuery(GroupId: groupId, Limit: 1));
            return page.Groups.FirstOrDefault();
        }

        public VisualSimilarityGroupRecord? GetVisualGroupByKey(string groupKey)
        {
            if (string.IsNullOrWhiteSpace(groupKey)) return null;
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM visual_similarity_groups WHERE group_key=$key AND analysis_run_id=(SELECT id FROM visual_analysis_runs WHERE status=1 ORDER BY id DESC LIMIT 1) LIMIT 1;";
            command.Parameters.AddWithValue("$key", groupKey);
            object? id = command.ExecuteScalar();
            return id == null || id == DBNull.Value ? null : GetVisualGroup(Convert.ToInt64(id));
        }

        public void SetVisualSuggestedKeeper(long groupId, long? fileId)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE visual_similarity_groups SET suggested_keeper_file_id=$file,updated_utc_ticks=$now WHERE id=$group AND ($file IS NULL OR $file IN(left_file_id,right_file_id));";
                command.Parameters.AddWithValue("$file", (object?)fileId ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$group", groupId);
                command.ExecuteNonQuery();
                return null;
            });
        }

        public IReadOnlyList<VisualSimilarityMemberRecord> GetVisualGroupMembers(long groupId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT g.id,f.id,f.full_path,COALESCE((SELECT l.path FROM file_location_memberships x JOIN library_locations l ON l.id=x.location_id WHERE x.file_id=f.id ORDER BY l.path LIMIT 1),''),
                       f.size_bytes,f.last_write_utc_ticks,f.availability_state,COALESCE(m.video_codec,''),m.width,m.height,m.total_bitrate,m.duration_seconds,
                       EXISTS(SELECT 1 FROM duplicate_file_protections p WHERE p.path_key=f.path_key),f.id=g.suggested_keeper_file_id,
                       f.path_key=COALESCE(d.manual_keeper_path_key,'')
                FROM visual_similarity_groups g
                JOIN indexed_files f ON f.id IN(g.left_file_id,g.right_file_id)
                LEFT JOIN media_metadata m ON m.file_id=f.id
                LEFT JOIN visual_group_decisions d ON d.group_key=g.group_key
                WHERE g.id=$id ORDER BY f.id;
                """;
            command.Parameters.AddWithValue("$id", groupId);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<VisualSimilarityMemberRecord>(2);
            while (reader.Read())
                result.Add(new VisualSimilarityMemberRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), FromUtcTicks(reader.GetInt64(5)), (IndexedFileAvailability)reader.GetInt32(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetDouble(11), reader.GetInt32(12) != 0, reader.GetInt32(13) != 0, reader.GetInt32(14) != 0));
            return result;
        }

        public void SaveVisualDecision(VisualGroupDecision decision)
        {
            ArgumentNullException.ThrowIfNull(decision);
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO visual_group_decisions(group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks,not_match)
                    SELECT g.group_key,COALESCE(f.path_key,''),$reviewed,$ignored,$now,$not_match
                    FROM visual_similarity_groups g LEFT JOIN indexed_files f ON f.id=$keeper
                    WHERE g.id=$id
                      AND ($keeper IS NULL OR $keeper IN(g.left_file_id,g.right_file_id))
                    ON CONFLICT(group_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks,not_match=excluded.not_match;
                    """;
                command.Parameters.AddWithValue("$keeper", (object?)decision.ManualKeeperFileId ?? DBNull.Value);
                command.Parameters.AddWithValue("$reviewed", decision.Reviewed ? 1 : 0);
                command.Parameters.AddWithValue("$ignored", decision.Ignored ? 1 : 0);
                command.Parameters.AddWithValue("$not_match", decision.NotMatch ? 1 : 0);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$id", decision.GroupId);
                if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException($"Visual group {decision.GroupId} does not exist.");
                return null;
            });
        }

        private static VisualFingerprintFact ReadVisualFingerprint(SqliteDataReader reader, int offset = 0) => new(
            reader.GetInt64(offset), reader.GetInt64(offset + 1), FromUtcTicks(reader.GetInt64(offset + 2)), reader.GetString(offset + 3), reader.GetString(offset + 4),
            reader.GetString(offset + 5), reader.GetInt32(offset + 6), reader.IsDBNull(offset + 7) ? Array.Empty<ulong>() : DeserializeHashes((byte[])reader[offset + 7]),
            (VisualFingerprintStatus)reader.GetInt32(offset + 8), reader.GetInt32(offset + 9), reader.GetString(offset + 10), reader.GetString(offset + 11));

        private static byte[] SerializeHashes(IReadOnlyList<ulong> hashes)
        {
            byte[] bytes = new byte[hashes.Count * sizeof(ulong)];
            for (int index = 0; index < hashes.Count; index++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(index * 8, 8), hashes[index]);
            return bytes;
        }

        private static IReadOnlyList<ulong> DeserializeHashes(byte[] bytes)
        {
            if (bytes.Length % 8 != 0) return Array.Empty<ulong>();
            var hashes = new ulong[bytes.Length / 8];
            for (int index = 0; index < hashes.Length; index++) hashes[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(index * 8, 8));
            return hashes;
        }

        private static void AddVisualQueryParameters(SqliteCommand command, VisualGroupQuery query)
        {
            string search = (query.Search ?? "").Trim();
            command.Parameters.AddWithValue("$group_id", (object?)query.GroupId ?? DBNull.Value);
            command.Parameters.AddWithValue("$confidence", Math.Clamp(query.MinimumConfidence, 0, 100));
            command.Parameters.AddWithValue("$search", search);
            command.Parameters.AddWithValue("$like", $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
            command.Parameters.AddWithValue("$reviewed", query.Reviewed.HasValue ? (query.Reviewed.Value ? 1 : 0) : -1);
            command.Parameters.AddWithValue("$ignored", query.Ignored.HasValue ? (query.Ignored.Value ? 1 : 0) : -1);
            command.Parameters.AddWithValue("$not_match", query.NotMatch.HasValue ? (query.NotMatch.Value ? 1 : 0) : -1);
            command.Parameters.AddWithValue("$codec_differs", query.CodecDiffers.HasValue ? (query.CodecDiffers.Value ? 1 : 0) : -1);
            command.Parameters.AddWithValue("$resolution_differs", query.ResolutionDiffers.HasValue ? (query.ResolutionDiffers.Value ? 1 : 0) : -1);
            command.Parameters.AddWithValue("$location", (object?)query.LocationId ?? DBNull.Value);
        }
    }
}
