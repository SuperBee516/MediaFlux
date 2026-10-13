using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegCudaNvdecFailureClassifierTests
{
    [Theory]
    [InlineData("CUDA_ERROR_LAUNCH_FAILED")]
    [InlineData("CUDA_ERROR_MAP_FAILED")]
    [InlineData("cuMemcpy2DAsync failure")]
    [InlineData("cuvidMapVideoFrame failure")]
    [InlineData("Failed unlocking input buffer")]
    public void KnownNvdecCudaEvidenceIsClassified(string stderr)
    {
        FfmpegCudaNvdecFailure result = FfmpegCudaNvdecFailureClassifier.Classify(stderr);
        Assert.True(result.IsCudaNvdecDeviceFailure);
        Assert.NotEmpty(result.MatchedEvidence);
    }

    [Theory]
    [InlineData("Invalid argument")]
    [InlineData("No space left on device")]
    [InlineData("moov atom not found")]
    [InlineData("Could not write header for output file")]
    public void OrdinaryFfmpegFailuresAreNotRecoveryCandidates(string stderr)
    {
        FfmpegCudaNvdecFailure result = FfmpegCudaNvdecFailureClassifier.Classify(stderr);
        Assert.False(result.IsCudaNvdecDeviceFailure);
        Assert.Empty(result.MatchedEvidence);
    }

    [Fact]
    public void RecoveryIsAllowedExactlyOnceOnlyForNvencHardwareDecode()
    {
        FfmpegCudaNvdecFailure failure = FfmpegCudaNvdecFailureClassifier.Classify("CUDA_ERROR_MAP_FAILED");
        Assert.True(FfmpegCudaNvdecFailureClassifier.ShouldRetryOnce(true, true, false, false, failure));
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryOnce(true, false, false, false, failure));
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryOnce(false, true, false, false, failure));
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryOnce(true, true, true, false, failure));
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryOnce(true, true, false, true, failure));
    }

    [Fact]
    public void ExactCudaScaleAutoScaleFailureGetsOneQualityPreservingRecovery()
    {
        const string arguments = "-hwaccel cuda -hwaccel_output_format cuda -vf scale_cuda=format=p010le -c:v hevc_nvenc -cq 27 -preset p5";
        const string stderr = "Impossible to convert between the formats supported by the filter 'Parsed_scale_cuda_0' and the filter 'auto_scale_0'\nError reinitializing filters!";

        Assert.True(FfmpegCudaNvdecFailureClassifier.ShouldRetryFilterGraphOnce(
            true, true, false, false, arguments, stderr));
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryFilterGraphOnce(
            true, true, false, true, arguments, stderr));
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryFilterGraphOnce(
            true, true, true, false, arguments, stderr));
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryFilterGraphOnce(
            false, true, false, false, arguments, stderr));
    }

    [Theory]
    [InlineData("Error reinitializing filters!")]
    [InlineData("Impossible to convert between formats: scale_cuda to auto_scale")]
    [InlineData("Impossible to convert between the formats supported by the filter 'scale' and 'auto_scale'\nError reinitializing filters!")]
    public void GenericFilterFailuresDoNotTriggerCudaRecovery(string stderr)
    {
        Assert.False(FfmpegCudaNvdecFailureClassifier.ShouldRetryFilterGraphOnce(
            true, true, false, false,
            "-hwaccel cuda -hwaccel_output_format cuda -vf scale_cuda=format=p010le -c:v hevc_nvenc",
            stderr));
    }

    [Fact]
    public void ReliablePrimaryFilterDiagnosisSurvivesUnknownFallbackSummary()
    {
        var primaryCollector = new FfmpegDiagnosticCollector();
        primaryCollector.Observe("Impossible to convert between the formats supported by the filter 'Parsed_scale_cuda_0' and 'auto_scale_0'", FfmpegDiagnosticComponent.Ffmpeg);
        FfmpegDiagnosticSummary primary = primaryCollector.Complete();
        FfmpegDiagnosticSummary fallback = new FfmpegDiagnosticCollector().Complete();
        var attempt = new FfmpegAttemptDiagnostic(1, "Primary encode", "primary", 1,
            "filter negotiation failed", primary);

        FfmpegDiagnosticSummary selected = EncodingService.SelectMostInformativeFailureDiagnostics(fallback, [attempt]);

        Assert.Equal(FfmpegDiagnosticCategory.FilterGraphNegotiation, selected.Classification.PrimaryCategory);
        Assert.Equal(FfmpegDiagnosticConfidence.Moderate, selected.Classification.Confidence);
    }
}
