using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MediaFlux.Services.LibraryCatalog;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class LibraryCatalogFoundationTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _output;

    public LibraryCatalogFoundationTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-LibraryCatalogTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void FreshDatabaseCreatesCurrentSchemaAndConfiguredRuntime()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();

        LibraryCatalogDiagnostics diagnostics = catalog.Initialize();

        Assert.True(File.Exists(catalog.DatabasePath));
        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, diagnostics.SchemaVersion);
        Assert.Equal(LibraryCatalogDatabase.ApplicationId, diagnostics.ApplicationId);
        Assert.Equal("wal", diagnostics.JournalMode, ignoreCase: true);
        Assert.Equal(1, diagnostics.SynchronousMode); // SQLite NORMAL
        Assert.True(diagnostics.ForeignKeysEnabled);
        Assert.True(IsWalResetSafeVersion(Version.Parse(diagnostics.SqliteVersion)),
            $"Bundled SQLite {diagnostics.SqliteVersion} does not contain the required WAL-reset correction.");
        Assert.True(diagnostics.PageSize > 0);
        Assert.Equal(new LibraryCatalogCounts(0, 0, 0, 0), catalog.GetCounts());
    }

    [Fact]
    public void ExistingDatabaseReopensWithoutRecreatingData()
    {
        string databasePath = GetDatabasePath();
        long locationId;
        using (var first = CreateCatalog(databasePath))
        {
            first.Initialize();
            locationId = first.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library"))).Id;
        }

        using var reopened = CreateCatalog(databasePath);
        LibraryCatalogDiagnostics diagnostics = reopened.Initialize();
        LibraryLocationRecord? location = reopened.GetLocation(locationId);

        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, diagnostics.SchemaVersion);
        Assert.NotNull(location);
        Assert.Equal(1, reopened.GetCounts().Locations);
    }

    [Fact]
    public void NewerSchemaVersionIsDetectedAndRejectedWithoutModification()
    {
        string databasePath = GetDatabasePath();
        using (SqliteLibraryCatalog current = CreateCatalog(databasePath))
            current.Initialize();
        SqliteConnection.ClearAllPools();
        ExecuteRaw(databasePath, "PRAGMA user_version=999;");
        SqliteConnection.ClearAllPools();

        using SqliteLibraryCatalog catalog = CreateCatalog(databasePath);
        LibraryCatalogInitializationResult result = catalog.TryInitialize();

        Assert.False(result.Success);
        Assert.Contains("newer", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("999", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionOneDatabaseMigratesInOrderWithBackupAndPreservesRawFacts()
    {
        string databasePath = GetDatabasePath();
        var oldDatabase = new LibraryCatalogDatabase(
            databasePath,
            Path.Combine(_root, "backups"),
            Path.Combine(_root, "recovery"));
        LibraryCatalogDiagnostics oldDiagnostics = oldDatabase.InitializeForTesting(1);
        Assert.Equal(1, oldDiagnostics.SchemaVersion);
        InsertVersionOneFixture(databasePath);
        SqliteConnection.ClearAllPools();

        using SqliteLibraryCatalog catalog = CreateCatalog(databasePath);
        LibraryCatalogInitializationResult result = catalog.TryInitialize();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, result.Diagnostics?.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.MigrationBackupPath));
        Assert.True(File.Exists(result.MigrationBackupPath));
        Assert.Equal(new LibraryCatalogCounts(1, 1, 0, 0), catalog.GetCounts());
        Assert.NotNull(catalog.GetFileByPath(Path.Combine(_root, "legacy", "movie.mkv")));
    }

    [Fact]
    public void InventoryBatchCommitsAtomicallyAndRollsBackOnConstraintFailure()
    {
        using SqliteLibraryCatalog catalog = CreateInitializedCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(
            new LibraryLocationUpsert(Path.Combine(_root, "library")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        LibraryInventoryEntry committed = Entry("library", "committed.mkv", "committed.mkv", 100);

        Assert.Equal(1, catalog.UpsertInventoryBatch(scan, new[] { committed }));
        Assert.Equal(new LibraryCatalogCounts(1, 1, 1, 1), catalog.GetCounts());

        LibraryInventoryEntry collisionOne = Entry("library", "one.mkv", "collision.mkv", 200);
        LibraryInventoryEntry collisionTwo = Entry("library", "two.mkv", "collision.mkv", 300);
        Assert.Throws<SqliteException>(() =>
            catalog.UpsertInventoryBatch(scan, new[] { collisionOne, collisionTwo }));

        Assert.Equal(new LibraryCatalogCounts(1, 1, 1, 1), catalog.GetCounts());
        Assert.Null(catalog.GetFileByPath(collisionOne.FullPath));
        Assert.Null(catalog.GetFileByPath(collisionTwo.FullPath));
    }

    [Fact]
    public void FileIdentityIsUniqueByPathButCanBelongToOverlappingRoots()
    {
        using SqliteLibraryCatalog catalog = CreateInitializedCatalog();
        string parentPath = Path.Combine(_root, "media");
        string nestedPath = Path.Combine(parentPath, "movies");
        LibraryLocationRecord parent = catalog.UpsertLocation(new LibraryLocationUpsert(parentPath));
        LibraryLocationRecord nested = catalog.UpsertLocation(new LibraryLocationUpsert(nestedPath));
        LibraryScanHandle parentScan = catalog.BeginScan(parent.Id);
        LibraryScanHandle nestedScan = catalog.BeginScan(nested.Id);
        string fullPath = Path.Combine(nestedPath, "Film.mkv");

        catalog.UpsertInventoryBatch(parentScan, new[]
        {
            new LibraryInventoryEntry(
                fullPath,
                Path.Combine("movies", "Film.mkv"),
                1_000,
                DateTime.UtcNow,
                VolumeId: "volume-a",
                FileIdentity: "file-42")
        });
        catalog.UpsertInventoryBatch(nestedScan, new[]
        {
            new LibraryInventoryEntry(
                fullPath.ToUpperInvariant(),
                "Film.mkv",
                1_000,
                DateTime.UtcNow,
                VolumeId: "volume-a",
                FileIdentity: "file-42")
        });

        LibraryCatalogCounts counts = catalog.GetCounts();
        IndexedFileRecord file = Assert.IsType<IndexedFileRecord>(catalog.GetFileByPath(fullPath));
        Assert.Equal(2, counts.Locations);
        Assert.Equal(1, counts.Files);
        Assert.Equal(2, counts.Memberships);
        Assert.Equal(2, catalog.GetMembershipsForFile(file.Id).Count);
        Assert.Single(catalog.GetFilesByIdentity("volume-a", "file-42"));
    }

    [Fact]
    public void LocationAndScanIdentityConstraintsUseStableNormalizedKeysAndGenerations()
    {
        using SqliteLibraryCatalog catalog = CreateInitializedCatalog();
        string locationPath = Path.Combine(_root, "library");
        LibraryLocationRecord first = catalog.UpsertLocation(new LibraryLocationUpsert(locationPath));
        LibraryLocationRecord samePathDifferentCase = catalog.UpsertLocation(
            new LibraryLocationUpsert(locationPath.ToUpperInvariant(), IncludeSubfolders: false));

        Assert.Equal(first.Id, samePathDifferentCase.Id);
        Assert.False(samePathDifferentCase.IncludeSubfolders);
        Assert.Equal(1, catalog.GetCounts().Locations);

        LibraryScanHandle generationOne = catalog.BeginScan(first.Id);
        catalog.CompleteScan(
            generationOne,
            new LibraryScanCompletion(LibraryScanStatus.Completed, 0, 0, 0, 0, 0, 0));
        LibraryScanHandle generationTwo = catalog.BeginScan(first.Id);

        Assert.Equal(1, generationOne.Generation);
        Assert.Equal(2, generationTwo.Generation);
        Assert.Throws<InvalidOperationException>(() =>
            catalog.UpsertInventoryBatch(
                generationOne,
                new[] { Entry("library", "stale.mkv", "stale.mkv", 10) }));
    }

    [Fact]
    public void NewScanGenerationSupersedesAnInterruptedRunningScan()
    {
        using SqliteLibraryCatalog catalog = CreateInitializedCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(
            new LibraryLocationUpsert(Path.Combine(_root, "library")));
        LibraryScanHandle interrupted = catalog.BeginScan(location.Id);
        LibraryScanHandle replacement = catalog.BeginScan(location.Id);

        Assert.Equal(interrupted.Generation + 1, replacement.Generation);
        Assert.Throws<InvalidOperationException>(() =>
            catalog.UpsertInventoryBatch(
                interrupted,
                new[] { Entry("library", "stale.mkv", "stale.mkv", 10) }));
        Assert.Throws<InvalidOperationException>(() =>
            catalog.CompleteScan(
                interrupted,
                new LibraryScanCompletion(LibraryScanStatus.Completed, 0, 0, 0, 0, 0, 0)));

        Assert.Equal(1, catalog.UpsertInventoryBatch(
            replacement,
            new[] { Entry("library", "current.mkv", "current.mkv", 20) }));
        catalog.CompleteScan(
            replacement,
            new LibraryScanCompletion(LibraryScanStatus.Completed, 1, 0, 1, 0, 0, 0));
    }

    [Fact]
    public void IntegrityBackupCheckpointAndRebuildAreCatalogOnlyOperations()
    {
        using SqliteLibraryCatalog catalog = CreateInitializedCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(
            new LibraryLocationUpsert(Path.Combine(_root, "library")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        catalog.UpsertInventoryBatch(scan, new[] { Entry("library", "movie.mkv", "movie.mkv", 123) });

        Assert.True(catalog.CheckIntegrity().IsHealthy);
        Assert.True(catalog.CheckIntegrity(fullCheck: true).IsHealthy);
        string backup = catalog.CreateBackup();
        Assert.True(File.Exists(backup));
        LibraryCatalogCheckpointResult checkpoint = catalog.Checkpoint(LibraryCatalogCheckpointMode.Truncate);
        Assert.False(checkpoint.Busy);

        using (SqliteLibraryCatalog backupCatalog = CreateCatalog(
                   backup,
                   Path.Combine(_root, "backup-of-backup"),
                   Path.Combine(_root, "backup-recovery")))
        {
            Assert.Equal(LibraryCatalogMigrations.CurrentVersion, backupCatalog.Initialize().SchemaVersion);
            Assert.Equal(1, backupCatalog.GetCounts().Files);
        }

        string recoveryArchive = catalog.RebuildCatalog();
        Assert.True(Directory.Exists(recoveryArchive));
        Assert.Contains(Directory.EnumerateFiles(recoveryArchive), file =>
            string.Equals(Path.GetFileName(file), "library-catalog.db", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new LibraryCatalogCounts(0, 0, 0, 0), catalog.GetCounts());
    }

    [Fact]
    public void InvalidDatabaseFailsGracefullyWithoutTouchingMediaPaths()
    {
        string databasePath = GetDatabasePath();
        string mediaMarker = Path.Combine(_root, "important-video.mkv");
        File.WriteAllBytes(mediaMarker, new byte[] { 1, 2, 3 });
        File.WriteAllText(databasePath, "this is not sqlite");
        using SqliteLibraryCatalog catalog = CreateCatalog(databasePath);

        LibraryCatalogInitializationResult result = catalog.TryInitialize();

        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
        Assert.True(File.Exists(mediaMarker));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(mediaMarker));
    }

    [Fact]
    public async Task ConcurrentInventoryWritersAreSerializedWithoutBusyFailures()
    {
        using SqliteLibraryCatalog catalog = CreateInitializedCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(
            new LibraryLocationUpsert(Path.Combine(_root, "concurrent-library")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);

        Task[] writers = Enumerable.Range(0, 4)
            .Select(worker => Task.Run(() =>
            {
                var batch = Enumerable.Range(0, 250)
                    .Select(index =>
                    {
                        string name = $"worker-{worker}-video-{index:D4}.mkv";
                        return Entry("concurrent-library", name, name, 1_000 + index);
                    })
                    .ToArray();
                Assert.Equal(batch.Length, catalog.UpsertInventoryBatch(scan, batch));
            }))
            .ToArray();

        await Task.WhenAll(writers);

        Assert.Equal(1_000, catalog.GetCounts().Files);
        Assert.Equal(1_000, catalog.GetCounts().Memberships);
    }

    [Fact]
    public void LargeBatchedWorkloadUsesBoundedBatchesAndIndexedQueries()
    {
        const int fileCount = 25_000;
        const int batchSize = 1_000;
        using SqliteLibraryCatalog catalog = CreateInitializedCatalog();
        LibraryLocationRecord location = catalog.UpsertLocation(
            new LibraryLocationUpsert(Path.Combine(_root, "large-library")));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();

        for (int offset = 0; offset < fileCount; offset += batchSize)
        {
            var batch = new List<LibraryInventoryEntry>(batchSize);
            for (int index = offset; index < Math.Min(offset + batchSize, fileCount); index++)
            {
                string relative = Path.Combine($"folder-{index / 1000:D3}", $"video-{index:D7}.mkv");
                batch.Add(new LibraryInventoryEntry(
                    Path.Combine(_root, "large-library", relative),
                    relative,
                    4_000_000_000L + index,
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index),
                    VolumeId: "synthetic-volume",
                    FileIdentity: $"identity-{index:D7}",
                    SeenUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
            }

            Assert.Equal(batch.Count, catalog.UpsertInventoryBatch(scan, batch));
        }

        stopwatch.Stop();
        catalog.Checkpoint(LibraryCatalogCheckpointMode.Truncate);
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        LibraryCatalogDiagnostics diagnostics = catalog.GetDiagnostics();
        var queryStopwatch = Stopwatch.StartNew();
        IndexedFileRecord? last = catalog.GetFileByPath(
            Path.Combine(_root, "large-library", "folder-024", "video-0024999.mkv"));
        IReadOnlyList<IndexedFileRecord> page = catalog.GetLocationFilesPage(location.Id, 20_000, 500);
        IReadOnlyList<IndexedFileRecord> identity = catalog.GetFilesByIdentity(
            "synthetic-volume",
            "identity-0024999");
        queryStopwatch.Stop();

        _output.WriteLine(
            "Inserted {0:N0} files in {1:N2}s; indexed lookups {2:N3}s; database {3:N0} bytes; retained managed-memory delta {4:N0} bytes; SQLite {5}.",
            fileCount,
            stopwatch.Elapsed.TotalSeconds,
            queryStopwatch.Elapsed.TotalSeconds,
            diagnostics.DatabaseBytes,
            memoryAfter - memoryBefore,
            diagnostics.SqliteVersion);

        Assert.Equal(fileCount, catalog.GetCounts().Files);
        Assert.NotNull(last);
        Assert.Equal(500, page.Count);
        Assert.Single(identity);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Synthetic catalog insert took {stopwatch.Elapsed}.");
        Assert.True(queryStopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Indexed synthetic catalog lookups took {queryStopwatch.Elapsed}.");
        Assert.True(diagnostics.DatabaseBytes < 64L * 1024 * 1024,
            $"Synthetic catalog unexpectedly grew to {diagnostics.DatabaseBytes:N0} bytes.");
        Assert.True(memoryAfter - memoryBefore < 64L * 1024 * 1024,
            $"Synthetic catalog retained an unexpected managed-memory delta of {memoryAfter - memoryBefore:N0} bytes.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SqliteLibraryCatalog CreateCatalog(string? databasePath = null)
    {
        return CreateCatalog(
            databasePath ?? GetDatabasePath(),
            Path.Combine(_root, "backups"),
            Path.Combine(_root, "recovery"));
    }

    private static SqliteLibraryCatalog CreateCatalog(
        string databasePath,
        string backupDirectory,
        string recoveryDirectory)
    {
        return new SqliteLibraryCatalog(databasePath, backupDirectory, recoveryDirectory);
    }

    private SqliteLibraryCatalog CreateInitializedCatalog()
    {
        SqliteLibraryCatalog catalog = CreateCatalog();
        catalog.Initialize();
        return catalog;
    }

    private string GetDatabasePath() => Path.Combine(_root, "library-catalog.db");

    private LibraryInventoryEntry Entry(
        string folder,
        string name,
        string relativePath,
        long size)
    {
        return new LibraryInventoryEntry(
            Path.Combine(_root, folder, name),
            relativePath,
            size,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SeenUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private void InsertVersionOneFixture(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        long now = DateTime.UtcNow.Ticks;
        string locationPath = Path.Combine(_root, "legacy");
        string filePath = Path.Combine(locationPath, "movie.mkv");
        command.CommandText =
            """
            INSERT INTO library_locations (
                path, path_key, include_subfolders, is_enabled, availability_state,
                last_error, current_generation, created_utc_ticks, updated_utc_ticks)
            VALUES ($location_path, $location_key, 1, 1, 0, '', 0, $now, $now);

            INSERT INTO indexed_files (
                full_path, path_key, file_name, extension, size_bytes,
                creation_utc_ticks, last_write_utc_ticks, volume_id, file_identity,
                availability_state, last_seen_utc_ticks, created_utc_ticks, updated_utc_ticks)
            VALUES ($file_path, $file_key, 'movie.mkv', '.mkv', 1234,
                    NULL, $now, '', '', 0, $now, $now, $now);
            """;
        command.Parameters.AddWithValue("$location_path", locationPath);
        command.Parameters.AddWithValue("$location_key", locationPath.ToUpperInvariant());
        command.Parameters.AddWithValue("$file_path", filePath);
        command.Parameters.AddWithValue("$file_key", filePath.ToUpperInvariant());
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void ExecuteRaw(string databasePath, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool IsWalResetSafeVersion(Version version)
    {
        return version >= new Version(3, 51, 3) ||
               version >= new Version(3, 50, 7) && version < new Version(3, 51, 0) ||
               version >= new Version(3, 44, 6) && version < new Version(3, 45, 0);
    }
}
