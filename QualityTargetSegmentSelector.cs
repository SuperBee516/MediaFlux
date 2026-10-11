using System.Drawing;
using System.Windows.Forms;
using MediaFlux.Models;

namespace MediaFlux;

internal sealed class QualityTargetSegmentSelector : UserControl
{
    internal sealed record Choice(
        QualityTarget Target,
        string Label,
        string AccessibleName,
        string Description);

    private static readonly Choice[] s_choices =
    [
        new(QualityTarget.SmallerFile, "Smaller", "Smaller File",
            "Prioritizes storage savings. More compression may reduce fine detail."),
        new(QualityTarget.Efficient, "Efficient", "Efficient",
            "Favors smaller files while maintaining good visual quality."),
        new(QualityTarget.Balanced, "Balanced", "Balanced",
            "Recommended for most videos. Balances visual quality and storage efficiency."),
        new(QualityTarget.HighQuality, "High", "High Quality",
            "Preserves additional source detail at the cost of larger output files."),
        new(QualityTarget.MaximumQuality, "Maximum", "Maximum Quality",
            "Prioritizes preservation of source quality. File size is secondary.")
    ];

    private readonly RadioButton[] _segments;
    private readonly ToolTip _toolTip;
    private QualityTarget _selectedTarget;

    internal static IReadOnlyList<Choice> Choices { get; } = Array.AsReadOnly(s_choices);

    internal event EventHandler? SelectedTargetChanged;

    internal QualityTarget SelectedTarget => _selectedTarget;

    internal IReadOnlyList<RadioButton> Segments => _segments;

    internal QualityTargetSegmentSelector(ToolTip toolTip, QualityTarget initialTarget = QualityTarget.Balanced)
    {
        _toolTip = toolTip;
        _selectedTarget = initialTarget;
        Name = "qualityTargetSegmentSelector";
        AccessibleName = "Automatic quality preference";
        AccessibleDescription = "Choose one of five automatic quality preferences, from smaller files to maximum quality.";
        AccessibleRole = AccessibleRole.Grouping;
        TabStop = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        MinimumSize = new Size(0, Scale(30));

        var layout = new TableLayoutPanel
        {
            Name = "qualityTargetSegmentLayout",
            AutoSize = false,
            ColumnCount = s_choices.Length,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false,
            AccessibleName = "Quality preferences"
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        for (int index = 0; index < s_choices.Length; index++)
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / s_choices.Length));

        _segments = s_choices.Select((choice, index) => CreateSegment(choice, index)).ToArray();
        layout.Controls.AddRange(_segments);
        Controls.Add(layout);
        _segments[IndexOf(initialTarget)].Checked = true;
        UpdateTabStops();
    }

    internal RadioButton GetSegment(QualityTarget target) => _segments[IndexOf(target)];

    internal void SetSelectedTarget(QualityTarget target)
    {
        _segments[IndexOf(target)].Checked = true;
    }

    internal static int IndexOf(QualityTarget target)
    {
        int index = Array.FindIndex(s_choices, choice => choice.Target == target);
        return index >= 0 ? index : Array.FindIndex(s_choices, choice => choice.Target == QualityTarget.Balanced);
    }

    internal static QualityTarget AtIndex(int index) => s_choices[Math.Clamp(index, 0, s_choices.Length - 1)].Target;

    internal static string FullName(QualityTarget target) => s_choices[IndexOf(target)].AccessibleName;

    internal static string Description(QualityTarget target) => s_choices[IndexOf(target)].Description;

    private RadioButton CreateSegment(Choice choice, int index)
    {
        var segment = new RadioButton
        {
            Name = $"qualityTarget{choice.Target}",
            Appearance = Appearance.Button,
            AutoCheck = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Standard,
            Margin = new Padding(1),
            TabIndex = index,
            TabStop = choice.Target == _selectedTarget,
            Text = choice.Label,
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = choice.AccessibleName,
            AccessibleDescription = $"{choice.AccessibleName}. {choice.Description}",
            AccessibleDefaultActionDescription = $"Select {choice.AccessibleName}",
            AccessibleRole = AccessibleRole.RadioButton
        };
        segment.CheckedChanged += (_, __) => OnSegmentChecked(segment, choice.Target);
        segment.KeyDown += (_, e) => HandleSegmentKeyDown(segment, choice.Target, e);
        _toolTip.SetToolTip(segment, $"{choice.AccessibleName}: {choice.Description}");
        return segment;
    }

    private void OnSegmentChecked(RadioButton segment, QualityTarget target)
    {
        if (!segment.Checked || _selectedTarget == target)
            return;

        _selectedTarget = target;
        UpdateTabStops();
        SelectedTargetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleSegmentKeyDown(RadioButton focused, QualityTarget target, KeyEventArgs e)
    {
        int index = IndexOf(target);
        int nextIndex = e.KeyCode switch
        {
            Keys.Left => Math.Max(0, index - 1),
            Keys.Right => Math.Min(_segments.Length - 1, index + 1),
            Keys.Home => 0,
            Keys.End => _segments.Length - 1,
            Keys.Enter => index,
            _ => -1
        };
        if (nextIndex < 0)
            return;

        e.Handled = true;
        e.SuppressKeyPress = true;
        _segments[nextIndex].Checked = true;
        _segments[nextIndex].Focus();
    }

    private void UpdateTabStops()
    {
        foreach (RadioButton segment in _segments)
            segment.TabStop = segment.Checked;
    }

    private int Scale(int pixels) => (int)Math.Round(pixels * (DeviceDpi / 96d));
}
