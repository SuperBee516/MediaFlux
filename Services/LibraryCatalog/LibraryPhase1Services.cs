using System.Security.Cryptography;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed class LibraryMatchEligibilityService
    {
        private readonly ILibraryCatalog _catalog;
        private readonly ILibraryRecoveryCatalog _recovery;

        public LibraryMatchEligibilityService(ILibraryCatalog catalog, ILibraryRecoveryCatalog recovery)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        }

        public LibraryMatchEligibility EvaluateExactGroup(long groupId, bool observeFileSystem = true)
        {
            if (_catalog is not ILibraryAnalysisCatalog analysis)
                return new(LibraryMatchEligibilityState.StaleEvidence, "Exact analysis catalog is unavailable.");
            ExactDuplicateGroupRecord? group = analysis.GetDuplicateGroup(groupId);
            if (group == null) return new(LibraryMatchEligibilityState.Retired, "The exact group no longer exists.");
            return EvaluateMembers(analysis.GetDuplicateGroupMembers(groupId)
                .Select(x => (x.FileId, x.FullPath, x.LocationPath, x.SizeBytes, x.LastWriteUtc)), observeFileSystem);
        }

        public LibraryMatchEligibility EvaluateVisualGroup(long groupId, bool observeFileSystem = true)
        {
            if (_catalog is not ILibraryVisualCatalog visual)
                return new(LibraryMatchEligibilityState.StaleEvidence, "Visual analysis catalog is unavailable.");
            VisualSimilarityGroupRecord? group = visual.GetVisualGroup(groupId);
            if (group == null) return new(LibraryMatchEligibilityState.Retired, "The visual match no longer exists.");
            if (group.Eligibility == LibraryMatchEligibilityState.Retired)
                return new(group.Eligibility, group.EligibilityReason);
            LibraryMatchEligibility eligibility = EvaluateMembers(visual.GetVisualGroupMembers(groupId)
                .Select(x => (x.FileId, x.FullPath, x.LocationPath, x.SizeBytes, x.LastWriteUtc)), observeFileSystem);
            if (observeFileSystem && eligibility.State != group.Eligibility)
                _recovery.SetVisualMatchLifecycle(groupId, eligibility.State, eligibility.Reason);
            return eligibility;
        }

        private LibraryMatchEligibility EvaluateMembers(
            IEnumerable<(long FileId, string FullPath, string LocationPath, long SizeBytes, DateTime LastWriteUtc)> members,
            bool observeFileSystem)
        {
            var values = members.ToArray();
            if (values.Length < 2) return new(LibraryMatchEligibilityState.Retired, "The match has fewer than two members.");
            LibraryLocationRecord[] locations = _catalog.GetLocations().ToArray();
            foreach (var member in values)
            {
                LibraryPresenceObservation? observation = _recovery.GetPresenceObservations(member.FileId)
                    .OrderByDescending(x => x.LastObservedUtc).FirstOrDefault();
                if (observation?.State == LibraryPresenceObservationState.ConfirmedMissing)
                    return new(LibraryMatchEligibilityState.Missing, observation.Details, observation.RelatedFileId);
                if (observation?.State == LibraryPresenceObservationState.MovedOrRenamed)
                    return new(LibraryMatchEligibilityState.Retired, observation.Details, observation.RelatedFileId);
                if (observation?.State == LibraryPresenceObservationState.StaleEvidence)
                    return new(LibraryMatchEligibilityState.StaleEvidence, observation.Details);

                LibraryLocationRecord? location = locations.FirstOrDefault(x =>
                    string.Equals(x.Path, member.LocationPath, StringComparison.OrdinalIgnoreCase));
                if (location?.Availability is LibraryLocationAvailability.Unavailable or LibraryLocationAvailability.Error ||
                    observation?.State is LibraryPresenceObservationState.Unavailable or LibraryPresenceObservationState.AccessFailure)
                    return new(LibraryMatchEligibilityState.Unavailable,
                        location?.LastError.Length > 0 ? location.LastError : observation?.Details ?? "The library location is unavailable.");
                if (!observeFileSystem) continue;

                try
                {
                    if (!File.Exists(member.FullPath))
                    {
                        bool rootReachable = false;
                        try { rootReachable = location != null && Directory.Exists(location.Path); }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
                        if (location == null || (location.Availability != LibraryLocationAvailability.Available && !rootReachable))
                            return new(LibraryMatchEligibilityState.Unavailable, "The location is not authoritatively available; absence was not treated as deletion.");
                        _recovery.RecordPresenceObservation(location.Id, member.FileId,
                            LibraryPresenceObservationState.SuspectedMissing, "review-verification",
                            "The path was absent during review; a successful authoritative scan must confirm deletion.");
                        return new(LibraryMatchEligibilityState.SuspectedMissing, "The path is absent but has not been confirmed by a completed scan.");
                    }
                    var info = new FileInfo(member.FullPath);
                    if (info.Length != member.SizeBytes || info.LastWriteTimeUtc.Ticks != member.LastWriteUtc.Ticks)
                    {
                        if (location != null)
                            _recovery.RecordPresenceObservation(location.Id, member.FileId,
                                LibraryPresenceObservationState.StaleEvidence, "review-verification",
                                "Size or modification time changed after duplicate evidence was collected.");
                        return new(LibraryMatchEligibilityState.StaleEvidence, "The file changed after duplicate evidence was collected.");
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    if (location != null)
                        _recovery.RecordPresenceObservation(location.Id, member.FileId,
                            LibraryPresenceObservationState.AccessFailure, "review-verification", ex.Message);
                    return new(LibraryMatchEligibilityState.Unavailable, "The path could not be verified: " + ex.Message);
                }
            }
            return new(LibraryMatchEligibilityState.Active, "All members and evidence are current.");
        }
    }

    public sealed class LibraryReanalysisCoordinator : IDisposable
    {
        private readonly ILibraryRecoveryCatalog _catalog;
        private readonly LibraryEnrichmentCoordinator _enrichment;
        private readonly LibraryDuplicateAnalysisCoordinator _duplicates;
        private readonly LibraryVisualAnalysisCoordinator _visual;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _worker;
        private bool _disposed;

        public LibraryReanalysisCoordinator(ILibraryRecoveryCatalog catalog, LibraryEnrichmentCoordinator enrichment,
            LibraryDuplicateAnalysisCoordinator duplicates, LibraryVisualAnalysisCoordinator visual)
        {
            _catalog = catalog; _enrichment = enrichment; _duplicates = duplicates; _visual = visual;
            _catalog.RecoverInterruptedReanalysis();
            _worker = Task.Run(() => WorkerAsync(_shutdown.Token));
            Signal();
        }

        public long Queue(long fileId, LibraryReanalysisWork work, string batchId = "", int maximumAttempts = 3)
        {
            long id = _catalog.EnqueueReanalysis(fileId, work, batchId, maximumAttempts);
            Signal();
            return id;
        }

        public IReadOnlyList<long> QueueFiles(IEnumerable<long> fileIds, LibraryReanalysisWork work, string batchId = "")
        {
            string batch = string.IsNullOrWhiteSpace(batchId) ? Guid.NewGuid().ToString("N") : batchId;
            long[] result = fileIds.Distinct().Select(id => _catalog.EnqueueReanalysis(id, work, batch)).ToArray();
            Signal();
            return result;
        }

        private async Task WorkerAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                IReadOnlyList<LibraryReanalysisItem> items = _catalog.ClaimReanalysisBatch(32, DateTime.UtcNow);
                if (items.Count == 0)
                {
                    try { await _signal.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                await ProcessBatchAsync(items, token).ConfigureAwait(false);
            }
        }

        private async Task ProcessBatchAsync(IReadOnlyList<LibraryReanalysisItem> items, CancellationToken token)
        {
            LibraryReanalysisWork requested = items.Aggregate(LibraryReanalysisWork.None, (mask, item) => mask | item.Work);
            try
            {
                long[] metadata = items.Where(x => x.Work.HasFlag(LibraryReanalysisWork.Metadata)).Select(x => x.FileId).ToArray();
                long[] exact = items.Where(x => x.Work.HasFlag(LibraryReanalysisWork.ExactHash)).Select(x => x.FileId).ToArray();
                long[] visual = items.Where(x => x.Work.HasFlag(LibraryReanalysisWork.VisualFingerprint)).Select(x => x.FileId).ToArray();
                if (metadata.Length > 0)
                {
                    _catalog.PrepareMetadataReanalysis(metadata);
                    await _enrichment.QueuePendingAsync(token).ConfigureAwait(false);
                }
                if (exact.Length > 0)
                {
                    _catalog.PrepareExactReanalysis(exact);
                    LibraryDuplicateAnalysisResult result = await _duplicates.AnalyzeAsync(token).ConfigureAwait(false);
                    if (result.Status != DuplicateAnalysisStatus.Completed) throw new InvalidOperationException(result.ErrorText);
                }
                if (visual.Length > 0)
                {
                    _catalog.PrepareVisualReanalysis(visual);
                    LibraryVisualAnalysisResult result = await _visual.AnalyzeAsync(token).ConfigureAwait(false);
                    if (result.Status != DuplicateAnalysisStatus.Completed) throw new InvalidOperationException(result.ErrorText);
                }
                foreach (LibraryReanalysisItem item in items) _catalog.CompleteReanalysisItem(item.Id, item.Work);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                foreach (LibraryReanalysisItem item in items)
                {
                    TimeSpan delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, item.AttemptCount)));
                    _catalog.CompleteReanalysisItem(item.Id, LibraryReanalysisWork.None, ex.Message, DateTime.UtcNow + delay);
                }
            }
        }

        private void Signal() { try { _signal.Release(); } catch (SemaphoreFullException) { } }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _shutdown.Cancel(); Signal();
            try { _worker.Wait(TimeSpan.FromSeconds(10)); } catch { }
            _shutdown.Dispose(); _signal.Dispose();
        }
    }

    public sealed class LibraryDecisionService
    {
        private readonly ILibraryRecoveryCatalog _catalog;
        private readonly Action<long, LibraryReanalysisWork, string> _queueReanalysis;

        public LibraryDecisionService(ILibraryRecoveryCatalog catalog, LibraryReanalysisCoordinator reanalysis)
            : this(catalog, (fileId, work, batch) => reanalysis.Queue(fileId, work, batch)) { }

        internal LibraryDecisionService(ILibraryRecoveryCatalog catalog,
            Action<long, LibraryReanalysisWork, string> queueReanalysis)
        { _catalog = catalog; _queueReanalysis = queueReanalysis; }

        public IReadOnlyList<LibraryDecisionEvent> GetRecent(int limit = 200) => _catalog.GetDecisionHistory(limit);
        public LibraryDecisionUndoResult Undo(long eventId) => _catalog.UndoDecision(eventId);

        public LibraryDecisionUndoResult RestoreQuarantine(LibraryQuarantineRestoreItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (string.IsNullOrWhiteSpace(item.DestinationPath) || !File.Exists(item.DestinationPath))
                return new(false, null, "The quarantined file no longer exists.");
            if (File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
                return new(false, null, "The original path is already occupied.");
            string? parent = Path.GetDirectoryName(item.SourcePath);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                return new(false, null, "The original folder is unavailable.");
            var info = new FileInfo(item.DestinationPath);
            if (info.Length != item.SourceSizeBytes || info.LastWriteTimeUtc.Ticks != item.SourceLastWriteUtc.Ticks)
                return new(false, null, "The quarantined file no longer matches the cleanup audit.");
            if (item.ExactHash is { Length: > 0 })
            {
                using FileStream stream = new(item.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.SequentialScan);
                if (!SHA256.HashData(stream).SequenceEqual(item.ExactHash))
                    return new(false, null, "The quarantined file no longer matches its audited SHA-256 evidence.");
            }
            try
            {
                File.Move(item.DestinationPath, item.SourcePath);
                _catalog.MarkFileRestoredFromQuarantine(item.FileId, item.SourcePath);
                long eventId = _catalog.AppendCleanupRestoreDecision(item);
                _queueReanalysis(item.FileId, LibraryReanalysisWork.All, $"restore-{eventId}");
                return new(true, eventId, "The quarantined file was restored and queued for verification.");
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(item.SourcePath) && !File.Exists(item.DestinationPath))
                        File.Move(item.SourcePath, item.DestinationPath);
                }
                catch { }
                return new(false, null, ex.Message);
            }
        }
    }

    public sealed class LibraryInsightsService
    {
        private readonly ILibraryRecoveryCatalog _catalog;
        public LibraryInsightsService(ILibraryRecoveryCatalog catalog) => _catalog = catalog;

        public LibraryHealthSnapshot GetHealth(int limit = 500)
        {
            LibraryMaintenanceResult maintenance = _catalog.RunSafeMaintenance();
            var issues = _catalog.QueryHealthIssues(limit).ToList();
            if (!maintenance.Integrity.IsHealthy)
                issues.Insert(0, new LibraryHealthIssue("catalog-integrity", LibraryHealthIssueKind.CatalogIntegrity,
                    LibraryHealthSeverity.Error, "Catalog integrity check failed", string.Join(Environment.NewLine, maintenance.Integrity.Messages),
                    "Rebuild the catalog from the Health view after backing up decisions."));
            foreach (LibraryQuarantineRestoreItem item in _catalog.GetQuarantineRestoreCandidates(limit))
                if (File.Exists(item.DestinationPath))
                    issues.Add(new LibraryHealthIssue($"quarantine:{item.AuditId}", LibraryHealthIssueKind.RestorableQuarantine,
                        LibraryHealthSeverity.Information, "Quarantined file can be restored", item.SourcePath,
                        "Restore only if the original path is free.", item.FileId, CleanupAuditId: item.AuditId));
            return new LibraryHealthSnapshot(issues.Take(limit).ToArray(), maintenance.Integrity, DateTime.UtcNow);
        }

        public LibraryMaintenanceResult RunMaintenance() => _catalog.RunSafeMaintenance();
        public IReadOnlyList<LibraryQuarantineRestoreItem> GetRestoreCandidates() => _catalog.GetQuarantineRestoreCandidates();
    }
}
