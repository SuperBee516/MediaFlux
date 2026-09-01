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

    [Fact]
    public void AiX2OriginalFinalResolutionReturnsToSourceExactlyOnce()
    {
        var settings = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, AiMode = AiRestorationMode.Animation, AiScale = AiRestorationScale.X2 };
        VideoOutputResolutionPlan final = VideoRestorationPipeline.ResolveFinalOutputResolution(640, 480, settings, EncodingService.ScaleMode.None);
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(settings, EncodingService.ScaleMode.None, final.ScaleFilter);

        Assert.Equal((640, 480), (final.Width, final.Height));
        Assert.Equal(1, plan.PostAiFilterChain.Split("scale=", StringSplitOptions.None).Length - 1);
        Assert.Contains("scale=640:480:flags=lanczos", plan.PostAiFilterChain);
    }

    [Fact]
    public void AiX2ExplicitLargerFinalResolutionUsesRequestedResizeWithoutDoubleScaling()
    {
        var settings = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, AiMode = AiRestorationMode.Animation, AiScale = AiRestorationScale.X2, Resize = VideoRestorationResize.To720p };
        VideoOutputResolutionPlan final = VideoRestorationPipeline.ResolveFinalOutputResolution(640, 480, settings, EncodingService.ScaleMode.None);
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(settings, EncodingService.ScaleMode.None, null);

        Assert.Equal((960, 720), (final.Width, final.Height));
        Assert.Equal(1, plan.PostAiFilterChain.Split("scale=", StringSplitOptions.None).Length - 1);
        Assert.Contains("scale=-2:720:flags=lanczos", plan.PostAiFilterChain);
    }

    [Fact]
    public void AspectPreservingFinalResolutionIsDerivedWithoutAiScale()
    {
        var settings = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, AiMode = AiRestorationMode.Animation, AiScale = AiRestorationScale.X2, Resize = VideoRestorationResize.Custom, CustomWidth = 1000, CustomHeight = 1000, PreserveAspectRatio = true };
        VideoOutputResolutionPlan final = VideoRestorationPipeline.ResolveFinalOutputResolution(640, 480, settings, EncodingService.ScaleMode.None);

        Assert.Equal((1000, 750), (final.Width, final.Height));
        Assert.Contains("force_original_aspect_ratio=decrease", final.ScaleFilter);
    }
}
