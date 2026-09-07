using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegEncodeFailureClassifierTests
{
    [Theory]
    [InlineData("Error writing trailer of output.mp4: No space left on device", "InsufficientSpace")]
    [InlineData("output.mp4: Permission denied", "PermissionDenied")]
    [InlineData("output.mp4: No such file or directory", "DestinationUnavailable")]
    [InlineData("av_interleaved_write_frame(): Input/output error", "WriteIoFailure")]
    public void StorageFailuresAreClassifiedOnlyFromSpecificOutputEvidence(string stderr, string expected)
    {
        FfmpegStorageFailure failure = FfmpegStorageFailureClassifier.Classify(stderr, "C:\\output.mp4");

        Assert.Equal(Enum.Parse<FfmpegStorageFailureKind>(expected), failure.Kind);
        Assert.True(failure.IsReliable);
    }

    [Fact]
    public void SourceFailureIsNotMistakenForDestinationDisappearance()
    {
        FfmpegStorageFailure failure = FfmpegStorageFailureClassifier.Classify(
            "source.mkv: No such file or directory", "C:\\output.mp4");

        Assert.Equal(FfmpegStorageFailureKind.None, failure.Kind);
    }

    [Fact]
    public void InputAccessAndIoErrorsAreNotMistakenForDestinationStorageFailures()
    {
        Assert.Equal(FfmpegStorageFailureKind.None,
            FfmpegStorageFailureClassifier.Classify("source.mkv: Permission denied", "C:\\output.mp4").Kind);
        Assert.Equal(FfmpegStorageFailureKind.None,
            FfmpegStorageFailureClassifier.Classify("source.mkv: Input/output error", "C:\\output.mp4").Kind);
    }

    [Theory]
    [InlineData("Unknown encoder 'hevc_nvenc'", "Unavailable")]
    [InlineData("[hevc_nvenc] InitializeEncoder failed: invalid param", "UnsupportedConfiguration")]
    [InlineData("[h264_nvenc] NV_ENC_ERR_GENERIC", "RuntimeFailure")]
    public void NvencFailuresAreClassifiedWithoutMakingThemFallbackCandidates(string stderr, string expected)
    {
        FfmpegNvencFailure failure = FfmpegNvencFailureClassifier.Classify(stderr);

        Assert.Equal(Enum.Parse<FfmpegNvencFailureKind>(expected), failure.Kind);
        Assert.True(failure.IsReliable);
    }
}
