using System.Collections.Concurrent;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class QueueEstimateRefinementSchedulerTests
{
    [Fact]
    public async Task TwentyAcceptedItemsDrainThroughOneBoundedWorker()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new ConcurrentQueue<string>();
        int active = 0;
        int maximumActive = 0;
        using var scheduler = new QueueEstimateRefinementScheduler<string>(async (work, token) =>
        {
            int current = Interlocked.Increment(ref active);
            int observed;
            while (current > (observed = Volatile.Read(ref maximumActive)) &&
                   Interlocked.CompareExchange(ref maximumActive, current, observed) != observed) { }
            if (work == "item-0")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            completed.Enqueue(work);
            Interlocked.Decrement(ref active);
        }, capacity: 32);

        Assert.True(scheduler.Request(Guid.NewGuid(), 1, "item-0", QueueEstimateRefinementPriority.Background, default));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 1; i < 20; i++)
        {
            QueueEstimateRefinementPriority priority = i switch
            {
                1 => QueueEstimateRefinementPriority.Selected,
                <= 3 => QueueEstimateRefinementPriority.Visible,
                <= 5 => QueueEstimateRefinementPriority.NearTerm,
                _ => QueueEstimateRefinementPriority.Background
            };
            Assert.True(scheduler.Request(Guid.NewGuid(), 1, $"item-{i}", priority, default));
        }

        Assert.Equal(20, scheduler.PendingCount);
        Assert.Empty(completed);
        releaseFirst.TrySetResult();
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(20, completed.Count);
        Assert.Equal(1, maximumActive);
        Assert.Equal(0, scheduler.RejectedCount);
    }

    [Fact]
    public async Task OneWorkerRunsSelectedAndVisibleBeforeBackgroundAndRejectsOverflow()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        int active = 0;
        int maximumActive = 0;
        using var scheduler = new QueueEstimateRefinementScheduler<string>(async (work, token) =>
        {
            int current = Interlocked.Increment(ref active);
            int observed;
            while (current > (observed = Volatile.Read(ref maximumActive)) &&
                   Interlocked.CompareExchange(ref maximumActive, current, observed) != observed) { }
            order.Enqueue(work);
            if (work == "running")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            Interlocked.Decrement(ref active);
        }, capacity: 3);

        Assert.True(scheduler.Request(Guid.NewGuid(), 1, "running", QueueEstimateRefinementPriority.Background, default));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduler.Request(Guid.NewGuid(), 1, "background-a", QueueEstimateRefinementPriority.Background, default));
        Assert.True(scheduler.Request(Guid.NewGuid(), 1, "background-b", QueueEstimateRefinementPriority.Background, default));
        Assert.True(scheduler.Request(Guid.NewGuid(), 1, "background-c", QueueEstimateRefinementPriority.Background, default));
        Assert.False(scheduler.Request(Guid.NewGuid(), 1, "background-overflow", QueueEstimateRefinementPriority.Background, default));
        Assert.True(scheduler.Request(Guid.NewGuid(), 1, "visible", QueueEstimateRefinementPriority.Visible, default));
        Assert.True(scheduler.Request(Guid.NewGuid(), 1, "selected", QueueEstimateRefinementPriority.Selected, default));

        Assert.Equal(3, scheduler.PendingCount - 1); // The currently running item is counted separately.
        Assert.Equal(1, scheduler.RejectedCount);
        releaseFirst.TrySetResult();
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "running", "selected", "visible", "background-a" }, order.ToArray());
        Assert.Equal(1, maximumActive);
    }
}
