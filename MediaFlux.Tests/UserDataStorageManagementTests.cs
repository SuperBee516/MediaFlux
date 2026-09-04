using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class UserDataStorageManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxStorage", Guid.NewGuid().ToString("N"));
    public UserDataStorageManagementTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task SnapshotIsAsyncCategorizedAndDoesNotDoubleCountKnownRoots()
    {
        Write("config.json", "config"); Write("data/encode-jobs.json", "jobs"); Write("data/ai-intermediates/ai-intermediate-failure/frame.bin", new string('x', 100));
        UserDataStorageSnapshot snapshot = await new UserDataStorageManagementService(_root).GetSnapshotAsync();
        Assert.Equal(110, snapshot.TotalBytes);
        Assert.Contains(snapshot.Items, item => item.Category == UserDataStorageCategory.AiFailureForensics && item.Bytes == 100);
        Assert.Contains(snapshot.Items, item => item.Category == UserDataStorageCategory.PersistentUserData && item.Bytes == 6);
    }

    [Fact]
    public void ExpiredCleanupRetainsRecentAndActiveFailureForensicsButBoundsOldOnes()
    {
        string old = DirectoryPath("data/ai-intermediates/ai-intermediate-old"); string active = DirectoryPath("data/ai-intermediates/ai-intermediate-active"); string recent = DirectoryPath("data/ai-intermediates/ai-intermediate-recent");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8)); Directory.SetLastWriteTimeUtc(active, DateTime.UtcNow.AddDays(-8)); AiProductionHardeningService.Register(active);
        try { UserDataCleanupResult result = new UserDataStorageManagementService(_root).CleanupExpiredGeneratedData(); Assert.True(result.DeletedDirectories >= 1); Assert.False(Directory.Exists(old)); Assert.True(Directory.Exists(active)); Assert.True(Directory.Exists(recent)); }
        finally { AiProductionHardeningService.Unregister(active); }
    }

    [Fact]
    public void CleanupPreservesPersistentStateAndPrunesExpiredGeneratedArtifacts()
    {
        Write("config.json", "config"); Write("data/encode-jobs.json", "jobs"); Write("data/restoration-profiles/Film.json", "profile");
        string preview = FilePath("data/restoration-previews/old.mp4", "preview"); File.SetLastWriteTimeUtc(preview, DateTime.UtcNow.AddDays(-31)); string temp = DirectoryPath("temp/old-operation"); Directory.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-8));
        new UserDataStorageManagementService(_root).CleanupExpiredGeneratedData();
        Assert.False(File.Exists(preview)); Assert.False(Directory.Exists(temp)); Assert.True(File.Exists(Path.Combine(_root, "config.json"))); Assert.True(File.Exists(Path.Combine(_root, "data", "encode-jobs.json"))); Assert.True(File.Exists(Path.Combine(_root, "data", "restoration-profiles", "Film.json")));
    }

    [Fact]
    public void CatalogSafetyArtifactsAreBoundedButRecentRecoveryIsRetained()
    {
        string old = FilePath("data/catalog-backups/old.db", "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31)); string recovery = DirectoryPath("data/catalog-recovery/recent");
        new UserDataStorageManagementService(_root).CleanupExpiredGeneratedData();
        Assert.False(File.Exists(old)); Assert.True(Directory.Exists(recovery));
    }

    [Fact]
    public async Task ExplicitCacheCleanupDoesNotTouchPersistentState()
    {
        Write("config.json", "config"); Write("data/encode-jobs.json", "jobs"); Write("data/tensorrt-engines/old.engine", "engine");
        UserDataCleanupResult result = await new UserDataStorageManagementService(_root).CleanupAsync(UserDataCleanupScope.RegenerableRuntimeCache);
        Assert.True(result.DeletedDirectories >= 1); Assert.False(Directory.Exists(Path.Combine(_root, "data", "tensorrt-engines"))); Assert.True(File.Exists(Path.Combine(_root, "config.json"))); Assert.True(File.Exists(Path.Combine(_root, "data", "encode-jobs.json")));
    }

    [Fact]
    public async Task CleanupGuardDoesNotTouchGeneratedDataDuringActiveWork()
    {
        string preview = FilePath("data/restoration-previews/old.mp4", "preview"); File.SetLastWriteTimeUtc(preview, DateTime.UtcNow.AddDays(-31));
        UserDataCleanupResult result = await new UserDataStorageManagementService(_root, () => true).CleanupAsync(UserDataCleanupScope.Previews);
        Assert.Equal(0, result.DeletedFiles); Assert.True(File.Exists(preview));
    }

    private void Write(string relative, string text) => File.WriteAllText(FilePath(relative, text), text);
    private string FilePath(string relative, string text) { string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); if (!File.Exists(path)) File.WriteAllText(path, text); return path; }
    private string DirectoryPath(string relative) { string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "artifact.bin"), "x"); return path; }
}
