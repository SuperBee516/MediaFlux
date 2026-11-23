using System.ComponentModel;
using MediaFlux.Models;

namespace MediaFlux
{
    public partial class MainForm
    {
        private DataGridView? _folderQueueGrid;
        private bool _folderQueueView;
        private bool _switchingQueueGridMode;
        private bool _refreshingFolderQueueGrid;
        private int _folderQueueTotal;
        private string _fileQueueSearch = "";
        private string _folderQueueSearch = "";

        private sealed record QueueFolder(string Path, DataGridViewRow[] Files);

        private Control CreateQueueGridModeSelector()
        {
            var selector = new ComboBox
            {
                Name = "cmbQueueGridMode",
                AccessibleName = "Queue display mode",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = ScaleUi(116),
                Margin = new Padding(0, 0, ScaleUi(8), 0)
            };
            selector.Items.AddRange(["File View", "Folder View"]);
            selector.SelectedIndex = 0;
            selector.SelectedIndexChanged += (_, _) => SetFolderQueueView(selector.SelectedIndex == 1);
            return selector;
        }

        private DataGridView CreateFolderQueueGrid()
        {
            var grid = new DataGridView
            {
                Name = "dgvFolderQueue",
                AccessibleName = "Encoding queue folders",
                AccessibleDescription = "Each row groups the queued files in one folder.",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                MinimumSize = dgvEncodeQueue.MinimumSize,
                Visible = false,
                ReadOnly = true,
                AllowDrop = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
            };
            AddFolderQueueColumn(grid, "colFolder", "Folder", 190);
            AddFolderQueueColumn(grid, "colFolderFiles", "Files", 64);
            AddFolderQueueColumn(grid, "colFolderStatus", "Status", 155);
            AddFolderQueueColumn(grid, "colFolderSize", "Size", 103);
            AddFolderQueueColumn(grid, "colFolderEstimatedSize", "Estimated Output", 194);
            var pathColumn = AddFolderQueueColumn(grid, "colFolderPath", "Folder Path", 230);
            pathColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            pathColumn.MinimumWidth = ScaleUi(150);

            var menu = new ContextMenuStrip();
            var startSelected = menu.Items.Add("Start Selected Folders", null, StartSelectedEncodeFilesFromContextMenu_Click);
            var startAll = menu.Items.Add("Start Entire Queue", null, StartFullEncodeQueueFromContextMenu_Click);
            var remove = menu.Items.Add("Remove Selected Folders from Queue", null, RemoveSelectedRows_Click);
            menu.Opening += (_, _) =>
            {
                DataGridViewRow[] selected = GetQueueWorkspaceSelectedFiles();
                bool canStart = !IsQueueEncodingActive() && _mediaRemuxCts == null;
                startSelected.Enabled = canStart && selected.Any(IsEligibleEncodeRow);
                startAll.Enabled = canStart && dgvEncodeQueue.Rows.Cast<DataGridViewRow>().Any(IsEligibleEncodeRow);
                remove.Enabled = _mediaRemuxCts == null && CanRemoveQueueRows(selected);
            };
            grid.ContextMenuStrip = menu;
            grid.Disposed += (_, _) => menu.Dispose();
            grid.SortCompare += FolderQueueGrid_SortCompare;
            grid.DragEnter += dgvEncodeQueue_DragEnter;
            grid.DragDrop += dgvEncodeQueue_DragDrop;
            grid.KeyDown += dgvEncodeQueue_KeyDown;
            grid.SelectionChanged += (_, _) =>
            {
                if (_folderQueueView && !Disposing && !_refreshingFolderQueueGrid && !_switchingQueueGridMode)
                    UpdateFolderQueueSelection();
            };
            grid.CellMouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0 || grid.Rows[e.RowIndex].Selected)
                    return;

                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
            };
            _folderQueueGrid = grid;
            return grid;
        }

        private DataGridViewColumn AddFolderQueueColumn(
            DataGridView grid, string name, string header, int width, bool sortable = true)
        {
            var column = new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = ScaleUi(width),
                SortMode = sortable ? DataGridViewColumnSortMode.Automatic : DataGridViewColumnSortMode.NotSortable
            };
            grid.Columns.Add(column);
            return column;
        }

        private void SetFolderQueueView(bool folderView)
        {
            if (_folderQueueView == folderView || _folderQueueGrid == null ||
                _queueWorkspaceSearchBox == null || _queueWorkspaceHost == null)
                return;

            _switchingQueueGridMode = true;
            _queueWorkspaceHost.SuspendLayout();
            try
            {
                if (folderView)
                    _fileQueueSearch = _queueWorkspaceSearchBox.Text;
                else
                    _folderQueueSearch = _queueWorkspaceSearchBox.Text;

                _folderQueueView = folderView;
                _queueWorkspaceSearchBox.PlaceholderText = folderView ? "Search folders..." : "Search queue...";
                _queueWorkspaceSearchBox.AccessibleName = folderView ? "Search queue folders" : "Search queue";
                _queueWorkspaceSearchBox.AccessibleDescription = folderView
                    ? "Searches folder names and paths."
                    : "Searches queue filenames and full paths without changing queue execution.";
                _queueWorkspaceSearchBox.Text = folderView ? _folderQueueSearch : _fileQueueSearch;
                dgvEncodeQueue.Visible = !folderView;
                _folderQueueGrid.Visible = folderView;
                SetQueueGridRowStyle(_folderQueueGrid, folderView);
                SetQueueGridRowStyle(dgvEncodeQueue, !folderView);
                ApplyEncodeQueueViewFilter();
            }
            finally
            {
                _switchingQueueGridMode = false;
                _queueWorkspaceHost.ResumeLayout();
            }

            UpdateFolderQueueSelection();
        }

        private void SetQueueGridRowStyle(Control grid, bool visible)
        {
            TableLayoutPanel host = _queueWorkspaceHost!;
            RowStyle style = host.RowStyles[host.GetRow(grid)];
            style.SizeType = visible ? SizeType.Percent : SizeType.Absolute;
            style.Height = visible ? 100F : 0F;
        }

        private void UpdateFolderQueueSelection()
        {
            UpdateQueueWorkspaceActionState();
            UpdateContextualDetails();
        }

        private DataGridViewRow[] GetQueueWorkspaceSelectedFiles()
        {
            if (!_folderQueueView)
                return dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToArray();

            return _folderQueueGrid!.SelectedRows.Cast<DataGridViewRow>()
                .SelectMany(row => ((QueueFolder)row.Tag!).Files)
                .Where(row => row.DataGridView == dgvEncodeQueue)
                .ToArray();
        }

        private void RefreshFolderQueueGrid()
        {
            DataGridView grid = _folderQueueGrid!;
            var folders = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .Select(row => (File: row, Path: GetFullPathFromRow(row)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .Select(item => (item.File, Folder: Path.GetDirectoryName(item.Path)))
                .Where(item => !string.IsNullOrEmpty(item.Folder))
                .GroupBy(item => item.Folder!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(item => item.File).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            _folderQueueTotal = folders.Count;

            // Status filters determine which folders appear. The displayed totals
            // and selection always cover the folder's full queue membership.
            string search = _queueWorkspaceSearchBox?.Text.Trim() ?? "";
            var visibleFolders = folders
                .Where(pair => pair.Key.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                               pair.Value.Any(MatchesQueueWorkspaceFilter))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var displayedRows = grid.Rows.Cast<DataGridViewRow>()
                .ToDictionary(row => ((QueueFolder)row.Tag!).Path, StringComparer.OrdinalIgnoreCase);
            DataGridViewRow[] previousSelection = GetQueueWorkspaceSelectedFiles();
            var selectedPaths = grid.SelectedRows.Cast<DataGridViewRow>()
                .Select(row => ((QueueFolder)row.Tag!).Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            DataGridViewColumn? sortedColumn = grid.SortedColumn;
            ListSortDirection sortDirection = grid.SortOrder == SortOrder.Descending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            _refreshingFolderQueueGrid = true;
            grid.SuspendLayout();
            try
            {
                foreach (var (path, row) in displayedRows)
                {
                    if (!visibleFolders.ContainsKey(path))
                        grid.Rows.Remove(row);
                }

                foreach (var (path, files) in visibleFolders)
                {
                    if (!displayedRows.TryGetValue(path, out DataGridViewRow? row))
                        row = grid.Rows[grid.Rows.Add()];

                    row.Tag = new QueueFolder(path, files);
                    UpdateFolderQueueRow(row, path, files);
                    row.Selected = selectedPaths.Contains(path);
                }

                if (sortedColumn != null && grid.SortOrder != SortOrder.None)
                    grid.Sort(sortedColumn, sortDirection);
            }
            finally
            {
                grid.ResumeLayout();
                _refreshingFolderQueueGrid = false;
            }

            if (!_switchingQueueGridMode)
            {
                DataGridViewRow[] currentSelection = GetQueueWorkspaceSelectedFiles();
                if (selectedPaths.Any(path => !visibleFolders.ContainsKey(path)) ||
                    !previousSelection.ToHashSet().SetEquals(currentSelection))
                    UpdateFolderQueueSelection();
                else if (currentSelection.Length > 0)
                    RefreshQueueInspectorSummary(currentSelection);
            }
        }

        private void UpdateFolderQueueRow(DataGridViewRow row, string path, DataGridViewRow[] files)
        {
            string name = Path.GetFileName(path);
            SetQueueWorkspaceCellValue(row.Cells["colFolder"], string.IsNullOrEmpty(name) ? path : name);
            SetQueueWorkspaceCellTooltip(row.Cells["colFolder"], path);
            SetQueueWorkspaceCellValue(row.Cells["colFolderPath"], path);
            if (!Equals(row.Cells["colFolderFiles"].Value, files.Length))
                row.Cells["colFolderFiles"].Value = files.Length;

            string status = string.Join(" · ", files
                .GroupBy(file => CompactQueueWorkspaceStatus(Convert.ToString(file.Cells["colStatus"].Value) ?? ""))
                .Select(group => $"{group.Count():N0} {group.Key}"));
            SetQueueWorkspaceCellValue(row.Cells["colFolderStatus"], status);
            SetQueueWorkspaceCellTooltip(row.Cells["colFolderStatus"], status);

            double sourceMb = files.Sum(GetInspectorSourceSizeMb);
            DataGridViewCell sizeCell = row.Cells["colFolderSize"];
            SetQueueWorkspaceCellValue(sizeCell, sourceMb > 0 ? FormatSize(sourceMb) : "—");
            sizeCell.Tag = sourceMb > 0 ? sourceMb : null;

            int eligible = 0;
            int estimated = 0;
            double estimatedMb = 0;
            foreach (DataGridViewRow file in files)
            {
                if (!IsEligibleEncodeRow(file) || file.Tag is RowMeta { AdaptiveSelectionSkipped: true })
                    continue;

                eligible++;
                if (file.Tag is RowMeta
                    {
                        EstimateState: QueueEstimateState.Pending or QueueEstimateState.Inspecting or
                        QueueEstimateState.Measuring or QueueEstimateState.Unavailable or QueueEstimateState.Failed
                    })
                {
                    continue;
                }

                double outputMb = GetInspectorEstimatedSizeMb(file);
                if (double.IsFinite(outputMb) && outputMb > 0)
                {
                    estimated++;
                    estimatedMb += outputMb;
                }
            }

            string estimate = estimated switch
            {
                0 when eligible == 0 => "—",
                0 => "No estimates yet",
                _ when estimated == eligible => FormatSize(estimatedMb),
                _ => $"{FormatSize(estimatedMb)} partial ({estimated:N0}/{eligible:N0})"
            };
            SetQueueWorkspaceCellValue(row.Cells["colFolderEstimatedSize"], estimate);
            row.Cells["colFolderEstimatedSize"].Tag = estimatedMb > 0 ? estimatedMb : null;
            SetQueueWorkspaceCellTooltip(row.Cells["colFolderEstimatedSize"],
                $"{estimated:N0} of {eligible:N0} eligible queue items have an output estimate.");
        }

        private void FolderQueueGrid_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
        {
            if (e.Column.Name is "colFolderSize" or "colFolderEstimatedSize")
            {
                DataGridView grid = (DataGridView)sender!;
                double? first = grid.Rows[e.RowIndex1].Cells[e.Column.Index].Tag as double?;
                double? second = grid.Rows[e.RowIndex2].Cells[e.Column.Index].Tag as double?;
                int comparison = first.HasValue == second.HasValue
                    ? first.GetValueOrDefault().CompareTo(second.GetValueOrDefault())
                    : first.HasValue ? 1 : -1;
                e.SortResult = comparison != 0
                    ? comparison
                    : StringComparer.OrdinalIgnoreCase.Compare(
                        Convert.ToString(grid.Rows[e.RowIndex1].Cells["colFolderPath"].Value),
                        Convert.ToString(grid.Rows[e.RowIndex2].Cells["colFolderPath"].Value));
                e.Handled = true;
            }
            else if (e.Column.Name == "colFolderStatus")
            {
                DataGridView grid = (DataGridView)sender!;
                e.SortResult = StringComparer.OrdinalIgnoreCase.Compare(
                    Convert.ToString(e.CellValue1),
                    Convert.ToString(e.CellValue2));
                if (e.SortResult == 0)
                {
                    e.SortResult = StringComparer.OrdinalIgnoreCase.Compare(
                        Convert.ToString(grid.Rows[e.RowIndex1].Cells["colFolderPath"].Value),
                        Convert.ToString(grid.Rows[e.RowIndex2].Cells["colFolderPath"].Value));
                }
                e.Handled = true;
            }
        }
    }
}
