using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncoderConfigPersistenceTests : IDisposable
{
    private readonly string _root;

    public EncoderConfigPersistenceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-EncoderConfigTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void LegacyConfigKeepsNvencAndMigratesFriendlyPreset()
    {
        string path = Path.Combine(_root, "legacy.json");
        File.WriteAllText(
            path,
            """
            {
              "LastEncodingSpeedPreset": "High Quality (Slower)"
            }
            """);

        Config config = Config.Load(path);

        Assert.Equal(VideoEncoderIds.Nvenc, config.LastEncoderId);
        Assert.Equal(
            nameof(VideoCodecFamily.Hevc),
            config.LastVideoCodec);
        Assert.Equal("p6", config.LastEncoderPreset);
    }

    [Fact]
    public void Libx265SelectionRoundTripsWithPresetAndQuality()
    {
        string path = Path.Combine(_root, "config.json");
        var config = new Config
        {
            LastEncoderId = VideoEncoderIds.Libx265,
            LastVideoCodec = nameof(VideoCodecFamily.Hevc),
            LastEncoderPreset = "veryslow",
            LastQualityValue = 27
        };

        config.Save(path);
        Config loaded = Config.Load(path);

        Assert.Equal(VideoEncoderIds.Libx265, loaded.LastEncoderId);
        Assert.Equal(
            nameof(VideoCodecFamily.Hevc),
            loaded.LastVideoCodec);
        Assert.Equal("veryslow", loaded.LastEncoderPreset);
        Assert.Equal(27, loaded.LastQualityValue);
    }

    [Fact]
    public void UnknownEncoderFallsBackToNvenc()
    {
        string path = Path.Combine(_root, "unknown.json");
        File.WriteAllText(
            path,
            """
            {
              "LastEncoderId": "not-an-encoder",
              "LastVideoCodec": "Hevc",
              "LastEncoderPreset": "unexpected"
            }
            """);

        Config config = Config.Load(path);

        Assert.Equal(VideoEncoderIds.Nvenc, config.LastEncoderId);
    }

    [Fact]
    public void NamedPresetRoundTripsStableEncoderSelection()
    {
        string path = Path.Combine(_root, "encoding-presets.json");
        var service = new EncodingPresetService(path);
        service.SaveOrReplace(new EncodingPreset
        {
            Name = "CPU archival",
            EncoderId = VideoEncoderIds.Libx265,
            VideoCodec = nameof(VideoCodecFamily.Hevc),
            EncoderPreset = "slower",
            QualityValue = 25,
            TenBit = true
        });

        EncodingPreset loaded = Assert.Single(service.LoadAll());
        string json = File.ReadAllText(path);

        Assert.Equal(VideoEncoderIds.Libx265, loaded.EncoderId);
        Assert.Equal(nameof(VideoCodecFamily.Hevc), loaded.VideoCodec);
        Assert.Equal("slower", loaded.EncoderPreset);
        Assert.Equal(25, loaded.QualityValue);
        Assert.True(loaded.TenBit);
        Assert.DoesNotContain("\"NvencPreset\"", json);
        Assert.DoesNotContain("\"DualNvenc\"", json);
    }

    [Fact]
    public void LegacyNamedPresetStillDeserializes()
    {
        string path = Path.Combine(_root, "legacy-presets.json");
        File.WriteAllText(
            path,
            """
            [
              {
                "Name": "Old CPU HEVC",
                "EncoderMode": "CPU (libx264)",
                "VideoFormat": "H.265 / HEVC (x265)",
                "NvencPreset": "Balanced (Recommended)"
              }
            ]
            """);

        EncodingPreset loaded =
            Assert.Single(new EncodingPresetService(path).LoadAll());
        VideoCodecFamily codec =
            VideoEncoderCompatibility.ParseCodecFamily(loaded.VideoFormat);

        Assert.Equal(
            VideoEncoderIds.Libx265,
            VideoEncoderCompatibility.ResolveEncoderId(
                loaded.EncoderMode,
                codec));
        Assert.Equal("", loaded.EncoderId);
        Assert.Equal("", loaded.EncoderPreset);
    }

    [Theory]
    [InlineData("CPU (libx264)", "H.264 (x264)", VideoEncoderIds.Libx264)]
    [InlineData("CPU (libx264)", "H.265 / HEVC (x265)", VideoEncoderIds.Libx265)]
    [InlineData("CPU (libx264)", "AV1", VideoEncoderIds.SvtAv1)]
    public void LegacyCpuSelectionMapsBySavedFormat(
        string legacyEncoder,
        string legacyFormat,
        string expectedEncoderId)
    {
        VideoCodecFamily family =
            VideoEncoderCompatibility.ParseCodecFamily(legacyFormat);

        string actual = VideoEncoderCompatibility.ResolveEncoderId(
            legacyEncoder,
            family);

        Assert.Equal(expectedEncoderId, actual);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
