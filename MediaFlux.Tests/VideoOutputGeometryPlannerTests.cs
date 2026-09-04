using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class VideoOutputGeometryPlannerTests
{
    [Theory]
    [InlineData(1280, 701, 1280, 702)]
    [InlineData(1280, 1067, 1280, 1068)]
    public void Nvenc420_NormalizesOddCodedHeightUp(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        VideoOutputGeometryPlan plan = Resolve(sourceWidth, sourceHeight);

        Assert.Equal(expectedWidth, plan.Width);
        Assert.Equal(expectedHeight, plan.Height);
        Assert.True(plan.WasNormalized);
        Assert.Equal($"{expectedWidth}:{expectedHeight}", plan.ScaleExpression);
        Assert.Contains("chroma alignment", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvenSourceWithoutResizeRemainsUnchanged()
    {
        VideoOutputGeometryPlan plan = Resolve(1280, 720);

        Assert.Equal(1280, plan.Width);
        Assert.Equal(720, plan.Height);
        Assert.False(plan.WasNormalized);
        Assert.False(plan.RequiresExplicitScale);
    }

    private static VideoOutputGeometryPlan Resolve(int width, int height) =>
        VideoOutputGeometryPlanner.Resolve(
            width,
            height,
            VideoRestorationPipeline.ResolveFinalOutputResolution(
                width,
                height,
                new VideoRestorationSettings(),
                EncodingService.ScaleMode.None),
            new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"),
            tenBit: false);
}
