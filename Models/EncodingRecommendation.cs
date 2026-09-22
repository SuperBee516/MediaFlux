namespace MediaFlux.Models;

public enum EncodingRecommendationKind { Encode, Skip, Review }
public enum EncodingRecommendationRisk { Unknown, Low, Medium, High }

/// <summary>Deterministic, advisory decision derived from one frozen encoding plan.</summary>
public sealed record EncodingRecommendation(
    EncodingRecommendationKind Recommendation,
    string PrimaryReason,
    IReadOnlyList<string> SupportingReasons,
    EncodingHistoricalConfidence Confidence,
    double? EstimatedOutputSizeMb,
    double? ExpectedSavingsMb,
    double? ExpectedSavingsPercent,
    EncodingRecommendationRisk QualityRisk,
    IReadOnlyList<EncodingPlanItem> Facts)
{
    public string DisplayName => Recommendation.ToString();
}
