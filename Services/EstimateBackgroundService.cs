using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaFlux.Models;
using MediaFlux.Services.EncodingV2;

namespace MediaFlux.Services
{
    /// <summary>
    /// Runs background size-estimation work and exposes result queues for the UI thread.
    /// </summary>
    public sealed class EstimateBackgroundService : IDisposable
    {
        private readonly MediaInfoService _mediaInfoService;
        private readonly SmartEncodeDecisionService _decisionService = new();
        // Captured analysis is reused across settings changes only while queue ownership
        // and the source's length/write timestamp still match.
        private readonly ConcurrentDictionary<string, (MediaInfoService.MediaInfo Info, long SourceBytes,
            long SourceWriteTicks, double SourceMb, double Duration, Guid QueueItemId, long AuthorityRevision)> _analyzedSources =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly QueueEmpiricalPreviewProvider? _previewProvider;
        private readonly Func<SmartEncodeSourceInfo, SmartEncodeIntent, SmartEncodeRecommendation> _evaluateRecommendation;
        private readonly Func<string, MediaInfoService.MediaInfo> _readMetadata;
        private readonly Func<string, bool> _isSourceOwnedByActiveJob;
        private readonly object _resetLock = new();
        private readonly int _workerCount = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
        private readonly QueueEstimateRefinementScheduler<EstimateRefinementWorkItem> _refinementScheduler;
        private readonly ConcurrentDictionary<Guid, EstimateRefinementWorkItem> _deferredRefinements = new();
        private readonly ConcurrentDictionary<Guid, QueueEstimateRefinementPriority> _refinementPriorities = new();

        private CancellationTokenSource _estimateCts = new();
        private BlockingCollection<EstimateWorkItem> _workItems = new();
        private Task[] _workers = Array.Empty<Task>();
        private int _generation;

        private readonly ConcurrentQueue<SmartEstimateResult> _smartResults = new();

        private int _pendingEstimates;
        private long _empiricalJobsLaunched;
        private long _empiricalJobsCompleted;
        private long _empiricalSamplesLaunched;
        private int _activeEmpiricalSamples;
        private int _maximumConcurrentEmpiricalSamples;
        private long _cancelledOrSupersededWork;

        private sealed record EstimateRefinementWorkItem(
            EstimateWorkItem Work,
            double SourceMb,
            double EstimatedMb,
            double DurationSeconds,
            string? Resolution,
            string? VideoCodec,
            double Fps,
            EncodingQualityResolution? QualityResolution,
            EncodingSizePredictionCalibration? SizeCalibration,
            string EstimateDiagnostic,
            double PlannedAudioBitrateKbps,
            double PlannedMappedAncillaryBitrateKbps,
            SmartEncodeRecommendation? Recommendation,
            ProductionPlanIdentity? PlanIdentity,
            PlanBoundEstimate? BoundEstimate,
            SmartEncodeSourceInfo? RecommendationSource = null,
            SmartEncodeIntent? RecommendationIntent = null);

        public EstimateBackgroundService(
            MediaInfoService mediaInfoService,
            EncodingStatisticsService? statistics = null,
            Func<string, bool>? isSourceOwnedByActiveJob = null,
            EncodingService? previewEncodingService = null,
            IMediaProbeService? previewProbeService = null,
            EncodeExecutionSnapshotBuilder? snapshots = null,
            string previewToolSignature = "")
        {
            _mediaInfoService = mediaInfoService ?? throw new ArgumentNullException(nameof(mediaInfoService));
            _readMetadata = _mediaInfoService.GetInfo;
            _evaluateRecommendation = _decisionService.Evaluate;
            _isSourceOwnedByActiveJob = isSourceOwnedByActiveJob ?? (_ => false);
            if (previewEncodingService is not null && previewProbeService is not null && snapshots is not null)
                _previewProvider = new(previewEncodingService, previewProbeService, snapshots, previewToolSignature);
            _refinementScheduler = new(ProcessRefinementAsync);
            StartWorkers();
        }

        internal EstimateBackgroundService(MediaInfoService media, QueueEmpiricalPreviewProvider preview,
            Func<string, MediaInfoService.MediaInfo> metadata, Func<string, bool>? isOwned = null,
            Func<SmartEncodeSourceInfo, SmartEncodeIntent, SmartEncodeRecommendation>? recommendationEvaluator = null)
            : this(media, isSourceOwnedByActiveJob: isOwned)
        {
            _previewProvider = preview;
            _readMetadata = metadata;
            if (recommendationEvaluator is not null)
                _evaluateRecommendation = recommendationEvaluator;
        }

        public readonly struct SmartEstimateResult
        {
            public int Generation { get; }
            public Guid QueueItemId { get; }
            public string Path { get; }
            public double SourceMb { get; }
            public double EstimatedMb { get; }
            public double DurationSec { get; }
            public string? Resolution { get; }
            public string? VideoCodec { get; }
            public double Fps { get; }
            public bool IsCustom { get; }
            public string? UnavailableReason { get; }
            public SmartEncodeRecommendation? Recommendation { get; }
            public string EstimateDiagnostic { get; }
            public double PlannedAudioBitrateKbps { get; }
            public double PlannedMappedAncillaryBitrateKbps { get; }
            public EncodingQualityResolution? QualityResolution { get; }
            public EncodingSizePredictionCalibration? SizeCalibration { get; }
            public EmpiricalPreviewResult? EmpiricalPreview { get; }
            public long EstimateAuthorityRevision { get; }
            public DateTime QueueAcceptedUtc { get; }
            public DateTime? QualityChangeStartedUtc { get; }
            public bool RecommendationOnly { get; }
            public ProductionPlanIdentity? PlanIdentity { get; }
            public PlanBoundEstimate? BoundEstimate { get; }
            public PreparedAdaptiveProductionPlan? PreparedAdaptiveProductionPlan { get; }
            public PreparedProductionPlan? PreparedProductionPlan { get; }
            public QueueEstimateState EstimateState { get; }

            public SmartEstimateResult(
                int generation,
                Guid queueItemId,
                string path,
                double sourceMb,
                double estimatedMb,
                double durationSec,
                string? resolution,
                string? videoCodec,
                double fps,
                bool isCustom,
                string? unavailableReason,
                SmartEncodeRecommendation? recommendation,
                string estimateDiagnostic,
                double plannedAudioBitrateKbps,
                double plannedMappedAncillaryBitrateKbps,
                EncodingQualityResolution? qualityResolution = null,
                EncodingSizePredictionCalibration? sizeCalibration = null,
                EmpiricalPreviewResult? empiricalPreview = null,
                long estimateAuthorityRevision = 0,
                DateTime? queueAcceptedUtc = null,
                DateTime? qualityChangeStartedUtc = null,
                bool recommendationOnly = false,
                ProductionPlanIdentity? planIdentity = null,
                PlanBoundEstimate? boundEstimate = null,
                QueueEstimateState? estimateState = null,
                PreparedAdaptiveProductionPlan? preparedAdaptiveProductionPlan = null,
                PreparedProductionPlan? preparedProductionPlan = null)
            {
                Generation = generation;
                QueueItemId = queueItemId;
                Path = path;
                SourceMb = sourceMb;
                EstimatedMb = estimatedMb;
                DurationSec = durationSec;
                Resolution = resolution;
                VideoCodec = videoCodec;
                Fps = fps;
                IsCustom = isCustom;
                UnavailableReason = unavailableReason;
                Recommendation = recommendation;
                EstimateDiagnostic = estimateDiagnostic;
                PlannedAudioBitrateKbps = plannedAudioBitrateKbps;
                PlannedMappedAncillaryBitrateKbps =
                    plannedMappedAncillaryBitrateKbps;
                QualityResolution = qualityResolution;
                SizeCalibration = sizeCalibration;
                EmpiricalPreview = empiricalPreview;
                EstimateAuthorityRevision = estimateAuthorityRevision;
                QueueAcceptedUtc = queueAcceptedUtc ?? DateTime.UtcNow;
                QualityChangeStartedUtc = qualityChangeStartedUtc;
                RecommendationOnly = recommendationOnly;
                PlanIdentity = planIdentity;
                BoundEstimate = boundEstimate;
                PreparedAdaptiveProductionPlan = preparedAdaptiveProductionPlan;
                PreparedProductionPlan = preparedProductionPlan;
                EstimateState = estimateState ?? (boundEstimate?.Authority == EstimateAuthorityLevel.Measured
                    ? QueueEstimateState.Measured
                    : boundEstimate is not null ? QueueEstimateState.Preliminary
                    : unavailableReason is not null ? QueueEstimateState.Unavailable
                    : QueueEstimateState.Pending);
            }
        }

