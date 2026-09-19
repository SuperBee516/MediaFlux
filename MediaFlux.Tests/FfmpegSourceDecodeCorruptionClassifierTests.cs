using MediaFlux.Services;
using MediaFlux.Models;
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
    public void DecoderInvalidDataDiagnosticIsReliableWithoutCodecSpecificBitstreamText()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "Error submitting packet to decoder: Invalid data found when processing input");

        Assert.True(result.IsReliable);
    }

    [Fact]
    public void DecoderProcessingFailureIsReliable()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "Error processing packet in decoder: Invalid data found when processing input");

        Assert.True(result.IsReliable);
    }

    [Fact]
    public void MatroskaEbmlCorruptionIsReliableEvenWhenFfmpegExitsSuccessfully()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "[matroska,webm @ 000001] invalid as first byte of an EBML number");

        Assert.True(result.IsReliable);
        Assert.Contains("invalid as first byte of an EBML number", result.MatchedEvidence);
    }

    [Fact]
    public void StrongStructuralEvidenceRequiresMoreThanOneStructuralSignal()
    {
        FfmpegSourceDecodeCorruption isolated = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "[h264] Invalid NAL unit size\nError submitting packet to decoder: Invalid data found when processing input");
        FfmpegSourceDecodeCorruption corroborated = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "[h264] Invalid NAL unit size\n[h264] missing picture in access unit\n" +
            "Error splitting the input into NAL units\nError processing packet in decoder: Invalid data found when processing input");

        Assert.False(isolated.IsStrongSourceIntegrityEvidence);
        Assert.True(corroborated.IsStrongSourceIntegrityEvidence);
    }

    [Fact]
    public void ExitCodeAloneIsNotSourceIntegrityEvidence()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "FFmpeg exit -1094995529");

        Assert.False(result.IsReliable);
        Assert.False(result.IsStrongSourceIntegrityEvidence);
    }

    [Fact]
    public void DownstreamEncoderAbortIsNotHardwareEvidence()
    {
        EncodingSourceFailureClassification result = EncodingSourceFailureClassifier.Classify(
            "Could not open encoder before EOF");

        Assert.NotEqual(EncodingSourceFailureType.GpuEncoderFailure, result.Type);
        Assert.False(result.IsRecoveryCandidate);
    }

    [Fact]
    public void StreamDecodeInvalidDataCountsAsDecoderEvidenceWhenStructuralCorruptionIsPresent()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "Invalid NAL unit size\nError splitting the input into NAL units\n" +
            "Error while decoding stream #0:0: Invalid data found when processing input");

        Assert.True(result.IsStrongSourceIntegrityEvidence);
    }

    [Fact]
    public void StructuralAndContainerCorruptionPreemptsTolerantDecodeWhenStrictXerrorStopsEarly()
    {
        FfmpegSourceDecodeCorruption result = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "stream 1, missing mandatory atoms, broken header\n" +
            "Invalid NAL unit size\nError splitting the input into NAL units");

        Assert.True(result.IsStrongSourceIntegrityEvidence);
    }

    [Fact]
    public void MediaFluxCaptureMarkerDoesNotOverrideStrongSourceCorruption()
    {
        EncodingSourceFailureClassification result = EncodingSourceFailureClassifier.Classify(
            "Invalid NAL unit size\nError splitting the input into NAL units\n" +
            "Error while decoding stream #0:0: Invalid data found when processing input\n" +
            "[Additional FFmpeg diagnostic output truncated by MediaFlux.]");

        Assert.Equal(EncodingSourceFailureType.VideoBitstreamCorruption, result.Type);
        Assert.True(result.IsRecoveryCandidate);
    }

    [Fact]
    public void HighConfidenceDiagnosticSummarySuppliesInitialDecoderEvidence()
    {
        FfmpegSourceDecodeCorruption raw = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "Invalid NAL unit size\nError splitting the input into NAL units");
        var summary = new FfmpegDiagnosticSummary(
            Array.Empty<FfmpegDiagnosticFamilySummary>(),
            new FfmpegDiagnosticClassification(
                FfmpegDiagnosticCategory.SourceIntegrity,
                "Malformed source",
                FfmpegDiagnosticConfidence.High,
                ["Invalid NAL unit size", "Error splitting input into NAL units", "Invalid input data"]),
            0, 0, false);

        Assert.True(FfmpegSourceDecodeCorruptionClassifier.HasStrongSourceIntegrityEvidence(raw, summary));
    }

    [Fact]
    public void LowConfidenceSummaryDoesNotPreemptGenericVideoRecovery()
    {
        FfmpegSourceDecodeCorruption raw = FfmpegSourceDecodeCorruptionClassifier.Classify(
            "Invalid NAL unit size");
        var summary = new FfmpegDiagnosticSummary(
            Array.Empty<FfmpegDiagnosticFamilySummary>(),
            new FfmpegDiagnosticClassification(
                FfmpegDiagnosticCategory.SourceDecode,
                "Uncertain source decode failure",
                FfmpegDiagnosticConfidence.Moderate,
                ["Invalid NAL unit size"]),
            0, 0, false);

        Assert.False(FfmpegSourceDecodeCorruptionClassifier.HasStrongSourceIntegrityEvidence(raw, summary));
    }
}
