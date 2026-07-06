using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Encode.Services
{
    /// <summary>
    /// Runs up to maxParallel workers over an append-only list.
    /// Treats items as append-only: anything added while running
    /// (e.g. from the context menu) is also processed.
    /// Callers are responsible for ensuring that any concurrent modifications
    /// to the <paramref name="items"/> collection are done in a thread-safe manner.
    /// </summary>
    /// <remarks>
    /// The runner never removes from <paramref name="items"/> and only advances a dispatch index,
    /// so append-only usage is safe as long as the underlying collection is not mutated in an
    /// unsafe way from multiple threads (for example, using a plain List&lt;T&gt; without external locking).
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
            Func<bool>? hasPendingItems = null)
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

                // Fill slots up to maxParallel using the *current* items.Count
                while (running.Count < maxParallel &&
                       jobIndex < GetCount(items, syncRoot) &&
                       !isCancelled() &&
                       !cancellationToken.IsCancellationRequested)
                {
                    var item = GetItemAndAdvance(items, syncRoot, ref jobIndex);

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
                    // And there is nothing undispatched → we are done
                    if (jobIndex >= GetCount(items, syncRoot))
                    {
                        if (hasPendingItems?.Invoke() == true)
                        {
                            try
                            {
                                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            continue;
                        }

                        break;
                    }

                    // Otherwise, new items have been appended; loop again to schedule them
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

        private int GetCount<T>(IList<T> items, object? syncRoot)
        {
            if (syncRoot == null)
                return items.Count;

            lock (syncRoot)
            {
                return items.Count;
            }
        }

        private T GetItemAndAdvance<T>(IList<T> items, object? syncRoot, ref int jobIndex)
        {
            if (syncRoot == null)
            {
                var item = items[jobIndex];
                jobIndex++;
                return item;
            }

            lock (syncRoot)
            {
                var item = items[jobIndex];
                jobIndex++;
                return item;
            }
        }
    }
}
