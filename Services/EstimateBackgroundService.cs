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
        private readonly object _resetLock = new();
        private readonly int _workerCount = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));

        private CancellationTokenSource _estimateCts = new();
        private BlockingCollection<EstimateWorkItem> _workItems = new();
        private Task[] _workers = Array.Empty<Task>();
        private int _generation;

        private readonly ConcurrentQueue<SmartEstimateResult> _smartResults = new();

        private int _pendingEstimates;

        public EstimateBackgroundService(MediaInfoService mediaInfoService)
        {
            _mediaInfoService = mediaInfoService ?? throw new ArgumentNullException(nameof(mediaInfoService));
            StartWorkers();
        }

        public readonly struct SmartEstimateResult
        {
            public int Generation { get; }
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

            public SmartEstimateResult(
                int generation,
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
                double plannedAudioBitrateKbps)
            {
                Generation = generation;
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
            }
        }

        private readonly struct EstimateWorkItem
        {
            public EstimateWorkItem(
                int generation,
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
                StorageSavingsOptions storageSavings)
            {
                Generation = generation;
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
            }

            public int Generation { get; }
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
            StorageSavingsOptions storageSavings)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            QueueWork(new EstimateWorkItem(
                Volatile.Read(ref _generation),
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
                storageSavings));
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
                        if (item.Generation == Volatile.Read(ref _generation))
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

                double durSec = info.DurationSeconds is > 0
                    ? info.DurationSeconds.Value
                    : _mediaInfoService.GetDurationSeconds(item.Path);

                bool useProfileEstimate = SizeEstimateService.ShouldUseProfileEstimate(
                    item.Auto,
                    item.ManualTargetMb);
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
                        item.Quality,
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
                        item.StorageSavings)
                    : null;
                double estMb = estimateBreakdown?.EstimatedOutputMb ??
                    (item.ManualTargetMb > 0 ? item.ManualTargetMb : 0);
                string estimateDiagnostic = estimateBreakdown?.Diagnostic ??
                    (item.ManualTargetMb > 0
                        ? $"Manual target selected: {item.ManualTargetMb:0.##} MB."
                        : "Required metadata is unavailable.");
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
                            EstimatedOutputMb = estMb,
                            MinimumSavingsPercent = item.MinimumSavingsPercent
                        });
                }

                _smartResults.Enqueue(
                    new SmartEstimateResult(
                        item.Generation, item.Path, srcMb, estMb, durSec, res, codec, fps,
                        item.IsCustom, unavailableReason, recommendation, estimateDiagnostic,
                        estimateBreakdown?.PlannedAudioBitrateKbps ?? 0));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error in smart estimate for {item.Path}: {ex.Message}");
                _smartResults.Enqueue(
                    new SmartEstimateResult(
                        item.Generation, item.Path, 0, 0, 0, null, null, 0,
                        item.IsCustom, "Metadata unavailable", null,
                        $"Estimate failed: {ex.Message}", 0));
            }
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
