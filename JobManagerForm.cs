using MediaFlux.Models;

namespace MediaFlux;

internal sealed class JobManagerForm : MediaFluxForm
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false };
    private readonly Func<IReadOnlyList<EncodeJob>> _jobs; private readonly Action<Guid, string> _action;
    private Guid? _contextJobId;
    public JobManagerForm(Func<IReadOnlyList<EncodeJob>> jobs, Action<Guid, string> action)
    {
        _jobs = jobs; _action = action; Text = "Job Manager"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(900, 410);
        foreach (var header in new[] { "Name", "Files", "Schedule / Next Run", "Estimated Output", "Estimated Savings", "Status", "Last Run", "Enabled" }) _grid.Columns.Add(header, header);
        var menu = new ContextMenuStrip();
        foreach (var text in new[] { "Run Now", "Edit Job", "Edit Encode Settings", "Change Schedule", "View Files", "Load into Main Queue", "Enable / Disable", "Delete" }) menu.Items.Add(text, null, (_, __) => InvokeAction(text));
        menu.Opening += (_, e) =>
        {
            if (_contextJobId == null && _grid.CurrentRow?.Tag is Guid currentId)
                _contextJobId = currentId;
            e.Cancel = _contextJobId == null;
        };
        _grid.ContextMenuStrip = menu;
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                return;
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
            _contextJobId = _grid.Rows[e.RowIndex].Tag as Guid?;
        };
        _grid.CellDoubleClick += (_, __) => InvokeAction("Edit Job"); Controls.Add(_grid); Shown += (_, __) => RefreshJobs(); Activated += (_, __) => RefreshJobs();
    }
    private void InvokeAction(string action)
    {
        Guid? jobId = _contextJobId ?? (_grid.CurrentRow?.Tag as Guid?);
        _contextJobId = null;
        if (jobId.HasValue) { _action(jobId.Value, action); RefreshJobs(); }
    }
    private void RefreshJobs()
    {
        _grid.Rows.Clear();
        foreach (var job in _jobs())
        {
            string schedule = job.ScheduleType == EncodeJobScheduleType.Once ? job.ScheduledLocalTime?.ToString("g") ?? "Scheduled" : "Manual";
            int index = _grid.Rows.Add(job.Name, job.Files.Count, schedule, Format(job.EstimatedOutputBytes), Format(job.EstimatedSavingsBytes), Display(job.Status), job.LastRunUtc?.ToLocalTime().ToString("g") ?? "--", job.Enabled ? "Yes" : "No");
            _grid.Rows[index].Tag = job.Id;
            foreach (DataGridViewCell cell in _grid.Rows[index].Cells) cell.ToolTipText = job.LastResult;
        }
    }
    private static string Display(EncodeJobStatus status) => status == EncodeJobStatus.CompletedWithErrors ? "Completed with Errors" : status.ToString();
    private static string Format(long bytes) => bytes <= 0 ? "--" : $"{bytes / 1024d / 1024d:0.#} MB";
}
