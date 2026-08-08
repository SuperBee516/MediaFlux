using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed class LibraryCatalogInitializationException : Exception
    {
        public LibraryCatalogInitializationException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    internal sealed class LibraryCatalogDatabase
    {
        public const int ApplicationId = 0x4D464C58; // MFLX
        private const long JournalSizeLimitBytes = 64L * 1024 * 1024;
        private const int BusyTimeoutMilliseconds = 10_000;
        private readonly object _initializationLock = new();
        private bool _initialized;

        public LibraryCatalogDatabase(
            string databasePath,
            string? backupDirectory = null,
            string? recoveryDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("A library catalog database path is required.", nameof(databasePath));

            DatabasePath = Path.GetFullPath(databasePath);
            string directory = Path.GetDirectoryName(DatabasePath)
                ?? throw new ArgumentException("The library catalog path has no parent directory.", nameof(databasePath));
            BackupDirectory = Path.GetFullPath(
                string.IsNullOrWhiteSpace(backupDirectory)
                    ? Path.Combine(directory, "catalog-backups")
                    : backupDirectory);
            RecoveryDirectory = Path.GetFullPath(
                string.IsNullOrWhiteSpace(recoveryDirectory)
                    ? Path.Combine(directory, "catalog-recovery")
                    : recoveryDirectory);
        }

        public string DatabasePath { get; }
        public string BackupDirectory { get; }
        public string RecoveryDirectory { get; }
        public string LastMigrationBackupPath { get; private set; } = "";

        public LibraryCatalogDiagnostics Initialize()
        {
            lock (_initializationLock)
            {
                if (_initialized)
                    return GetDiagnosticsCore();

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
                    Directory.CreateDirectory(BackupDirectory);
                    Directory.CreateDirectory(RecoveryDirectory);

                    using SqliteConnection connection = OpenConfiguredConnection(readOnly: false, enableWal: true);
                    int version = ReadIntPragma(connection, "user_version");
                    int applicationId = ReadIntPragma(connection, "application_id");
                    ValidateCatalogIdentity(connection, version, applicationId);

                    if (version > LibraryCatalogMigrations.CurrentVersion)
                    {
                        throw new LibraryCatalogInitializationException(
                            $"The library catalog schema version {version} is newer than this MediaFlux build supports ({LibraryCatalogMigrations.CurrentVersion}).");
                    }

                    if (version > 0)
                    {
                        LibraryCatalogIntegrityResult beforeMigration = CheckIntegrityCore(connection, fullCheck: false);
                        if (!beforeMigration.IsHealthy)
                        {
                            throw new LibraryCatalogInitializationException(
                                $"The existing library catalog failed its integrity check: {string.Join("; ", beforeMigration.Messages)}");
                        }
                    }

                    if (version > 0 && version < LibraryCatalogMigrations.CurrentVersion)
                        LastMigrationBackupPath = CreateBackupCore(connection, BuildBackupPath($"pre-v{version}-migration"));

                    LibraryCatalogMigrations.Apply(
                        connection,
                        version,
                        LibraryCatalogMigrations.CurrentVersion,
                        ApplicationId);

                    LibraryCatalogIntegrityResult afterMigration = CheckIntegrityCore(connection, fullCheck: false);
                    if (!afterMigration.IsHealthy)
                    {
                        throw new LibraryCatalogInitializationException(
                            $"The library catalog failed its post-migration integrity check: {string.Join("; ", afterMigration.Messages)}");
                    }

                    CheckpointCore(connection, LibraryCatalogCheckpointMode.Passive);
                    _initialized = true;
                    return ReadDiagnostics(connection);
                }
                catch (LibraryCatalogInitializationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new LibraryCatalogInitializationException(
                        $"MediaFlux could not initialize the library catalog at '{DatabasePath}'. No media files were changed.",
                        ex);
                }
            }
        }

        internal LibraryCatalogDiagnostics InitializeForTesting(int targetVersion)
        {
            lock (_initializationLock)
            {
                if (File.Exists(DatabasePath))
                    throw new InvalidOperationException("A test-version catalog can only be created at a new path.");
                if (targetVersion < 0 || targetVersion > LibraryCatalogMigrations.CurrentVersion)
                    throw new ArgumentOutOfRangeException(nameof(targetVersion));

                Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
                using SqliteConnection connection = OpenConfiguredConnection(readOnly: false, enableWal: true);
                LibraryCatalogMigrations.Apply(connection, 0, targetVersion, ApplicationId);
                _initialized = targetVersion == LibraryCatalogMigrations.CurrentVersion;
                return ReadDiagnostics(connection);
            }
        }

        public SqliteConnection OpenConnection(bool readOnly = false)
        {
            Initialize();
            return OpenConfiguredConnection(readOnly, enableWal: false);
        }

        public LibraryCatalogDiagnostics GetDiagnostics()
        {
            Initialize();
            return GetDiagnosticsCore();
        }

        public LibraryCatalogIntegrityResult CheckIntegrity(bool fullCheck)
        {
            Initialize();
            using SqliteConnection connection = OpenConfiguredConnection(readOnly: false, enableWal: false);
            return CheckIntegrityCore(connection, fullCheck);
        }

        public LibraryCatalogCheckpointResult Checkpoint(LibraryCatalogCheckpointMode mode)
        {
            Initialize();
            using SqliteConnection connection = OpenConfiguredConnection(readOnly: false, enableWal: false);
            return CheckpointCore(connection, mode);
        }

        public string CreateBackup(string? destinationPath = null)
        {
            Initialize();
            using SqliteConnection connection = OpenConfiguredConnection(readOnly: false, enableWal: false);
            string path = string.IsNullOrWhiteSpace(destinationPath)
                ? BuildBackupPath("manual")
                : Path.GetFullPath(destinationPath);
            return CreateBackupCore(connection, path);
        }

        public string RebuildCatalog()
        {
            lock (_initializationLock)
            {
                Directory.CreateDirectory(RecoveryDirectory);
                SqliteConnection.ClearAllPools();
                string archiveDirectory = Path.Combine(
                    RecoveryDirectory,
                    $"library-catalog-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
                Directory.CreateDirectory(archiveDirectory);

                foreach (string source in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
                {
                    if (!File.Exists(source))
                        continue;
                    File.Move(source, Path.Combine(archiveDirectory, Path.GetFileName(source)));
                }

                _initialized = false;
                LastMigrationBackupPath = "";
                Initialize();
                return archiveDirectory;
            }
        }

        private LibraryCatalogDiagnostics GetDiagnosticsCore()
        {
            using SqliteConnection connection = OpenConfiguredConnection(readOnly: false, enableWal: false);
            return ReadDiagnostics(connection);
        }

        private SqliteConnection OpenConfiguredConnection(bool readOnly, bool enableWal)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
                Pooling = true,
                DefaultTimeout = BusyTimeoutMilliseconds / 1000,
                ForeignKeys = true
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            try
            {
                ExecuteNonQuery(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds}; PRAGMA foreign_keys=ON;");
                if (!readOnly)
                {
                    if (enableWal)
                    {
                        string journalMode = ReadTextPragma(connection, "journal_mode", assignment: "WAL");
                        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                            throw new LibraryCatalogInitializationException($"SQLite did not enable WAL mode; active mode is '{journalMode}'.");
                    }

                    ExecuteNonQuery(
                        connection,
                        $"PRAGMA synchronous=NORMAL; PRAGMA wal_autocheckpoint=2000; PRAGMA journal_size_limit={JournalSizeLimitBytes.ToString(CultureInfo.InvariantCulture)};");
                }
                else
                {
                    ExecuteNonQuery(connection, "PRAGMA query_only=ON;");
                }

                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void ValidateCatalogIdentity(SqliteConnection connection, int version, int applicationId)
        {
            if (version == 0 && applicationId == 0)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                long tableCount = (long)(command.ExecuteScalar() ?? 0L);
                if (tableCount == 0)
                    return;
            }

            if (applicationId != ApplicationId)
            {
                throw new LibraryCatalogInitializationException(
                    $"The database at '{connection.DataSource}' is not a MediaFlux library catalog (application id {applicationId}).");
            }
        }

        private string CreateBackupCore(SqliteConnection source, string destinationPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (File.Exists(destinationPath))
                throw new IOException($"The catalog backup already exists: '{destinationPath}'.");

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            using var destination = new SqliteConnection(builder.ToString());
            destination.Open();
            source.BackupDatabase(destination);
            return destinationPath;
        }

        private string BuildBackupPath(string label)
        {
            string safeLabel = string.Concat(label.Select(character =>
                char.IsLetterOrDigit(character) || character == '-' ? character : '-'));
            return Path.Combine(
                BackupDirectory,
                $"library-catalog-{safeLabel}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.db");
        }

        private static LibraryCatalogIntegrityResult CheckIntegrityCore(SqliteConnection connection, bool fullCheck)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = fullCheck ? "PRAGMA integrity_check;" : "PRAGMA quick_check;";
            using SqliteDataReader reader = command.ExecuteReader();
            var messages = new List<string>();
            while (reader.Read())
                messages.Add(reader.GetString(0));

            bool healthy = messages.Count == 1 &&
                           string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase);
            return new LibraryCatalogIntegrityResult(healthy, messages);
        }

        private static LibraryCatalogCheckpointResult CheckpointCore(
            SqliteConnection connection,
            LibraryCatalogCheckpointMode mode)
        {
            string sqliteMode = mode.ToString().ToUpperInvariant();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA wal_checkpoint({sqliteMode});";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return new LibraryCatalogCheckpointResult(false, 0, 0);

            return new LibraryCatalogCheckpointResult(
                reader.GetInt32(0) != 0,
                reader.GetInt32(1),
                reader.GetInt32(2));
        }

        private static LibraryCatalogDiagnostics ReadDiagnostics(SqliteConnection connection)
        {
            return new LibraryCatalogDiagnostics(
                ReadIntPragma(connection, "user_version"),
                ReadIntPragma(connection, "application_id"),
                ReadTextPragma(connection, "journal_mode"),
                ReadIntPragma(connection, "synchronous"),
                ReadIntPragma(connection, "foreign_keys") != 0,
                ReadSqliteVersion(connection),
                ReadLongPragma(connection, "page_count"),
                ReadLongPragma(connection, "page_size"),
                ReadLongPragma(connection, "freelist_count"));
        }

        private static string ReadSqliteVersion(SqliteConnection connection)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            return command.ExecuteScalar()?.ToString() ?? "";
        }

        private static int ReadIntPragma(SqliteConnection connection, string name) =>
            checked((int)ReadLongPragma(connection, name));

        private static long ReadLongPragma(SqliteConnection connection, string name)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {name};";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static string ReadTextPragma(
            SqliteConnection connection,
            string name,
            string? assignment = null)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = assignment == null
                ? $"PRAGMA {name};"
                : $"PRAGMA {name}={assignment};";
            return command.ExecuteScalar()?.ToString() ?? "";
        }

        private static void ExecuteNonQuery(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
