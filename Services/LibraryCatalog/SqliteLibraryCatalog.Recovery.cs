using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog : ILibraryRecoveryCatalog, ILibraryPhase2Catalog
    {
        private sealed record ExactDecisionState(bool Exists, long SizeBytes, string Algorithm, int Version,
            string HashHex, string ManualKeeperPathKey, bool Reviewed, bool Ignored);
        private sealed record VisualDecisionState(bool Exists, string GroupKey, string ManualKeeperPathKey,
            bool Reviewed, bool Ignored, bool NotMatch);
        private sealed record FamilyDecisionState(bool Exists, string FamilyKey, string ManualKeeperPathKey,
            bool Reviewed, bool Ignored);
        private sealed record ProtectionDecisionState(bool Exists, string PathKey, string ProtectedPath,
            string Reason);

        public IReadOnlyList<LibraryPresenceObservation> GetPresenceObservations(long fileId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT location_id,file_id,state,consecutive_observations,related_file_id,source,details,last_observed_utc_ticks " +
                "FROM library_presence_observations WHERE file_id=$file ORDER BY location_id;";
            command.Parameters.AddWithValue("$file", fileId);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<LibraryPresenceObservation>();
            while (reader.Read())
            {
                result.Add(new LibraryPresenceObservation(
                    reader.GetInt64(0), reader.GetInt64(1), (LibraryPresenceObservationState)reader.GetInt32(2),
                    reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5),
                    reader.GetString(6), FromUtcTicks(reader.GetInt64(7))));
            }
            return result;
        }

        public void RecordPresenceObservation(long locationId, long fileId, LibraryPresenceObservationState state,
            string source, string details = "", long? relatedFileId = null)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                UpsertPresenceObservationCore(connection, transaction, locationId, fileId, state, source, details, relatedFileId);
                RefreshVisualLifecycleCore(connection, transaction, fileId, preserveRetired: true);
                return null;
            });
        }

        public void MarkFileRemovedByCleanup(long fileId, string expectedPath, string reason)
        {
            ThrowIfDisposed();
            (_, string expectedKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(expectedPath);
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand validate = connection.CreateCommand();
                validate.Transaction = transaction;
                validate.CommandText = "SELECT path_key FROM indexed_files WHERE id=$file;";
                validate.Parameters.AddWithValue("$file", fileId);
                string? actualKey = Convert.ToString(validate.ExecuteScalar());
                if (!string.Equals(actualKey, expectedKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("The cleanup result no longer matches the indexed file path.");

                using SqliteCommand memberships = connection.CreateCommand();
                memberships.Transaction = transaction;
                memberships.CommandText =
                    "UPDATE file_location_memberships SET availability_state=$missing WHERE file_id=$file; " +
                    "UPDATE indexed_files SET availability_state=$missing,updated_utc_ticks=$now WHERE id=$file;";
                memberships.Parameters.AddWithValue("$missing", (int)IndexedFileAvailability.Missing);
                memberships.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                memberships.Parameters.AddWithValue("$file", fileId);
                memberships.ExecuteNonQuery();

                foreach (long locationId in ReadMembershipLocationIds(connection, transaction, fileId))
                    UpsertPresenceObservationCore(connection, transaction, locationId, fileId,
                        LibraryPresenceObservationState.ConfirmedMissing, "cleanup", reason);

                using SqliteCommand retire = connection.CreateCommand();
                retire.Transaction = transaction;
                retire.CommandText =
                    "UPDATE visual_similarity_groups SET lifecycle_state=$retired,lifecycle_reason=$reason," +
                    "lifecycle_updated_utc_ticks=$now WHERE left_file_id=$file OR right_file_id=$file;";
                retire.Parameters.AddWithValue("$retired", (int)LibraryMatchEligibilityState.Retired);
                retire.Parameters.AddWithValue("$reason", reason ?? "Removed by Library Analyzer cleanup.");
                retire.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                retire.Parameters.AddWithValue("$file", fileId);
                retire.ExecuteNonQuery();
                return null;
            });
        }

        public void MarkFileRestoredFromQuarantine(long fileId, string expectedPath)
        {
            ThrowIfDisposed();
            (_, string expectedKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(expectedPath);
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE indexed_files SET availability_state=$present,updated_utc_ticks=$now " +
                    "WHERE id=$file AND path_key=$path; " +
                    "UPDATE file_location_memberships SET availability_state=$present WHERE file_id=$file;";
                command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$file", fileId);
                command.Parameters.AddWithValue("$path", expectedKey);
                command.ExecuteNonQuery();
                foreach (long locationId in ReadMembershipLocationIds(connection, transaction, fileId))
                    UpsertPresenceObservationCore(connection, transaction, locationId, fileId,
                        LibraryPresenceObservationState.StaleEvidence, "quarantine-restore",
                        "The file was restored and its derived evidence must be refreshed.");
                using SqliteCommand lifecycle = connection.CreateCommand();
                lifecycle.Transaction = transaction;
                lifecycle.CommandText =
                    "UPDATE visual_similarity_groups SET lifecycle_state=$stale,lifecycle_reason=$reason," +
                    "lifecycle_updated_utc_ticks=$now WHERE left_file_id=$file OR right_file_id=$file;";
                lifecycle.Parameters.AddWithValue("$stale", (int)LibraryMatchEligibilityState.StaleEvidence);
                lifecycle.Parameters.AddWithValue("$reason", "Restored from quarantine; targeted re-analysis is required.");
                lifecycle.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                lifecycle.Parameters.AddWithValue("$file", fileId);
                lifecycle.ExecuteNonQuery();
                return null;
            });
        }

        public void RestoreLocationAfterVerifiedNoChanges(long locationId)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                long now = DateTime.UtcNow.Ticks;
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE file_location_memberships SET availability_state=$present WHERE location_id=$location; " +
                    "INSERT INTO library_presence_observations(location_id,file_id,state,consecutive_observations,source,details,last_observed_utc_ticks) " +
                    "SELECT location_id,file_id,$observation,0,'usn-no-change','Location availability restored with a verified no-change journal checkpoint.',$now " +
                    "FROM file_location_memberships WHERE location_id=$location " +
                    "ON CONFLICT(location_id,file_id) DO UPDATE SET state=excluded.state,consecutive_observations=0," +
                    "related_file_id=NULL,source=excluded.source,details=excluded.details,last_observed_utc_ticks=excluded.last_observed_utc_ticks; " +
                    "UPDATE indexed_files SET availability_state=CASE WHEN EXISTS(" +
                    "SELECT 1 FROM file_location_memberships m WHERE m.file_id=indexed_files.id AND m.availability_state=$present" +
                    ") THEN $present ELSE availability_state END,updated_utc_ticks=$now " +
                    "WHERE id IN(SELECT file_id FROM file_location_memberships WHERE location_id=$location);";
                command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                command.Parameters.AddWithValue("$observation", (int)LibraryPresenceObservationState.Present);
                command.Parameters.AddWithValue("$location", locationId);
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
                RefreshVisualLifecycleCore(connection, transaction, null, preserveRetired: true);
                return null;
            });
        }

        public void SetVisualMatchLifecycle(long groupId, LibraryMatchEligibilityState state, string reason)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE visual_similarity_groups SET lifecycle_state=$state,lifecycle_reason=$reason," +
                                      "lifecycle_updated_utc_ticks=$now WHERE id=$group;";
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$reason", reason ?? "");
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$group", groupId);
                command.ExecuteNonQuery();
                return null;
            });
        }

        public long EnqueueReanalysis(long fileId, LibraryReanalysisWork work, string batchId = "", int maximumAttempts = 3)
        {
            if (work is <= LibraryReanalysisWork.None or > LibraryReanalysisWork.All)
                throw new ArgumentOutOfRangeException(nameof(work));
            maximumAttempts = Math.Clamp(maximumAttempts, 1, 10);
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand existing = connection.CreateCommand();
                existing.Transaction = transaction;
                existing.CommandText = "SELECT id,work_mask FROM library_reanalysis_queue WHERE file_id=$file AND status IN(0,1) LIMIT 1;";
                existing.Parameters.AddWithValue("$file", fileId);
                long? id = null;
                int currentMask = 0;
                using (SqliteDataReader reader = existing.ExecuteReader())
                {
                    if (reader.Read()) { id = reader.GetInt64(0); currentMask = reader.GetInt32(1); }
                }
                long now = DateTime.UtcNow.Ticks;
                if (id.HasValue)
                {
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE library_reanalysis_queue SET work_mask=$work,maximum_attempts=MAX(maximum_attempts,$max)," +
                                         "batch_id=CASE WHEN batch_id='' THEN $batch ELSE batch_id END,updated_utc_ticks=$now WHERE id=$id;";
                    update.Parameters.AddWithValue("$work", currentMask | (int)work);
                    update.Parameters.AddWithValue("$max", maximumAttempts);
                    update.Parameters.AddWithValue("$batch", batchId ?? "");
                    update.Parameters.AddWithValue("$now", now);
                    update.Parameters.AddWithValue("$id", id.Value);
                    update.ExecuteNonQuery();
                    return id.Value;
                }
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO library_reanalysis_queue(file_id,work_mask,status,maximum_attempts,batch_id,created_utc_ticks,updated_utc_ticks) " +
                                     "SELECT $file,$work,0,$max,$batch,$now,$now WHERE EXISTS(SELECT 1 FROM indexed_files WHERE id=$file) RETURNING id;";
                insert.Parameters.AddWithValue("$file", fileId);
                insert.Parameters.AddWithValue("$work", (int)work);
                insert.Parameters.AddWithValue("$max", maximumAttempts);
                insert.Parameters.AddWithValue("$batch", batchId ?? "");
                insert.Parameters.AddWithValue("$now", now);
                object? value = insert.ExecuteScalar();
                return value == null ? throw new KeyNotFoundException($"Library file {fileId} does not exist.") : Convert.ToInt64(value);
            });
        }

        public IReadOnlyList<LibraryReanalysisItem> ClaimReanalysisBatch(int limit, DateTime utcNow)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 256);
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "SELECT q.id,q.file_id,f.full_path,q.work_mask,q.status,q.attempt_count,q.maximum_attempts,q.batch_id," +
                                     "q.error_text,q.next_attempt_utc_ticks,q.created_utc_ticks,q.updated_utc_ticks " +
                                     "FROM library_reanalysis_queue q JOIN indexed_files f ON f.id=q.file_id " +
                                     "WHERE q.status=0 AND (q.next_attempt_utc_ticks IS NULL OR q.next_attempt_utc_ticks<=$now) " +
                                     "ORDER BY q.id LIMIT $limit;";
                select.Parameters.AddWithValue("$now", utcNow.Ticks);
                select.Parameters.AddWithValue("$limit", limit);
                var items = new List<LibraryReanalysisItem>();
                using (SqliteDataReader reader = select.ExecuteReader())
                {
                    while (reader.Read()) items.Add(ReadReanalysisItem(reader));
                }
                if (items.Count > 0)
                {
                    using SqliteCommand claim = connection.CreateCommand();
                    claim.Transaction = transaction;
                    claim.CommandText = "UPDATE library_reanalysis_queue SET status=1,attempt_count=attempt_count+1,updated_utc_ticks=$now WHERE id=$id AND status=0;";
                    claim.Parameters.Add("$id", SqliteType.Integer);
                    claim.Parameters.AddWithValue("$now", utcNow.Ticks);
                    foreach (LibraryReanalysisItem item in items)
                    {
                        claim.Parameters["$id"].Value = item.Id;
                        claim.ExecuteNonQuery();
                    }
                    items = items.Select(item => item with { Status = LibraryReanalysisStatus.Running, AttemptCount = item.AttemptCount + 1, UpdatedUtc = utcNow }).ToList();
                }
                return (IReadOnlyList<LibraryReanalysisItem>)items;
            });
        }

        public void CompleteReanalysisItem(long itemId, LibraryReanalysisWork completedWork, string errorText = "", DateTime? retryUtc = null)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT work_mask,attempt_count,maximum_attempts FROM library_reanalysis_queue WHERE id=$id AND status=1;";
                read.Parameters.AddWithValue("$id", itemId);
                int workMask, attempts, maximum;
                using (SqliteDataReader reader = read.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    workMask = reader.GetInt32(0); attempts = reader.GetInt32(1); maximum = reader.GetInt32(2);
                }
                bool failed = !string.IsNullOrWhiteSpace(errorText);
                int remaining = workMask & ~(int)completedWork;
                int status = failed ? (attempts >= maximum ? 3 : 0) : (remaining == 0 ? 2 : 0);
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE library_reanalysis_queue SET work_mask=$work,status=$status,error_text=$error," +
                                     "next_attempt_utc_ticks=$retry,updated_utc_ticks=$now WHERE id=$id;";
                update.Parameters.AddWithValue("$work", remaining == 0 ? workMask : remaining);
                update.Parameters.AddWithValue("$status", status);
                update.Parameters.AddWithValue("$error", errorText ?? "");
                update.Parameters.AddWithValue("$retry", status == 0 && failed ? (object?)(retryUtc ?? DateTime.UtcNow.AddMinutes(2)).Ticks : DBNull.Value);
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                update.Parameters.AddWithValue("$id", itemId);
                update.ExecuteNonQuery();
                return null;
            });
        }

        public int RecoverInterruptedReanalysis()
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE library_reanalysis_queue SET status=CASE WHEN attempt_count>=maximum_attempts THEN 3 ELSE 0 END," +
                                      "error_text=CASE WHEN error_text='' THEN 'Interrupted before targeted re-analysis completed.' ELSE error_text END," +
                                      "next_attempt_utc_ticks=CASE WHEN attempt_count>=maximum_attempts THEN NULL ELSE $now END,updated_utc_ticks=$now WHERE status=1;";
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                return command.ExecuteNonQuery();
            });
        }

        public void PrepareMetadataReanalysis(IReadOnlyCollection<long> fileIds) => PrepareFacts(fileIds, "metadata");
        public void PrepareExactReanalysis(IReadOnlyCollection<long> fileIds) => PrepareFacts(fileIds, "exact");
        public void PrepareVisualReanalysis(IReadOnlyCollection<long> fileIds) => PrepareFacts(fileIds, "visual");

        private void PrepareFacts(IReadOnlyCollection<long> fileIds, string kind)
        {
            ArgumentNullException.ThrowIfNull(fileIds);
            if (fileIds.Count == 0) return;
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                foreach (long fileId in fileIds.Distinct())
                {
                    using SqliteCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = kind switch
                    {
                        "metadata" => "UPDATE media_metadata SET probe_status=0,attempt_count=0,next_retry_utc_ticks=NULL,error_message='Targeted metadata re-analysis requested.',updated_utc_ticks=$now WHERE file_id=$file;",
                        "exact" => "DELETE FROM file_hash_facts WHERE file_id=$file;",
                        _ => "DELETE FROM visual_hash_bands WHERE file_id=$file; DELETE FROM visual_fingerprints WHERE file_id=$file;"
                    };
                    command.Parameters.AddWithValue("$file", fileId);
                    command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    command.ExecuteNonQuery();
                }
                return null;
            });
        }

        public IReadOnlyList<LibraryDecisionEvent> GetDecisionHistory(int limit = 200)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id,target_kind,target_key,event_kind,before_state,after_state,batch_id,source," +
                                  "reversal_of_event_id,reversed_by_event_id,occurred_utc_ticks FROM library_decision_events " +
                                  "ORDER BY occurred_utc_ticks DESC,id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<LibraryDecisionEvent>();
            while (reader.Read()) result.Add(ReadDecisionEvent(reader));
            return result;
        }

        public LibraryDecisionUndoResult UndoDecision(long eventId)
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                LibraryDecisionEvent? item = ReadDecisionEvent(connection, transaction, eventId);
                if (item == null) return new LibraryDecisionUndoResult(false, null, "The decision event does not exist.");
                if (!item.CanUndo) return new LibraryDecisionUndoResult(false, null, "This decision was already reversed or is itself a reversal.");
                if (string.Equals(item.Source, "restored-history", StringComparison.Ordinal))
                    return new LibraryDecisionUndoResult(false, null, "Restored historical events are audit-only and cannot be undone in this catalog.");
                if (item.EventKind == LibraryDecisionEventKind.CleanupRestored)
                    return new LibraryDecisionUndoResult(false, null, "A quarantine restoration cannot be automatically reversed. The audit history was preserved.");
                string current = ReadCurrentDecisionState(connection, transaction, item);
                if (!JsonStatesEqual(current, item.AfterState))
                    return new LibraryDecisionUndoResult(false, null, "Undo was blocked because a newer conflicting decision superseded this state.");
                ApplyDecisionState(connection, transaction, item.TargetKind, item.BeforeState);
                long reversalId = InsertDecisionEventCore(connection, transaction, item.TargetKind, item.TargetKey,
                    item.EventKind, current, item.BeforeState, item.BatchId, "undo", item.Id);
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE library_decision_events SET reversed_by_event_id=$reversal WHERE id=$id;";
                update.Parameters.AddWithValue("$reversal", reversalId);
                update.Parameters.AddWithValue("$id", item.Id);
                update.ExecuteNonQuery();
                return new LibraryDecisionUndoResult(true, reversalId, "The decision was safely reversed.");
            });
        }

        public long AppendCleanupRestoreDecision(LibraryQuarantineRestoreItem item, string batchId = "")
        {
            ArgumentNullException.ThrowIfNull(item);
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                string key = QuarantineTargetKey(item.IsVisual, item.AuditId);
                string before = JsonSerializer.Serialize(new { Restored = false, item.SourcePath, item.DestinationPath });
                string after = JsonSerializer.Serialize(new { Restored = true, item.SourcePath, item.DestinationPath });
                return InsertDecisionEventCore(connection, transaction, LibraryDecisionTargetKind.Cleanup, key,
                    LibraryDecisionEventKind.CleanupRestored, before, after, batchId, "quarantine-restore");
            });
        }

        public IReadOnlyList<LibraryHealthIssue> QueryHealthIssues(int limit = 500)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 5000);
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            var result = new List<LibraryHealthIssue>();
            ReadPresenceIssues(connection, result, limit);
            ReadLocationIssues(connection, result, limit);
            ReadMetadataIssues(connection, result, limit);
            ReadDerivedEvidenceIssues(connection, result, limit);
            ReadRunIssues(connection, result, limit);
            ReadCleanupAndQueueIssues(connection, result, limit);
            ReadIntegrityIssues(connection, result, limit);
            return result.Take(limit).ToArray();
        }

        public IReadOnlyList<LibraryQuarantineRestoreItem> GetQuarantineRestoreCandidates(int limit = 200)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            var result = new List<LibraryQuarantineRestoreItem>();
            ReadQuarantineCandidates(connection, result, visual: false, limit);
            ReadQuarantineCandidates(connection, result, visual: true, limit);
            return result.OrderByDescending(item => item.AuditId).Take(limit).ToArray();
        }

        public LibraryMaintenanceResult RunSafeMaintenance()
        {
            ThrowIfDisposed();
            int recovered = RecoverInterruptedReanalysis();
            (int groups, int runs) = WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand pruneGroups = connection.CreateCommand();
                pruneGroups.Transaction = transaction;
                pruneGroups.CommandText = "DELETE FROM exact_duplicate_groups WHERE analysis_run_id<>(SELECT COALESCE(MAX(id),0) FROM duplicate_analysis_runs WHERE status=1);";
                int prunedGroups = pruneGroups.ExecuteNonQuery();
                using SqliteCommand pruneRuns = connection.CreateCommand();
                pruneRuns.Transaction = transaction;
                pruneRuns.CommandText =
                    "DELETE FROM duplicate_analysis_runs WHERE id NOT IN(SELECT id FROM duplicate_analysis_runs ORDER BY id DESC LIMIT 20) " +
                    "AND NOT EXISTS(SELECT 1 FROM exact_duplicate_groups g WHERE g.analysis_run_id=duplicate_analysis_runs.id); " +
                    "DELETE FROM visual_analysis_runs WHERE id NOT IN(SELECT id FROM visual_analysis_runs ORDER BY id DESC LIMIT 20) " +
                    "AND NOT EXISTS(SELECT 1 FROM visual_similarity_groups g WHERE g.analysis_run_id=visual_analysis_runs.id);";
                int prunedRuns = pruneRuns.ExecuteNonQuery();
                return (prunedGroups, prunedRuns);
            });
            using (SqliteConnection connection = _database.OpenConnection())
            using (SqliteCommand optimize = connection.CreateCommand())
            {
                optimize.CommandText = "PRAGMA optimize;";
                optimize.ExecuteNonQuery();
            }
            LibraryCatalogCheckpointResult checkpoint = _database.Checkpoint(LibraryCatalogCheckpointMode.Passive);
            LibraryCatalogIntegrityResult integrity = _database.CheckIntegrity(fullCheck: false);
            return new LibraryMaintenanceResult(recovered, groups, runs, checkpoint, integrity);
        }

        private static void UpsertPresenceObservationCore(SqliteConnection connection, SqliteTransaction transaction,
            long locationId, long fileId, LibraryPresenceObservationState state, string source, string details,
            long? relatedFileId = null)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO library_presence_observations(location_id,file_id,state,consecutive_observations,related_file_id,source,details,last_observed_utc_ticks) " +
                "VALUES($location,$file,$state,CASE WHEN $state=0 THEN 0 ELSE 1 END,$related,$source,$details,$now) " +
                "ON CONFLICT(location_id,file_id) DO UPDATE SET state=excluded.state," +
                "consecutive_observations=CASE WHEN excluded.state=0 THEN 0 WHEN library_presence_observations.state=excluded.state " +
                "THEN library_presence_observations.consecutive_observations+1 ELSE 1 END,related_file_id=excluded.related_file_id," +
                "source=excluded.source,details=excluded.details,last_observed_utc_ticks=excluded.last_observed_utc_ticks;";
            command.Parameters.AddWithValue("$location", locationId);
            command.Parameters.AddWithValue("$file", fileId);
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$related", (object?)relatedFileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", source ?? "");
            command.Parameters.AddWithValue("$details", details ?? "");
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            command.ExecuteNonQuery();
        }

        private static IReadOnlyList<long> ReadMembershipLocationIds(SqliteConnection connection, SqliteTransaction transaction, long fileId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT location_id FROM file_location_memberships WHERE file_id=$file ORDER BY location_id;";
            command.Parameters.AddWithValue("$file", fileId);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<long>();
            while (reader.Read()) result.Add(reader.GetInt64(0));
            return result;
        }

        private static void RecordPossibleMoveCore(SqliteConnection connection, SqliteTransaction transaction,
            LibraryScanHandle scan, long newFileId, string volumeId, string fileIdentity)
        {
            if (string.IsNullOrWhiteSpace(volumeId) || string.IsNullOrWhiteSpace(fileIdentity))
                return;
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT DISTINCT f.id FROM indexed_files f JOIN file_location_memberships m ON m.file_id=f.id " +
                "WHERE f.id<>$new AND f.volume_id=$volume AND f.file_identity=$identity " +
                "AND m.location_id=$location AND m.last_seen_generation<$generation ORDER BY f.id LIMIT 2;";
            command.Parameters.AddWithValue("$new", newFileId);
            command.Parameters.AddWithValue("$volume", volumeId);
            command.Parameters.AddWithValue("$identity", fileIdentity);
            command.Parameters.AddWithValue("$location", scan.LocationId);
            command.Parameters.AddWithValue("$generation", scan.Generation);
            var candidates = new List<long>();
            using (SqliteDataReader reader = command.ExecuteReader()) while (reader.Read()) candidates.Add(reader.GetInt64(0));
            if (candidates.Count != 1) return;
            UpsertPresenceObservationCore(connection, transaction, scan.LocationId, candidates[0],
                LibraryPresenceObservationState.MovedOrRenamed, "stable-identity",
                "The same stable file identity was observed at a new path during this scan.", newFileId);
            RefreshVisualLifecycleCore(connection, transaction, candidates[0], preserveRetired: true);
        }

        private static void RefreshVisualLifecycleCore(SqliteConnection connection, SqliteTransaction transaction,
            long? fileId, bool preserveRetired)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE visual_similarity_groups AS g SET lifecycle_state=CASE " +
                "WHEN $preserve=1 AND g.lifecycle_state=$retired THEN $retired " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state=$moved) THEN $stale_state " +
                "WHEN EXISTS(SELECT 1 FROM indexed_files f WHERE f.id IN(g.left_file_id,g.right_file_id) AND f.availability_state=$missing) THEN $missing_state " +
                "WHEN EXISTS(SELECT 1 FROM indexed_files f WHERE f.id IN(g.left_file_id,g.right_file_id) AND f.availability_state=$unavailable) THEN $unavailable_state " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state=$suspected) THEN $suspected_state " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state IN($obs_unavailable,$access)) THEN $unavailable_state " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state=$stale) THEN $stale_state " +
                "WHEN EXISTS(SELECT 1 FROM indexed_files f LEFT JOIN visual_fingerprints v ON v.file_id=f.id " +
                "WHERE f.id IN(g.left_file_id,g.right_file_id) AND (v.file_id IS NULL OR v.status<>2 OR v.source_size_bytes<>f.size_bytes OR v.source_last_write_utc_ticks<>f.last_write_utc_ticks OR v.source_volume_id<>f.volume_id OR v.source_file_identity<>f.file_identity)) THEN $stale_state " +
                "ELSE $active END," +
                "lifecycle_reason=CASE " +
                "WHEN $preserve=1 AND g.lifecycle_state=$retired THEN g.lifecycle_reason " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state=$moved) THEN 'A member appears to have moved or been renamed; evidence must be rebuilt.' " +
                "WHEN EXISTS(SELECT 1 FROM indexed_files f WHERE f.id IN(g.left_file_id,g.right_file_id) AND f.availability_state=$missing) THEN 'One or more members are confirmed missing.' " +
                "WHEN EXISTS(SELECT 1 FROM indexed_files f WHERE f.id IN(g.left_file_id,g.right_file_id) AND f.availability_state=$unavailable) THEN 'One or more members are on an unavailable location.' " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state=$suspected) THEN 'One or more members are suspected missing pending authoritative scan.' " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state IN($obs_unavailable,$access)) THEN 'One or more members could not be verified because the location is unavailable.' " +
                "WHEN EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(g.left_file_id,g.right_file_id) AND o.state=$stale) THEN 'One or more members have stale evidence.' " +
                "WHEN EXISTS(SELECT 1 FROM indexed_files f LEFT JOIN visual_fingerprints v ON v.file_id=f.id WHERE f.id IN(g.left_file_id,g.right_file_id) AND (v.file_id IS NULL OR v.status<>2 OR v.source_size_bytes<>f.size_bytes OR v.source_last_write_utc_ticks<>f.last_write_utc_ticks OR v.source_volume_id<>f.volume_id OR v.source_file_identity<>f.file_identity)) THEN 'Visual fingerprint evidence is stale.' " +
                "ELSE '' END,lifecycle_updated_utc_ticks=$now " +
                "WHERE $file IS NULL OR g.left_file_id=$file OR g.right_file_id=$file;";
            command.Parameters.AddWithValue("$preserve", preserveRetired ? 1 : 0);
            command.Parameters.AddWithValue("$file", (object?)fileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$active", (int)LibraryMatchEligibilityState.Active);
            command.Parameters.AddWithValue("$suspected_state", (int)LibraryMatchEligibilityState.SuspectedMissing);
            command.Parameters.AddWithValue("$missing_state", (int)LibraryMatchEligibilityState.Missing);
            command.Parameters.AddWithValue("$unavailable_state", (int)LibraryMatchEligibilityState.Unavailable);
            command.Parameters.AddWithValue("$stale_state", (int)LibraryMatchEligibilityState.StaleEvidence);
            command.Parameters.AddWithValue("$retired", (int)LibraryMatchEligibilityState.Retired);
            command.Parameters.AddWithValue("$missing", (int)IndexedFileAvailability.Missing);
            command.Parameters.AddWithValue("$unavailable", (int)IndexedFileAvailability.Unavailable);
            command.Parameters.AddWithValue("$suspected", (int)LibraryPresenceObservationState.SuspectedMissing);
            command.Parameters.AddWithValue("$obs_unavailable", (int)LibraryPresenceObservationState.Unavailable);
            command.Parameters.AddWithValue("$access", (int)LibraryPresenceObservationState.AccessFailure);
            command.Parameters.AddWithValue("$moved", (int)LibraryPresenceObservationState.MovedOrRenamed);
            command.Parameters.AddWithValue("$stale", (int)LibraryPresenceObservationState.StaleEvidence);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            command.ExecuteNonQuery();
        }

        private static LibraryReanalysisItem ReadReanalysisItem(SqliteDataReader reader) => new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), (LibraryReanalysisWork)reader.GetInt32(3),
            (LibraryReanalysisStatus)reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7),
            reader.GetString(8), reader.IsDBNull(9) ? null : FromUtcTicks(reader.GetInt64(9)),
            FromUtcTicks(reader.GetInt64(10)), FromUtcTicks(reader.GetInt64(11)));

        private static LibraryDecisionEvent ReadDecisionEvent(SqliteDataReader reader) => new(
            reader.GetInt64(0), (LibraryDecisionTargetKind)reader.GetInt32(1), reader.GetString(2),
            (LibraryDecisionEventKind)reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt64(9),
            FromUtcTicks(reader.GetInt64(10)));

        private static LibraryDecisionEvent? ReadDecisionEvent(SqliteConnection connection, SqliteTransaction transaction, long eventId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id,target_kind,target_key,event_kind,before_state,after_state,batch_id,source," +
                                  "reversal_of_event_id,reversed_by_event_id,occurred_utc_ticks FROM library_decision_events WHERE id=$id;";
            command.Parameters.AddWithValue("$id", eventId);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadDecisionEvent(reader) : null;
        }

        private static long InsertDecisionEventCore(SqliteConnection connection, SqliteTransaction transaction,
            LibraryDecisionTargetKind targetKind, string targetKey, LibraryDecisionEventKind eventKind,
            string beforeState, string afterState, string batchId, string source, long? reversalOf = null)
        {
            if (JsonStatesEqual(beforeState, afterState)) return 0;
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO library_decision_events(target_kind,target_key,event_kind,before_state,after_state,batch_id,source,reversal_of_event_id,occurred_utc_ticks) " +
                                  "VALUES($target_kind,$target_key,$event_kind,$before,$after,$batch,$source,$reversal,$now) RETURNING id;";
            command.Parameters.AddWithValue("$target_kind", (int)targetKind);
            command.Parameters.AddWithValue("$target_key", targetKey);
            command.Parameters.AddWithValue("$event_kind", (int)eventKind);
            command.Parameters.AddWithValue("$before", beforeState);
            command.Parameters.AddWithValue("$after", afterState);
            command.Parameters.AddWithValue("$batch", batchId ?? "");
            command.Parameters.AddWithValue("$source", source ?? "");
            command.Parameters.AddWithValue("$reversal", (object?)reversalOf ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static bool JsonStatesEqual(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            try
            {
                using JsonDocument a = JsonDocument.Parse(left);
                using JsonDocument b = JsonDocument.Parse(right);
                return string.Equals(a.RootElement.GetRawText(), b.RootElement.GetRawText(), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string ReadCurrentDecisionState(SqliteConnection connection, SqliteTransaction transaction, LibraryDecisionEvent item) =>
            item.TargetKind switch
            {
                LibraryDecisionTargetKind.ExactGroup => Serialize(ReadExactDecisionByState(connection, transaction,
                    JsonSerializer.Deserialize<ExactDecisionState>(item.AfterState)!)),
                LibraryDecisionTargetKind.VisualGroup => Serialize(ReadVisualDecisionByState(connection, transaction,
                    JsonSerializer.Deserialize<VisualDecisionState>(item.AfterState)!)),
                LibraryDecisionTargetKind.VisualFamily => Serialize(ReadFamilyDecisionByState(connection, transaction,
                    JsonSerializer.Deserialize<FamilyDecisionState>(item.AfterState)!)),
                LibraryDecisionTargetKind.FileProtection => Serialize(ReadProtectionByState(connection, transaction,
                    JsonSerializer.Deserialize<ProtectionDecisionState>(item.AfterState)!)),
                _ => item.AfterState
            };

        private static void ApplyDecisionState(SqliteConnection connection, SqliteTransaction transaction,
            LibraryDecisionTargetKind kind, string json)
        {
            switch (kind)
            {
                case LibraryDecisionTargetKind.ExactGroup:
                    ApplyExactDecisionState(connection, transaction, JsonSerializer.Deserialize<ExactDecisionState>(json)!);
                    break;
                case LibraryDecisionTargetKind.VisualGroup:
                    ApplyVisualDecisionState(connection, transaction, JsonSerializer.Deserialize<VisualDecisionState>(json)!);
                    break;
                case LibraryDecisionTargetKind.VisualFamily:
                    ApplyFamilyDecisionState(connection, transaction, JsonSerializer.Deserialize<FamilyDecisionState>(json)!);
                    break;
                case LibraryDecisionTargetKind.FileProtection:
                    ApplyProtectionDecisionState(connection, transaction, JsonSerializer.Deserialize<ProtectionDecisionState>(json)!);
                    break;
                default:
                    throw new InvalidOperationException("This decision type cannot be undone automatically.");
            }
        }

        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
        private static string QuarantineTargetKey(bool visual, long auditId) => $"quarantine:{(visual ? "visual" : "exact")}:{auditId}";

        private static ExactDecisionState CaptureExactDecisionState(SqliteConnection connection, SqliteTransaction transaction, long groupId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT g.size_bytes,g.full_algorithm,g.full_version,g.full_hash,d.manual_keeper_path_key,d.reviewed,d.ignored " +
                "FROM exact_duplicate_groups g LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes " +
                "AND d.full_algorithm=g.full_algorithm AND d.full_version=g.full_version AND d.full_hash=g.full_hash WHERE g.id=$group;";
            command.Parameters.AddWithValue("$group", groupId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Duplicate group {groupId} does not exist.");
            bool exists = !reader.IsDBNull(4);
            return new ExactDecisionState(exists, reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                Convert.ToHexString((byte[])reader[3]), exists ? reader.GetString(4) : "",
                exists && reader.GetInt32(5) != 0, exists && reader.GetInt32(6) != 0);
        }

        private static VisualDecisionState CaptureVisualDecisionState(SqliteConnection connection, SqliteTransaction transaction, long groupId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT g.group_key,d.manual_keeper_path_key,d.reviewed,d.ignored,d.not_match " +
                                  "FROM visual_similarity_groups g LEFT JOIN visual_group_decisions d ON d.group_key=g.group_key WHERE g.id=$group;";
            command.Parameters.AddWithValue("$group", groupId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Visual group {groupId} does not exist.");
            bool exists = !reader.IsDBNull(1);
            return new VisualDecisionState(exists, reader.GetString(0), exists ? reader.GetString(1) : "",
                exists && reader.GetInt32(2) != 0, exists && reader.GetInt32(3) != 0, exists && reader.GetInt32(4) != 0);
        }

        private static FamilyDecisionState CaptureFamilyDecisionState(SqliteConnection connection, SqliteTransaction transaction, long familyId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT f.family_key,d.manual_keeper_path_key,d.reviewed,d.ignored FROM visual_families f LEFT JOIN visual_family_decisions d ON d.family_key=f.family_key WHERE f.id=$family;";
            command.Parameters.AddWithValue("$family", familyId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Visual family {familyId} does not exist.");
            bool exists = !reader.IsDBNull(1);
            return new FamilyDecisionState(exists, reader.GetString(0), exists ? reader.GetString(1) : "",
                exists && reader.GetInt32(2) != 0, exists && reader.GetInt32(3) != 0);
        }

        private static ProtectionDecisionState CaptureProtectionDecisionState(SqliteConnection connection, SqliteTransaction transaction, long fileId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT f.path_key,f.full_path,p.protected_path,p.reason FROM indexed_files f " +
                                  "LEFT JOIN duplicate_file_protections p ON p.path_key=f.path_key WHERE f.id=$file;";
            command.Parameters.AddWithValue("$file", fileId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Library file {fileId} does not exist.");
            bool exists = !reader.IsDBNull(2);
            return new ProtectionDecisionState(exists, reader.GetString(0), exists ? reader.GetString(2) : reader.GetString(1),
                exists ? reader.GetString(3) : "");
        }

        private static string ExactTargetKey(ExactDecisionState state) =>
            $"exact:{state.SizeBytes}:{state.Algorithm}:{state.Version}:{state.HashHex}";

        private static LibraryDecisionEventKind ExactEventKind(ExactDecisionState before, ExactDecisionState after) =>
            !string.Equals(before.ManualKeeperPathKey, after.ManualKeeperPathKey, StringComparison.Ordinal)
                ? LibraryDecisionEventKind.KeeperChanged
                : before.Reviewed != after.Reviewed ? LibraryDecisionEventKind.ReviewedChanged
                : LibraryDecisionEventKind.IgnoredChanged;

        private static LibraryDecisionEventKind VisualEventKind(VisualDecisionState before, VisualDecisionState after) =>
            !string.Equals(before.ManualKeeperPathKey, after.ManualKeeperPathKey, StringComparison.Ordinal)
                ? LibraryDecisionEventKind.KeeperChanged
                : before.NotMatch != after.NotMatch ? LibraryDecisionEventKind.NotMatchChanged
                : before.Ignored != after.Ignored ? LibraryDecisionEventKind.IgnoredChanged
                : LibraryDecisionEventKind.ReviewedChanged;

        private static LibraryDecisionEventKind FamilyEventKind(FamilyDecisionState before, FamilyDecisionState after) =>
            !string.Equals(before.ManualKeeperPathKey, after.ManualKeeperPathKey, StringComparison.Ordinal)
                ? LibraryDecisionEventKind.KeeperChanged
                : before.Ignored != after.Ignored ? LibraryDecisionEventKind.IgnoredChanged
                : LibraryDecisionEventKind.ReviewedChanged;

        // Decision snapshot, health-query, and quarantine-query helpers are kept below to keep all phase-1 writes transactional.
        private static ExactDecisionState ReadExactDecisionByState(SqliteConnection connection, SqliteTransaction transaction, ExactDecisionState identity)
        {
            using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT manual_keeper_path_key,reviewed,ignored FROM duplicate_group_decisions WHERE size_bytes=$size AND full_algorithm=$algorithm AND full_version=$version AND full_hash=$hash;";
            command.Parameters.AddWithValue("$size", identity.SizeBytes); command.Parameters.AddWithValue("$algorithm", identity.Algorithm);
            command.Parameters.AddWithValue("$version", identity.Version); command.Parameters.Add("$hash", SqliteType.Blob).Value = Convert.FromHexString(identity.HashHex);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? identity with { Exists = true, ManualKeeperPathKey = reader.GetString(0), Reviewed = reader.GetInt32(1) != 0, Ignored = reader.GetInt32(2) != 0 }
                : identity with { Exists = false, ManualKeeperPathKey = "", Reviewed = false, Ignored = false };
        }

        private static VisualDecisionState ReadVisualDecisionByState(SqliteConnection connection, SqliteTransaction transaction, VisualDecisionState identity)
        {
            using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT manual_keeper_path_key,reviewed,ignored,not_match FROM visual_group_decisions WHERE group_key=$key;";
            command.Parameters.AddWithValue("$key", identity.GroupKey); using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? identity with { Exists = true, ManualKeeperPathKey = reader.GetString(0), Reviewed = reader.GetInt32(1) != 0, Ignored = reader.GetInt32(2) != 0, NotMatch = reader.GetInt32(3) != 0 }
                : identity with { Exists = false, ManualKeeperPathKey = "", Reviewed = false, Ignored = false, NotMatch = false };
        }

        private static FamilyDecisionState ReadFamilyDecisionByState(SqliteConnection connection, SqliteTransaction transaction, FamilyDecisionState identity)
        {
            using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT manual_keeper_path_key,reviewed,ignored FROM visual_family_decisions WHERE family_key=$key;";
            command.Parameters.AddWithValue("$key", identity.FamilyKey); using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? identity with { Exists = true, ManualKeeperPathKey = reader.GetString(0), Reviewed = reader.GetInt32(1) != 0, Ignored = reader.GetInt32(2) != 0 }
                : identity with { Exists = false, ManualKeeperPathKey = "", Reviewed = false, Ignored = false };
        }

        private static ProtectionDecisionState ReadProtectionByState(SqliteConnection connection, SqliteTransaction transaction, ProtectionDecisionState identity)
        {
            using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT protected_path,reason FROM duplicate_file_protections WHERE path_key=$key;";
            command.Parameters.AddWithValue("$key", identity.PathKey); using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? identity with { Exists = true, ProtectedPath = reader.GetString(0), Reason = reader.GetString(1) }
                : identity with { Exists = false, ProtectedPath = "", Reason = "" };
        }

        private static void ApplyExactDecisionState(SqliteConnection c, SqliteTransaction t, ExactDecisionState s)
        {
            using SqliteCommand cmd = c.CreateCommand(); cmd.Transaction = t;
            if (!s.Exists) cmd.CommandText = "DELETE FROM duplicate_group_decisions WHERE size_bytes=$size AND full_algorithm=$algorithm AND full_version=$version AND full_hash=$hash;";
            else cmd.CommandText = "INSERT INTO duplicate_group_decisions(size_bytes,full_algorithm,full_version,full_hash,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks) VALUES($size,$algorithm,$version,$hash,$keeper,$reviewed,$ignored,$now) ON CONFLICT(size_bytes,full_algorithm,full_version,full_hash) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks;";
            cmd.Parameters.AddWithValue("$size", s.SizeBytes); cmd.Parameters.AddWithValue("$algorithm", s.Algorithm); cmd.Parameters.AddWithValue("$version", s.Version);
            cmd.Parameters.Add("$hash", SqliteType.Blob).Value = Convert.FromHexString(s.HashHex); cmd.Parameters.AddWithValue("$keeper", s.ManualKeeperPathKey);
            cmd.Parameters.AddWithValue("$reviewed", s.Reviewed ? 1 : 0); cmd.Parameters.AddWithValue("$ignored", s.Ignored ? 1 : 0); cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); cmd.ExecuteNonQuery();
        }

        private static void ApplyVisualDecisionState(SqliteConnection c, SqliteTransaction t, VisualDecisionState s)
        {
            using SqliteCommand cmd = c.CreateCommand(); cmd.Transaction = t;
            if (!s.Exists) cmd.CommandText = "DELETE FROM visual_group_decisions WHERE group_key=$key;";
            else cmd.CommandText = "INSERT INTO visual_group_decisions(group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks,not_match) VALUES($key,$keeper,$reviewed,$ignored,$now,$not_match) ON CONFLICT(group_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks,not_match=excluded.not_match;";
            cmd.Parameters.AddWithValue("$key", s.GroupKey); cmd.Parameters.AddWithValue("$keeper", s.ManualKeeperPathKey); cmd.Parameters.AddWithValue("$reviewed", s.Reviewed ? 1 : 0);
            cmd.Parameters.AddWithValue("$ignored", s.Ignored ? 1 : 0); cmd.Parameters.AddWithValue("$not_match", s.NotMatch ? 1 : 0); cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); cmd.ExecuteNonQuery();
        }

        private static void ApplyFamilyDecisionState(SqliteConnection c, SqliteTransaction t, FamilyDecisionState s)
        {
            using SqliteCommand cmd = c.CreateCommand(); cmd.Transaction = t;
            if (!s.Exists) cmd.CommandText = "DELETE FROM visual_family_decisions WHERE family_key=$key;";
            else cmd.CommandText = "INSERT INTO visual_family_decisions(family_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks) VALUES($key,$keeper,$reviewed,$ignored,$now) ON CONFLICT(family_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks;";
            cmd.Parameters.AddWithValue("$key", s.FamilyKey); cmd.Parameters.AddWithValue("$keeper", s.ManualKeeperPathKey);
            cmd.Parameters.AddWithValue("$reviewed", s.Reviewed ? 1 : 0); cmd.Parameters.AddWithValue("$ignored", s.Ignored ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); cmd.ExecuteNonQuery();
        }

        private static void ApplyProtectionDecisionState(SqliteConnection c, SqliteTransaction t, ProtectionDecisionState s)
        {
            using SqliteCommand cmd = c.CreateCommand(); cmd.Transaction = t;
            if (!s.Exists) cmd.CommandText = "DELETE FROM duplicate_file_protections WHERE path_key=$key;";
            else cmd.CommandText = "INSERT INTO duplicate_file_protections(path_key,protected_path,reason,updated_utc_ticks) VALUES($key,$path,$reason,$now) ON CONFLICT(path_key) DO UPDATE SET protected_path=excluded.protected_path,reason=excluded.reason,updated_utc_ticks=excluded.updated_utc_ticks;";
            cmd.Parameters.AddWithValue("$key", s.PathKey); cmd.Parameters.AddWithValue("$path", s.ProtectedPath); cmd.Parameters.AddWithValue("$reason", s.Reason); cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); cmd.ExecuteNonQuery();
        }

        private static void ReadPresenceIssues(SqliteConnection c, List<LibraryHealthIssue> result, int limit)
        {
            using SqliteCommand cmd = c.CreateCommand(); cmd.CommandText = "SELECT o.location_id,o.file_id,o.state,o.details,f.full_path,o.related_file_id FROM library_presence_observations o JOIN indexed_files f ON f.id=o.file_id WHERE o.state<>0 ORDER BY o.last_observed_utc_ticks DESC LIMIT $limit;"; cmd.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader r = cmd.ExecuteReader(); while (r.Read())
            {
                var state = (LibraryPresenceObservationState)r.GetInt32(2); long fileId = r.GetInt64(1); string path = r.GetString(4);
                (LibraryHealthIssueKind kind, LibraryHealthSeverity severity, string title, LibraryReanalysisWork work) = state switch
                {
                    LibraryPresenceObservationState.SuspectedMissing => (LibraryHealthIssueKind.SuspectedMissing, LibraryHealthSeverity.Warning, "File is suspected missing", LibraryReanalysisWork.None),
                    LibraryPresenceObservationState.ConfirmedMissing => (LibraryHealthIssueKind.Missing, LibraryHealthSeverity.Warning, "File is confirmed missing", LibraryReanalysisWork.None),
                    LibraryPresenceObservationState.MovedOrRenamed => (LibraryHealthIssueKind.MovedOrRenamed, LibraryHealthSeverity.Warning, "File appears moved or renamed", LibraryReanalysisWork.All),
                    LibraryPresenceObservationState.StaleEvidence => (LibraryHealthIssueKind.StaleVisualEvidence, LibraryHealthSeverity.Warning, "File evidence is stale", LibraryReanalysisWork.All),
                    LibraryPresenceObservationState.AccessFailure => (LibraryHealthIssueKind.AccessFailure, LibraryHealthSeverity.Error, "File access failed", LibraryReanalysisWork.None),
                    _ => (LibraryHealthIssueKind.UnavailableLocation, LibraryHealthSeverity.Warning, "File location is unavailable", LibraryReanalysisWork.None)
                };
                result.Add(new LibraryHealthIssue($"presence:{r.GetInt64(0)}:{fileId}", kind, severity, title, path + Environment.NewLine + r.GetString(3),
                    work == LibraryReanalysisWork.None ? "Verify or rescan the containing location." : "Queue targeted re-analysis after verifying the current path.", fileId, LocationId: r.GetInt64(0), SuggestedReanalysis: work));
            }
        }

        private static void ReadLocationIssues(SqliteConnection c, List<LibraryHealthIssue> result, int limit)
        {
            using SqliteCommand cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,path,availability_state,last_error FROM library_locations WHERE availability_state IN(2,3) ORDER BY updated_utc_ticks DESC LIMIT $limit;"; cmd.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader r = cmd.ExecuteReader(); while (r.Read()) result.Add(new LibraryHealthIssue($"location:{r.GetInt64(0)}", LibraryHealthIssueKind.UnavailableLocation,
                r.GetInt32(2) == 3 ? LibraryHealthSeverity.Error : LibraryHealthSeverity.Warning, "Library location is unavailable", r.GetString(1) + Environment.NewLine + r.GetString(3),
                "Reconnect the location, then run its scan. Files are not treated as deleted.", LocationId: r.GetInt64(0)));
        }

        private static void ReadMetadataIssues(SqliteConnection c, List<LibraryHealthIssue> result, int limit)
        {
            using SqliteCommand cmd = c.CreateCommand(); cmd.CommandText = "SELECT f.id,f.full_path,m.probe_status,m.error_message,CASE WHEN m.source_size_bytes<>f.size_bytes OR m.source_last_write_utc_ticks<>f.last_write_utc_ticks THEN 1 ELSE 0 END FROM indexed_files f JOIN media_metadata m ON m.file_id=f.id WHERE m.probe_status=3 OR m.source_size_bytes<>f.size_bytes OR m.source_last_write_utc_ticks<>f.last_write_utc_ticks ORDER BY m.updated_utc_ticks DESC LIMIT $limit;"; cmd.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader r = cmd.ExecuteReader(); while (r.Read()) { bool stale = r.GetInt32(4) != 0; result.Add(new LibraryHealthIssue($"metadata:{r.GetInt64(0)}", stale ? LibraryHealthIssueKind.StaleMetadata : LibraryHealthIssueKind.ProbeFailure,
                stale ? LibraryHealthSeverity.Warning : LibraryHealthSeverity.Error, stale ? "Metadata is stale" : "FFprobe analysis failed", r.GetString(1) + Environment.NewLine + r.GetString(3),
                "Queue targeted metadata re-analysis.", r.GetInt64(0), SuggestedReanalysis: LibraryReanalysisWork.Metadata)); }
        }

        private static void ReadRunIssues(SqliteConnection c, List<LibraryHealthIssue> result, int limit)
        {
            foreach ((string table, string label) in new[] { ("scan_runs", "scan"), ("duplicate_analysis_runs", "exact analysis"), ("visual_analysis_runs", "visual analysis") })
            {
                using SqliteCommand cmd = c.CreateCommand(); cmd.CommandText = $"SELECT id,status,error_text FROM {table} WHERE status IN(2,3,4) AND error_text<>'' ORDER BY id DESC LIMIT $limit;"; cmd.Parameters.AddWithValue("$limit", Math.Min(limit, 50));
                using SqliteDataReader r = cmd.ExecuteReader(); while (r.Read()) result.Add(new LibraryHealthIssue($"run:{table}:{r.GetInt64(0)}", label == "scan" ? LibraryHealthIssueKind.FailedScan : LibraryHealthIssueKind.FailedAnalysis,
                    r.GetInt32(1) == 3 ? LibraryHealthSeverity.Error : LibraryHealthSeverity.Warning, $"Incomplete {label} run", r.GetString(2), "Review the error and retry the affected work."));
            }
        }

        private static void ReadDerivedEvidenceIssues(SqliteConnection c, List<LibraryHealthIssue> result, int limit)
        {
            using (SqliteCommand exact = c.CreateCommand())
            {
                exact.CommandText = "SELECT f.id,f.full_path FROM indexed_files f JOIN file_hash_facts h ON h.file_id=f.id " +
                    "WHERE h.source_size_bytes<>f.size_bytes OR h.source_last_write_utc_ticks<>f.last_write_utc_ticks OR h.source_volume_id<>f.volume_id OR h.source_file_identity<>f.file_identity LIMIT $limit;";
                exact.Parameters.AddWithValue("$limit", limit);
                using SqliteDataReader reader = exact.ExecuteReader();
                while (reader.Read()) result.Add(new LibraryHealthIssue($"exact-evidence:{reader.GetInt64(0)}",
                    LibraryHealthIssueKind.StaleExactEvidence, LibraryHealthSeverity.Warning, "Exact duplicate evidence is stale",
                    reader.GetString(1), "Queue targeted exact-hash analysis.", reader.GetInt64(0),
                    SuggestedReanalysis: LibraryReanalysisWork.ExactHash));
            }
            using (SqliteCommand visual = c.CreateCommand())
            {
                visual.CommandText = "SELECT f.id,f.full_path FROM indexed_files f JOIN visual_fingerprints v ON v.file_id=f.id " +
                    "WHERE v.source_size_bytes<>f.size_bytes OR v.source_last_write_utc_ticks<>f.last_write_utc_ticks OR v.source_volume_id<>f.volume_id OR v.source_file_identity<>f.file_identity OR v.status<>2 LIMIT $limit;";
                visual.Parameters.AddWithValue("$limit", limit);
                using SqliteDataReader reader = visual.ExecuteReader();
                while (reader.Read()) result.Add(new LibraryHealthIssue($"visual-evidence:{reader.GetInt64(0)}",
                    LibraryHealthIssueKind.StaleVisualEvidence, LibraryHealthSeverity.Warning, "Visual fingerprint evidence is stale",
                    reader.GetString(1), "Queue targeted visual-fingerprint analysis.", reader.GetInt64(0),
                    SuggestedReanalysis: LibraryReanalysisWork.VisualFingerprint));
            }
            using (SqliteCommand groups = c.CreateCommand())
            {
                groups.CommandText = "SELECT id,lifecycle_state,lifecycle_reason FROM visual_similarity_groups WHERE lifecycle_state<>0 ORDER BY lifecycle_updated_utc_ticks DESC LIMIT $limit;";
                groups.Parameters.AddWithValue("$limit", limit);
                using SqliteDataReader reader = groups.ExecuteReader();
                while (reader.Read()) result.Add(new LibraryHealthIssue($"visual-match:{reader.GetInt64(0)}",
                    LibraryHealthIssueKind.StaleDuplicateRecord, LibraryHealthSeverity.Warning, "Visual match is suspended",
                    reader.GetString(2), "Resolve member availability or re-analyze the affected files.", GroupId: reader.GetInt64(0)));
            }
        }

        private static void ReadCleanupAndQueueIssues(SqliteConnection c, List<LibraryHealthIssue> result, int limit)
        {
            foreach ((string table, bool visual) in new[] { ("duplicate_cleanup_plans", false), ("visual_cleanup_plans", true) })
            {
                using SqliteCommand cmd = c.CreateCommand(); cmd.CommandText = $"SELECT id,status,error_text FROM {table} WHERE status IN(1,2,4) ORDER BY id DESC LIMIT $limit;"; cmd.Parameters.AddWithValue("$limit", Math.Min(limit, 100));
                using SqliteDataReader r = cmd.ExecuteReader(); while (r.Read()) result.Add(new LibraryHealthIssue($"cleanup:{(visual ? "visual" : "exact")}:{r.GetInt64(0)}", LibraryHealthIssueKind.UnresolvedCleanup,
                    r.GetInt32(1) == 4 ? LibraryHealthSeverity.Error : LibraryHealthSeverity.Warning, "Cleanup plan needs attention", $"Plan {r.GetInt64(0)}: {r.GetString(2)}", "Review the cleanup audit before retrying or creating a new plan."));
            }
            using SqliteCommand queue = c.CreateCommand(); queue.CommandText = "SELECT q.id,q.file_id,f.full_path,q.error_text FROM library_reanalysis_queue q JOIN indexed_files f ON f.id=q.file_id WHERE q.status=3 ORDER BY q.updated_utc_ticks DESC LIMIT $limit;"; queue.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader qr = queue.ExecuteReader(); while (qr.Read()) result.Add(new LibraryHealthIssue($"reanalysis:{qr.GetInt64(0)}", LibraryHealthIssueKind.ReanalysisFailure, LibraryHealthSeverity.Error,
                "Targeted re-analysis exhausted its retries", qr.GetString(2) + Environment.NewLine + qr.GetString(3), "Verify availability, then queue the file again.", qr.GetInt64(1), SuggestedReanalysis: LibraryReanalysisWork.All));
        }

        private static void ReadIntegrityIssues(SqliteConnection c, List<LibraryHealthIssue> result, int limit)
        {
            using SqliteCommand command = c.CreateCommand();
            command.CommandText = """
                SELECT f.id,f.full_path,r.result_state,r.scrub_type,r.error_category,r.details,
                       CASE WHEN r.method_version<>1 OR r.source_size_bytes<>f.size_bytes OR r.source_last_write_utc_ticks<>f.last_write_utc_ticks OR
                                      (r.source_volume_id<>'' AND r.source_volume_id<>f.volume_id) OR
                                      (r.source_file_identity<>'' AND r.source_file_identity<>f.file_identity) THEN 1 ELSE 0 END stale
                FROM media_integrity_results r JOIN indexed_files f ON f.id=r.file_id
                WHERE r.result_state IN(5,8) OR (r.result_state IN(3,4,5) AND
                      (r.method_version<>1 OR r.source_size_bytes<>f.size_bytes OR r.source_last_write_utc_ticks<>f.last_write_utc_ticks OR
                       (r.source_volume_id<>'' AND r.source_volume_id<>f.volume_id) OR
                       (r.source_file_identity<>'' AND r.source_file_identity<>f.file_identity)))
                ORDER BY r.updated_utc_ticks DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long fileId = reader.GetInt64(0); string path = reader.GetString(1); bool stale = reader.GetInt32(6) != 0;
                LibraryIntegrityResultState state = (LibraryIntegrityResultState)reader.GetInt32(2);
                LibraryIntegrityScrubType type = (LibraryIntegrityScrubType)reader.GetInt32(3);
                LibraryHealthIssueKind kind = stale ? LibraryHealthIssueKind.IntegrityResultStale :
                    state == LibraryIntegrityResultState.Cancelled ? LibraryHealthIssueKind.IntegrityCheckInterrupted : LibraryHealthIssueKind.IntegrityCheckFailed;
                string title = stale ? "Media integrity result is stale" : state == LibraryIntegrityResultState.Cancelled ? "Media integrity check was interrupted" : "Media integrity verification failed";
                string action = stale ? "Re-analyze metadata, then run Quick Scrub." : $"Retry {type} Scrub; use Full Scrub for deeper verification when appropriate.";
                result.Add(new LibraryHealthIssue($"integrity:{fileId}", kind,
                    state == LibraryIntegrityResultState.Failed && !stale ? LibraryHealthSeverity.Error : LibraryHealthSeverity.Warning,
                    title, path + Environment.NewLine + reader.GetString(5), action, fileId,
                    SuggestedReanalysis: stale ? LibraryReanalysisWork.Metadata : LibraryReanalysisWork.None,
                    SuggestedIntegrityScrub: stale ? LibraryIntegrityScrubType.Quick : type));
            }
        }

        private static void ReadQuarantineCandidates(SqliteConnection c, List<LibraryQuarantineRestoreItem> result, bool visual, int limit)
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = visual
                ? "SELECT a.id,a.plan_id,a.file_id,a.source_path,a.destination_path,CASE WHEN i.file_id=a.file_id THEN i.source_size_bytes ELSE i.keeper_size_bytes END,CASE WHEN i.file_id=a.file_id THEN i.source_last_write_utc_ticks ELSE i.keeper_last_write_utc_ticks END,CASE WHEN i.file_id=a.file_id THEN i.source_volume_id ELSE i.keeper_volume_id END,CASE WHEN i.file_id=a.file_id THEN i.source_file_identity ELSE i.keeper_file_identity END,i.exact_hash FROM visual_cleanup_audit a JOIN visual_cleanup_plan_items i ON i.plan_id=a.plan_id AND a.file_id IN(i.file_id,i.keeper_file_id) WHERE a.action=1 AND a.outcome=2 AND a.destination_path<>'' AND NOT EXISTS(SELECT 1 FROM library_decision_events e WHERE e.target_key='quarantine:visual:'||a.id AND e.event_kind=6) ORDER BY a.id DESC LIMIT $limit;"
                : "SELECT a.id,a.plan_id,a.file_id,a.source_path,a.destination_path,i.source_size_bytes,i.source_last_write_utc_ticks,i.source_volume_id,i.source_file_identity,i.full_hash FROM duplicate_cleanup_audit a JOIN duplicate_cleanup_plan_items i ON i.plan_id=a.plan_id AND i.file_id=a.file_id WHERE a.action=1 AND a.outcome=2 AND a.destination_path<>'' AND NOT EXISTS(SELECT 1 FROM library_decision_events e WHERE e.target_key='quarantine:exact:'||a.id AND e.event_kind=6) ORDER BY a.id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000)); using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new LibraryQuarantineRestoreItem(visual, r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5), FromUtcTicks(r.GetInt64(6)), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : (byte[])r[9]));
        }
    }
}
