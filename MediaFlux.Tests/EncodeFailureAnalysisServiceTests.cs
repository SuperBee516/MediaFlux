using System.IO;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeFailureAnalysisServiceTests
{
    [Fact]
    public void NvencInitializationFailureIncludesConservativePlanContext()
    {
        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("FFmpeg stopped because the requested NVENC encoder is unavailable."),
            "[hevc_nvenc] Cannot load libnvidia-encode.so.1\nError while opening encoder",
            encoderId: "nvenc",
            encoder: "GPU (NVENC)",
            codec: "hevc_nvenc");

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.VideoEncoder, analysis!.Category);
        Assert.Equal("Video encoder initialization", analysis.FailureStage);
        Assert.Contains("software", analysis.RecommendedAction, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("GPU (NVENC)", analysis.PlanContext!.Encoder);
        Assert.Contains("libnvidia", analysis.TechnicalDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSourceIsClassifiedAsInputFailure()
    {
        EncodeFailureAnalysis? analysis = Analyze(
            new FileNotFoundException("Input file does not exist.", "C:\\missing.mkv"),
            "",
            source: "C:\\missing.mkv");

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.Input, analysis!.Category);
        Assert.Contains("exists", analysis.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptStreamIsClassifiedAsDecoderFailure()
    {
        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("ffmpeg exited with code 1"),
            "Error splitting the input into NAL units\nError submitting packet to decoder: Invalid data found when processing input",
            source: "C:\\source.mkv");

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.Decoder, analysis!.Category);
        Assert.Equal("Input decoding", analysis.FailureStage);
    }

    [Fact]
    public void OutputDiskFailureIsNotConfusedWithSourceFailure()
    {
        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("ffmpeg exited with code 1"),
            "Error writing trailer of output.mp4: No space left on device",
            source: "C:\\source.mkv",
            output: "C:\\output.mp4");

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.Output, analysis!.Category);
        Assert.Contains("free disk", analysis.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AudioEncoderFailureIsNotLabeledAsVideoEncoderFailure()
    {
        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("ffmpeg exited with code 1"),
            "Unknown encoder 'aac'");

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.Audio, analysis!.Category);
        Assert.Equal("Audio conversion", analysis.FailureStage);
    }

    [Fact]
    public void CorruptCopiedAudioIsClassifiedAsAudioInputFailure()
    {
        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("ffmpeg exited with code 1"),
            "Error while decoding stream #0:1: Invalid data found when processing input",
            encoderId: "nvenc",
            encoder: "GPU (NVENC)",
            codec: "hevc_nvenc");

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.Audio, analysis!.Category);
        Assert.Equal("Input decoding", analysis.FailureStage);
        Assert.DoesNotContain("NVENC", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopiedAudioMp4TimestampFailureGetsPreciseNonCorruptionDiagnosis()
    {
        const string stderr = "[aost#0:1/copy @ 0000000000000000] Non-monotonic DTS; previous: 0, current: 0; Error submitting a packet to the muxer: Invalid argument\r\n" +
            "[out#0/mp4 @ 0000000000000000] Error muxing a packet\r\n[out#0/mp4 @ 0000000000000000] Task finished with error code: -22 (Invalid argument)";

        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("ffmpeg exited with code -22; terminal FFmpeg evidence: " + stderr),
            stderr,
            encoderId: "nvenc",
            encoder: "GPU (NVENC)",
            codec: "hevc_nvenc");

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.Audio, analysis!.Category);
        Assert.Equal("Audio timestamp incompatibility", analysis.Summary);
        Assert.Contains("stream-copy-to-MP4", analysis.LikelyCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("previous DTS 0", analysis.LikelyCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("current DTS 0", analysis.LikelyCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio output stream 1", analysis.LikelyCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not establish source corruption", analysis.LikelyCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No valid final output was promoted", analysis.LikelyCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not automatically transcode", analysis.RecommendedAction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Non-monotonic DTS", analysis.TechnicalDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Matroska")]
    [InlineData("Unknown")]
    public void RuntimeShapedAudioTimestampFailureRequiresResolvedMp4Context(string outputContainer)
    {
        const string stderr = "[aost#0:1/copy @ 0000000000000000] Non-monotonic DTS; previous: 0, current: 0; Error submitting a packet to the muxer: Invalid argument\r\n" +
            "[out#0/mp4 @ 0000000000000000] Error muxing a packet\r\n" +
            "[out#0/mp4 @ 0000000000000000] Task finished with error code: -22 (Invalid argument)";

        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("ffmpeg exited with code -22; terminal FFmpeg evidence: " + stderr),
            stderr,
            outputContainer: outputContainer);

        Assert.NotNull(analysis);
        Assert.NotEqual("Audio timestamp incompatibility", analysis!.Summary);
    }

    [Fact]
    public void TimestampDiagnosisDoesNotOverrideUnrelatedFailuresOrDestinationIo()
    {
        EncodeFailureAnalysis? unrelated = Analyze(
            new InvalidOperationException("encoder initialization failed"),
            "Unknown encoder 'hevc_nvenc'",
            encoderId: "nvenc",
            codec: "hevc_nvenc");
        Assert.Equal(EncodeFailureCategory.VideoEncoder, unrelated!.Category);

        EncodeFailureAnalysis? destination = Analyze(
            new InvalidOperationException("ffmpeg exited with code 1"),
            "[aost#0:1/copy @ 0000000000000000] Non-monotonic DTS; previous: 0, current: 0; Error submitting a packet to the muxer: Invalid argument\r\n" +
            "[out#0/mp4 @ 0000000000000000] Error writing trailer of output.mp4: No space left on device");
        Assert.Equal(EncodeFailureCategory.Output, destination!.Category);
        Assert.DoesNotContain("Audio timestamp incompatibility", destination.Summary, StringComparison.OrdinalIgnoreCase);

        EncodeFailureAnalysis? videoTimestamp = Analyze(
            new InvalidOperationException("ffmpeg exited with code 1"),
            "[vost#0:0/copy] Non-monotonic DTS; previous: 0, current: 0; Error submitting a packet to the muxer\n[out#0/mp4] Error muxing a packet");
        Assert.Equal(EncodeFailureCategory.Output, videoTimestamp!.Category);
    }

    [Fact]
    public void CompatibleAudioCopyWithoutFailureSignatureDoesNotReceiveTimestampDiagnosis()
    {
        Assert.Null(FfmpegAudioTimestampFailureClassifier.Classify(
            "AAC stream copy selected; MP4 output", OutputContainer.Mp4));

        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("no specific FFmpeg failure evidence"),
            "AAC stream copy selected; MP4 output");
        Assert.Equal(EncodeFailureCategory.Unknown, analysis!.Category);
        Assert.NotEqual("Audio timestamp incompatibility", analysis.Summary);
    }

    [Fact]
    public void EarlierTimestampFailureDoesNotOverrideUnrelatedTerminalFailure()
    {
        const string earlierAttempt = "[aost#0:1/copy] Non-monotonic DTS; previous: 0, current: 0; Error submitting a packet to the muxer";
        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("Unknown encoder 'hevc_nvenc'"),
            earlierAttempt,
            encoderId: "nvenc",
            codec: "hevc_nvenc");

        Assert.Equal(EncodeFailureCategory.VideoEncoder, analysis!.Category);
    }

    [Fact]
    public void CancellationDoesNotProduceFailureAnalysis()
    {
        EncodeFailureAnalysis? analysis = Analyze(
            new OperationCanceledException("The operation was canceled."),
            "Error writing trailer of output.mp4: No space left on device",
            canceled: true);

        Assert.Null(analysis);
    }

    [Fact]
    public void UnknownFailurePreservesBoundedTechnicalDetail()
    {
        string log = string.Join(Environment.NewLine, Enumerable.Range(1, 1000).Select(i => $"unclassified diagnostic line {i}"));
        EncodeFailureAnalysis? analysis = Analyze(
            new InvalidOperationException("unexpected process termination"),
            log);

        Assert.NotNull(analysis);
        Assert.Equal(EncodeFailureCategory.Unknown, analysis!.Category);
        Assert.True(analysis.TechnicalDetail.Length <= 900);
        Assert.Contains("do not match", analysis.LikelyCause, StringComparison.OrdinalIgnoreCase);
    }

    private static EncodeFailureAnalysis? Analyze(
        Exception exception,
        string diagnostic,
        string source = "C:\\source.mkv",
        string output = "C:\\output.mp4",
        string encoderId = "software",
        string encoder = "Software",
        string codec = "libx265",
        bool canceled = false,
        string outputContainer = "Mp4") =>
        EncodeFailureAnalysisService.Analyze(new EncodeFailureAnalysisContext(
            exception,
            diagnostic,
            "Encoding",
            canceled,
            source,
            output,
            encoderId,
            encoder,
            codec,
            "p5",
            false,
            outputContainer,
            "Off"));
}
