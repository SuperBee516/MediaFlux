namespace MediaFlux.Services.LibraryCatalog
{
    public enum LibraryMatchEligibilityState
    {
        Active = 0,
        SuspectedMissing = 1,
        Missing = 2,
        Unavailable = 3,
        StaleEvidence = 4,
        Retired = 5
    }

    public enum LibraryPresenceObservationState
    {
        Present = 0,
        SuspectedMissing = 1,
        ConfirmedMissing = 2,
        Unavailable = 3,
        AccessFailure = 4,
        MovedOrRenamed = 5,
        StaleEvidence = 6
    }

    [Flags]
    public enum LibraryReanalysisWork
    {
        None = 0,
        Metadata = 1,
        ExactHash = 2,
        VisualFingerprint = 4,
        All = Metadata | ExactHash | VisualFingerprint
    }

    public enum LibraryReanalysisStatus
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Failed = 3
    }

    public enum LibraryDecisionTargetKind
    {
        ExactGroup = 0,
        VisualGroup = 1,
        FileProtection = 2,
        Cleanup = 3,
        Batch = 4,
        VisualFamily = 5
    }

    public enum LibraryDecisionEventKind
    {
        KeeperChanged = 0,
        ReviewedChanged = 1,
        IgnoredChanged = 2,
        NotMatchChanged = 3,
        ProtectionChanged = 4,
        BatchApplied = 5,
        CleanupRestored = 6
    }

    public enum LibraryHealthIssueKind
    {
        SuspectedMissing,
        Missing,
        MovedOrRenamed,
        UnavailableLocation,
        AccessFailure,
        ProbeFailure,
        StaleMetadata,
        StaleExactEvidence,
        StaleVisualEvidence,
        FailedScan,
        FailedAnalysis,
        StaleDuplicateRecord,
        UnresolvedCleanup,
        ReanalysisFailure,
        CatalogIntegrity,
        RestorableQuarantine
    }

    public enum LibraryHealthSeverity
    {
        Information,
        Warning,
        Error
    }

    public sealed record LibraryPresenceObservation(
        long LocationId,
        long FileId,
        LibraryPresenceObservationState State,
        int ConsecutiveObservations,
        long? RelatedFileId,
        string Source,
        string Details,
        DateTime LastObservedUtc);

    public sealed record LibraryMatchEligibility(
        LibraryMatchEligibilityState State,
        string Reason,
        long? RelatedFileId = null)
    {
        public bool IsActive => State == LibraryMatchEligibilityState.Active;
    }

    public sealed record LibraryReanalysisItem(
        long Id,
        long FileId,
        string FullPath,
        LibraryReanalysisWork Work,
        LibraryReanalysisStatus Status,
        int AttemptCount,
        int MaximumAttempts,
        string BatchId,
        string ErrorText,
        DateTime? NextAttemptUtc,
        DateTime CreatedUtc,
        DateTime UpdatedUtc);

    public sealed record LibraryDecisionEvent(
        long Id,
        LibraryDecisionTargetKind TargetKind,
        string TargetKey,
        LibraryDecisionEventKind EventKind,
        string BeforeState,
        string AfterState,
        string BatchId,
        string Source,
        long? ReversalOfEventId,
        long? ReversedByEventId,
        DateTime OccurredUtc)
    {
        public bool CanUndo => !ReversalOfEventId.HasValue && !ReversedByEventId.HasValue;
    }

    public sealed record LibraryDecisionUndoResult(bool Succeeded, long? ReversalEventId, string Message);

    public sealed record LibraryHealthIssue(
        string Key,
        LibraryHealthIssueKind Kind,
        LibraryHealthSeverity Severity,
        string Title,
        string Details,
        string RecommendedAction,
        long? FileId = null,
        long? GroupId = null,
        long? LocationId = null,
        LibraryReanalysisWork SuggestedReanalysis = LibraryReanalysisWork.None,
        long? CleanupAuditId = null);

    public sealed record LibraryHealthSnapshot(
        IReadOnlyList<LibraryHealthIssue> Issues,
        LibraryCatalogIntegrityResult Integrity,
        DateTime CheckedUtc);

    public sealed record LibraryMaintenanceResult(
        int RecoveredQueueItems,
        int PrunedExactGroups,
        int PrunedAnalysisRuns,
        LibraryCatalogCheckpointResult Checkpoint,
        LibraryCatalogIntegrityResult Integrity);

    public sealed record LibraryQuarantineRestoreItem(
        bool IsVisual,
        long AuditId,
        long PlanId,
        long FileId,
        string SourcePath,
        string DestinationPath,
        long SourceSizeBytes,
        DateTime SourceLastWriteUtc,
        string SourceVolumeId,
        string SourceFileIdentity,
        byte[]? ExactHash);

    public interface ILibraryRecoveryCatalog
    {
        IReadOnlyList<LibraryPresenceObservation> GetPresenceObservations(long fileId);
        void RecordPresenceObservation(long locationId, long fileId, LibraryPresenceObservationState state,
            string source, string details = "", long? relatedFileId = null);
        void MarkFileRemovedByCleanup(long fileId, string expectedPath, string reason);
        void MarkFileRestoredFromQuarantine(long fileId, string expectedPath);
        void RestoreLocationAfterVerifiedNoChanges(long locationId);
        void SetVisualMatchLifecycle(long groupId, LibraryMatchEligibilityState state, string reason);

        long EnqueueReanalysis(long fileId, LibraryReanalysisWork work, string batchId = "", int maximumAttempts = 3);
        IReadOnlyList<LibraryReanalysisItem> ClaimReanalysisBatch(int limit, DateTime utcNow);
        void CompleteReanalysisItem(long itemId, LibraryReanalysisWork completedWork, string errorText = "", DateTime? retryUtc = null);
        int RecoverInterruptedReanalysis();
        void PrepareMetadataReanalysis(IReadOnlyCollection<long> fileIds);
        void PrepareExactReanalysis(IReadOnlyCollection<long> fileIds);
        void PrepareVisualReanalysis(IReadOnlyCollection<long> fileIds);

        IReadOnlyList<LibraryDecisionEvent> GetDecisionHistory(int limit = 200);
        LibraryDecisionUndoResult UndoDecision(long eventId);
        long AppendCleanupRestoreDecision(LibraryQuarantineRestoreItem item, string batchId = "");

        IReadOnlyList<LibraryHealthIssue> QueryHealthIssues(int limit = 500);
        IReadOnlyList<LibraryQuarantineRestoreItem> GetQuarantineRestoreCandidates(int limit = 200);
        LibraryMaintenanceResult RunSafeMaintenance();
    }
}
