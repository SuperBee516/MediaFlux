using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaFlux.Services
{
    /// <summary>
    /// Runs background size-estimation work and exposes result queues for the UI thread.
    /// </summary>
    public sealed class EstimateBackgroundService : IDisposable
    {
        private readonly SizeEstimateService _sizeEstimateService;
        private readonly MediaInfoService _mediaInfoService;
        private readonly object _resetLock = new();
        private readonly int _workerCount = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));

        private CancellationTokenSource _estimateCts = new();
        private BlockingCollection<EstimateWorkItem> _workItems = new();
        private Task[] _workers = Array.Empty<Task>();
        private int _generation;

        private readonly ConcurrentQueue<SmartEstimateResult> _smartResults = new();
        private readonly ConcurrentQueue<RangeEstimateResult> _rangeResults = new();

        private int _pendingEstimates;

        public EstimateBackgroundService(
            SizeEstimateService sizeEstimateService,
            MediaInfoService mediaInfoService)
        {
            _sizeEstimateService = sizeEstimateService ?? throw new ArgumentNullException(nameof(sizeEstimateService));
            _mediaInfoService = mediaInfoService ?? throw new ArgumentNullException(nameof(mediaInfoService));
            StartWorkers();
        }

        public readonly struct SmartEstimateResult
        {
            public string Path { get; }
            public double SourceMb { get; }
            public double EstimatedMb { get; }
            public double DurationSec { get; }
            public string? Resolution { get; }
            public string? VideoCodec { get; }

            public SmartEstimateResult(
                string path,
                double sourceMb,
                double estimatedMb,
                double durationSec,
                string? resolution,
                string? videoCodec)
            {
                Path = path;
                SourceMb = sourceMb;
                EstimatedMb = estimatedMb;
                DurationSec = durationSec;
                Resolution = resolution;
                VideoCodec = videoCodec;
            }
        }

        public readonly struct RangeEstimateResult
        {
            public string Path { get; }
            public int MinKiB { get; }
            public int MaxKiB { get; }
            public int MidKiB { get; }

            public RangeEstimateResult(string path, int minKiB, int maxKiB, int midKiB)
            {
                Path = path;
                MinKiB = minKiB;
                MaxKiB = maxKiB;
                MidKiB = midKiB;
            }
        }

        private readonly struct EstimateWorkItem
        {
            public EstimateWorkItem(
                int generation,
                bool smart,
                string path,
                bool auto,
                string profile,
                double manualTargetMb,
                string codec,
                int quality)
            {
                Generation = generation;
                Smart = smart;
                Path = path;
                Auto = auto;
                Profile = profile;
                ManualTargetMb = manualTargetMb;
                Codec = codec;
                Quality = quality;
            }

            public int Generation { get; }
            public bool Smart { get; }
            public string Path { get; }
            public bool Auto { get; }
            public string Profile { get; }
            public double ManualTargetMb { get; }
            public string Codec { get; }
            public int Quality { get; }
        }

        public int PendingEstimates => _pendingEstimates;

        public void QueueSmartEstimate(string path, bool auto, string profile, double manualTargetMb)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            QueueWork(new EstimateWorkItem(
                Volatile.Read(ref _generation),
                smart: true,
                path,
                auto,
                profile,
                manualTargetMb,
                codec: "",
                quality: 0));
        }

        public void QueueRangeEstimate(string path, string codec, int quality)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            QueueWork(new EstimateWorkItem(
                Volatile.Read(ref _generation),
                smart: false,
                path,
                auto: false,
                profile: "",
                manualTargetMb: 0,
                codec,
                quality));
        }

        public bool TryDequeueSmart(out SmartEstimateResult result)
            => _smartResults.TryDequeue(out result);

        public bool TryDequeueRange(out RangeEstimateResult result)
            => _rangeResults.TryDequeue(out result);

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

                while (_rangeResults.TryDequeue(out _)) { }
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
                            if (item.Smart)
                                ProcessSmartEstimate(item);
                            else
                                ProcessRangeEstimate(item);
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
                if (srcMb <= 0) srcMb = 1.0;

                double estMb = item.Auto
                    ? _sizeEstimateService.EstimateAutoTargetMbSmart(item.Path, item.Profile)
                    : item.ManualTargetMb > 0 ? item.ManualTargetMb : srcMb * 0.5;

                if (estMb <= 0) estMb = srcMb * 0.5;

                double durSec = _mediaInfoService.GetDurationSeconds(item.Path);
                string? res = null;
                string? codec = null;
                try
                {
                    var (w, h) = _mediaInfoService.GetResolutionPixels(item.Path);
                    if (w > 0 && h > 0)
                        res = $"{w}x{h}";

                    codec = _mediaInfoService.GetVideoCodec(item.Path);
                }
                catch
                {
                    // best-effort only
                }

                _smartResults.Enqueue(
                    new SmartEstimateResult(item.Path, srcMb, estMb, durSec, res, codec));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error in smart estimate for {item.Path}: {ex.Message}");
                _smartResults.Enqueue(
                    new SmartEstimateResult(item.Path, 0, 0, 0, null, null));
            }
        }

        private void ProcessRangeEstimate(EstimateWorkItem item)
        {
            try
            {
                var r = _sizeEstimateService.EstimateSizeRangeKiB(item.Path, item.Codec, item.Quality);
                _rangeResults.Enqueue(
                    new RangeEstimateResult(item.Path, r.minKiB, r.maxKiB, r.midKiB));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error in range estimate for {item.Path}: {ex.Message}");
                _rangeResults.Enqueue(new RangeEstimateResult(item.Path, 0, 0, 0));
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
