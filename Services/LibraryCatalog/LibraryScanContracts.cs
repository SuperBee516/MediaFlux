namespace MediaFlux.Services.LibraryCatalog
{
    public sealed record LibraryScanOptions(
        int DiscoveryQueueCapacity = 2_000,
        int BatchSize = 500,
        bool CollectStableFileIdentity = true)
    {
        public LibraryScanOptions Validate()
        {
            if (DiscoveryQueueCapacity < 1 || DiscoveryQueueCapacity > 100_000)
                throw new ArgumentOutOfRangeException(nameof(DiscoveryQueueCapacity));
            if (BatchSize < 1 || BatchSize > 10_000)
                throw new ArgumentOutOfRangeException(nameof(BatchSize));
            return this;
        }
    }

    public enum LibraryScanOutcome
    {
        Completed,
        Canceled,
        Failed,
        Unavailable,
        Superseded
    }

    public sealed record LibraryScanProgress(
        long LocationId,
        string Stage,
        string CurrentPath,
        long DiscoveredFiles,
        long WrittenFiles,
        long NewFiles,
        long ChangedFiles,
        long UnchangedFiles,
        long MissingFiles,
        long ErrorCount,
        int QueuedFiles,
        long EnrichmentQueuedFiles,
        long EnrichmentDeferredFiles,
        bool Paused);

    public sealed record LibraryScanResult(
        long LocationId,
        LibraryScanOutcome Outcome,
        long DiscoveredFiles,
        long NewFiles,
        long ChangedFiles,
        long UnchangedFiles,
        long MissingFiles,
        long ErrorCount,
        int PeakQueuedFiles,
        string ErrorMessage);

    public sealed record LibraryFileSystemEntry(
        string FullPath,
        long SizeBytes,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc);

    public sealed record LibraryFileIdentity(string VolumeId, string FileId)
    {
        public static LibraryFileIdentity Empty { get; } = new("", "");
    }

    public interface ILibraryFileSystem
    {
        bool DirectoryExists(string path);
        IEnumerable<LibraryFileSystemEntry> EnumerateFiles(
            string rootPath,
            bool recursive,
            Action<string, Exception> onError,
            CancellationToken cancellationToken);
    }

    public interface ILibraryFileIdentityProvider
    {
        LibraryFileIdentity GetIdentity(string path);
    }

    public sealed record LibraryEnrichmentRequest(
        long FileId,
        string FullPath,
        string VolumeId,
        long SizeBytes,
        DateTime LastWriteUtc,
        int AttemptCount = 1);

    public interface ILibraryEnrichmentSink
    {
        ValueTask EnqueueAsync(
            LibraryEnrichmentRequest request,
            CancellationToken cancellationToken);

        bool TryEnqueue(LibraryEnrichmentRequest request);
    }
}
