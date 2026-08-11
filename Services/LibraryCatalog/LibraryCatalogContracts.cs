namespace MediaFlux.Services.LibraryCatalog
{
    public enum LibraryLocationAvailability
    {
        Unknown = 0,
        Available = 1,
        Unavailable = 2,
        Error = 3
    }

    public enum IndexedFileAvailability
    {
        Present = 0,
        Missing = 1,
        Unavailable = 2
    }

    public enum LibraryScanStatus
    {
        Running = 0,
        Completed = 1,
        Canceled = 2,
        Failed = 3,
        Interrupted = 4
    }

    public enum LibraryInventoryChangeKind
    {
        Unchanged = 0,
        New = 1,
        Changed = 2
    }

    public enum LibraryProbeStatus
    {
        Pending = 0,
        InProgress = 1,
        Succeeded = 2,
        Failed = 3
    }

    public enum LibraryCatalogCheckpointMode
    {
        Passive,
        Full,
        Restart,
        Truncate
    }

    public sealed record LibraryLocationUpsert(
        string Path,
        bool IncludeSubfolders = true,
        bool IsEnabled = true,
        LibraryLocationAvailability Availability = LibraryLocationAvailability.Unknown,
        string LastError = "");

    public sealed record LibraryLocationRecord(
        long Id,
        string Path,
        string PathKey,
        bool IncludeSubfolders,
        bool IsEnabled,
        LibraryLocationAvailability Availability,
        string LastError,
        long CurrentGeneration,
        DateTime CreatedUtc,
        DateTime UpdatedUtc,
        DateTime? LastCompletedScanUtc);

    public sealed record LibraryScanHandle(
        long ScanRunId,
        long LocationId,
        long Generation);

    public sealed record LibraryScanCompletion(
        LibraryScanStatus Status,
        long DiscoveredFiles,
        long UnchangedFiles,
        long NewFiles,
        long ChangedFiles,
        long MissingFiles,
        long ErrorCount,
        string ErrorText = "",
        DateTime? CompletedUtc = null);

    public sealed record LibraryInventoryEntry(
        string FullPath,
        string RelativePath,
        long SizeBytes,
        DateTime LastWriteTimeUtc,
        DateTime? CreationTimeUtc = null,
        string VolumeId = "",
        string FileIdentity = "",
        IndexedFileAvailability Availability = IndexedFileAvailability.Present,
        DateTime? SeenUtc = null);

    public sealed record IndexedFileRecord(
        long Id,
        string FullPath,
        string PathKey,
        string FileName,
        string Extension,
        long SizeBytes,
        DateTime? CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        string VolumeId,
        string FileIdentity,
        IndexedFileAvailability Availability,
        DateTime LastSeenUtc,
        DateTime CreatedUtc,
        DateTime UpdatedUtc);

    public sealed record LibraryFileMembershipRecord(
        long LocationId,
        long FileId,
        string RelativePath,
        string RelativePathKey,
        long LastSeenGeneration,
        IndexedFileAvailability Availability,
        DateTime LastSeenUtc);

    public sealed record LibraryCatalogCounts(
        long Locations,
        long Files,
        long Memberships,
        long ScanRuns);

    public sealed record LibraryInventoryMutation(
        long FileId,
        string FullPath,
        string VolumeId,
        long SizeBytes,
        long LastWriteUtcTicks,
        LibraryInventoryChangeKind ChangeKind,
        bool RequiresEnrichment);

    public sealed record LibraryInventoryBatchResult(
        int Written,
        int NewFiles,
        int ChangedFiles,
        int UnchangedFiles,
        IReadOnlyList<LibraryInventoryMutation> Mutations);

    public sealed record LibraryReconciliationResult(
        long MissingMemberships,
        long MissingFiles);

    public sealed record LibraryAudioStreamMetadata(
        string Codec,
        int? Channels,
        string ChannelLayout,
        string Language);

    public sealed record LibrarySubtitleStreamMetadata(
        string Codec,
        string Language);

    public sealed record LibraryMediaMetadata(
        long FileId,
        int MetadataVersion,
        string ProbeToolVersion,
        LibraryProbeStatus ProbeStatus,
        int AttemptCount,
        DateTime? NextRetryUtc,
        DateTime? LastAttemptUtc,
        DateTime? LastSuccessUtc,
        long SourceSizeBytes,
        DateTime SourceLastWriteUtc,
        string FormatName,
        double? DurationSeconds,
        long? TotalBitRate,
        string VideoCodec,
        string VideoProfile,
        int? VideoLevel,
        int? Width,
        int? Height,
        double? FrameRate,
        string PixelFormat,
        int? BitDepth,
        string FieldOrder,
        string ColorRange,
        string ColorSpace,
        string ColorTransfer,
        string ColorPrimaries,
        IReadOnlyList<LibraryAudioStreamMetadata> AudioStreams,
        IReadOnlyList<LibrarySubtitleStreamMetadata> SubtitleStreams,
        int ChapterCount,
        int AttachmentCount,
        string ErrorMessage);

    public sealed record LibraryEnrichmentCandidate(
        long FileId,
        string FullPath,
        string VolumeId,
        long SizeBytes,
        DateTime LastWriteUtc,
        int AttemptCount);

    public sealed record LibraryOverview(
        long IndexedFiles,
        long LogicalSizeBytes,
        long PendingEnrichment,
        long UnavailableLocations,
        DateTime? LastCompletedScanUtc,
        long ActiveScans);

    public sealed record LibraryFileQuery(
        string Search = "",
        long? LocationId = null,
        IndexedFileAvailability? Availability = null,
        LibraryProbeStatus? ProbeStatus = null,
        string SortColumn = "path",
        bool Descending = false,
        int Offset = 0,
        int Limit = 200);

    public sealed record LibraryFileViewRecord(
        long FileId,
        string FileName,
        string FullPath,
        string LocationPath,
        long SizeBytes,
        DateTime LastWriteUtc,
        IndexedFileAvailability Availability,
        string FormatName,
        string VideoCodec,
        int? Width,
        int? Height,
        long? TotalBitRate,
        double? DurationSeconds,
        LibraryProbeStatus ProbeStatus,
        string ProbeError);

    public sealed record LibraryFilePage(
        long TotalCount,
        IReadOnlyList<LibraryFileViewRecord> Files);

    public sealed record LibraryCatalogDiagnostics(
        int SchemaVersion,
        int ApplicationId,
        string JournalMode,
        int SynchronousMode,
        bool ForeignKeysEnabled,
        string SqliteVersion,
        long PageCount,
        long PageSize,
        long FreePageCount)
    {
        public long DatabaseBytes => PageCount * PageSize;
        public long FreeBytes => FreePageCount * PageSize;
    }

    public sealed record LibraryCatalogIntegrityResult(
        bool IsHealthy,
        IReadOnlyList<string> Messages);

    public sealed record LibraryCatalogCheckpointResult(
        bool Busy,
        int LogFrames,
        int CheckpointedFrames);

    public sealed record LibraryCatalogInitializationResult(
        bool Success,
        LibraryCatalogDiagnostics? Diagnostics,
        string MigrationBackupPath,
        string ErrorMessage,
        Exception? Exception = null);

    public interface ILibraryCatalog : IDisposable
    {
        string DatabasePath { get; }
        LibraryCatalogInitializationResult TryInitialize();
        LibraryCatalogDiagnostics Initialize();
        LibraryCatalogDiagnostics GetDiagnostics();
        LibraryCatalogIntegrityResult CheckIntegrity(bool fullCheck = false);
        LibraryCatalogCheckpointResult Checkpoint(
            LibraryCatalogCheckpointMode mode = LibraryCatalogCheckpointMode.Passive);
        string CreateBackup(string? destinationPath = null);
        string RebuildCatalog();

        LibraryLocationRecord UpsertLocation(LibraryLocationUpsert location);
        LibraryLocationRecord? GetLocation(long locationId);
        LibraryScanHandle BeginScan(long locationId, DateTime? startedUtc = null);
        void CompleteScan(LibraryScanHandle scan, LibraryScanCompletion completion);
        int UpsertInventoryBatch(
            LibraryScanHandle scan,
            IReadOnlyCollection<LibraryInventoryEntry> entries);
        LibraryInventoryBatchResult UpsertInventoryBatchDetailed(
            LibraryScanHandle scan,
            IReadOnlyCollection<LibraryInventoryEntry> entries,
            int currentMetadataVersion);
        LibraryReconciliationResult ReconcileCompletedScan(LibraryScanHandle scan);
        void SetLocationAvailability(
            long locationId,
            LibraryLocationAvailability availability,
            string error = "",
            bool markMembershipsUnavailable = false);
        IReadOnlyList<LibraryLocationRecord> GetLocations(bool includeDisabled = true);
        IReadOnlyDictionary<long, long> GetLocationFileCounts();
        void RemoveLocation(long locationId, bool removeOrphanedFiles);
        int RecoverInterruptedWork();

        IReadOnlyList<LibraryEnrichmentCandidate> ClaimEnrichmentBatch(
            int limit,
            int metadataVersion,
            string probeToolVersion,
            DateTime utcNow);
        void SaveMediaMetadata(LibraryMediaMetadata metadata);
        LibraryMediaMetadata? GetMediaMetadata(long fileId);
        LibraryOverview GetOverview(int metadataVersion);
        LibraryFilePage QueryFiles(LibraryFileQuery query);

        IndexedFileRecord? GetFileByPath(string path);
        IReadOnlyList<IndexedFileRecord> GetFilesByIdentity(string volumeId, string fileIdentity);
        IReadOnlyList<IndexedFileRecord> GetLocationFilesPage(
            long locationId,
            long afterFileId,
            int limit);
        IReadOnlyList<LibraryFileMembershipRecord> GetMembershipsForFile(long fileId);
        LibraryCatalogCounts GetCounts();
    }
}
