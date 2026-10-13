using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MediaFlux.Services;

namespace MediaFlux;

public partial class MainForm
{
    private const int QueueOrderRunningValue = -1;
    private const int QueueOrderDispatchedValue = -2;
    private int _queueExecutionOrderRefreshPosted;

    private readonly record struct QueueExecutionPositionSnapshot(
        Dictionary<DataGridViewRow, int> Positions,
        int PendingCount);

    private ToolStripMenuItem CreateQueuePriorityMenu()
    {
        var priority = new ToolStripMenuItem("Queue Priority")
        {
            Name = "queuePriorityMenu",
            AccessibleName = "Queue execution priority",
            AccessibleDescription = "Changes logical execution order only. Grid sorting and filtering remain presentation-only."
        };

        AddQueuePriorityItem(priority, "Encode Next", QueueExecutionOrderOperation.EncodeNext);
        priority.DropDownItems.Add(new ToolStripSeparator());
        AddQueuePriorityItem(priority, "Move to Top", QueueExecutionOrderOperation.MoveToTop);
        AddQueuePriorityItem(priority, "Move Up", QueueExecutionOrderOperation.MoveUp);
        AddQueuePriorityItem(priority, "Move Down", QueueExecutionOrderOperation.MoveDown);
        AddQueuePriorityItem(priority, "Move to Bottom", QueueExecutionOrderOperation.MoveToBottom);
        priority.DropDownOpening += (_, __) => UpdateQueuePriorityCommandState(priority);
        return priority;
    }

    private void AddQueuePriorityItem(
        ToolStripMenuItem parent,
        string text,
        QueueExecutionOrderOperation operation)
    {
        var item = new ToolStripMenuItem(text)
        {
            Name = $"queuePriority{operation}",
            Tag = operation,
            AccessibleName = text,
            AccessibleDescription = "Reorders selected pending queue items without changing the current grid sort or starting encoding."
        };
        item.Click += QueuePriorityMenuItem_Click;
        parent.DropDownItems.Add(item);
    }

    private void UpdateQueuePriorityCommandState(ToolStripMenuItem priority)
    {
        List<DataGridViewRow> selectedRows = GetSelectedEncodeRowsInExecutionOrder().ToList();
        bool anyEnabled = false;
        foreach (ToolStripItem toolItem in priority.DropDownItems)
        {
            if (toolItem is not ToolStripMenuItem item ||
                item.Tag is not QueueExecutionOrderOperation operation)
            {
                continue;
            }

            item.Enabled = selectedRows.Count > 0 &&
                           PreviewQueueExecutionOrder(selectedRows, operation).Changed;
            anyEnabled |= item.Enabled;
        }

        priority.Enabled = anyEnabled;
    }

