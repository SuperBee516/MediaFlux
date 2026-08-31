using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class VideoRestorationPhase2Tests
{
    [Fact]
    public void DvdAnimationEvidenceGetsConservativeDvdRecommendation()
    {
        var analysis = new VideoRestorationAnalysisResult { SourcePath = "dvd.mpg", Width = 720, Height = 480, Codec = "mpeg2video", Blocking = RestorationEvidenceLevel.Moderate };
        VideoRestorationRecommendation recommendation = VideoRestorationRecommendationService.Recommend(analysis, encodeHintAnimation: true);
        Assert.Equal(VideoRestorationPreset.DvdAnimationRestore, recommendation.Settings.Preset);
    }

    [Fact]
    public void UncertainSourceRemainsOff()
    {
        var analysis = new VideoRestorationAnalysisResult { SourcePath = "unknown.mkv", Codec = "h264" };
        VideoRestorationRecommendation recommendation = VideoRestorationRecommendationService.Recommend(analysis, encodeHintAnimation: false);
        Assert.Equal(VideoRestorationPreset.Off, recommendation.Settings.Preset);
        Assert.Equal(0, recommendation.Confidence);
    }

    [Fact]
    public void ExplicitUserSettingWinsOverRecommendation()
    {
        var chosen = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, Denoise = VideoRestorationStrength.Strong };
        VideoRestorationRecommendation recommendation = VideoRestorationRecommendationService.Recommend(new VideoRestorationAnalysisResult { SourcePath = "source.mkv" }, true, chosen);
        Assert.Equal(VideoRestorationPreset.Custom, recommendation.Settings.Preset);
        Assert.Equal(VideoRestorationStrength.Strong, recommendation.Settings.Denoise);
    }

    [Fact]
    public void MarkdownReleaseNotesBecomeReadableText()
    {
        string text = ReleaseNotesFormatter.Format("## What's New\n\n- **Restoration** [details](https://example.com)");
        Assert.Contains("What's New", text);
        Assert.Contains("• Restoration details", text);
        Assert.DoesNotContain("##", text);
    }

    [Fact]
    public void PictureConditionSamplingNeedsEnoughFrames()
    {
        var result = VideoRestorationPictureConditionSampling.Classify(new[] { new VideoRestorationFrameMetrics(.1, .1, .1, .1) });
        Assert.Equal(RestorationEvidenceLevel.Unknown, result.Noise);
    }

    [Fact]
    public void PictureConditionSamplingClassifiesConsistentNoisyBlockedWindows()
    {
        var samples = Enumerable.Range(0, 3).Select(_ => new VideoRestorationFrameMetrics(.08, .2, .03, .25)).ToArray();
        var result = VideoRestorationPictureConditionSampling.Classify(samples);
        Assert.Equal(RestorationEvidenceLevel.High, result.Noise);
        Assert.Equal(RestorationEvidenceLevel.High, result.Banding);
        Assert.Equal(RestorationEvidenceLevel.High, result.Blocking);
    }

    [Fact]
    public void CleanAnimationDoesNotGetRestoration()
    {
        var analysis = new VideoRestorationAnalysisResult { SourcePath = "clean.mkv", AnimationHint = true, Noise = RestorationEvidenceLevel.Low, Blocking = RestorationEvidenceLevel.Low, Banding = RestorationEvidenceLevel.Low };
        Assert.Equal(VideoRestorationPreset.Off, VideoRestorationRecommendationService.Recommend(analysis, true).Settings.Preset);
    }

    [Fact]
    public void KnownMissingRestorationFilterFailsBeforeCommandGeneration()
    {
        VideoRestorationPipeline.SetAvailableFilters(new[] { "hqdn3d" });
        Assert.Throws<NotSupportedException>(() => VideoRestorationPipeline.ValidateAvailable(new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, Deband = VideoRestorationStrength.Light }));
        VideoRestorationPipeline.ClearAvailableFilters();
    }

    [Fact]
    public void FilterInventoryParsesRealisticFfmpegRowsFromEitherStream()
    {
        string stdout = " TS deband V->V Debands video.\n T. deblock V->V Deblock video.";
        string stderr = " TS hqdn3d V->V Apply a High Quality 3D Denoiser.\n TS unsharp V->V Sharpen or blur the input video.";
        HashSet<string> filters = FfmpegRestorationCapabilityService.ParseFilters(stdout, stderr);
        Assert.Contains("deband", filters); Assert.Contains("deblock", filters); Assert.Contains("hqdn3d", filters); Assert.Contains("unsharp", filters);
    }

    [Fact]
    public void UnknownInventoryDoesNotClaimFiltersAreUnavailable()
    {
        var inventory = new FfmpegRestorationCapabilities("ffmpeg.exe", "unknown", new HashSet<string>(), FfmpegFilterInventoryState.Unknown, -1, 0);
        Assert.Equal(FfmpegFilterAvailability.Unknown, inventory.GetAvailability("hqdn3d"));
    }

    [Fact]
    public void CredibleInventoryCanConfirmAbsence()
    {
        var inventory = new FfmpegRestorationCapabilities("ffmpeg.exe", "test", new HashSet<string> { "hqdn3d" }, FfmpegFilterInventoryState.Available, 0, 300);
        Assert.Equal(FfmpegFilterAvailability.Unavailable, inventory.GetAvailability("deband"));
    }
}
