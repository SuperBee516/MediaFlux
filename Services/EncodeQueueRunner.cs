using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaFlux.Services
{
    /// <summary>
    /// Runs up to maxParallel workers over an ordered queue. Items may be appended,
    /// and the undispatched tail may be reordered, while the runner is active.
    /// Callers are responsible for ensuring that any concurrent modifications
    /// to the <paramref name="items"/> collection are done in a thread-safe manner.
    /// </summary>
    /// <remarks>
    /// The runner advances a dispatch index and never removes items itself. When a sync root
    /// is supplied, append/reorder operations must use that same lock. The dispatched-count
    /// callback runs under the lock immediately after the index advances, defining the
    /// immutable prefix boundary for concurrent reorder operations. The completion
    /// callback also runs under that lock after the runner observes an empty tail, so a
    /// caller can atomically close live append admission.
    /// </remarks>
    public sealed class EncodeQueueRunner
    {
        public async Task RunAsync<T>(
            IList<T> items,
            Func<T, Task> worker,
            int maxParallel,
            Func<bool> isPaused,
            Func<bool> isCancelled,
            CancellationToken cancellationToken = default,
            object? syncRoot = null,
            Func<bool>? hasPendingItems = null,
            Action<int>? dispatchedCountUpdated = null,
            Func<bool>? tryCompleteWhenDrained = null)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (worker == null) throw new ArgumentNullException(nameof(worker));
            if (isPaused == null) throw new ArgumentNullException(nameof(isPaused));
            if (isCancelled == null) throw new ArgumentNullException(nameof(isCancelled));
            if (maxParallel <= 0) throw new ArgumentOutOfRangeException(nameof(maxParallel));

            // Track currently running worker tasks. Pre-sized to maxParallel for minor efficiency.
            var running = new List<Task>(maxParallel);
            // Index of the next item to dispatch into a worker.
            int jobIndex = 0;

            while (true)
            {
                // Main loop: check for cancel, honor pause, schedule work, then wait for completion.

                // Hard cancel?
                if (isCancelled() || cancellationToken.IsCancellationRequested)
                    break;

                // Pause handling
                try
                {
                    while (isPaused() &&
                           !isCancelled() &&
                           !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancellation requested while paused; exit the loop like any other cancel.
                    break;
                }

                // Claim each next item atomically with the dispatch boundary. A
                // pending-tail removal/reorder cannot invalidate a prior Count check.
                while (running.Count < maxParallel &&
                       !isCancelled() &&
                       !cancellationToken.IsCancellationRequested)
                {
                    if (!TryGetItemAndAdvance(
                        items,
                        syncRoot,
                        ref jobIndex,
                        dispatchedCountUpdated,
                        out T item))
                    {
                        break;
                    }

                    // Start worker WITHOUT awaiting it → this is where we get parallelism.
                    // Guard against synchronous exceptions so they are treated like faulted tasks.
                    Task task;
                    try
                    {
                        task = Task.Run(() => worker(item), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        task = Task.FromException(ex);
                    }

                    running.Add(task);
                }

                // Nothing running?
                if (running.Count == 0)
                {
                    // Completion and live append admission share the queue lock.
                    // The callback may close admission once imports and pending
                    // queue entries are both drained.
                    if (TryCompleteWhenDrained(
                            items,
                            syncRoot,
                            jobIndex,
                            hasPendingItems,
                            tryCompleteWhenDrained))
                    {
                        break;
                    }

                    if (HasUndispatchedItems(items, syncRoot, jobIndex))
                        continue;

                    // An external import may already be admitted but still
                    // discovering files. Keep the runner alive until it appends
                    // or releases that admission.
                    if (hasPendingItems?.Invoke() == true || tryCompleteWhenDrained != null)
                    {
                        try
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                    continue;
                }

                // Wait for at least one job to finish
                Task finished;
                try
                {
                    finished = await Task.WhenAny(running).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                running.Remove(finished);

                // Observe exceptions so they don't stay unobserved.
                // The worker itself (EncodeSingleRow) handles UI/logging.
                if (finished.IsFaulted)
                {
                    var _ = finished.Exception;
                }
            }

            // Wait for active workers to finish their own cleanup paths. Cancellation stops
            // new dispatches, but launched FFmpeg processes still need a chance to quit safely.
            if (running.Count > 0)
            {
                try
                {
                    await Task.WhenAll(running).ConfigureAwait(false);
                }
                catch
                {
                    // per-job failures already handled in worker
                }
            }
        }

        private bool HasUndispatchedItems<T>(IList<T> items, object? syncRoot, int jobIndex)
        {
            if (syncRoot == null)
                return jobIndex < items.Count;

            lock (syncRoot)
            {
                return jobIndex < items.Count;
            }
        }

        private bool TryGetItemAndAdvance<T>(
            IList<T> items,
            object? syncRoot,
            ref int jobIndex,
            Action<int>? dispatchedCountUpdated,
            out T item)
        {
            if (syncRoot == null)
            {
                if (jobIndex >= items.Count)
                {
                    item = default!;
                    return false;
                }

                item = items[jobIndex];
                jobIndex++;
                dispatchedCountUpdated?.Invoke(jobIndex);
                return true;
            }

            lock (syncRoot)
            {
                if (jobIndex >= items.Count)
                {
                    item = default!;
                    return false;
                }

                item = items[jobIndex];
                jobIndex++;
                // The item becomes immutable at this exact point. The callback runs
                // under the same lock used by tail reorder and append operations.
                dispatchedCountUpdated?.Invoke(jobIndex);
                return true;
            }
        }

        private bool TryCompleteWhenDrained<T>(
            IList<T> items,
            object? syncRoot,
            int jobIndex,
            Func<bool>? hasPendingItems,
            Func<bool>? tryCompleteWhenDrained)
        {
            if (syncRoot == null)
            {
                if (jobIndex < items.Count)
                    return false;
                if (tryCompleteWhenDrained != null)
                    return tryCompleteWhenDrained();
            }
            else
            {
                lock (syncRoot)
                {
                    if (jobIndex < items.Count)
                        return false;
                    if (tryCompleteWhenDrained != null)
                        return tryCompleteWhenDrained();
                }
            }

            // Preserve the legacy callback boundary: without an atomic completion
            // callback, pending-state observation remains outside the queue lock.
            return hasPendingItems?.Invoke() != true;
        }
    }
}
