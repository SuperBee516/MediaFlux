using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegSourceTruncationClassifierTests
{
    [Theory]
    [InlineData("[mov] unexpected end of file")]
    [InlineData("partial file")]
    [InlineData("stream is truncated")]
    public void ExplicitIncompleteMediaEvidenceIsReliable(string stderr)
    {
        Assert.True(FfmpegSourceTruncationClassifier.Classify(stderr).IsReliable);
    }

    [Theory]
    [InlineData("duration metadata differs by two seconds")]
    [InlineData("Invalid NAL unit size")]
    [InlineData("")]
    public void MetadataAndDecodeDiagnosticsAreNotMisclassifiedAsTruncation(string stderr)
    {
        Assert.False(FfmpegSourceTruncationClassifier.Classify(stderr).IsReliable);
    }
}
