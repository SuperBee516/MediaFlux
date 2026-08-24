using MediaFlux.Models;

namespace MediaFlux;

internal sealed class JobManagerForm : MediaFluxForm
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false };
    private readonly Func<IReadOnlyList<EncodeJob>> _jobs; private readonly Action<EncodeJob, string> _action;
    public JobManagerForm(Func<IReadOnlyList<EncodeJob>> jobs, Action<EncodeJob, string> action)
    {
        _jobs = jobs; _action = action; Text = "Job Manager"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(900, 410);
        foreach (var header in new[] { "Name", "Files", "Schedule / Next Run", "Estimated Output", "Estimated Savings", "Status", "Last Run", "Enabled" }) _grid.Columns.Add(header, header);
        var menu = new ContextMenuStrip();
        foreach (var text in new[] { "Run Now", "Edit Job", "Edit Encode Settings", "Change Schedule", "View Files", "Load into Main Queue", "Enable / Disable", "Delete" }) menu.Items.Add(text, null, (_, __) => InvokeAction(text));
        _grid.ContextMenuStrip = menu; _grid.CellDoubleClick += (_, __) => InvokeAction("Edit Job"); Controls.Add(_grid); Shown += (_, __) => RefreshJobs(); Activated += (_, __) => RefreshJobs();
    }
    private void InvokeAction(string action) { if (_grid.CurrentRow?.Tag is EncodeJob job) { _action(job, action); RefreshJobs(); } }
    private void RefreshJobs()
    {
        _grid.Rows.Clear();
        foreach (var job in _jobs())
        {
            string schedule = job.ScheduleType == EncodeJobScheduleType.Once ? job.ScheduledLocalTime?.ToString("g") ?? "Scheduled" : "Manual";
            int index = _grid.Rows.Add(job.Name, job.Files.Count, schedule, Format(job.EstimatedOutputBytes), Format(job.EstimatedSavingsBytes), Display(job.Status), job.LastRunUtc?.ToLocalTime().ToString("g") ?? "--", job.Enabled ? "Yes" : "No");
            _grid.Rows[index].Tag = job;
            foreach (DataGridViewCell cell in _grid.Rows[index].Cells) cell.ToolTipText = job.LastResult;
        }
    }
    private static string Display(EncodeJobStatus status) => status == EncodeJobStatus.CompletedWithErrors ? "Completed with Errors" : status.ToString();
    private static string Format(long bytes) => bytes <= 0 ? "--" : $"{bytes / 1024d / 1024d:0.#} MB";
}
