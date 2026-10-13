namespace MediaFlux.Services;

public enum QueueEstimateRefinementPriority { Selected = 0, Visible = 1, NearTerm = 2, Background = 3 }

/// <summary>Small, single-worker priority queue for speculative measured estimates.</summary>
internal sealed class QueueEstimateRefinementScheduler<T> : IDisposable
{
    private sealed record Entry(
        Guid QueueItemId,
        int Generation,
        T Work,
        QueueEstimateRefinementPriority Priority,
        long Order,
        CancellationToken RequestToken);

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _pending = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<T, CancellationToken, Task> _execute;
    private readonly int _capacity;
    private readonly Task _worker;
    private Entry? _running;
    private CancellationTokenSource? _runningCancellation;
    private long _nextOrder;
    private long _cancelledOrSuperseded;
    private long _rejected;
    private bool _disposed;
    private TaskCompletionSource _idle = CompletedSignal();

    internal QueueEstimateRefinementScheduler(Func<T, CancellationToken, Task> execute, int capacity = 32)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        if (capacity is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _worker = Task.Run(WorkerLoopAsync);
    }

    internal int PendingCount
    {
        get { lock (_gate) return _pending.Count + (_running is null ? 0 : 1); }
    }

    internal long CancelledOrSupersededCount => Interlocked.Read(ref _cancelledOrSuperseded);
    internal long RejectedCount => Interlocked.Read(ref _rejected);

    internal Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return _idle.Task.WaitAsync(cancellationToken);
    }

    internal bool Request(
        Guid queueItemId,
        int generation,
        T work,
        QueueEstimateRefinementPriority priority,
        CancellationToken requestToken)
    {
        if (queueItemId == Guid.Empty)
            throw new ArgumentException("A queue item id is required.", nameof(queueItemId));

        lock (_gate)
        {
            if (_disposed || requestToken.IsCancellationRequested)
                return false;

            LinkedListNode<Entry>? existing = _pending.First;
            while (existing is not null)
            {
                LinkedListNode<Entry>? next = existing.Next;
                if (existing.Value.QueueItemId == queueItemId)
                {
                    _pending.Remove(existing);
                    Interlocked.Increment(ref _cancelledOrSuperseded);
                }
                existing = next;
            }

            if (_running?.QueueItemId == queueItemId)
            {
                _runningCancellation?.Cancel();
                Interlocked.Increment(ref _cancelledOrSuperseded);
            }

            if (_pending.Count >= _capacity)
            {
                LinkedListNode<Entry>? leastValuable = _pending.Last;
                if (leastValuable is null || (int)priority >= (int)leastValuable.Value.Priority)
                {
                    Interlocked.Increment(ref _rejected);
                    return false;
                }
                _pending.RemoveLast();
                Interlocked.Increment(ref _cancelledOrSuperseded);
            }

            var entry = new Entry(queueItemId, generation, work, priority, checked(++_nextOrder), requestToken);
            if (_pending.Count == 0 && _running is null)
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            InsertByPriority(entry);
            _available.Release();
            return true;
        }
    }

    internal bool SetPriority(Guid queueItemId, QueueEstimateRefinementPriority priority)
    {
        lock (_gate)
        {
            LinkedListNode<Entry>? node = _pending.First;
            while (node is not null && node.Value.QueueItemId != queueItemId)
                node = node.Next;
            if (node is null || priority == node.Value.Priority)
                return false;

            Entry promoted = node.Value with { Priority = priority, Order = checked(++_nextOrder) };
            _pending.Remove(node);
            InsertByPriority(promoted);
            return true;
        }
    }

    internal void CancelAll()
    {
        lock (_gate)
        {
            Interlocked.Add(ref _cancelledOrSuperseded, _pending.Count);
            _pending.Clear();
            _runningCancellation?.Cancel();
            CompleteIfIdleNoLock();
        }
    }

    internal void Cancel(Guid queueItemId)
    {
        lock (_gate)
        {
            LinkedListNode<Entry>? node = _pending.First;
            while (node is not null)
            {
                LinkedListNode<Entry>? next = node.Next;
                if (node.Value.QueueItemId == queueItemId)
                {
                    _pending.Remove(node);
                    Interlocked.Increment(ref _cancelledOrSuperseded);
                }
                node = next;
            }
            if (_running?.QueueItemId == queueItemId)
            {
                _runningCancellation?.Cancel();
                Interlocked.Increment(ref _cancelledOrSuperseded);
            }
            CompleteIfIdleNoLock();
        }
    }

    private void InsertByPriority(Entry entry)
    {
        LinkedListNode<Entry>? node = _pending.First;
        while (node is not null &&
               ((int)node.Value.Priority < (int)entry.Priority ||
                node.Value.Priority == entry.Priority && node.Value.Order <= entry.Order))
            node = node.Next;
        if (node is null)
            _pending.AddLast(entry);
        else
            _pending.AddBefore(node, entry);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Entry? entry;
                CancellationTokenSource? runCancellation = null;
                lock (_gate)
                {
                    if (_pending.First is not { } first)
                        continue;
                    entry = first.Value;
                    _pending.RemoveFirst();
                    if (entry.RequestToken.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _cancelledOrSuperseded);
                        entry = null;
                    }
                    else
                    {
                        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            entry.RequestToken, _shutdown.Token);
                        _running = entry;
                        _runningCancellation = runCancellation;
                    }
                    CompleteIfIdleNoLock();
                }

                if (entry is not null && runCancellation is not null)
                {
                    try
                    {
                        await _execute(entry.Work, runCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
                    {
                    }
                    catch
                    {
                        // A failed refinement is advisory; it cannot stop later queue items.
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_running, entry))
                            {
                                _running = null;
                                _runningCancellation = null;
                            }
                            CompleteIfIdleNoLock();
                        }
                        runCancellation.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Add(ref _cancelledOrSuperseded, _pending.Count);
            _pending.Clear();
            _runningCancellation?.Cancel();
            _shutdown.Cancel();
            CompleteIfIdleNoLock();
        }
    }

    private void CompleteIfIdleNoLock()
    {
        if (_pending.Count == 0 && _running is null)
            _idle.TrySetResult();
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
