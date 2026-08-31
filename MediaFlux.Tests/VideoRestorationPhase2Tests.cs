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
}
