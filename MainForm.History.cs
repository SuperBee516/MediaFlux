using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Encode.Services;

namespace Encode
{
    public partial class MainForm : Form
    {
        private void ViewHistoryToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            // Load records from the new persistent store (data/history.json)
            var records = _historyService.LoadAll();

            // Build a viewer form with a split: grid (top) + log (bottom)
            var frm = new Form
            {
                Text = "Job History",
                StartPosition = FormStartPosition.CenterParent,
                Width = 1100,
                Height = 560
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 300
            };

            // Top panel: toolbar + grid
            var topPanel = new Panel { Dock = DockStyle.Fill };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(6, 6, 6, 0)
            };

            var btnRefresh = new Button { Text = "Refresh", Width = 90 };
            var btnRequeue = new Button { Text = "Requeue", Width = 100 };
            var btnOpenSrc = new Button { Text = "Open Source", Width = 120 };
            var btnOpenOut = new Button { Text = "Open Output", Width = 120 };
            var btnDelete = new Button { Text = "Delete Selected", Width = 140 };
            var btnClearAll = new Button { Text = "Clear All", Width = 100 };
            var btnClose = new Button { Text = "Close", Width = 90 };

            bar.Controls.AddRange(new Control[] { btnRefresh, btnRequeue, btnOpenSrc, btnOpenOut, btnDelete, btnClearAll, btnClose });

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoGenerateColumns = false,
                MultiSelect = true
            };

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colWhen", HeaderText = "Finished (Local)", Width = 160 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colType", HeaderText = "Type", Width = 80 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colStatus", HeaderText = "Status", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colSource", HeaderText = "Source", Width = 320 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colOutput", HeaderText = "Output", Width = 320 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colNotes", HeaderText = "Notes", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            topPanel.Controls.Add(grid);
            topPanel.Controls.Add(bar);
            split.Panel1.Controls.Add(topPanel);

            // Bottom panel: log viewer
            var txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false
            };
            split.Panel2.Controls.Add(txtLog);

            frm.Controls.Add(split);

            // ----- local helpers -----
            void LoadGrid()
            {
                var rows = _historyService.LoadAll();
                grid.Rows.Clear();
                foreach (var r in rows)
                {
                    int idx = grid.Rows.Add();
                    var row = grid.Rows[idx];
                    row.Tag = r;
                    row.Cells["colWhen"].Value = r.EndUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    row.Cells["colType"].Value = r.Type.ToString();
                    row.Cells["colStatus"].Value = r.Status.ToString();
                    row.Cells["colSource"].Value = r.SourcePath;
                    row.Cells["colOutput"].Value = r.OutputPath;
                    row.Cells["colNotes"].Value = r.Notes ?? "";

                    if (r.Status != JobStatus.Success)
                    {
                        row.DefaultCellStyle.BackColor = Color.MistyRose;
                        row.DefaultCellStyle.SelectionBackColor = Color.MistyRose;
                        row.DefaultCellStyle.SelectionForeColor = Color.Black;
                    }
                }
                txtLog.Clear();
            }

            void ShowSelectedLog()
            {
                if (grid.SelectedRows.Count == 0) { txtLog.Clear(); return; }
                if (grid.SelectedRows[0].Tag is JobHistoryRecord r)
                    txtLog.Text = r.Log ?? "";
                else
                    txtLog.Clear();
            }

            // ----- wire events -----
            grid.SelectionChanged += (_, __) => ShowSelectedLog();
            btnRefresh.Click += (_, __) => LoadGrid();

            btnRequeue.Click += (_, __) =>
            {
                if (grid.SelectedRows.Count == 0) return;
                int added = 0;
                foreach (DataGridViewRow gr in grid.SelectedRows)
                {
                    if (gr.Tag is JobHistoryRecord rec)
                    {
                        var src = rec.SourcePath;
                        if (!string.IsNullOrWhiteSpace(src) && File.Exists(src))
                        {
                            if (AddEncodeItemIfNotPresent(src)) added++;
                        }
                    }
                }
                if (added > 0)
                {
                    MessageBox.Show($"Added {added} file(s) to the Encode queue.", "Requeue",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    // optional: refresh estimated sizes after adding
                    btnRefreshEncode_Click(null, EventArgs.Empty);
                }
            };

            btnOpenSrc.Click += (_, __) =>
            {
                if (grid.SelectedRows.Count == 0) return;
                if (grid.SelectedRows[0].Tag is JobHistoryRecord r)
                {
                    var dir = Path.GetDirectoryName(r.SourcePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        Process.Start("explorer.exe", dir);
                }
            };

            btnOpenOut.Click += (_, __) =>
            {
                if (grid.SelectedRows.Count == 0) return;
                if (grid.SelectedRows[0].Tag is JobHistoryRecord r)
                {
                    var dir = Path.GetDirectoryName(r.OutputPath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        Process.Start("explorer.exe", dir);
                }
            };

            btnDelete.Click += (_, __) =>
            {
                if (grid.SelectedRows.Count == 0) return;
                var ids = new List<string>();
                foreach (DataGridViewRow gr in grid.SelectedRows)
                    if (gr.Tag is JobHistoryRecord r) ids.Add(r.Id);

                if (ids.Count == 0) return;

                var ok = MessageBox.Show($"Delete {ids.Count} selected entr{(ids.Count == 1 ? "y" : "ies")}?",
                    "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ok != DialogResult.Yes) return;

                _historyService.DeleteByIds(ids);
                LoadGrid();
            };

            btnClearAll.Click += (_, __) =>
            {
                var ok = MessageBox.Show("Clear ALL history?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ok != DialogResult.Yes) return;

                _historyService.Clear();
                LoadGrid();
            };

            btnClose.Click += (_, __) => frm.Close();

            // Initial load and show
            LoadGrid();
            frm.ShowDialog(this);
        }

        private void LoadHistoryGrid()
        {
            var list = _historyService.LoadAll();
            dgvHistory.Rows.Clear();

            foreach (var r in list)
            {
                int idx = dgvHistory.Rows.Add();
                var row = dgvHistory.Rows[idx];
                row.Tag = r; // keep full record on the row
                row.Cells["colH_When"].Value = r.EndUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                row.Cells["colH_Type"].Value = r.Type.ToString();
                row.Cells["colH_Status"].Value = r.Status.ToString();
                row.Cells["colH_Source"].Value = r.SourcePath;
                row.Cells["colH_Output"].Value = r.OutputPath;
                row.Cells["colH_Notes"].Value = string.IsNullOrWhiteSpace(r.Notes) ? "" : r.Notes;

                // Optional: color failed rows
                if (r.Status != Encode.Services.JobStatus.Success)
                {
                    row.DefaultCellStyle.BackColor = Color.MistyRose;
                    row.DefaultCellStyle.SelectionBackColor = Color.MistyRose;
                    row.DefaultCellStyle.SelectionForeColor = Color.Black;
                }
            }
        }

        private void dgvHistory_SelectionChanged(object sender, EventArgs e)
        {
            if (dgvHistory.SelectedRows.Count == 0) { txtHistoryLog.Text = ""; return; }
            var rec = dgvHistory.SelectedRows[0].Tag as Encode.Services.JobHistoryRecord;
            txtHistoryLog.Text = rec?.Log ?? "";
        }

        // Toolbar buttons
        private void btnHistoryRefresh_Click(object sender, EventArgs e) => LoadHistoryGrid();

        private void btnHistoryRequeue_Click(object sender, EventArgs e)
        {
            if (dgvHistory.SelectedRows.Count == 0) return;
            int added = 0;
            foreach (DataGridViewRow row in dgvHistory.SelectedRows)
            {
                if (row.Tag is Encode.Services.JobHistoryRecord rec)
                {
                    var path = rec.SourcePath;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        if (AddEncodeItemIfNotPresent(path)) added++;
                    }
                }
            }
            if (added > 0)
            {
                MessageBox.Show($"Added {added} file(s) to the Encode queue.", "Requeue",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                SafeRefreshEstimates();
                SwitchToEncodeTab();
            }
        }

        private void btnHistoryOpenSrc_Click(object sender, EventArgs e)
        {
            if (dgvHistory.SelectedRows.Count == 0) return;
            var rec = dgvHistory.SelectedRows[0].Tag as Encode.Services.JobHistoryRecord;
            var p = rec?.SourcePath;
            if (string.IsNullOrWhiteSpace(p)) return;
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
        }

        private void btnHistoryOpenOut_Click(object sender, EventArgs e)
        {
            if (dgvHistory.SelectedRows.Count == 0) return;
            var rec = dgvHistory.SelectedRows[0].Tag as Encode.Services.JobHistoryRecord;
            var p = rec?.OutputPath;
            if (string.IsNullOrWhiteSpace(p)) return;
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
        }

        private void btnHistoryDelete_Click(object sender, EventArgs e)
        {
            if (dgvHistory.SelectedRows.Count == 0) return;
            var ids = new List<string>();
            foreach (DataGridViewRow row in dgvHistory.SelectedRows)
                if (row.Tag is Encode.Services.JobHistoryRecord rec)
                    ids.Add(rec.Id);

            if (ids.Count == 0) return;

            var ok = MessageBox.Show($"Delete {ids.Count} selected entr{(ids.Count == 1 ? "y" : "ies")}?",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ok != DialogResult.Yes) return;

            _historyService.DeleteByIds(ids);
            LoadHistoryGrid();
        }

        private void btnHistoryClearAll_Click(object sender, EventArgs e)
        {
            var ok = MessageBox.Show("Clear ALL history?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ok != DialogResult.Yes) return;
            _historyService.Clear();
            LoadHistoryGrid();
        }



    }
}
