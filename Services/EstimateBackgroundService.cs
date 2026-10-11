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
        // Captured analysis is reused during settings-only refreshes. Normal analysis
        // still asks MediaInfoService to validate the file signature.
        private readonly ConcurrentDictionary<string, (MediaInfoService.MediaInfo Info, double SourceMb, double Duration,
            Guid QueueItemId, long AuthorityRevision)> _analyzedSources =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly QueueEmpiricalPreviewProvider? _previewProvider;
        private readonly Func<SmartEncodeSourceInfo, SmartEncodeIntent, SmartEncodeRecommendation> _evaluateRecommendation;
        private readonly Func<string, MediaInfoService.MediaInfo> _readMetadata;
        private readonly Func<string, bool> _isSourceOwnedByActiveJob;
        private readonly object _resetLock = new();
        private readonly int _workerCount = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));

        private CancellationTokenSource _estimateCts = new();
        private BlockingCollection<EstimateWorkItem> _workItems = new();
        private Task[] _workers = Array.Empty<Task>();
        private int _generation;

        private readonly ConcurrentQueue<SmartEstimateResult> _smartResults = new();

        private int _pendingEstimates;

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
                bool recommendationOnly = false)
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
            public DateTime QueueAcceptedUtc { get; init; }
            public DateTime QueuedUtc { get; init; }
            public DateTime? QualityChangeStartedUtc { get; init; }
        }

        // Include completed-but-not-yet-applied results so the UI does not report
        // analysis complete before the grid and item models have been refreshed.
        public int PendingEstimates =>
            Math.Max(0, Volatile.Read(ref _pendingEstimates)) + _smartResults.Count(result => !result.RecommendationOnly);
        public int CurrentGeneration => Volatile.Read(ref _generation);

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
            DateTime? qualityChangeStartedUtc = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

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
                qualityIntent,
                sourceAdaptiveCeilingEligible,
                encoderPreset,
                outputBitDepth,
                concurrentEncoderSessions)
            { PreviewSettings = previewSettings, PreviewItem = previewItem,
                EstimateAuthorityRevision = estimateAuthorityRevision,
                EmpiricalPreviewEnabled = empiricalPreviewEnabled,
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
            Stopwatch estimateWatch = Stopwatch.StartNew();
            try
            {
                // A job may claim a source after its estimate was queued. Do not
                // begin metadata probing for a source now owned by encode/recovery.
                if (_isSourceOwnedByActiveJob(item.Path))
                    return;

                _analyzedSources.TryGetValue(item.Path, out var captured);
                bool warmRefresh = item.QualityChangeStartedUtc is not null &&
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
                if (!warmRefresh && item.Generation == CurrentGeneration && !_isSourceOwnedByActiveJob(item.Path))
                {
                    if (srcMb > 0 && durSec > 0)
                        _analyzedSources[item.Path] = (info, srcMb, durSec,
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
                int estimateQuality = qualityResolution?.EffectiveQuality ?? item.Quality;
                double manualNvencPresetRateMultiplier =
                    item.QualityIntent is null &&
                    !item.IsCustom &&
                    item.Encoder.FfmpegCodec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)
                        ? V2FastEstimateService.GetPresetRateMultiplier(
                            item.Encoder.FfmpegCodec,
                            item.EncoderPreset)
                        : 1d;
                SizeEstimateBreakdown? estimateBreakdown = useProfileEstimate
                    ? SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                        srcMb,
                        durSec,
                        info.Width ?? 0,
                        info.Height ?? 0,
                        fps,
                        info.BitrateKbps ?? 0,
                        codec,
                        item.Profile,
                        item.Encoder.FfmpegCodec,
                        estimateQuality,
                        item.TargetHeight,
                        info.AudioBitrateKbps ?? 0,
                        info.AudioStreamCount,
                        item.TargetAudioChannels,
                        info.TotalBitrateKbps ?? 0,
                        info.SubtitleBitrateKbps ?? 0,
                        info.SubtitleStreamCount,
                        info.DataBitrateKbps ?? 0,
                        info.DataStreamCount,
                        info.AttachmentStreamCount,
                        info.AttachmentSizeBytes,
                        item.StorageSavings,
                        sourceAdaptiveCeilingEligible: item.SourceAdaptiveCeilingEligible,
                        useResolvedQualityTarget: qualityResolution is
                        {
                            Intent: { Kind: EncodingQualityIntentKind.QualityTarget },
                            EffectiveQuality: not null,
                            IsSupersededByTargetSize: false
                        },
                        presetRateMultiplier: manualNvencPresetRateMultiplier)
                    : null;
                double estMb = estimateBreakdown?.EstimatedOutputMb ??
                    (item.ManualTargetMb > 0 ? item.ManualTargetMb : 0);
                double baseEstMb = estMb;
                EncodingSizePredictionCalibration? sizeCalibration = null;
                if (baseEstMb > 0)
                    sizeCalibration = EncodingSizePredictionCalibration.Unavailable(baseEstMb,
                        "Deterministic metadata arithmetic; optional history is not queried.") with
                    { EstimateModelId = useProfileEstimate ? "BitrateArithmetic" : "ManualTarget", EstimateStatus = "Arithmetic",
                        EstimatePredictedVideoBitrateKbps = estimateBreakdown?.TargetVideoBitrateKbps };
                double displayEstMb = baseEstMb;
                string estimateDiagnostic = estimateBreakdown?.Diagnostic ??
                    (item.ManualTargetMb > 0 ? $"Manual target selected: {item.ManualTargetMb:0.##} MiB."
                        : "Required metadata is unavailable.");
                bool constantQuality = item.EmpiricalPreviewEnabled && useProfileEstimate &&
                    !item.Profile.Equals("No Compression", StringComparison.OrdinalIgnoreCase) &&
                    (item.PreviewSettings?.AutomaticQuality == true || item.QualityIntent is not null ||
                     item.PreviewItem?.CustomCompressionProfile is null &&
                     (!item.StorageSavings.Enabled || !SizeEstimateService.IsHevcCodec(item.Encoder.FfmpegCodec) || item.StorageSavings.UsesQualityTarget));
                if (item.PreviewItem?.LibraryPolicyIntent?.ProjectedOutputBytes is > 0)
                    constantQuality = false;
                EmpiricalPreviewResult? preview = null;
                if (constantQuality)
                {
                    string fallbackReason;
                    try
                    {
                        if (_previewProvider is null || item.PreviewSettings is null || item.PreviewItem is null)
                            throw new PreviewUnavailableException("Empirical preview tools/settings are unavailable.");
                        cancellationToken.ThrowIfCancellationRequested();
                        var measured = await _previewProvider.EstimateAsync(item.PreviewSettings,
                            item.PreviewItem with { EstimatedTargetMb = baseEstMb }, cancellationToken).ConfigureAwait(false);
                        preview = measured.Result;
                        qualityResolution = measured.Quality;
                        displayEstMb = baseEstMb = estMb = preview.ProjectedTotalBytes / (1024d * 1024);
                        srcMb = preview.SourceBytes / (1024d * 1024);
                        sizeCalibration = EncodingSizePredictionCalibration.Unavailable(baseEstMb,
                            "Empirical video projection; historical Generic BPP calibration bypassed.") with
                        {
                            EstimateModelId = EmpiricalPreviewResult.ModelId, EstimateStatus = "AdvisoryPreview",
                            EstimatePredictedVideoBitrateKbps = preview.Candidates.Single(c => c.Quality == preview.DisplayQuality).MeanVideoBytesPerSecond * 8 / 1000
                        };
                        estimateDiagnostic = preview.Diagnostic;
                        fallbackReason = "";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        fallbackReason = ex.Message.Length <= 300 ? ex.Message : ex.Message[..300];
                    }
                    if (preview is null)
                    {
                        sizeCalibration = (sizeCalibration ?? EncodingSizePredictionCalibration.Unavailable(baseEstMb, fallbackReason)) with
                        { EstimateModelId = "GenericBppV1", EstimateStatus = "LowConfidenceGenericFallback:PreviewUnavailable" };
                        estimateDiagnostic = $"EstimateSource=GenericBppV1; low confidence / provisional fallback; preview unavailable: {fallbackReason}. {estimateDiagnostic}";
                    }
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[SizeEstimate] {item.Path}: {estimateDiagnostic}");
                string? unavailableReason = null;
                if (srcMb <= 0)
                    unavailableReason = "Source size unavailable";
                else if (estMb <= 0)
                    unavailableReason = "Metadata unavailable";

                SmartEncodeRecommendation? recommendation = null;
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
                        qualityResolution, sizeCalibration, preview, item.EstimateAuthorityRevision,
                        item.QueueAcceptedUtc, item.QualityChangeStartedUtc));
                string estimateReadyStage = item.QualityChangeStartedUtc is null
                    ? "first-estimate-ready"
                    : "revised-estimate-ready";
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage={estimateReadyStage} item={item.QueueItemId:N} " +
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
                        recommendation = _evaluateRecommendation(
                            new SmartEncodeSourceInfo
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
                        },
                            new SmartEncodeIntent
                        {
                            TargetCodec = item.Encoder.FfmpegCodec,
                            TargetHeight = item.TargetHeight,
                            EstimatedOutputMb = baseEstMb,
                            MinimumSavingsPercent = item.MinimumSavingsPercent,
                            EstimateConfidenceCeiling =
                                SmartEncodeDecisionService.ResolveEstimateConfidenceCeiling(sizeCalibration)
                            });
                    }
                    catch (Exception ex)
                    {
                        string reason = ex.Message.Length <= 240 ? ex.Message : ex.Message[..240];
                        recommendation = new SmartEncodeRecommendation
                        {
                            Kind = SmartEncodeRecommendationKind.Unavailable,
                            Confidence = SmartEncodeConfidence.Low,
                            PrimaryReason = "Recommendation unavailable; the size estimate remains valid.",
                            Reasons = [reason]
                        };
                        estimateDiagnostic += $" Recommendation unavailable: {reason}. The size estimate remains valid.";
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.Generation == CurrentGeneration && !_isSourceOwnedByActiveJob(item.Path))
                        _smartResults.Enqueue(new SmartEstimateResult(
                            item.Generation, item.QueueItemId, item.Path, srcMb, displayEstMb, durSec, res, codec, fps,
                            item.IsCustom, unavailableReason, recommendation, estimateDiagnostic,
                            estimateBreakdown?.PlannedAudioBitrateKbps ?? 0,
                            estimateBreakdown?.PlannedMappedAncillaryBitrateKbps ?? 0,
                            qualityResolution, sizeCalibration, preview, item.EstimateAuthorityRevision,
                            item.QueueAcceptedUtc, item.QualityChangeStartedUtc, recommendationOnly: true));
                    recommendationWatch.Stop();
                    CriticalPathTiming.WriteLine(
                        $"[CriticalPathTiming] stage=recommendation-ready item={item.QueueItemId:N} " +
                        $"elapsedMs={recommendationWatch.Elapsed.TotalMilliseconds:0} kind={recommendation.Kind}");
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
