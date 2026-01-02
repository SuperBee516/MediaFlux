using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Encode
{
    public partial class MainForm : Form
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

        private void AddToEncodeQueueFromContextMenu_Click(object? sender, EventArgs e)
        {
            if (!_encodingActive || _activeEncodeQueue == null)
            {
                MessageBox.Show(
                    "No encode is currently running. Start encoding first, then use \"Add to queue\" while it is in progress.",
                    "Add to queue",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                MessageBox.Show(
                    "Select one or more files in the list first.",
                    "Add to queue",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            int added = 0;

            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow || row.DataGridView == null)
                    continue;

                // Don’t enqueue the same row twice
                if (_activeEncodeQueue.Contains(row))
                    continue;

                _activeEncodeQueue.Add(row);
                added++;
            }

            if (added > 0)
            {
                toolStripStatusLabel1.Text =
                    $"Added {added} file(s) to the in-progress encode queue.";

                int totalNow = _activeEncodeQueue.Count;
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
        }

        private void OpenLocationFromContextMenu_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
                return;

            var row = dgvEncodeQueue.SelectedRows[0];

            string? path = null;

            // Preferred: RowMeta
            if (row.Tag is RowMeta meta)
                path = meta.Path;
            else
                path = row.Cells["colPath"]?.Value as string
                       ?? row.Cells["colSource"]?.Value as string;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
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
    }
}
