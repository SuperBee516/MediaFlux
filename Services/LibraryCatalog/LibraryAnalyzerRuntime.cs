namespace MediaFlux.Services.LibraryCatalog
{
    public sealed class LibraryAnalyzerRuntime : IDisposable
    {
        private readonly SqliteLibraryCatalog _catalog;
        private readonly LibraryEnrichmentCoordinator _enrichment;
        private readonly LibraryDuplicateAnalysisCoordinator _duplicates;
        private readonly LibraryVisualAnalysisCoordinator _visual;
        private readonly LibraryReanalysisCoordinator _reanalysis;
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
            _catalog.RecoverInterruptedCleanupPlans();
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
                storageScheduler: scheduler,
                keeperPreferences: keeperPreferences);
            _visual = new LibraryVisualAnalysisCoordinator(
                _catalog,
                visualExtractor,
                isEncodingActive: isEncodingActive,
                storageScheduler: scheduler,
                keeperPreferences: keeperPreferences);
            DuplicateCleanup = new LibraryDuplicateCleanupService(_catalog, _catalog, keeperPreferences, isEncodingActive);
            VisualDuplicateCleanup = new LibraryVisualDuplicateCleanupService(_catalog, _catalog, _catalog, keeperPreferences, isEncodingActive);
            MatchEligibility = new LibraryMatchEligibilityService(_catalog, _catalog);
            _reanalysis = new LibraryReanalysisCoordinator(_catalog, _enrichment, _duplicates, _visual);
            Reanalysis = _reanalysis;
            Decisions = new LibraryDecisionService(_catalog, _reanalysis);
            Insights = new LibraryInsightsService(_catalog);
            KeeperExplanations = new LibraryKeeperExplanationService();
            MassReview = new LibraryMassReviewService(_catalog, MatchEligibility, keeperPreferences ?? new MediaFlux.Models.DuplicateKeeperPreferences());
            Recommendations = new LibraryRecommendationService(_catalog, DuplicateCleanup, VisualDuplicateCleanup, _catalog);
            PolicyEvaluation = new LibraryPolicyEvaluationService(_catalog);
            VisualFamilies = new LibraryVisualFamilyService(_catalog, VisualDuplicateCleanup, keeperPreferences);
            ReclamationOpportunities = new StorageReclamationOpportunitySource(_catalog, _catalog, _catalog, _catalog,
                DuplicateCleanup, VisualDuplicateCleanup, VisualFamilies, PolicyEvaluation);
            Recommendations.AttachFamilies(_catalog, VisualFamilies);
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
            _ = RunMaintenanceSafelyAsync();
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
        public LibraryMatchEligibilityService MatchEligibility { get; }
        public LibraryReanalysisCoordinator Reanalysis { get; }
        public LibraryDecisionService Decisions { get; }
        public LibraryInsightsService Insights { get; }
        public LibraryKeeperExplanationService KeeperExplanations { get; }
        public LibraryMassReviewService MassReview { get; }
        public LibraryRecommendationService Recommendations { get; }
        public LibraryPolicyEvaluationService PolicyEvaluation { get; }
        public StorageReclamationOpportunitySource ReclamationOpportunities { get; }
        public string ReclamationRevision => _catalog.GetPolicyFactsRevision();
        public ILibraryVisualFamilyCatalog FamilyCatalog => _catalog;
        public LibraryVisualFamilyService VisualFamilies { get; }

        public void UpdateVisualKeeperPreferences(MediaFlux.Models.DuplicateKeeperPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            _visual.UpdateKeeperPreferences(preferences);
            VisualDuplicateCleanup.UpdateKeeperPreferences(preferences);
            MassReview.UpdatePreferences(preferences);
            VisualFamilies.UpdateKeeperPreferences(preferences);
        }

        public void UpdateExactKeeperPreferences(MediaFlux.Models.DuplicateKeeperPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            _duplicates.UpdateKeeperPreferences(preferences);
            DuplicateCleanup.UpdateKeeperPreferences(preferences);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Scanner.CancelAndWait(TimeSpan.FromSeconds(10));
            _reanalysis.Dispose();
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

        private async Task RunMaintenanceSafelyAsync()
        {
            try
            {
                await Task.Yield();
                Insights.RunMaintenance();
            }
            catch
            {
                // Health view reports persistent maintenance or integrity problems.
            }
        }
    }
}