    private QueueExecutionOrderResult PreviewQueueExecutionOrder(
        IReadOnlyCollection<DataGridViewRow> selectedRows,
        QueueExecutionOrderOperation operation)
    {
        List<DataGridViewRow> items;
        int dispatchedCount = 0;
        bool activeQueueAvailable;
        lock (_activeEncodeQueueLock)
        {
            activeQueueAvailable = _encodingActive && _activeEncodeQueue != null;
            if (activeQueueAvailable)
            {
                items = _activeEncodeQueue!.ToList();
                dispatchedCount = _activeEncodeQueueDispatchedCount;
            }
            else
            {
                items = new List<DataGridViewRow>();
            }
        }

        if (!activeQueueAvailable)
            items = GetEligibleEncodeRowsInExecutionOrder().ToList();

        // Duplicate-excluded rows are never offered to the ordering API as pending
        // candidates. They remain in the grid and are reported as unavailable.
        List<DataGridViewRow> reorderableSelection = selectedRows
            .Where(row => row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true })
            .ToList();
        return QueueExecutionOrder.Reorder(
            items,
            reorderableSelection,
            dispatchedCount,
            operation);
    }

    private void QueuePriorityMenuItem_Click(object? sender, EventArgs e)
    {
        if (sender is ToolStripItem { Tag: QueueExecutionOrderOperation operation })
            ExecuteQueuePriorityOperation(operation);
    }

    private void ExecuteQueuePriorityOperation(QueueExecutionOrderOperation operation)
    {
        List<DataGridViewRow> selectedRows = GetSelectedEncodeRowsInExecutionOrder().ToList();
        if (selectedRows.Count == 0)
            return;

        int excludedCount = selectedRows.Count(row =>
            row.Tag is RowMeta { ExcludedFromEncodeAsDuplicate: true });
        List<DataGridViewRow> reorderableSelection = selectedRows
            .Where(row => row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true })
            .ToList();
        QueueExecutionOrderResult result = ReorderQueueRows(reorderableSelection, operation);

        if (result.Changed)
            RefreshQueueExecutionOrderPresentation();
        else
            RefreshQueueInspectorExecutionPosition();

        int unavailableCount = result.DispatchedCount + result.NotPendingCount + excludedCount;
        if (unavailableCount > 0)
        {
            var details = new List<string>();
            int dispatched = result.DispatchedCount;
            if (dispatched > 0)
                details.Add($"{dispatched:N0} already dispatched");
            int outsideRun = result.NotPendingCount;
            if (outsideRun > 0)
                details.Add($"{outsideRun:N0} outside the current execution scope");
            if (excludedCount > 0)
                details.Add($"{excludedCount:N0} duplicate-excluded");

            toolStripStatusLabel1.Text = result.Changed
                ? $"Queue priority updated; {string.Join(", ", details)} item(s) were not moved."
                : $"No selected item could be moved; {string.Join(", ", details)} item(s) are not reorderable.";
        }
    }

    private QueueExecutionPositionSnapshot GetQueueExecutionPositionSnapshot()
    {
        List<DataGridViewRow> rows = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .ToList();
        var positions = rows.ToDictionary(row => row, _ => 0);
        foreach (DataGridViewRow row in rows)
        {
            if (row.Tag is RowMeta { ExcludedFromEncodeAsDuplicate: true })
                continue;
            if (IsQueueRowActivelyEncoding(row))
                positions[row] = QueueOrderRunningValue;
        }

        List<DataGridViewRow>? activeQueue = null;
        int dispatchedCount = 0;
        lock (_activeEncodeQueueLock)
        {
            if (_encodingActive && _activeEncodeQueue != null)
            {
                activeQueue = _activeEncodeQueue.ToList();
                dispatchedCount = Math.Clamp(_activeEncodeQueueDispatchedCount, 0, activeQueue.Count);
            }
        }

        if (activeQueue != null)
        {
            HashSet<DataGridViewRow> pendingRows = activeQueue.Skip(dispatchedCount)
                .Where(row => row != null && positions.ContainsKey(row) &&
                              row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true })
                .Distinct()
                .ToHashSet();
            foreach (DataGridViewRow row in activeQueue.Take(dispatchedCount).Distinct())
            {
                if (positions.TryGetValue(row, out int current) && current != QueueOrderRunningValue &&
                    !pendingRows.Contains(row))
                {
                    positions[row] = QueueOrderDispatchedValue;
                }
            }

            int pendingPosition = 0;
            foreach (DataGridViewRow row in activeQueue.Skip(dispatchedCount).Distinct())
            {
                if (!pendingRows.Contains(row))
                    continue;
                pendingPosition++;
                if (positions[row] != QueueOrderRunningValue)
                    positions[row] = pendingPosition;
            }

            return new QueueExecutionPositionSnapshot(positions, pendingPosition);
        }

        int queuePosition = 0;
        foreach (DataGridViewRow row in GetEligibleEncodeRowsInExecutionOrder())
        {
            queuePosition++;
            if (positions.TryGetValue(row, out int current) && current != QueueOrderRunningValue)
                positions[row] = queuePosition;
        }

        return new QueueExecutionPositionSnapshot(positions, queuePosition);
    }

    private static string FormatQueueExecutionOrderCellValue(object? value)
    {
        if (value is not int position)
            return string.Empty;
        if (position == QueueOrderRunningValue)
            return "Running";
        if (position == QueueOrderDispatchedValue)
            return "Dispatched";
        return position > 0
            ? position.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : string.Empty;
    }

    private string GetQueueExecutionPositionText(DataGridViewRow row)
    {
        QueueExecutionPositionSnapshot snapshot = GetQueueExecutionPositionSnapshot();
        if (!snapshot.Positions.TryGetValue(row, out int position) || position == 0)
            return "Not queued";
        if (position == QueueOrderRunningValue)
            return "Running";
        if (position == QueueOrderDispatchedValue)
            return "Dispatched";
        return position == 1
            ? "Next"
            : $"{position:N0} of {snapshot.PendingCount:N0}";
    }

    private void RefreshQueueExecutionOrderPresentation()
    {
        if (!dgvEncodeQueue.Columns.Contains("colOrder") || IsDisposed)
            return;
        if (InvokeRequired)
        {
            ScheduleQueueExecutionOrderPresentationRefresh();
            return;
        }

        QueueExecutionPositionSnapshot snapshot = GetQueueExecutionPositionSnapshot();
        DataGridViewRow[] rows = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .ToArray();
        DataGridViewRow[] selectedRows = rows.Where(row => row.Selected).ToArray();
        var selectedSet = selectedRows.ToHashSet();
        DataGridViewRow? currentRow = dgvEncodeQueue.CurrentRow;
        string? currentColumnName = dgvEncodeQueue.CurrentCell?.OwningColumn.Name;
        bool changed = false;

        dgvEncodeQueue.SuspendLayout();
        try
        {
            foreach (DataGridViewRow row in rows)
            {
                int position = snapshot.Positions.TryGetValue(row, out int value) ? value : 0;
                object? cellValue = position == 0 ? null : position;
                DataGridViewCell cell = row.Cells["colOrder"];
                if (!Equals(cell.Value, cellValue))
                {
                    cell.Value = cellValue;
                    changed = true;
                }
                string tooltip = position switch
                {
                    QueueOrderRunningValue => "This item has been dispatched and is currently running.",
                    QueueOrderDispatchedValue => "This item has already been dispatched in the current run.",
                    > 0 => $"Execution position {position:N0} of {snapshot.PendingCount:N0}. Column sort and queue filters do not change this order.",
                    _ => "This item is not in the applicable pending execution scope."
                };
                if (!string.Equals(cell.ToolTipText, tooltip, StringComparison.Ordinal))
                    cell.ToolTipText = tooltip;
            }
        }
        finally
        {
            dgvEncodeQueue.ResumeLayout(false);
        }

        if (changed && dgvEncodeQueue.SortedColumn?.Name == "colOrder")
            ReapplyCurrentEncodeQueueSort();

        foreach (DataGridViewRow row in rows)
        {
            if (row.Visible && row.Selected != selectedSet.Contains(row))
                row.Selected = selectedSet.Contains(row);
        }
        if (currentRow != null && currentRow.DataGridView == dgvEncodeQueue && currentRow.Visible &&
            currentColumnName != null && dgvEncodeQueue.Columns.Contains(currentColumnName))
        {
            try { dgvEncodeQueue.CurrentCell = currentRow.Cells[currentColumnName]; }
            catch (InvalidOperationException) { }
        }

        dgvEncodeQueue.InvalidateColumn(dgvEncodeQueue.Columns["colOrder"].Index);
        RefreshQueueInspectorExecutionPosition();
    }

    private void ScheduleQueueExecutionOrderPresentationRefresh()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        if (!InvokeRequired)
        {
            RefreshQueueExecutionOrderPresentation();
            return;
        }
        if (System.Threading.Interlocked.Exchange(ref _queueExecutionOrderRefreshPosted, 1) != 0)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                System.Threading.Interlocked.Exchange(ref _queueExecutionOrderRefreshPosted, 0);
                if (!IsDisposed)
                    RefreshQueueExecutionOrderPresentation();
            }));
        }
        catch (InvalidOperationException)
        {
            System.Threading.Interlocked.Exchange(ref _queueExecutionOrderRefreshPosted, 0);
        }
    }

    private void RefreshQueueInspectorExecutionPosition()
    {
        if (_queueInspectorExecution == null)
            return;

        DataGridViewRow[] selectedRows = GetQueueInspectorSelectedRows();
        string text = selectedRows.Length switch
        {
            0 => "—",
            1 => GetQueueExecutionPositionText(selectedRows[0]),
            _ => "Varies by item"
        };
        SetInspectorText(_queueInspectorExecution, text);
    }
}
