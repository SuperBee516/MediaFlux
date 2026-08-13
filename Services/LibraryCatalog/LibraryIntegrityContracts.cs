namespace MediaFlux.Services.LibraryCatalog;

public enum LibraryIntegrityScrubType { Quick = 0, Full = 1 }

public enum LibraryIntegrityResultState
{
    NeverChecked = 0, Pending = 1, Running = 2, Passed = 3, Warning = 4,
    Failed = 5, Stale = 6, Unavailable = 7, Cancelled = 8
}

public enum LibraryIntegrityErrorCategory
{
    None = 0, VideoDecodeError = 1, AudioDecodeError = 2, TruncatedMedia = 3,
    InvalidTimestamps = 4, ContainerReadFailure = 5, MissingVideoStream = 6,
    FileChanged = 7, FileDisappeared = 8, StorageUnavailable = 9,
    ToolFailure = 10, Cancelled = 11
}

public enum LibraryIntegrityQueueStatus { Pending = 0, Running = 1, Completed = 2, Failed = 3, Cancelled = 4 }

public sealed record LibraryIntegrityResult(
    long FileId, string FullPath, string FileName, string LocationPath, long SizeBytes,
    string VideoCodec, double? DurationSeconds, int MethodVersion, LibraryIntegrityScrubType ScrubType,
    LibraryIntegrityResultState State, DateTime? CheckedUtc, long SourceSizeBytes,
    DateTime SourceLastWriteUtc, string SourceVolumeId, string SourceFileIdentity,
    long BytesChecked, double MediaDurationCheckedSeconds, double ElapsedSeconds,
    LibraryIntegrityErrorCategory ErrorCategory, string Details, string ToolVersion,
    bool IsStale, long? QueueId = null);

public sealed record LibraryIntegrityResultWrite(
    long FileId, int MethodVersion, LibraryIntegrityScrubType ScrubType,
    LibraryIntegrityResultState State, DateTime? CheckedUtc, long SourceSizeBytes,
    DateTime SourceLastWriteUtc, string SourceVolumeId, string SourceFileIdentity,
    long BytesChecked, double MediaDurationCheckedSeconds, double ElapsedSeconds,
    LibraryIntegrityErrorCategory ErrorCategory, string Details, string ToolVersion);

public sealed record LibraryIntegrityQueueItem(
    long Id, long FileId, string FullPath, string VolumeId, long SizeBytes,
    DateTime LastWriteUtc, string FileIdentity, string VideoCodec, double? DurationSeconds,
    int AudioStreamCount, LibraryIntegrityScrubType ScrubType, LibraryIntegrityQueueStatus Status,
    int AttemptCount, int MaximumAttempts, string BatchId, string ErrorText,
    DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record LibraryIntegrityQuery(
    LibraryIntegrityResultState? State = null, long? LocationId = null, string Search = "",
    int Offset = 0, int Limit = 200);

public sealed record LibraryIntegrityPage(long TotalCount, IReadOnlyList<LibraryIntegrityResult> Results);

public sealed record LibraryIntegritySummary(
    long TotalFiles, long Passed, long Warnings, long Failed, long NeverChecked,
    long Stale, long Pending, long Running, long Cancelled);

public sealed record LibraryIntegrityProgress(
    long QueueId, long FileId, string FullPath, LibraryIntegrityScrubType ScrubType,
    double? Percent, TimeSpan Elapsed, TimeSpan? EstimatedRemaining, string Status);

public sealed record LibraryIntegrityRunResult(
    LibraryIntegrityResultWrite Result, IReadOnlyList<double> QuickPositionsSeconds);

public interface ILibraryIntegrityCatalog
{
    long EnqueueIntegrity(long fileId, LibraryIntegrityScrubType scrubType, string batchId = "", int maximumAttempts = 3);
    IReadOnlyList<LibraryIntegrityQueueItem> ClaimIntegrityBatch(int limit, DateTime utcNow);
    void CompleteIntegrityItem(long queueId, LibraryIntegrityResultWrite result, string errorText = "");
    void CancelIntegrityItem(long queueId, LibraryIntegrityResultWrite result);
    int RecoverInterruptedIntegrity();
    LibraryIntegrityPage QueryIntegrity(LibraryIntegrityQuery query);
    LibraryIntegritySummary GetIntegritySummary();
    IReadOnlyList<long> GetIntegrityFileIds(long? locationId, LibraryIntegrityResultState? state, int limit = 50_000);
    LibraryIntegrityResult? GetIntegrityResult(long fileId);
}
