namespace MediaFlux.Services.LibraryCatalog
{
    public enum DuplicateAnalysisStatus
    {
        Running = 0,
        Completed = 1,
        Canceled = 2,
        Failed = 3,
        Interrupted = 4
    }

    public enum DuplicateCleanupAction
    {
        RecycleBin = 0,
        Quarantine = 1,
        PermanentDelete = 2
    }

    public enum DuplicateCleanupStatus
    {
        Draft = 0,
        Ready = 1,
        Running = 2,
        Completed = 3,
        Failed = 4
    }

    public enum DuplicateCleanupItemStatus
    {
        Planned = 0,
        Validated = 1,
        Succeeded = 2,
        Excluded = 3,
        Failed = 4
    }

    public enum LibraryHashKind
    {
        QuickFingerprint,
        FullSha256
    }

    public sealed record DuplicateAnalysisHandle(long RunId, DateTime StartedUtc);

    public sealed record DuplicateAnalysisCompletion(
        DuplicateAnalysisStatus Status,
        long SizeCandidates,
        long QuickHashed,
        long FullHashed,
        long ExactGroups,
        long ErrorCount,
        string ErrorText = "");

    public sealed record LibraryHashCandidate(
        long FileId,
        string FullPath,
        string PathKey,
        long SizeBytes,
        DateTime LastWriteUtc,
        string VolumeId,
        string FileIdentity);

    public sealed record LibraryFileHashFact(
        long FileId,
        long SourceSizeBytes,
        DateTime SourceLastWriteUtc,
        string SourceVolumeId,
        string SourceFileIdentity,
        string QuickAlgorithm,
        int QuickVersion,
        byte[]? QuickHash,
        DateTime? QuickCompletedUtc,
        string FullAlgorithm,
        int FullVersion,
        byte[]? FullHash,
        DateTime? FullCompletedUtc,
        int FailureCount,
        string ErrorMessage);

    public sealed record LibraryHashWrite(LibraryHashCandidate Candidate, byte[]? Hash, string ErrorMessage = "");

    public sealed record DuplicateGroupQuery(
        string Search = "",
        long? LocationId = null,
        string Codec = "",
        string ResolutionTier = "",
        bool? Reviewed = null,
        bool? Ignored = null,
        bool? Protected = null,
        string SortColumn = "reclaimable",
        bool Descending = true,
        int Offset = 0,
        int Limit = 100);

    public sealed record ExactDuplicateGroupRecord(
        long GroupId,
        long SizeBytes,
        string FullAlgorithm,
        int FullVersion,
        byte[] FullHash,
        int MemberCount,
        int PhysicalCopyCount,
        long ReclaimableBytes,
        long? SuggestedKeeperFileId,
        long? ManualKeeperFileId,
        bool Reviewed,
        bool Ignored,
        int ProtectedMemberCount,
        string VideoCodec,
        string ResolutionTier);

    public sealed record ExactDuplicateGroupPage(
        long TotalCount,
        IReadOnlyList<ExactDuplicateGroupRecord> Groups);

    public sealed record ExactDuplicateMemberRecord(
        long GroupId,
        long FileId,
        string FullPath,
        string PathKey,
        string LocationPath,
        long SizeBytes,
        DateTime LastWriteUtc,
        string VolumeId,
        string FileIdentity,
        string PhysicalIdentityKey,
        bool IsHardLinkAlias,
        IndexedFileAvailability Availability,
        string VideoCodec,
        int? Width,
        int? Height,
        long? TotalBitRate,
        double? DurationSeconds,
        bool IsProtected,
        bool IsSuggestedKeeper,
        bool IsManualKeeper);

    public sealed record DuplicateGroupDecision(
        long GroupId,
        long? ManualKeeperFileId,
        bool Reviewed,
        bool Ignored);

    public sealed record LibraryStatisticBucket(string Label, long FileCount, long SizeBytes);

    public sealed record LibraryLargestFile(
        long FileId,
        string FileName,
        string FullPath,
        long SizeBytes,
        string VideoCodec,
        string ResolutionTier);

