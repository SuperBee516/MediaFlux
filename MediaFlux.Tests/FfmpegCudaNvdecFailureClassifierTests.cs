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
}
