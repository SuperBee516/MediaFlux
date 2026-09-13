using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class QueueAnalysisPresentationTests
{
    [Theory]
    [InlineData(SmartEncodeRecommendationKind.StrongCandidate, "Strong candidate")]
    [InlineData(SmartEncodeRecommendationKind.ModerateCandidate, "Moderate candidate")]
    [InlineData(SmartEncodeRecommendationKind.Review, "Review")]
    [InlineData(SmartEncodeRecommendationKind.Skip, "Skip")]
    public void AvailableAnalysisPresentsExistingRecommendationKinds(
        SmartEncodeRecommendationKind kind,
        string displayName)
    {
        QueueAnalysisPresentation presentation = QueueAnalysisPresentation.Create(
            Recommendation(kind), 1_024, 600);

        Assert.True(presentation.IsAvailable);
        Assert.Equal(displayName, presentation.Recommendation);
        Assert.Equal("High", presentation.Confidence);
        Assert.Equal("1 GB → 600 MB", presentation.EstimatedResult);
    }

    [Fact]
    public void ReasonsRemainIndividualAndTooltipUsesTheSamePresentation()
    {
        SmartEncodeRecommendation recommendation = Recommendation(
            SmartEncodeRecommendationKind.Review,
            reasons: ["First reason.", "Second reason."]);
        QueueAnalysisPresentation presentation = QueueAnalysisPresentation.Create(recommendation);

        Assert.Equal(["First reason.", "Second reason."], presentation.Reasons);
        Assert.Equal(recommendation.BuildTooltip(), presentation.BuildTooltip());
        Assert.Contains("• First reason.", presentation.BuildTooltip());
        Assert.Contains("• Second reason.", presentation.BuildTooltip());
    }

    [Fact]
    public void NegativeSavingsUseIncreaseLanguage()
    {
        QueueAnalysisPresentation presentation = QueueAnalysisPresentation.Create(
            Recommendation(SmartEncodeRecommendationKind.Review, -6.7, -57.7));

        Assert.Equal("Estimated increase", presentation.SavingsLabel);
        Assert.Equal("6.7% (+57.7 MB)", presentation.SavingsValue);
        Assert.Contains("Estimated increase: 6.7% (+57.7 MB)", presentation.BuildTooltip());
    }

    [Fact]
    public void PositiveAndZeroSavingsRemainSavings()
    {
        QueueAnalysisPresentation positive = QueueAnalysisPresentation.Create(
            Recommendation(SmartEncodeRecommendationKind.StrongCandidate, 35, 680));
        QueueAnalysisPresentation zero = QueueAnalysisPresentation.Create(
            Recommendation(SmartEncodeRecommendationKind.Skip, 0, 0));

        Assert.Equal("Estimated savings", positive.SavingsLabel);
        Assert.Equal("35% (680 MB)", positive.SavingsValue);
        Assert.Equal("Estimated savings", zero.SavingsLabel);
        Assert.Equal("0% (0 MB)", zero.SavingsValue);
    }

    [Fact]
    public void NotYetAnalyzedStateDoesNotFabricateRecommendation()
    {
        QueueAnalysisPresentation presentation = QueueAnalysisPresentation.Create(null);

        Assert.False(presentation.IsAvailable);
        Assert.Equal("Queue analysis has not been performed for this file.", presentation.Status);
        Assert.Null(presentation.Recommendation);
        Assert.Empty(presentation.Reasons);
    }

    [Fact]
    public void SeparateSelectedRowsDoNotRetainPreviousAnalysis()
    {
        QueueAnalysisPresentation analyzed = QueueAnalysisPresentation.Create(
            Recommendation(SmartEncodeRecommendationKind.StrongCandidate, 35, 680), 1_000, 320);
        QueueAnalysisPresentation notAnalyzed = QueueAnalysisPresentation.Create(null);

        Assert.Equal("Strong candidate", analyzed.Recommendation);
        Assert.False(notAnalyzed.IsAvailable);
        Assert.Null(notAnalyzed.Recommendation);
        Assert.Equal("Queue analysis has not been performed for this file.", notAnalyzed.Status);
    }

    private static SmartEncodeRecommendation Recommendation(
        SmartEncodeRecommendationKind kind,
        double savingsPercent = 35,
        double savingsMb = 680,
        IReadOnlyList<string>? reasons = null) => new()
    {
        Kind = kind,
        Confidence = SmartEncodeConfidence.High,
        EstimatedSavingsPercent = savingsPercent,
        EstimatedSavingsMb = savingsMb,
        PrimaryReason = "First reason.",
        Reasons = reasons ?? ["First reason."]
    };
}
