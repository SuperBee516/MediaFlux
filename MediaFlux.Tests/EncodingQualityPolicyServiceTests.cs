using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingQualityPolicyServiceTests
{
    private readonly EncodingQualityPolicyService _service = new();

    [Theory]
    [InlineData(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc")]
    [InlineData(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265")]
    [InlineData(VideoEncoderIds.Qsv, VideoCodecFamily.Hevc, "hevc_qsv")]
    public void QualityTargetsAreMonotonicForEverySupportedBackend(
        string encoderId, VideoCodecFamily codec, string ffmpegCodec)
    {
        var values = Enum.GetValues<QualityTarget>()
            .Select(target => Resolve(target, Source(1920, 1080, 30, 8_000_000),
                new VideoEncoderSelection(encoderId, codec, ffmpegCodec)).EffectiveQuality!.Value)
            .ToArray();

        Assert.Equal(values.OrderByDescending(value => value), values);
    }

    [Fact]
    public void ResolutionIsDeterministicAndUsesMeasuredSourceCharacteristics()
    {
        VideoEncoderSelection encoder = new(VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc, "libx265");
        EncodingQualityResolution first = Resolve(QualityTarget.Balanced,
            Source(1920, 1080, 30, 8_000_000), encoder);
        EncodingQualityResolution again = Resolve(QualityTarget.Balanced,
            Source(1920, 1080, 30, 8_000_000), encoder);
        EncodingQualityResolution compressed = Resolve(QualityTarget.Balanced,
            Source(1280, 720, 30, 500_000), encoder);

        Assert.Equal(first.EffectiveQuality, again.EffectiveQuality);
        Assert.Equal(first.Assessment, again.Assessment);
        Assert.Equal(first.Reasons, again.Reasons);
        Assert.Equal(EncodingQualityAssessment.HighQualitySource, first.Assessment);
        Assert.Equal(EncodingQualityAssessment.CompressedSource, compressed.Assessment);
        Assert.True(first.EffectiveQuality < compressed.EffectiveQuality);
    }

    [Fact]
    public void DownscaleAdjustsQualityAndTargetSizeSupersedesIt()
    {
        VideoEncoderSelection encoder = new(VideoEncoderIds.Nvenc,
            VideoCodecFamily.Hevc, "hevc_nvenc");
        MediaProbeResult source = Source(3840, 2160, 30, 24_000_000);
        EncodingQualityResolution original = Resolve(QualityTarget.Balanced, source, encoder);
        EncodingQualityResolution downscaled = _service.Resolve(new(
            EncodingQualityIntent.Automatic(QualityTarget.Balanced), source, encoder,
            new VideoOutputGeometryPlan(3840, 2160, 1280, 720, 1280, 720,
                "1280:720", "nv12", 2, "test"),
            EncodingService.ScaleMode.To720p, null));
        EncodingQualityResolution targetSize = _service.Resolve(new(
            EncodingQualityIntent.Automatic(QualityTarget.Balanced), source, encoder,
            null, EncodingService.ScaleMode.None, 500));

        Assert.True(downscaled.EffectiveQuality > original.EffectiveQuality);
        Assert.True(targetSize.IsSupersededByTargetSize);
        Assert.Null(targetSize.EffectiveQuality);
        Assert.Contains(targetSize.Reasons, reason =>
            reason.Code == EncodingQualityReasonCode.TargetSizeSupersedesQuality);
    }

    [Fact]
    public void LegacyNumericIntentRetainsExistingProviderNormalization()
    {
        VideoEncoderSelection encoder = new(VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc, "libx265");

        EncodingQualityResolution resolution = _service.Resolve(new(
            EncodingQualityIntent.LegacyNumeric(80), Source(1920, 1080, 30, 4_000_000),
            encoder, null, EncodingService.ScaleMode.None, null));

        Assert.Equal(51, resolution.EffectiveQuality);
        Assert.Equal(EncodingQualityIntentKind.LegacyNumeric, resolution.Intent.Kind);
        Assert.Contains(resolution.Reasons, reason =>
            reason.Code == EncodingQualityReasonCode.LegacyNumericIntent);
    }

    [Fact]
    public void MechanismReflectsExistingProviderContract()
    {
        MediaProbeResult source = Source(1920, 1080, 30, 4_000_000);

        Assert.Equal(EncoderQualityMechanism.Cq, Resolve(QualityTarget.Balanced, source,
            new(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc")).Mechanism);
        Assert.Equal(EncoderQualityMechanism.Crf, Resolve(QualityTarget.Balanced, source,
            new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265")).Mechanism);
        Assert.Equal(EncoderQualityMechanism.Icq, Resolve(QualityTarget.Balanced, source,
            new(VideoEncoderIds.Qsv, VideoCodecFamily.Hevc, "hevc_qsv")).Mechanism);
    }

    [Theory]
    [InlineData(VideoEncoderIds.Libx265, "libx265", 24)]
    [InlineData(VideoEncoderIds.Nvenc, "hevc_nvenc", 24)]
    [InlineData(VideoEncoderIds.Qsv, "hevc_qsv", 19)]
    public void BalancedCalibrationMatrixDifferentiatesRepresentativeSources(
        string encoderId, string ffmpegCodec, int expectedTypicalBaseline)
    {
        VideoEncoderSelection encoder = new(encoderId, VideoCodecFamily.Hevc, ffmpegCodec);
        var profiles = new[]
        {
            (Name: "low-bitrate-720p", Source: Source(1280, 720, 30, 500_000)),
            (Name: "high-bitrate-720p", Source: Source(1280, 720, 30, 20_000_000)),
            (Name: "low-density-1080p", Source: Source(1920, 1080, 30, 2_000_000)),
            (Name: "high-density-1080p", Source: Source(1920, 1080, 30, 6_000_000)),
            (Name: "4k", Source: Source(3840, 2160, 30, 24_000_000)),
            (Name: "unusually-high-density", Source: Source(1920, 1080, 30, 40_000_000)),
            (Name: "unusually-low-density", Source: Source(1920, 1080, 30, 500_000))
        };

        EncodingQualityResolution[] results = profiles
            .Select(profile => Resolve(QualityTarget.Balanced, profile.Source, encoder))
            .ToArray();

        Assert.All(results, result =>
        {
            Assert.InRange(result.EffectiveQuality!.Value, 0, 51);
            Assert.NotEqual(EncodingQualityAssessment.Unknown, result.Assessment);
        });
        Assert.Equal(expectedTypicalBaseline + 1, results[0].EffectiveQuality);
        Assert.Equal(expectedTypicalBaseline - 2, results[1].EffectiveQuality);
        Assert.Equal(expectedTypicalBaseline + 1, results[2].EffectiveQuality);
        Assert.Equal(expectedTypicalBaseline, results[3].EffectiveQuality);
        Assert.Equal(expectedTypicalBaseline - 1, results[4].EffectiveQuality);
        Assert.Equal(expectedTypicalBaseline - 2, results[5].EffectiveQuality);
        Assert.Equal(expectedTypicalBaseline + 1, results[6].EffectiveQuality);
    }

    [Fact]
    public void PathologicalMetadataRemainsFiniteDeterministicAndBounded()
    {
        VideoEncoderSelection encoder = new(VideoEncoderIds.Nvenc,
            VideoCodecFamily.Hevc, "hevc_nvenc");
        var sources = new[]
        {
            Source(1, 1, 0.001, 1), Source(7680, 4320, 240, long.MaxValue),
            Source(8192, 4320, 24, 1_000_000), Source(1920, 1080, 0, 8_000_000),
            Source(0, 0, 0, 0)
        };

        foreach (MediaProbeResult source in sources)
        {
            EncodingQualityResolution first = Resolve(QualityTarget.Balanced, source, encoder);
            EncodingQualityResolution second = Resolve(QualityTarget.Balanced, source, encoder);
            Assert.Equal(first.Intent, second.Intent);
            Assert.Equal(first.EffectiveQuality, second.EffectiveQuality);
            Assert.Equal(first.Mechanism, second.Mechanism);
            Assert.Equal(first.Assessment, second.Assessment);
            Assert.Equal(first.IsSupersededByTargetSize, second.IsSupersededByTargetSize);
            Assert.Equal(first.Reasons, second.Reasons);
            Assert.InRange(first.EffectiveQuality!.Value, 0, 51);
        }
    }

    private EncodingQualityResolution Resolve(
        QualityTarget target, MediaProbeResult source, VideoEncoderSelection encoder) =>
        _service.Resolve(new(EncodingQualityIntent.Automatic(target), source, encoder,
            null, EncodingService.ScaleMode.None, null));

    private static MediaProbeResult Source(int width, int height, double fps, long bitrate) =>
        new()
        {
            Success = true,
            Streams = [new MediaProbeStreamInfo
            {
                CodecType = "video", CodecName = "h264", Width = width,
                Height = height, FrameRate = fps, BitRate = bitrate
            }]
        };
}
