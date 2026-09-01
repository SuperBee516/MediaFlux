using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiProductionHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxHardening", Guid.NewGuid().ToString("N"));
    public AiProductionHardeningTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void EstimateScalesWithAiScaleAndFrames()
    {
        AiTemporaryStorageEstimate small = AiProductionHardeningService.Estimate(640, 480, 100, AiRestorationScale.X2, _root, AiChunkPlanner.MinimumFramesPerChunk);
        AiTemporaryStorageEstimate large = AiProductionHardeningService.Estimate(640, 480, 200, AiRestorationScale.X4, _root, AiChunkPlanner.MaximumFramesPerChunk);
        Assert.True(large.EstimatedBytes > small.EstimatedBytes);
    }
    [Fact]
    public void OrphanCleanupOnlyRemovesMediaFluxNamedDirectories()
    {
        string owned = Path.Combine(_root, "ai-intermediate-old"), unrelated = Path.Combine(_root, "user-files"); Directory.CreateDirectory(owned); Directory.CreateDirectory(unrelated); Directory.SetLastWriteTimeUtc(owned, DateTime.UtcNow.AddDays(-3)); Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-3));
        Assert.Equal(1, AiProductionHardeningService.CleanupOrphans(_root)); Assert.False(Directory.Exists(owned)); Assert.True(Directory.Exists(unrelated));
    }
    [Theory]
    [InlineData("Vulkan device lost", "Vulkan GPU")]
    [InlineData("out of memory", "GPU resources")]
    public void BackendFailuresAreClassified(string detail, string expected) => Assert.Contains(expected, AiProductionHardeningService.ClassifyBackendFailure(detail));
}
