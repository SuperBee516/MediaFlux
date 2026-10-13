using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaFlux.Services;

/// <summary>Intentional operations on the logical execution order of pending queue items.</summary>
public enum QueueExecutionOrderOperation
{
    MoveToTop,
    MoveUp,
    MoveDown,
    MoveToBottom,
    EncodeNext
}

/// <summary>Describes which requested items were eligible for an execution-order mutation.</summary>
public sealed record QueueExecutionOrderResult(
    bool Changed,
    int RequestedCount,
    int EligibleCount,
    int DispatchedCount,
    int NotPendingCount)
{
    public bool HadDispatchedItems => DispatchedCount > 0;
    public bool HadNotPendingItems => NotPendingCount > 0;
}

/// <summary>
/// Mutates an ordered queue while preserving its dispatched prefix. Callers must hold
/// the same synchronization lock used by the dispatcher for the entire operation.
/// </summary>
public static class QueueExecutionOrder
{
    public static QueueExecutionOrderResult Reorder<T>(
        IList<T> items,
        IEnumerable<T> requestedItems,
        int dispatchedCount,
        QueueExecutionOrderOperation operation)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(requestedItems);

        int boundary = Math.Clamp(dispatchedCount, 0, items.Count);
        HashSet<T> requested = new(requestedItems);
        if (requested.Count == 0)
            return new(false, 0, 0, 0, 0);

        // A row which has already appeared in the dispatched prefix is immutable,
        // even if the same row reference is also present as an intentional retry.
        HashSet<T> dispatchedItems = new(items.Take(boundary));
        HashSet<T> pendingItems = new(items.Skip(boundary));
        HashSet<T> reorderable = requested
            .Where(item => pendingItems.Contains(item) && !dispatchedItems.Contains(item))
            .ToHashSet();
        int dispatched = requested.Count(dispatchedItems.Contains);
        int unavailable = requested.Count - reorderable.Count - dispatched;

        if (reorderable.Count == 0)
            return new(false, requested.Count, 0, dispatched, unavailable);

        var tail = items.Skip(boundary).ToList();
        List<T> reorderedTail = operation switch
        {
            QueueExecutionOrderOperation.MoveToTop or QueueExecutionOrderOperation.EncodeNext =>
                tail.Where(reorderable.Contains)
                    .Concat(tail.Where(item => !reorderable.Contains(item)))
                    .ToList(),
            QueueExecutionOrderOperation.MoveToBottom =>
                tail.Where(item => !reorderable.Contains(item))
                    .Concat(tail.Where(reorderable.Contains))
                    .ToList(),
            QueueExecutionOrderOperation.MoveUp => MoveUp(tail, reorderable),
            QueueExecutionOrderOperation.MoveDown => MoveDown(tail, reorderable),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        bool changed = !tail.SequenceEqual(reorderedTail);
        if (changed)
        {
            for (int index = 0; index < reorderedTail.Count; index++)
                items[boundary + index] = reorderedTail[index];
        }

        return new(changed, requested.Count, reorderable.Count, dispatched, unavailable);
    }

    private static List<T> MoveUp<T>(IReadOnlyList<T> items, HashSet<T> selected)
    {
        var result = items.ToList();
        for (int index = 1; index < result.Count; index++)
        {
            if (selected.Contains(result[index]) && !selected.Contains(result[index - 1]))
                (result[index - 1], result[index]) = (result[index], result[index - 1]);
        }
        return result;
    }

    private static List<T> MoveDown<T>(IReadOnlyList<T> items, HashSet<T> selected)
    {
        var result = items.ToList();
        for (int index = result.Count - 2; index >= 0; index--)
        {
            if (selected.Contains(result[index]) && !selected.Contains(result[index + 1]))
                (result[index], result[index + 1]) = (result[index + 1], result[index]);
        }
        return result;
    }
}
