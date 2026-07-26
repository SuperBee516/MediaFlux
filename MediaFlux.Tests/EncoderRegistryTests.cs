using MediaFlux.Models;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncoderRegistryTests
{
    [Theory]
    [InlineData("h264_nvenc", VideoEncoderIds.Nvenc, VideoCodecFamily.H264)]
    [InlineData("hevc_nvenc", VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc)]
    [InlineData("av1_nvenc", VideoEncoderIds.Nvenc, VideoCodecFamily.Av1)]
    [InlineData("h264_qsv", VideoEncoderIds.Qsv, VideoCodecFamily.H264)]
    [InlineData("hevc_qsv", VideoEncoderIds.Qsv, VideoCodecFamily.Hevc)]
    [InlineData("av1_qsv", VideoEncoderIds.Qsv, VideoCodecFamily.Av1)]
    [InlineData("libx264", VideoEncoderIds.Libx264, VideoCodecFamily.H264)]
    [InlineData("libx265", VideoEncoderIds.Libx265, VideoCodecFamily.Hevc)]
    [InlineData("libsvtav1", VideoEncoderIds.SvtAv1, VideoCodecFamily.Av1)]
    public void LegacyCodecResolutionUsesStableEncoderIdentity(
        string ffmpegCodec,
        string expectedEncoderId,
        VideoCodecFamily expectedCodecFamily)
    {
        ResolvedVideoEncoder resolved =
            EncoderRegistry.Default.ResolveLegacyCodec(ffmpegCodec);

        Assert.Equal(expectedEncoderId, resolved.Selection.EncoderId);
        Assert.Equal(expectedCodecFamily, resolved.Selection.CodecFamily);
        Assert.Equal(ffmpegCodec, resolved.Selection.FfmpegCodec);
    }

    [Theory]
    [InlineData(VideoCodecFamily.H264, "h264_nvenc")]
    [InlineData(VideoCodecFamily.Hevc, "hevc_nvenc")]
    [InlineData(VideoCodecFamily.Av1, "av1_nvenc")]
    public void StableNvencSelectionResolvesCodec(
        VideoCodecFamily codecFamily,
        string expectedFfmpegCodec)
    {
        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Nvenc,
            codecFamily);

        Assert.Equal(expectedFfmpegCodec, resolved.Selection.FfmpegCodec);
        Assert.True(resolved.Provider.Capabilities.IsHardware);
    }

    [Fact]
    public void Libx265DefinitionIsHevcOnly()
    {
        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);

        Assert.Equal("libx265", resolved.Selection.FfmpegCodec);
        Assert.True(resolved.Provider.Capabilities.SupportsTenBit);
        Assert.Throws<InvalidOperationException>(() =>
            EncoderRegistry.Default.Resolve(
                VideoEncoderIds.Libx265,
                VideoCodecFamily.H264));
    }

    [Fact]
    public void Libx265DefinitionContainsFullPresetRange()
    {
        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);

        Assert.Equal(
            [
                "ultrafast",
                "superfast",
                "veryfast",
                "faster",
                "fast",
                "medium",
                "slow",
                "slower",
                "veryslow",
                "placebo"
            ],
            resolved.Provider.Capabilities.Presets.Select(
                item => item.Value));
        Assert.Equal(
            new EncoderQualityRange("CRF", 0, 51),
            resolved.Provider.Capabilities.QualityRange);
    }

    [Theory]
    [InlineData("ULTRAFAST", "ultrafast")]
    [InlineData(" medium ", "medium")]
    [InlineData("placebo", "placebo")]
    [InlineData("p5", "slow")]
    [InlineData("", "slow")]
    public void Libx265PresetNormalizationProducesValidToken(
        string requested,
        string expected)
    {
        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);

        Assert.Equal(
            expected,
            resolved.Provider.NormalizePreset(requested));
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(27, 27)]
    [InlineData(99, 51)]
    public void Libx265CrfNormalizationClampsToValidRange(
        int? requested,
        int expected)
    {
        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc);

        Assert.Equal(
            expected,
            resolved.Provider.NormalizeQuality(
                VideoCodecFamily.Hevc,
                requested));
    }

    [Fact]
    public void NvencDefinitionContainsAllNativePresetTokens()
    {
        EncoderCapabilities nvenc = EncoderRegistry.Default
            .GetCapabilities()
            .Single(item => item.Id == VideoEncoderIds.Nvenc);

        Assert.Equal(
            ["p1", "p2", "p3", "p4", "p5", "p6", "p7"],
            nvenc.Presets.Select(item => item.Value));
        Assert.Equal(
            [
                "Fastest (p1)",
                "Faster (p2)",
                "Fast (p3)",
                "Medium (p4)",
                "Slow (p5)",
                "Slower (p6)",
                "Slowest (p7)"
            ],
            nvenc.Presets.Select(item => item.DisplayName));
        Assert.Equal("p5", nvenc.DefaultPreset);
    }
}