        private readonly struct EstimateWorkItem
        {
            public EstimateWorkItem(
                int generation,
                Guid queueItemId,
                string path,
                bool auto,
                string profile,
                double manualTargetMb,
                VideoEncoderSelection encoder,
                int quality,
                int? targetHeight,
                int? targetAudioChannels,
                bool isCustom,
                bool recommendationsEnabled,
                double minimumSavingsPercent,
                StorageSavingsOptions storageSavings,
                EncodingQualityIntent? qualityIntent,
                bool sourceAdaptiveCeilingEligible,
                string encoderPreset,
                int outputBitDepth,
                bool concurrentEncoderSessions)
            {
                Generation = generation;
                QueueItemId = queueItemId;
                Path = path;
                Auto = auto;
                Profile = profile;
                ManualTargetMb = manualTargetMb;
                Encoder = encoder;
                Quality = quality;
                TargetHeight = targetHeight;
                TargetAudioChannels = targetAudioChannels;
                IsCustom = isCustom;
                RecommendationsEnabled = recommendationsEnabled;
                MinimumSavingsPercent = minimumSavingsPercent;
                StorageSavings = storageSavings.CloneNormalized();
                QualityIntent = qualityIntent;
                SourceAdaptiveCeilingEligible = sourceAdaptiveCeilingEligible;
                EncoderPreset = encoderPreset;
                OutputBitDepth = outputBitDepth;
                ConcurrentEncoderSessions = concurrentEncoderSessions;
            }

            public int Generation { get; }
            public Guid QueueItemId { get; }
            public string Path { get; }
            public bool Auto { get; }
            public string Profile { get; }
            public double ManualTargetMb { get; }
            public VideoEncoderSelection Encoder { get; }
            public int Quality { get; }
            public int? TargetHeight { get; }
            public int? TargetAudioChannels { get; }
            public bool IsCustom { get; }
            public bool RecommendationsEnabled { get; }
            public double MinimumSavingsPercent { get; }
            public StorageSavingsOptions StorageSavings { get; }
            public EncodingQualityIntent? QualityIntent { get; }
            public bool SourceAdaptiveCeilingEligible { get; }
            public string EncoderPreset { get; }
            public int OutputBitDepth { get; }
            public bool ConcurrentEncoderSessions { get; }
            public EncodeExecutionSnapshotSettings? PreviewSettings { get; init; }
            public EncodeExecutionSnapshotItem? PreviewItem { get; init; }
            public long EstimateAuthorityRevision { get; init; }
            public bool EmpiricalPreviewEnabled { get; init; }
            public QueueEstimateRefinementPriority RefinementPriority { get; init; }
            public DateTime QueueAcceptedUtc { get; init; }
            public DateTime QueuedUtc { get; init; }
            public DateTime? QualityChangeStartedUtc { get; init; }
            public ProductionPlanIdentity? PlanIdentity { get; init; }
        }

        // Include completed-but-not-yet-applied results so the UI does not report
        // analysis complete before the grid and item models have been refreshed.
        public int PendingEstimates =>
            Math.Max(0, Volatile.Read(ref _pendingEstimates)) + _smartResults.Count(result =>
                !result.RecommendationOnly && result.EstimateState is not (QueueEstimateState.Measuring or QueueEstimateState.Measured));
        public int CurrentGeneration => Volatile.Read(ref _generation);
        public int PendingRefinements => _refinementScheduler.PendingCount;
        internal QueueMetadataProbeDiagnostics? MetadataProbeDiagnostics =>
            _previewProvider?.MetadataProbeDiagnostics;
        internal Task WaitForRefinementsAsync(CancellationToken cancellationToken = default) =>
            _refinementScheduler.WaitForIdleAsync(cancellationToken);
        public long EmpiricalJobsLaunched => Interlocked.Read(ref _empiricalJobsLaunched);
        public long EmpiricalJobsCompleted => Interlocked.Read(ref _empiricalJobsCompleted);
        public long EmpiricalSamplesLaunched => Interlocked.Read(ref _empiricalSamplesLaunched);
        public int MaximumConcurrentEmpiricalSamples => Volatile.Read(ref _maximumConcurrentEmpiricalSamples);
        public long CancelledOrSupersededWork => Interlocked.Read(ref _cancelledOrSupersededWork) +
            _refinementScheduler.CancelledOrSupersededCount;

        public void SetRefinementPriority(Guid queueItemId, QueueEstimateRefinementPriority priority)
        {
            _refinementPriorities[queueItemId] = priority;
            if (_refinementScheduler.SetPriority(queueItemId, priority) ||
                priority == QueueEstimateRefinementPriority.Background)
                return;

            if (_deferredRefinements.TryRemove(queueItemId, out EstimateRefinementWorkItem? refinement))
                _refinementScheduler.Request(queueItemId, refinement.Work.Generation,
                    refinement, priority, _estimateCts.Token);
        }

