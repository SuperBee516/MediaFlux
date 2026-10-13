using MediaFlux.Services;
using MediaFlux.Models;
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

    [Fact]
    public void NonMonotonicAudioDtsMuxFailureIsNotLabeledAsDestinationIo()
    {
        string stderr = "[aost#0:1/copy] Non-monotonic DTS; previous: 38225920, current: 38225920; Error submitting a packet to the muxer: Invalid argument\n" +
            "[out#0/mp4] Error muxing a packet\n[out#0/mp4] Task finished with error code: -22 (Invalid argument)";
        Assert.Equal(FfmpegStorageFailureKind.None,
            FfmpegStorageFailureClassifier.Classify(stderr, @"Z:\output.mp4").Kind);
    }

    [Fact]
    public void CopiedAudioNonMonotonicDtsIsRecognizedOnlyForMp4MuxSubmissionFailure()
    {
        const string stderr = "[aost#0:1/copy] Non-monotonic DTS; previous: 38225920, current: 38225920; Error submitting a packet to the muxer: Invalid argument\n" +
            "[out#0/mp4] Error muxing a packet\n[out#0/mp4] Task finished with error code: -22 (Invalid argument)";

        FfmpegAudioTimestampFailure failure = Assert.IsType<FfmpegAudioTimestampFailure>(
            FfmpegAudioTimestampFailureClassifier.Classify(stderr, OutputContainer.Mp4));

        Assert.Equal(1, failure.OutputAudioStreamIndex);
        Assert.Equal(38225920, failure.PreviousDts);
        Assert.Equal(38225920, failure.CurrentDts);
        Assert.Contains("Non-monotonic DTS", failure.MatchedEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stream-copy-to-MP4", failure.DescribeDiagnosis(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopiedAudioNonMonotonicDtsWithRuntimeObjectAddressIsRecognized()
    {
        const string stderr = "[aost#0:1/copy @ 0000000000000000] Non-monotonic DTS; previous: 0, current: 0; Error submitting a packet to the muxer: Invalid argument\r\n" +
            "[out#0/mp4 @ 0000000000000000] Error muxing a packet\r\n" +
            "[out#0/mp4 @ 0000000000000000] Task finished with error code: -22 (Invalid argument)";

        FfmpegAudioTimestampFailure failure = Assert.IsType<FfmpegAudioTimestampFailure>(
            FfmpegAudioTimestampFailureClassifier.Classify(stderr, OutputContainer.Mp4));

        Assert.Equal(1, failure.OutputAudioStreamIndex);
        Assert.Equal(0, failure.PreviousDts);
        Assert.Equal(0, failure.CurrentDts);
        Assert.Contains("/copy @ 0000000000000000]", failure.MatchedEvidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ordinary unrelated FFmpeg failure", "Mp4")]
    [InlineData("[vost#0:0/copy] Non-monotonic DTS; previous: 4, current: 4; Error submitting a packet to the muxer", "Mp4")]
    [InlineData("[aost#0:1/copy] Non-monotonic DTS; previous: 4, current: 4; Error submitting a packet to the muxer", "Matroska")]
    [InlineData("[aost#0:1/copy] Non-monotonic DTS; previous: 4, current: 4; Error writing trailer", "Mp4")]
    [InlineData("AAC stream copy selected; MP4 output", "Mp4")]
    public void AudioTimestampFailureRequiresCopiedAudioMp4AndMuxSubmissionEvidence(string stderr, string container)
    {
        Assert.Null(FfmpegAudioTimestampFailureClassifier.Classify(
            stderr, Enum.Parse<OutputContainer>(container)));
    }

    [Fact]
    public void AudioTimestampFailureDoesNotMatchWhenResolvedContainerContextIsUnavailable()
    {
        const string stderr = "[aost#0:1/copy @ 0000000000000000] Non-monotonic DTS; previous: 0, current: 0; Error submitting a packet to the muxer: Invalid argument";

        Assert.Null(FfmpegAudioTimestampFailureClassifier.Classify(stderr, (OutputContainer)0));
    }

    [Theory]
    [InlineData("Unknown encoder 'hevc_nvenc'", "Unavailable")]
    [InlineData("Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0", "DriverIncompatible")]
    [InlineData("[hevc_nvenc] InitializeEncoder failed: invalid param", "UnsupportedConfiguration")]
    [InlineData("[h264_nvenc] NV_ENC_ERR_GENERIC", "RuntimeFailure")]
    public void NvencFailuresAreClassifiedWithoutMakingThemFallbackCandidates(string stderr, string expected)
    {
        FfmpegNvencFailure failure = FfmpegNvencFailureClassifier.Classify(stderr);

        Assert.Equal(Enum.Parse<FfmpegNvencFailureKind>(expected), failure.Kind);
        Assert.True(failure.IsReliable);
    }
}
