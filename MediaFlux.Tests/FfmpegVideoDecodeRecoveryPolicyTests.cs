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

    [Fact]
    public void MatroskaCorruptionEvidenceAllowsOneTolerantValidationRecovery()
    {
        FfmpegVideoDecodeRecoveryDecision decision = Evaluate(
            "invalid as first byte of an EBML number");

        Assert.True(decision.Eligible);
        Assert.Contains("EBML", decision.Evidence);
        Assert.False(Evaluate("invalid as first byte of an EBML number", alreadyAttempted: true).Eligible);
    }

    [Fact]
    public void DurationFailureWithoutCurrentCorruptionEvidenceIsNotRecoverable()
    {
        Assert.False(Evaluate("FFmpeg completed; staged output duration is 25 seconds short").Eligible);
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

    [Fact]
    public void MaterialCfrValidationDeficitAllowsOneNvencRecovery()
    {
        var evidence = new EncodeOutputValidationFailureEvidence
        {
            SourceProbe = new MediaProbeResult { Success = true },
            OutputProbe = new MediaProbeResult { Success = true },
            ExpectedFrameCount = 64_729,
            ActualFrameCount = 63_380,
            FrameDelta = -1_349,
            FrameRate = 30000d / 1001d,
            DeficitSeconds = 45.011,
            AllowedSeconds = .75,
            SourceDurationSeconds = 2159.747,
            OutputDurationSeconds = 2159.758
        };
        SourceTimingAnalysis timing = new(SourceTimingClassification.Cfr, AiTimingEligibility.EligibleCurrentCfrPipeline, 80, 30000d / 1001d, 30000d / 1001d, 0, false, false, "stable");

        FfmpegVideoDecodeRecoveryDecision decision = FfmpegVideoDecodeRecoveryPolicy.EvaluateFrameDeficit(
            evidence, ContainerCompatibilityPolicy.Intelligent, timing, false, false, true);

        Assert.True(decision.Eligible);
        Assert.Same(evidence, decision.FrameDeficit);
        Assert.False(FfmpegVideoDecodeRecoveryPolicy.EvaluateFrameDeficit(evidence, ContainerCompatibilityPolicy.Intelligent, timing, false, true, true).Eligible);
        Assert.False(FfmpegVideoDecodeRecoveryPolicy.EvaluateFrameDeficit(evidence, ContainerCompatibilityPolicy.Intelligent, timing, false, false, false).Eligible);
    }

    [Theory]
    [InlineData(SourceTimingClassification.Vfr)]
    [InlineData(SourceTimingClassification.CfrMinorVariance)]
    [InlineData(SourceTimingClassification.IrregularUnsafe)]
    public void NonCurrentCfrTimingDoesNotTriggerFrameDeficitRecovery(SourceTimingClassification classification)
    {
        var evidence = new EncodeOutputValidationFailureEvidence
        {
            SourceProbe = new MediaProbeResult { Success = true }, OutputProbe = new MediaProbeResult { Success = true },
            ExpectedFrameCount = 3000, ActualFrameCount = 2900, FrameDelta = -100, FrameRate = 30,
            DeficitSeconds = 3.33, AllowedSeconds = .75, SourceDurationSeconds = 100, OutputDurationSeconds = 100
        };
        var timing = new SourceTimingAnalysis(classification, AiTimingEligibility.PotentialFutureTimestampAware, 80, 30, 30, 0, false, false, "not current CFR");
        Assert.False(FfmpegVideoDecodeRecoveryPolicy.EvaluateFrameDeficit(evidence, ContainerCompatibilityPolicy.Intelligent, timing, false, false, true).Eligible);
    }

    private static FfmpegVideoDecodeRecoveryDecision Evaluate(
        string diagnostics,
        ContainerCompatibilityPolicy policy = ContainerCompatibilityPolicy.Intelligent,
        bool canceled = false,
        bool alreadyAttempted = false) =>
        FfmpegVideoDecodeRecoveryPolicy.Evaluate(
            diagnostics, policy, canceled, alreadyAttempted, requestedHardwareEncoder: true, "output.mp4");
}
