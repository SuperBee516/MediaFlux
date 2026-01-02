using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Encode
{
    public partial class MainForm : Form
    {
        // =====================
        // Export/Import DTOs
        // =====================
        private sealed class QueueSnapshot
        {
            public string Version { get; set; } = "1.0";
            public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
            public QueueSettings Settings { get; set; } = new QueueSettings();
            public List<QueueItem> Items { get; set; } = new List<QueueItem>();
        }

        private sealed class QueueSettings
        {
            public bool AutoTargetSize { get; set; }
            public double? ManualTargetMb { get; set; }  // null when auto
            public string CompressionProfile { get; set; } = "";
            public string EncoderMode { get; set; } = "";        // "GPU (NVENC)" or "CPU (libx264)"
            public string OutputFolder { get; set; } = "";       // cmbEncodeOutput.Text
        }

        private sealed class QueueItem
        {
            public string Path { get; set; } = "";
        }

        // Centralized JSON options (pretty for readability)
        private static readonly JsonSerializerOptions _queueJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Build a settings snapshot from current UI
        private QueueSettings CaptureCurrentQueueSettings()
        {
            double? manualMb = null;
            if (!chkAutoTargetSize.Checked && double.TryParse(txtTargetSize.Text, out var mb) && mb > 0)
                manualMb = mb;

            return new QueueSettings
            {
                AutoTargetSize = chkAutoTargetSize.Checked,
                ManualTargetMb = manualMb,
                CompressionProfile = comboCompressionProfile.SelectedItem?.ToString() ?? "",
                EncoderMode = comboEncoderMode.SelectedItem?.ToString() ?? "",
                OutputFolder = cmbEncodeOutput.Text ?? ""
            };
        }

        // Resolve full file path from a row (supports Tag = RowMeta or string)
        private string? GetPathFromRow(DataGridViewRow row)
        {
            if (row.Tag is RowMeta rm) return rm.Path;
            if (row.Tag is string s) return s;
            return null;
        }

        // FILE MENU: Export Queue…
        private void exportQueueToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.Rows.Count == 0)
            {
                MessageBox.Show("There are no items in the Encode queue to export.",
                    "Export Queue", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var items = new List<QueueItem>();
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                var path = GetPathFromRow(row);
                if (!string.IsNullOrWhiteSpace(path))
                    items.Add(new QueueItem { Path = path });
            }

            if (items.Count == 0)
            {
                MessageBox.Show("No valid items to export.", "Export Queue",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Title = "Export Queue",
                Filter = "Encode Queue (*.cequeue)|*.cequeue|JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"queue_{DateTime.Now:yyyyMMdd_HHmm}.cequeue",
                OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            var snapshot = new QueueSnapshot
            {
                Settings = CaptureCurrentQueueSettings(),
                Items = items
            };

            try
            {
                var json = JsonSerializer.Serialize(snapshot, _queueJsonOptions);
                File.WriteAllText(sfd.FileName, json);
                toolStripStatusLabel1.Text = $"Exported {items.Count} item(s) to {Path.GetFileName(sfd.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Export Queue",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // FILE MENU: Import Queue…        
        private void importQueueToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Import Queue",
                Filter = "Encode Queue (*.cequeue;*.json)|*.cequeue;*.json|All files (*.*)|*.*",
                Multiselect = false
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var json = File.ReadAllText(ofd.FileName);
                var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(json, _queueJsonOptions);
                if (snapshot == null)
                {
                    MessageBox.Show("Invalid or empty queue file.", "Import Queue",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Ask: Append or Replace?
                var choice = MessageBox.Show(
                    "Do you want to APPEND these items to your current queue?\n\n" +
                    "Click Yes to Append, No to Replace the current queue.",
                    "Import Queue",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (choice == DialogResult.Cancel) return;

                if (choice == DialogResult.No)
                {

                    dgvEncodeQueue.Rows.Clear();
                }

                int added = 0, missing = 0;
                foreach (var qi in snapshot.Items)
                {
                    if (string.IsNullOrWhiteSpace(qi.Path))
                        continue;

                    if (File.Exists(qi.Path))
                    {
                        if (AddEncodeItemIfNotPresent(qi.Path))
                            added++;
                    }
                    else
                    {
                        missing++;
                    }
                }

                // Optionally apply snapshot settings to UI (non-destructive)
                ApplySnapshotSettingsToUi(snapshot.Settings);

                toolStripStatusLabel1.Text =
                    $"Imported {added} item(s){(missing > 0 ? $", {missing} missing file(s) skipped" : "")} from {Path.GetFileName(ofd.FileName)}";

                // Auto-refresh estimates once imported
                SafeRefreshEstimates();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Import Queue",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
