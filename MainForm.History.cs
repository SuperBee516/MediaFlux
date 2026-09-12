using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private void ViewHistoryToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            try
            {
                using var form = new JobHistoryForm(_historyService, _config, _configPath, path => AddEncodeItemIfNotPresent(path), () => { SafeRefreshEstimates(); SwitchToEncodeTab(); });
                form.ShowDialog(this);
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(AppPaths.UserDataDirectory, "Open Job History failed", exception: ex);
                MessageBox.Show(this, "Job History could not be opened. See the MediaFlux error log for details.", "Job History", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadHistoryGrid()
        {
            var list = _historyService.LoadAll(); dgvHistory.Rows.Clear();
            foreach (var r in list)
            {
                int idx = dgvHistory.Rows.Add(); var row = dgvHistory.Rows[idx]; row.Tag = r;
                row.Cells["colH_When"].Value = r.EndUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); row.Cells["colH_Type"].Value = FormatHistoryJobType(r.Type); row.Cells["colH_Status"].Value = r.Status.ToString(); row.Cells["colH_Source"].Value = r.SourcePath; row.Cells["colH_Output"].Value = r.OutputPath; row.Cells["colH_Notes"].Value = string.IsNullOrWhiteSpace(r.Notes) ? "" : r.Notes;
            }
        }

        private static string FormatHistoryJobType(JobType type) => JobHistoryPresentation.FormatType(type);
        private void dgvHistory_SelectionChanged(object sender, EventArgs e) { if (dgvHistory.SelectedRows.Count == 0) { txtHistoryLog.Text = ""; return; } var rec = dgvHistory.SelectedRows[0].Tag as JobHistoryRecord; txtHistoryLog.Text = (rec?.Log ?? "") + (rec?.DiagnosticSummary == null ? "" : Environment.NewLine + Environment.NewLine + EncodingDiagnosticsService.FormatCompletedSummary(rec.DiagnosticSummary)); }
        private void btnHistoryRefresh_Click(object sender, EventArgs e) => LoadHistoryGrid();
        private void btnHistoryRequeue_Click(object sender, EventArgs e) { if (dgvHistory.SelectedRows.Count == 0) return; int added = 0; foreach (DataGridViewRow row in dgvHistory.SelectedRows) if (row.Tag is JobHistoryRecord rec && File.Exists(rec.SourcePath) && AddEncodeItemIfNotPresent(rec.SourcePath)) added++; if (added > 0) { MessageBox.Show($"Added {added} file(s) to the Encode queue.", "Requeue", MessageBoxButtons.OK, MessageBoxIcon.Information); SafeRefreshEstimates(); SwitchToEncodeTab(); } }
        private void btnHistoryOpenSrc_Click(object sender, EventArgs e) => OpenHistoryPath(true);
        private void btnHistoryOpenOut_Click(object sender, EventArgs e) => OpenHistoryPath(false);
        private void OpenHistoryPath(bool source) { if (dgvHistory.SelectedRows.Count == 0) return; var rec = dgvHistory.SelectedRows[0].Tag as JobHistoryRecord; var path = source ? rec?.SourcePath : rec?.OutputPath; if (string.IsNullOrWhiteSpace(path)) return; var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path); if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) Process.Start("explorer.exe", dir); }
        private void btnHistoryDelete_Click(object sender, EventArgs e) { var ids = dgvHistory.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag as JobHistoryRecord).Where(r => r != null).Select(r => r!.Id).ToArray(); if (ids.Length == 0 || MessageBox.Show($"Delete {ids.Length} selected entr{(ids.Length == 1 ? "y" : "ies")} ?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _historyService.DeleteByIds(ids); LoadHistoryGrid(); }
        private void btnHistoryClearAll_Click(object sender, EventArgs e) { if (MessageBox.Show("Clear ALL history?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _historyService.Clear(); LoadHistoryGrid(); }
    }
}
