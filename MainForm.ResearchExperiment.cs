using MediaFlux.Models;

namespace MediaFlux;

public partial class MainForm
{
    private void AssignResearchExperimentToSelectedRow_Click(object? sender, EventArgs e)
    {
        DataGridViewRow[] selectedRows = dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow).ToArray();
        if (_encodingActive || selectedRows.Length != 1)
            return;

        DataGridViewRow row = selectedRows[0];
        RowMeta meta = EnsureRowMeta(row);
        using var dialog = new PredictionShadowExperimentAssignmentForm(
            meta.PredictionShadowExperimentAssignment);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Assignment is not { } assignment)
            return;

        meta.PredictionShadowExperimentAssignment = assignment;
        UpdateResearchExperimentAssignmentPresentation(row);
        ShowStatusInfo($"Research assignment set: {FormatResearchExperimentAssignment(assignment)}");
    }

    private void ClearResearchExperimentFromSelectedRow_Click(object? sender, EventArgs e)
    {
        DataGridViewRow[] selectedRows = dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow).ToArray();
        if (_encodingActive || selectedRows.Length != 1 || selectedRows[0].Tag is not RowMeta meta)
            return;

        meta.PredictionShadowExperimentAssignment = null;
        UpdateResearchExperimentAssignmentPresentation(selectedRows[0]);
        ShowStatusInfo("Research experiment assignment cleared.");
    }

    private static string FormatResearchExperimentAssignment(PredictionShadowExperimentAssignment assignment) =>
        $"{assignment.ExperimentId} / Slot {assignment.Slot:00} / Attempt {assignment.Attempt} / {assignment.Stratum} / {assignment.Role}";

    private static void UpdateResearchExperimentAssignmentPresentation(DataGridViewRow row)
    {
        if (row.DataGridView is not { } grid || !grid.Columns.Contains("colName"))
            return;

        PredictionShadowExperimentAssignment? assignment = (row.Tag as RowMeta)?.PredictionShadowExperimentAssignment;
        row.Cells["colName"].ToolTipText = assignment is null
            ? string.Empty
            : "Research assignment: " + FormatResearchExperimentAssignment(assignment);
    }
}