    public sealed record LibraryStatistics(
        long TotalFiles,
        long TotalBytes,
        long PresentFiles,
        long MissingFiles,
        long UnavailableFiles,
        long ProbeSucceeded,
        long ProbePending,
        long ProbeFailed,
        long ExactDuplicateGroups,
        long ExactDuplicateFiles,
        long ExactDuplicateBytes,
        long ReclaimableDuplicateBytes,
        IReadOnlyList<LibraryStatisticBucket> ByLocation,
        IReadOnlyList<LibraryStatisticBucket> ByCodec,
        IReadOnlyList<LibraryStatisticBucket> ByResolution,
        IReadOnlyList<LibraryStatisticBucket> ByContainer,
        IReadOnlyList<LibraryStatisticBucket> ByDynamicRange,
        IReadOnlyList<LibraryLargestFile> LargestFiles);

    public sealed record DuplicateCleanupPlanRecord(
        long PlanId,
        DuplicateCleanupAction Action,
        DuplicateCleanupStatus Status,
        string QuarantineRoot,
        DateTime CreatedUtc,
        DateTime? CompletedUtc,
        string ErrorText,
        IReadOnlyList<DuplicateCleanupPlanItemRecord> Items);

    public sealed record DuplicateCleanupPlanItemRecord(
        long PlanId,
        long GroupId,
        long FileId,
        long KeeperFileId,
        string SourcePath,
        string SourcePathKey,
        long SourceSizeBytes,
        DateTime SourceLastWriteUtc,
        string SourceVolumeId,
        string SourceFileIdentity,
        byte[] FullHash,
        DuplicateCleanupItemStatus Status,
        string DestinationPath,
        string ValidationError);

    public interface ILibraryAnalysisCatalog
    {
        string CreateUserDataBackup(string? destinationPath = null);
        LibraryUserDataRestoreResult RestoreUserDataBackup(string sourcePath);
        DuplicateAnalysisHandle BeginDuplicateAnalysis(
            string quickAlgorithm,
            int quickVersion,
            string fullAlgorithm,
            int fullVersion);
        void CompleteDuplicateAnalysis(DuplicateAnalysisHandle run, DuplicateAnalysisCompletion completion);
        int RecoverInterruptedDuplicateWork();
        long CountSizeCandidates();
        IReadOnlyList<LibraryHashCandidate> GetQuickHashCandidates(int quickVersion, int limit);
        IReadOnlyList<LibraryHashCandidate> GetFullHashCandidates(int quickVersion, int fullVersion, int limit);
        LibraryFileHashFact? GetFileHashFact(long fileId);
        void SaveQuickHash(LibraryHashCandidate candidate, string algorithm, int version, byte[] hash);
        void SaveFullHash(LibraryHashCandidate candidate, string algorithm, int version, byte[] hash);
        void SaveHashFailure(LibraryHashCandidate candidate, string message);
        void SaveHashBatch(IReadOnlyCollection<LibraryHashWrite> writes, LibraryHashKind kind, string algorithm, int version);
        long RebuildExactDuplicateGroups(DuplicateAnalysisHandle run, string fullAlgorithm, int fullVersion);
        IReadOnlyList<long> GetDuplicateGroupIds(long analysisRunId, long afterGroupId, int limit);
        ExactDuplicateGroupPage QueryDuplicateGroups(DuplicateGroupQuery query);
        ExactDuplicateGroupRecord? GetDuplicateGroup(long groupId);
        IReadOnlyList<ExactDuplicateMemberRecord> GetDuplicateGroupMembers(long groupId);
        void SetSuggestedKeeper(long groupId, long? fileId);
        void SaveDuplicateDecision(DuplicateGroupDecision decision);
        void SetFileProtection(long fileId, bool isProtected, string reason = "");
        LibraryStatistics GetLibraryStatistics(int topCount = 10);
        long CreateCleanupPlan(
            DuplicateCleanupAction action,
            string quarantineRoot,
            IReadOnlyCollection<DuplicateCleanupPlanItemRecord> items);
        DuplicateCleanupPlanRecord? GetCleanupPlan(long planId);
        void UpdateCleanupPlanItem(
            long planId,
            long fileId,
            DuplicateCleanupItemStatus status,
            string destinationPath,
            string validationError);
        void CompleteCleanupPlan(long planId, DuplicateCleanupStatus status, string errorText = "");
        void AppendCleanupAudit(
            long planId,
            long fileId,
            string sourcePath,
            string destinationPath,
            DuplicateCleanupAction action,
            DuplicateCleanupItemStatus outcome,
            string message);
    }
}