        public void CancelRefinement(Guid queueItemId)
        {
            _deferredRefinements.TryRemove(queueItemId, out _);
            _refinementPriorities.TryRemove(queueItemId, out _);
            _refinementScheduler.Cancel(queueItemId);
        }

        internal async Task<QueueProductionPlanEstimate> PrepareProductionPlanAsync(
            EncodeExecutionSnapshotSettings settings,
            EncodeExecutionSnapshotItem item,
            long estimateAuthorityRevision,
            CancellationToken cancellationToken,
            Action<int>? sampleActivity = null,
            bool productionPriority = false)
        {
            if (_previewProvider is null)
                throw new InvalidOperationException("Measured production-plan preparation is unavailable.");
            QueueProductionPlanEstimate result = await _previewProvider.PrepareAndEstimateAsync(
                settings, item, estimateAuthorityRevision, cancellationToken, sampleActivity,
                productionPriority).ConfigureAwait(false);
            PreparedProductionPlan prepared = BindEstimateGeneration(
                result.PreparedPlan, CurrentGeneration);
            return result with { PreparedPlan = prepared };
        }

        private static PreparedProductionPlan BindEstimateGeneration(
            PreparedProductionPlan prepared,
            int generation)
        {
            PlanBoundEstimate? estimate = prepared.Estimate is { } bound
                ? bound with { Generation = generation }
                : null;
            EncodeExecutionSnapshot snapshot = prepared.Snapshot with { BoundEstimate = estimate };
            EncodeExecutionSnapshotBuildResult build = prepared.BuildResult with { Snapshot = snapshot };
            return prepared with { BuildResult = build, Estimate = estimate, QueueGeneration = generation };
        }

        internal bool QueueEmpiricalRefinement(
            string path,
            Guid queueItemId,
            long estimateAuthorityRevision,
            DateTime queueAcceptedUtc,
            double sourceMb,
            double preliminaryMb,
            double durationSeconds,
            string? resolution,
            string? videoCodec,
            double fps,
            EncodeExecutionSnapshotSettings previewSettings,
            EncodeExecutionSnapshotItem previewItem,
            ProductionPlanIdentity planIdentity,
            PlanBoundEstimate boundEstimate,
            QueueEstimateRefinementPriority priority,
            bool storageSavingsDecisionRelevant)
        {
            if (_previewProvider is null || boundEstimate.Authority != EstimateAuthorityLevel.Preliminary ||
                !boundEstimate.Matches(planIdentity) ||
                !string.Equals(planIdentity.SourceIdentity,
                    ProductionPlanFingerprintService.CaptureSourceIdentity([path]), StringComparison.Ordinal) ||
                !ShouldScheduleMeasuredRefinement(priority, previewSettings.StorageSavings,
                    storageSavingsDecisionRelevant, previewItem.SourceSizeBytes.GetValueOrDefault(), preliminaryMb))
                return false;

            int generation = CurrentGeneration;
            var encoder = new VideoEncoderSelection(
                previewSettings.EncoderId,
                previewSettings.CodecFamily,
                planIdentity.Inputs.Codec);
            var work = new EstimateWorkItem(
                generation, queueItemId, path, true, previewSettings.CompressionProfile,
                0, encoder, (int)Math.Round(planIdentity.Inputs.QualityValue ?? 0), null, null,
                false, false, 0, previewSettings.StorageSavings, null,
                storageSavingsDecisionRelevant, previewSettings.EncoderPreset,
                previewSettings.TenBit ? 10 : 8,
                previewSettings.IncludeConcurrentEncoderSessions)
            {
                PreviewSettings = previewSettings,
                PreviewItem = previewItem,
                EstimateAuthorityRevision = estimateAuthorityRevision,
                PlanIdentity = planIdentity,
                QueueAcceptedUtc = queueAcceptedUtc,
                RefinementPriority = priority
            };
            var refinement = new EstimateRefinementWorkItem(
                work, sourceMb, preliminaryMb, durationSeconds, resolution, videoCodec, fps,
                QualityResolution: null, SizeCalibration: null,
                EstimateDiagnostic: "Preliminary V2 fast estimate; measured refinement is advisory.",
                PlannedAudioBitrateKbps: 0, PlannedMappedAncillaryBitrateKbps: 0,
                Recommendation: null, PlanIdentity: planIdentity, BoundEstimate: boundEstimate);
            return _refinementScheduler.Request(
                queueItemId, generation, refinement, priority, _estimateCts.Token);
        }

        private void RecordEmpiricalSampleActivity(int delta)
        {
            if (delta > 0)
            {
                Interlocked.Increment(ref _empiricalSamplesLaunched);
                int active = Interlocked.Increment(ref _activeEmpiricalSamples);
                int observed;
                while (active > (observed = Volatile.Read(ref _maximumConcurrentEmpiricalSamples)) &&
                       Interlocked.CompareExchange(ref _maximumConcurrentEmpiricalSamples, active, observed) != observed) { }
            }
            else
            {
                Interlocked.Decrement(ref _activeEmpiricalSamples);
            }
        }

        public void QueueSmartEstimate(
            string path,
            bool auto,
            string profile,
            double manualTargetMb,
            VideoEncoderSelection encoder,
            int quality,
            int? targetHeight,
            int? targetAudioChannels,
            bool isCustom,
            bool recommendationsEnabled,
            double minimumSavingsPercent,
            StorageSavingsOptions storageSavings,
            EncodingQualityIntent? qualityIntent = null,
            bool sourceAdaptiveCeilingEligible = false,
            bool historicalCalibrationEnabled = true,
            Guid queueItemId = default,
            string encoderPreset = "",
            int outputBitDepth = 0,
            bool concurrentEncoderSessions = false,
            EncodeExecutionSnapshotSettings? previewSettings = null,
            EncodeExecutionSnapshotItem? previewItem = null,
            long estimateAuthorityRevision = 0,
            bool empiricalPreviewEnabled = false,
            DateTime? queueAcceptedUtc = null,
            DateTime? qualityChangeStartedUtc = null,
            ProductionPlanIdentity? planIdentity = null,
            QueueEstimateRefinementPriority refinementPriority = QueueEstimateRefinementPriority.Background)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            EncodingQualityIntent? effectiveQualityIntent =
                qualityIntent?.Kind == EncodingQualityIntentKind.AutomaticTargetBitrate &&
                manualTargetMb > 0
                    ? EncodingQualityIntent.Automatic(qualityIntent.Target ?? QualityTarget.Balanced)
                    : qualityIntent;
            _refinementPriorities[queueItemId] = refinementPriority;
            EncodeExecutionSnapshotSettings? effectivePreviewSettings = previewSettings is null
                ? null
                : previewSettings with
                {
                    // The queued immutable intent owns the rate-control route.
                    // Do not let default live settings turn an explicitly legacy
                    // quality-target request into target-bitrate VBR preparation.
                    UseAutomaticTargetBitrate =
                        effectiveQualityIntent?.Kind == EncodingQualityIntentKind.AutomaticTargetBitrate
                };

