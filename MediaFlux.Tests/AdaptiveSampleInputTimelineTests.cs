using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AdaptiveSampleInputTimelineTests
{
    [Theory]
    [InlineData(0.0, "0.033")]
    [InlineData(5.0, "5.033")]
    public void PreviewSampleSeekIncludesTheVideoStartOffsetBeforeInput(double sampleStartSeconds,
        string expectedInputStart)
    {
        EncodingDecisionContext context = AdaptiveStorageSavingsTests.Context();
        EncodingPlan plan = EncodingPlanService.Create(context);
        AdaptiveQualitySelectionRequest request = AdaptiveStorageSavingsTests.Request() with
        {
            SourceVideoStartOffsetSeconds = 0.033,
            SourceVideoEndOffsetSeconds = 1_581.233989
        };
        var service = new EncodingService(Path.GetTempPath(), _ => { });

        string arguments = service.BuildPreviewSampleArguments(context, plan, concurrentEncoderSessions: false,
            request: request, quality: 25,
            sample: new RepresentativeSample("Window", TimeSpan.FromSeconds(sampleStartSeconds), TimeSpan.FromSeconds(25)),
            output: "adaptive-sample.mp4");

        int seekIndex = arguments.IndexOf($"-ss {expectedInputStart}", StringComparison.Ordinal);
        int inputIndex = arguments.IndexOf("-i ", StringComparison.Ordinal);
        Assert.True(seekIndex >= 0, $"Expected an input-relative seek of {expectedInputStart}s in: {arguments}");
        Assert.True(inputIndex > seekIndex, $"The seek must precede the input: {arguments}");
        Assert.Contains("-t 25", arguments);
    }

    [Fact]
    public void MissingOrInvalidVideoStartOffsetPreservesTheSampleStart()
    {
        var sample = new RepresentativeSample("Window", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25));

        Assert.Equal(sample.Start, EncodingService.GetInputRelativeSampleStart(sample, null));
        Assert.Equal(sample.Start, EncodingService.GetInputRelativeSampleStart(sample, double.NaN));
        Assert.Equal(TimeSpan.FromSeconds(5.033),
            EncodingService.GetInputRelativeSampleStart(sample, 0.033));
    }
}
