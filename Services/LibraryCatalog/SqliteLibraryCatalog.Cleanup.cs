using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        public string CreateUserDataBackup(string? destinationPath = null)
        {
            ThrowIfDisposed();
            return WithWriterGate(() => CreateUserDataBackupCore(destinationPath));
        }

        private string CreateUserDataBackupCore(string? destinationPath)
        {
            string path = string.IsNullOrWhiteSpace(destinationPath)
                ? Path.Combine(_database.BackupDirectory, $"library-user-data-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.db")
                : Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
                throw new IOException($"The user-data backup already exists: {path}");
            using SqliteConnection connection = _database.OpenConnection(readOnly: false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                ATTACH DATABASE $path AS userdata;
                CREATE TABLE userdata.backup_manifest(created_utc_ticks INTEGER NOT NULL, source_schema_version INTEGER NOT NULL) STRICT;
                INSERT INTO userdata.backup_manifest VALUES($now, $version);
                CREATE TABLE userdata.duplicate_group_decisions AS SELECT * FROM main.duplicate_group_decisions;
                CREATE TABLE userdata.duplicate_file_protections AS SELECT * FROM main.duplicate_file_protections;
                CREATE TABLE userdata.duplicate_cleanup_plans AS SELECT * FROM main.duplicate_cleanup_plans;
                CREATE TABLE userdata.duplicate_cleanup_plan_items AS SELECT * FROM main.duplicate_cleanup_plan_items;
                CREATE TABLE userdata.duplicate_cleanup_audit AS SELECT * FROM main.duplicate_cleanup_audit;
                CREATE TABLE userdata.visual_group_decisions AS SELECT * FROM main.visual_group_decisions;
                CREATE TABLE userdata.visual_family_decisions AS SELECT * FROM main.visual_family_decisions;
                CREATE TABLE userdata.visual_cleanup_plans AS SELECT * FROM main.visual_cleanup_plans;
                CREATE TABLE userdata.visual_cleanup_plan_items AS SELECT * FROM main.visual_cleanup_plan_items;
                CREATE TABLE userdata.visual_cleanup_audit AS SELECT * FROM main.visual_cleanup_audit;
                CREATE TABLE userdata.library_decision_events AS SELECT * FROM main.library_decision_events;
                DETACH DATABASE userdata;
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            command.Parameters.AddWithValue("$version", LibraryCatalogMigrations.CurrentVersion);
            command.ExecuteNonQuery();
            return path;
        }

        public LibraryUserDataRestoreResult RestoreUserDataBackup(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("A user-data backup path is required.", nameof(sourcePath));
            ThrowIfDisposed();
            string path = Path.GetFullPath(sourcePath);
            if (!File.Exists(path)) throw new FileNotFoundException("The user-data backup does not exist.", path);
            return WithWriterGate(() =>
            {
                using SqliteConnection connection = _database.OpenConnection(readOnly: false);
                bool attached = false;
                try
                {
                    using (SqliteCommand attach = connection.CreateCommand())
                    {
                        attach.CommandText = "ATTACH DATABASE $path AS restored;";
                        attach.Parameters.AddWithValue("$path", path);
                        attach.ExecuteNonQuery();
                        attached = true;
                    }
                    using (SqliteCommand integrity = connection.CreateCommand())
                    {
                        integrity.CommandText = "PRAGMA restored.integrity_check;";
                        if (!string.Equals(Convert.ToString(integrity.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The selected user-data backup failed SQLite integrity checking.");
                    }
                    if (!AttachedTableExists(connection, "backup_manifest") ||
                        !AttachedTableExists(connection, "duplicate_group_decisions") ||
                        !AttachedTableExists(connection, "duplicate_file_protections"))
                        throw new InvalidDataException("The selected database is not a compatible MediaFlux user-data backup.");

                    using SqliteCommand version = connection.CreateCommand();
                    version.CommandText = "SELECT source_schema_version FROM restored.backup_manifest LIMIT 1;";
                    int sourceVersion = Convert.ToInt32(version.ExecuteScalar());
                    if (sourceVersion < 4 || sourceVersion > LibraryCatalogMigrations.CurrentVersion)
                        throw new InvalidDataException($"The backup schema version {sourceVersion} is not supported by this MediaFlux version.");

                    using SqliteTransaction transaction = connection.BeginTransaction();
                    int decisions = RestoreTable(connection, transaction,
                        """
                        INSERT INTO duplicate_group_decisions(size_bytes,full_algorithm,full_version,full_hash,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks)
                        SELECT size_bytes,full_algorithm,full_version,full_hash,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks FROM restored.duplicate_group_decisions WHERE 1
                        ON CONFLICT(size_bytes,full_algorithm,full_version,full_hash) DO UPDATE SET
                            manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks;
                        """);
                    int protections = RestoreTable(connection, transaction,
                        """
                        INSERT INTO duplicate_file_protections(path_key,protected_path,reason,updated_utc_ticks)
                        SELECT path_key,protected_path,reason,updated_utc_ticks FROM restored.duplicate_file_protections WHERE 1
                        ON CONFLICT(path_key) DO UPDATE SET protected_path=excluded.protected_path,reason=excluded.reason,updated_utc_ticks=excluded.updated_utc_ticks;
                        """);
                    int visual = 0;
                    if (AttachedTableExists(connection, "visual_group_decisions"))
                    {
                        string visualRestoreSql = sourceVersion >= 7
                            ?
                            """
                            INSERT INTO visual_group_decisions(group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks,not_match)
                            SELECT group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks,not_match FROM restored.visual_group_decisions WHERE 1
                            ON CONFLICT(group_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks,not_match=excluded.not_match;
                            """
                            :
                            """
                            INSERT INTO visual_group_decisions(group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks,not_match)
                            SELECT group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks,0 FROM restored.visual_group_decisions WHERE 1
                            ON CONFLICT(group_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks,not_match=0;
                            """;
                        visual = RestoreTable(connection, transaction,
                            visualRestoreSql);
                    }
                    int history = 0;
                    if (sourceVersion >= 8 && AttachedTableExists(connection, "library_decision_events"))
                    {
                        history = RestoreTable(connection, transaction,
                            """
                            INSERT INTO library_decision_events(target_kind,target_key,event_kind,before_state,after_state,batch_id,source,
                                reversal_of_event_id,reversed_by_event_id,occurred_utc_ticks)
                            SELECT target_kind,target_key,event_kind,before_state,after_state,batch_id,'restored-history',NULL,NULL,occurred_utc_ticks
                            FROM restored.library_decision_events;
                            """);
                    }
                    int familyDecisions = 0;
                    if (sourceVersion >= 9 && AttachedTableExists(connection, "visual_family_decisions"))
                    {
                        familyDecisions = RestoreTable(connection, transaction,
                            """
                            INSERT INTO visual_family_decisions(family_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks)
                            SELECT family_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks FROM restored.visual_family_decisions WHERE 1
                            ON CONFLICT(family_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,
                                reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks;
                            """);
                    }
                    transaction.Commit();
                    return new LibraryUserDataRestoreResult(
                        decisions,
                        protections,
                        visual,
                        new[] { "Cleanup plan and audit history is retained in the backup for audit purposes but is not re-executed or imported. Restored decision-event history remains read-only." },
                        history,
                        familyDecisions);
                }
                finally
                {
                    if (attached)
                    {
                        using SqliteCommand detach = connection.CreateCommand();
                        detach.CommandText = "DETACH DATABASE restored;";
                        detach.ExecuteNonQuery();
                    }
                }
            });
        }

        private static bool AttachedTableExists(SqliteConnection connection, string table)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM restored.sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        private static int RestoreTable(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
            using SqliteCommand changes = connection.CreateCommand();
            changes.Transaction = transaction;
            changes.CommandText = "SELECT changes();";
            return Convert.ToInt32(changes.ExecuteScalar());
        }

        public long CreateCleanupPlan(
            DuplicateCleanupAction action,
            string quarantineRoot,
            IReadOnlyCollection<DuplicateCleanupPlanItemRecord> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Count == 0)
                throw new ArgumentException("A cleanup plan must contain at least one item.", nameof(items));
            if (action == DuplicateCleanupAction.Quarantine && string.IsNullOrWhiteSpace(quarantineRoot))
                throw new ArgumentException("A quarantine root is required.", nameof(quarantineRoot));
            long planId = BeginCleanupPlan(action, quarantineRoot);
            try
            {
                AppendCleanupPlanItems(planId, items);
                MarkCleanupPlanReady(planId);
                return planId;
            }
            catch
            {
                CompleteCleanupPlan(planId, DuplicateCleanupStatus.Failed, "Cleanup planning failed before the plan became ready.");
                throw;
            }
        }

        public long BeginCleanupPlan(DuplicateCleanupAction action, string quarantineRoot)
        {
            if (action == DuplicateCleanupAction.Quarantine && string.IsNullOrWhiteSpace(quarantineRoot))
                throw new ArgumentException("A quarantine root is required.", nameof(quarantineRoot));
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand plan = connection.CreateCommand();
                plan.Transaction = transaction;
                plan.CommandText = "INSERT INTO duplicate_cleanup_plans(action,status,quarantine_root,created_utc_ticks) VALUES($action,$status,$root,$now) RETURNING id;";
                plan.Parameters.AddWithValue("$action", (int)action);
                plan.Parameters.AddWithValue("$status", (int)DuplicateCleanupStatus.Draft);
                plan.Parameters.AddWithValue("$root", quarantineRoot ?? "");
                plan.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                return Convert.ToInt64(plan.ExecuteScalar());
            });
        }

        public void AppendCleanupPlanItems(long planId, IReadOnlyCollection<DuplicateCleanupPlanItemRecord> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Count == 0) return;
            if (items.Count > 500) throw new ArgumentOutOfRangeException(nameof(items), "Cleanup plan append batches are limited to 500 items.");
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using (SqliteCommand validate = connection.CreateCommand())
                {
                    validate.Transaction = transaction;
                    validate.CommandText = "SELECT status FROM duplicate_cleanup_plans WHERE id=$id;";
                    validate.Parameters.AddWithValue("$id", planId);
                    object? value = validate.ExecuteScalar();
                    if (value == null) throw new KeyNotFoundException($"Cleanup plan {planId} does not exist.");
                    if ((DuplicateCleanupStatus)Convert.ToInt32(value) != DuplicateCleanupStatus.Draft)
                        throw new InvalidOperationException("Cleanup items can only be appended to a draft plan.");
                }

                using SqliteCommand item = connection.CreateCommand();
                item.Transaction = transaction;
                item.CommandText =
                    """
                    INSERT INTO duplicate_cleanup_plan_items(plan_id,group_id,file_id,keeper_file_id,source_path,source_path_key,
                        source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,full_hash,status,destination_path,validation_error)
                    VALUES($plan,$group,$file,$keeper,$path,$path_key,$size,$modified,$volume,$identity,$hash,$status,'','');
                    """;
                item.Parameters.Add("$plan", SqliteType.Integer);
                item.Parameters.Add("$group", SqliteType.Integer);
                item.Parameters.Add("$file", SqliteType.Integer);
                item.Parameters.Add("$keeper", SqliteType.Integer);
                item.Parameters.Add("$path", SqliteType.Text);
                item.Parameters.Add("$path_key", SqliteType.Text);
                item.Parameters.Add("$size", SqliteType.Integer);
                item.Parameters.Add("$modified", SqliteType.Integer);
                item.Parameters.Add("$volume", SqliteType.Text);
                item.Parameters.Add("$identity", SqliteType.Text);
                item.Parameters.Add("$hash", SqliteType.Blob);
                item.Parameters.Add("$status", SqliteType.Integer);
                foreach (DuplicateCleanupPlanItemRecord source in items)
                {
                    item.Parameters["$plan"].Value = planId;
                    item.Parameters["$group"].Value = source.GroupId;
                    item.Parameters["$file"].Value = source.FileId;
                    item.Parameters["$keeper"].Value = source.KeeperFileId;
                    item.Parameters["$path"].Value = source.SourcePath;
                    item.Parameters["$path_key"].Value = source.SourcePathKey;
                    item.Parameters["$size"].Value = source.SourceSizeBytes;
                    item.Parameters["$modified"].Value = source.SourceLastWriteUtc.Ticks;
                    item.Parameters["$volume"].Value = source.SourceVolumeId ?? "";
                    item.Parameters["$identity"].Value = source.SourceFileIdentity ?? "";
                    item.Parameters["$hash"].Value = source.FullHash;
                    item.Parameters["$status"].Value = (int)DuplicateCleanupItemStatus.Planned;
                    item.ExecuteNonQuery();
                }
                return null;
            });
        }

        public int AppendEligibleCleanupGroups(long planId, IReadOnlyCollection<long> groupIds)
        {
            ArgumentNullException.ThrowIfNull(groupIds);
            long[] ids = groupIds.Distinct().ToArray();
            if (ids.Length == 0) return 0;
            if (ids.Length > 500) throw new ArgumentOutOfRangeException(nameof(groupIds), "Cleanup planning batches are limited to 500 groups.");
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                using (SqliteCommand validate = connection.CreateCommand())
                {
                    validate.Transaction = transaction;
                    validate.CommandText = "SELECT status FROM duplicate_cleanup_plans WHERE id=$id;";
                    validate.Parameters.AddWithValue("$id", planId);
                    object? value = validate.ExecuteScalar();
                    if (value == null) throw new KeyNotFoundException($"Cleanup plan {planId} does not exist.");
                    if ((DuplicateCleanupStatus)Convert.ToInt32(value) != DuplicateCleanupStatus.Draft)
                        throw new InvalidOperationException("Cleanup groups can only be appended to a draft plan.");
                }
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                string groupParameters = string.Join(",", ids.Select((_, index) => "$group" + index));
                command.CommandText =
                    """
                    WITH selected_groups AS (
                        SELECT g.id,g.full_algorithm,g.full_version,g.full_hash,COALESCE(d.ignored,0) ignored,
                               COALESCE(
                                   (SELECT keeper.id FROM indexed_files keeper
                                    JOIN exact_duplicate_members km ON km.file_id=keeper.id AND km.group_id=g.id
                                    WHERE keeper.path_key=d.manual_keeper_path_key LIMIT 1),
                                   g.suggested_keeper_file_id) keeper_id
                        FROM exact_duplicate_groups g
                        LEFT JOIN duplicate_group_decisions d ON d.size_bytes=g.size_bytes AND d.full_algorithm=g.full_algorithm
                            AND d.full_version=g.full_version AND d.full_hash=g.full_hash
                        WHERE g.id IN (
                    """ + groupParameters +
                    """
                        )
                    )
                    INSERT OR IGNORE INTO duplicate_cleanup_plan_items(
                        plan_id,group_id,file_id,keeper_file_id,source_path,source_path_key,source_size_bytes,
                        source_last_write_utc_ticks,source_volume_id,source_file_identity,full_hash,status,destination_path,validation_error)
                    SELECT $plan,g.id,f.id,g.keeper_id,f.full_path,f.path_key,f.size_bytes,f.last_write_utc_ticks,
                           f.volume_id,f.file_identity,h.full_hash,$planned,'',''
                    FROM selected_groups g
                    JOIN exact_duplicate_members m ON m.group_id=g.id
                    JOIN indexed_files f ON f.id=m.file_id
                    JOIN file_hash_facts h ON h.file_id=f.id
                    JOIN indexed_files keeper ON keeper.id=g.keeper_id
                    JOIN file_hash_facts keeper_hash ON keeper_hash.file_id=keeper.id
                    WHERE g.ignored=0 AND g.keeper_id IS NOT NULL AND f.id<>g.keeper_id
                      AND f.availability_state=$present AND keeper.availability_state=$present
                      AND m.is_hard_link_alias=0
                      AND NOT EXISTS(SELECT 1 FROM duplicate_file_protections p WHERE p.path_key=f.path_key)
                      AND h.full_algorithm=g.full_algorithm AND h.full_version=g.full_version AND h.full_hash=g.full_hash
                      AND h.source_size_bytes=f.size_bytes AND h.source_last_write_utc_ticks=f.last_write_utc_ticks
                      AND keeper_hash.full_algorithm=g.full_algorithm AND keeper_hash.full_version=g.full_version AND keeper_hash.full_hash=g.full_hash
                      AND keeper_hash.source_size_bytes=keeper.size_bytes AND keeper_hash.source_last_write_utc_ticks=keeper.last_write_utc_ticks
                      AND NOT EXISTS(SELECT 1 FROM library_presence_observations o WHERE o.file_id IN(f.id,keeper.id) AND o.state<>$presence_present)
                      AND EXISTS(SELECT 1 FROM file_location_memberships fm JOIN library_locations l ON l.id=fm.location_id
                                 WHERE fm.file_id=f.id AND fm.availability_state=$present AND l.availability_state<$location_unavailable)
                      AND EXISTS(SELECT 1 FROM file_location_memberships km JOIN library_locations kl ON kl.id=km.location_id
                                 WHERE km.file_id=keeper.id AND km.availability_state=$present AND kl.availability_state<$location_unavailable)
                      AND NOT EXISTS(SELECT 1 FROM duplicate_cleanup_plan_items existing WHERE existing.plan_id=$plan AND existing.file_id=f.id)
                    ORDER BY g.id,f.id LIMIT 500;
                    """;
                command.Parameters.AddWithValue("$plan", planId);
                command.Parameters.AddWithValue("$planned", (int)DuplicateCleanupItemStatus.Planned);
                command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                command.Parameters.AddWithValue("$presence_present", (int)LibraryPresenceObservationState.Present);
                command.Parameters.AddWithValue("$location_unavailable", (int)LibraryLocationAvailability.Unavailable);
                for (int index = 0; index < ids.Length; index++) command.Parameters.AddWithValue("$group" + index, ids[index]);
                return command.ExecuteNonQuery();
            });
        }

        public void MarkCleanupPlanReady(long planId) => TransitionCleanupPlan(planId, DuplicateCleanupStatus.Draft, DuplicateCleanupStatus.Ready, requireItems: true);

        public void MarkCleanupPlanRunning(long planId) => TransitionCleanupPlan(planId, DuplicateCleanupStatus.Ready, DuplicateCleanupStatus.Running, requireItems: true);

        private void TransitionCleanupPlan(long planId, DuplicateCleanupStatus from, DuplicateCleanupStatus to, bool requireItems)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE duplicate_cleanup_plans SET status=$to,completed_utc_ticks=NULL,error_text='' WHERE id=$id AND status=$from" +
                    (requireItems ? " AND EXISTS(SELECT 1 FROM duplicate_cleanup_plan_items WHERE plan_id=$id)" : "") + ";";
                command.Parameters.AddWithValue("$to", (int)to);
                command.Parameters.AddWithValue("$from", (int)from);
                command.Parameters.AddWithValue("$id", planId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Cleanup plan {planId} could not transition from {from} to {to}.");
                return null;
            });
        }

        public int RecoverInterruptedCleanupPlans()
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                int recovered = 0;
                using (SqliteCommand drafts = connection.CreateCommand())
                {
                    drafts.Transaction = transaction;
                    drafts.CommandText = "UPDATE duplicate_cleanup_plans SET status=$failed,completed_utc_ticks=$now,error_text='Interrupted while the cleanup plan was being prepared; the draft is not executable.' WHERE status=$draft;";
                    drafts.Parameters.AddWithValue("$failed", (int)DuplicateCleanupStatus.Failed);
                    drafts.Parameters.AddWithValue("$draft", (int)DuplicateCleanupStatus.Draft);
                    drafts.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    recovered += drafts.ExecuteNonQuery();
                }
                using (SqliteCommand running = connection.CreateCommand())
                {
                    running.Transaction = transaction;
                    running.CommandText = "UPDATE duplicate_cleanup_plans SET status=$ready,completed_utc_ticks=NULL,error_text='Recovered after interruption. Completed items remain final; remaining items require revalidation.' WHERE status=$running;";
                    running.Parameters.AddWithValue("$ready", (int)DuplicateCleanupStatus.Ready);
                    running.Parameters.AddWithValue("$running", (int)DuplicateCleanupStatus.Running);
                    recovered += running.ExecuteNonQuery();
                }
                return recovered;
            });
        }

        public DuplicateCleanupPlanSummary? GetCleanupPlanSummary(long planId, bool includeLocations = true)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT p.action,p.status,p.quarantine_root,p.created_utc_ticks,p.completed_utc_ticks,p.error_text,
                       COUNT(i.file_id),COUNT(DISTINCT i.group_id),
                       COALESCE(SUM(CASE WHEN i.status=$planned THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN i.status=$succeeded THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN i.status=$excluded THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN i.status=$failed THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(i.source_size_bytes),0),
                       COALESCE(SUM(CASE WHEN i.status=$succeeded THEN i.source_size_bytes ELSE 0 END),0)
                FROM duplicate_cleanup_plans p LEFT JOIN duplicate_cleanup_plan_items i ON i.plan_id=p.id
                WHERE p.id=$id GROUP BY p.id;
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
                Action = (DuplicateCleanupAction)reader.GetInt32(0), Status = (DuplicateCleanupStatus)reader.GetInt32(1), Root = reader.GetString(2),
                Created = FromUtcTicks(reader.GetInt64(3)), Completed = reader.IsDBNull(4) ? (DateTime?)null : FromUtcTicks(reader.GetInt64(4)), Error = reader.GetString(5),
                Total = reader.GetInt64(6), Groups = reader.GetInt64(7), Planned = reader.GetInt64(8), Succeeded = reader.GetInt64(9),
                Excluded = reader.GetInt64(10), Failed = reader.GetInt64(11), Bytes = reader.GetInt64(12), Reclaimed = reader.GetInt64(13)
            };
            reader.Close();
            if (!includeLocations)
                return new DuplicateCleanupPlanSummary(planId, header.Action, header.Status, header.Root, header.Created, header.Completed, header.Error,
                    header.Total, header.Groups, header.Planned, header.Succeeded, header.Excluded, header.Failed, header.Bytes, header.Reclaimed, Array.Empty<ExactDuplicateReclaimLocation>());
            using SqliteCommand locations = connection.CreateCommand();
            locations.CommandText =
                """
                WITH item_locations AS (
                    SELECT i.file_id,i.source_size_bytes,
                           (SELECT MIN(m.location_id) FROM file_location_memberships m WHERE m.file_id=i.file_id) location_id
                    FROM duplicate_cleanup_plan_items i WHERE i.plan_id=$id
                )
                SELECT l.id,l.path,COUNT(*),COALESCE(SUM(x.source_size_bytes),0)
                FROM item_locations x JOIN library_locations l ON l.id=x.location_id
                GROUP BY l.id,l.path ORDER BY 4 DESC,l.path;
                """;
            locations.Parameters.AddWithValue("$id", planId);
            using SqliteDataReader locationReader = locations.ExecuteReader();
            var buckets = new List<ExactDuplicateReclaimLocation>();
            while (locationReader.Read()) buckets.Add(new ExactDuplicateReclaimLocation(locationReader.GetInt64(0), locationReader.GetString(1), locationReader.GetInt64(2), locationReader.GetInt64(3)));
            return new DuplicateCleanupPlanSummary(planId, header.Action, header.Status, header.Root, header.Created, header.Completed, header.Error,
                header.Total, header.Groups, header.Planned, header.Succeeded, header.Excluded, header.Failed, header.Bytes, header.Reclaimed, buckets);
        }

        public IReadOnlyList<DuplicateCleanupPlanItemRecord> GetCleanupPlanItemsBatch(
            long planId, long afterGroupId, long afterFileId, int limit, DuplicateCleanupItemStatus? status = null)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 500);
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand items = connection.CreateCommand();
            items.CommandText = "SELECT plan_id,group_id,file_id,keeper_file_id,source_path,source_path_key,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,full_hash,status,destination_path,validation_error FROM duplicate_cleanup_plan_items WHERE plan_id=$id AND (group_id>$group OR (group_id=$group AND file_id>$file)) AND ($status IS NULL OR status=$status) ORDER BY group_id,file_id LIMIT $limit;";
            items.Parameters.AddWithValue("$id", planId);
            items.Parameters.AddWithValue("$group", afterGroupId);
            items.Parameters.AddWithValue("$file", afterFileId);
            items.Parameters.AddWithValue("$status", status.HasValue ? (int)status.Value : DBNull.Value);
            items.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = items.ExecuteReader();
            var result = new List<DuplicateCleanupPlanItemRecord>(limit);
            while (reader.Read()) result.Add(ReadCleanupPlanItem(reader));
            return result;
        }

        private static DuplicateCleanupPlanItemRecord ReadCleanupPlanItem(SqliteDataReader reader) => new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
            FromUtcTicks(reader.GetInt64(7)), reader.GetString(8), reader.GetString(9), (byte[])reader[10],
            (DuplicateCleanupItemStatus)reader.GetInt32(11), reader.GetString(12), reader.GetString(13));

        public DuplicateCleanupPlanRecord? GetCleanupPlan(long planId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand plan = connection.CreateCommand();
            plan.CommandText = "SELECT action,status,quarantine_root,created_utc_ticks,completed_utc_ticks,error_text FROM duplicate_cleanup_plans WHERE id=$id;";
            plan.Parameters.AddWithValue("$id", planId);
            using SqliteDataReader planReader = plan.ExecuteReader();
            if (!planReader.Read()) return null;
            var action = (DuplicateCleanupAction)planReader.GetInt32(0);
            var status = (DuplicateCleanupStatus)planReader.GetInt32(1);
            string root = planReader.GetString(2);
            DateTime created = FromUtcTicks(planReader.GetInt64(3));
            DateTime? completed = planReader.IsDBNull(4) ? null : FromUtcTicks(planReader.GetInt64(4));
            string error = planReader.GetString(5);
            planReader.Close();

            using SqliteCommand items = connection.CreateCommand();
            items.CommandText = "SELECT plan_id,group_id,file_id,keeper_file_id,source_path,source_path_key,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,full_hash,status,destination_path,validation_error FROM duplicate_cleanup_plan_items WHERE plan_id=$id ORDER BY group_id,file_id;";
            items.Parameters.AddWithValue("$id", planId);
            using SqliteDataReader reader = items.ExecuteReader();
            var result = new List<DuplicateCleanupPlanItemRecord>();
            while (reader.Read()) result.Add(ReadCleanupPlanItem(reader));
            return new DuplicateCleanupPlanRecord(planId, action, status, root, created, completed, error, result);
        }

        public void UpdateCleanupPlanItem(long planId, long fileId, DuplicateCleanupItemStatus status, string destinationPath, string validationError)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE duplicate_cleanup_plan_items SET status=$status,destination_path=$destination,validation_error=$error WHERE plan_id=$plan AND file_id=$file;";
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$destination", destinationPath ?? "");
                command.Parameters.AddWithValue("$error", validationError ?? "");
                command.Parameters.AddWithValue("$plan", planId);
                command.Parameters.AddWithValue("$file", fileId);
                command.ExecuteNonQuery();
                return null;
            });
        }

        public void RecordCleanupPlanItemOutcome(
            long planId, long fileId, string sourcePath, string destinationPath, DuplicateCleanupAction action,
            DuplicateCleanupItemStatus status, string validationError, string auditMessage)
        {
            if (status is DuplicateCleanupItemStatus.Planned or DuplicateCleanupItemStatus.Validated)
                throw new ArgumentException("A persisted cleanup outcome must be succeeded, excluded, or failed.", nameof(status));
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using (SqliteCommand update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE duplicate_cleanup_plan_items SET status=$status,destination_path=$destination,validation_error=$error WHERE plan_id=$plan AND file_id=$file AND status IN($planned,$validated);";
                    update.Parameters.AddWithValue("$status", (int)status);
                    update.Parameters.AddWithValue("$destination", destinationPath ?? "");
                    update.Parameters.AddWithValue("$error", validationError ?? "");
                    update.Parameters.AddWithValue("$plan", planId);
                    update.Parameters.AddWithValue("$file", fileId);
                    update.Parameters.AddWithValue("$planned", (int)DuplicateCleanupItemStatus.Planned);
                    update.Parameters.AddWithValue("$validated", (int)DuplicateCleanupItemStatus.Validated);
                    if (update.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException($"Cleanup item {fileId} was already finalized or no longer exists.");
                }
                using SqliteCommand audit = connection.CreateCommand();
                audit.Transaction = transaction;
                audit.CommandText = "INSERT INTO duplicate_cleanup_audit(plan_id,file_id,source_path,destination_path,action,outcome,message,occurred_utc_ticks) VALUES($plan,$file,$source,$destination,$action,$outcome,$message,$now);";
                audit.Parameters.AddWithValue("$plan", planId);
                audit.Parameters.AddWithValue("$file", fileId);
                audit.Parameters.AddWithValue("$source", sourcePath ?? "");
                audit.Parameters.AddWithValue("$destination", destinationPath ?? "");
                audit.Parameters.AddWithValue("$action", (int)action);
                audit.Parameters.AddWithValue("$outcome", (int)status);
                audit.Parameters.AddWithValue("$message", auditMessage ?? "");
                audit.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                audit.ExecuteNonQuery();
                return null;
            });
        }

        public void CompleteCleanupPlan(long planId, DuplicateCleanupStatus status, string errorText = "")
        {
            if (status is DuplicateCleanupStatus.Draft or DuplicateCleanupStatus.Ready or DuplicateCleanupStatus.Running)
                throw new ArgumentException("The final cleanup status must be Completed or Failed.", nameof(status));
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE duplicate_cleanup_plans SET status=$status,completed_utc_ticks=$now,error_text=$error WHERE id=$id;";
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.Parameters.AddWithValue("$error", errorText ?? "");
                command.Parameters.AddWithValue("$id", planId);
                command.ExecuteNonQuery();
                return null;
            });
        }

        public void AppendCleanupAudit(long planId, long fileId, string sourcePath, string destinationPath, DuplicateCleanupAction action, DuplicateCleanupItemStatus outcome, string message)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO duplicate_cleanup_audit(plan_id,file_id,source_path,destination_path,action,outcome,message,occurred_utc_ticks) VALUES($plan,$file,$source,$destination,$action,$outcome,$message,$now);";
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
}
