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
        Assert.Equal(Path.Combine(_root, "Backups"), paths.Backups);
        Assert.False(MediaFluxStoragePathService.IsWithin(paths.Backups, paths.Root));
        Assert.Equal(Path.Combine(_root, "UserData", "data", "ai-intermediates"), paths.AiIntermediates);
        Assert.Equal(Path.Combine(_root, "UserData", "config.json"), paths.Config);
    }

    [Fact]
    public void InitializeDirectoriesDoesNotCreateBackupsInsideUserData()
    {
        var paths = Paths();

        paths.InitializeDirectories();

        Assert.True(Directory.Exists(paths.Root));
        Assert.False(Directory.Exists(Path.Combine(paths.Root, "Backups")));
        Assert.False(Directory.Exists(paths.Backups));
    }

    [Fact]
    public void LegacyInternalBackupsMigrateWithoutOverwriteOrDeletionAndAreIdempotent()
    {
        var paths = Paths();
        string legacy = Path.Combine(paths.Root, "Backups");
        string destination = paths.Backups;
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(legacy, "old.zip"), "old");
        File.WriteAllText(Path.Combine(legacy, "same.zip"), "legacy");
        File.WriteAllText(Path.Combine(destination, "same.zip"), "new");

        AppPaths.MigrateBackupArchives(legacy, destination);
        AppPaths.MigrateBackupArchives(legacy, destination);

        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "old.zip")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "same.zip")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacy, "same.zip")));
    }

    [Fact]
    public void StartupBackupInitializationMigratesLegacyLocationsCopyOnlyAndIdempotently()
    {
        string userData = Path.Combine(_root, "UserData");
        string legacyInstallBackups = Path.Combine(_root, "PreviousInstall", "Backups");
        string legacyEncodeBackups = Path.Combine(_root, "Encode", "Backups");
        string destination = Path.Combine(_root, "MediaFlux", "Backups");
        Directory.CreateDirectory(Path.Combine(userData, "Backups"));
        Directory.CreateDirectory(legacyInstallBackups);
        Directory.CreateDirectory(Path.Combine(legacyInstallBackups, "nested"));
        Directory.CreateDirectory(legacyEncodeBackups);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(userData, "Backups", "internal.zip"), "internal");
        File.WriteAllText(Path.Combine(userData, "Backups", "collision.zip"), "internal legacy");
        File.WriteAllText(Path.Combine(legacyInstallBackups, "nested", "install.txt"), "install");
        File.WriteAllText(Path.Combine(legacyEncodeBackups, "historical.zip"), "historical");
        File.WriteAllText(Path.Combine(legacyEncodeBackups, "collision.zip"), "Encode legacy");
        File.WriteAllText(Path.Combine(destination, "collision.zip"), "current");

        AppPaths.InitializeBackupLocations(
            userData, legacyInstallBackups, legacyEncodeBackups, destination,
            migrateHistoricalEncodeBackups: true);
        AppPaths.InitializeBackupLocations(
            userData, legacyInstallBackups, legacyEncodeBackups, destination,
            migrateHistoricalEncodeBackups: true);

        Assert.Equal("internal", File.ReadAllText(Path.Combine(destination, "internal.zip")));
        Assert.Equal("historical", File.ReadAllText(Path.Combine(destination, "historical.zip")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(destination, "collision.zip")));
        Assert.Equal("install", File.ReadAllText(Path.Combine(destination, "nested", "install.txt")));
        Assert.Equal("internal legacy", File.ReadAllText(Path.Combine(userData, "Backups", "collision.zip")));
        Assert.Equal("historical", File.ReadAllText(Path.Combine(legacyEncodeBackups, "historical.zip")));
        Assert.Equal("install", File.ReadAllText(Path.Combine(legacyInstallBackups, "nested", "install.txt")));
    }

    [Fact]
    public void StartupBackupInitializationCreatesNewDefaultWithoutLocalAppDataAndHonorsMigrationMarker()
    {
        string userData = Path.Combine(_root, "UserData");
        string destination = Path.Combine(_root, "Backups");
        string legacyEncodeBackups = Path.Combine(_root, "Encode", "Backups");
        Directory.CreateDirectory(legacyEncodeBackups);
        File.WriteAllText(Path.Combine(legacyEncodeBackups, "historical.zip"), "historical");

        AppPaths.InitializeBackupLocations(
            userData, destination, legacyEncodeBackups, destination,
            migrateHistoricalEncodeBackups: false);

        Assert.True(Directory.Exists(destination));
        Assert.False(Directory.Exists(Path.Combine(userData, "Backups")));
        Assert.False(File.Exists(Path.Combine(destination, "historical.zip")));

        AppPaths.InitializeBackupLocations(
            userData, destination, legacyEncodeBackups, destination,
            migrateHistoricalEncodeBackups: true);

        Assert.Equal("historical", File.ReadAllText(Path.Combine(destination, "historical.zip")));
    }

    [Fact]
    public void BackupMigrationSkipsSameAndNestedPathsButNotBoundarySimilarSiblings()
    {
        string source = Path.Combine(_root, "Foo", "Bar");
        string nestedDestination = Path.Combine(source, "Child");
        string nestedSource = Path.Combine(_root, "Tree", "Child");
        string ancestorDestination = Path.Combine(_root, "Tree");
        string sibling = Path.Combine(_root, "Foo", "Bar2");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(nestedSource);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(source, "same.zip"), "source");
        File.WriteAllText(Path.Combine(nestedSource, "nested.zip"), "nested");
        File.WriteAllText(Path.Combine(sibling, "sibling.zip"), "sibling");

        Assert.True(MediaFluxStoragePathService.PathsOverlap(source, source));
        Assert.True(MediaFluxStoragePathService.PathsOverlap(source, nestedDestination));
        Assert.True(MediaFluxStoragePathService.PathsOverlap(nestedSource, ancestorDestination));
        Assert.False(MediaFluxStoragePathService.PathsOverlap(source, sibling));

        AppPaths.MigrateBackupArchives(source, source);
        AppPaths.MigrateBackupArchives(source, nestedDestination);
        AppPaths.MigrateBackupArchives(nestedSource, ancestorDestination);
        AppPaths.CopyDirectoryIfMissing(source, nestedDestination);
        AppPaths.CopyDirectoryIfMissing(nestedSource, ancestorDestination);
        AppPaths.MigrateBackupArchives(source, sibling);

        Assert.False(Directory.Exists(nestedDestination));
        Assert.False(File.Exists(Path.Combine(ancestorDestination, "nested.zip")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(source, "same.zip")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(nestedSource, "nested.zip")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(sibling, "same.zip")));
    }

    [Fact]
    public void StartupMigrationDoesNotCreateDestinationInsideExistingLegacySource()
    {
        string legacySource = Path.Combine(_root, "Old", "Backups");
        string destination = Path.Combine(legacySource, "NewBackups");
        Directory.CreateDirectory(legacySource);
        File.WriteAllText(Path.Combine(legacySource, "keep.zip"), "keep");

        AppPaths.InitializeBackupLocations(
            Path.Combine(_root, "UserData"),
            legacySource,
            Path.Combine(_root, "Encode", "Backups"),
            destination,
            migrateHistoricalEncodeBackups: true);

        Assert.False(Directory.Exists(destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(legacySource, "keep.zip")));
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
