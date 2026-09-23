using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MediaFlux;

public partial class MainForm
{
    private const int MaximumQueueColumnWidth = 100_000;
    private bool _applyingQueueColumnPreferences;
    private bool _applyingQueueColumnOrder;
    private bool _queueColumnResizeGesture;
    private bool _queueColumnResizeChanged;
    private bool _queueColumnOrderChanged;

    private void InitializeQueueColumnSizingPreferences()
    {
        if (_config.QueueColumnSizingInitialized)
            return;

        bool hasLegacyCustomWidth = false;
        foreach (KeyValuePair<string, int> pair in _config.EncodeGridColumnWidths ?? new())
        {
            if (string.Equals(pair.Key, "colName", StringComparison.OrdinalIgnoreCase) ||
                pair.Value <= 0 || pair.Value > MaximumQueueColumnWidth)
            {
                continue;
            }

            DataGridViewColumn? column = FindQueueColumn(pair.Key);
            if (column == null)
                continue;

            int defaultWidth = GetQueueColumnDefaultWidth(column);
            if (defaultWidth > 0 && pair.Value != defaultWidth)
            {
                hasLegacyCustomWidth = true;
                break;
            }
        }

        if (!hasLegacyCustomWidth && _config.SizeColumnWidth > 0 && _config.SizeColumnWidth != 92)
            hasLegacyCustomWidth = true;
        if (!hasLegacyCustomWidth && _config.CreatedColumnWidth > 0 && _config.CreatedColumnWidth != 140)
            hasLegacyCustomWidth = true;

        _config.QueueColumnSizingInitialized = true;
        _config.EncodeGridColumnWidthsCustomized = hasLegacyCustomWidth;
        _config.Save(_configPath);
    }

    private void ApplyQueueColumnLayoutPreferences()
    {
        if (dgvEncodeQueue == null)
            return;

        _applyingQueueColumnPreferences = true;
        try
        {
            foreach (DataGridViewColumn column in dgvEncodeQueue.Columns)
            {
                int minimum = GetQueueColumnMinimumWidth(column);
                column.MinimumWidth = minimum;
                if (column.Width < minimum)
                    column.Width = minimum;

                if (!_config.EncodeGridColumnWidthsCustomized &&
                    string.Equals(column.Name, "colName", StringComparison.OrdinalIgnoreCase))
                {
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                    column.FillWeight = 100;
                    continue;
                }

                if (_config.EncodeGridColumnWidthsCustomized)
                {
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    int customizedDefaultWidth = GetQueueColumnDefaultWidth(column);
                    if (customizedDefaultWidth > 0)
                    {
                        column.Width = Math.Clamp(
                            customizedDefaultWidth,
                            minimum,
                            GetQueueColumnMaximumWidth(column));
                    }
                    continue;
                }

                int defaultWidth = GetQueueColumnDefaultWidth(column);
                if (defaultWidth <= 0)
                    continue;

                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                column.Width = Math.Clamp(defaultWidth, minimum, GetQueueColumnMaximumWidth(column));
            }

            if (_config.EncodeGridColumnWidthsCustomized)
                RestoreQueueColumnWidths();
        }
        finally
        {
            _applyingQueueColumnPreferences = false;
        }

        ApplyQueueColumnResizeLock();
    }

    private void RestoreQueueColumnWidths()
    {
        foreach (KeyValuePair<string, int> pair in _config.EncodeGridColumnWidths ?? new())
        {
            DataGridViewColumn? column = FindQueueColumn(pair.Key);
            if (column == null || pair.Value <= 0 || pair.Value > MaximumQueueColumnWidth)
                continue;

            column.Width = Math.Clamp(
                pair.Value,
                GetQueueColumnMinimumWidth(column),
                GetQueueColumnMaximumWidth(column));
        }
    }

    private void ApplyQueueColumnResizeLock()
    {
        if (dgvEncodeQueue == null)
            return;

        bool locked = _config.EncodeGridColumnWidthsLocked;
        dgvEncodeQueue.AllowUserToResizeColumns = !locked;
        foreach (DataGridViewColumn column in dgvEncodeQueue.Columns)
            column.Resizable = locked ? DataGridViewTriState.False : DataGridViewTriState.True;
    }

