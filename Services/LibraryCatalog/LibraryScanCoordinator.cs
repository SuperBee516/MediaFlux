using System.Threading.Channels;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed class LibraryScanCoordinator
    {
        private readonly ILibraryCatalog _catalog;
        private readonly ILibraryFileSystem _fileSystem;
        private readonly ILibraryFileIdentityProvider _identityProvider;
        private readonly HashSet<string> _extensions;
        private readonly LibraryScanOptions _options;
        private readonly ILibraryEnrichmentSink? _enrichmentSink;
        private readonly AsyncPauseGate _pauseGate = new();
        private readonly SemaphoreSlim _scanGate = new(1, 1);
        private CancellationTokenSource? _activeCancellation;

        public LibraryScanCoordinator(
            ILibraryCatalog catalog,
            IEnumerable<string> supportedExtensions,
            ILibraryFileSystem? fileSystem = null,
            ILibraryFileIdentityProvider? identityProvider = null,
            LibraryScanOptions? options = null,
            ILibraryEnrichmentSink? enrichmentSink = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _fileSystem = fileSystem ?? new LibraryFileSystem();
            _identityProvider = identityProvider ?? new WindowsLibraryFileIdentityProvider();
            _extensions = new HashSet<string>(
                SupportedExtensionsStore.Normalize(supportedExtensions),
                StringComparer.OrdinalIgnoreCase);
            if (_extensions.Count == 0)
                throw new ArgumentException("At least one supported video extension is required.", nameof(supportedExtensions));
            _options = (options ?? new LibraryScanOptions()).Validate();
            _enrichmentSink = enrichmentSink;
        }

        public bool IsPaused => _pauseGate.IsPaused;
        public bool IsScanning => _activeCancellation != null;

        public void Pause() => _pauseGate.Pause();
        public void Resume() => _pauseGate.Resume();
        public void Cancel() => _activeCancellation?.Cancel();

        public async Task<LibraryScanResult> ScanLocationAsync(
            long locationId,
            int metadataVersion,
            IProgress<LibraryScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = linkedCancellation;
            CancellationToken token = linkedCancellation.Token;
            LibraryScanHandle? scan = null;
            long discovered = 0;
            long written = 0;
            long newFiles = 0;
            long changedFiles = 0;
            long unchangedFiles = 0;
            long missingFiles = 0;
            long errors = 0;
            int queued = 0;
            int peakQueued = 0;
            string lastError = "";

            try
            {
                LibraryLocationRecord location = _catalog.GetLocation(locationId)
                    ?? throw new KeyNotFoundException($"Library location {locationId} does not exist.");
                if (!location.IsEnabled)
                    return Result(LibraryScanOutcome.Failed, "The library location is disabled.");

                scan = _catalog.BeginScan(locationId);
                Report("Checking location");
                if (!_fileSystem.DirectoryExists(location.Path))
                {
                    string message = "The library location is offline or unavailable.";
                    _catalog.SetLocationAvailability(
                        locationId,
                        LibraryLocationAvailability.Unavailable,
                        message,
                        markMembershipsUnavailable: true);
                    _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Failed, message));
                    return Result(LibraryScanOutcome.Unavailable, message);
                }

                _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Available);
                var channel = Channel.CreateBounded<LibraryInventoryEntry>(new BoundedChannelOptions(_options.DiscoveryQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });

                Task producer = Task.Run(async () =>
                {
                    try
                    {
                        foreach (LibraryFileSystemEntry file in _fileSystem.EnumerateFiles(
                                     location.Path,
                                     location.IncludeSubfolders,
                                     (path, exception) =>
                                     {
                                         Interlocked.Increment(ref errors);
                                         lastError = $"{path}: {exception.Message}";
                                     },
                                     token))
                        {
                            token.ThrowIfCancellationRequested();
                            await _pauseGate.WaitAsync(token).ConfigureAwait(false);
                            if (!_extensions.Contains(Path.GetExtension(file.FullPath)))
                                continue;

                            LibraryFileIdentity identity = _options.CollectStableFileIdentity
                                ? _identityProvider.GetIdentity(file.FullPath)
                                : LibraryFileIdentity.Empty;
                            var entry = new LibraryInventoryEntry(
                                file.FullPath,
                                Path.GetRelativePath(location.Path, file.FullPath),
                                file.SizeBytes,
                                file.LastWriteTimeUtc,
                                file.CreationTimeUtc,
                                identity.VolumeId,
                                identity.FileId);
                            await channel.Writer.WriteAsync(entry, token).ConfigureAwait(false);
                            int currentQueued = Interlocked.Increment(ref queued);
                            UpdatePeak(ref peakQueued, currentQueued);
                            Interlocked.Increment(ref discovered);
                            if ((discovered & 127) == 0)
                                Report("Discovering files");
                        }
                        channel.Writer.TryComplete();
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.TryComplete(ex);
                    }
                }, token);

                var batch = new List<LibraryInventoryEntry>(_options.BatchSize);
                await foreach (LibraryInventoryEntry entry in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref queued);
                    await _pauseGate.WaitAsync(token).ConfigureAwait(false);
                    batch.Add(entry);
                    if (batch.Count >= _options.BatchSize)
                        await FlushBatchAsync(batch).ConfigureAwait(false);
                }
                if (batch.Count > 0)
                    await FlushBatchAsync(batch).ConfigureAwait(false);
                await producer.ConfigureAwait(false);

                if (errors > 0)
                {
                    string message = $"Enumeration was incomplete because {errors:N0} file-system errors occurred. {lastError}".Trim();
                    _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Error, message);
                    _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Failed, message));
                    return Result(LibraryScanOutcome.Failed, message);
                }

                Report("Reconciling inventory");
                LibraryReconciliationResult reconciliation = _catalog.ReconcileCompletedScan(scan);
                missingFiles = reconciliation.MissingFiles;
                _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Completed, ""));
                _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Available);
                Report("Completed");
                return Result(LibraryScanOutcome.Completed, "");

                async Task FlushBatchAsync(List<LibraryInventoryEntry> pending)
                {
                    LibraryInventoryBatchResult result = _catalog.UpsertInventoryBatchDetailed(
                        scan,
                        pending,
                        metadataVersion);
                    written += result.Written;
                    newFiles += result.NewFiles;
                    changedFiles += result.ChangedFiles;
                    unchangedFiles += result.UnchangedFiles;
                    pending.Clear();
                    Report("Writing inventory");

                    if (_enrichmentSink == null)
                        return;
                    foreach (LibraryInventoryMutation mutation in result.Mutations.Where(item => item.RequiresEnrichment))
                    {
                        await _enrichmentSink.EnqueueAsync(
                            new LibraryEnrichmentRequest(
                                mutation.FileId,
                                mutation.FullPath,
                                mutation.VolumeId,
                                mutation.SizeBytes,
                                new DateTime(mutation.LastWriteUtcTicks, DateTimeKind.Utc)),
                            token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Resume();
                if (scan != null)
                {
                    try
                    {
                        _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Canceled, "Scan canceled."));
                    }
                    catch (InvalidOperationException)
                    {
                        return Result(LibraryScanOutcome.Superseded, "The scan was superseded by a newer generation.");
                    }
                }
                return Result(LibraryScanOutcome.Canceled, "Scan canceled.");
            }
            catch (InvalidOperationException ex) when (scan != null &&
                ex.Message.Contains("generation", StringComparison.OrdinalIgnoreCase))
            {
                return Result(LibraryScanOutcome.Superseded, ex.Message);
            }
            catch (Exception ex)
            {
                if (scan != null)
                {
                    try
                    {
                        _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Error, ex.Message);
                        _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Failed, ex.Message));
                    }
                    catch
                    {
                        // Preserve the original scan failure.
                    }
                }
                return Result(LibraryScanOutcome.Failed, ex.Message);
            }
            finally
            {
                Resume();
                _activeCancellation = null;
                _scanGate.Release();
            }

            LibraryScanCompletion Completion(LibraryScanStatus status, string error) => new(
                status,
                discovered,
                unchangedFiles,
                newFiles,
                changedFiles,
                missingFiles,
                errors + (!string.IsNullOrWhiteSpace(error) && errors == 0 ? 1 : 0),
                error);

            LibraryScanResult Result(LibraryScanOutcome outcome, string error) => new(
                locationId,
                outcome,
                discovered,
                newFiles,
                changedFiles,
                unchangedFiles,
                missingFiles,
                errors,
                peakQueued,
                error);

            void Report(string stage) => progress?.Report(new LibraryScanProgress(
                locationId,
                stage,
                Interlocked.Read(ref discovered),
                Interlocked.Read(ref written),
                Interlocked.Read(ref newFiles),
                Interlocked.Read(ref changedFiles),
                Interlocked.Read(ref unchangedFiles),
                Interlocked.Read(ref missingFiles),
                Interlocked.Read(ref errors),
                Volatile.Read(ref queued),
                IsPaused));
        }

        private static void UpdatePeak(ref int peak, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref peak);
                if (current >= value)
                    return;
            }
            while (Interlocked.CompareExchange(ref peak, value, current) != current);
        }
    }
}
