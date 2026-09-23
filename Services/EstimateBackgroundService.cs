using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    /// <summary>
    /// Runs background size-estimation work and exposes result queues for the UI thread.
    /// </summary>
    public sealed class EstimateBackgroundService : IDisposable
    {
        private readonly MediaInfoService _mediaInfoService;
        private readonly SmartEncodeDecisionService _decisionService = new();
        private readonly EncodingStatisticsService? _statistics;
        private readonly EncodingPredictionAccuracyService _accuracy = new();
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
            Func<string, bool>? isSourceOwnedByActiveJob = null)
        {
            _mediaInfoService = mediaInfoService ?? throw new ArgumentNullException(nameof(mediaInfoService));
            _statistics = statistics;
            _isSourceOwnedByActiveJob = isSourceOwnedByActiveJob ?? (_ => false);
            StartWorkers();
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
                EncodingSizePredictionCalibration? sizeCalibration = null)
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
                bool sourceAdaptiveCeilingEligible)
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
            public bool HistoricalCalibrationEnabled { get; init; }
        }

        // Include completed-but-not-yet-applied results so the UI does not report
        // analysis complete before the grid and item models have been refreshed.
        public int PendingEstimates =>
            Math.Max(0, Volatile.Read(ref _pendingEstimates)) + _smartResults.Count;
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
            Guid queueItemId = default)
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
                sourceAdaptiveCeilingEligible)
            { HistoricalCalibrationEnabled = historicalCalibrationEnabled });
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
            _workers = Enumerable.Range(0, _workerCount)
                .Select(_ => Task.Run(() => WorkerLoop(ct), ct))
                .ToArray();
        }

        private void WorkerLoop(CancellationToken ct)
        {
            try
            {
                foreach (var item in _workItems.GetConsumingEnumerable(ct))
                {
                    if (ct.IsCancellationRequested)
                        break;

                    try
                    {
                        if (item.Generation == Volatile.Read(ref _generation) &&
                            !_isSourceOwnedByActiveJob(item.Path))
                        {
                            ProcessSmartEstimate(item);
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

        private void ProcessSmartEstimate(EstimateWorkItem item)
        {
            try
            {
                // A job may claim a source after its estimate was queued. Do not
                // begin metadata probing for a source now owned by encode/recovery.
                if (_isSourceOwnedByActiveJob(item.Path))
                    return;

                double srcMb = GetMbOnDisk(item.Path);
                var info = new MediaInfoService.MediaInfo();
                string? res = null;
                string? codec = null;
                double fps = 0;
                try
                {
                    info = _mediaInfoService.GetInfo(item.Path);
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

                // GetInfo may synchronously launch FFprobe. If execution claimed
                // the source while it was running, skip follow-up probing and publication.
                if (_isSourceOwnedByActiveJob(item.Path))
                    return;

                double durSec;
                if (info.DurationSeconds is > 0)
                {
                    durSec = info.DurationSeconds.Value;
                }
                else
                {
                    if (_isSourceOwnedByActiveJob(item.Path))
                        return;
                    durSec = _mediaInfoService.GetDurationSeconds(item.Path);
                }

                bool useProfileEstimate = SizeEstimateService.ShouldUseProfileEstimate(
                    item.Auto,
                    item.ManualTargetMb);
                EncodingQualityResolution? qualityResolution = ResolveAutomaticQuality(
                    item,
                    info,
                    useProfileEstimate);
                int estimateQuality = qualityResolution?.EffectiveQuality ?? item.Quality;
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
                        sourceAdaptiveCeilingEligible: item.SourceAdaptiveCeilingEligible)
                    : null;
                double estMb = estimateBreakdown?.EstimatedOutputMb ??
                    (item.ManualTargetMb > 0 ? item.ManualTargetMb : 0);
                double baseEstMb = estMb;
                EncodingSizePredictionCalibration? sizeCalibration = null;
                if (baseEstMb > 0)
                {
                    try
                    {
                        int sourceHeight = info.Height ?? 0;
                        int outputHeight = item.TargetHeight is > 0 ? Math.Min(sourceHeight, item.TargetHeight.Value) : sourceHeight;
                        EncodingSizeCalibrationContext calibrationContext = new(
                            codec ?? "", item.Encoder.FfmpegCodec,
                            EncodingRuntimeEstimatorService.ResolutionTier(sourceHeight),
                            EncodingRuntimeEstimatorService.ResolutionTier(outputHeight),
                            item.Encoder.EncoderId,
                            item.Encoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase)
                                ? HardwarePerformanceService.DetectGpuIdentity() : "cpu",
                            qualityResolution?.EffectiveQuality?.ToString() ?? item.QualityIntent?.Target?.ToString() ?? item.Quality.ToString(),
                            qualityResolution?.Assessment.ToString() ?? "Unknown",
                            PredictionCodecFamily(codec) == PredictionCodecFamily(item.Encoder.FfmpegCodec));
                        sizeCalibration = _statistics == null
                            ? EncodingSizePredictionCalibration.Unavailable(baseEstMb,
                                "Historical statistics are unavailable.", AdaptivePredictionPolicies.Current.PolicyId, DateTime.UtcNow)
                            : _accuracy.CalibrateSizePrediction(
                                baseEstMb,
                                calibrationContext,
                                _statistics.GetAll(),
                                item.HistoricalCalibrationEnabled,
                                eligible: useProfileEstimate && !item.IsCustom,
                                ineligibleReason: item.IsCustom
                                    ? "Custom queue settings are not eligible for historical size calibration."
                                    : !useProfileEstimate
                                        ? "Manual target-size mode is authoritative and is not eligible for historical calibration."
                                        : "This estimate is not eligible for historical calibration.");
                    }
                    catch (Exception ex)
                    {
                        sizeCalibration = EncodingSizePredictionCalibration.Unavailable(baseEstMb,
                            $"Calibration unavailable: {ex.Message}", AdaptivePredictionPolicies.Current.PolicyId, DateTime.UtcNow);
                    }
                }
                double displayEstMb = sizeCalibration?.CalibratedPredictionMb ?? baseEstMb;
                string estimateDiagnostic = estimateBreakdown?.Diagnostic ??
                    (item.ManualTargetMb > 0
                        ? $"Manual target selected: {item.ManualTargetMb:0.##} MB."
                        : "Required metadata is unavailable.");
                if (sizeCalibration != null)
                    estimateDiagnostic = $"{estimateDiagnostic} Size calibration decision: {sizeCalibration.Decision}; {sizeCalibration.Reason} " +
                        $"Historical {sizeCalibration.Confidence} confidence, N={sizeCalibration.SampleCount}, median signed error {sizeCalibration.MedianSignedErrorPercent:+0.##;-0.##;0}%; " +
                        $"effectiveness {sizeCalibration.EffectivenessState}, evaluation N={sizeCalibration.EvaluationSampleCount}, median improvement {sizeCalibration.MedianCalibrationImprovementPercent:+0.##;-0.##;0}%." +
                        (sizeCalibration.Decision == EncodingCalibrationDecision.ShadowEvaluationOnly
                            ? $" Shadow candidate {sizeCalibration.HypotheticalCalibratedPredictionMb:0.##} MB; displayed estimate remains base {baseEstMb:0.##} MB."
                            : sizeCalibration.Applied
                                ? $" Applied correction {sizeCalibration.EffectiveCorrectionPercent:+0.##;-0.##;0}%."
                                : string.Empty);
                System.Diagnostics.Debug.WriteLine(
                    $"[SizeEstimate] {item.Path}: {estimateDiagnostic}");
                string? unavailableReason = null;
                if (srcMb <= 0)
                    unavailableReason = "Source size unavailable";
                else if (estMb <= 0)
                    unavailableReason = "Metadata unavailable";

                SmartEncodeRecommendation? recommendation = null;
                if (item.RecommendationsEnabled)
                {
                    int totalBitrateKbps = info.TotalBitrateKbps ??
                        (durSec > 0 && srcMb > 0
                            ? (int)Math.Round(srcMb * 8192d / durSec)
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

                    recommendation = _decisionService.Evaluate(
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
                            MinimumSavingsPercent = item.MinimumSavingsPercent
                        });
                }

                if (_isSourceOwnedByActiveJob(item.Path))
                    return;

                _smartResults.Enqueue(
                    new SmartEstimateResult(
                        item.Generation, item.QueueItemId, item.Path, srcMb, displayEstMb, durSec, res, codec, fps,
                        item.IsCustom, unavailableReason, recommendation, estimateDiagnostic,
                        estimateBreakdown?.PlannedAudioBitrateKbps ?? 0,
                        estimateBreakdown?.PlannedMappedAncillaryBitrateKbps ?? 0,
                        qualityResolution, sizeCalibration));
            }
            catch (Exception ex)
            {
                if (_isSourceOwnedByActiveJob(item.Path))
                    return;

                System.Diagnostics.Debug.WriteLine(
                    $"Error in smart estimate for {item.Path}: {ex.Message}");
                _smartResults.Enqueue(
                    new SmartEstimateResult(
                        item.Generation, item.QueueItemId, item.Path, 0, 0, 0, null, null, 0,
                        item.IsCustom, "Metadata unavailable", null,
                        $"Estimate failed: {ex.Message}", 0, 0,
                        sizeCalibration: null));
            }
        }

        private static string PredictionCodecFamily(string? codec)
        {
            string value = (codec ?? "").ToLowerInvariant();
            return value.Contains("265") || value.Contains("hevc") ? "hevc" : value.Contains("264") || value.Contains("avc") ? "h264" : value.Contains("av1") ? "av1" : value;
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
