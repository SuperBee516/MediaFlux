using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class QueueExecutionOrderTests
{
    [Fact]
    public void SingleItemOperationsMutateTheIdleLogicalOrder()
    {
        (QueueExecutionOrderOperation Operation, int Selected, int[] Expected)[] cases =
        [
            (QueueExecutionOrderOperation.MoveUp, 3, [1, 3, 2, 4]),
            (QueueExecutionOrderOperation.MoveDown, 1, [2, 1, 3, 4]),
            (QueueExecutionOrderOperation.MoveToTop, 3, [3, 1, 2, 4]),
            (QueueExecutionOrderOperation.MoveToBottom, 1, [2, 3, 4, 1]),
            (QueueExecutionOrderOperation.EncodeNext, 3, [3, 1, 2, 4])
        ];

        foreach (var testCase in cases)
        {
            var items = new[] { 1, 2, 3, 4 }.ToList();
            QueueExecutionOrderResult result = QueueExecutionOrder.Reorder(
                items,
                new[] { testCase.Selected },
                dispatchedCount: 0,
                operation: testCase.Operation);

            Assert.True(result.Changed);
            Assert.Equal(testCase.Expected, items);
        }
    }

    [Fact]
    public void BlockMovesPreserveSelectedRelativeOrder()
    {
        var items = new[] { 1, 2, 3, 4, 5 }.ToList();

        QueueExecutionOrderResult result = QueueExecutionOrder.Reorder(
            items,
            new[] { 2, 4 },
            dispatchedCount: 0,
            operation: QueueExecutionOrderOperation.MoveToTop);

        Assert.True(result.Changed);
        Assert.Equal(new[] { 2, 4, 1, 3, 5 }, items);
        Assert.Equal(2, result.EligibleCount);
    }

    [Fact]
    public void MultiSelectionMovesUpAndDownAsAnOrderPreservingBlock()
    {
        (QueueExecutionOrderOperation Operation, int[] Expected)[] cases =
        [
            (QueueExecutionOrderOperation.MoveUp, [2, 3, 1, 4, 5]),
            (QueueExecutionOrderOperation.MoveDown, [1, 4, 2, 3, 5])
        ];

        foreach (var testCase in cases)
        {
            var items = new[] { 1, 2, 3, 4, 5 }.ToList();
            QueueExecutionOrderResult result = QueueExecutionOrder.Reorder(
                items,
                new[] { 2, 3 },
                dispatchedCount: 0,
                operation: testCase.Operation);

            Assert.True(result.Changed);
            Assert.Equal(2, result.EligibleCount);
            Assert.Equal(testCase.Expected, items);
        }
    }

    [Fact]
    public void DispatchedPrefixIsImmutableAndResultReportsSkippedSelections()
    {
        var items = new[] { 1, 2, 3, 4 }.ToList();

        QueueExecutionOrderResult result = QueueExecutionOrder.Reorder(
            items,
            new[] { 1, 4, 99 },
            dispatchedCount: 2,
            operation: QueueExecutionOrderOperation.EncodeNext);

        Assert.Equal(new[] { 1, 2, 4, 3 }, items);
        Assert.True(result.Changed);
        Assert.Equal(1, result.DispatchedCount);
        Assert.Equal(1, result.NotPendingCount);
        Assert.Equal(1, result.EligibleCount);
        Assert.True(result.HadDispatchedItems);
        Assert.True(result.HadNotPendingItems);
    }

    [Fact]
    public void ResultDistinguishesAlreadyPositionedEligibleItemsFromUnavailableItems()
    {
        var items = new[] { 1, 2, 3 }.ToList();

        QueueExecutionOrderResult result = QueueExecutionOrder.Reorder(
            items,
            new[] { 1 },
            dispatchedCount: 0,
            operation: QueueExecutionOrderOperation.MoveToTop);

        Assert.False(result.Changed);
        Assert.Equal(1, result.RequestedCount);
        Assert.Equal(1, result.EligibleCount);
        Assert.Equal(0, result.DispatchedCount);
        Assert.Equal(0, result.NotPendingCount);
        Assert.Equal(new[] { 1, 2, 3 }, items);
    }

    [Fact]
    public void SelectingOnlyDispatchedOrUnavailableItemsIsASafeNoOp()
    {
        var items = new[] { 1, 2, 3 }.ToList();

        QueueExecutionOrderResult result = QueueExecutionOrder.Reorder(
            items,
            new[] { 1, 99 },
            dispatchedCount: 1,
            operation: QueueExecutionOrderOperation.MoveToBottom);

        Assert.False(result.Changed);
        Assert.Equal(1, result.DispatchedCount);
        Assert.Equal(1, result.NotPendingCount);
        Assert.Equal(new[] { 1, 2, 3 }, items);
    }
}
