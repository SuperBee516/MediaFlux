using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using MediaFlux.Models;

namespace MediaFlux.Services.LibraryCatalog
{
    public interface ILibraryMetadataProbe
    {
        string ToolVersion { get; }
        Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
    }

    public sealed class FfprobeLibraryMetadataProbe : ILibraryMetadataProbe
    {
        private readonly FfprobeService _service;

        public FfprobeLibraryMetadataProbe(
            string applicationDirectory,
            string? configuredFfprobePath = null,
            IMediaToolProcessRunner? processRunner = null,
            TimeSpan? timeout = null)
        {
            FfmpegToolPaths paths = FfmpegToolResolver.Resolve(
                applicationDirectory,
                configuredFfprobePath: configuredFfprobePath);
            _service = new FfprobeService(paths.FfprobePath, processRunner ?? new MediaToolProcessRunner(), timeout);
            ToolVersion = ReadToolVersion(paths.FfprobePath);
        }

        public string ToolVersion { get; }

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            _service.ProbeAsync(path, cancellationToken);

        private static string ReadToolVersion(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                string version = info.FileVersion ?? info.ProductVersion ?? "";
                if (!string.IsNullOrWhiteSpace(version))
                    return $"ffprobe-{version.Trim()}";
                if (File.Exists(path))
                    return $"ffprobe-file-{File.GetLastWriteTimeUtc(path).Ticks}";
            }
            catch
            {
                // The metadata schema version still provides deterministic invalidation.
            }
            return "ffprobe-unknown";
        }
    }

    public sealed record LibraryEnrichmentOptions(
        int WorkerCount = 2,
        int QueueCapacity = 128,
        int PendingClaimBatchSize = 64,
        int MaxAttempts = 3,
        TimeSpan? RetryBaseDelay = null,
        TimeSpan? RetryPollInterval = null,
        TimeSpan? EncodingThrottleDelay = null)
    {
        public TimeSpan EffectiveRetryBaseDelay => RetryBaseDelay ?? TimeSpan.FromMinutes(2);
        public TimeSpan EffectiveRetryPollInterval => RetryPollInterval ?? TimeSpan.FromSeconds(30);
        public TimeSpan EffectiveEncodingThrottleDelay => EncodingThrottleDelay ?? TimeSpan.FromSeconds(2);

        public LibraryEnrichmentOptions Validate()
        {
            if (WorkerCount < 1 || WorkerCount > 8)
                throw new ArgumentOutOfRangeException(nameof(WorkerCount));
            if (QueueCapacity < WorkerCount || QueueCapacity > 10_000)
                throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
            if (PendingClaimBatchSize < 1 || PendingClaimBatchSize > QueueCapacity)
                throw new ArgumentOutOfRangeException(nameof(PendingClaimBatchSize));
            if (MaxAttempts < 1 || MaxAttempts > 10)
                throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
            return this;
        }
    }

    public sealed record LibraryEnrichmentProgress(
        long Completed,
        long Failed,
        int Queued,
        int Active,
        string CurrentPath,
        string Activity,
        bool IsEncodingThrottled);

    public sealed class LibraryEnrichmentCoordinator : ILibraryEnrichmentSink, IAsyncDisposable
    {
        public const int CurrentMetadataVersion = 2;
        private readonly ILibraryCatalog _catalog;
        private readonly ILibraryMetadataProbe _probe;
        private readonly LibraryEnrichmentOptions _options;
        private readonly Func<bool> _isEncodingActive;
        private readonly Channel<LibraryEnrichmentRequest> _channel;
        private readonly ConcurrentDictionary<long, byte> _queuedFiles = new();
        private readonly LibraryStorageScheduler _storageScheduler;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly SemaphoreSlim _pendingSignal = new(0, 1);
        private readonly List<Task> _workers = new();
        private Task? _retryLoop;
        private long _completed;
        private long _failed;
        private int _queued;
        private int _active;
        private bool _started;
        private int _encodingThrottled;

        public LibraryEnrichmentCoordinator(
            ILibraryCatalog catalog,
            ILibraryMetadataProbe probe,
            LibraryEnrichmentOptions? options = null,
            Func<bool>? isEncodingActive = null,
            LibraryStorageScheduler? storageScheduler = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _options = (options ?? new LibraryEnrichmentOptions()).Validate();
            _isEncodingActive = isEncodingActive ?? (() => false);
            _storageScheduler = storageScheduler ?? new LibraryStorageScheduler();
            _channel = Channel.CreateBounded<LibraryEnrichmentRequest>(new BoundedChannelOptions(_options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public event EventHandler<LibraryEnrichmentProgress>? ProgressChanged;
        public bool IsRunning => Volatile.Read(ref _active) > 0 || Volatile.Read(ref _queued) > 0;
        public int QueuedCount => Volatile.Read(ref _queued);
        public int ActiveCount => Volatile.Read(ref _active);
        public bool IsEncodingThrottled => Volatile.Read(ref _encodingThrottled) != 0;

        public void Start()
        {
            if (_started)
                return;
            _started = true;
            for (int index = 0; index < _options.WorkerCount; index++)
                _workers.Add(Task.Run(() => WorkerAsync(_shutdown.Token)));
            _retryLoop = Task.Run(() => RetryLoopAsync(_shutdown.Token));
        }

        public async ValueTask EnqueueAsync(
            LibraryEnrichmentRequest request,
            CancellationToken cancellationToken)
        {
            if (!_started)
                throw new InvalidOperationException("The enrichment coordinator has not been started.");
            if (!_queuedFiles.TryAdd(request.FileId, 0))
                return;
            Interlocked.Increment(ref _queued);
            try
            {
                await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                RaiseProgress(request.FullPath, "Queued for metadata");
            }
            catch
            {
                Interlocked.Decrement(ref _queued);
                _queuedFiles.TryRemove(request.FileId, out _);
                throw;
            }
        }

        public bool TryEnqueue(LibraryEnrichmentRequest request)
        {
            if (!_started)
                throw new InvalidOperationException("The enrichment coordinator has not been started.");
            if (!_queuedFiles.TryAdd(request.FileId, 0))
                return true;

            Interlocked.Increment(ref _queued);
            if (_channel.Writer.TryWrite(request))
            {
                RaiseProgress(request.FullPath, "Queued for metadata");
                return true;
            }

            Interlocked.Decrement(ref _queued);
            _queuedFiles.TryRemove(request.FileId, out _);
            NotifyPendingWork();
            return false;
        }

        public void NotifyPendingWork()
        {
            try { _pendingSignal.Release(); }
            catch (SemaphoreFullException) { }
        }

        public async Task<int> QueuePendingAsync(CancellationToken cancellationToken = default)
        {
            int queued = 0;
            IReadOnlyList<LibraryEnrichmentCandidate> candidates = _catalog.ClaimEnrichmentBatch(
                _options.PendingClaimBatchSize,
                CurrentMetadataVersion,
                _probe.ToolVersion,
                DateTime.UtcNow);
            foreach (LibraryEnrichmentCandidate candidate in candidates)
            {
                await EnqueueAsync(
                    new LibraryEnrichmentRequest(
                        candidate.FileId,
                        candidate.FullPath,
                        candidate.VolumeId,
                        candidate.SizeBytes,
                        candidate.LastWriteUtc,
                        candidate.AttemptCount),
                    cancellationToken).ConfigureAwait(false);
                queued++;
            }
            return queued;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_started)
            {
                _shutdown.Dispose();
                _pendingSignal.Dispose();
                return;
            }
            _shutdown.Cancel();
            _channel.Writer.TryComplete();
            try
            {
                await Task.WhenAll(_workers.Append(_retryLoop ?? Task.CompletedTask)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _shutdown.Dispose();
            _pendingSignal.Dispose();
        }

        private async Task WorkerAsync(CancellationToken cancellationToken)
        {
            await foreach (LibraryEnrichmentRequest request in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _active);
                Interlocked.Decrement(ref _queued);
                bool slotReleased = false;
                try
                {
                    while (_isEncodingActive())
                    {
                        Volatile.Write(ref _encodingThrottled, 1);
                        RaiseProgress(request.FullPath, "Waiting for active encoding");
                        await Task.Delay(_options.EffectiveEncodingThrottleDelay, cancellationToken).ConfigureAwait(false);
                    }
                    Volatile.Write(ref _encodingThrottled, 0);

                    RaiseProgress(request.FullPath, "Waiting for storage access");
                    await using (await _storageScheduler.AcquireAsync(
                                     request.FullPath,
                                     request.VolumeId,
                                     cancellationToken).ConfigureAwait(false))
                    {
                        RaiseProgress(request.FullPath, "Reading media metadata");
                        MediaProbeResult result = await _probe.ProbeAsync(request.FullPath, cancellationToken).ConfigureAwait(false);
                        DateTime now = DateTime.UtcNow;
                        LibraryMediaMetadata metadata = LibraryMetadataMapper.Map(
                            request,
                            result,
                            CurrentMetadataVersion,
                            _probe.ToolVersion,
                            now,
                            result.Success || request.AttemptCount >= _options.MaxAttempts
                                ? null
                                : now + RetryDelay(request.AttemptCount));
                        // Once probing is complete, release the in-memory slot before
                        // saving: a stale save can make this file pending again.
                        _queuedFiles.TryRemove(request.FileId, out _);
                        slotReleased = true;
                        _catalog.SaveMediaMetadata(metadata);
                        if (result.Success)
                            Interlocked.Increment(ref _completed);
                        else
                            Interlocked.Increment(ref _failed);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DateTime now = DateTime.UtcNow;
                    if (!slotReleased)
                    {
                        _queuedFiles.TryRemove(request.FileId, out _);
                        slotReleased = true;
                    }
                    _catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(
                        request,
                        MediaProbeResult.Failed(ex.Message),
                        CurrentMetadataVersion,
                        _probe.ToolVersion,
                        now,
                        request.AttemptCount >= _options.MaxAttempts ? null : now + RetryDelay(request.AttemptCount)));
                    Interlocked.Increment(ref _failed);
                }
                finally
                {
                    Volatile.Write(ref _encodingThrottled, 0);
                    if (!slotReleased)
                        _queuedFiles.TryRemove(request.FileId, out _);
                    Interlocked.Decrement(ref _active);
                    RaiseProgress(request.FullPath, "Metadata item finished");
                    if (QueuedCount < _options.QueueCapacity / 2)
                        NotifyPendingWork();
                }
            }
        }

        private async Task RetryLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _pendingSignal.WaitAsync(_options.EffectiveRetryPollInterval, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (QueuedCount < _options.QueueCapacity / 2)
                    await QueuePendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private TimeSpan RetryDelay(int attempt)
        {
            double multiplier = Math.Pow(2, Math.Clamp(attempt - 1, 0, 6));
            return TimeSpan.FromTicks(checked((long)(_options.EffectiveRetryBaseDelay.Ticks * multiplier)));
        }

        private void RaiseProgress(string path, string activity) => ProgressChanged?.Invoke(this, new LibraryEnrichmentProgress(
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _failed),
            Volatile.Read(ref _queued),
            Volatile.Read(ref _active),
            path,
            activity,
            IsEncodingThrottled));
    }

    internal static class LibraryMetadataMapper
    {
        public static LibraryMediaMetadata Map(
            LibraryEnrichmentRequest request,
            MediaProbeResult probe,
            int metadataVersion,
            string toolVersion,
            DateTime attemptedUtc,
            DateTime? nextRetryUtc)
        {
            MediaProbeStreamInfo? video = probe.Streams
                .Where(stream => string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(stream => stream.Dispositions.TryGetValue("default", out bool isDefault) && isDefault)
                .FirstOrDefault();
            var audio = probe.Streams
                .Where(stream => string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
                .Select(stream => new LibraryAudioStreamMetadata(
                    stream.CodecName,
                    stream.Channels,
                    stream.ChannelLayout,
                    stream.Language,
                    stream.BitRate is > 0 ? stream.BitRate : null,
                    stream.SampleRateHz is > 0 ? stream.SampleRateHz : null))
                .ToArray();
            var subtitles = probe.Streams
                .Where(stream => string.Equals(stream.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase))
                .Select(stream => new LibrarySubtitleStreamMetadata(stream.CodecName, stream.Language))
                .ToArray();
            int attachments = probe.Streams.Count(stream =>
                string.Equals(stream.CodecType, "attachment", StringComparison.OrdinalIgnoreCase));
            int? reportedBitDepth = video?.BitsPerRawSample is > 0 ? video.BitsPerRawSample : null;
            int? pixelFormatBitDepth = InferBitDepth(video?.PixelFormat);
            int? bitDepth = reportedBitDepth.HasValue && pixelFormatBitDepth.HasValue &&
                            reportedBitDepth != pixelFormatBitDepth
                ? null
                : reportedBitDepth ?? pixelFormatBitDepth;
            int videoStreamCount = probe.Streams.Count(stream =>
                string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            double? averageFrameRate = PositiveFinite(video?.AverageFrameRate);
            double? nominalFrameRate = PositiveFinite(video?.NominalFrameRate);
            double? effectiveFrameRate = PositiveFinite(video?.FrameRate);
            string? frameRateBasis = averageFrameRate.HasValue && effectiveFrameRate == averageFrameRate
                ? "average"
                : nominalFrameRate.HasValue && effectiveFrameRate == nominalFrameRate
                    ? "nominal"
                    : null;
            long? totalBitRate = probe.BitRate;
            if (totalBitRate is not > 0 && probe.DurationSeconds is > 0)
            {
                totalBitRate = checked((long)Math.Round(
                    request.SizeBytes * 8d / probe.DurationSeconds.Value,
                    MidpointRounding.AwayFromZero));
            }

            return new LibraryMediaMetadata(
                request.FileId,
                metadataVersion,
                toolVersion,
                probe.Success ? LibraryProbeStatus.Succeeded : LibraryProbeStatus.Failed,
                request.AttemptCount,
                probe.Success ? null : nextRetryUtc,
                attemptedUtc,
                probe.Success ? attemptedUtc : null,
                request.SizeBytes,
                request.LastWriteUtc,
                probe.Success ? probe.FormatName : "",
                probe.Success ? probe.DurationSeconds : null,
                probe.Success ? totalBitRate : null,
                probe.Success ? video?.CodecName ?? "" : "",
                probe.Success ? video?.Profile ?? "" : "",
                probe.Success ? video?.Level : null,
                probe.Success ? video?.Width : null,
                probe.Success ? video?.Height : null,
                probe.Success ? effectiveFrameRate : null,
                probe.Success ? video?.PixelFormat ?? "" : "",
                probe.Success ? bitDepth : null,
                probe.Success ? video?.FieldOrder ?? "" : "",
                probe.Success ? video?.ColorRange ?? "" : "",
                probe.Success ? video?.ColorSpace ?? "" : "",
                probe.Success ? video?.ColorTransfer ?? "" : "",
                probe.Success ? video?.ColorPrimaries ?? "" : "",
                probe.Success ? audio : Array.Empty<LibraryAudioStreamMetadata>(),
                probe.Success ? subtitles : Array.Empty<LibrarySubtitleStreamMetadata>(),
                probe.Success ? probe.Chapters.Count : 0,
                probe.Success ? attachments : 0,
                probe.Success ? "" : probe.ErrorMessage,
                probe.Success && video?.BitRate is > 0 ? video.BitRate : null,
                probe.Success && video is { Index: >= 0 } ? video.Index : null,
                probe.Success ? videoStreamCount : null,
                probe.Success ? averageFrameRate : null,
                probe.Success ? nominalFrameRate : null,
                probe.Success ? frameRateBasis : null,
                probe.Success ? ValidAspectRatio(video?.SampleAspectRatio) : null,
                probe.Success ? ValidAspectRatio(video?.DisplayAspectRatio) : null);
        }

        private static double? PositiveFinite(double? value) =>
            value is > 0 && double.IsFinite(value.Value) ? value : null;

        private static string? ValidAspectRatio(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            string[] parts = value.Trim().Split(':');
            return parts.Length == 2 &&
                   int.TryParse(parts[0], out int numerator) && numerator > 0 &&
                   int.TryParse(parts[1], out int denominator) && denominator > 0
                ? $"{numerator}:{denominator}"
                : null;
        }

        private static int? InferBitDepth(string? pixelFormat)
        {
            if (string.IsNullOrWhiteSpace(pixelFormat))
                return null;
            string format = pixelFormat.Trim().ToLowerInvariant();
            if (format is "p010le" or "p010be") return 10;
            if (format is "p012le" or "p012be") return 12;
            if (format is "p016le" or "p016be") return 16;
            if (format is "yuv420p" or "yuv422p" or "yuv444p" or
                "yuvj420p" or "yuvj422p" or "yuvj444p" or
                "nv12" or "nv21" or "gray" or "gbrp" or
                "rgb24" or "bgr24" or "rgba" or "bgra" or "argb" or "abgr")
                return 8;
            foreach (string family in new[] { "yuv420p", "yuv422p", "yuv444p", "gbrp", "gray" })
            {
                foreach (int depth in new[] { 10, 12, 14, 16 })
                {
                    if (format == $"{family}{depth}le" || format == $"{family}{depth}be")
                        return depth;
                }
            }
            return null;
        }
    }
}
