using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux;

public partial class MainForm
{
    private Label? _encodingPlanStatusLabel;
    private TableLayoutPanel? _encodingPlanTable;
    private DataGridViewRow? _renderedIntelligenceRow;
    private EncodingIntelligencePresentation.PresentationKey? _renderedIntelligenceKey;
    private Guid? _renderedPlanId;
    private readonly Dictionary<string, Label> _dynamicIntelligenceValues = new(StringComparer.Ordinal);
    private Control CreateEncodingPlanGroup()
    {
        var group = new GroupBox
        {
            Text = "Encoding Plan",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 10),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = Padding.Empty,
            BackColor = Color.White
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _encodingPlanStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8),
            Text = "Select a queue item to view Encoding Intelligence."
        };
        content.Controls.Add(_encodingPlanStatusLabel, 0, 0);

        _encodingPlanTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        _encodingPlanTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.Controls.Add(_encodingPlanTable, 0, 1);

        group.Controls.Add(content);
        return group;
    }

    private void ScheduleEncodingPlanRefresh()
    {
        if (_encodingPlanTable == null || IsDisposed)
            return;
        DataGridViewRow[] rows = dgvEncodeQueue.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .ToArray();
        if (rows.Length != 1)
        {
            RenderEncodingPlanStatus(rows.Length == 0
                ? "Select a queue item to view Encoding Intelligence."
                : "Select one queue item at a time to view Encoding Intelligence.");
            return;
        }

        RowMeta meta = EnsureRowMeta(rows[0]);
        if (meta.IntelligencePlan == null)
        {
            if (ReferenceEquals(_renderedIntelligenceRow, rows[0]) && _renderedIntelligenceKey != null)
                return;
            RenderEncodingPlanStatus("Encoding Intelligence will appear when the existing encode preflight publishes its plan.");
            return;
        }
        RenderEncodingPlan(meta.IntelligencePlan, meta.IntelligenceOutcome, rows[0]);
    }

    private void RenderEncodingPlanStatus(string text)
    {
        if (_renderedIntelligenceRow == null && _renderedPlanId == null &&
            string.Equals(_encodingPlanStatusLabel?.Text, text, StringComparison.Ordinal))
            return;
        if (_encodingPlanStatusLabel != null)
            _encodingPlanStatusLabel.Text = text;
        _renderedIntelligenceRow = null;
        _renderedIntelligenceKey = null;
        _renderedPlanId = null;
        _dynamicIntelligenceValues.Clear();
        ClearEncodingPlanRows();
    }

    private void RenderEncodingPlanUnavailable(string text)
    {
        RenderEncodingPlanStatus(text);
    }

    private void RefreshCurrentEncodingIntelligence(DataGridViewRow row, RowMeta meta)
    {
        if (IsDisposed || meta.IntelligencePlan == null ||
            !dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>().Contains(row))
            return;
        RenderEncodingPlan(meta.IntelligencePlan, meta.IntelligenceOutcome, row);
    }

    private void RenderEncodingPlan(EncodingPlan plan, EncodingExecutionOutcome? outcome = null, DataGridViewRow? row = null)
    {
        if (_encodingPlanStatusLabel == null || _encodingPlanTable == null)
            return;

        EncodingIntelligencePresentation.PresentationKey key =
            EncodingIntelligencePresentation.GetKey(plan, outcome);
        if (ReferenceEquals(_renderedIntelligenceRow, row) && _renderedPlanId == plan.PlanId &&
            _renderedIntelligenceKey == key)
            return;

        if (ReferenceEquals(_renderedIntelligenceRow, row) && _renderedPlanId == plan.PlanId)
        {
            UpdateDynamicEncodingIntelligence(plan, outcome);
            _renderedIntelligenceKey = key;
            return;
        }

        _renderedIntelligenceRow = row;
        _renderedIntelligenceKey = key;
        _renderedPlanId = plan.PlanId;
        _dynamicIntelligenceValues.Clear();

        string status = plan.IsAvailable
            ? "Planned settings are authoritative. Historical estimates are advisory and never change the encode."
            : plan.UnavailableReason;
        if (!string.Equals(_encodingPlanStatusLabel.Text, status, StringComparison.Ordinal))
            _encodingPlanStatusLabel.Text = status;
        ClearEncodingPlanRows();

        if (!plan.IsAvailable)
            return;

        EncodingIntelligencePresentation.Model presentation = EncodingIntelligencePresentation.Create(plan, outcome);
        AddEncodingPlanSection("Encoding intelligence", presentation.Summary);
        if (presentation.Reasons.Count > 0)
            AddEncodingPlanSection("Why this plan?", presentation.Reasons);
        if (presentation.Technical.Count > 0)
            AddEncodingPlanSection("Technical details", presentation.Technical);

        foreach (EncodingPlanSection section in plan.Sections)
            AddEncodingPlanSection(section.Title, section.Items);

        if (presentation.Summary.Count == 0 && plan.Sections.Count == 0)
        {
            int fallbackRow = _encodingPlanTable.RowCount++;
            _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _encodingPlanTable.Controls.Add(
                new Label
                {
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Text = "No additional resolved processing or stream changes are available for this item."
                },
                0,
                fallbackRow);
        }
    }

    private void UpdateDynamicEncodingIntelligence(EncodingPlan plan, EncodingExecutionOutcome? outcome)
    {
        EncodingIntelligencePresentation.Model presentation = EncodingIntelligencePresentation.Create(plan, outcome);
        foreach (EncodingPlanItem item in presentation.Summary.Concat(presentation.Technical))
        {
            if (_dynamicIntelligenceValues.TryGetValue(item.Label, out Label? label) &&
                !string.Equals(label.Text, item.Value, StringComparison.Ordinal))
                label.Text = item.Value;
        }
    }

    private void AddEncodingPlanSection(string title, IReadOnlyList<EncodingPlanItem> items)
    {
        if (_encodingPlanTable == null || items.Count == 0)
            return;

        int sectionRow = _encodingPlanTable.RowCount++;
        _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = CreateInfoCaption(title.ToUpperInvariant());
        header.ForeColor = Color.FromArgb(31, 88, 166);
        header.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
        header.Margin = new Padding(0, sectionRow == 0 ? 1 : 8, 0, 3);
        _encodingPlanTable.Controls.Add(header, 0, sectionRow);
        foreach (EncodingPlanItem item in items)
            AddEncodingPlanItem(item);
    }

    private void AddEncodingPlanItem(EncodingPlanItem item)
    {
        if (_encodingPlanTable == null)
            return;

        int row = _encodingPlanTable.RowCount++;
        _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = string.IsNullOrWhiteSpace(item.Reason) ? 1 : 2,
            Margin = new Padding(0, 0, 0, 3),
            Padding = new Padding(7, 3, 7, 3),
            BackColor = Color.FromArgb(248, 249, 251)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (!string.IsNullOrWhiteSpace(item.Reason))
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = CreateInfoCaption(item.Label.ToUpperInvariant());
        label.Anchor = AnchorStyles.Left;
        panel.Controls.Add(label, 0, 0);

        var value = CreateInfoValue(item.Value, bold: true);
        value.AutoSize = true;
        value.MaximumSize = new Size(760, 0);
        panel.Controls.Add(value, 1, 0);
        if (item.Label is "Recovery" or "Lifecycle" or "Terminal result" or "Validation" or "Finalization")
            _dynamicIntelligenceValues[item.Label] = value;

        if (!string.IsNullOrWhiteSpace(item.Reason))
        {
            var reason = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                ForeColor = SystemColors.GrayText,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic),
                Text = $"Reason: {item.Reason}",
                Margin = new Padding(0, 1, 0, 0)
            };
            panel.Controls.Add(reason, 1, 1);
        }

        _encodingPlanTable.Controls.Add(panel, 0, row);
    }

    private void ClearEncodingPlanRows()
    {
        if (_encodingPlanTable == null)
            return;

        _encodingPlanTable.SuspendLayout();
        try
        {
            _encodingPlanTable.Controls.Clear();
            _encodingPlanTable.RowStyles.Clear();
            _encodingPlanTable.RowCount = 0;
        }
        finally
        {
            _encodingPlanTable.ResumeLayout(true);
        }
    }
}
