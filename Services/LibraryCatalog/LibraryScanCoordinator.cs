using System.Threading.Channels;
using System.Diagnostics;

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
        private readonly ILibraryScanAccelerationCatalog? _accelerationCatalog;
        private readonly ILibraryChangeJournalProvider? _changeJournal;
        private readonly LibraryStorageScheduler _storageScheduler;
        private readonly Action<string, string, Exception?>? _diagnosticLog;
        private readonly AsyncPauseGate _pauseGate = new();
        private readonly SemaphoreSlim _scanGate = new(1, 1);
        private CancellationTokenSource? _activeCancellation;
        private TaskCompletionSource? _activeCompletion;

        public LibraryScanCoordinator(
            ILibraryCatalog catalog,
            IEnumerable<string> supportedExtensions,
            ILibraryFileSystem? fileSystem = null,
            ILibraryFileIdentityProvider? identityProvider = null,
            LibraryScanOptions? options = null,
            ILibraryEnrichmentSink? enrichmentSink = null,
            ILibraryScanAccelerationCatalog? accelerationCatalog = null,
            ILibraryChangeJournalProvider? changeJournal = null,
            LibraryStorageScheduler? storageScheduler = null,
            Action<string, string, Exception?>? diagnosticLog = null)
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
            _accelerationCatalog = accelerationCatalog ?? catalog as ILibraryScanAccelerationCatalog;
            _changeJournal = changeJournal ?? (_accelerationCatalog == null ? null : new WindowsUsnChangeJournalProvider());
            _storageScheduler = storageScheduler ?? new LibraryStorageScheduler();
            _diagnosticLog = diagnosticLog;
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
            _activeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token = linkedCancellation.Token;
            LibraryScanHandle? scan = null;
            long discovered = 0;
            long written = 0;
            long newFiles = 0;
            long changedFiles = 0;
            long unchangedFiles = 0;
            long missingFiles = 0;
            long errors = 0;
            long enrichmentQueued = 0;
            long enrichmentDeferred = 0;
            int queued = 0;
            int peakQueued = 0;
            string lastError = "";
            string currentPath = "";
            long lastProgressTimestamp = 0;
            int loggedEnrichmentBackpressure = 0;
            int loggedIndexing = 0;

            try
            {
                LibraryLocationRecord location = _catalog.GetLocation(locationId)
                    ?? throw new KeyNotFoundException($"Library location {locationId} does not exist.");
                if (!location.IsEnabled)
                {
                    Report("Failed", location.Path, force: true);
                    Log("failed", $"Location: {location.Path}\r\nThe library location is disabled.");
                    return Result(LibraryScanOutcome.Failed, "The library location is disabled.");
                }

                scan = _catalog.BeginScan(locationId);
                Log("started", $"Location: {location.Path}\r\nScan run: {scan.ScanRunId}\r\nGeneration: {scan.Generation}");
                Report("Checking location", location.Path, force: true);
                if (!_fileSystem.DirectoryExists(location.Path))
                {
                    string message = "The library location is offline or unavailable.";
                    _catalog.SetLocationAvailability(
                        locationId,
                        LibraryLocationAvailability.Unavailable,
                        message,
                        markMembershipsUnavailable: true);
                    _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Failed, message));
                    Report("Unavailable", location.Path, force: true);
                    Log("unavailable", $"Location: {location.Path}\r\n{message}");
                    return Result(LibraryScanOutcome.Unavailable, message);
                }

                _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Available);
                LibraryChangeJournalCheckpoint? scanStartCheckpoint = null;
                if (_accelerationCatalog != null && _changeJournal != null &&
                    _changeJournal.TryGetCheckpoint(location.Path, out LibraryChangeJournalCheckpoint checkpoint, out _))
                {
                    scanStartCheckpoint = checkpoint;
                    LibraryScanAcceleratorState? previous = _accelerationCatalog.GetScanAcceleratorState(locationId);
                    if (previous != null && LibraryChangeJournalSafety.ProvesNoVolumeChanges(previous, checkpoint))
                    {
                        Report("Finalizing scan", location.Path, force: true);
                        _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Completed, ""));
                        _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Available);
                        _accelerationCatalog.SaveScanAcceleratorState(ToAcceleratorState(locationId, checkpoint, "No-change scan shortcut used."));
                        Report("Scan complete", location.Path, force: true);
                        Log("completed", $"Location: {location.Path}\r\nUSN no-change shortcut used.");
                        return Result(LibraryScanOutcome.Completed, "");
                    }
                }

                Report("Waiting for storage access", location.Path, force: true);
                await using IAsyncDisposable storageLease = await _storageScheduler.AcquireAsync(
                    location.Path,
                    cancellationToken: token).ConfigureAwait(false);
                Report("Discovering files", location.Path, force: true);
                Log("enumeration started", $"Location: {location.Path}\r\nDiscovery queue capacity: {_options.DiscoveryQueueCapacity}\r\nBatch size: {_options.BatchSize}");
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
                            Report("Discovering files", file.FullPath);
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
                        FlushBatch(batch);
                }
                if (batch.Count > 0)
                    FlushBatch(batch);
                await producer.ConfigureAwait(false);

                if (errors > 0)
                {
                    string message = $"Enumeration was incomplete because {errors:N0} file-system errors occurred. {lastError}".Trim();
                    _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Error, message);
                    _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Failed, message));
                    Report("Failed", currentPath, force: true);
                    Log("failed", $"Location: {location.Path}\r\n{message}");
                    return Result(LibraryScanOutcome.Failed, message);
                }

                Report("Finalizing scan", location.Path, force: true);
                Log("finalizing", $"Location: {location.Path}\r\nDiscovered: {discovered:N0}\r\nIndexed: {written:N0}");
                LibraryReconciliationResult reconciliation = _catalog.ReconcileCompletedScan(scan);
                missingFiles = reconciliation.MissingFiles;
                _catalog.CompleteScan(scan, Completion(LibraryScanStatus.Completed, ""));
                _catalog.SetLocationAvailability(locationId, LibraryLocationAvailability.Available);
                if (scanStartCheckpoint != null)
                    _accelerationCatalog?.SaveScanAcceleratorState(ToAcceleratorState(locationId, scanStartCheckpoint, "Authoritative scan checkpoint."));
                Report("Scan complete", location.Path, force: true);
                Log("completed", $"Location: {location.Path}\r\nDiscovered: {discovered:N0}\r\nIndexed: {written:N0}\r\nNew: {newFiles:N0}\r\nChanged: {changedFiles:N0}\r\nUnchanged: {unchangedFiles:N0}\r\nMissing: {missingFiles:N0}\r\nErrors: {errors:N0}\r\nMetadata deferred: {enrichmentDeferred:N0}");
                return Result(LibraryScanOutcome.Completed, "");

                void FlushBatch(List<LibraryInventoryEntry> pending)
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
                    Report("Indexing files", result.Mutations.LastOrDefault()?.FullPath ?? currentPath, force: true);
                    if (Interlocked.Exchange(ref loggedIndexing, 1) == 0)
                        Log("inventory indexing started", $"Location: {location.Path}\r\nFirst committed batch: {result.Written:N0} files");

                    if (_enrichmentSink == null)
                        return;
                    foreach (LibraryInventoryMutation mutation in result.Mutations.Where(item => item.RequiresEnrichment))
                    {
                        bool accepted = _enrichmentSink.TryEnqueue(new LibraryEnrichmentRequest(
                                mutation.FileId,
                                mutation.FullPath,
                                mutation.VolumeId,
                                mutation.SizeBytes,
                                new DateTime(mutation.LastWriteUtcTicks, DateTimeKind.Utc)));
                        if (accepted)
                            Interlocked.Increment(ref enrichmentQueued);
                        else
                        {
                            Interlocked.Increment(ref enrichmentDeferred);
                            if (Interlocked.Exchange(ref loggedEnrichmentBackpressure, 1) == 0)
                                Log("metadata queue saturated", $"Location: {location.Path}\r\nDiscovered: {discovered:N0}\r\nIndexed: {written:N0}\r\nDiscovery queued: {queued:N0}\r\nMetadata work remains durable in the catalog and will be claimed in the background.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Resume();
                Report("Canceled", currentPath, force: true);
                Log("canceled", $"Location id: {locationId}\r\nDiscovered: {discovered:N0}\r\nIndexed: {written:N0}");
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
                Report("Superseded", currentPath, force: true);
                Log("superseded", $"Location id: {locationId}\r\n{ex.Message}");
                return Result(LibraryScanOutcome.Superseded, ex.Message);
            }
            catch (Exception ex)
            {
                Report("Failed", currentPath, force: true);
                Log("failed", $"Location id: {locationId}\r\nDiscovered: {discovered:N0}\r\nIndexed: {written:N0}", ex);
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
                _activeCompletion?.TrySetResult();
                _activeCompletion = null;
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

            void Report(string stage, string path = "", bool force = false)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    Volatile.Write(ref currentPath, path);
                long now = Stopwatch.GetTimestamp();
                long previous = Volatile.Read(ref lastProgressTimestamp);
                if (!force && previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(250))
                    return;
                Volatile.Write(ref lastProgressTimestamp, now);
                progress?.Report(new LibraryScanProgress(
                    locationId,
                    stage,
                    Volatile.Read(ref currentPath),
                    Interlocked.Read(ref discovered),
                    Interlocked.Read(ref written),
                    Interlocked.Read(ref newFiles),
                    Interlocked.Read(ref changedFiles),
                    Interlocked.Read(ref unchangedFiles),
                    Interlocked.Read(ref missingFiles),
                    Interlocked.Read(ref errors),
                    Volatile.Read(ref queued),
                    Interlocked.Read(ref enrichmentQueued),
                    Interlocked.Read(ref enrichmentDeferred),
                    IsPaused));
            }

            void Log(string eventName, string details, Exception? exception = null) =>
                _diagnosticLog?.Invoke(eventName, details, exception);
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

        public bool CancelAndWait(TimeSpan timeout)
        {
            _activeCancellation?.Cancel();
            Task? completion = _activeCompletion?.Task;
            if (completion == null) return true;
            try { return completion.Wait(timeout); }
            catch { return completion.IsCompleted; }
        }

        private static LibraryScanAcceleratorState ToAcceleratorState(
            long locationId,
            LibraryChangeJournalCheckpoint checkpoint,
            string message) => new(
                locationId,
                "usn-volume-checkpoint-v1",
                checkpoint.VolumeIdentity,
                checkpoint.FileSystemName,
                checkpoint.JournalId,
                checkpoint.NextUsn,
                checkpoint.LowestValidUsn,
                DateTime.UtcNow,
                message);
    }
}
