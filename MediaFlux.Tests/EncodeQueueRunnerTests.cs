using MediaFlux.Services;
using System.Collections.Concurrent;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeQueueRunnerTests
{
    [Fact]
    public async Task AppendedItemsAreDispatchedAfterTheInitialOrderedList()
    {
        var items = new List<int> { 1, 2 };
        var dispatched = new List<int>();
        var gate = new object();
        bool appended = false;

        await new EncodeQueueRunner().RunAsync(
            items,
            async item =>
            {
                lock (gate)
                    dispatched.Add(item);
                if (item == 1 && !appended)
                {
                    appended = true;
                    lock (gate)
                    {
                        items.Add(3);
                        items.Add(4);
                    }
                }
                await Task.CompletedTask;
            },
            maxParallel: 1,
            isPaused: () => false,
            isCancelled: () => false,
            syncRoot: gate);

        Assert.Equal(new[] { 1, 2, 3, 4 }, dispatched);
    }

    [Fact]
    public async Task ActiveEncodeNextAndAppendAffectOnlyUndispatchedTail()
    {
        var items = new List<int> { 1, 2, 3 };
        var sync = new object();
        var dispatched = new List<int>();
        var completed = new ConcurrentBag<int>();
        var firstClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int claimedCount = 0;
        QueueExecutionOrderResult? claimedItemReorder = null;

        Task run = new EncodeQueueRunner().RunAsync(
            items,
            async item =>
            {
                lock (sync)
                    dispatched.Add(item);
                if (item == 1)
                    await releaseFirst.Task;
                completed.Add(item);
            },
            maxParallel: 1,
            isPaused: () => false,
            isCancelled: () => false,
            syncRoot: sync,
            dispatchedCountUpdated: count =>
            {
                claimedCount = count;
                if (count == 1)
                {
                    // This callback runs after the dispatch index advances but
                    // before RunAsync schedules the worker task.
                    claimedItemReorder = QueueExecutionOrder.Reorder(
                        items,
                        new[] { 1 },
                        count,
                        QueueExecutionOrderOperation.EncodeNext);
                    firstClaimed.TrySetResult();
                }
            });

        await firstClaimed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(claimedItemReorder);
        Assert.False(claimedItemReorder.Changed);
        Assert.Equal(1, claimedItemReorder.DispatchedCount);
        QueueExecutionOrderResult reorder;
        lock (sync)
        {
            reorder = QueueExecutionOrder.Reorder(
                items,
                new[] { 3 },
                claimedCount,
                QueueExecutionOrderOperation.EncodeNext);
            items.Add(4);
        }

        Assert.True(reorder.Changed);
        Assert.Equal(new[] { 1, 3, 2, 4 }, items);
        Assert.Equal(1, claimedCount);

        releaseFirst.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { 1, 3, 2, 4 }, dispatched);
        Assert.Equal(new[] { 1, 2, 3, 4 }, completed.OrderBy(item => item));
        Assert.Equal(4, completed.Distinct().Count());
    }

    [Fact]
    public async Task ParallelDispatchedWorkersStayInTheImmutablePrefixDuringReorder()
    {
        var items = new List<int> { 1, 2, 3, 4, 5 };
        var sync = new object();
        var running = new ConcurrentDictionary<int, byte>();
        var completed = new ConcurrentBag<int>();
        var twoWorkersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorkers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int claimedCount = 0;

        Task run = new EncodeQueueRunner().RunAsync(
            items,
            async item =>
            {
                running[item] = 0;
                if (running.Count == 2)
                    twoWorkersStarted.TrySetResult();
                await releaseWorkers.Task;
                completed.Add(item);
                running.TryRemove(item, out _);
            },
            maxParallel: 2,
            isPaused: () => false,
            isCancelled: () => false,
            syncRoot: sync,
            dispatchedCountUpdated: count => claimedCount = count);

        await twoWorkersStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        QueueExecutionOrderResult reorder;
        lock (sync)
        {
            Assert.Equal(2, claimedCount);
            Assert.Equal(new[] { 1, 2 }, items.Take(claimedCount));
            reorder = QueueExecutionOrder.Reorder(
                items,
                new[] { 5 },
                claimedCount,
                QueueExecutionOrderOperation.EncodeNext);
            Assert.Equal(new[] { 1, 2 }, items.Take(claimedCount));
        }

        Assert.True(reorder.Changed);
        Assert.Equal(new[] { 1, 2, 5, 3, 4 }, items);
        Assert.Equal(new[] { 1, 2 }, running.Keys.OrderBy(item => item));

        releaseWorkers.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(running);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, completed.OrderBy(item => item));
        Assert.Equal(5, completed.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentAppendAndReorderPreserveEveryPendingItemOnce()
    {
        var items = Enumerable.Range(0, 32).ToList();
        var sync = new object();
        var start = new Barrier(2);

        Task append = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int item = 32; item < 256; item++)
            {
                lock (sync)
                    items.Add(item);
            }
        });
        Task reorder = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int iteration = 0; iteration < 224; iteration++)
            {
                lock (sync)
                {
                    int selected = items[^1];
                    QueueExecutionOrder.Reorder(
                        items,
                        new[] { selected },
                        dispatchedCount: 0,
                        operation: QueueExecutionOrderOperation.MoveToTop);
                }
            }
        });

        await Task.WhenAll(append, reorder).WaitAsync(TimeSpan.FromSeconds(10));

        lock (sync)
        {
            Assert.Equal(256, items.Count);
            Assert.Equal(Enumerable.Range(0, 256).OrderBy(item => item), items.OrderBy(item => item));
            Assert.Equal(256, items.Distinct().Count());
        }
    }

    [Fact]
    public async Task AppendAtDrainBoundaryIsEitherDispatchedOrRejectedAfterAtomicSeal()
    {
        var items = new List<int> { 1 };
        var sync = new object();
        var completed = new ConcurrentBag<int>();
        using var appendWindow = new ManualResetEventSlim();
        using var continueRunner = new ManualResetEventSlim();
        int completionAttempts = 0;
        int pendingImport = 1;
        bool accepting = true;

        Task run = new EncodeQueueRunner().RunAsync(
            items,
            item =>
            {
                completed.Add(item);
                return Task.CompletedTask;
            },
            maxParallel: 1,
            isPaused: () => false,
            isCancelled: () => false,
            syncRoot: sync,
            hasPendingItems: () =>
            {
                if (Volatile.Read(ref completionAttempts) == 1)
                {
                    appendWindow.Set();
                    if (!continueRunner.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Drain-boundary append was not released.");
                }
                return Volatile.Read(ref pendingImport) > 0;
            },
            tryCompleteWhenDrained: () =>
            {
                Interlocked.Increment(ref completionAttempts);
                if (Volatile.Read(ref pendingImport) > 0)
                    return false;
                accepting = false;
                return true;
            });

        Assert.True(appendWindow.Wait(TimeSpan.FromSeconds(10)));
        lock (sync)
        {
            Assert.True(accepting);
            items.Add(2);
            Volatile.Write(ref pendingImport, 0);
        }
        continueRunner.Set();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { 1, 2 }, completed.OrderBy(item => item));
        Assert.Equal(2, completed.Distinct().Count());
        Assert.False(accepting);
        Assert.Equal(0, pendingImport);
    }

    [Fact]
    public async Task AppendAfterCompletionSealRemainsOwnedByTheNextRun()
    {
        var items = new List<int> { 1 };
        var nextRunItems = new List<int>();
        var sync = new object();
        var completed = new ConcurrentBag<int>();
        var sealedForAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool accepting = true;

        Task run = new EncodeQueueRunner().RunAsync(
            items,
            item =>
            {
                completed.Add(item);
                return Task.CompletedTask;
            },
            maxParallel: 1,
            isPaused: () => false,
            isCancelled: () => false,
            syncRoot: sync,
            tryCompleteWhenDrained: () =>
            {
                accepting = false;
                sealedForAppend.TrySetResult();
                return true;
            });

        Task append = Task.Run(async () =>
        {
            await sealedForAppend.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (sync)
            {
                if (accepting)
                    items.Add(2);
                else
                    nextRunItems.Add(2);
            }
        });

        await Task.WhenAll(run, append).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { 1 }, completed);
        Assert.Equal(new[] { 2 }, nextRunItems);
        Assert.DoesNotContain(2, items);
    }

    [Fact]
    public async Task LegacyPendingImportKeepsRunnerAliveUntilItAppends()
    {
        var items = new List<int> { 1 };
        var sync = new object();
        var completed = new ConcurrentBag<int>();
        var pendingObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pendingImport = 1;

        Task run = new EncodeQueueRunner().RunAsync(
            items,
            item =>
            {
                completed.Add(item);
                return Task.CompletedTask;
            },
            maxParallel: 1,
            isPaused: () => false,
            isCancelled: () => false,
            syncRoot: sync,
            hasPendingItems: () =>
            {
                if (Volatile.Read(ref pendingImport) > 0)
                    pendingObserved.TrySetResult();
                return Volatile.Read(ref pendingImport) > 0;
            });

        await pendingObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (sync)
        {
            items.Add(2);
            Volatile.Write(ref pendingImport, 0);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { 1, 2 }, completed.OrderBy(item => item));
        Assert.Equal(2, completed.Distinct().Count());
    }
}
