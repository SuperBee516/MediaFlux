using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SizeEstimateServiceTests
{
    [Fact]
    public void AutoEstimate_UsesEachFilesMetadataIndependently()
    {
        double detailed1080p = Estimate(
            srcMb: 1_000, durationSec: 3_600, width: 1920, height: 1080,
            fps: 23.976, bitrateKbps: 8_000, sourceCodec: "h264");
        double lowComplexity720p = Estimate(
            srcMb: 1_000, durationSec: 3_600, width: 1280, height: 720,
            fps: 29.97, bitrateKbps: 2_000, sourceCodec: "hevc");

        Assert.NotEqual(detailed1080p, lowComplexity720p);
        Assert.NotEqual(
            Math.Round(100 * (1 - detailed1080p / 1_000)),
            Math.Round(100 * (1 - lowComplexity720p / 1_000)));
    }

    [Fact]
    public void AutoEstimate_ChangesWithCurrentOutputSettings()
    {
        double h264High = Estimate(targetCodec: "libx264", profile: "High Quality");
        double hevcMedium = Estimate(targetCodec: "libx265", profile: "Medium Quality (Default)");
        double av1Low = Estimate(targetCodec: "libaom-av1", profile: "Low Quality (Smaller File)");

        Assert.True(h264High > hevcMedium);
        Assert.True(hevcMedium > av1Low);
    }

    [Fact]
    public void AutoEstimate_ChangesWhenOutputIsScaled()
    {
        double original = Estimate(width: 3840, height: 2160, targetHeight: null);
        double downscaled = Estimate(width: 3840, height: 2160, targetHeight: 1080);

        Assert.True(downscaled < original);
    }

    [Fact]
    public void AutoEstimate_ReturnsUnavailableWhenEssentialMetadataIsMissing()
    {
        double estimate = Estimate(durationSec: 0);

        Assert.Equal(0, estimate);
    }

    [Fact]
    public void NoCompression_DoesNotInventSavings()
    {
        double estimate = Estimate(srcMb: 750, profile: "No Compression");

        Assert.Equal(750, estimate);
    }

    private static double Estimate(
        double srcMb = 1_200,
        double durationSec = 3_600,
        int width = 1920,
        int height = 1080,
        double fps = 30,
        int bitrateKbps = 2_600,
        string sourceCodec = "h264",
        string profile = "Medium Quality (Default)",
        string targetCodec = "libx265",
        int quality = 22,
        int? targetHeight = null)
    {
        return SizeEstimateService.EstimateAutoTargetMbSmart(
            srcMb,
            durationSec,
            width,
            height,
            fps,
            bitrateKbps,
            sourceCodec,
            profile,
            targetCodec,
            quality,
            targetHeight);
    }
}
