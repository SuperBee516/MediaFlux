using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        public DuplicateAnalysisHandle BeginDuplicateAnalysis(
            string quickAlgorithm,
            int quickVersion,
            string fullAlgorithm,
            int fullVersion)
        {
            if (string.IsNullOrWhiteSpace(quickAlgorithm) || string.IsNullOrWhiteSpace(fullAlgorithm))
                throw new ArgumentException("Hash algorithm names are required.");
            if (quickVersion < 1 || fullVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(quickVersion));
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                long now = DateTime.UtcNow.Ticks;
                using SqliteCommand interrupt = connection.CreateCommand();
                interrupt.Transaction = transaction;
                interrupt.CommandText =
                    "UPDATE duplicate_analysis_runs SET status=$interrupted, completed_utc_ticks=$now, error_text=CASE WHEN error_text='' THEN 'Superseded by a newer analysis.' ELSE error_text END WHERE status=$running;";
                interrupt.Parameters.AddWithValue("$interrupted", (int)DuplicateAnalysisStatus.Interrupted);
                interrupt.Parameters.AddWithValue("$running", (int)DuplicateAnalysisStatus.Running);
                interrupt.Parameters.AddWithValue("$now", now);
                interrupt.ExecuteNonQuery();

                using SqliteCommand retry = connection.CreateCommand();
                retry.Transaction = transaction;
                retry.CommandText = "UPDATE file_hash_facts SET failure_count=0,error_message='',updated_utc_ticks=$now WHERE failure_count>=3;";
                retry.Parameters.AddWithValue("$now", now);
                retry.ExecuteNonQuery();

                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO duplicate_analysis_runs(status,quick_algorithm,quick_version,full_algorithm,full_version,started_utc_ticks) VALUES($status,$qa,$qv,$fa,$fv,$now) RETURNING id;";
                insert.Parameters.AddWithValue("$status", (int)DuplicateAnalysisStatus.Running);
                insert.Parameters.AddWithValue("$qa", quickAlgorithm);
                insert.Parameters.AddWithValue("$qv", quickVersion);
                insert.Parameters.AddWithValue("$fa", fullAlgorithm);
                insert.Parameters.AddWithValue("$fv", fullVersion);
                insert.Parameters.AddWithValue("$now", now);
                return new DuplicateAnalysisHandle(Convert.ToInt64(insert.ExecuteScalar()), FromUtcTicks(now));
            });
        }

        public void CompleteDuplicateAnalysis(DuplicateAnalysisHandle run, DuplicateAnalysisCompletion completion)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(completion);
            if (completion.Status == DuplicateAnalysisStatus.Running)
                throw new ArgumentException("A completed analysis cannot remain running.", nameof(completion));
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE duplicate_analysis_runs SET status=$status, completed_utc_ticks=$now,
                        size_candidates=$size, quick_hashed=$quick, full_hashed=$full,
                        exact_groups=$groups, error_count=$errors, error_text=$error
                    WHERE id=$id AND status=$running;
                    """;
                command.Parameters.AddWithValue("$status", (int)completion.Status);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$size", completion.SizeCandidates);
                command.Parameters.AddWithValue("$quick", completion.QuickHashed);
                command.Parameters.AddWithValue("$full", completion.FullHashed);
                command.Parameters.AddWithValue("$groups", completion.ExactGroups);
                command.Parameters.AddWithValue("$errors", completion.ErrorCount);
                command.Parameters.AddWithValue("$error", completion.ErrorText ?? "");
                command.Parameters.AddWithValue("$id", run.RunId);
                command.Parameters.AddWithValue("$running", (int)DuplicateAnalysisStatus.Running);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The duplicate analysis is no longer running.");
                return null;
            });
        }

        public int RecoverInterruptedDuplicateWork()
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE duplicate_analysis_runs SET status=$interrupted, completed_utc_ticks=$now, error_text=CASE WHEN error_text='' THEN 'Application exited during duplicate analysis.' ELSE error_text END WHERE status=$running;";
                command.Parameters.AddWithValue("$interrupted", (int)DuplicateAnalysisStatus.Interrupted);
                command.Parameters.AddWithValue("$running", (int)DuplicateAnalysisStatus.Running);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                return command.ExecuteNonQuery();
            });
        }

        public long CountSizeCandidates()
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM indexed_files f WHERE f.availability_state=$present AND EXISTS (SELECT 1 FROM indexed_files other WHERE other.availability_state=$present AND other.size_bytes=f.size_bytes AND other.id<>f.id);";
            command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public IReadOnlyList<LibraryHashCandidate> GetQuickHashCandidates(int quickVersion, int limit) =>
            ReadHashCandidates(quickVersion, fullVersion: 0, Math.Clamp(limit, 1, 10_000), fullStage: false);

        public IReadOnlyList<LibraryHashCandidate> GetFullHashCandidates(int quickVersion, int fullVersion, int limit) =>
            ReadHashCandidates(quickVersion, fullVersion, Math.Clamp(limit, 1, 10_000), fullStage: true);

        private IReadOnlyList<LibraryHashCandidate> ReadHashCandidates(
            int quickVersion,
            int fullVersion,
            int limit,
            bool fullStage)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = fullStage
                ? """
                  SELECT f.id,f.full_path,f.path_key,f.size_bytes,f.last_write_utc_ticks,f.volume_id,f.file_identity
                  FROM indexed_files f JOIN file_hash_facts h ON h.file_id=f.id
                  WHERE f.availability_state=$present AND h.source_size_bytes=f.size_bytes
                    AND h.source_last_write_utc_ticks=f.last_write_utc_ticks
                    AND h.source_volume_id=f.volume_id AND h.source_file_identity=f.file_identity
                    AND h.quick_version=$qv AND h.quick_hash IS NOT NULL
                    AND (h.full_version<>$fv OR h.full_hash IS NULL)
                    AND h.failure_count<3
                    AND EXISTS (
                        SELECT 1 FROM indexed_files f2 JOIN file_hash_facts h2 ON h2.file_id=f2.id
                        WHERE f2.id<>f.id AND f2.availability_state=$present
                          AND f2.size_bytes=f.size_bytes AND h2.source_size_bytes=f2.size_bytes
                          AND h2.source_last_write_utc_ticks=f2.last_write_utc_ticks
                          AND h2.quick_version=$qv AND h2.quick_hash=h.quick_hash)
                  ORDER BY f.id LIMIT $limit;
                  """
                : """
                  SELECT f.id,f.full_path,f.path_key,f.size_bytes,f.last_write_utc_ticks,f.volume_id,f.file_identity
                  FROM indexed_files f LEFT JOIN file_hash_facts h ON h.file_id=f.id
                  WHERE f.availability_state=$present
                    AND EXISTS (SELECT 1 FROM indexed_files f2 WHERE f2.id<>f.id AND f2.availability_state=$present AND f2.size_bytes=f.size_bytes)
                    AND (h.file_id IS NULL OR h.source_size_bytes<>f.size_bytes
                         OR h.source_last_write_utc_ticks<>f.last_write_utc_ticks
                         OR h.source_volume_id<>f.volume_id OR h.source_file_identity<>f.file_identity
                         OR h.quick_version<>$qv OR h.quick_hash IS NULL)
                    AND COALESCE(h.failure_count,0)<3
                  ORDER BY f.id LIMIT $limit;
                  """;
            command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
            command.Parameters.AddWithValue("$qv", quickVersion);
            command.Parameters.AddWithValue("$fv", fullVersion);
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<LibraryHashCandidate>(limit);
            while (reader.Read())
            {
                result.Add(new LibraryHashCandidate(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    FromUtcTicks(reader.GetInt64(4)), reader.GetString(5), reader.GetString(6)));
            }
            return result;
        }

        public LibraryFileHashFact? GetFileHashFact(long fileId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT file_id,source_size_bytes,source_last_write_utc_ticks,source_volume_id,
                       source_file_identity,quick_algorithm,quick_version,quick_hash,quick_completed_utc_ticks,
                       full_algorithm,full_version,full_hash,full_completed_utc_ticks,failure_count,error_message
                FROM file_hash_facts WHERE file_id=$id;
                """;
            command.Parameters.AddWithValue("$id", fileId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new LibraryFileHashFact(
                reader.GetInt64(0), reader.GetInt64(1), FromUtcTicks(reader.GetInt64(2)), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : (byte[])reader[7],
                reader.IsDBNull(8) ? null : FromUtcTicks(reader.GetInt64(8)), reader.GetString(9), reader.GetInt32(10),
                reader.IsDBNull(11) ? null : (byte[])reader[11], reader.IsDBNull(12) ? null : FromUtcTicks(reader.GetInt64(12)),
                reader.GetInt32(13), reader.GetString(14));
        }

        public void SaveQuickHash(LibraryHashCandidate candidate, string algorithm, int version, byte[] hash) =>
            SaveHashBatch(new[] { new LibraryHashWrite(candidate, hash) }, LibraryHashKind.QuickFingerprint, algorithm, version);

        public void SaveFullHash(LibraryHashCandidate candidate, string algorithm, int version, byte[] hash) =>
            SaveHashBatch(new[] { new LibraryHashWrite(candidate, hash) }, LibraryHashKind.FullSha256, algorithm, version);

        public void SaveHashFailure(LibraryHashCandidate candidate, string message) =>
            SaveHashBatch(new[] { new LibraryHashWrite(candidate, null, message) }, LibraryHashKind.QuickFingerprint, "", 0);

        public void SaveHashBatch(IReadOnlyCollection<LibraryHashWrite> writes, LibraryHashKind kind, string algorithm, int version)
        {
            ArgumentNullException.ThrowIfNull(writes);
            if (writes.Count == 0) return;
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                foreach (LibraryHashWrite write in writes)
                {
                    ArgumentNullException.ThrowIfNull(write.Candidate);
                    if (write.Hash == null)
                    {
                        SaveHashFailureCore(connection, transaction, write.Candidate, write.ErrorMessage);
                        continue;
                    }
                    IReadOnlyList<LibraryHashCandidate> targets = ReadIdentityTargets(connection, transaction, write.Candidate);
                    foreach (LibraryHashCandidate target in targets)
                        SaveHashCore(connection, transaction, target, algorithm, version, write.Hash, kind == LibraryHashKind.FullSha256);
                }
                return null;
            });
        }

        private static void SaveHashCore(SqliteConnection connection, SqliteTransaction transaction, LibraryHashCandidate target, string algorithm, int version, byte[] hash, bool full)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = full
                ? """
                  INSERT INTO file_hash_facts(file_id,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,full_algorithm,full_version,full_hash,full_completed_utc_ticks,updated_utc_ticks)
                  VALUES($id,$size,$modified,$volume,$identity,$algorithm,$version,$hash,$now,$now)
                  ON CONFLICT(file_id) DO UPDATE SET source_size_bytes=excluded.source_size_bytes,source_last_write_utc_ticks=excluded.source_last_write_utc_ticks,
                      source_volume_id=excluded.source_volume_id,source_file_identity=excluded.source_file_identity,full_algorithm=excluded.full_algorithm,
                      full_version=excluded.full_version,full_hash=excluded.full_hash,full_completed_utc_ticks=excluded.full_completed_utc_ticks,
                      failure_count=0,error_message='',updated_utc_ticks=excluded.updated_utc_ticks;
                  """
                : """
                  INSERT INTO file_hash_facts(file_id,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,quick_algorithm,quick_version,quick_hash,quick_completed_utc_ticks,updated_utc_ticks)
                  VALUES($id,$size,$modified,$volume,$identity,$algorithm,$version,$hash,$now,$now)
                  ON CONFLICT(file_id) DO UPDATE SET source_size_bytes=excluded.source_size_bytes,source_last_write_utc_ticks=excluded.source_last_write_utc_ticks,
                      source_volume_id=excluded.source_volume_id,source_file_identity=excluded.source_file_identity,quick_algorithm=excluded.quick_algorithm,
                      quick_version=excluded.quick_version,quick_hash=excluded.quick_hash,quick_completed_utc_ticks=excluded.quick_completed_utc_ticks,
                      full_algorithm='',full_version=0,full_hash=NULL,full_completed_utc_ticks=NULL,failure_count=0,error_message='',updated_utc_ticks=excluded.updated_utc_ticks;
                  """;
            AddHashParameters(command, target, algorithm, version, hash);
            command.ExecuteNonQuery();
        }

        private static void SaveHashFailureCore(SqliteConnection connection, SqliteTransaction transaction, LibraryHashCandidate candidate, string message)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                    INSERT INTO file_hash_facts(file_id,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,failure_count,error_message,updated_utc_ticks)
                    VALUES($id,$size,$modified,$volume,$identity,1,$error,$now)
                    ON CONFLICT(file_id) DO UPDATE SET source_size_bytes=excluded.source_size_bytes,source_last_write_utc_ticks=excluded.source_last_write_utc_ticks,
                        source_volume_id=excluded.source_volume_id,source_file_identity=excluded.source_file_identity,
                        quick_algorithm=CASE WHEN file_hash_facts.source_size_bytes=excluded.source_size_bytes AND file_hash_facts.source_last_write_utc_ticks=excluded.source_last_write_utc_ticks THEN file_hash_facts.quick_algorithm ELSE '' END,
                        quick_version=CASE WHEN file_hash_facts.source_size_bytes=excluded.source_size_bytes AND file_hash_facts.source_last_write_utc_ticks=excluded.source_last_write_utc_ticks THEN file_hash_facts.quick_version ELSE 0 END,
                        quick_hash=CASE WHEN file_hash_facts.source_size_bytes=excluded.source_size_bytes AND file_hash_facts.source_last_write_utc_ticks=excluded.source_last_write_utc_ticks THEN file_hash_facts.quick_hash ELSE NULL END,
                        full_algorithm=CASE WHEN file_hash_facts.source_size_bytes=excluded.source_size_bytes AND file_hash_facts.source_last_write_utc_ticks=excluded.source_last_write_utc_ticks THEN file_hash_facts.full_algorithm ELSE '' END,
                        full_version=CASE WHEN file_hash_facts.source_size_bytes=excluded.source_size_bytes AND file_hash_facts.source_last_write_utc_ticks=excluded.source_last_write_utc_ticks THEN file_hash_facts.full_version ELSE 0 END,
                        full_hash=CASE WHEN file_hash_facts.source_size_bytes=excluded.source_size_bytes AND file_hash_facts.source_last_write_utc_ticks=excluded.source_last_write_utc_ticks THEN file_hash_facts.full_hash ELSE NULL END,
                        failure_count=file_hash_facts.failure_count+1,error_message=excluded.error_message,updated_utc_ticks=excluded.updated_utc_ticks;
                """;
            command.Parameters.AddWithValue("$id", candidate.FileId);
            command.Parameters.AddWithValue("$size", candidate.SizeBytes);
            command.Parameters.AddWithValue("$modified", candidate.LastWriteUtc.Ticks);
            command.Parameters.AddWithValue("$volume", candidate.VolumeId ?? "");
            command.Parameters.AddWithValue("$identity", candidate.FileIdentity ?? "");
            command.Parameters.AddWithValue("$error", message ?? "");
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            command.ExecuteNonQuery();
        }

        public long RebuildExactDuplicateGroups(DuplicateAnalysisHandle run, string fullAlgorithm, int fullVersion)
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                long now = DateTime.UtcNow.Ticks;
                using SqliteCommand groups = connection.CreateCommand();
                groups.Transaction = transaction;
                groups.CommandText =
                    """
                    INSERT INTO exact_duplicate_groups(size_bytes,full_algorithm,full_version,full_hash,member_count,physical_copy_count,reclaimable_bytes,analysis_run_id,updated_utc_ticks)
                    SELECT f.size_bytes,$algorithm,$version,h.full_hash,COUNT(*),
                           COUNT(DISTINCT CASE WHEN f.volume_id<>'' AND f.file_identity<>'' THEN f.volume_id || ':' || f.file_identity ELSE f.path_key END),
                           f.size_bytes * MAX(0, COUNT(DISTINCT CASE WHEN f.volume_id<>'' AND f.file_identity<>'' THEN f.volume_id || ':' || f.file_identity ELSE f.path_key END)-1),
                           $run,$now
                    FROM indexed_files f JOIN file_hash_facts h ON h.file_id=f.id
                    WHERE f.availability_state=$present AND h.full_algorithm=$algorithm AND h.full_version=$version AND h.full_hash IS NOT NULL
                      AND h.source_size_bytes=f.size_bytes AND h.source_last_write_utc_ticks=f.last_write_utc_ticks
                      AND h.source_volume_id=f.volume_id AND h.source_file_identity=f.file_identity
                    GROUP BY f.size_bytes,h.full_hash HAVING COUNT(*)>=2
                    ON CONFLICT(size_bytes,full_algorithm,full_version,full_hash) DO UPDATE SET
                        member_count=excluded.member_count,physical_copy_count=excluded.physical_copy_count,
                        reclaimable_bytes=excluded.reclaimable_bytes,analysis_run_id=excluded.analysis_run_id,updated_utc_ticks=excluded.updated_utc_ticks;
                    """;
                groups.Parameters.AddWithValue("$algorithm", fullAlgorithm);
                groups.Parameters.AddWithValue("$version", fullVersion);
                groups.Parameters.AddWithValue("$run", run.RunId);
                groups.Parameters.AddWithValue("$now", now);
                groups.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                groups.ExecuteNonQuery();

                using SqliteCommand clearMembers = connection.CreateCommand();
                clearMembers.Transaction = transaction;
                clearMembers.CommandText = "DELETE FROM exact_duplicate_members WHERE group_id IN (SELECT id FROM exact_duplicate_groups WHERE analysis_run_id=$run);";
                clearMembers.Parameters.AddWithValue("$run", run.RunId);
                clearMembers.ExecuteNonQuery();

                using SqliteCommand members = connection.CreateCommand();
                members.Transaction = transaction;
                members.CommandText =
                    """
                    INSERT INTO exact_duplicate_members(group_id,file_id,physical_identity_key,is_hard_link_alias)
                    SELECT g.id,f.id,
                           CASE WHEN f.volume_id<>'' AND f.file_identity<>'' THEN f.volume_id || ':' || f.file_identity ELSE f.path_key END,
                           CASE WHEN f.volume_id<>'' AND f.file_identity<>'' AND EXISTS(
                               SELECT 1 FROM indexed_files prior WHERE prior.id<f.id AND prior.volume_id=f.volume_id AND prior.file_identity=f.file_identity
                           ) THEN 1 ELSE 0 END
                    FROM exact_duplicate_groups g
                    JOIN file_hash_facts h ON h.full_hash=g.full_hash AND h.full_algorithm=g.full_algorithm AND h.full_version=g.full_version
                    JOIN indexed_files f ON f.id=h.file_id AND f.size_bytes=g.size_bytes
                    WHERE g.analysis_run_id=$run AND f.availability_state=$present
                      AND h.source_size_bytes=f.size_bytes AND h.source_last_write_utc_ticks=f.last_write_utc_ticks
                      AND h.source_volume_id=f.volume_id AND h.source_file_identity=f.file_identity;
                    """;
                members.Parameters.AddWithValue("$run", run.RunId);
                members.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                members.ExecuteNonQuery();

                using SqliteCommand count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM exact_duplicate_groups WHERE analysis_run_id=$run;";
                count.Parameters.AddWithValue("$run", run.RunId);
                return Convert.ToInt64(count.ExecuteScalar());
            });
        }

        public IReadOnlyList<long> GetDuplicateGroupIds(long analysisRunId, long afterGroupId, int limit)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM exact_duplicate_groups WHERE analysis_run_id=$run AND id>$after ORDER BY id LIMIT $limit;";
            command.Parameters.AddWithValue("$run", analysisRunId);
            command.Parameters.AddWithValue("$after", afterGroupId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<long>();
            while (reader.Read()) result.Add(reader.GetInt64(0));
            return result;
        }

        private static IReadOnlyList<LibraryHashCandidate> ReadIdentityTargets(
            SqliteConnection connection,
            SqliteTransaction transaction,
            LibraryHashCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.VolumeId) || string.IsNullOrWhiteSpace(candidate.FileIdentity))
                return new[] { candidate };
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT id,full_path,path_key,size_bytes,last_write_utc_ticks,volume_id,file_identity FROM indexed_files WHERE availability_state=$present AND volume_id=$volume AND file_identity=$identity;";
            command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
            command.Parameters.AddWithValue("$volume", candidate.VolumeId);
            command.Parameters.AddWithValue("$identity", candidate.FileIdentity);
            using SqliteDataReader reader = command.ExecuteReader();
            var targets = new List<LibraryHashCandidate>();
            while (reader.Read())
                targets.Add(new LibraryHashCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), FromUtcTicks(reader.GetInt64(4)), reader.GetString(5), reader.GetString(6)));
            return targets.Count == 0 ? new[] { candidate } : targets;
        }

        private static void AddHashParameters(SqliteCommand command, LibraryHashCandidate target, string algorithm, int version, byte[] hash)
        {
            command.Parameters.AddWithValue("$id", target.FileId);
            command.Parameters.AddWithValue("$size", target.SizeBytes);
            command.Parameters.AddWithValue("$modified", target.LastWriteUtc.Ticks);
            command.Parameters.AddWithValue("$volume", target.VolumeId ?? "");
            command.Parameters.AddWithValue("$identity", target.FileIdentity ?? "");
            command.Parameters.AddWithValue("$algorithm", algorithm);
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        }
    }
}
