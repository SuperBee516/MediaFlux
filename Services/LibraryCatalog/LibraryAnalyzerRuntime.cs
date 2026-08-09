namespace MediaFlux.Services.LibraryCatalog
{
    public sealed class LibraryAnalyzerRuntime : IDisposable
    {
        private readonly SqliteLibraryCatalog _catalog;
        private readonly LibraryEnrichmentCoordinator _enrichment;
        private readonly LibraryDuplicateAnalysisCoordinator _duplicates;
        private readonly LibraryVisualAnalysisCoordinator _visual;
        private bool _disposed;

        public LibraryAnalyzerRuntime(
            IEnumerable<string> supportedExtensions,
            string applicationDirectory,
            string? configuredFfmpegPath,
            string? configuredFfprobePath,
            Func<bool>? isEncodingActive = null,
            IEnumerable<string>? protectedPaths = null,
            MediaFlux.Models.DuplicateKeeperPreferences? keeperPreferences = null)
            : this(
                SqliteLibraryCatalog.CreateDefault(),
                supportedExtensions,
                new FfprobeLibraryMetadataProbe(applicationDirectory, configuredFfprobePath),
                new FfmpegVisualFingerprintExtractor(applicationDirectory, configuredFfmpegPath),
                isEncodingActive,
                protectedPaths,
                keeperPreferences: keeperPreferences)
        {
        }

        internal LibraryAnalyzerRuntime(
            SqliteLibraryCatalog catalog,
            IEnumerable<string> supportedExtensions,
            ILibraryMetadataProbe probe,
            ILibraryVisualFingerprintExtractor visualExtractor,
            Func<bool>? isEncodingActive = null,
            IEnumerable<string>? protectedPaths = null,
            LibraryScanOptions? scanOptions = null,
            LibraryEnrichmentOptions? enrichmentOptions = null,
            ILibraryFileSystem? fileSystem = null,
            ILibraryFileIdentityProvider? identityProvider = null,
            ILibraryChangeJournalProvider? changeJournal = null,
            LibraryStorageScheduler? storageScheduler = null,
            MediaFlux.Models.DuplicateKeeperPreferences? keeperPreferences = null)
        {
            _catalog = catalog;
            _catalog.Initialize();
            _catalog.RecoverInterruptedWork();
            _catalog.RecoverInterruptedDuplicateWork();
            _catalog.RecoverInterruptedVisualWork();
            LibraryStorageScheduler scheduler = storageScheduler ?? new LibraryStorageScheduler();
            _enrichment = new LibraryEnrichmentCoordinator(
                _catalog,
                probe,
                enrichmentOptions,
                isEncodingActive,
                scheduler);
            _enrichment.Start();
            string[] protectedRoots = (protectedPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .ToArray();
            _duplicates = new LibraryDuplicateAnalysisCoordinator(
                _catalog,
                isEncodingActive: isEncodingActive,
                isProtectedPath: path => protectedRoots.Any(root => Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)),
                storageScheduler: scheduler);
            _visual = new LibraryVisualAnalysisCoordinator(
                _catalog,
                visualExtractor,
                isEncodingActive: isEncodingActive,
                storageScheduler: scheduler,
                keeperPreferences: keeperPreferences);
            DuplicateCleanup = new LibraryDuplicateCleanupService(_catalog, _catalog, isEncodingActive);
            VisualDuplicateCleanup = new LibraryVisualDuplicateCleanupService(_catalog, _catalog, _catalog, keeperPreferences, isEncodingActive);
            Scanner = new LibraryScanCoordinator(
                _catalog,
                supportedExtensions,
                fileSystem,
                identityProvider,
                scanOptions,
                _enrichment,
                _catalog,
                changeJournal,
                scheduler,
                (eventName, details, exception) => ErrorLogService.Append(
                    AppPaths.UserDataDirectory,
                    $"Library Analyzer scan: {eventName}",
                    exception: exception,
                    details: details));
            _ = QueuePendingSafelyAsync();
        }

        public ILibraryCatalog Catalog => _catalog;
        public LibraryScanCoordinator Scanner { get; }
        public LibraryEnrichmentCoordinator Enrichment => _enrichment;
        public ILibraryAnalysisCatalog AnalysisCatalog => _catalog;
        public LibraryDuplicateAnalysisCoordinator Duplicates => _duplicates;
        public ILibraryVisualCatalog VisualCatalog => _catalog;
        public LibraryVisualAnalysisCoordinator VisualSimilarity => _visual;
        public LibraryDuplicateCleanupService DuplicateCleanup { get; }
        public LibraryVisualDuplicateCleanupService VisualDuplicateCleanup { get; }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Scanner.CancelAndWait(TimeSpan.FromSeconds(10));
            _duplicates.CancelAndWait(TimeSpan.FromSeconds(10));
            _visual.CancelAndWait(TimeSpan.FromSeconds(10));
            try
            {
                _enrichment.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                _duplicates.Dispose();
                _visual.Dispose();
                _catalog.Checkpoint(LibraryCatalogCheckpointMode.Passive);
                _catalog.Dispose();
            }
        }

        private async Task QueuePendingSafelyAsync()
        {
            try
            {
                await _enrichment.QueuePendingAsync().ConfigureAwait(false);
            }
            catch
            {
                // Pending work remains durable and will be picked up by the retry loop
                // or the next application start.
            }
        }
    }
}
