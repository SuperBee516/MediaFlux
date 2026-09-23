using MediaFlux.Services;
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
}
