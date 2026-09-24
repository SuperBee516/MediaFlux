using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FreshInstallEncodingDefaultsTests
{
    [Fact]
    public void MissingConfigUsesRequestedFreshInstallEncodingDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mediaflux-fresh-{Guid.NewGuid():N}.json");
        try
        {
            Config config = Config.Load(path);

            Assert.Equal(VideoEncoderIds.Nvenc, config.LastEncoderId);
            Assert.Equal(nameof(VideoCodecFamily.Hevc), config.LastVideoCodec);
            Assert.True(config.LastChkAutoTargetSize);
            Assert.Equal("p5", config.LastEncoderPreset);
            Assert.Equal("Automatic", config.LastQualityMode);
            Assert.Equal(nameof(QualityTarget.Balanced), config.LastQualityTarget);
            Assert.False(config.LastChkProcessAll);
            Assert.Equal(nameof(OutputContainerSelection.Auto), config.LastOutputContainer);
            Assert.Equal(nameof(ContainerCompatibilityPolicy.Intelligent), config.ContainerCompatibilityPolicy);
            Assert.True(config.ShowX264Files);
            Assert.False(config.ShowX265Files);
            Assert.False(config.ShowAv1Files);
            Assert.True(config.ShowOtherCodecFiles);
            Assert.Equal(VideoRestorationMode.Off, config.VideoRestoration.Mode);
            Assert.Equal(VideoRestorationPreset.Off, config.VideoRestoration.Preset);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExistingConfigValuesOverrideFreshInstallDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mediaflux-existing-{Guid.NewGuid():N}.json");
        try
        {
            new Config
            {
                LastEncoderId = VideoEncoderIds.Libx265,
                LastVideoCodec = nameof(VideoCodecFamily.Hevc),
                LastChkAutoTargetSize = false,
                LastEncoderPreset = "fast",
                LastQualityMode = "Manual",
                LastQualityTarget = nameof(QualityTarget.HighQuality),
                LastChkProcessAll = true,
                LastOutputContainer = nameof(OutputContainerSelection.Matroska),
                ContainerCompatibilityPolicy = nameof(ContainerCompatibilityPolicy.Strict),
                ShowX264Files = false,
                ShowX265Files = true,
                ShowAv1Files = true,
                ShowOtherCodecFiles = false
            }.Save(path);

            Config loaded = Config.Load(path);

            Assert.Equal(VideoEncoderIds.Libx265, loaded.LastEncoderId);
            Assert.False(loaded.LastChkAutoTargetSize);
            Assert.Equal("fast", loaded.LastEncoderPreset);
            Assert.Equal("Manual", loaded.LastQualityMode);
            Assert.Equal(nameof(QualityTarget.HighQuality), loaded.LastQualityTarget);
            Assert.True(loaded.LastChkProcessAll);
            Assert.Equal(nameof(OutputContainerSelection.Matroska), loaded.LastOutputContainer);
            Assert.Equal(nameof(ContainerCompatibilityPolicy.Strict), loaded.ContainerCompatibilityPolicy);
            Assert.False(loaded.ShowX264Files);
            Assert.True(loaded.ShowX265Files);
            Assert.True(loaded.ShowAv1Files);
            Assert.False(loaded.ShowOtherCodecFiles);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
