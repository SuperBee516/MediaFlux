using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Encode.Services
{
    /// <summary>
    /// Runs background size-estimation work and exposes
    /// result queues for the UI to consume on the UI thread.
    /// </summary>
    public sealed class EstimateBackgroundService : IDisposable
    {
        private readonly SizeEstimateService _sizeEstimateService;
        private readonly MediaInfoService _mediaInfoService;

        // Limit parallel estimation work; leave 1 core for UI
        private readonly SemaphoreSlim _estimateLimiter =
            new(Math.Max(1, Environment.ProcessorCount - 1));

        private CancellationTokenSource _estimateCts = new();

        private readonly ConcurrentQueue<SmartEstimateResult> _smartResults = new();
        private readonly ConcurrentQueue<RangeEstimateResult> _rangeResults = new();

        private int _pendingEstimates;

        public EstimateBackgroundService(
            SizeEstimateService sizeEstimateService,
            MediaInfoService mediaInfoService)
        {
            _sizeEstimateService = sizeEstimateService ?? throw new ArgumentNullException(nameof(sizeEstimateService));
            _mediaInfoService = mediaInfoService ?? throw new ArgumentNullException(nameof(mediaInfoService));
        }

        // ───────────────────── Result types ─────────────────────

        public readonly struct SmartEstimateResult
        {
            public string Path { get; }
            public double SourceMb { get; }
            public double EstimatedMb { get; }
            public double DurationSec { get; }
            public string? Resolution { get; }

            public SmartEstimateResult(
                string path,
                double sourceMb,
                double estimatedMb,
                double durationSec,
                string? resolution)
            {
                Path = path;
                SourceMb = sourceMb;
                EstimatedMb = estimatedMb;
                DurationSec = durationSec;
                Resolution = resolution;
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

        public int PendingEstimates => _pendingEstimates;

        // ───────────────────── Queue work ─────────────────────

        public void QueueSmartEstimate(string path, bool auto, string profile, double manualTargetMb)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var ct = _estimateCts.Token;
            Interlocked.Increment(ref _pendingEstimates);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _estimateLimiter.WaitAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;

                    double srcMb = GetMbOnDisk(path);
                    if (srcMb <= 0) srcMb = 1.0;

                    double estMb;
                    if (auto)
                    {
                        estMb = _sizeEstimateService.EstimateAutoTargetMbSmart(path, profile);
                    }
                    else
                    {
                        estMb = manualTargetMb > 0 ? manualTargetMb : srcMb * 0.5;
                    }

                    if (estMb <= 0) estMb = srcMb * 0.5;

                    double durSec = _mediaInfoService.GetDurationSeconds(path);
                    string? res = null;
                    try
                    {
                        var (w, h) = _mediaInfoService.GetResolutionPixels(path);
                        if (w > 0 && h > 0)
                            res = $"{w}x{h}";
                    }
                    catch
                    {
                        // best-effort only
                    }

                    _smartResults.Enqueue(
                        new SmartEstimateResult(path, srcMb, estMb, durSec, res));
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Error in smart estimate for {path}: {ex.Message}");
                    _smartResults.Enqueue(
                        new SmartEstimateResult(path, 0, 0, 0, null));
                }
                finally
                {
                    try { _estimateLimiter.Release(); } catch { }
                    Interlocked.Decrement(ref _pendingEstimates);
                }
            }, ct);
        }

        public void QueueRangeEstimate(string path, string codec, int quality)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var ct = _estimateCts.Token;
            Interlocked.Increment(ref _pendingEstimates);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _estimateLimiter.WaitAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;

                    var r = _sizeEstimateService.EstimateSizeRangeKiB(path, codec, quality);
                    _rangeResults.Enqueue(
                        new RangeEstimateResult(path, r.minKiB, r.maxKiB, r.midKiB));
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Error in range estimate for {path}: {ex.Message}");
                    _rangeResults.Enqueue(new RangeEstimateResult(path, 0, 0, 0));
                }
                finally
                {
                    try { _estimateLimiter.Release(); } catch { }
                    Interlocked.Decrement(ref _pendingEstimates);
                }
            }, ct);
        }

        // ───────────────────── Consume results ─────────────────────

        public bool TryDequeueSmart(out SmartEstimateResult result)
            => _smartResults.TryDequeue(out result);

        public bool TryDequeueRange(out RangeEstimateResult result)
            => _rangeResults.TryDequeue(out result);

        // ───────────────────── Reset / cancel ─────────────────────

        public void ResetAndCancel()
        {
            try { _estimateCts.Cancel(); } catch { }
            try { _estimateCts.Dispose(); } catch { }

            _estimateCts = new CancellationTokenSource();

            while (_rangeResults.TryDequeue(out _)) { }
            while (_smartResults.TryDequeue(out _)) { }

            _pendingEstimates = 0;
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

        public void Dispose()
        {
            ResetAndCancel();
            _estimateLimiter.Dispose();
        }
    }
}
