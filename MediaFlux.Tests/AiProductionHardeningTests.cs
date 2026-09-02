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
    public void EstimateScalesWithActualPeakChunkWorkingSet()
    {
        AiTemporaryStorageEstimate small = AiProductionHardeningService.Estimate(640, 480, 100, AiRestorationScale.X2, _root, AiChunkPlanner.MinimumFramesPerChunk);
        AiTemporaryStorageEstimate large = AiProductionHardeningService.Estimate(640, 480, 200, AiRestorationScale.X4, _root, AiChunkPlanner.MaximumFramesPerChunk);
        Assert.True(large.EstimatedBytes > small.EstimatedBytes);
    }
    [Theory]
    [InlineData(640, 480)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void PeakEstimateDoesNotGrowWithCompletedVideoDuration(int width, int height)
    {
        AiTemporaryStorageEstimate shortVideo = AiProductionHardeningService.Estimate(width, height, 120, AiRestorationScale.X2, _root, 60, long.MaxValue);
        AiTemporaryStorageEstimate longVideo = AiProductionHardeningService.Estimate(width, height, 120_000, AiRestorationScale.X2, _root, 60, long.MaxValue);

        Assert.Equal(shortVideo.EstimatedBytes, longVideo.EstimatedBytes);
        Assert.Equal(shortVideo.EstimatedIntermediateBytes, longVideo.EstimatedIntermediateBytes);
    }
    [Theory]
    [InlineData(640, 480, 2)]
    [InlineData(640, 480, 3)]
    [InlineData(640, 480, 4)]
    [InlineData(1280, 720, 2)]
    [InlineData(1280, 720, 3)]
    [InlineData(1280, 720, 4)]
    [InlineData(1920, 1080, 2)]
    [InlineData(1920, 1080, 3)]
    [InlineData(1920, 1080, 4)]
    [InlineData(3840, 2160, 2)]
    [InlineData(3840, 2160, 3)]
    [InlineData(3840, 2160, 4)]
    public void PeakEstimateIncludesEveryActiveWorkingSetComponent(int width, int height, int scaleValue)
    {
        AiTemporaryStorageEstimate estimate = AiProductionHardeningService.Estimate(width, height, 90_000, (AiRestorationScale)scaleValue, _root, 60, long.MaxValue);

        Assert.True(estimate.EstimatedPeakExtractedBytes > 0);
        Assert.True(estimate.EstimatedPeakRestoredBytes > 0);
        Assert.True(estimate.EstimatedIntermediateBytes > 0);
        Assert.True(estimate.SafetyMarginBytes > 0);
        Assert.Equal(estimate.PeakWorkingSetBytes + estimate.SafetyMarginBytes, estimate.EstimatedBytes);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ScaleIncreasesOnlyScaledWorkingFiles(int scaleValue)
    {
        AiRestorationScale scale = (AiRestorationScale)scaleValue;
        AiTemporaryStorageEstimate estimate = AiProductionHardeningService.Estimate(1280, 720, 10_000, scale, _root, 60, long.MaxValue);
        AiTemporaryStorageEstimate x2 = AiProductionHardeningService.Estimate(1280, 720, 10_000, AiRestorationScale.X2, _root, 60, long.MaxValue);

        Assert.Equal(x2.EstimatedPeakExtractedBytes, estimate.EstimatedPeakExtractedBytes);
        Assert.True(estimate.EstimatedPeakRestoredBytes >= x2.EstimatedPeakRestoredBytes);
        Assert.True(estimate.EstimatedIntermediateBytes >= x2.EstimatedIntermediateBytes);
    }
    [Fact]
    public void PeakSpacePreflightAcceptsLongVideoWhenActiveChunkFitsAndRejectsActualShortfall()
    {
        AiTemporaryStorageEstimate baseline = AiProductionHardeningService.Estimate(1920, 1080, 200_000, AiRestorationScale.X2, _root, 60, long.MaxValue);
        AiTemporaryStorageEstimate fits = AiProductionHardeningService.Estimate(1920, 1080, 200_000, AiRestorationScale.X2, _root, 60, baseline.EstimatedBytes);
        AiTemporaryStorageEstimate insufficient = AiProductionHardeningService.Estimate(1920, 1080, 200_000, AiRestorationScale.X2, _root, 60, baseline.EstimatedBytes - 1);

        Assert.False(fits.IsClearlyInsufficient);
        Assert.True(insufficient.IsClearlyInsufficient);
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
