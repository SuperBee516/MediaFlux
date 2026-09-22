using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingRecommendationServiceTests
{
    [Fact]
    public void MeaningfulReliableSavingsRecommendEncode()
    {
        EncodingRecommendation result = EncodingRecommendationService.Evaluate(Plan(100, 60, EncodingHistoricalConfidence.High));
        Assert.Equal(EncodingRecommendationKind.Encode, result.Recommendation);
        Assert.InRange(result.ExpectedSavingsPercent!.Value, 39.9, 40.1);
    }

    [Fact]
    public void NegligibleSavingsRecommendSkip()
    {
        EncodingRecommendation result = EncodingRecommendationService.Evaluate(Plan(100, 99, EncodingHistoricalConfidence.High, targetCodec: "h264"));
        Assert.Equal(EncodingRecommendationKind.Skip, result.Recommendation);
    }

    [Fact]
    public void LargerOutputAndInsufficientEvidenceRemainReview()
    {
        Assert.Equal(EncodingRecommendationKind.Review, EncodingRecommendationService.Evaluate(Plan(100, 120, EncodingHistoricalConfidence.High)).Recommendation);
        Assert.Equal(EncodingRecommendationKind.Review, EncodingRecommendationService.Evaluate(Plan(100, null, EncodingHistoricalConfidence.None)).Recommendation);
    }

    [Fact]
    public void LowBitrateSourceIsReviewEvenWithPredictedSavings()
    {
        EncodingPlan plan = Plan(100, 40, EncodingHistoricalConfidence.High, 40, 3840, 2160);
        Assert.Equal(EncodingRecommendationKind.Review, EncodingRecommendationService.Evaluate(plan).Recommendation);
    }

    [Fact]
    public void RecommendationIsDeterministicAndFrozenOnPlanCreation()
    {
        EncodingPlan first = Plan(100, 60, EncodingHistoricalConfidence.High);
        EncodingRecommendation a = EncodingRecommendationService.Evaluate(first);
        EncodingRecommendation b = EncodingRecommendationService.Evaluate(first);
        Assert.Equal(a.Recommendation, b.Recommendation);
        Assert.Equal(a.PrimaryReason, b.PrimaryReason);
        Assert.Equal(a.ExpectedSavingsPercent, b.ExpectedSavingsPercent);
    }

    private static EncodingPlan Plan(double sourceMb, double? outputMb, EncodingHistoricalConfidence confidence, double bitrate = 8_000, int width = 1920, int height = 1080, string targetCodec = "hevc")
    {
        var plan = new EncodingPlan
        {
            IsAvailable = true,
            Source = new EncodingPlanSource("h264", width, height, 30, 600) { SizeBytes = (long)(sourceMb * 1048576), BitrateKbps = bitrate },
            Video = new EncodingPlanVideo("Reencode", targetCodec, targetCodec == "h264" ? "libx264" : "libx265", width, height, width, height, null),
            Estimates = new EncodingPlanEstimates(null, null, outputMb is > 0 ? outputMb / sourceMb : null,
                new EncodingHistoricalPrediction(10, 1, confidence, null, null, null, null, null, null, outputMb, null, null, null, "test"))
        };
        return plan;
    }
}
