using System.IO.Compression;
using Microsoft.Data.Sqlite;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class UpdaterBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxUpdaterBackup", Guid.NewGuid().ToString("N"));
    public UpdaterBackupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PersistentManifestExcludesRuntimeArtifactsWithoutDisruptingLiveWork()
    {
        string userData = Path.Combine(_root, "UserData"), data = Path.Combine(userData, "data"), backups = Path.Combine(_root, "Backups");
        Write(Path.Combine(userData, "config.json"), "config");
        Write(Path.Combine(data, "encode-presets.json"), "presets");
        Write(Path.Combine(data, "encode-jobs.json"), "jobs");
        Write(Path.Combine(data, "library-catalog.db"), "database");
        Write(Path.Combine(data, "restoration-profiles", "Cartoon.json"), "profile");
        Write(Path.Combine(data, "user-assets", "overlay.png"), "asset");
        Write(Path.Combine(data, "ai-intermediates", "ai-intermediate-one", "frame.png"), new string('a', 1024));
        Write(Path.Combine(data, "restoration-previews", "preview.mp4"), "preview");
        Write(Path.Combine(data, "frame-previews", "frame.png"), "preview");
        Write(Path.Combine(data, "staging", "partial.mkv"), "stage");
        Write(Path.Combine(userData, "temp", "operation", "work.bin"), "temp");
        Write(Path.Combine(data, "leftover.tmp"), "tmp");
        Write(Path.Combine(data, "leftover.partial.mkv"), "partial");
        var progress = new List<string>();

        string archive = BackupManager.CreateBackup(userData, backups, 3, progress.Add);

        Assert.True(Directory.Exists(Path.Combine(data, "ai-intermediates")));
        Assert.True(Directory.Exists(Path.Combine(data, "restoration-previews")));
        Assert.True(Directory.Exists(Path.Combine(data, "frame-previews")));
        Assert.True(Directory.Exists(Path.Combine(data, "staging")));
        Assert.True(Directory.Exists(Path.Combine(userData, "temp")));
        Assert.True(File.Exists(Path.Combine(data, "leftover.tmp")));
        Assert.True(File.Exists(Path.Combine(data, "leftover.partial.mkv")));
        Assert.True(File.Exists(Path.Combine(data, "restoration-profiles", "Cartoon.json")));

        using (ZipArchive zip = ZipFile.OpenRead(archive))
        {
            string[] entries = zip.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            Assert.Contains("config.json", entries);
            Assert.Contains("data/encode-presets.json", entries);
            Assert.Contains("data/encode-jobs.json", entries);
            Assert.Contains("data/library-catalog.db", entries);
            Assert.Contains("data/restoration-profiles/Cartoon.json", entries);
            Assert.Contains("data/user-assets/overlay.png", entries);
            Assert.DoesNotContain(entries, entry => entry.Contains("ai-intermediates", StringComparison.OrdinalIgnoreCase) || entry.Contains("restoration-previews", StringComparison.OrdinalIgnoreCase) || entry.Contains("frame-previews", StringComparison.OrdinalIgnoreCase) || entry.Contains("staging", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || entry.Contains(".partial", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains("Preparing backup...", progress);
        Assert.Contains("Excluding regenerable runtime data...", progress);
        Assert.Contains("Backing up persistent settings...", progress);
        Assert.Contains("Backup complete.", progress);
    }

    [Fact]
    public void PersistentConfigurationProfilesAndJobsRestoreFromTheNewManifest()
    {
        string userData = Path.Combine(_root, "UserData"), data = Path.Combine(userData, "data"), backups = Path.Combine(_root, "Backups"), restored = Path.Combine(_root, "Restored");
        Write(Path.Combine(userData, "config.json"), "{\"Setting\":true}");
        Write(Path.Combine(data, "encode-jobs.json"), "[\"job\"]");
        Write(Path.Combine(data, "restoration-profiles", "Film.json"), "{\"Version\":1}");

        string archive = BackupManager.CreateBackup(userData, backups, 3);
        BackupManager.ExtractUserDataValidated(archive, restored);

        Assert.Equal("{\"Setting\":true}", File.ReadAllText(Path.Combine(restored, "config.json")));
        Assert.Equal("[\"job\"]", File.ReadAllText(Path.Combine(restored, "data", "encode-jobs.json")));
        Assert.Equal("{\"Version\":1}", File.ReadAllText(Path.Combine(restored, "data", "restoration-profiles", "Film.json")));
    }

    [Fact]
    public void MissingOptionalPersistentFoldersAreNormalAndReportNoTemporaryData()
    {
        string userData = Path.Combine(_root, "UserData"), backups = Path.Combine(_root, "Backups");
        var progress = new List<string>();
        string archive = BackupManager.CreateBackup(userData, backups, 3, progress.Add);
        Assert.True(File.Exists(archive));
        Assert.Contains("Excluding regenerable runtime data...", progress);
        Assert.Contains("Backup complete.", progress);
    }

    [Fact]
    public void DefaultStorageBackupIsOutsideUserDataAndCanCreateBackup()
    {
        string userData = Path.Combine(_root, "UserData");
        var paths = new MediaFluxStoragePathService(userData, Path.Combine(_root, "storage-location.json"));

        string archive = BackupManager.CreateBackup(userData, paths.Backups, 3);

        Assert.False(MediaFluxStoragePathService.IsWithin(paths.Backups, userData));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void BackupInsideUserDataIsRejected()
    {
        string userData = Path.Combine(_root, "UserData");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            BackupManager.CreateBackup(userData, Path.Combine(userData, "Backups"), 3));

        Assert.Contains("outside the MediaFlux user-data folder", exception.Message);
    }

    [Fact]
    public void FormerPersistedDefaultResolvesToSiblingAndSettingsCanSaveCanonicalPath()
    {
        string userData = Path.Combine(_root, "UserData");
        string defaultBackups = Path.Combine(_root, "Backups");
        string legacyEncode = Path.Combine(_root, "Encode", "Backups");
        string formerDefault = Path.Combine(userData, "Backups");
        Directory.CreateDirectory(userData);

        string resolvedFormerDefault = BackupManager.ResolveBackupFolder(
            formerDefault, userData, defaultBackups, legacyEncode);
        string resolvedEncodeDefault = BackupManager.ResolveBackupFolder(
            legacyEncode, userData, defaultBackups, legacyEncode);
        string resolvedBlank = BackupManager.ResolveBackupFolder(
            " ", userData, defaultBackups, legacyEncode);
        string customExternal = Path.Combine(_root, "CustomerArchive", "Backups");
        string resolvedCustom = BackupManager.ResolveBackupFolder(
            customExternal, userData, defaultBackups, legacyEncode);
        string customInternal = Path.Combine(userData, "Custom", "Backups");

        Assert.Equal(Path.GetFullPath(defaultBackups), resolvedFormerDefault);
        Assert.Equal(Path.GetFullPath(defaultBackups), resolvedEncodeDefault);
        Assert.Equal(Path.GetFullPath(defaultBackups), resolvedBlank);
        Assert.Equal(Path.GetFullPath(customExternal), resolvedCustom);
        Assert.Equal(Path.GetFullPath(customInternal), BackupManager.ResolveBackupFolder(
            customInternal, userData, defaultBackups, legacyEncode));

        // SettingsForm displays ResolveBackupFolder(cfg.BackupFolderPath) and persists the
        // displayed textbox value on a legitimate Save, without a startup config write.
        var config = new Config { BackupFolderPath = formerDefault };
        config.BackupFolderPath = BackupManager.ResolveBackupFolder(
            config.BackupFolderPath, userData, defaultBackups, legacyEncode);
        Assert.Equal(Path.GetFullPath(defaultBackups), config.BackupFolderPath);

        string archive = BackupManager.CreateBackup(userData, resolvedFormerDefault, 3);
        Assert.StartsWith(Path.GetFullPath(defaultBackups) + Path.DirectorySeparatorChar, archive, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void CustomBackupWithinUserDataRemainsRejectedAfterResolution()
    {
        string userData = Path.Combine(_root, "UserData");
        string customInternal = Path.Combine(userData, "Custom", "Backups");
        string resolved = BackupManager.ResolveBackupFolder(
            customInternal,
            userData,
            Path.Combine(_root, "Backups"),
            Path.Combine(_root, "Encode", "Backups"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            BackupManager.CreateBackup(userData, resolved, 3));

        Assert.Contains("outside the MediaFlux user-data folder", exception.Message);
    }

    [Fact]
    public void LegacyEncodeBackupsMigrateWithoutOverwriteOrDeletion()
    {
        string legacy = Path.Combine(_root, "Encode", "Backups");
        string destination = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(legacy, "historical.zip"), "historical");
        File.WriteAllText(Path.Combine(destination, "existing.zip"), "current");
        File.WriteAllText(Path.Combine(legacy, "existing.zip"), "legacy");

        AppPaths.MigrateBackupArchives(legacy, destination);

        Assert.Equal("historical", File.ReadAllText(Path.Combine(destination, "historical.zip")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(destination, "existing.zip")));
        Assert.True(File.Exists(Path.Combine(legacy, "historical.zip")));
    }

    [Fact]
    public void LegacyBackupRestoreCompatibilityIsUnchanged()
    {
        string archive = Path.Combine(_root, "legacy.zip"), restored = Path.Combine(_root, "Restored");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var config = new StreamWriter(zip.CreateEntry("config.json").Open())) config.Write("{}");
            using (var jobs = new StreamWriter(zip.CreateEntry("data/encode-jobs.json").Open())) jobs.Write("[]");
        }
        BackupManager.ExtractUserDataValidated(archive, restored);
        Assert.True(File.Exists(Path.Combine(restored, "config.json")));
        Assert.True(File.Exists(Path.Combine(restored, "data", "encode-jobs.json")));
    }

    [Fact]
    public void LiveWalBackupsUseConsistentSqliteSnapshotsAndPreserveBenchmarkData()
    {
        string userData = Path.Combine(_root, "UserData");
        string data = Path.Combine(userData, "data");
        string benchmarks = Path.Combine(data, "ai-benchmarks.db");
        string catalog = Path.Combine(data, "library-catalog.db");
        string backups = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(data);

        var benchmarkDatabase = new AiBenchmarkDatabase(benchmarks);
        benchmarkDatabase.Store(new AiBenchmarkDatabaseEntry(
            new AiBenchmarkDatabaseKey("ncnn", "ncnn-1", "model", "gpu", "driver", "FP32", 2, "1080p"),
            NcnnRuntimeConfiguration.SafeDefault, 12.5, null, true, DateTimeOffset.UtcNow, "preserve me"));
        CreateSqliteDatabase(catalog, "catalog_entries", "library value");

        // Keep a pooled WAL reader alive to represent normal AI/runtime activity while the
        // updater's optional data backup is created.
        using var active = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = benchmarks,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true
        }.ToString());
        active.Open();
        using var command = active.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ai_benchmark_results;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
        Assert.True(File.Exists(benchmarks + "-wal"));

        string archive = BackupManager.CreateBackup(userData, backups, 3);
        string restored = Path.Combine(_root, "Restored");
        BackupManager.ExtractUserDataValidated(archive, restored);

        var restoredBenchmarks = new AiBenchmarkDatabase(Path.Combine(restored, "data", "ai-benchmarks.db"));
        Assert.Single(restoredBenchmarks.List());
        Assert.Equal("preserve me", restoredBenchmarks.List()[0].Entry.Summary);
        Assert.Equal("library value", ReadSqliteValue(Path.Combine(restored, "data", "library-catalog.db"), "catalog_entries"));

        using ZipArchive zip = ZipFile.OpenRead(archive);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase));
    }

    private static void CreateSqliteDatabase(string path, string table, string value)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode=WAL; CREATE TABLE {table}(value TEXT NOT NULL); INSERT INTO {table}(value) VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string ReadSqliteValue(string path, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {table};";
        return (string)command.ExecuteScalar()!;
    }

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
