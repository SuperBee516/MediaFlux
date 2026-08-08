using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog : ILibraryCatalog, ILibraryAnalysisCatalog,
        ILibraryVisualCatalog, ILibraryScanAccelerationCatalog
    {
        private const int MaximumPageSize = 10_000;
        private readonly LibraryCatalogDatabase _database;
        private readonly SemaphoreSlim _writerGate = new(1, 1);
        private bool _disposed;

        public SqliteLibraryCatalog(
            string databasePath,
            string? backupDirectory = null,
            string? recoveryDirectory = null)
        {
            _database = new LibraryCatalogDatabase(databasePath, backupDirectory, recoveryDirectory);
        }

        public static SqliteLibraryCatalog CreateDefault() => new(
            AppPaths.LibraryCatalogFile,
            AppPaths.LibraryCatalogBackupDirectory,
            AppPaths.LibraryCatalogRecoveryDirectory);

        public string DatabasePath => _database.DatabasePath;

        public LibraryCatalogInitializationResult TryInitialize()
        {
            try
            {
                LibraryCatalogDiagnostics diagnostics = Initialize();
                return new LibraryCatalogInitializationResult(
                    true,
                    diagnostics,
                    _database.LastMigrationBackupPath,
                    "");
            }
            catch (Exception ex)
            {
                return new LibraryCatalogInitializationResult(
                    false,
                    null,
                    _database.LastMigrationBackupPath,
                    ex.Message,
                    ex);
            }
        }

        public LibraryCatalogDiagnostics Initialize()
        {
            ThrowIfDisposed();
            return _database.Initialize();
        }

        public LibraryCatalogDiagnostics GetDiagnostics()
        {
            ThrowIfDisposed();
            return _database.GetDiagnostics();
        }

        public LibraryCatalogIntegrityResult CheckIntegrity(bool fullCheck = false)
        {
            ThrowIfDisposed();
            return _database.CheckIntegrity(fullCheck);
        }

        public LibraryCatalogCheckpointResult Checkpoint(
            LibraryCatalogCheckpointMode mode = LibraryCatalogCheckpointMode.Passive)
        {
            ThrowIfDisposed();
            return WithWriterGate(() => _database.Checkpoint(mode));
        }

        public string CreateBackup(string? destinationPath = null)
        {
            ThrowIfDisposed();
            return WithWriterGate(() => _database.CreateBackup(destinationPath));
        }

        public string RebuildCatalog()
        {
            ThrowIfDisposed();
            return WithWriterGate(() =>
            {
                try
                {
                    CreateUserDataBackupCore(destinationPath: null);
                }
                catch
                {
                    // A rebuild must remain available for a damaged catalog. The complete
                    // database/WAL set is still retained in the recovery archive below.
                }
                return _database.RebuildCatalog();
            });
        }

        public LibraryLocationRecord UpsertLocation(LibraryLocationUpsert location)
        {
            ArgumentNullException.ThrowIfNull(location);
            ThrowIfDisposed();
            (string path, string pathKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(location.Path);
            long now = DateTime.UtcNow.Ticks;

            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO library_locations (
                        path, path_key, include_subfolders, is_enabled, availability_state,
                        last_error, current_generation, created_utc_ticks, updated_utc_ticks)
                    VALUES ($path, $path_key, $include_subfolders, $is_enabled, $availability,
                            $last_error, 0, $now, $now)
                    ON CONFLICT(path_key) DO UPDATE SET
                        path = excluded.path,
                        include_subfolders = excluded.include_subfolders,
                        is_enabled = excluded.is_enabled,
                        availability_state = excluded.availability_state,
                        last_error = excluded.last_error,
                        updated_utc_ticks = excluded.updated_utc_ticks
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$path_key", pathKey);
                command.Parameters.AddWithValue("$include_subfolders", location.IncludeSubfolders ? 1 : 0);
                command.Parameters.AddWithValue("$is_enabled", location.IsEnabled ? 1 : 0);
                command.Parameters.AddWithValue("$availability", (int)location.Availability);
                command.Parameters.AddWithValue("$last_error", location.LastError ?? "");
                command.Parameters.AddWithValue("$now", now);
                long id = Convert.ToInt64(command.ExecuteScalar());
                return ReadLocation(connection, transaction, id)
                    ?? throw new InvalidOperationException("The catalog location was not returned after its upsert.");
            });
        }

        public LibraryLocationRecord? GetLocation(long locationId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            return ReadLocation(connection, transaction: null, locationId);
        }

        public LibraryScanHandle BeginScan(long locationId, DateTime? startedUtc = null)
        {
            ThrowIfDisposed();
            long startedTicks = ToUtcTicks(startedUtc ?? DateTime.UtcNow);

            return WithWriteTransaction((connection, transaction) =>
            {
                using (SqliteCommand supersedeCommand = connection.CreateCommand())
                {
                    supersedeCommand.Transaction = transaction;
                    supersedeCommand.CommandText =
                        """
                        UPDATE scan_runs
                        SET status = $canceled,
                            completed_utc_ticks = $started,
                            error_text = CASE
                                WHEN error_text = '' THEN 'Superseded by a newer scan generation.'
                                ELSE error_text
                            END
                        WHERE location_id = $location_id AND status = $running;
                        """;
                    supersedeCommand.Parameters.AddWithValue("$canceled", (int)LibraryScanStatus.Canceled);
                    supersedeCommand.Parameters.AddWithValue("$started", startedTicks);
                    supersedeCommand.Parameters.AddWithValue("$location_id", locationId);
                    supersedeCommand.Parameters.AddWithValue("$running", (int)LibraryScanStatus.Running);
                    supersedeCommand.ExecuteNonQuery();
                }

                using SqliteCommand generationCommand = connection.CreateCommand();
                generationCommand.Transaction = transaction;
                generationCommand.CommandText =
                    """
                    UPDATE library_locations
                    SET current_generation = current_generation + 1,
                        updated_utc_ticks = $started
                    WHERE id = $location_id
                    RETURNING current_generation;
                    """;
                generationCommand.Parameters.AddWithValue("$started", startedTicks);
                generationCommand.Parameters.AddWithValue("$location_id", locationId);
                object? generationValue = generationCommand.ExecuteScalar();
                if (generationValue == null)
                    throw new KeyNotFoundException($"Library location {locationId} does not exist.");
                long generation = Convert.ToInt64(generationValue);

                using SqliteCommand insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO scan_runs (location_id, generation, status, started_utc_ticks)
                    VALUES ($location_id, $generation, $status, $started)
                    RETURNING id;
                    """;
                insertCommand.Parameters.AddWithValue("$location_id", locationId);
                insertCommand.Parameters.AddWithValue("$generation", generation);
                insertCommand.Parameters.AddWithValue("$status", (int)LibraryScanStatus.Running);
                insertCommand.Parameters.AddWithValue("$started", startedTicks);
                long scanRunId = Convert.ToInt64(insertCommand.ExecuteScalar());
                return new LibraryScanHandle(scanRunId, locationId, generation);
            });
        }

        public void CompleteScan(LibraryScanHandle scan, LibraryScanCompletion completion)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(completion);
            if (completion.Status == LibraryScanStatus.Running)
                throw new ArgumentException("A completed scan cannot retain Running status.", nameof(completion));
            ValidateNonNegativeCounts(completion);
            ThrowIfDisposed();

            WithWriteTransaction<object?>((connection, transaction) =>
            {
                long completedTicks = ToUtcTicks(completion.CompletedUtc ?? DateTime.UtcNow);
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE scan_runs
                    SET status = $status,
                        completed_utc_ticks = $completed,
                        discovered_files = $discovered,
                        unchanged_files = $unchanged,
                        new_files = $new,
                        changed_files = $changed,
                        missing_files = $missing,
                        error_count = $errors,
                        error_text = $error_text
                    WHERE id = $scan_id AND location_id = $location_id
                      AND generation = $generation AND status = $running
                      AND EXISTS (
                          SELECT 1 FROM library_locations
                          WHERE id = $location_id AND current_generation = $generation
                      );
                    """;
                command.Parameters.AddWithValue("$status", (int)completion.Status);
                command.Parameters.AddWithValue("$completed", completedTicks);
                command.Parameters.AddWithValue("$discovered", completion.DiscoveredFiles);
                command.Parameters.AddWithValue("$unchanged", completion.UnchangedFiles);
                command.Parameters.AddWithValue("$new", completion.NewFiles);
                command.Parameters.AddWithValue("$changed", completion.ChangedFiles);
                command.Parameters.AddWithValue("$missing", completion.MissingFiles);
                command.Parameters.AddWithValue("$errors", completion.ErrorCount);
                command.Parameters.AddWithValue("$error_text", completion.ErrorText ?? "");
                command.Parameters.AddWithValue("$scan_id", scan.ScanRunId);
                command.Parameters.AddWithValue("$location_id", scan.LocationId);
                command.Parameters.AddWithValue("$generation", scan.Generation);
                command.Parameters.AddWithValue("$running", (int)LibraryScanStatus.Running);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The scan was not running or no longer matched its location generation.");

                if (completion.Status == LibraryScanStatus.Completed)
                {
                    using SqliteCommand locationCommand = connection.CreateCommand();
                    locationCommand.Transaction = transaction;
                    locationCommand.CommandText =
                        """
                        UPDATE library_locations
                        SET last_completed_scan_utc_ticks = $completed,
                            updated_utc_ticks = $completed
                        WHERE id = $location_id;
                        """;
                    locationCommand.Parameters.AddWithValue("$completed", completedTicks);
                    locationCommand.Parameters.AddWithValue("$location_id", scan.LocationId);
                    locationCommand.ExecuteNonQuery();
                }

                return null;
            });
        }

        public int UpsertInventoryBatch(
            LibraryScanHandle scan,
            IReadOnlyCollection<LibraryInventoryEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(entries);
            ThrowIfDisposed();
            if (entries.Count == 0)
                return 0;

            return WithWriteTransaction((connection, transaction) =>
            {
                EnsureRunningScan(connection, transaction, scan);
                using SqliteCommand fileCommand = CreateFileUpsertCommand(connection, transaction);
                using SqliteCommand membershipCommand = CreateMembershipUpsertCommand(connection, transaction);
                int written = 0;

                foreach (LibraryInventoryEntry entry in entries)
                {
                    ArgumentNullException.ThrowIfNull(entry);
                    if (entry.SizeBytes < 0)
                        throw new ArgumentOutOfRangeException(nameof(entries), "Catalog file sizes cannot be negative.");
                    (string fullPath, string pathKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(entry.FullPath);
                    (string relativePath, string relativePathKey) = LibraryCatalogPathNormalizer.NormalizeRelativePath(entry.RelativePath);
                    DateTime seenUtc = entry.SeenUtc ?? DateTime.UtcNow;
                    long seenTicks = ToUtcTicks(seenUtc);

                    SetFileUpsertParameters(fileCommand, entry, fullPath, pathKey, seenTicks);
                    long fileId = Convert.ToInt64(fileCommand.ExecuteScalar());

                    membershipCommand.Parameters["$location_id"].Value = scan.LocationId;
                    membershipCommand.Parameters["$file_id"].Value = fileId;
                    membershipCommand.Parameters["$relative_path"].Value = relativePath;
                    membershipCommand.Parameters["$relative_path_key"].Value = relativePathKey;
                    membershipCommand.Parameters["$generation"].Value = scan.Generation;
                    membershipCommand.Parameters["$availability"].Value = (int)entry.Availability;
                    membershipCommand.Parameters["$last_seen"].Value = seenTicks;
                    membershipCommand.ExecuteNonQuery();
                    written++;
                }

                return written;
            });
        }

        public IndexedFileRecord? GetFileByPath(string path)
        {
            ThrowIfDisposed();
            (_, string pathKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(path);
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = FileSelectSql + " WHERE path_key = $path_key;";
            command.Parameters.AddWithValue("$path_key", pathKey);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadFile(reader) : null;
        }

        public IReadOnlyList<IndexedFileRecord> GetFilesByIdentity(string volumeId, string fileIdentity)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(volumeId) || string.IsNullOrWhiteSpace(fileIdentity))
                return Array.Empty<IndexedFileRecord>();

            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = FileSelectSql +
                                  " WHERE volume_id = $volume_id AND file_identity = $file_identity ORDER BY id;";
            command.Parameters.AddWithValue("$volume_id", volumeId);
            command.Parameters.AddWithValue("$file_identity", fileIdentity);
            return ReadFiles(command);
        }

        public IReadOnlyList<IndexedFileRecord> GetLocationFilesPage(
            long locationId,
            long afterFileId,
            int limit)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, MaximumPageSize);
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                FileSelectSql +
                """
                 JOIN file_location_memberships membership ON membership.file_id = indexed_files.id
                 WHERE membership.location_id = $location_id AND indexed_files.id > $after_file_id
                 ORDER BY indexed_files.id
                 LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$location_id", locationId);
            command.Parameters.AddWithValue("$after_file_id", Math.Max(0, afterFileId));
            command.Parameters.AddWithValue("$limit", limit);
            return ReadFiles(command);
        }

        public IReadOnlyList<LibraryFileMembershipRecord> GetMembershipsForFile(long fileId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT location_id, file_id, relative_path, relative_path_key,
                       last_seen_generation, availability_state, last_seen_utc_ticks
                FROM file_location_memberships
                WHERE file_id = $file_id
                ORDER BY location_id;
                """;
            command.Parameters.AddWithValue("$file_id", fileId);
            using SqliteDataReader reader = command.ExecuteReader();
            var memberships = new List<LibraryFileMembershipRecord>();
            while (reader.Read())
            {
                memberships.Add(new LibraryFileMembershipRecord(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    (IndexedFileAvailability)reader.GetInt32(5),
                    FromUtcTicks(reader.GetInt64(6))));
            }
            return memberships;
        }

        public LibraryCatalogCounts GetCounts()
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            return new LibraryCatalogCounts(
                ReadTableCount(connection, "library_locations"),
                ReadTableCount(connection, "indexed_files"),
                ReadTableCount(connection, "file_location_memberships"),
                ReadTableCount(connection, "scan_runs"));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _writerGate.Dispose();
        }

        private T WithWriterGate<T>(Func<T> action)
        {
            _writerGate.Wait();
            try
            {
                return action();
            }
            finally
            {
                _writerGate.Release();
            }
        }

        private T WithWriteTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> action)
        {
            return WithWriterGate(() =>
            {
                using SqliteConnection connection = _database.OpenConnection(readOnly: false);
                using SqliteTransaction transaction = connection.BeginTransaction();
                try
                {
                    T result = action(connection, transaction);
                    transaction.Commit();
                    return result;
                }
                catch
                {
                    try
                    {
                        transaction.Rollback();
                    }
                    catch
                    {
                        // Preserve the original write failure. Disposing an active
                        // transaction remains a final best-effort rollback safeguard.
                    }
                    throw;
                }
            });
        }

        private static void EnsureRunningScan(
            SqliteConnection connection,
            SqliteTransaction transaction,
            LibraryScanHandle scan)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT COUNT(*) FROM scan_runs
                WHERE id = $scan_id AND location_id = $location_id
                  AND generation = $generation AND status = $running
                  AND EXISTS (
                      SELECT 1 FROM library_locations
                      WHERE id = $location_id AND current_generation = $generation
                  );
                """;
            command.Parameters.AddWithValue("$scan_id", scan.ScanRunId);
            command.Parameters.AddWithValue("$location_id", scan.LocationId);
            command.Parameters.AddWithValue("$generation", scan.Generation);
            command.Parameters.AddWithValue("$running", (int)LibraryScanStatus.Running);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Inventory can only be written to its active scan generation.");
        }

        private static SqliteCommand CreateFileUpsertCommand(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO indexed_files (
                    full_path, path_key, file_name, extension, size_bytes,
                    creation_utc_ticks, last_write_utc_ticks, volume_id, file_identity,
                    availability_state, last_seen_utc_ticks, created_utc_ticks, updated_utc_ticks)
                VALUES ($full_path, $path_key, $file_name, $extension, $size,
                        $creation, $last_write, $volume_id, $file_identity,
                        $availability, $last_seen, $last_seen, $last_seen)
                ON CONFLICT(path_key) DO UPDATE SET
                    full_path = excluded.full_path,
                    file_name = excluded.file_name,
                    extension = excluded.extension,
                    size_bytes = excluded.size_bytes,
                    creation_utc_ticks = excluded.creation_utc_ticks,
                    last_write_utc_ticks = excluded.last_write_utc_ticks,
                    volume_id = excluded.volume_id,
                    file_identity = excluded.file_identity,
                    availability_state = excluded.availability_state,
                    last_seen_utc_ticks = excluded.last_seen_utc_ticks,
                    updated_utc_ticks = excluded.updated_utc_ticks
                RETURNING id;
                """;
            command.Parameters.Add("$full_path", SqliteType.Text);
            command.Parameters.Add("$path_key", SqliteType.Text);
            command.Parameters.Add("$file_name", SqliteType.Text);
            command.Parameters.Add("$extension", SqliteType.Text);
            command.Parameters.Add("$size", SqliteType.Integer);
            command.Parameters.Add("$creation", SqliteType.Integer);
            command.Parameters.Add("$last_write", SqliteType.Integer);
            command.Parameters.Add("$volume_id", SqliteType.Text);
            command.Parameters.Add("$file_identity", SqliteType.Text);
            command.Parameters.Add("$availability", SqliteType.Integer);
            command.Parameters.Add("$last_seen", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static void SetFileUpsertParameters(
            SqliteCommand command,
            LibraryInventoryEntry entry,
            string fullPath,
            string pathKey,
            long seenTicks)
        {
            command.Parameters["$full_path"].Value = fullPath;
            command.Parameters["$path_key"].Value = pathKey;
            command.Parameters["$file_name"].Value = Path.GetFileName(fullPath);
            command.Parameters["$extension"].Value = Path.GetExtension(fullPath).ToLowerInvariant();
            command.Parameters["$size"].Value = entry.SizeBytes;
            command.Parameters["$creation"].Value = entry.CreationTimeUtc.HasValue
                ? ToUtcTicks(entry.CreationTimeUtc.Value)
                : DBNull.Value;
            command.Parameters["$last_write"].Value = ToUtcTicks(entry.LastWriteTimeUtc);
            command.Parameters["$volume_id"].Value = entry.VolumeId ?? "";
            command.Parameters["$file_identity"].Value = entry.FileIdentity ?? "";
            command.Parameters["$availability"].Value = (int)entry.Availability;
            command.Parameters["$last_seen"].Value = seenTicks;
        }

        private static SqliteCommand CreateMembershipUpsertCommand(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO file_location_memberships (
                    location_id, file_id, relative_path, relative_path_key,
                    last_seen_generation, availability_state, last_seen_utc_ticks)
                VALUES ($location_id, $file_id, $relative_path, $relative_path_key,
                        $generation, $availability, $last_seen)
                ON CONFLICT(location_id, file_id) DO UPDATE SET
                    relative_path = excluded.relative_path,
                    relative_path_key = excluded.relative_path_key,
                    last_seen_generation = excluded.last_seen_generation,
                    availability_state = excluded.availability_state,
                    last_seen_utc_ticks = excluded.last_seen_utc_ticks;
                """;
            command.Parameters.Add("$location_id", SqliteType.Integer);
            command.Parameters.Add("$file_id", SqliteType.Integer);
            command.Parameters.Add("$relative_path", SqliteType.Text);
            command.Parameters.Add("$relative_path_key", SqliteType.Text);
            command.Parameters.Add("$generation", SqliteType.Integer);
            command.Parameters.Add("$availability", SqliteType.Integer);
            command.Parameters.Add("$last_seen", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static LibraryLocationRecord? ReadLocation(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            long locationId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT id, path, path_key, include_subfolders, is_enabled,
                       availability_state, last_error, current_generation,
                       created_utc_ticks, updated_utc_ticks, last_completed_scan_utc_ticks
                FROM library_locations WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", locationId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new LibraryLocationRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(4) != 0,
                (LibraryLocationAvailability)reader.GetInt32(5),
                reader.GetString(6),
                reader.GetInt64(7),
                FromUtcTicks(reader.GetInt64(8)),
                FromUtcTicks(reader.GetInt64(9)),
                reader.IsDBNull(10) ? null : FromUtcTicks(reader.GetInt64(10)));
        }

        private const string FileSelectSql =
            """
            SELECT indexed_files.id, indexed_files.full_path, indexed_files.path_key,
                   indexed_files.file_name, indexed_files.extension, indexed_files.size_bytes,
                   indexed_files.creation_utc_ticks, indexed_files.last_write_utc_ticks,
                   indexed_files.volume_id, indexed_files.file_identity,
                   indexed_files.availability_state, indexed_files.last_seen_utc_ticks,
                   indexed_files.created_utc_ticks, indexed_files.updated_utc_ticks
            FROM indexed_files
            """;

        private static IReadOnlyList<IndexedFileRecord> ReadFiles(SqliteCommand command)
        {
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<IndexedFileRecord>();
            while (reader.Read())
                results.Add(ReadFile(reader));
            return results;
        }

        private static IndexedFileRecord ReadFile(SqliteDataReader reader)
        {
            return new IndexedFileRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : FromUtcTicks(reader.GetInt64(6)),
                FromUtcTicks(reader.GetInt64(7)),
                reader.GetString(8),
                reader.GetString(9),
                (IndexedFileAvailability)reader.GetInt32(10),
                FromUtcTicks(reader.GetInt64(11)),
                FromUtcTicks(reader.GetInt64(12)),
                FromUtcTicks(reader.GetInt64(13)));
        }

        private static long ReadTableCount(SqliteConnection connection, string tableName)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static void ValidateNonNegativeCounts(LibraryScanCompletion completion)
        {
            if (completion.DiscoveredFiles < 0 || completion.UnchangedFiles < 0 ||
                completion.NewFiles < 0 || completion.ChangedFiles < 0 ||
                completion.MissingFiles < 0 || completion.ErrorCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(completion), "Scan counters cannot be negative.");
            }
        }

        private static long ToUtcTicks(DateTime value) =>
            (value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime()).Ticks;

        private static DateTime FromUtcTicks(long value) =>
            new(value, DateTimeKind.Utc);

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
