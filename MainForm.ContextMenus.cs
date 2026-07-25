using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;


namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        // All context-menu setup + context-menu-only handlers will live here.

        private void InitializeAudioQueueContextMenu()
        {
            // ─── Audio queue context menu ───────────────────────────
            var audioMenu = new ContextMenuStrip();
            audioMenu.Items.Add("Start Selected", null, StartSelectedAudio_Click);
            audioMenu.Items.Add(new ToolStripSeparator());
            audioMenu.Items.Add("Remove Selected", null, RemoveSelectedAudioRows_Click);
            audioMenu.Items.Add("Clear Queue", null, ClearAudioQueue_Click);
            audioMenu.Items.Add(new ToolStripSeparator());
            audioMenu.Items.Add("Open Containing Folder", null, OpenAudioFileFolder_Click);
            audioMenu.Items.Add("Edit Metadata…", null, EditAudioMetadata_Click);

            dgvAudioQueue.ContextMenuStrip = audioMenu;
            dgvAudioQueue.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvAudioQueue.MultiSelect = true;
        }

        private void InitializeEncodeQueueContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Start", null, StartEncodeFromContextMenu_Click);
            menu.Items.Add("Add to Encoding Queue", null, AddToEncodeQueueFromContextMenu_Click);

            var customSettings = new ToolStripMenuItem("Custom Encode Settings");
            var customProfileMenu = new ToolStripMenuItem("Quality / File Size");

            IEnumerable<string> profileItems = comboCompressionProfile?.Items
                .Cast<object>()
                .Select(item => item.ToString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                ?? Enumerable.Empty<string>();

            if (!profileItems.Any())
            {
                profileItems = new[]
                {
                    "Very High Quality (Largest File)",
                    "High Quality",
                    "Medium Quality (Default)",
                    "Low Quality (Smaller File)",
                    "Very Low Quality (Smallest File)"
                };
            }

            foreach (var profile in profileItems)
            {
                var item = new ToolStripMenuItem(profile)
                {
                    Tag = profile
                };
                item.Click += CustomProfileMenuItem_Click;
                customProfileMenu.DropDownItems.Add(item);
            }

            customSettings.DropDownItems.Add(customProfileMenu);
            customSettings.DropDownItems.Add("Set Target Size…", null, SetCustomTargetSize_Click);
            customSettings.DropDownItems.Add(new ToolStripSeparator());
            customSettings.DropDownItems.Add("Clear Custom Settings", null, ClearCustomSettings_Click);
            menu.Items.Add(customSettings);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Remove Selected", null, RemoveSelectedRows_Click);
            menu.Items.Add("Clear Grid", null, ClearGrid_Click);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Rename File…", null, RenameFile_Click);
            menu.Items.Add("Open Location", null, OpenLocationFromContextMenu_Click);
            menu.Items.Add("Copy Source Path", null, CopySourcePathFromContextMenu_Click);
            menu.Items.Add("Copy Output Preview", null, CopyOutputPreviewFromContextMenu_Click);
            menu.Items.Add("Open Duplicate Manager", null, ShowDuplicateManager_Click);
            menu.Items.Add("Include Selected Exact Duplicate(s) in Encode", null, IncludeSelectedDuplicateRowsInEncode_Click);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Schedule Start…", null, ScheduleEncode_Click);

            dgvEncodeQueue.ContextMenuStrip = menu;
        }

        private void IncludeSelectedDuplicateRowsInEncode_Click(object? sender, EventArgs e)
        {
            int included = 0;
            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow || row.Tag is not RowMeta meta || !meta.ExcludedFromEncodeAsDuplicate)
                    continue;

                meta.ExcludedFromEncodeAsDuplicate = false;
                meta.DuplicateExclusionOverridden = true;
                SetEncodeRowState(row, meta.StatusBeforeDuplicateExclusion, "", "", "Included in encoding by user override; the duplicate marking remains for review.");

                lock (_activeEncodeQueueLock)
                {
                    if (_encodingActive && _activeEncodeQueue != null && !_activeEncodeQueue.Contains(row))
                        _activeEncodeQueue.Add(row);
                }
                included++;
            }

            if (included == 0)
            {
                ShowStatusInfo("Select one or more soft-excluded exact duplicate rows first.");
                return;
            }

            UpdateDuplicateSummary(_lastDuplicateScanResult);
            SafeRefreshEstimates();
            ShowStatusInfo($"Included {included:N0} exact duplicate file(s) in encoding. No source files were changed.");
        }

        private void AddToEncodeQueueFromContextMenu_Click(object? sender, EventArgs e)
        {
            if (!_encodingActive || _activeEncodeQueue == null)
            {
                ShowStatusInfo("Start encoding first, then use Add to Encoding Queue while it is running.");
                return;
            }

            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                ShowStatusInfo("Select one or more files first.");
                return;
            }

            int added = 0;
            int excluded = 0;

            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow || row.DataGridView == null)
                    continue;

                if (row.Tag is RowMeta meta && meta.ExcludedFromEncodeAsDuplicate)
                {
                    excluded++;
                    continue;
                }

                bool addedRow = false;
                lock (_activeEncodeQueueLock)
                {
                    // Don’t enqueue the same row twice
                    if (!_activeEncodeQueue.Contains(row))
                    {
                        _activeEncodeQueue.Add(row);
                        addedRow = true;
                    }
                }

                if (addedRow)
                    added++;
            }

            if (added > 0)
            {
                toolStripStatusLabel1.Text =
                    $"Added {added} file(s) to the in-progress encode queue.";

                int totalNow;
                lock (_activeEncodeQueueLock)
                {
                    totalNow = _activeEncodeQueue.Count;
                }
                int queued = totalNow - _encodeProcessedCount;

                string currentFileName = "Current file";
                if (_activeEncodeRow != null)
                {
                    string? path = null;

                    if (_activeEncodeRow.Tag is RowMeta rm)
                        path = rm.Path;
                    else if (_activeEncodeRow.Tag is string tagPath)
                        path = tagPath;

                    if (!string.IsNullOrEmpty(path))
                        currentFileName = Path.GetFileName(path);
                }

                lblEncodeStatus.Text =
                    $"Encoding: {currentFileName} ({_encodeProcessedCount}/{totalNow}) – Queued: {queued}";
            }
            else if (excluded > 0)
            {
                ShowStatusInfo("Soft-excluded exact duplicates were not added. Use 'Include Selected Exact Duplicate(s) in Encode' first.");
            }
        }

        private void OpenLocationFromContextMenu_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
                return;

            var row = dgvEncodeQueue.SelectedRows[0];

            string? path = null;

            // Preferred: RowMeta
            if (row.Tag is RowMeta meta)
            {
                path = meta.IsDvdEncode && meta.DvdEncodeOptions != null
                    ? Path.GetDirectoryName(
                        meta.DvdEncodeOptions.Candidate.Segments[0].Path)
                    : meta.Path;
            }
            else
                path = row.Cells["colPath"]?.Value as string
                       ?? row.Cells["colSource"]?.Value as string;

            if (string.IsNullOrWhiteSpace(path) ||
                (!File.Exists(path) && !Directory.Exists(path)))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = Directory.Exists(path)
                        ? "\"" + path + "\""
                        : "/select,\"" + path + "\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Failed to open location:\n" + ex.Message,
                    "Open Location",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void CopySourcePathFromContextMenu_Click(object? sender, EventArgs e)
        {
            var path = GetSelectedEncodeRowPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowStatusInfo("Select a file before copying its path.");
                return;
            }

            Clipboard.SetText(path);
            ShowStatusInfo("Copied source path.");
        }

        private void CopyOutputPreviewFromContextMenu_Click(object? sender, EventArgs e)
        {
            var path = GetSelectedEncodeRowPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowStatusInfo("Select a file before copying its output preview.");
                return;
            }

            var row = dgvEncodeQueue.SelectedRows[0];
            string outputFolder = cmbEncodeOutput?.Text ?? "";
            if (row.Tag is RowMeta { IsDvdEncode: true } dvdMeta &&
                dvdMeta.DvdEncodeOptions != null)
            {
                outputFolder = Path.GetDirectoryName(
                    dvdMeta.DvdEncodeOptions.OutputPath) ?? outputFolder;
            }
            if (string.IsNullOrWhiteSpace(outputFolder))
                outputFolder = Path.GetDirectoryName(path) ?? "";

            string format = comboVideoFormat?.Text ?? "";
            string outputBaseName =
                row.Tag is RowMeta { IsDvdEncode: true } meta &&
                meta.DvdEncodeOptions != null
                    ? Path.GetFileNameWithoutExtension(meta.DvdEncodeOptions.OutputPath)
                    : Path.GetFileNameWithoutExtension(path);
            string outputPreview = Path.Combine(
                outputFolder,
                outputBaseName + BuildOutputSuffix(format) + ".mp4");

            Clipboard.SetText(outputPreview);
            ShowStatusInfo("Copied output preview path.");
        }

        private string? GetSelectedEncodeRowPath()
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
                return null;

            var row = dgvEncodeQueue.SelectedRows[0];
            if (row.Tag is RowMeta meta)
            {
                return meta.IsDvdEncode && meta.DvdEncodeOptions != null
                    ? Path.GetDirectoryName(
                        meta.DvdEncodeOptions.Candidate.Segments[0].Path)
                    : meta.Path;
            }
            if (row.Tag is string path)
                return path;

            return row.Cells["colPath"]?.Value as string
                   ?? row.Cells["colSource"]?.Value as string;
        }

        private void CustomProfileMenuItem_Click(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem item || item.Tag is not string profile)
                return;

            ApplyCustomProfile(profile);
        }

        private void SetCustomTargetSize_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                ShowStatusInfo("Select one or more files before setting a custom target size.");
                return;
            }

            double initial = 0;
            if (dgvEncodeQueue.SelectedRows.Count == 1 &&
                dgvEncodeQueue.SelectedRows[0].Tag is RowMeta rm &&
                rm.CustomTargetMb.HasValue)
            {
                initial = rm.CustomTargetMb.Value;
            }
            else if (double.TryParse(txtTargetSize.Text, out var globalMb) && globalMb > 0)
            {
                initial = globalMb;
            }
            else
            {
                initial = 1000;
            }

            if (!TryPromptTargetSize(initial, out var targetMb))
                return;

            ApplyCustomTarget(targetMb);
        }

        private void ClearCustomSettings_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                ShowStatusInfo("Select one or more files before clearing custom settings.");
                return;
            }

            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow || row.DataGridView == null)
                    continue;

                if (_activeEncodeRow == row)
                    continue;

                var meta = EnsureRowMeta(row);
                meta.CustomCompressionProfile = null;
                meta.CustomTargetMb = null;
                var path = GetPathFromRow(row);
                if (!string.IsNullOrWhiteSpace(path))
                    _estimatedSizeMap.Remove(path);

                UpdateRowCustomFlag(row);
            }

            RunEstimatePass();
            UpdateSizeTotals();
        }

        private void ApplyCustomProfile(string profile)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                ShowStatusInfo("Select one or more files before applying a custom profile.");
                return;
            }

            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow || row.DataGridView == null)
                    continue;

                if (_activeEncodeRow == row)
                    continue;

                var meta = EnsureRowMeta(row);
                meta.CustomCompressionProfile = profile;
                meta.CustomTargetMb = null;
                UpdateRowCustomFlag(row);
            }

            RunEstimatePass();
        }

        private void ApplyCustomTarget(double targetMb)
        {
            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow || row.DataGridView == null)
                    continue;

                if (_activeEncodeRow == row)
                    continue;

                var meta = EnsureRowMeta(row);
                meta.CustomTargetMb = targetMb;
                meta.CustomCompressionProfile = null;
                UpdateRowCustomFlag(row);
            }

            RunEstimatePass();
        }

        private bool TryPromptTargetSize(double initialMb, out double targetMb)
        {
            targetMb = 0;

            using var dialog = new MediaFluxForm
            {
                Text = "Set Target Size",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(320, 140),
                ShowInTaskbar = false
            };

            var lbl = new Label
            {
                Text = "Target size (MB):",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            var input = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 200000,
                DecimalPlaces = 1,
                Value = (decimal)Math.Min(200000, Math.Max(1, initialMb)),
                Anchor = AnchorStyles.Left | AnchorStyles.Right
            };

            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Right
            };

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Right
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(lbl, 0, 0);
            layout.Controls.Add(input, 1, 0);

            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill
            };
            buttonPanel.Controls.Add(btnOk);
            buttonPanel.Controls.Add(btnCancel);

            layout.SetColumnSpan(buttonPanel, 2);
            layout.Controls.Add(buttonPanel, 0, 2);

            dialog.Controls.Add(layout);
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return false;

            targetMb = (double)input.Value;
            return targetMb > 0;
        }
    }
}