            QueueWork(new EstimateWorkItem(
                Volatile.Read(ref _generation),
                queueItemId,
                path,
                auto,
                profile,
                manualTargetMb,
                encoder,
                quality,
                targetHeight,
                targetAudioChannels,
                isCustom,
                recommendationsEnabled,
                minimumSavingsPercent,
                storageSavings,
                effectiveQualityIntent,
                sourceAdaptiveCeilingEligible,
                encoderPreset,
                outputBitDepth,
                concurrentEncoderSessions)
            { PreviewSettings = effectivePreviewSettings, PreviewItem = previewItem,
                EstimateAuthorityRevision = estimateAuthorityRevision,
                EmpiricalPreviewEnabled = empiricalPreviewEnabled,
                RefinementPriority = refinementPriority,
                PlanIdentity = planIdentity,
                QueueAcceptedUtc = queueAcceptedUtc ?? DateTime.UtcNow,
                QueuedUtc = DateTime.UtcNow,
                QualityChangeStartedUtc = qualityChangeStartedUtc });
        }

        public bool TryDequeueSmart(out SmartEstimateResult result)
        {
            while (_smartResults.TryDequeue(out result))
            {
                if (result.Generation == CurrentGeneration)
                    return true;
            }

            result = default;
            return false;
        }

        public void ResetAndCancel()
        {
            lock (_resetLock)
            {
                Interlocked.Add(ref _cancelledOrSupersededWork, Math.Max(0, Volatile.Read(ref _pendingEstimates)));
                _refinementScheduler.CancelAll();
                _deferredRefinements.Clear();
                _refinementPriorities.Clear();
                try { _estimateCts.Cancel(); } catch { }
                try { _workItems.CompleteAdding(); } catch { }
                try { _estimateCts.Dispose(); } catch { }

                _estimateCts = new CancellationTokenSource();
                _workItems = new BlockingCollection<EstimateWorkItem>();
                Interlocked.Increment(ref _generation);

                while (_smartResults.TryDequeue(out _)) { }

                _pendingEstimates = 0;
                StartWorkers();
            }
        }

        public void Dispose()
        {
            _refinementScheduler.Dispose();
            lock (_resetLock)
            {
                try { _estimateCts.Cancel(); } catch { }
                try { _workItems.CompleteAdding(); } catch { }
                try { _estimateCts.Dispose(); } catch { }
            }
        }

        private void QueueWork(EstimateWorkItem item)
        {
            lock (_resetLock)
            {
                try
                {
                    Interlocked.Increment(ref _pendingEstimates);
                    _workItems.Add(item, _estimateCts.Token);
                }
                catch
                {
                    Interlocked.Decrement(ref _pendingEstimates);
                }
            }
        }

        private void StartWorkers()
        {
            var ct = _estimateCts.Token;
            var queue = _workItems;
            _workers = Enumerable.Range(0, _workerCount)
                .Select(_ => Task.Run(() => WorkerLoopAsync(queue, ct), ct))
                .ToArray();
        }

