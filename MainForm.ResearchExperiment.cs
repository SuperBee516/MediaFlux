using MediaFlux.Models;
using MediaFlux.Services;

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
        if (meta.PredictionShadowExperimentAssignment is { } existingBinding &&
            !PredictionShadowExperimentAssignmentPersistence.MatchesSource(existingBinding, meta.Path))
        {
            ShowStatusInfo("The source changed after assignment. Clear the stale assignment before assigning this source again.");
            return;
        }
        using var dialog = new PredictionShadowExperimentAssignmentForm(
            meta.PredictionShadowExperimentAssignment?.Assignment);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Assignment is not { } assignment)
            return;

        if (!_predictionShadowService.CanRegisterExperimentAssignment(assignment))
        {
            ShowStatusInfo("Research assignment was rejected: the journal history is incomplete/conflicting or this is not the next valid target/replacement attempt.");
            return;
        }

        meta.PredictionShadowExperimentAssignment =
            PredictionShadowExperimentAssignmentPersistence.Capture(meta.Path, assignment);
        if (meta.PredictionShadowExperimentAssignment is null)
        {
            ShowStatusInfo("The assignment was not saved because the source file is unavailable or changed.");
            return;
        }
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

        PredictionShadowExperimentAssignment? assignment =
            (row.Tag as RowMeta)?.PredictionShadowExperimentAssignment?.Assignment;
        row.Cells["colName"].ToolTipText = assignment is null
            ? string.Empty
            : "Research assignment: " + FormatResearchExperimentAssignment(assignment);
    }
}
