using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiBenchmarkDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxAiBenchmarkDatabaseTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StoresAllRuntimeIdentityAndReturnsFastestStableMatchingResult()
    {
        var database = new AiBenchmarkDatabase(Path.Combine(_root, "ai-benchmarks.db"));
        AiBenchmarkDatabaseKey key = Key();
        database.Store(new(key, new(NcnnThreadConfiguration.OneTwoTwo, 256), 12.5, 2_000_000_000, true, DateTimeOffset.UtcNow.AddMinutes(-1), "validated"));
        database.Store(new(key, new(NcnnThreadConfiguration.TwoTwoTwo, 512), 18.75, 2_200_000_000, true, DateTimeOffset.UtcNow, "validated"));
        database.Store(new(key, new(NcnnThreadConfiguration.FourFourFour, 1024), 100, 3_000_000_000, false, DateTimeOffset.UtcNow, "Vulkan failure"));

        Assert.True(database.TryGetFastestStable(key, out AiBenchmarkDatabaseEntry result));
        Assert.Equal("2:2:2", result.Configuration.Threads!.ToString());
        Assert.Equal(512, result.Configuration.TileSize);
        Assert.Equal(18.75, result.FramesPerSecond);
        Assert.Equal(2_200_000_000, result.PeakVramBytes);
        Assert.True(result.IsStable);
    }

    [Fact]
    public void DriverBackendAndModelChangesAutomaticallyInvalidatePreviousResults()
    {
        var database = new AiBenchmarkDatabase(Path.Combine(_root, "ai-benchmarks.db"));
        AiBenchmarkDatabaseKey key = Key();
        database.Store(new(key, new(NcnnThreadConfiguration.TwoTwoTwo, 512), 18.75, null, true, DateTimeOffset.UtcNow, "validated"));

        Assert.False(database.TryGetFastestStable(key with { DriverVersion = "556.2" }, out _));
        Assert.False(database.TryGetFastestStable(key with { BackendIdentity = "ncnn-2.0" }, out _));
        Assert.False(database.TryGetFastestStable(key with { Model = "realesr-animevideov3-x4" }, out _));
    }

    [Fact]
    public async Task ManagementListsDeletesAndRoundTripsVersionedExports()
    {
        string databasePath = Path.Combine(_root, "ai-benchmarks.db");
        var database = new AiBenchmarkDatabase(databasePath);
        AiBenchmarkDatabaseKey key = Key();
        database.Store(new(key, new(NcnnThreadConfiguration.OneTwoTwo, 256), 12.5, null, true, DateTimeOffset.UtcNow.AddMinutes(-2), "validated"));
        database.Store(new(key with { Model = "failed-model" }, NcnnRuntimeConfiguration.SafeDefault, 0, null, false, DateTimeOffset.UtcNow, "failed"));
        var manager = new AiBenchmarkManagementService(database);

        IReadOnlyList<AiBenchmarkRecord> records = manager.List();
        Assert.Equal(2, records.Count);
        string export = Path.Combine(_root, "benchmarks.mfai-benchmarks.json");
        await manager.ExportAsync(export, records.Where(record => record.Entry.IsStable));

        var importedDatabase = new AiBenchmarkDatabase(Path.Combine(_root, "imported.db"));
        AiBenchmarkImportResult imported = await new AiBenchmarkManagementService(importedDatabase).ImportAsync(export);
        Assert.Equal(1, imported.Imported);
        Assert.Single(importedDatabase.List());
        Assert.Equal(1, manager.DeleteObsolete());
        Assert.Single(manager.List());
        Assert.Equal(1, manager.DeleteSelected(manager.List()));
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task ManagementRejectsUnsupportedOrInvalidImports()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "unsupported.json");
        await File.WriteAllTextAsync(path, "{\"Version\":99,\"Entries\":[]}");

        AiBenchmarkImportResult result = await new AiBenchmarkManagementService(new AiBenchmarkDatabase(Path.Combine(_root, "import.db"))).ImportAsync(path);

        Assert.Equal(0, result.Imported);
        Assert.Contains("Unsupported", result.Message);
    }

    private static AiBenchmarkDatabaseKey Key() => new("ncnn-vulkan", "ncnn-1.0", "realesrgan-x4plus", "NVIDIA RTX", "555.1", "FP32", 4, "1080p");
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
