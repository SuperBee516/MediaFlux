namespace MediaFlux.Services.LibraryCatalog;

public sealed record LibraryVisualReviewAutomationOptions(
    bool SemiAutomaticKeeperApproval = false,
    int MaximumMassReviewMatches = 100,
    double MinimumAutomationMargin = 15,
    double MinimumVisualConfidence = 95)
{
    public LibraryVisualReviewAutomationOptions Normalize() => this with
    {
        MaximumMassReviewMatches = Math.Clamp(MaximumMassReviewMatches, 1, 1_000),
        MinimumAutomationMargin = Math.Clamp(MinimumAutomationMargin, 0, 100),
        MinimumVisualConfidence = Math.Clamp(MinimumVisualConfidence, 76, 100)
    };
}

public sealed record LibraryKeeperExplanation(
    long? RecommendedKeeperFileId,
    double Score,
    double Margin,
    bool RequiresReview,
    string Summary,
    IReadOnlyList<string> Factors);

public sealed record LibraryMassReviewPreviewItem(
    long GroupId,
    string GroupKey,
    double Confidence,
    long KeeperFileId,
    string KeeperPath,
    double Score,
    double Margin,
    string Explanation,
    IReadOnlyList<VisualSimilarityMemberRecord> Members,
    bool Included = true,
    string ExclusionReason = "");

public sealed record LibraryMassReviewPreview(
    string BatchId,
    LibraryVisualReviewAutomationOptions Options,
    IReadOnlyList<LibraryMassReviewPreviewItem> EligibleItems,
    IReadOnlyList<LibraryMassReviewPreviewItem> ExcludedItems);

public sealed record LibraryMassReviewApplyResult(
    string BatchId,
    int Applied,
    int Excluded,
    IReadOnlyList<string> Messages);

public sealed record LibraryCleanupRecommendationCategory(
    string Name,
    string SafetyLabel,
    int MatchCount,
    long ReclaimableBytes,
    string Description);

public sealed record LibraryCleanupRecommendationDashboard(
    IReadOnlyList<LibraryCleanupRecommendationCategory> Categories,
    DateTime CalculatedUtc);

public sealed record LibraryStorageOptimizationCandidate(
    long FileId,
    string FullPath,
    string VideoCodec,
    int? Width,
    int? Height,
    long SizeBytes,
    long? TotalBitRate,
    double? DurationSeconds,
    bool IsHdr,
    double OpportunityScore,
    string Rationale);

public interface ILibraryPhase2Catalog
{
    IReadOnlyList<LibraryStorageOptimizationCandidate> QueryStorageOptimizationCandidates(int limit = 500);
}
