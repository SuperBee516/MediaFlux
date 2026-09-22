using MediaFlux.Services;
using MediaFlux.Models;
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
        string file = Path.Combine(_root, "not-a-folder"); File.WriteAllText(file, "x");
        Assert.False(paths.TryValidateNewRoot(file, out _, out _));
    }

    [Fact]
    public void CustomRootRemainsAuthoritativeAcrossRestartAndUpdateBoundary()
    {
        string defaultRoot = Path.Combine(_root, "Default", "UserData");
        string customRoot = Path.Combine(_root, "Custom", "UserData");
        string pointer = Path.Combine(_root, "MediaFlux", "storage-location.json");
        Directory.CreateDirectory(defaultRoot);
        Directory.CreateDirectory(customRoot);

        new Config { OutputSuffix = "_DEFAULT", PreventSleepDuringEncoding = false, BackupsToKeep = 1 }
            .Save(Path.Combine(defaultRoot, "config.json"));
        new Config { OutputSuffix = "_CUSTOM", PreventSleepDuringEncoding = true, BackupsToKeep = 7 }
            .Save(Path.Combine(customRoot, "config.json"));

        var beforeUpdate = new MediaFluxStoragePathService(defaultRoot, pointer);
        beforeUpdate.WriteConfiguredRoot(customRoot);
        AssertCustomConfig(beforeUpdate);

        // A new service instance models the first startup after Velopack replaces the app tree.
        var afterUpdate = new MediaFluxStoragePathService(defaultRoot, pointer);
        AssertCustomConfig(afterUpdate);
    }

    [Fact]
    public void MissingPointerRetainsBackwardCompatibleDefaultRoot()
    {
        string defaultRoot = Path.Combine(_root, "Default", "UserData");
        var paths = new MediaFluxStoragePathService(defaultRoot, Path.Combine(_root, "missing.json"));

        Assert.Equal(Path.GetFullPath(defaultRoot), paths.Root);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"Root\":\"\"}")]
    public void MalformedPointerFailsClosedInsteadOfFallingBack(string contents)
    {
        string defaultRoot = Path.Combine(_root, "Default", "UserData");
        string pointer = Path.Combine(_root, "storage-location.json");
        File.WriteAllText(pointer, contents);
        var paths = new MediaFluxStoragePathService(defaultRoot, pointer);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _ = paths.Root);

        Assert.Contains("was not replaced with the default root", exception.Message);
    }

    [Fact]
    public void ConfiguredRootRemainsAuthoritativeWhenDefaultAndCustomBothExist()
    {
        string defaultRoot = Path.Combine(_root, "Default", "UserData");
        string customRoot = Path.Combine(_root, "Custom", "UserData");
        string pointer = Path.Combine(_root, "storage-location.json");
        new Config { OutputSuffix = "_DEFAULT" }.Save(Path.Combine(defaultRoot, "config.json"));
        new Config { OutputSuffix = "_CUSTOM" }.Save(Path.Combine(customRoot, "config.json"));
        var paths = new MediaFluxStoragePathService(defaultRoot, pointer);
        paths.WriteConfiguredRoot(customRoot);

        Assert.Equal("_CUSTOM", Config.Load(paths.Config).OutputSuffix);
    }

    [Fact]
    public void ConfiguredUnavailableRootDoesNotFallbackToDefault()
    {
        string defaultRoot = Path.Combine(_root, "Default", "UserData");
        string unavailableRoot = Path.Combine(_root, "unavailable-root");
        string pointer = Path.Combine(_root, "storage-location.json");
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllText(unavailableRoot, "not a directory");
        var paths = new MediaFluxStoragePathService(defaultRoot, pointer);
        paths.WriteConfiguredRoot(unavailableRoot);

        Assert.Equal(Path.GetFullPath(unavailableRoot), paths.Root);
        Assert.Throws<IOException>(() => paths.InitializeDirectories());
        Assert.NotEqual(Path.GetFullPath(defaultRoot), paths.Root);
    }

    private static void AssertCustomConfig(MediaFluxStoragePathService paths)
    {
        Assert.EndsWith(Path.Combine("Custom", "UserData"), paths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(paths.Root + Path.DirectorySeparatorChar + "config.json", paths.Config, StringComparer.OrdinalIgnoreCase);
        Config config = Config.Load(paths.Config);
        Assert.Equal("_CUSTOM", config.OutputSuffix);
        Assert.True(config.PreventSleepDuringEncoding);
        Assert.Equal(7, config.BackupsToKeep);
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
    public async Task MigrationAcceptsAnExistingEmptyDestinationDirectory()
    {
        var paths = Paths(); paths.InitializeDirectories(); File.WriteAllText(Path.Combine(paths.Data, "encode-jobs.json"), "jobs");
        string destination = Path.Combine(_root, "PickerCreated"); Directory.CreateDirectory(destination);
        MediaFluxStorageMigrationResult result = await new MediaFluxStorageMigrationService(paths).MigrateAsync(destination);
        Assert.True(result.Succeeded, result.Message); Assert.Equal(destination, paths.Root); Assert.Equal("jobs", File.ReadAllText(Path.Combine(destination, "data", "encode-jobs.json")));
    }

    [Fact]
    public async Task MigrationRejectsAnExistingNonEmptyDestinationAndKeepsSourceAuthoritative()
    {
        var paths = Paths(); paths.InitializeDirectories(); File.WriteAllText(paths.Config, "{} ");
        string destination = Path.Combine(_root, "Occupied"); Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "keep.txt"), "unrelated");
        MediaFluxStorageMigrationResult result = await new MediaFluxStorageMigrationService(paths).MigrateAsync(destination);
        Assert.False(result.Succeeded); Assert.Equal(Path.Combine(_root, "UserData"), paths.Root); Assert.True(File.Exists(Path.Combine(destination, "keep.txt"))); Assert.True(File.Exists(paths.Config));
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