        private async Task WorkerLoopAsync(BlockingCollection<EstimateWorkItem> queue, CancellationToken ct)
        {
            try
            {
                foreach (var item in queue.GetConsumingEnumerable(ct))
                {
                    if (ct.IsCancellationRequested)
                        break;

                    try
                    {
                        if (item.Generation == Volatile.Read(ref _generation) &&
                            !_isSourceOwnedByActiveJob(item.Path))
                        {
                            await ProcessSmartEstimateAsync(item, ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (item.Generation == Volatile.Read(ref _generation))
                            Interlocked.Decrement(ref _pendingEstimates);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during reset/dispose.
            }
        }

        private async Task ProcessSmartEstimateAsync(EstimateWorkItem item, CancellationToken cancellationToken)
        {
            if (item.QualityIntent?.Kind == EncodingQualityIntentKind.AutomaticTargetBitrate)
            {
                await ProcessAutomaticTargetEstimateAsync(item, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.CompletedTask.ConfigureAwait(false);
            Stopwatch estimateWatch = Stopwatch.StartNew();
            try
            {
                // A job may claim a source after its estimate was queued. Do not
                // begin metadata probing for a source now owned by encode/recovery.
                if (_isSourceOwnedByActiveJob(item.Path))
                    return;

                _analyzedSources.TryGetValue(item.Path, out var captured);
                var sourceBeforeProbe = new FileInfo(item.Path);
                sourceBeforeProbe.Refresh();
                bool sourceMatchesCaptured = sourceBeforeProbe.Exists
                    ? sourceBeforeProbe.Length == captured.SourceBytes &&
                      sourceBeforeProbe.LastWriteTimeUtc.Ticks == captured.SourceWriteTicks
                    : item.QualityChangeStartedUtc is not null;
                bool warmRefresh = sourceMatchesCaptured &&
                    captured.Info is not null && captured.QueueItemId == item.QueueItemId &&
                    captured.AuthorityRevision == item.EstimateAuthorityRevision;
                double srcMb = warmRefresh ? captured.SourceMb : GetMbOnDisk(item.Path);
                var info = new MediaInfoService.MediaInfo();
                Stopwatch metadataWatch = Stopwatch.StartNew();
                string? res = null;
                string? codec = null;
                double fps = 0;
                try
                {
                    info = warmRefresh ? captured.Info! : _readMetadata(item.Path);
                    int w = info.Width ?? 0;
                    int h = info.Height ?? 0;
                    if (w > 0 && h > 0)
                        res = $"{w}x{h}";

                    codec = info.VideoCodec;
                    fps = info.Fps ?? 0;
                }
                catch
                {
                    // best-effort only
                }
                metadataWatch.Stop();

                // GetInfo may synchronously launch FFprobe. If execution claimed
                // the source while it was running, skip follow-up probing and publication.
                if (_isSourceOwnedByActiveJob(item.Path))
                    return;

                double durSec;
                Stopwatch durationWatch = Stopwatch.StartNew();
                if (warmRefresh)
                {
                    durSec = captured.Duration;
                }
                else if (info.DurationSeconds is > 0)
                {
                    durSec = info.DurationSeconds.Value;
                }
                else
                {
                    if (_isSourceOwnedByActiveJob(item.Path))
                        return;
                    durSec = _mediaInfoService.GetDurationSeconds(item.Path);
                }
                durationWatch.Stop();
                if (item.Generation == CurrentGeneration && !_isSourceOwnedByActiveJob(item.Path))
                {
                    var sourceAfterProbe = new FileInfo(item.Path);
                    sourceAfterProbe.Refresh();
                    if (sourceBeforeProbe.Exists && sourceAfterProbe.Exists && sourceAfterProbe.Length > 0 && durSec > 0 &&
                        sourceBeforeProbe.Length == sourceAfterProbe.Length &&
                        sourceBeforeProbe.LastWriteTimeUtc.Ticks == sourceAfterProbe.LastWriteTimeUtc.Ticks)
                        _analyzedSources[item.Path] = (info, sourceAfterProbe.Length,
                            sourceAfterProbe.LastWriteTimeUtc.Ticks, srcMb, durSec,
                            item.QueueItemId, item.EstimateAuthorityRevision);
                    else
                        _analyzedSources.TryRemove(item.Path, out _);
                }
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage=source-metadata-ready item={item.QueueItemId:N} " +
                    $"source={Path.GetFileName(item.Path)} queueWaitMs={(DateTime.UtcNow - item.QueuedUtc).TotalMilliseconds:0} " +
                    $"metadataMs={metadataWatch.Elapsed.TotalMilliseconds:0} durationMs={durationWatch.Elapsed.TotalMilliseconds:0} " +
                    $"durationSeconds={durSec:0.###}");

                Stopwatch calculationWatch = Stopwatch.StartNew();
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage=estimate-recalculation-start item={item.QueueItemId:N} source={Path.GetFileName(item.Path)} warm={warmRefresh} qualityTarget={item.QualityIntent?.Target}");
                bool useProfileEstimate = SizeEstimateService.ShouldUseProfileEstimate(
                    item.Auto,
                    item.ManualTargetMb);
                EncodingQualityResolution? qualityResolution = ResolveAutomaticQuality(
                    item,
                    info,
                    useProfileEstimate);
                const double displayEstMb = 0;
                const double baseEstMb = 0;
                EncodingSizePredictionCalibration? sizeCalibration = null;
                SizeEstimateBreakdown? estimateBreakdown = null;
                string estimateDiagnostic =
                    "No preliminary numeric estimate is shown. Output size is measured from the exact prepared production plan.";
                bool fixedManualQuality = item.PreviewSettings is { AutomaticQuality: false, NumericQualityValue: not null } &&
                    item.QualityIntent is null;
                bool constantQuality = item.ManualTargetMb <= 0 && item.EmpiricalPreviewEnabled &&
                    !item.Profile.Equals("No Compression", StringComparison.OrdinalIgnoreCase) &&
                    (item.PreviewSettings?.AutomaticQuality == true || item.QualityIntent is not null ||
                     item.PreviewSettings?.NumericQualityValue.HasValue == true || fixedManualQuality);
                if (item.PreviewItem?.LibraryPolicyIntent?.ProjectedOutputBytes is > 0)
                    constantQuality = false;
                System.Diagnostics.Debug.WriteLine(
                    $"[SizeEstimate] {item.Path}: {estimateDiagnostic}");
                string? unavailableReason = null;
                if (srcMb <= 0)
                    unavailableReason = "Source size unavailable";

                SmartEncodeRecommendation? recommendation = null;
                SmartEncodeSourceInfo? recommendationSource = null;
                SmartEncodeIntent? recommendationIntent = null;
                calculationWatch.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Generation != CurrentGeneration || _isSourceOwnedByActiveJob(item.Path))
                    return;

                _smartResults.Enqueue(
                    new SmartEstimateResult(
                        item.Generation, item.QueueItemId, item.Path, srcMb, displayEstMb, durSec, res, codec, fps,
                        item.IsCustom, unavailableReason, null, estimateDiagnostic,
                        estimateBreakdown?.PlannedAudioBitrateKbps ?? 0,
                        estimateBreakdown?.PlannedMappedAncillaryBitrateKbps ?? 0,
                        qualityResolution, sizeCalibration, null, item.EstimateAuthorityRevision,
                        item.QueueAcceptedUtc, item.QualityChangeStartedUtc,
                        planIdentity: null, boundEstimate: null,
                        estimateState: QueueEstimateState.Pending));
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage=production-plan-pending item={item.QueueItemId:N} " +
                    $"source={Path.GetFileName(item.Path)} elapsedMs={estimateWatch.Elapsed.TotalMilliseconds:0} " +
                    $"queueWaitMs={(DateTime.UtcNow - item.QueuedUtc).TotalMilliseconds:0} " +
                    $"sinceAcceptedMs={(DateTime.UtcNow - item.QueueAcceptedUtc).TotalMilliseconds:0}" +
                    (item.QualityChangeStartedUtc is { } qualityChangeUtc
                        ? $" sinceQualityChangeMs={(DateTime.UtcNow - qualityChangeUtc).TotalMilliseconds:0}"
                        : string.Empty));
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage=estimate-computation item={item.QueueItemId:N} warm={warmRefresh} elapsedMs={calculationWatch.Elapsed.TotalMilliseconds:0.###}");
                if (item.RecommendationsEnabled)
                {
                    Stopwatch recommendationWatch = Stopwatch.StartNew();
                    int totalBitrateKbps = info.TotalBitrateKbps ??
                        (durSec > 0 && srcMb > 0
                            ? (int)Math.Round(srcMb * 8388.608d / durSec)
                            : 0);
                    int videoBitrateKbps = info.BitrateKbps ?? 0;
                    int audioBitrateKbps = info.AudioBitrateKbps ??
                        (videoBitrateKbps > 0
                            ? Math.Max(0, totalBitrateKbps - videoBitrateKbps)
                            : 0);
                    string extension = Path.GetExtension(item.Path);
                    bool likelyAnimation =
                        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".apng", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(codec, "gif", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(codec, "apng", StringComparison.OrdinalIgnoreCase);

                    try
                    {
                        recommendationSource = new SmartEncodeSourceInfo
                        {
                            Path = item.Path,
                            SourceMb = srcMb,
                            DurationSeconds = durSec,
                            Width = info.Width ?? 0,
                            Height = info.Height ?? 0,
                            FramesPerSecond = fps,
                            VideoBitrateKbps = videoBitrateKbps,
                            TotalBitrateKbps = totalBitrateKbps,
                            AudioBitrateKbps = audioBitrateKbps,
                            VideoStreamCount = info.VideoStreamCount,
                            AudioStreamCount = info.AudioStreamCount,
                            SubtitleStreamCount = info.SubtitleStreamCount,
                            VideoCodec = codec ?? "",
                            FormatName = info.FormatName ?? "",
                            FieldOrder = info.FieldOrder ?? "",
                            IsLikelyAnimation = likelyAnimation
                        };
                        recommendationIntent = new SmartEncodeIntent
                        {
                            TargetCodec = item.Encoder.FfmpegCodec,
                            TargetHeight = item.TargetHeight,
                            EstimatedOutputMb = baseEstMb,
                            MinimumSavingsPercent = item.MinimumSavingsPercent,
                            EstimateConfidenceCeiling =
                                SmartEncodeDecisionService.ResolveEstimateConfidenceCeiling(sizeCalibration)
                        };
                        recommendation = null;
                    }
                    catch (Exception ex)
                    {
                        string reason = ex.Message.Length <= 240 ? ex.Message : ex.Message[..240];
                        System.Diagnostics.Debug.WriteLine(
                            $"Recommendation inputs unavailable for {item.Path}: {reason}");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    recommendationWatch.Stop();
                    CriticalPathTiming.WriteLine(
                        $"[CriticalPathTiming] stage=recommendation-inputs-ready item={item.QueueItemId:N} " +
                        $"elapsedMs={recommendationWatch.Elapsed.TotalMilliseconds:0}");
                }

                if (constantQuality && item.PreviewSettings is not null && item.PreviewItem is not null)
                {
                    var refinement = new EstimateRefinementWorkItem(
                        item,
                        srcMb,
                        displayEstMb,
                        durSec,
                        res,
                        codec,
                        fps,
                        qualityResolution,
                        sizeCalibration,
                        estimateDiagnostic,
                        estimateBreakdown?.PlannedAudioBitrateKbps ?? 0,
                        estimateBreakdown?.PlannedMappedAncillaryBitrateKbps ?? 0,
                        recommendation,
                        PlanIdentity: null,
                        BoundEstimate: null,
                        recommendationSource,
                        recommendationIntent);
                    QueueEstimateRefinementPriority priority = _refinementPriorities.TryGetValue(
                        item.QueueItemId, out QueueEstimateRefinementPriority currentPriority)
                        ? currentPriority : item.RefinementPriority;
                    bool accepted = false;
                    if (priority == QueueEstimateRefinementPriority.Background)
                    {
                        _deferredRefinements[item.QueueItemId] = refinement;
                        QueueEstimateRefinementPriority updatedPriority = _refinementPriorities.TryGetValue(
                            item.QueueItemId, out currentPriority) ? currentPriority : priority;
                        if (updatedPriority != QueueEstimateRefinementPriority.Background &&
                            _deferredRefinements.TryRemove(item.QueueItemId, out EstimateRefinementWorkItem? promoted))
                            accepted = _refinementScheduler.Request(item.QueueItemId, item.Generation,
                                promoted, updatedPriority, cancellationToken);
                    }
                    else
                    {
                        accepted = _refinementScheduler.Request(
                            item.QueueItemId, item.Generation, refinement, priority, cancellationToken);
                    }
                    CriticalPathTiming.WriteLine(
                        $"[CriticalPathTiming] stage=estimate-refinement-queued item={item.QueueItemId:N} " +
                        $"priority={item.RefinementPriority} accepted={accepted} " +
                        $"pending={_refinementScheduler.PendingCount}");
                }

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested || item.Generation != CurrentGeneration || _isSourceOwnedByActiveJob(item.Path))
                    return;

                System.Diagnostics.Debug.WriteLine(
                    $"Error in smart estimate for {item.Path}: {ex.Message}");
                _smartResults.Enqueue(
                    new SmartEstimateResult(
                        item.Generation, item.QueueItemId, item.Path, 0, 0, 0, null, null, 0,
                        item.IsCustom, "Metadata unavailable", null,
                        $"Estimate failed: {ex.Message}", 0, 0,
                        sizeCalibration: null, estimateAuthorityRevision: item.EstimateAuthorityRevision,
                        queueAcceptedUtc: item.QueueAcceptedUtc, qualityChangeStartedUtc: item.QualityChangeStartedUtc));
            }
        }

        private async Task ProcessAutomaticTargetEstimateAsync(
            EstimateWorkItem item,
            CancellationToken cancellationToken)
        {
            if (_isSourceOwnedByActiveJob(item.Path) || item.Generation != CurrentGeneration)
                return;

            _smartResults.Enqueue(new SmartEstimateResult(
                item.Generation, item.QueueItemId, item.Path, 0, 0, 0, null, null, 0,
                item.IsCustom, null, null,
                "Inspecting source metadata and preparing the Automatic target plan.", 0, 0,
                estimateAuthorityRevision: item.EstimateAuthorityRevision,
                queueAcceptedUtc: item.QueueAcceptedUtc,
                qualityChangeStartedUtc: item.QualityChangeStartedUtc,
                estimateState: QueueEstimateState.Inspecting));

            try
            {
                if (_previewProvider is null || item.PreviewSettings is null || item.PreviewItem is null)
                    throw new PreviewUnavailableException(
                        "The queue item does not have the settings required to prepare its Automatic production plan.");

                QueueProductionPlanEstimate result = await PrepareProductionPlanAsync(
                    item.PreviewSettings,
                    item.PreviewItem,
                    item.EstimateAuthorityRevision,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Generation != CurrentGeneration || _isSourceOwnedByActiveJob(item.Path))
                    return;

                PreparedProductionPlan prepared = result.PreparedPlan;
                AutomaticTargetPlan? target = prepared.Plan.AutomaticTarget;
                if (target is null || !target.IsAvailable || prepared.Estimate is null)
                    throw new PreviewUnavailableException(target?.Reason ?? prepared.Plan.UnavailableReason ??
                        "The target planner did not produce a complete output projection.");
                if (!string.Equals(prepared.Identity.SourceIdentity,
                    ProductionPlanFingerprintService.CaptureSourceIdentity([item.Path]), StringComparison.Ordinal))
                    throw new PreviewUnavailableException("Source identity changed while the Automatic target plan was prepared.");

                double sourceMb = (prepared.Snapshot.SourceSizeBytes ?? 0) / (1024d * 1024d);
                double estimateMb = prepared.Estimate.ProjectedBytes / (1024d * 1024d);
                double duration = prepared.Snapshot.MediaDurationSeconds ?? 0;
                string? resolution = prepared.Plan.Source is { Width: > 0, Height: > 0 } source
                    ? $"{source.Width}x{source.Height}"
                    : null;
                double fps = prepared.Plan.Source?.FrameRate ?? 0;
                EncodingQualityResolution? quality = prepared.Plan.Quality;
                SmartEncodeRecommendation? recommendation = null;
                if (item.RecommendationsEnabled)
                {
                    MediaProbeResult probe = result.SourceMetadata;
                    MediaProbeStreamInfo? video = probe.Streams.FirstOrDefault(stream =>
                        stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
                    int audioCount = probe.Streams.Count(stream =>
                        stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase));
                    var sourceInfo = new SmartEncodeSourceInfo
                    {
                        Path = item.Path,
                        SourceMb = sourceMb,
                        DurationSeconds = duration,
                        Width = video?.Width ?? 0,
                        Height = video?.Height ?? 0,
                        FramesPerSecond = fps,
                        VideoBitrateKbps = target.SourceVideoBitrateKbps is > 0
                            ? (int)Math.Min(int.MaxValue, Math.Round(target.SourceVideoBitrateKbps.Value))
                            : 0,
                        TotalBitrateKbps = duration > 0 && sourceMb > 0
                            ? (int)Math.Min(int.MaxValue, Math.Round(sourceMb * 8388.608d / duration))
                            : 0,
                        AudioBitrateKbps = target.PlannedAudioBitrateKbps is > 0
                            ? (int)Math.Min(int.MaxValue, Math.Round(target.PlannedAudioBitrateKbps.Value))
                            : 0,
                        VideoStreamCount = probe.Streams.Count(stream =>
                            stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase)),
                        AudioStreamCount = audioCount,
                        SubtitleStreamCount = probe.Streams.Count(stream =>
                            stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase)),
                        VideoCodec = video?.CodecName ?? prepared.Plan.Source?.Codec ?? "",
                        FormatName = probe.FormatName,
                        FieldOrder = video?.FieldOrder ?? ""
                    };
                    var intent = new SmartEncodeIntent
                    {
                        TargetCodec = prepared.Plan.Video?.Codec ?? "",
                        TargetHeight = prepared.Plan.Video?.EffectiveHeight,
                        EstimatedOutputMb = estimateMb,
                        MinimumSavingsPercent = item.MinimumSavingsPercent,
                        EstimateConfidenceCeiling = SmartEncodeDecisionService.ResolveEstimateConfidenceCeiling(null)
                    };
                    recommendation = _evaluateRecommendation(sourceInfo, intent);
                }

                PreparedProductionPlan queuedPlan = prepared with
                {
                    QueueItemId = item.QueueItemId,
                    QueueGeneration = item.Generation
                };
                QueueEstimateState state = target.Disposition == AutomaticTargetDisposition.Review
                    ? QueueEstimateState.Review
                    : QueueEstimateState.Ready;
                _smartResults.Enqueue(new SmartEstimateResult(
                    item.Generation, item.QueueItemId, item.Path, sourceMb, estimateMb,
                    duration, resolution, prepared.Plan.Source?.Codec, fps,
                    item.IsCustom, null, recommendation, result.Diagnostic,
                    target.PlannedAudioBitrateKbps ?? 0,
                    target.PlannedMappedAncillaryBitrateKbps ?? 0,
                    quality, null, null, item.EstimateAuthorityRevision,
                    item.QueueAcceptedUtc, item.QualityChangeStartedUtc,
                    planIdentity: prepared.Identity,
                    boundEstimate: prepared.Estimate,
                    estimateState: state,
                    preparedProductionPlan: queuedPlan));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected for a queue generation or quality-profile change.
            }
            catch (PreviewUnavailableException ex)
            {
                if (item.Generation != CurrentGeneration || _isSourceOwnedByActiveJob(item.Path))
                    return;
                _smartResults.Enqueue(new SmartEstimateResult(
                    item.Generation, item.QueueItemId, item.Path, 0, 0, 0, null, null, 0,
                    item.IsCustom, ex.Message, null, ex.Message, 0, 0,
                    estimateAuthorityRevision: item.EstimateAuthorityRevision,
                    queueAcceptedUtc: item.QueueAcceptedUtc,
                    qualityChangeStartedUtc: item.QualityChangeStartedUtc,
                    estimateState: QueueEstimateState.Unavailable));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (item.Generation != CurrentGeneration || _isSourceOwnedByActiveJob(item.Path))
                    return;
                _smartResults.Enqueue(new SmartEstimateResult(
                    item.Generation, item.QueueItemId, item.Path, 0, 0, 0, null, null, 0,
                    item.IsCustom, "Automatic target estimation failed", null,
                    $"Automatic target estimation failed: {ex.Message}", 0, 0,
                    estimateAuthorityRevision: item.EstimateAuthorityRevision,
                    queueAcceptedUtc: item.QueueAcceptedUtc,
                    qualityChangeStartedUtc: item.QualityChangeStartedUtc,
                    estimateState: QueueEstimateState.Failed));
            }
        }

        private static bool ShouldScheduleMeasuredRefinement(EstimateWorkItem item, double preliminaryMb)
        {
            return ShouldScheduleMeasuredRefinement(item.RefinementPriority, item.StorageSavings,
                item.SourceAdaptiveCeilingEligible,
                item.PreviewItem?.SourceSizeBytes.GetValueOrDefault() ?? 0,
                preliminaryMb, item.Path);
        }

        private static bool ShouldScheduleMeasuredRefinement(
            QueueEstimateRefinementPriority priority,
            StorageSavingsOptions storageSavings,
            bool policyDecisionRelevant,
            long sourceBytes,
            double preliminaryMb,
            string? sourcePath = null) => priority != QueueEstimateRefinementPriority.Background;

        private async Task ProcessRefinementAsync(EstimateRefinementWorkItem refinement, CancellationToken cancellationToken)
        {
            EstimateWorkItem item = refinement.Work;
            if (item.Generation != CurrentGeneration || _isSourceOwnedByActiveJob(item.Path) ||
                _previewProvider is null || item.PreviewSettings is null || item.PreviewItem is null)
                return;

            _smartResults.Enqueue(new SmartEstimateResult(
                item.Generation, item.QueueItemId, item.Path, refinement.SourceMb, refinement.EstimatedMb,
                refinement.DurationSeconds, refinement.Resolution, refinement.VideoCodec, refinement.Fps,
                item.IsCustom, null, null, refinement.EstimateDiagnostic,
                refinement.PlannedAudioBitrateKbps, refinement.PlannedMappedAncillaryBitrateKbps,
                refinement.QualityResolution, refinement.SizeCalibration, null,
                item.EstimateAuthorityRevision, item.QueueAcceptedUtc, item.QualityChangeStartedUtc,
                planIdentity: null, boundEstimate: null,
                estimateState: QueueEstimateState.Measuring));

            Interlocked.Increment(ref _empiricalJobsLaunched);
            try
            {
                QueueProductionPlanEstimate measured = await _previewProvider.PrepareAndEstimateAsync(
                    item.PreviewSettings,
                    item.PreviewItem,
                    item.EstimateAuthorityRevision,
                    cancellationToken,
                    RecordEmpiricalSampleActivity).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Generation != CurrentGeneration || _isSourceOwnedByActiveJob(item.Path))
                    return;

                PreparedProductionPlan prepared = BindEstimateGeneration(
                    measured.PreparedPlan, item.Generation);
                ProductionPlanIdentity planIdentity = prepared.Identity;
                if (!string.Equals(planIdentity.SourceIdentity,
                    ProductionPlanFingerprintService.CaptureSourceIdentity([item.Path]), StringComparison.Ordinal))
                {
                    _smartResults.Enqueue(new SmartEstimateResult(
                        item.Generation, item.QueueItemId, item.Path, refinement.SourceMb,
                        0, refinement.DurationSeconds, refinement.Resolution,
                        refinement.VideoCodec, refinement.Fps, item.IsCustom, null,
                        null, "Measured plan was discarded because its source identity changed.",
                        refinement.PlannedAudioBitrateKbps, refinement.PlannedMappedAncillaryBitrateKbps,
                        prepared.Plan.Quality, null, null, item.EstimateAuthorityRevision,
                        item.QueueAcceptedUtc, item.QualityChangeStartedUtc,
                        planIdentity: null, boundEstimate: null,
                        estimateState: QueueEstimateState.Unavailable));
                    return;
                }

                EmpiricalPreviewResult? preview = measured.Preview;
                PlanBoundEstimate? boundEstimate = prepared.Estimate;
                double measuredMb = boundEstimate is null
                    ? 0
                    : boundEstimate.ProjectedBytes / (1024d * 1024d);
                EncodingQualityResolution? qualityResolution = prepared.Plan.Quality;
                _smartResults.Enqueue(new SmartEstimateResult(
                    item.Generation, item.QueueItemId, item.Path,
                    preview?.SourceBytes / (1024d * 1024d) ?? refinement.SourceMb, measuredMb,
                    refinement.DurationSeconds, refinement.Resolution, refinement.VideoCodec,
                    refinement.Fps, item.IsCustom, null, null,
                    preview?.Diagnostic ?? measured.Diagnostic, refinement.PlannedAudioBitrateKbps,
                    refinement.PlannedMappedAncillaryBitrateKbps, qualityResolution,
                    null, preview, item.EstimateAuthorityRevision,
                    item.QueueAcceptedUtc, item.QualityChangeStartedUtc,
                    planIdentity: planIdentity, boundEstimate: boundEstimate,
                    estimateState: boundEstimate is null ? QueueEstimateState.Unavailable : QueueEstimateState.Measured,
                    preparedProductionPlan: prepared));

                if (measuredMb > 0 && refinement.RecommendationSource is { } recommendationSource &&
                    refinement.RecommendationIntent is { } recommendationIntent)
                {
                    SmartEncodeRecommendation measuredRecommendation;
                    try
                    {
                        measuredRecommendation = _evaluateRecommendation(
                            recommendationSource,
                            new SmartEncodeIntent
                            {
                                TargetCodec = recommendationIntent.TargetCodec,
                                TargetHeight = recommendationIntent.TargetHeight,
                                EstimatedOutputMb = measuredMb,
                                MinimumSavingsPercent = recommendationIntent.MinimumSavingsPercent,
                                EstimateConfidenceCeiling = SmartEncodeDecisionService.ResolveEstimateConfidenceCeiling(null)
                            });
                    }
                    catch (Exception ex)
                    {
                        string reason = ex.Message.Length <= 240 ? ex.Message : ex.Message[..240];
                        measuredRecommendation = new SmartEncodeRecommendation
                        {
                            Kind = SmartEncodeRecommendationKind.Unavailable,
                            Confidence = SmartEncodeConfidence.Low,
                            PrimaryReason = "Recommendation unavailable; the prepared production plan remains valid.",
                            Reasons = [reason]
                        };
                    }

                    _smartResults.Enqueue(new SmartEstimateResult(
                        item.Generation, item.QueueItemId, item.Path,
                        preview?.SourceBytes / (1024d * 1024d) ?? refinement.SourceMb, measuredMb,
                        refinement.DurationSeconds, refinement.Resolution, refinement.VideoCodec,
                        refinement.Fps, item.IsCustom, null, measuredRecommendation,
                        "Recommendation evaluated against the measured prepared plan.",
                        refinement.PlannedAudioBitrateKbps, refinement.PlannedMappedAncillaryBitrateKbps,
                        qualityResolution, null, null, item.EstimateAuthorityRevision,
                        item.QueueAcceptedUtc, item.QualityChangeStartedUtc,
                        recommendationOnly: true, planIdentity: planIdentity, boundEstimate: boundEstimate,
                        estimateState: QueueEstimateState.Measured));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (item.Generation == CurrentGeneration && !_isSourceOwnedByActiveJob(item.Path))
                {
                    string reason = ex.Message.Length <= 300 ? ex.Message : ex.Message[..300];
                    _smartResults.Enqueue(new SmartEstimateResult(
                        item.Generation, item.QueueItemId, item.Path, refinement.SourceMb,
                        0, refinement.DurationSeconds, refinement.Resolution,
                        refinement.VideoCodec, refinement.Fps, item.IsCustom, null,
                        refinement.Recommendation,
                        $"Measured production plan unavailable: {reason}",
                        refinement.PlannedAudioBitrateKbps, refinement.PlannedMappedAncillaryBitrateKbps,
                        refinement.QualityResolution, refinement.SizeCalibration, null,
                        item.EstimateAuthorityRevision, item.QueueAcceptedUtc,
                        item.QualityChangeStartedUtc, planIdentity: null,
                        boundEstimate: null,
                        estimateState: QueueEstimateState.Unavailable));
                }
            }
            finally
            {
                Interlocked.Increment(ref _empiricalJobsCompleted);
            }
        }

        private static EncodingQualityResolution? ResolveAutomaticQuality(
            EstimateWorkItem item,
            MediaInfoService.MediaInfo info,
            bool useProfileEstimate)
        {
            if (item.QualityIntent == null || !useProfileEstimate ||
                info.Width is not > 0 || info.Height is not > 0 || info.Fps is not > 0)
            {
                return null;
            }

            int requestedHeight = item.TargetHeight.GetValueOrDefault(info.Height.Value);
            int requestedWidth = Math.Max(1, (int)Math.Round(
                info.Width.Value * (requestedHeight / (double)info.Height.Value)));
            VideoOutputGeometryPlan geometry = VideoOutputGeometryPlanner.Resolve(
                info.Width.Value,
                info.Height.Value,
                new VideoOutputResolutionPlan(requestedWidth, requestedHeight, string.Empty,
                    "Queue estimate output resolution"),
                item.Encoder,
                tenBit: false);
            var source = new MediaProbeResult
            {
                Success = true,
                Streams =
                [
                    new MediaProbeStreamInfo
                    {
                        CodecType = "video",
                        CodecName = info.VideoCodec ?? string.Empty,
                        Width = info.Width,
                        Height = info.Height,
                        FrameRate = info.Fps,
                        BitRate = info.BitrateKbps is > 0
                            ? info.BitrateKbps.Value * 1000L
                            : null
                    }
                ]
            };
            return new EncodingQualityPolicyService().Resolve(
                new EncodingQualityPolicyRequest(
                    item.QualityIntent,
                    source,
                    item.Encoder,
                    geometry,
                    EncodingService.ScaleMode.None,
                    item.Auto ? null : item.ManualTargetMb));
        }

        private static double GetMbOnDisk(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) return fi.Length / (1024.0 * 1024.0);
            }
            catch
            {
                // ignore IO errors
            }
            return 0;
        }
    }
}
