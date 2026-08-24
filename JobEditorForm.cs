using MediaFlux.Models;

namespace MediaFlux;

internal sealed class JobEditorForm : MediaFluxForm
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _schedule = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DateTimePicker _when = new() { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", ShowUpDown = true };
    private readonly CheckBox _enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly TableLayoutPanel _grid;
    public EncodeJob Job { get; }

    public JobEditorForm(EncodeJob job)
    {
        Job = job;
        Text = "Job Editor"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(570, 340);
        _name.Text = job.Name;
        _schedule.Items.AddRange(new object[] { "Manual", "Scheduled Once" });
        _schedule.SelectedIndex = job.ScheduleType == EncodeJobScheduleType.Once ? 1 : 0;
        _when.Value = job.ScheduledLocalTime ?? DateTime.Now.AddHours(1);
        _enabled.Checked = job.Enabled;
        _schedule.SelectedIndexChanged += (_, __) => _when.Enabled = _schedule.SelectedIndex == 1;

        _grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 8 };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add("Job Name", _name); Add("File count", new Label { Text = job.Files.Count.ToString("N0"), AutoSize = true, Anchor = AnchorStyles.Left });
        Add("Schedule", _schedule); Add("Date / time", _when);
        Add("Job state", _enabled);
        Add("Output location", new Label { Text = string.IsNullOrWhiteSpace(job.Settings.OutputFolder) ? "Same as source" : job.Settings.OutputFolder, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        Add("Encode configuration", new Label { Text = $"{job.Settings.VideoCodec}, {job.Settings.CompressionProfile}, {job.Settings.OutputContainer}", AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        Add("Estimated output / savings", new Label { Text = $"{Format(job.EstimatedOutputBytes)} / {Format(job.EstimatedSavingsBytes)}", AutoSize = true, Anchor = AnchorStyles.Left });
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(cancel); buttons.Controls.Add(save); _grid.Controls.Add(buttons, 1, 8); _grid.RowCount = 9;
        Controls.Add(_grid); AcceptButton = save; CancelButton = cancel;
        FormClosing += (_, e) => { if (DialogResult != DialogResult.OK) return; if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show(this, "A job name is required.", Text); e.Cancel = true; return; } Job.Name = _name.Text.Trim(); Job.ScheduleType = _schedule.SelectedIndex == 1 ? EncodeJobScheduleType.Once : EncodeJobScheduleType.Manual; Job.ScheduledLocalTime = Job.ScheduleType == EncodeJobScheduleType.Once ? _when.Value : null; Job.Enabled = _enabled.Checked; Job.ModifiedUtc = DateTime.UtcNow; Services.EncodeJobService.RefreshStatus(Job); };
        _when.Enabled = _schedule.SelectedIndex == 1;
    }
    private void Add(string label, Control value) { int row = _rows++; _grid.Controls.Add(new Label { Text = label + ":", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); _grid.Controls.Add(value, 1, row); }
    private int _rows;
    private static string Format(long bytes) => bytes <= 0 ? "--" : $"{bytes / 1024d / 1024d:0.#} MB";
}
