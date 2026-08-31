using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class VideoRestorationPipelineTests
{
    [Fact]
    public void OffProducesNoFilterChain() => Assert.Equal("", VideoRestorationPipeline.BuildFilterChain(new VideoRestorationSettings(), EncodingService.ScaleMode.None));

    [Theory]
    [InlineData(VideoRestorationPreset.VintageAnimationLight)]
    [InlineData(VideoRestorationPreset.VintageAnimationRestore)]
    [InlineData(VideoRestorationPreset.DvdAnimationRestore)]
    [InlineData(VideoRestorationPreset.VhsTvCaptureRestore)]
    public void PresetsProduceDeterministicChains(VideoRestorationPreset preset)
    {
        string first = VideoRestorationPipeline.BuildFilterChain(new VideoRestorationSettings { Preset = preset }, EncodingService.ScaleMode.None);
        Assert.NotEmpty(first);
        Assert.Equal(first, VideoRestorationPipeline.BuildFilterChain(new VideoRestorationSettings { Preset = preset }, EncodingService.ScaleMode.None));
    }

    [Fact]
    public void CustomResizeConflictsWithNormalEncodeResize()
    {
        var settings = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, Resize = VideoRestorationResize.To1080p };
        Assert.Throws<ArgumentException>(() => VideoRestorationPipeline.BuildFilterChain(settings, EncodingService.ScaleMode.To720p));
    }

    [Fact]
    public void LegacyJobJsonDefaultsRestorationToOff()
    {
        EncodeJobSettings settings = JsonSerializer.Deserialize<EncodeJobSettings>("{}")!;
        Assert.NotNull(settings.Restoration);
        Assert.Equal(VideoRestorationPreset.Off, settings.Restoration.Preset);
    }

    [Fact]
    public void JobSettingsCloneKeepsIndependentRestorationSnapshot()
    {
        var settings = new EncodeJobSettings { Restoration = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, Denoise = VideoRestorationStrength.Medium } };
        EncodeJobSettings clone = settings.Clone(); settings.Restoration.Denoise = VideoRestorationStrength.Off;
        Assert.Equal(VideoRestorationStrength.Medium, clone.Restoration.Denoise);
    }
}
