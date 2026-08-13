using System.IO.Compression;
using Xunit;

namespace MediaFlux.Tests;

public sealed class Phase9PersistenceCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MediaFlux-Phase9Persistence",
        Guid.NewGuid().ToString("N"));

    public Phase9PersistenceCoverageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplicationBackupIncludesCrossPhaseUserData()
    {
        string userData = Path.Combine(_root, "UserData");
        string data = Path.Combine(userData, "data");
        string backups = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(userData, "config.json"), "{}");
        string[] crossPhaseFiles =
        {
            "library-policies.json",
            "storage-reclamation-plan.json",
            "library-catalog.db",
            "encoding-statistics.jsonl",
            "history.jsonl"
        };
        foreach (string file in crossPhaseFiles)
            File.WriteAllText(Path.Combine(data, file), file);

        string archive = BackupManager.CreateBackup(userData, backups, 3);

        using ZipArchive zip = ZipFile.OpenRead(archive);
        HashSet<string> entries = zip.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("config.json", entries);
        Assert.All(crossPhaseFiles, file =>
            Assert.Contains($"data/{file}", entries));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
