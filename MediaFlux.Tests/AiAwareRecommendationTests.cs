using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiAwareRecommendationTests
{
    [Fact]
    public void LowResolutionArtifactedAnimationCanRecommendAiPreview()
    {
        var analysis = new VideoRestorationAnalysisResult { SourcePath = "a", Width = 640, Height = 480, AnimationHint = true, Noise = RestorationEvidenceLevel.Moderate };
        var context = new AiRecommendationContext(new[] { Model("anime", AiRestorationMode.Animation, AiRestorationScale.X2) });
        var result = VideoRestorationRecommendationService.Recommend(analysis, true, aiContext: context);
        Assert.Equal(AiRecommendationOutcome.AiWorthPreviewing, result.AiOutcome); Assert.Equal(AiRestorationScale.X2, result.Settings.AiScale); Assert.False(result.IsPreviewTested);
    }
    [Fact]
    public void CompatibleModelAloneDoesNotRecommendAi()
    {
        var result = VideoRestorationRecommendationService.Recommend(new VideoRestorationAnalysisResult { SourcePath = "a", Width = 1920, Height = 1080, AnimationHint = true }, true, aiContext: new AiRecommendationContext(new[] { Model("anime", AiRestorationMode.Animation, AiRestorationScale.X2) }));
        Assert.NotEqual(AiRecommendationOutcome.AiWorthPreviewing, result.AiOutcome);
    }
    [Fact]
    public void SeverePreviewIsDiscouraged()
    {
        var severe = new TemporalQualityResult(TemporalStability.SevereInstability, 70, 0, 0, 0, 0, 0, 0, "test");
        var result = VideoRestorationRecommendationService.Recommend(new VideoRestorationAnalysisResult { SourcePath = "a" }, false, aiContext: new AiRecommendationContext(Array.Empty<AiRestorationModel>(), CurrentTemporalQuality: severe));
        Assert.Equal(AiRecommendationOutcome.CurrentAiDiscouraged, result.AiOutcome); Assert.True(result.RequiresManualConfirmation);
    }
    [Fact]
    public void CuratedCandidatesRejectUnsupportedScale()
    {
        var valid = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "anime", AiScale = AiRestorationScale.X2 };
        var invalid = valid.Clone(); invalid.AiScale = AiRestorationScale.X4;
        Assert.Single(AiConfigurationComparisonService.ValidateCuratedCandidates(new[] { valid, invalid }, new[] { Model("anime", AiRestorationMode.Animation, AiRestorationScale.X2) }));
    }
    private static AiRestorationModel Model(string id, AiRestorationMode category, params AiRestorationScale[] scales) => new(id, id, category, scales, "models", "a.param", "a.bin", "ncnn-vulkan");
}
