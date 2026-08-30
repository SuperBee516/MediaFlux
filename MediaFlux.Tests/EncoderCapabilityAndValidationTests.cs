using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncoderCapabilityAndValidationTests
{
    [Fact]
    public void EncoderListParserReturnsOnlyVideoEncoderNames()
    {
        const string output =
            """
            Encoders:
             V..... = Video
             A..... = Audio
             V....D h264_nvenc          NVIDIA NVENC H.264 encoder
             V....D hevc_nvenc          NVIDIA NVENC HEVC encoder
             V....D libx265             libx265 H.265 / HEVC
             A..... aac                 AAC encoder
            """;

        IReadOnlySet<string> actual =
            FfmpegEncoderCapabilityService.ParseEncoderNames(output);

        Assert.Equal(
            ["h264_nvenc", "hevc_nvenc", "libx265"],
            actual.OrderBy(item => item));
    }

    [Fact]
    public void EncoderOptionParserFindsHighBitDepthSupport()
    {
        const string output =
            """
            Encoder hevc_nvenc [NVIDIA NVENC hevc encoder]:
              -preset            <int>        E..V....... Set the encoding preset
              -highbitdepth      <boolean>    E..V....... Enable 10 bit encode for 8 bit input
              -split_encode_mode <int>        E..V....... Specifies the split encoding mode
            """;

        IReadOnlySet<string> actual =
            FfmpegEncoderCapabilityService.ParseEncoderOptionNames(output);

        Assert.Contains("highbitdepth", actual);
        Assert.Contains("split_encode_mode", actual);
        Assert.DoesNotContain("Encoder", actual);
    }

    [Fact]
    public void EncoderPixelFormatPreflightRecognizesSupportedFormats()
    {
        const string help = """
            Encoder hevc_nvenc [NVIDIA NVENC hevc encoder]:
            Supported pixel formats: yuv420p nv12 p010le yuv444p
            """;

        IReadOnlySet<string> formats =
            FfmpegEncoderCapabilityService.ParseEncoderPixelFormats(help);

        Assert.Contains("p010le", formats);
        Assert.Contains("nv12", formats);
    }

    [Fact]
    public void Libx265NormalizationRemovesGpuOnlyState()
    {
        ResolvedVideoEncoder encoder = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);

        ValidatedEncoderSettings actual =
            EncodingRequestValidator.ValidateAndNormalize(
                EncoderRegistry.Default,
                encoder.Selection,
                useGpu: true,
                targetMb: null,
                preset: "VERYSLOW",
                qualityValue: 80,
                tenBit: true,
                audioChannels: 6,
                concurrentEncoderSessions: true);

        Assert.False(actual.UseGpu);
        Assert.False(actual.ConcurrentEncoderSessions);
        Assert.True(actual.TenBit);
        Assert.Equal("veryslow", actual.Preset);
        Assert.Equal(51, actual.QualityValue);
    }

    [Fact]
    public void UnsupportedTenBitRequestFailsBeforeEncoding()
    {
        ResolvedVideoEncoder encoder = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx264,
            VideoCodecFamily.H264);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            EncodingRequestValidator.ValidateAndNormalize(
                EncoderRegistry.Default, encoder.Selection, false, null, "slow", 23,
                tenBit: true, audioChannels: null, concurrentEncoderSessions: false));

        Assert.Contains("Requested: H264 10-bit", error.Message);
    }

    [Fact]
    public void MismatchedStableCodecIsRejected()
    {
        var invalid = new VideoEncoderSelection(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc,
            "hevc_nvenc");

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() =>
                EncodingRequestValidator.ValidateAndNormalize(
                    EncoderRegistry.Default,
                    invalid,
                    useGpu: false,
                    targetMb: null,
                    preset: "medium",
                    qualityValue: 24,
                    tenBit: false,
                    audioChannels: null,
                    concurrentEncoderSessions: false));

        Assert.Contains("must use FFmpeg codec 'libx265'", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void InvalidAudioChannelCountIsRejected(int audioChannels)
    {
        ResolvedVideoEncoder encoder = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EncodingRequestValidator.ValidateAndNormalize(
                EncoderRegistry.Default,
                encoder.Selection,
                useGpu: false,
                targetMb: null,
                preset: "medium",
                qualityValue: 24,
                tenBit: false,
                audioChannels,
                concurrentEncoderSessions: false));
    }

    [Fact]
    public void MissingFfmpegEncoderIsRejectedBeforeExecution()
    {
        ResolvedVideoEncoder encoder = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);
        var capabilities = new FfmpegEncoderCapabilities(
            "ffmpeg.exe",
            inspectionSucceeded: true,
            ["hevc_nvenc", "libx264"],
            errorMessage: null);

        NotSupportedException error =
            Assert.Throws<NotSupportedException>(() =>
                EncodingRequestValidator.EnsureEncoderAvailable(
                    encoder.Selection,
                    capabilities));

        Assert.Contains("'libx265'", error.Message);
    }

    [Fact]
    public void FailedCapabilityInspectionDoesNotCreateFalseNegative()
    {
        ResolvedVideoEncoder encoder = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);
        var capabilities = new FfmpegEncoderCapabilities(
            "ffmpeg.exe",
            inspectionSucceeded: false,
            [],
            "Inspection failed.");

        EncodingRequestValidator.EnsureEncoderAvailable(
            encoder.Selection,
            capabilities);
    }

    [Fact]
    public void AvailabilityFiltersEncodersAndCodecFamilies()
    {
        var capabilities = new FfmpegEncoderCapabilities(
            "ffmpeg.exe",
            inspectionSucceeded: true,
            [
                "h264_nvenc",
                "hevc_nvenc",
                "h264_qsv",
                "hevc_qsv",
                "libx264",
                "libsvtav1"
            ],
            errorMessage: null);

        IReadOnlyList<EncoderCapabilities> encoders =
            EncoderAvailability.GetAvailableEncoders(
                EncoderRegistry.Default,
                capabilities);
        EncoderCapabilities qsv = Assert.Single(
            encoders,
            item => item.Id == VideoEncoderIds.Qsv);

        Assert.DoesNotContain(
            encoders,
            item => item.Id == VideoEncoderIds.Libx265);
        Assert.Equal(
            [VideoCodecFamily.H264, VideoCodecFamily.Hevc],
            EncoderAvailability.GetAvailableCodecs(
                EncoderRegistry.Default,
                qsv,
                capabilities));
    }
}
