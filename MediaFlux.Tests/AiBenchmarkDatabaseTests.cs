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

    private static AiBenchmarkDatabaseKey Key() => new("ncnn-vulkan", "ncnn-1.0", "realesrgan-x4plus", "NVIDIA RTX", "555.1", "FP32", 4, "1080p");
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
