using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegVideoDecodeRecoveryPolicyTests
{
    private const string ReliableVideoCorruption =
        "[h264] Invalid NAL unit size\n" +
        "[h264] missing picture in access unit\n" +
        "Error splitting the input into NAL units\n" +
        "Error submitting packet to decoder: Invalid data found when processing input";

    [Fact]
    public void IntelligentAllowsReliableVideoCorruptionOnce()
    {
        FfmpegVideoDecodeRecoveryDecision decision = Evaluate(ReliableVideoCorruption);

        Assert.True(decision.Eligible);
        Assert.Contains("Invalid NAL unit size", decision.Evidence);
        Assert.False(Evaluate(ReliableVideoCorruption, alreadyAttempted: true).Eligible);
    }

    [Theory]
    [InlineData(ContainerCompatibilityPolicy.Strict, false, false)]
    [InlineData(ContainerCompatibilityPolicy.AlwaysAsk, false, false)]
    [InlineData(ContainerCompatibilityPolicy.Intelligent, true, false)]
    public void PolicyAndCancellationGateRecovery(ContainerCompatibilityPolicy policy, bool canceled, bool expected)
    {
        Assert.Equal(expected, Evaluate(ReliableVideoCorruption, policy: policy, canceled: canceled).Eligible);
    }

    [Theory]
    [InlineData("unexpected end of file")]
    [InlineData("No space left on device while writing output.mp4")]
    [InlineData("NV_ENC_ERR_INVALID_PARAM")]
    [InlineData("Unknown encoder 'hevc_nvenc'")]
    [InlineData("Error while decoding stream #0:1: Invalid data found when processing input")]
    [InlineData("random ffmpeg failure")]
    public void UnrelatedOrInsufficientEvidenceDoesNotTriggerRecovery(string diagnostics)
    {
        Assert.False(Evaluate(diagnostics).Eligible);
    }

    private static FfmpegVideoDecodeRecoveryDecision Evaluate(
        string diagnostics,
        ContainerCompatibilityPolicy policy = ContainerCompatibilityPolicy.Intelligent,
        bool canceled = false,
        bool alreadyAttempted = false) =>
        FfmpegVideoDecodeRecoveryPolicy.Evaluate(
            diagnostics, policy, canceled, alreadyAttempted, requestedHardwareEncoder: true, "output.mp4");
}
