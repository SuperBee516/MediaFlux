using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace MediaFlux.Services.EncodingV2;

/// <summary>Stable queue-time identity for one source version.</summary>
public readonly record struct V2SourceFileIdentity(
    string FullPath,
    long SourceSizeBytes,
    long LastWriteUtcTicks,
    bool VerifyOnDisk = true)
{
    public static V2SourceFileIdentity Capture(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified source path is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("The estimate source no longer exists.", fullPath);
        return new V2SourceFileIdentity(fullPath, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    public static V2SourceFileIdentity FromFacts(V2FastEstimateSourceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (string.IsNullOrWhiteSpace(facts.SourcePath) || !Path.IsPathFullyQualified(facts.SourcePath))
            throw new ArgumentException("Supplied source facts need a fully qualified source path.", nameof(facts));
        return new V2SourceFileIdentity(Path.GetFullPath(facts.SourcePath), facts.SourceSizeBytes, 0, VerifyOnDisk: false);
    }

    public bool IsCurrentOnDisk()
    {
        if (!VerifyOnDisk)
            return true;
        try
        {
            V2SourceFileIdentity current = Capture(FullPath);
            return current.SourceSizeBytes == SourceSizeBytes && current.LastWriteUtcTicks == LastWriteUtcTicks &&
                SamePath(current.FullPath, FullPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool SamePath(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
}

public sealed record V2SourceFactsCacheResolution(
    V2FastEstimateSourceFacts Facts,
    bool CacheHit,
    double ProbeMilliseconds);

/// <summary>
/// Bounded per-path source-facts cache. A cache hit requires the same normalized path,
/// file length, and last-write timestamp. It never hashes media content.
/// </summary>
public sealed class V2SourceFactsCache : IDisposable
{
    private readonly Func<string, CancellationToken, Task<V2FastEstimateSourceFacts>> _probe;
    private readonly int _maximumEntries;
    private readonly SemaphoreSlim _probeSlot = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InFlightProbe> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _leastRecentlyUsed = new();
    private long _probeInvocationCount;
    private long _cacheHitCount;
    private bool _disposed;

    public V2SourceFactsCache(
        Func<string, CancellationToken, Task<V2FastEstimateSourceFacts>> probe,
        int maximumEntries = 256)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        if (maximumEntries is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries;
    }

    public long ProbeInvocationCount => Interlocked.Read(ref _probeInvocationCount);
    public long CacheHitCount => Interlocked.Read(ref _cacheHitCount);

    public bool TryGet(V2SourceFileIdentity identity, out V2FastEstimateSourceFacts? facts)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(identity.FullPath, out CacheEntry? entry) && entry.Identity == identity)
            {
                Touch(entry);
                facts = entry.Facts;
                return true;
            }
        }

        facts = null;
        return false;
    }

    public void Seed(V2SourceFileIdentity identity, V2FastEstimateSourceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ValidateFactsIdentity(identity, facts);
        lock (_gate)
        {
            ThrowIfDisposed();
            Store(identity, facts with { SourcePath = Path.GetFullPath(identity.FullPath) });
        }
    }

    public async Task<V2SourceFactsCacheResolution> GetAsync(
        V2SourceFileIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (identity.VerifyOnDisk && !identity.IsCurrentOnDisk())
        {
            Invalidate(identity.FullPath);
            throw new V2SourceIdentityChangedException(identity.FullPath);
        }

        InFlightProbe flight;
        bool startProbe = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(identity.FullPath, out CacheEntry? cached))
            {
                if (cached.Identity == identity)
                {
                    Touch(cached);
                    Interlocked.Increment(ref _cacheHitCount);
                    return new V2SourceFactsCacheResolution(cached.Facts, CacheHit: true, ProbeMilliseconds: 0);
                }

                RemoveEntry(cached);
            }

            if (_inFlight.TryGetValue(identity.FullPath, out flight!) && flight.Identity == identity)
            {
                // Share a single probe for concurrent requests for the same source version.
            }
            else
            {
                flight = new InFlightProbe(identity);
                _inFlight[identity.FullPath] = flight;
                startProbe = true;
            }
        }

        if (startProbe)
            _ = ProbeAndCompleteAsync(flight);

        return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }
        lock (_gate)
        {
            if (_entries.TryGetValue(fullPath, out CacheEntry? entry))
                RemoveEntry(entry);
        }
    }

    private async Task ProbeAndCompleteAsync(InFlightProbe flight)
    {
        try
        {
            await _probeSlot.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            V2FastEstimateSourceFacts facts;
            double elapsed;
            try
            {
                Interlocked.Increment(ref _probeInvocationCount);
                long started = Stopwatch.GetTimestamp();
                facts = await Task.Run(async () =>
                    await _probe(flight.Identity.FullPath, _shutdown.Token).ConfigureAwait(false),
                    _shutdown.Token).ConfigureAwait(false);
                elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            finally
            {
                _probeSlot.Release();
            }

            if (flight.Identity.VerifyOnDisk && !flight.Identity.IsCurrentOnDisk())
                throw new V2SourceIdentityChangedException(flight.Identity.FullPath);
            ValidateFactsIdentity(flight.Identity, facts);
            V2SourceFactsCacheResolution resolution = new(
                facts with { SourcePath = Path.GetFullPath(flight.Identity.FullPath) },
                CacheHit: false,
                elapsed);

            lock (_gate)
            {
                if (_inFlight.TryGetValue(flight.Identity.FullPath, out InFlightProbe? current) &&
                    ReferenceEquals(current, flight))
                {
                    _inFlight.Remove(flight.Identity.FullPath);
                    Store(flight.Identity, resolution.Facts);
                }
            }
            flight.Completion.TrySetResult(resolution);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(flight.Identity.FullPath, out InFlightProbe? current) &&
                    ReferenceEquals(current, flight))
                    _inFlight.Remove(flight.Identity.FullPath);
            }
            flight.Completion.TrySetException(ex);
        }
    }

    private void Store(V2SourceFileIdentity identity, V2FastEstimateSourceFacts facts)
    {
        if (_entries.TryGetValue(identity.FullPath, out CacheEntry? existing))
            RemoveEntry(existing);
        var node = _leastRecentlyUsed.AddFirst(identity.FullPath);
        _entries[identity.FullPath] = new CacheEntry(identity, facts, node);
        while (_entries.Count > _maximumEntries)
        {
            string oldestPath = _leastRecentlyUsed.Last!.Value;
            RemoveEntry(_entries[oldestPath]);
        }
    }

    private void Touch(CacheEntry entry)
    {
        _leastRecentlyUsed.Remove(entry.Node);
        _leastRecentlyUsed.AddFirst(entry.Node);
    }

    private void RemoveEntry(CacheEntry entry)
    {
        _entries.Remove(entry.Identity.FullPath);
        _leastRecentlyUsed.Remove(entry.Node);
    }

    private static void ValidateFactsIdentity(V2SourceFileIdentity identity, V2FastEstimateSourceFacts facts)
    {
        if (facts.SourceSizeBytes != identity.SourceSizeBytes ||
            (!string.IsNullOrWhiteSpace(facts.SourcePath) && !V2SourceFileIdentity.SamePath(facts.SourcePath, identity.FullPath)))
            throw new V2PlanningException("SourceIdentityMismatch", "The source facts do not match the queue source identity.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(V2SourceFactsCache));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _shutdown.Cancel();
    }

    private sealed class CacheEntry(V2SourceFileIdentity identity, V2FastEstimateSourceFacts facts, LinkedListNode<string> node)
    {
        public V2SourceFileIdentity Identity { get; } = identity;
        public V2FastEstimateSourceFacts Facts { get; } = facts;
        public LinkedListNode<string> Node { get; } = node;
    }

    private sealed class InFlightProbe(V2SourceFileIdentity identity)
    {
        public V2SourceFileIdentity Identity { get; } = identity;
        public TaskCompletionSource<V2SourceFactsCacheResolution> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class V2SourceIdentityChangedException(string path)
    : IOException($"Source facts were discarded because the file changed or disappeared: '{path}'.");

public sealed record V2EstimateRefreshResult(
    Guid QueueItemId,
    long Generation,
    V2SourceFileIdentity SourceIdentity,
    V2FastEstimateSourceFacts? SourceFacts,
    V2FastEstimateResult? Estimate,
    bool SourceFactsReused,
    double ProbeMilliseconds,
    double QueueWaitMilliseconds,
    double TotalMilliseconds,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool IsStale = false)
{
    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;
}

public sealed record V2EstimateRefreshTicket(
    long Generation,
    Task<V2EstimateRefreshResult> Completion);

/// <summary>
/// One owned background worker for queue estimate requests. Pending requests are coalesced
/// by queue-item id, and generation checks suppress obsolete results before they reach the UI.
/// </summary>
public sealed class V2EstimateRefreshCoordinator : IDisposable
{
    private readonly V2SourceFactsCache _sourceFacts;
    private readonly Channel<Guid> _pendingIds = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RequestWork> _pending = new();
    private readonly Dictionary<Guid, RequestWork> _running = new();
    private readonly Dictionary<Guid, long> _generations = new();
    private readonly ConcurrentQueue<V2EstimateRefreshResult> _completed = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _nextGeneration;
    private long _staleSuppressedCount;
    private long _calculationCount;
    private bool _disposed;

    public V2EstimateRefreshCoordinator(V2SourceFactsCache sourceFacts)
    {
        _sourceFacts = sourceFacts ?? throw new ArgumentNullException(nameof(sourceFacts));
        _worker = Task.Run(ProcessQueueAsync);
    }

    public long StaleSuppressedCount => Interlocked.Read(ref _staleSuppressedCount);
    public long CalculationCount => Interlocked.Read(ref _calculationCount);
    public long SourceProbeCount => _sourceFacts.ProbeInvocationCount;
    public long SourceFactsCacheHitCount => _sourceFacts.CacheHitCount;
    public int PendingWorkCount
    {
        get { lock (_gate) return _pending.Count + _running.Count; }
    }

    public bool TryGetCachedSourceFacts(V2SourceFileIdentity identity, out V2FastEstimateSourceFacts? facts) =>
        _sourceFacts.TryGet(identity, out facts);

    public void SeedSourceFacts(V2SourceFileIdentity identity, V2FastEstimateSourceFacts facts) =>
        _sourceFacts.Seed(identity, facts);

    public Task<V2SourceFactsCacheResolution> GetSourceFactsAsync(
        V2SourceFileIdentity identity,
        CancellationToken cancellationToken = default) =>
        _sourceFacts.GetAsync(identity, cancellationToken);

    public V2EstimateRefreshTicket Request(
        Guid queueItemId,
        V2SourceFileIdentity sourceIdentity,
        V2FastEstimateCommandRequest request)
    {
        if (queueItemId == Guid.Empty)
            throw new ArgumentException("A queue item id is required.", nameof(queueItemId));
        ArgumentNullException.ThrowIfNull(request);
        if (!V2SourceFileIdentity.SamePath(request.SourcePath, sourceIdentity.FullPath))
            throw new V2PlanningException("SourceIdentityMismatch", "The estimate request path does not match the queue item source.");

        if (request.SourceFacts is { } supplied)
            _sourceFacts.Seed(sourceIdentity, supplied);

        RequestWork work;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(V2EstimateRefreshCoordinator));
            long generation = checked(++_nextGeneration);
            _generations[queueItemId] = generation;
            work = new RequestWork(queueItemId, generation, sourceIdentity, request, Stopwatch.GetTimestamp());

            if (_running.TryGetValue(queueItemId, out RequestWork? running))
                CompleteAsStale(running);

            if (_pending.TryGetValue(queueItemId, out RequestWork? queued))
            {
                _pending[queueItemId] = work;
                CompleteAsStale(queued);
            }
            else
            {
                _pending[queueItemId] = work;
                if (!_pendingIds.Writer.TryWrite(queueItemId))
                {
                    _pending.Remove(queueItemId);
                    throw new InvalidOperationException("The V2 estimate worker is no longer accepting requests.");
                }
            }
        }

        return new V2EstimateRefreshTicket(work.Generation, work.Completion.Task);
    }

    public bool TryDequeueCompleted(out V2EstimateRefreshResult result) => _completed.TryDequeue(out result!);

    public void Forget(Guid queueItemId)
    {
        if (queueItemId == Guid.Empty)
            return;
        lock (_gate)
        {
            _generations.Remove(queueItemId);
            if (_pending.Remove(queueItemId, out RequestWork? pending))
                CompleteAsStale(pending);
            if (_running.TryGetValue(queueItemId, out RequestWork? running))
                CompleteAsStale(running);
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (RequestWork work in _pending.Values)
                CompleteAsStale(work);
            foreach (RequestWork work in _running.Values)
                CompleteAsStale(work);
            _pending.Clear();
            _generations.Clear();
        }
        while (_completed.TryDequeue(out _)) { }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (Guid queueItemId in _pendingIds.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                RequestWork? work;
                lock (_gate)
                {
                    if (!_pending.Remove(queueItemId, out work))
                        continue;
                    _running[queueItemId] = work;
                }

                try
                {
                    await ProcessOneAsync(work).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_running.TryGetValue(queueItemId, out RequestWork? current) && ReferenceEquals(current, work))
                            _running.Remove(queueItemId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessOneAsync(RequestWork work)
    {
        try
        {
            if (!IsCurrent(work))
            {
                CompleteAsStale(work);
                return;
            }

            double queueWait = Stopwatch.GetElapsedTime(work.SubmittedTimestamp).TotalMilliseconds;
            V2SourceFactsCacheResolution source;
            if (work.Request.SourceFacts is { } supplied)
            {
                if (work.SourceIdentity.VerifyOnDisk && !work.SourceIdentity.IsCurrentOnDisk())
                    throw new V2SourceIdentityChangedException(work.SourceIdentity.FullPath);
                source = new V2SourceFactsCacheResolution(supplied, CacheHit: true, ProbeMilliseconds: 0);
            }
            else
            {
                source = await _sourceFacts.GetAsync(work.SourceIdentity, _shutdown.Token).ConfigureAwait(false);
            }

            if (!IsCurrent(work))
            {
                CompleteAsStale(work);
                return;
            }

            Interlocked.Increment(ref _calculationCount);
            V2FastEstimateResult estimate = V2FastEstimateService.Estimate(
                V2FastEstimateService.CreateRequest(work.Request, source.Facts));

            if (!IsCurrent(work))
            {
                CompleteAsStale(work);
                return;
            }

            V2EstimateRefreshResult completed = new(
                work.QueueItemId,
                work.Generation,
                work.SourceIdentity,
                source.Facts,
                estimate,
                source.CacheHit,
                source.ProbeMilliseconds,
                queueWait,
                Stopwatch.GetElapsedTime(work.SubmittedTimestamp).TotalMilliseconds);
            _completed.Enqueue(completed);
            work.Completion.TrySetResult(completed);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            CompleteAsStale(work);
        }
        catch (Exception ex)
        {
            if (!IsCurrent(work))
            {
                CompleteAsStale(work);
                return;
            }

            string code = ex is V2PlanningException planning ? planning.Code :
                ex is V2SourceIdentityChangedException ? "SourceChanged" : "EstimateRefreshFailed";
            V2EstimateRefreshResult failed = new(
                work.QueueItemId,
                work.Generation,
                work.SourceIdentity,
                null,
                null,
                SourceFactsReused: false,
                ProbeMilliseconds: 0,
                QueueWaitMilliseconds: Stopwatch.GetElapsedTime(work.SubmittedTimestamp).TotalMilliseconds,
                TotalMilliseconds: Stopwatch.GetElapsedTime(work.SubmittedTimestamp).TotalMilliseconds,
                ErrorCode: code,
                ErrorMessage: ex.Message);
            _completed.Enqueue(failed);
            work.Completion.TrySetResult(failed);
        }
    }

    private bool IsCurrent(RequestWork work)
    {
        lock (_gate)
            return _generations.TryGetValue(work.QueueItemId, out long generation) && generation == work.Generation;
    }

    private void CompleteAsStale(RequestWork work)
    {
        var stale = new V2EstimateRefreshResult(
            work.QueueItemId,
            work.Generation,
            work.SourceIdentity,
            null,
            null,
            SourceFactsReused: false,
            ProbeMilliseconds: 0,
            QueueWaitMilliseconds: 0,
            TotalMilliseconds: 0,
            IsStale: true);
        if (work.Completion.TrySetResult(stale))
            Interlocked.Increment(ref _staleSuppressedCount);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        CancelAll();
        _pendingIds.Writer.TryComplete();
        _shutdown.Cancel();
        _sourceFacts.Dispose();
    }

    private sealed class RequestWork(
        Guid queueItemId,
        long generation,
        V2SourceFileIdentity sourceIdentity,
        V2FastEstimateCommandRequest request,
        long submittedTimestamp)
    {
        public Guid QueueItemId { get; } = queueItemId;
        public long Generation { get; } = generation;
        public V2SourceFileIdentity SourceIdentity { get; } = sourceIdentity;
        public V2FastEstimateCommandRequest Request { get; } = request;
        public long SubmittedTimestamp { get; } = submittedTimestamp;
        public TaskCompletionSource<V2EstimateRefreshResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
