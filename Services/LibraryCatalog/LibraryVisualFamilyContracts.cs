namespace MediaFlux.Services.LibraryCatalog;

public sealed record VisualFamilyRecord(
    long FamilyId,
    string FamilyKey,
    int MemberCount,
    double MinimumConfidence,
    long ReclaimableBytes,
    long? SuggestedKeeperFileId,
    long? ManualKeeperFileId,
    bool Reviewed,
    bool Ignored,
    LibraryMatchEligibilityState Eligibility,
    string EligibilityReason);

public sealed record VisualFamilyPage(long TotalCount, IReadOnlyList<VisualFamilyRecord> Families);

public sealed record VisualFamilyQuery(
    bool? Reviewed = null,
    bool? Ignored = null,
    int Offset = 0,
    int Limit = 100,
    bool IncludeInactive = false);

public sealed record VisualFamilyMemberRecord(
    long FamilyId,
    long FileId,
    string FullPath,
    string LocationPath,
    long SizeBytes,
    DateTime LastWriteUtc,
    IndexedFileAvailability Availability,
    string VideoCodec,
    int? Width,
    int? Height,
    long? TotalBitRate,
    double? DurationSeconds,
    bool IsProtected,
    bool IsSuggestedKeeper,
    bool IsManualKeeper,
    bool IsHdr,
    string AudioSummary,
    double MinimumMemberConfidence,
    double? FrameRate = null);

public sealed record VisualFamilyEdgeRecord(
    long FamilyId,
    long VisualGroupId,
    long LeftFileId,
    long RightFileId,
    double Confidence,
    string EvidenceText);

public sealed record VisualFamilyDecision(
    long FamilyId,
    long? ManualKeeperFileId,
    bool Reviewed,
    bool Ignored,
    string BatchId = "",
    string Source = "library-analyzer-family");

public sealed record VisualFamilyConstructionResult(
    int FamiliesCreated,
    int AmbiguousComponents,
    int EligibleEdges,
    int LargestComponent,
    TimeSpan Elapsed);

public sealed record VisualFamilyCleanupProposal(
    VisualFamilyRecord Family,
    VisualFamilyMemberRecord Keeper,
    IReadOnlyList<VisualCleanupProposalItem> Items,
    int ExcludedMembers,
    long ReclaimableBytes);

public interface ILibraryVisualFamilyCatalog
{
    VisualFamilyConstructionResult RebuildVisualFamilies(double minimumConfidence = 76, int maximumComponentSize = 128, int maximumCliques = 10_000);
    VisualFamilyPage QueryVisualFamilies(VisualFamilyQuery query);
    VisualFamilyRecord? GetVisualFamily(long familyId);
    IReadOnlyList<VisualFamilyMemberRecord> GetVisualFamilyMembers(long familyId);
    IReadOnlyList<VisualFamilyEdgeRecord> GetVisualFamilyEdges(long familyId);
    void SetVisualFamilySuggestedKeeper(long familyId, long? fileId);
    void SaveVisualFamilyDecision(VisualFamilyDecision decision);
    bool IsFileInActiveCleanupPlan(long fileId);
}
