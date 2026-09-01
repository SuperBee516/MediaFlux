using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class RestorationModeTests
{
    [Fact]
    public void OffDisablesAllConventionalAndAiRestoration()
    {
        var settings = AdvancedAi();
        settings.Mode = VideoRestorationMode.Off;

        VideoRestorationSettings resolved = VideoRestorationModeResolver.Resolve(settings);
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(resolved, EncodingService.ScaleMode.None);

        Assert.Equal(VideoRestorationPreset.Off, resolved.Preset);
        Assert.Equal(AiRestorationMode.Off, resolved.AiMode);
        Assert.False(plan.UsesAi);
        Assert.Equal("", plan.ConventionalFilterChain);
    }

    [Fact]
    public void AutoUsesRecommendationAndIgnoresAdvancedSettings()
    {
        var settings = AdvancedAi();
        settings.Mode = VideoRestorationMode.Auto;
        settings.AutoRecommendation = new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationLight };

        VideoRestorationSettings resolved = VideoRestorationModeResolver.Resolve(settings);

        Assert.Equal(VideoRestorationPreset.VintageAnimationLight, resolved.Preset);
        Assert.Equal(AiRestorationMode.Off, resolved.AiMode);
        Assert.NotEqual(settings.Denoise, resolved.Denoise);
    }

    [Fact]
    public void CustomUsesExactAdvancedSettings()
    {
        var settings = AdvancedAi();
        settings.Mode = VideoRestorationMode.Custom;

        VideoRestorationSettings resolved = VideoRestorationModeResolver.Resolve(settings);

        Assert.Equal(AiRestorationMode.General, resolved.AiMode);
        Assert.Equal("anime-v3", resolved.AiModelId);
        Assert.Equal(AiRestorationScale.X2, resolved.AiScale);
        Assert.Equal(VideoRestorationStrength.Strong, resolved.Denoise);
    }

    [Fact]
    public void AdvancedSettingsPersistAcrossOffAndAutoModeChanges()
    {
        var settings = AdvancedAi();
        VideoRestorationSettings saved = settings.Clone();
        settings.Mode = VideoRestorationMode.Off;
        Assert.Equal(AiRestorationMode.General, settings.AiMode);
        settings.Mode = VideoRestorationMode.Auto;
        Assert.Equal("anime-v3", settings.AiModelId);
        settings.Mode = VideoRestorationMode.Custom;

        Assert.True(VideoRestorationPreviewSelection.Equivalent(saved, settings));
    }

    [Fact]
    public void PreviewResolverFollowsTheSameOffAndAutoRules()
    {
        var off = AdvancedAi(); off.Mode = VideoRestorationMode.Off;
        Assert.False(VideoRestorationModeResolver.Resolve(off).AiMode != AiRestorationMode.Off);

        var auto = AdvancedAi(); auto.Mode = VideoRestorationMode.Auto; auto.AutoRecommendation = new VideoRestorationSettings { Preset = VideoRestorationPreset.DvdAnimationRestore };
        Assert.Equal(VideoRestorationPreset.DvdAnimationRestore, VideoRestorationModeResolver.Resolve(auto).Preset);
    }

    [Fact]
    public void SavedAndScheduledJobSnapshotRetainsMasterModeAndAdvancedSettings()
    {
        var job = new EncodeJob { Settings = new EncodeJobSettings { Restoration = AdvancedAi() } };
        job.Settings.Restoration.Mode = VideoRestorationMode.Custom;
        EncodeJob snapshot = EncodeJobService.CreateExecutionSnapshot(job);
        job.Settings.Restoration.Mode = VideoRestorationMode.Off;

        Assert.Equal(VideoRestorationMode.Custom, snapshot.Settings.Restoration.Mode);
        Assert.Equal(AiRestorationMode.General, snapshot.Settings.Restoration.AiMode);
    }

    [Theory]
    [InlineData(VideoRestorationMode.Off, false, false, false, false, "All restoration disabled.")]
    [InlineData(VideoRestorationMode.Auto, true, true, true, false, "Analyze / Recommend controls restoration.")]
    [InlineData(VideoRestorationMode.Custom, true, true, true, true, "Using Advanced restoration settings.")]
    public void GuiControlStateFollowsMasterMode(VideoRestorationMode mode, bool analyze, bool preview, bool apply, bool advanced, string status)
    {
        RestorationModeControlState state = VideoRestorationModeResolver.ControlState(mode);
        Assert.Equal((analyze, preview, apply, advanced, status), (state.AnalyzeEnabled, state.PreviewEnabled, state.ApplyEnabled, state.AdvancedEnabled, state.StatusText));
    }

    private static VideoRestorationSettings AdvancedAi() => new()
    {
        Mode = VideoRestorationMode.Custom,
        Preset = VideoRestorationPreset.Custom,
        Denoise = VideoRestorationStrength.Strong,
        Deblock = VideoRestorationStrength.Medium,
        AiMode = AiRestorationMode.General,
        AiModelId = "anime-v3",
        AiScale = AiRestorationScale.X2
    };
}
