using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        private const int VisualCleanupBatchSize = 500;

        public long CreateVisualCleanupPlan(
            DuplicateCleanupAction action,
            string quarantineRoot,
            bool allowUnreviewed,
            double minimumConfidence,
            IReadOnlyCollection<VisualCleanupPlanItemRecord> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Count == 0)
                throw new ArgumentException("A visual cleanup plan must contain at least one item.", nameof(items));
            long planId = BeginVisualCleanupPlan(action, quarantineRoot, allowUnreviewed, minimumConfidence);
            try
            {
                foreach (VisualCleanupPlanItemRecord[] batch in items.Chunk(VisualCleanupBatchSize))
                    AppendVisualCleanupPlanItems(planId, batch);
                MarkVisualCleanupPlanReady(planId);
                return planId;
            }
            catch
            {
                CompleteVisualCleanupPlan(planId, DuplicateCleanupStatus.Failed,
                    "Visual cleanup planning failed before the plan became ready.");
                throw;
            }
        }

        public long BeginVisualCleanupPlan(
            DuplicateCleanupAction action,
            string quarantineRoot,
            bool allowUnreviewed,
            double minimumConfidence)
        {
            ThrowIfDisposed();
            if (action == DuplicateCleanupAction.Quarantine && string.IsNullOrWhiteSpace(quarantineRoot))
                throw new ArgumentException("A quarantine root is required.", nameof(quarantineRoot));
            minimumConfidence = Math.Clamp(minimumConfidence, 0, 100);
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand plan = connection.CreateCommand();
                plan.Transaction = transaction;
                plan.CommandText =
                    """
                    INSERT INTO visual_cleanup_plans(
                        action,status,quarantine_root,allow_unreviewed,minimum_confidence,created_utc_ticks)
                    VALUES($action,$status,$root,$unreviewed,$confidence,$now)
                    RETURNING id;
                    """;
                plan.Parameters.AddWithValue("$action", (int)action);
                plan.Parameters.AddWithValue("$status", (int)DuplicateCleanupStatus.Draft);
                plan.Parameters.AddWithValue("$root", quarantineRoot ?? "");
                plan.Parameters.AddWithValue("$unreviewed", allowUnreviewed ? 1 : 0);
                plan.Parameters.AddWithValue("$confidence", minimumConfidence);
                plan.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                return Convert.ToInt64(plan.ExecuteScalar());
            });
        }

        public void AppendVisualCleanupPlanItems(
            long planId,
            IReadOnlyCollection<VisualCleanupPlanItemRecord> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Count == 0) return;
            if (items.Count > VisualCleanupBatchSize)
                throw new ArgumentOutOfRangeException(nameof(items),
                    $"Visual cleanup plan append batches are limited to {VisualCleanupBatchSize} items.");
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using (SqliteCommand validate = connection.CreateCommand())
                {
                    validate.Transaction = transaction;
                    validate.CommandText = "SELECT status FROM visual_cleanup_plans WHERE id=$id;";
                    validate.Parameters.AddWithValue("$id", planId);
                    object? status = validate.ExecuteScalar();
                    if (status == null) throw new KeyNotFoundException($"Visual cleanup plan {planId} does not exist.");
                    if ((DuplicateCleanupStatus)Convert.ToInt32(status) != DuplicateCleanupStatus.Draft)
                        throw new InvalidOperationException("Visual cleanup items can only be appended to a draft plan.");
                }

                using SqliteCommand item = connection.CreateCommand();
                item.Transaction = transaction;
                item.CommandText =
                    """
                    INSERT INTO visual_cleanup_plan_items(
                        plan_id,group_key,group_id,file_id,keeper_file_id,source_path,source_size_bytes,
                        source_last_write_utc_ticks,source_volume_id,source_file_identity,keeper_path,
                        keeper_size_bytes,keeper_last_write_utc_ticks,keeper_volume_id,keeper_file_identity,
                        confidence_score,exact_hash,cleanup_intent,status,family_id)
                    VALUES(
                        $plan,$key,$group,$file,$keeper,$source,$source_size,$source_time,$source_volume,
                        $source_identity,$keeper_path,$keeper_size,$keeper_time,$keeper_volume,$keeper_identity,
                        $confidence,$hash,$intent,$status,$family);
                    """;
                foreach (string name in new[]
                         {
                             "$plan", "$key", "$group", "$file", "$keeper", "$source", "$source_size",
                             "$source_time", "$source_volume", "$source_identity", "$keeper_path", "$keeper_size",
                             "$keeper_time", "$keeper_volume", "$keeper_identity", "$confidence", "$hash",
                             "$intent", "$status", "$family"
                         })
                    item.Parameters.Add(new SqliteParameter(name, null));

                foreach (VisualCleanupPlanItemRecord source in items)
                {
                    item.Parameters["$plan"].Value = planId;
                    item.Parameters["$key"].Value = source.GroupKey;
                    item.Parameters["$group"].Value = source.GroupId;
                    item.Parameters["$file"].Value = source.FileId;
                    item.Parameters["$keeper"].Value = source.KeeperFileId;
                    item.Parameters["$source"].Value = source.SourcePath;
                    item.Parameters["$source_size"].Value = source.SourceSizeBytes;
                    item.Parameters["$source_time"].Value = source.SourceLastWriteUtc.Ticks;
                    item.Parameters["$source_volume"].Value = source.SourceVolumeId ?? "";
                    item.Parameters["$source_identity"].Value = source.SourceFileIdentity ?? "";
                    item.Parameters["$keeper_path"].Value = source.KeeperPath;
                    item.Parameters["$keeper_size"].Value = source.KeeperSizeBytes;
                    item.Parameters["$keeper_time"].Value = source.KeeperLastWriteUtc.Ticks;
                    item.Parameters["$keeper_volume"].Value = source.KeeperVolumeId ?? "";
                    item.Parameters["$keeper_identity"].Value = source.KeeperFileIdentity ?? "";
                    item.Parameters["$confidence"].Value = source.ConfidenceScore;
                    item.Parameters["$hash"].Value = (object?)source.ExactHash ?? DBNull.Value;
                    item.Parameters["$intent"].Value = (int)source.Intent;
                    item.Parameters["$status"].Value = (int)DuplicateCleanupItemStatus.Planned;
                    item.Parameters["$family"].Value = (object?)source.FamilyId ?? DBNull.Value;
                    item.ExecuteNonQuery();
                }
                return null;
            });
        }

        public void MarkVisualCleanupPlanReady(long planId) =>
            TransitionVisualCleanupPlan(
                planId,
                DuplicateCleanupStatus.Draft,
                DuplicateCleanupStatus.Ready,
                requireItems: true);

        public void MarkVisualCleanupPlanRunning(long planId) =>
            TransitionVisualCleanupPlan(
                planId,
                DuplicateCleanupStatus.Ready,
                DuplicateCleanupStatus.Running,
                requireItems: true);

        private void TransitionVisualCleanupPlan(
            long planId,
            DuplicateCleanupStatus from,
            DuplicateCleanupStatus to,
            bool requireItems)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE visual_cleanup_plans
                    SET status=$to
                    WHERE id=$id AND status=$from
                      AND ($require=0 OR EXISTS(
                            SELECT 1 FROM visual_cleanup_plan_items WHERE plan_id=$id));
                    """;
                command.Parameters.AddWithValue("$to", (int)to);
                command.Parameters.AddWithValue("$id", planId);
                command.Parameters.AddWithValue("$from", (int)from);
                command.Parameters.AddWithValue("$require", requireItems ? 1 : 0);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException(
                        $"Visual cleanup plan {planId} could not transition from {from} to {to}.");
                return null;
            });
        }

        public int RecoverInterruptedVisualCleanupPlans()
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                int recovered;
                using (SqliteCommand drafts = connection.CreateCommand())
                {
                    drafts.Transaction = transaction;
                    drafts.CommandText =
                        """
                        UPDATE visual_cleanup_plans
                        SET status=$failed,completed_utc_ticks=$now,
                            error_text=CASE WHEN error_text='' THEN
                                'Interrupted while the visual cleanup plan was being created.'
                                ELSE error_text END
                        WHERE status=$draft;
                        """;
                    drafts.Parameters.AddWithValue("$failed", (int)DuplicateCleanupStatus.Failed);
                    drafts.Parameters.AddWithValue("$draft", (int)DuplicateCleanupStatus.Draft);
                    drafts.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    recovered = drafts.ExecuteNonQuery();
                }
                using (SqliteCommand running = connection.CreateCommand())
                {
                    running.Transaction = transaction;
                    running.CommandText =
                        """
                        UPDATE visual_cleanup_plans
                        SET status=$ready,completed_utc_ticks=NULL,
                            error_text=CASE WHEN error_text='' THEN
                                'Recovered after interruption; completed items will not be repeated.'
                                ELSE error_text END
                        WHERE status=$running;
                        """;
                    running.Parameters.AddWithValue("$ready", (int)DuplicateCleanupStatus.Ready);
                    running.Parameters.AddWithValue("$running", (int)DuplicateCleanupStatus.Running);
                    recovered += running.ExecuteNonQuery();
                }
                return recovered;
            });
        }

        public VisualCleanupPlanSummary? GetVisualCleanupPlanSummary(
            long planId,
            bool includeLocations = true)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT p.action,p.status,p.quarantine_root,p.allow_unreviewed,p.minimum_confidence,
                       p.created_utc_ticks,p.completed_utc_ticks,p.error_text,
                       COUNT(i.file_id),
                       COUNT(DISTINCT CASE WHEN i.family_id IS NOT NULL THEN i.family_id END),
                       COUNT(DISTINCT i.keeper_file_id),
                       COALESCE(SUM(CASE WHEN i.status=$planned THEN 1 ELSE 0 END),0),
                       (SELECT COUNT(*) FROM visual_cleanup_audit a
                        WHERE a.plan_id=p.id AND a.outcome=$succeeded),
                       (SELECT COUNT(*) FROM visual_cleanup_audit a
                        WHERE a.plan_id=p.id AND a.outcome=$excluded),
                       (SELECT COUNT(*) FROM visual_cleanup_audit a
                        WHERE a.plan_id=p.id AND a.outcome=$failed),
                       COALESCE(SUM(CASE WHEN i.status=$planned THEN i.source_size_bytes ELSE 0 END),0),
                       COALESCE((
                           SELECT SUM(CASE WHEN a.file_id=completed.file_id
                                           THEN completed.source_size_bytes
                                           ELSE completed.keeper_size_bytes END)
                           FROM visual_cleanup_audit a
                           JOIN visual_cleanup_plan_items completed
                             ON completed.plan_id=a.plan_id
                            AND a.file_id IN(completed.file_id,completed.keeper_file_id)
                           WHERE a.plan_id=p.id AND a.outcome=$succeeded),0)
                FROM visual_cleanup_plans p
                LEFT JOIN visual_cleanup_plan_items i ON i.plan_id=p.id
                WHERE p.id=$id
                GROUP BY p.id;
                """;
            command.Parameters.AddWithValue("$id", planId);
            command.Parameters.AddWithValue("$planned", (int)DuplicateCleanupItemStatus.Planned);
            command.Parameters.AddWithValue("$succeeded", (int)DuplicateCleanupItemStatus.Succeeded);
            command.Parameters.AddWithValue("$excluded", (int)DuplicateCleanupItemStatus.Excluded);
            command.Parameters.AddWithValue("$failed", (int)DuplicateCleanupItemStatus.Failed);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var header = new
            {
                Action = (DuplicateCleanupAction)reader.GetInt32(0),
                Status = (DuplicateCleanupStatus)reader.GetInt32(1),
                Root = reader.GetString(2),
                AllowUnreviewed = reader.GetInt32(3) != 0,
                Confidence = reader.GetDouble(4),
                Created = FromUtcTicks(reader.GetInt64(5)),
                Completed = reader.IsDBNull(6) ? (DateTime?)null : FromUtcTicks(reader.GetInt64(6)),
                Error = reader.GetString(7),
                Total = reader.GetInt64(8),
                Families = reader.GetInt64(9),
                Keepers = reader.GetInt64(10),
                Planned = reader.GetInt64(11),
                Succeeded = reader.GetInt64(12),
                Excluded = reader.GetInt64(13),
                Failed = reader.GetInt64(14),
                PlannedBytes = reader.GetInt64(15),
                ReclaimedBytes = reader.GetInt64(16)
            };
            reader.Close();
            if (!includeLocations)
                return new VisualCleanupPlanSummary(
                    planId, header.Action, header.Status, header.Root, header.AllowUnreviewed,
                    header.Confidence, header.Created, header.Completed, header.Error, header.Total,
                    header.Families, header.Keepers, header.Planned, header.Succeeded, header.Excluded,
                    header.Failed, header.PlannedBytes, header.ReclaimedBytes,
                    Array.Empty<VisualCleanupLocationSummary>());

            using SqliteCommand locations = connection.CreateCommand();
            locations.CommandText =
                """
                SELECT COALESCE(
                           (SELECT MIN(l.path)
                            FROM file_location_memberships m
                            JOIN library_locations l ON l.id=m.location_id
                            WHERE m.file_id=i.file_id),
                           '(Unmapped)'),
                       COUNT(*),COALESCE(SUM(i.source_size_bytes),0)
                FROM visual_cleanup_plan_items i
                WHERE i.plan_id=$id AND i.status=$planned
                GROUP BY 1
                ORDER BY 3 DESC,1;
                """;
            locations.Parameters.AddWithValue("$id", planId);
            locations.Parameters.AddWithValue("$planned", (int)DuplicateCleanupItemStatus.Planned);
            using SqliteDataReader locationReader = locations.ExecuteReader();
            var locationRows = new List<VisualCleanupLocationSummary>();
            while (locationReader.Read())
                locationRows.Add(new VisualCleanupLocationSummary(
                    locationReader.GetString(0),
                    locationReader.GetInt64(1),
                    locationReader.GetInt64(2)));
            return new VisualCleanupPlanSummary(
                planId, header.Action, header.Status, header.Root, header.AllowUnreviewed,
                header.Confidence, header.Created, header.Completed, header.Error, header.Total,
                header.Families, header.Keepers, header.Planned, header.Succeeded, header.Excluded,
                header.Failed, header.PlannedBytes, header.ReclaimedBytes, locationRows);
        }

        public IReadOnlyList<VisualCleanupPlanItemRecord> GetVisualCleanupPlanItemsBatch(
            long planId,
            long afterGroupId,
            long afterFileId,
            int limit,
            DuplicateCleanupItemStatus? status = null)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, VisualCleanupBatchSize);
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand items = connection.CreateCommand();
            items.CommandText =
                """
                SELECT plan_id,group_key,group_id,file_id,keeper_file_id,source_path,source_size_bytes,
                       source_last_write_utc_ticks,source_volume_id,source_file_identity,keeper_path,
                       keeper_size_bytes,keeper_last_write_utc_ticks,keeper_volume_id,keeper_file_identity,
                       confidence_score,exact_hash,cleanup_intent,status,destination_path,validation_error,
                       family_id
                FROM visual_cleanup_plan_items
                WHERE plan_id=$id
                  AND (group_id>$after_group OR (group_id=$after_group AND file_id>$after_file))
                  AND ($status IS NULL OR status=$status)
                ORDER BY group_id,file_id
                LIMIT $limit;
                """;
            items.Parameters.AddWithValue("$id", planId);
            items.Parameters.AddWithValue("$after_group", Math.Max(0, afterGroupId));
            items.Parameters.AddWithValue("$after_file", Math.Max(0, afterFileId));
            items.Parameters.AddWithValue("$status", status.HasValue ? (int)status.Value : DBNull.Value);
            items.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = items.ExecuteReader();
            var result = new List<VisualCleanupPlanItemRecord>(limit);
            while (reader.Read()) result.Add(ReadVisualCleanupPlanItem(reader));
            return result;
        }

        public VisualCleanupPlanRecord? GetVisualCleanupPlan(long planId)
        {
            VisualCleanupPlanSummary? summary = GetVisualCleanupPlanSummary(planId, includeLocations: false);
            if (summary == null) return null;
            var all = new List<VisualCleanupPlanItemRecord>();
            long afterGroupId = 0;
            long afterFileId = 0;
            while (true)
            {
                IReadOnlyList<VisualCleanupPlanItemRecord> batch =
                    GetVisualCleanupPlanItemsBatch(planId, afterGroupId, afterFileId, VisualCleanupBatchSize);
                if (batch.Count == 0) break;
                all.AddRange(batch);
                VisualCleanupPlanItemRecord last = batch[^1];
                afterGroupId = last.GroupId;
                afterFileId = last.FileId;
                if (batch.Count < VisualCleanupBatchSize) break;
            }
            return new VisualCleanupPlanRecord(
                summary.PlanId, summary.Action, summary.Status, summary.QuarantineRoot,
                summary.AllowUnreviewed, summary.MinimumConfidence, summary.CreatedUtc,
                summary.CompletedUtc, summary.ErrorText, all);
        }

        private static VisualCleanupPlanItemRecord ReadVisualCleanupPlanItem(SqliteDataReader reader) => new(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6),
            FromUtcTicks(reader.GetInt64(7)), reader.GetString(8), reader.GetString(9),
            reader.GetString(10), reader.GetInt64(11), FromUtcTicks(reader.GetInt64(12)),
            reader.GetString(13), reader.GetString(14), reader.GetDouble(15),
            reader.IsDBNull(16) ? null : (byte[])reader[16],
            (VisualCleanupIntent)reader.GetInt32(17),
            (DuplicateCleanupItemStatus)reader.GetInt32(18), reader.GetString(19),
            reader.GetString(20), reader.IsDBNull(21) ? null : reader.GetInt64(21));

        public void UpdateVisualCleanupPlanItem(
            long planId,
            long fileId,
            DuplicateCleanupItemStatus status,
            string destinationPath,
            string validationError) =>
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE visual_cleanup_plan_items
                    SET status=$status,destination_path=$destination,validation_error=$error
                    WHERE plan_id=$plan AND file_id=$file
                      AND status IN($planned,$validated);
                    """;
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$destination", destinationPath ?? "");
                command.Parameters.AddWithValue("$error", validationError ?? "");
                command.Parameters.AddWithValue("$plan", planId);
                command.Parameters.AddWithValue("$file", fileId);
                command.Parameters.AddWithValue("$planned", (int)DuplicateCleanupItemStatus.Planned);
                command.Parameters.AddWithValue("$validated", (int)DuplicateCleanupItemStatus.Validated);
                command.ExecuteNonQuery();
                return null;
            });

        public void CompleteVisualCleanupPlan(
            long planId,
            DuplicateCleanupStatus status,
            string errorText = "") =>
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE visual_cleanup_plans
                    SET status=$status,completed_utc_ticks=$now,error_text=$error
                    WHERE id=$plan;
                    """;
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$error", errorText ?? "");
                command.Parameters.AddWithValue("$plan", planId);
                command.ExecuteNonQuery();
                return null;
            });

        public void AppendVisualCleanupAudit(
            long planId,
            long fileId,
            string sourcePath,
            string destinationPath,
            DuplicateCleanupAction action,
            DuplicateCleanupItemStatus outcome,
            string message) =>
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO visual_cleanup_audit(
                        plan_id,file_id,source_path,destination_path,action,outcome,message,occurred_utc_ticks)
                    VALUES($plan,$file,$source,$destination,$action,$outcome,$message,$now);
                    """;
                command.Parameters.AddWithValue("$plan", planId);
                command.Parameters.AddWithValue("$file", fileId);
                command.Parameters.AddWithValue("$source", sourcePath ?? "");
                command.Parameters.AddWithValue("$destination", destinationPath ?? "");
                command.Parameters.AddWithValue("$action", (int)action);
                command.Parameters.AddWithValue("$outcome", (int)outcome);
                command.Parameters.AddWithValue("$message", message ?? "");
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.ExecuteNonQuery();
                return null;
            });
    }
}
