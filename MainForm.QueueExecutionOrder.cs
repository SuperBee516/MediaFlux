using System;
using System.Collections.Generic;
using System.Linq;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm
    {
        private enum ActiveQueueAppendOutcome
        {
            NotAdmitted,
            Added,
            AlreadyPresent
        }

        /// <summary>
        /// Applies an explicit logical-order operation. While encoding, the active queue
        /// lock is shared with dispatch, so the runner's dispatched prefix is immutable
        /// and only its undispatched tail can move.
        /// </summary>
        private QueueExecutionOrderResult ReorderQueueRows(
            IEnumerable<DataGridViewRow> requestedRows,
            QueueExecutionOrderOperation operation)
        {
            ArgumentNullException.ThrowIfNull(requestedRows);
            List<DataGridViewRow> requested = requestedRows
                .Where(row => row != null && !row.IsNewRow && row.DataGridView == dgvEncodeQueue)
                .Distinct()
                .ToList();
            List<DataGridViewRow> logicalRows = GetEncodeRowsInExecutionOrder().ToList();

            // Materialize any missing metadata before taking the dispatch lock. The
            // critical section below must not touch DataGridView state.
            foreach (DataGridViewRow row in logicalRows)
                EnsureRowMeta(row);

            lock (_activeEncodeQueueLock)
            {
                if (_encodingActive && _activeEncodeQueue != null)
                {
                    QueueExecutionOrderResult result = QueueExecutionOrder.Reorder(
                        _activeEncodeQueue,
                        requested,
                        _activeEncodeQueueDispatchedCount,
                        operation);
                    if (result.Changed)
                    {
                        NormalizeQueueSequences(MergeActiveQueueOrderIntoLogicalRows(
                            logicalRows,
                            _activeEncodeQueue,
                            _activeEncodeQueueDispatchedCount));
                    }
                    return result;
                }
            }

            QueueExecutionOrderResult idleResult = QueueExecutionOrder.Reorder(
                logicalRows,
                requested,
                dispatchedCount: 0,
                operation: operation);
            if (idleResult.Changed)
                NormalizeQueueSequences(logicalRows);
            return idleResult;
        }

        private List<DataGridViewRow> MergeActiveQueueOrderIntoLogicalRows(
            IReadOnlyList<DataGridViewRow> logicalRows,
            IReadOnlyList<DataGridViewRow> activeQueue,
            int dispatchedCount)
        {
            // A retry reuses its row object. If that object is in both the claimed
            // prefix and pending tail, represent it at its pending-tail position;
            // QueueSequence describes one logical row, not multiple attempts.
            int boundary = Math.Clamp(dispatchedCount, 0, activeQueue.Count);
            List<DataGridViewRow> pendingRows = activeQueue.Skip(boundary).Distinct().ToList();
            HashSet<DataGridViewRow> pendingSet = pendingRows.ToHashSet();
            List<DataGridViewRow> activeLogicalRows = activeQueue
                .Take(boundary)
                .Where(row => !pendingSet.Contains(row))
                .Distinct()
                .Concat(pendingRows)
                .ToList();

            // Reorder only the slots already occupied by rows in this run. Rows
            // outside the active scope retain their slots and relative order, so
            // filtering the full logical order to pending active rows gives the
            // runner's next distinct-row order without pulling later-run work in.
            HashSet<DataGridViewRow> logicalSet = logicalRows.ToHashSet();
            activeLogicalRows = activeLogicalRows
                .Where(logicalSet.Contains)
                .ToList();
            HashSet<DataGridViewRow> activeSet = activeLogicalRows.ToHashSet();
            var merged = logicalRows.ToList();
            int activeIndex = 0;
            for (int index = 0; index < merged.Count; index++)
            {
                if (activeSet.Contains(merged[index]))
                    merged[index] = activeLogicalRows[activeIndex++];
            }
            return merged;
        }

        private List<DataGridViewRow> OrderRowsByQueueSequence(IEnumerable<DataGridViewRow> rows)
        {
            List<DataGridViewRow> queueRows = rows.ToList();
            foreach (DataGridViewRow row in queueRows)
                EnsureRowMeta(row);

            var sequenceSnapshot = new List<(DataGridViewRow Row, long Sequence)>(queueRows.Count);
            if (_encodingActive)
            {
                lock (_activeEncodeQueueLock)
                {
                    foreach (DataGridViewRow row in queueRows)
                    {
                        if (row.Tag is not RowMeta meta)
                            throw new InvalidOperationException("Queue row metadata must be initialized before ordering.");
                        sequenceSnapshot.Add((row, meta.QueueSequence));
                    }
                }
            }
            else
            {
                foreach (DataGridViewRow row in queueRows)
                {
                    if (row.Tag is not RowMeta meta)
                        throw new InvalidOperationException("Queue row metadata must be initialized before ordering.");
                    sequenceSnapshot.Add((row, meta.QueueSequence));
                }
            }

            return sequenceSnapshot
                .OrderBy(item => item.Sequence)
                .Select(item => item.Row)
                .ToList();
        }

        private void NormalizeQueueSequences(IReadOnlyList<DataGridViewRow> logicalRows)
        {
            long sequence = 0;
            foreach (DataGridViewRow row in logicalRows)
            {
                if (row.Tag is not RowMeta meta)
                    throw new InvalidOperationException("Queue row metadata must be initialized before order normalization.");
                meta.QueueSequence = ++sequence;
            }

            // The allocator is monotonic. Keeping its high-water mark means every
            // subsequent normal append receives a unique sequence after this order.
            while (true)
            {
                long current = Interlocked.Read(ref _nextEncodeQueueSequence);
                if (current >= sequence ||
                    Interlocked.CompareExchange(
                        ref _nextEncodeQueueSequence,
                        sequence,
                        current) == current)
                {
                    break;
                }
            }
        }

        private bool TryAppendActiveEncodeQueueRow(DataGridViewRow row)
        {
            return AppendActiveEncodeQueueRow(row) == ActiveQueueAppendOutcome.Added;
        }

        private ActiveQueueAppendOutcome AppendActiveEncodeQueueRow(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow || row.DataGridView != dgvEncodeQueue)
                return ActiveQueueAppendOutcome.NotAdmitted;

            RowMeta meta = EnsureRowMeta(row);

            lock (_activeEncodeQueueLock)
            {
                if (!_encodingActive || _activeEncodeQueue == null)
                    return ActiveQueueAppendOutcome.NotAdmitted;
                if (_activeEncodeQueue.Contains(row))
                    return ActiveQueueAppendOutcome.AlreadyPresent;
                if (!_activeEncodeQueueAccepting)
                    return ActiveQueueAppendOutcome.NotAdmitted;

                // Admission to a live run is an append in logical order as well as
                // in the runner list, including rows that were already in the grid.
                meta.QueueSequence = AllocateEncodeQueueSequence();
                _activeEncodeQueue.Add(row);
            }

            ScheduleQueueExecutionOrderPresentationRefresh();
            return ActiveQueueAppendOutcome.Added;
        }

        private bool TrySoftExcludePendingDuplicateRow(
            DataGridViewRow row,
            RowMeta meta,
            string statusBeforeExclusion)
        {
            lock (_activeEncodeQueueLock)
            {
                if (_encodingActive && _activeEncodeQueue != null)
                {
                    int dispatchedCount = Math.Clamp(
                        _activeEncodeQueueDispatchedCount,
                        0,
                        _activeEncodeQueue.Count);
                    for (int index = 0; index < dispatchedCount; index++)
                    {
                        if (ReferenceEquals(_activeEncodeQueue[index], row))
                            return false;
                    }

                    // Make the eligibility change and pending-tail removal atomic
                    // with dispatch, so a just-excluded row cannot be claimed.
                    for (int index = _activeEncodeQueue.Count - 1; index >= dispatchedCount; index--)
                    {
                        if (ReferenceEquals(_activeEncodeQueue[index], row))
                            _activeEncodeQueue.RemoveAt(index);
                    }
                }

                meta.StatusBeforeDuplicateExclusion = statusBeforeExclusion;
                meta.ExcludedFromEncodeAsDuplicate = true;
                return true;
            }
        }

        private bool TryCompleteActiveEncodeQueueWhenDrained()
        {
            // EncodeQueueRunner invokes this callback while holding
            // _activeEncodeQueueLock. Do not reacquire it or perform UI work here.
            if (_activeEncodeQueue == null)
            {
                _activeEncodeQueueAccepting = false;
                return true;
            }

            if (_activeEncodeQueueDispatchedCount < _activeEncodeQueue.Count ||
                Volatile.Read(ref _pendingEncodeImports) > 0)
            {
                return false;
            }

            // An append that acquires this lock afterwards is outside the run;
            // every append that acquired it earlier is visible in Count above.
            _activeEncodeQueueAccepting = false;
            return true;
        }
    }
}
