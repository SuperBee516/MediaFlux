using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class OutputContainerValidationTests
{
    [Fact]
    public void MatroskaValidation_AcceptsContainerAndPreservedAttachments()
    {
        MediaProbeResult source = Probe(
            "matroska,webm",
            Stream("video", "hevc"),
            Stream("audio", "aac"),
            Stream("attachment", "ttf"));
        MediaProbeResult output = Probe(
            "matroska,webm",
            Stream("video", "hevc"),
            Stream("audio", "aac"),
            Stream("attachment", "ttf"));
        EncodeOutputValidationRequest request = Request(OutputContainer.Matroska, copyAttachments: true);

        Assert.Equal("", EncodeOutputValidationService.ValidateProbe(request, source, output));
    }

    [Fact]
    public void MatroskaValidation_RejectsMissingSelectedAttachment()
    {
        MediaProbeResult source = Probe(
            "matroska,webm",
            Stream("video", "hevc"),
            Stream("attachment", "ttf"));
        MediaProbeResult output = Probe("matroska,webm", Stream("video", "hevc"));

        string error = EncodeOutputValidationService.ValidateProbe(
            Request(OutputContainer.Matroska, copyAttachments: true),
            source,
            output);

        Assert.Contains("attachment", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validation_RejectsWrongResolvedContainer()
    {
        MediaProbeResult source = Probe("matroska,webm", Stream("video", "hevc"));
        MediaProbeResult output = Probe("mov,mp4", Stream("video", "hevc"));

        string error = EncodeOutputValidationService.ValidateProbe(
            Request(OutputContainer.Matroska), source, output);

        Assert.Contains("requested Matroska", error, StringComparison.OrdinalIgnoreCase);
    }

    private static EncodeOutputValidationRequest Request(
        OutputContainer container,
        bool copyAttachments = false) => new()
    {
        Input = EncodingInputSource.FromFile("source.mkv"),
        Encoder = EncoderRegistry.Default.ResolveLegacyCodec("libx265").Selection,
        CopyAttachments = copyAttachments,
        ContainerDecision = new OutputContainerDecision
        {
            Requested = container == OutputContainer.Matroska
                ? OutputContainerSelection.Matroska
                : OutputContainerSelection.Mp4,
            Resolved = container,
            Reason = "Test."
        }
    };

    private static MediaProbeResult Probe(
        string format,
        params MediaProbeStreamInfo[] streams) => new()
    {
        Success = true,
        FormatName = format,
        DurationSeconds = 30,
        Streams = streams
    };

    private static MediaProbeStreamInfo Stream(string type, string codec) => new()
    {
        CodecType = type,
        CodecName = codec,
        Width = type == "video" ? 1920 : null,
        Height = type == "video" ? 1080 : null,
        PixelFormat = type == "video" ? "yuv420p" : ""
    };
}
