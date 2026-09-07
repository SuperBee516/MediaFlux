using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegSourceDecodeCorruptionClassifierTests
{
    [Fact]
    public void CorroboratedH264BitstreamAndDecoderFailuresAreReliable()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "[h264] Invalid NAL unit size\n[h264] missing picture in access unit\nError submitting packet to decoder: Invalid data found when processing input");

        Assert.True(result.IsReliable);
        Assert.Equal(3, result.MatchedEvidence.Count);
    }

    [Fact]
    public void LoneDecoderDiagnosticIsNotTreatedAsReliableCorruption()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "Error submitting packet to decoder: Invalid data found when processing input");

        Assert.False(result.IsReliable);
    }
}
