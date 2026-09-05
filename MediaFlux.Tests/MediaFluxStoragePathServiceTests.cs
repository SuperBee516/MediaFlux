using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class MediaFluxStoragePathServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxStoragePaths", Guid.NewGuid().ToString("N"));
    public MediaFluxStoragePathServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void DefaultRootAndDerivedPathsPreserveExistingLayout()
    {
        var paths = Paths();
        Assert.Equal(Path.Combine(_root, "UserData"), paths.Root);
        Assert.Equal(Path.Combine(_root, "UserData", "data", "ai-intermediates"), paths.AiIntermediates);
        Assert.Equal(Path.Combine(_root, "UserData", "config.json"), paths.Config);
    }

    [Fact]
    public void ValidationRejectsCollisionAndRecursiveDestinations()
    {
        var paths = Paths(); paths.InitializeDirectories();
        Assert.False(paths.TryValidateNewRoot(paths.Root, out _, out _));
        Assert.False(paths.TryValidateNewRoot(Path.Combine(paths.Root, "nested"), out _, out _));
        string occupied = Path.Combine(_root, "occupied"); Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "x"), "x");
        Assert.False(paths.TryValidateNewRoot(occupied, out _, out _));
    }

    [Fact]
    public async Task MigrationCopiesVerifiesAndPublishesOnlyAfterSuccess()
    {
        var paths = Paths(); paths.InitializeDirectories(); File.WriteAllText(Path.Combine(paths.Data, "encode-jobs.json"), "jobs");
        string destination = Path.Combine(_root, "Moved");
        MediaFluxStorageMigrationResult result = await new MediaFluxStorageMigrationService(paths).MigrateAsync(destination);
        Assert.True(result.Succeeded, result.Message); Assert.Equal(destination, paths.Root); Assert.Equal("jobs", File.ReadAllText(Path.Combine(destination, "data", "encode-jobs.json"))); Assert.True(File.Exists(Path.Combine(_root, "UserData", "data", "encode-jobs.json")));
    }

    [Fact]
    public async Task MigrationCancellationAndActiveWorkKeepSourceAuthoritative()
    {
        var paths = Paths(); paths.InitializeDirectories(); File.WriteAllText(paths.Config, "{} "); string destination = Path.Combine(_root, "Moved");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        MediaFluxStorageMigrationResult cancelled = await new MediaFluxStorageMigrationService(paths).MigrateAsync(destination, cts.Token);
        Assert.True(cancelled.Cancelled); Assert.Equal(Path.Combine(_root, "UserData"), paths.Root); Assert.False(Directory.Exists(destination));
        MediaFluxStorageMigrationResult active = await new MediaFluxStorageMigrationService(paths, () => true).MigrateAsync(Path.Combine(_root, "Other"));
        Assert.False(active.Succeeded); Assert.Equal(Path.Combine(_root, "UserData"), paths.Root);
    }

    private MediaFluxStoragePathService Paths() => new(Path.Combine(_root, "UserData"), Path.Combine(_root, "pointer.json"));
}