    private void ResetQueueColumnWidths()
    {
        if (dgvEncodeQueue == null)
            return;

        _applyingQueueColumnPreferences = true;
        try
        {
            foreach (DataGridViewColumn column in dgvEncodeQueue.Columns)
            {
                column.MinimumWidth = GetQueueColumnMinimumWidth(column);
                if (string.Equals(column.Name, "colName", StringComparison.OrdinalIgnoreCase))
                {
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                    column.FillWeight = 100;
                    continue;
                }

                int defaultWidth = GetQueueColumnDefaultWidth(column);
                if (defaultWidth <= 0)
                    continue;

                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                column.Width = Math.Clamp(defaultWidth, column.MinimumWidth, GetQueueColumnMaximumWidth(column));
            }
        }
        finally
        {
            _applyingQueueColumnPreferences = false;
        }

        _config.EncodeGridColumnWidthsCustomized = false;
        _config.QueueColumnSizingInitialized = true;
        _config.EncodeGridColumnWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewColumn column in dgvEncodeQueue.Columns)
        {
            if (!string.Equals(column.Name, "colName", StringComparison.OrdinalIgnoreCase))
                _config.EncodeGridColumnWidths[column.Name] = column.Width;
        }
        _config.NameColumnWidth = 0;
        _config.SizeColumnWidth = 92;
        _config.CreatedColumnWidth = 140;
        _queueColumnResizeGesture = false;
        _queueColumnResizeChanged = false;
        ApplyQueueColumnResizeLock();
        _config.Save(_configPath);
    }

    private void DgvEncodeQueue_ColumnResizeMouseDown(object? sender, MouseEventArgs e)
    {
        _queueColumnResizeGesture = false;
        _queueColumnResizeChanged = false;
        if (e.Button != MouseButtons.Left || _config.EncodeGridColumnWidthsLocked ||
            !dgvEncodeQueue.AllowUserToResizeColumns)
        {
            return;
        }

        DataGridView.HitTestInfo hit = dgvEncodeQueue.HitTest(e.X, e.Y);
        if (hit.Type != DataGridViewHitTestType.ColumnHeader || hit.ColumnIndex < 0)
            return;

        Rectangle bounds = dgvEncodeQueue.GetColumnDisplayRectangle(hit.ColumnIndex, false);
        int dividerZone = Math.Max(4, ScaleUi(5));
        if (Math.Abs(e.X - bounds.Right) <= dividerZone || Math.Abs(e.X - bounds.Left) <= dividerZone)
            _queueColumnResizeGesture = true;
    }

    private void DgvEncodeQueue_ColumnResizeMouseUp(object? sender, MouseEventArgs e)
    {
        if (_queueColumnResizeGesture && _queueColumnResizeChanged)
            PersistQueueColumnWidths();
        _queueColumnResizeGesture = false;
        _queueColumnResizeChanged = false;

        if (_queueColumnOrderChanged)
            PersistQueueColumnOrder();
    }

    private void ApplyRememberedQueueColumnOrder()
    {
        List<string> normalized = NormalizeQueueColumnOrder(_config.EncodeGridColumnOrder);
        bool changed = !normalized.SequenceEqual(
            _config.EncodeGridColumnOrder ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        _applyingQueueColumnOrder = true;
        try
        {
            for (int index = 0; index < normalized.Count; index++)
            {
                DataGridViewColumn? column = FindQueueColumn(normalized[index]);
                if (column != null && column.DisplayIndex != index)
                    column.DisplayIndex = index;
            }
        }
        finally
        {
            _applyingQueueColumnOrder = false;
        }

        _config.EncodeGridColumnOrder = normalized;
        if (changed)
            _config.Save(_configPath);
    }

    private void MoveQueueColumn(string columnName, int direction)
    {
        if (direction is not (-1 or 1))
            return;

        List<string> currentOrder = GetCurrentQueueColumnOrder();
        int currentIndex = currentOrder.FindIndex(name =>
            string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase));
        int targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= currentOrder.Count)
            return;

        DataGridViewColumn? column = FindQueueColumn(columnName);
        if (column == null)
            return;

        _applyingQueueColumnOrder = true;
        try { column.DisplayIndex = targetIndex; }
        finally { _applyingQueueColumnOrder = false; }
        PersistQueueColumnOrder();
    }

    private void ResetQueueColumnOrder()
    {
        List<string> defaultOrder = GetDefaultQueueColumnOrder();
        _applyingQueueColumnOrder = true;
        try
        {
            for (int index = 0; index < defaultOrder.Count; index++)
            {
                DataGridViewColumn? column = FindQueueColumn(defaultOrder[index]);
                if (column != null && column.DisplayIndex != index)
                    column.DisplayIndex = index;
            }
        }
        finally
        {
            _applyingQueueColumnOrder = false;
        }

        _config.EncodeGridColumnOrder = defaultOrder;
        _config.Save(_configPath);
        _queueColumnOrderChanged = false;
    }

    private void PersistQueueColumnOrder()
    {
        _config.EncodeGridColumnOrder = GetCurrentQueueColumnOrder();
        _config.Save(_configPath);
        _queueColumnOrderChanged = false;
    }

    private List<string> GetCurrentQueueColumnOrder() => dgvEncodeQueue.Columns
        .Cast<DataGridViewColumn>()
        .OrderBy(column => column.DisplayIndex)
        .Select(column => column.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToList();

    private List<string> NormalizeQueueColumnOrder(IEnumerable<string>? savedOrder)
    {
        var normalized = new List<string>(dgvEncodeQueue.Columns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (savedOrder != null)
        {
            foreach (string? savedName in savedOrder)
            {
                if (string.IsNullOrWhiteSpace(savedName))
                    continue;
                DataGridViewColumn? column = FindQueueColumn(savedName);
                if (column != null && seen.Add(column.Name))
                    normalized.Add(column.Name);
            }
        }

        foreach (string name in GetDefaultQueueColumnOrder())
        {
            DataGridViewColumn? column = FindQueueColumn(name);
            if (column != null && seen.Add(column.Name))
                normalized.Add(column.Name);
        }

        foreach (DataGridViewColumn column in dgvEncodeQueue.Columns
                     .Cast<DataGridViewColumn>()
                     .OrderBy(column => column.DisplayIndex))
        {
            if (!string.IsNullOrWhiteSpace(column.Name) && seen.Add(column.Name))
                normalized.Add(column.Name);
        }

        return normalized;
    }

    private List<string> GetDefaultQueueColumnOrder()
    {
        string[] phaseOneOrder =
        [
            "colName", "colStatus", "colEncodeRecommendation", "colPlannedOutput",
            "colSourceEstimate", "colProgress", "colETA", "colSize", "colEstimatedSize",
            "colCreated", "colCustom", "colDuplicate", "colDuplicateConfidence", "colDuplicateAction"
        ];

        var result = phaseOneOrder.Where(name => FindQueueColumn(name) != null).ToList();
        var included = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
        result.AddRange(dgvEncodeQueue.Columns.Cast<DataGridViewColumn>()
            .OrderBy(column => column.DisplayIndex)
            .Select(column => column.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name) && included.Add(name)));
        return result;
    }

    private void RefreshQueueColumnOrderChoices(ComboBox picker, string? selectedColumnName)
    {
        picker.BeginUpdate();
        try
        {
            picker.Items.Clear();
            foreach (DataGridViewColumn column in dgvEncodeQueue.Columns
                         .Cast<DataGridViewColumn>()
                         .OrderBy(column => column.DisplayIndex))
            {
                string displayName = string.IsNullOrWhiteSpace(column.HeaderText)
                    ? column.Name
                    : column.HeaderText;
                if (!column.Visible)
                    displayName += " (hidden)";
                picker.Items.Add(new QueueColumnSettingsItem(column.Name, displayName));
            }

            int selectedIndex = -1;
            for (int index = 0; index < picker.Items.Count; index++)
            {
                if (picker.Items[index] is QueueColumnSettingsItem item &&
                    string.Equals(item.ColumnName, selectedColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = index;
                    break;
                }
            }
            picker.SelectedIndex = selectedIndex >= 0 ? selectedIndex : picker.Items.Count > 0 ? 0 : -1;
        }
        finally { picker.EndUpdate(); }
    }

    private void UpdateQueueColumnMoveButtons(ComboBox picker, Button moveLeft, Button moveRight)
    {
        DataGridViewColumn? column = picker.SelectedItem is QueueColumnSettingsItem item
            ? FindQueueColumn(item.ColumnName)
            : null;
        moveLeft.Enabled = column != null && column.DisplayIndex > 0;
        moveRight.Enabled = column != null && column.DisplayIndex < dgvEncodeQueue.Columns.Count - 1;
    }

    private sealed record QueueColumnSettingsItem(string ColumnName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private void HandleQueueColumnWidthChanged(DataGridViewColumn column)
    {
        if (_applyingQueueColumnPreferences ||
            _config.EncodeGridColumnWidthsLocked ||
            !_queueColumnResizeGesture)
        {
            return;
        }

        int clampedWidth = Math.Clamp(
            column.Width,
            GetQueueColumnMinimumWidth(column),
            GetQueueColumnMaximumWidth(column));
        if (clampedWidth != column.Width)
        {
            _applyingQueueColumnPreferences = true;
            try { column.Width = clampedWidth; }
            finally { _applyingQueueColumnPreferences = false; }
        }

        if (!_config.EncodeGridColumnWidthsCustomized)
            FreezeCurrentQueueColumnWidths();

        _config.EncodeGridColumnWidths ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(column.Name))
            _config.EncodeGridColumnWidths[column.Name] = column.Width;
        if (column.Name == "colSize")
            _config.SizeColumnWidth = column.Width;
        else if (column.Name == "colCreated")
            _config.CreatedColumnWidth = column.Width;
        _queueColumnResizeChanged = true;
    }

    private void FreezeCurrentQueueColumnWidths()
    {
        _applyingQueueColumnPreferences = true;
        try
        {
            foreach (DataGridViewColumn column in dgvEncodeQueue.Columns)
            {
                column.MinimumWidth = GetQueueColumnMinimumWidth(column);
                int boundedWidth = Math.Clamp(
                    column.Width,
                    column.MinimumWidth,
                    GetQueueColumnMaximumWidth(column));
                if (column.Width != boundedWidth)
                    column.Width = boundedWidth;
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            }
        }
        finally
        {
            _applyingQueueColumnPreferences = false;
        }

        _config.EncodeGridColumnWidthsCustomized = true;
        _config.QueueColumnSizingInitialized = true;
        _config.EncodeGridColumnWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewColumn current in dgvEncodeQueue.Columns)
            _config.EncodeGridColumnWidths[current.Name] = current.Width;
    }

    private void PersistQueueColumnWidths()
    {
        if (dgvEncodeQueue == null || _config.EncodeGridColumnWidthsLocked)
            return;

        _config.EncodeGridColumnWidths ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewColumn column in dgvEncodeQueue.Columns)
            _config.EncodeGridColumnWidths[column.Name] = column.Width;
        _config.EncodeGridColumnWidthsCustomized = true;
        _config.QueueColumnSizingInitialized = true;
        _config.Save(_configPath);
    }

    private DataGridViewColumn? FindQueueColumn(string name) =>
        dgvEncodeQueue.Columns.Cast<DataGridViewColumn>()
            .FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));

    private int GetQueueColumnDefaultWidth(DataGridViewColumn column) => column.Name switch
    {
        "colName" => 0,
        "colSize" => 92,
        "colEstimatedSize" => 150,
        "colEncodeRecommendation" => ScaleUi(120),
        "colCreated" => 140,
        "colStatus" => ScaleUi(86),
        "colProgress" => ScaleUi(72),
        "colETA" => ScaleUi(72),
        "colCustom" => 72,
        "colDuplicate" => 92,
        "colDuplicateConfidence" => 92,
        "colDuplicateAction" => 130,
        "colPlannedOutput" => ScaleUi(190),
        "colSourceEstimate" => ScaleUi(205),
        _ => 0
    };

    private int GetQueueColumnMinimumWidth(DataGridViewColumn column) => column.Name switch
    {
        "colName" => ScaleUi(150),
        "colStatus" => ScaleUi(64),
        "colEncodeRecommendation" => ScaleUi(92),
        "colPlannedOutput" => ScaleUi(120),
        "colSourceEstimate" => ScaleUi(140),
        "colProgress" or "colETA" => ScaleUi(58),
        _ => Math.Max(ScaleUi(48), column.MinimumWidth)
    };

    private int GetQueueColumnMaximumWidth(DataGridViewColumn column) =>
        Math.Max(ScaleUi(1600), GetQueueColumnMinimumWidth(column));
}
