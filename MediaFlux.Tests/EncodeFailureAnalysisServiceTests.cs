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
        bool canceled = false) =>
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
            "Mp4",
            "Off"));
}
