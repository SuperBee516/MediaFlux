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
                CREATE TABLE userdata.visual_cleanup_plans AS SELECT * FROM main.visual_cleanup_plans;
                CREATE TABLE userdata.visual_cleanup_plan_items AS SELECT * FROM main.visual_cleanup_plan_items;
                CREATE TABLE userdata.visual_cleanup_audit AS SELECT * FROM main.visual_cleanup_audit;
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
                        visual = RestoreTable(connection, transaction,
                            """
                            INSERT INTO visual_group_decisions(group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks)
                            SELECT group_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks FROM restored.visual_group_decisions WHERE 1
                            ON CONFLICT(group_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks;
                            """);
                    }
                    transaction.Commit();
                    return new LibraryUserDataRestoreResult(
                        decisions,
                        protections,
                        visual,
                        new[] { "Cleanup plan and audit history is retained in the backup for audit purposes but is not re-executed or imported." });
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
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand plan = connection.CreateCommand();
                plan.Transaction = transaction;
                plan.CommandText = "INSERT INTO duplicate_cleanup_plans(action,status,quarantine_root,created_utc_ticks) VALUES($action,$status,$root,$now) RETURNING id;";
                plan.Parameters.AddWithValue("$action", (int)action);
                plan.Parameters.AddWithValue("$status", (int)DuplicateCleanupStatus.Ready);
                plan.Parameters.AddWithValue("$root", quarantineRoot ?? "");
                plan.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                long planId = Convert.ToInt64(plan.ExecuteScalar());

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
                return planId;
            });
        }

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
            while (reader.Read()) result.Add(new DuplicateCleanupPlanItemRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), FromUtcTicks(reader.GetInt64(7)), reader.GetString(8), reader.GetString(9), (byte[])reader[10], (DuplicateCleanupItemStatus)reader.GetInt32(11), reader.GetString(12), reader.GetString(13)));
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
