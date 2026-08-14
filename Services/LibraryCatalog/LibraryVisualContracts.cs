namespace MediaFlux.Services.LibraryCatalog
{
    public enum VisualFingerprintStatus
    {
        Pending = 0,
        InProgress = 1,
        Succeeded = 2,
        Failed = 3
    }

    public sealed record VisualAnalysisHandle(long RunId, DateTime StartedUtc);

    public sealed record VisualAnalysisCompletion(
        DuplicateAnalysisStatus Status,
        long EligibleFiles,
        long FingerprintedFiles,
        long CandidatePairs,
        long MatchPairs,
        long ErrorCount,
        string ErrorText = "");

    public sealed record VisualFingerprintCandidate(
        long FileId,
        string FullPath,
        string PathKey,
        long SizeBytes,
        DateTime LastWriteUtc,
        string VolumeId,
        string FileIdentity,
        double DurationSeconds);

    public sealed record VisualFingerprintFact(
        long FileId,
        long SourceSizeBytes,
        DateTime SourceLastWriteUtc,
        string SourceVolumeId,
        string SourceFileIdentity,
        string Algorithm,
        int AlgorithmVersion,
        IReadOnlyList<ulong> FrameHashes,
        VisualFingerprintStatus Status,
        int AttemptCount,
        string ToolVersion,
        string ErrorMessage);

    public sealed record VisualFingerprintWrite(
        VisualFingerprintCandidate Candidate,
        IReadOnlyList<ulong> FrameHashes,
        string ToolVersion,
        string ErrorMessage = "");

    public sealed record VisualCandidatePair(
        long LeftFileId,
        long RightFileId,
        int BandMatches,
        VisualFingerprintFact LeftFingerprint,
        VisualFingerprintFact RightFingerprint,
        double LeftDurationSeconds,
        double RightDurationSeconds);

    public sealed record VisualMatchWrite(
        long LeftFileId,
        long RightFileId,
        double ConfidenceScore,
        int FrameMatches,
        int FrameComparisons,
        double AverageHashDistance,
        double DurationDeltaSeconds,
        string EvidenceText);

    public sealed record VisualGroupQuery(
        long? GroupId = null,
        string Search = "",
        long? LocationId = null,
        bool? Reviewed = null,
        bool? Ignored = null,
        bool? NotMatch = null,
        bool? CodecDiffers = null,
        bool? ResolutionDiffers = null,
        double MinimumConfidence = 0,
        string SortColumn = "confidence",
        bool Descending = true,
        int Offset = 0,
        int Limit = 100,
        bool IncludeInactive = false,
        bool IncludeFamilyPairs = false);

    public sealed record VisualSimilarityGroupRecord(
        long GroupId,
        string GroupKey,
        double ConfidenceScore,
        int FrameMatches,
        int FrameComparisons,
        double AverageHashDistance,
        double DurationDeltaSeconds,
        string EvidenceText,
        long LeftFileId,
        long RightFileId,
        long? SuggestedKeeperFileId,
        long? ManualKeeperFileId,
        bool Reviewed,
        bool Ignored,
        bool NotMatch,
        bool CodecDiffers,
        bool ResolutionDiffers,
        long ReclaimableBytes,
        LibraryMatchEligibilityState Eligibility = LibraryMatchEligibilityState.Active,
        string EligibilityReason = "");

    public sealed record VisualSimilarityGroupPage(
        long TotalCount,
        IReadOnlyList<VisualSimilarityGroupRecord> Groups);

    public sealed record VisualSimilarityMemberRecord(
        long GroupId,
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
        bool IsHdr = false,
        string AudioSummary = "",
        double? FrameRate = null);

    public sealed record VisualGroupDecision(
        long GroupId,
        long? ManualKeeperFileId,
        bool Reviewed,
        bool Ignored,
        bool NotMatch = false,
        string BatchId = "",
        string Source = "library-analyzer");

    public enum VisualCleanupIntent
    {
        DeleteCandidate = 0,
        DeleteBoth = 1
    }

    public sealed record VisualCleanupPlanItemRecord(
        long PlanId,
        string GroupKey,
        long GroupId,
        long FileId,
        long KeeperFileId,
        string SourcePath,
        long SourceSizeBytes,
        DateTime SourceLastWriteUtc,
        string SourceVolumeId,
        string SourceFileIdentity,
        string KeeperPath,
        long KeeperSizeBytes,
        DateTime KeeperLastWriteUtc,
        string KeeperVolumeId,
        string KeeperFileIdentity,
        double ConfidenceScore,
        byte[]? ExactHash,
        VisualCleanupIntent Intent,
        DuplicateCleanupItemStatus Status,
        string DestinationPath,
        string ValidationError,
        long? FamilyId = null);

    public sealed record VisualCleanupPlanRecord(
        long PlanId,
        DuplicateCleanupAction Action,
        DuplicateCleanupStatus Status,
        string QuarantineRoot,
        bool AllowUnreviewed,
        double MinimumConfidence,
        DateTime CreatedUtc,
        DateTime? CompletedUtc,
        string ErrorText,
        IReadOnlyList<VisualCleanupPlanItemRecord> Items);

    public sealed record VisualCleanupLocationSummary(
        string LocationPath,
        long FileCount,
        long ReclaimableBytes);

    public sealed record VisualCleanupPlanSummary(
        long PlanId,
        DuplicateCleanupAction Action,
        DuplicateCleanupStatus Status,
        string QuarantineRoot,
        bool AllowUnreviewed,
        double MinimumConfidence,
        DateTime CreatedUtc,
        DateTime? CompletedUtc,
        string ErrorText,
        long TotalItems,
        long TotalFamilies,
        long KeeperCount,
        long PlannedItems,
        long SucceededItems,
        long ExcludedItems,
        long FailedItems,
        long PlannedBytes,
        long ReclaimedBytes,
        IReadOnlyList<VisualCleanupLocationSummary> Locations);

    public interface ILibraryVisualCatalog
    {
        VisualAnalysisHandle BeginVisualAnalysis(string algorithm, int algorithmVersion);
        void CompleteVisualAnalysis(VisualAnalysisHandle run, VisualAnalysisCompletion completion);
        int RecoverInterruptedVisualWork();
        long CountVisualFingerprintCandidates(int algorithmVersion, string toolVersion);
        IReadOnlyList<VisualFingerprintCandidate> GetVisualFingerprintCandidates(int algorithmVersion, string toolVersion, int limit);
        VisualFingerprintFact? GetVisualFingerprint(long fileId);
        void SaveVisualFingerprintBatch(IReadOnlyCollection<VisualFingerprintWrite> writes, string algorithm, int algorithmVersion);
        long BuildVisualCandidatePairs(VisualAnalysisHandle run, int algorithmVersion, int maximumBandBucket, int minimumBandMatches);
        IReadOnlyList<VisualCandidatePair> GetVisualCandidatePairs(long runId, long afterLeftFileId, long afterRightFileId, int limit);
        void PrepareVisualSimilarityGroups(VisualAnalysisHandle run);
        void AppendVisualSimilarityGroups(VisualAnalysisHandle run, IReadOnlyCollection<VisualMatchWrite> matches);
        void PublishVisualSimilarityGroups(VisualAnalysisHandle run);
        VisualSimilarityGroupPage QueryVisualGroups(VisualGroupQuery query);
        VisualSimilarityGroupRecord? GetVisualGroup(long groupId);
        VisualSimilarityGroupRecord? GetVisualGroupByKey(string groupKey);
        IReadOnlyList<VisualSimilarityMemberRecord> GetVisualGroupMembers(long groupId);
        void SetVisualSuggestedKeeper(long groupId, long? fileId);
        void SaveVisualDecision(VisualGroupDecision decision);
        long CreateVisualCleanupPlan(DuplicateCleanupAction action, string quarantineRoot, bool allowUnreviewed,
            double minimumConfidence, IReadOnlyCollection<VisualCleanupPlanItemRecord> items);
        long BeginVisualCleanupPlan(DuplicateCleanupAction action, string quarantineRoot, bool allowUnreviewed,
            double minimumConfidence);
        void AppendVisualCleanupPlanItems(long planId, IReadOnlyCollection<VisualCleanupPlanItemRecord> items);
        void MarkVisualCleanupPlanReady(long planId);
        void MarkVisualCleanupPlanRunning(long planId);
        int RecoverInterruptedVisualCleanupPlans();
        VisualCleanupPlanSummary? GetVisualCleanupPlanSummary(long planId, bool includeLocations = true);
        IReadOnlyList<VisualCleanupPlanItemRecord> GetVisualCleanupPlanItemsBatch(
            long planId, long afterGroupId, long afterFileId, int limit,
            DuplicateCleanupItemStatus? status = null);
        VisualCleanupPlanRecord? GetVisualCleanupPlan(long planId);
        void UpdateVisualCleanupPlanItem(long planId, long fileId, DuplicateCleanupItemStatus status, string destinationPath, string validationError);
        void CompleteVisualCleanupPlan(long planId, DuplicateCleanupStatus status, string errorText = "");
        void AppendVisualCleanupAudit(long planId, long fileId, string sourcePath, string destinationPath,
            DuplicateCleanupAction action, DuplicateCleanupItemStatus outcome, string message);
    }

    public sealed record LibraryScanAcceleratorState(
        long LocationId,
        string AcceleratorKind,
        string VolumeIdentity,
        string FileSystemName,
        long JournalId,
        long NextUsn,
        long LowestValidUsn,
        DateTime LastAuthoritativeScanUtc,
        string StatusMessage);

    public interface ILibraryScanAccelerationCatalog
    {
        LibraryScanAcceleratorState? GetScanAcceleratorState(long locationId);
        void SaveScanAcceleratorState(LibraryScanAcceleratorState state);
        void ClearScanAcceleratorState(long locationId);
    }

    public sealed record LibraryUserDataRestoreResult(
        int DuplicateDecisions,
        int FileProtections,
        int VisualDecisions,
        IReadOnlyList<string> Warnings,
        int DecisionEvents = 0,
        int FamilyDecisions = 0);
}
