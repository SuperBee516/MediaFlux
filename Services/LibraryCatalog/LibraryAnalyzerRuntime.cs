namespace MediaFlux.Services.LibraryCatalog
{
    public sealed class LibraryAnalyzerRuntime : IDisposable
    {
        private readonly SqliteLibraryCatalog _catalog;
        private readonly LibraryEnrichmentCoordinator _enrichment;
        private bool _disposed;

        public LibraryAnalyzerRuntime(
            IEnumerable<string> supportedExtensions,
            string applicationDirectory,
            string? configuredFfprobePath,
            Func<bool>? isEncodingActive = null)
            : this(
                SqliteLibraryCatalog.CreateDefault(),
                supportedExtensions,
                new FfprobeLibraryMetadataProbe(applicationDirectory, configuredFfprobePath),
                isEncodingActive)
        {
        }

        internal LibraryAnalyzerRuntime(
            SqliteLibraryCatalog catalog,
            IEnumerable<string> supportedExtensions,
            ILibraryMetadataProbe probe,
            Func<bool>? isEncodingActive = null,
            LibraryScanOptions? scanOptions = null,
            LibraryEnrichmentOptions? enrichmentOptions = null,
            ILibraryFileSystem? fileSystem = null,
            ILibraryFileIdentityProvider? identityProvider = null)
        {
            _catalog = catalog;
            _catalog.Initialize();
            _catalog.RecoverInterruptedWork();
            _enrichment = new LibraryEnrichmentCoordinator(
                _catalog,
                probe,
                enrichmentOptions,
                isEncodingActive);
            _enrichment.Start();
            Scanner = new LibraryScanCoordinator(
                _catalog,
                supportedExtensions,
                fileSystem,
                identityProvider,
                scanOptions,
                _enrichment);
            _ = QueuePendingSafelyAsync();
        }

        public ILibraryCatalog Catalog => _catalog;
        public LibraryScanCoordinator Scanner { get; }
        public LibraryEnrichmentCoordinator Enrichment => _enrichment;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Scanner.Cancel();
            try
            {
                _enrichment.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
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
