using MediaFlux.Models;

namespace MediaFlux;

internal sealed class PredictionShadowExperimentAssignmentForm : Form
{
    private readonly TextBox _experimentId = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _slot = new() { Minimum = 1, Maximum = int.MaxValue, Value = 1, Dock = DockStyle.Left, Width = 150 };
    private readonly NumericUpDown _attempt = new() { Minimum = 1, Maximum = int.MaxValue, Value = 1, Dock = DockStyle.Left, Width = 150 };
    private readonly ComboBox _stratum = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 180 };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 180 };

    public PredictionShadowExperimentAssignment? Assignment { get; private set; }

    public PredictionShadowExperimentAssignmentForm(PredictionShadowExperimentAssignment? current)
    {
        Text = "Research Experiment Assignment";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 300);

        _stratum.Items.AddRange(Enum.GetValues<PredictionShadowExperimentStratum>().Cast<object>().ToArray());
        _role.Items.AddRange(Enum.GetValues<PredictionShadowExperimentRole>().Cast<object>().ToArray());
        _stratum.SelectedItem = current?.Stratum ?? PredictionShadowExperimentStratum.Low;
        _role.SelectedItem = current?.Role ?? PredictionShadowExperimentRole.Target;
        if (current is not null)
        {
            _experimentId.Text = current.ExperimentId;
            _slot.Value = Math.Clamp(current.Slot, (int)_slot.Minimum, (int)_slot.Maximum);
            _attempt.Value = Math.Clamp(current.Attempt, (int)_attempt.Minimum, (int)_attempt.Maximum);
        }

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(12),
            AutoSize = false
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        AddField(fields, 0, "Experiment ID", _experimentId);
        AddField(fields, 1, "Slot", _slot);
        AddField(fields, 2, "Attempt", _attempt);
        AddField(fields, 3, "Stratum", _stratum);
        AddField(fields, 4, "Role", _role);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        var save = new Button { Text = "Assign", Width = 92, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", Width = 92, DialogResult = DialogResult.Cancel };
        save.Click += Save_Click;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        fields.Controls.Add(buttons, 0, 5);
        fields.SetColumnSpan(buttons, 2);
        Controls.Add(fields);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_experimentId.Text) ||
            _stratum.SelectedItem is not PredictionShadowExperimentStratum stratum ||
            _role.SelectedItem is not PredictionShadowExperimentRole role)
        {
            MessageBox.Show(this, "Enter an experiment ID and choose a valid stratum and role.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var assignment = new PredictionShadowExperimentAssignment(
            _experimentId.Text.Trim(), (int)_slot.Value, (int)_attempt.Value, stratum, role);
        if (!assignment.IsValid())
        {
            MessageBox.Show(this, "The experiment assignment is invalid.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Assignment = assignment;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void AddField(TableLayoutPanel table, int row, string label, Control control)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        table.Controls.Add(control, 1, row);
    }
}
