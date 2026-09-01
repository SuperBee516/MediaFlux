using System.IO.Compression;
using Xunit;

namespace MediaFlux.Tests;

public sealed class UpdaterBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxUpdaterBackup", Guid.NewGuid().ToString("N"));
    public UpdaterBackupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PersistentManifestExcludesAndCleansOwnedRuntimeArtifacts()
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

        Assert.False(Directory.Exists(Path.Combine(data, "ai-intermediates")));
        Assert.False(Directory.Exists(Path.Combine(data, "restoration-previews")));
        Assert.False(Directory.Exists(Path.Combine(data, "frame-previews")));
        Assert.False(Directory.Exists(Path.Combine(data, "staging")));
        Assert.False(Directory.Exists(Path.Combine(userData, "temp")));
        Assert.False(File.Exists(Path.Combine(data, "leftover.tmp")));
        Assert.False(File.Exists(Path.Combine(data, "leftover.partial.mkv")));
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
        Assert.Contains("Cleaning temporary AI files...", progress);
        Assert.Contains(progress, message => message.StartsWith("✓ Deleted", StringComparison.Ordinal));
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
        Assert.Contains("No temporary MediaFlux data found.", progress);
        Assert.Contains("Backup complete.", progress);
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

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
