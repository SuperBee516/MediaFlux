namespace MediaFlux;

internal class AnalyzerMetricCard : Panel
{
    private readonly Label _value = new()
    {
        Dock = DockStyle.Top,
        Height = 32,
        Font = new Font("Segoe UI Semibold", 16F),
        Padding = new Padding(9, 4, 8, 0),
        AutoEllipsis = true
    };

    private readonly Label _secondary = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = SystemColors.GrayText,
        Padding = new Padding(9, 0, 8, 5),
        AutoEllipsis = true
    };

    public AnalyzerMetricCard(string title)
    {
        AccessibleName = title;
        AccessibleRole = AccessibleRole.Grouping;
        TabStop = false;
        BorderStyle = BorderStyle.FixedSingle;
        Margin = new Padding(3);
        Padding = new Padding(1);
        BackColor = SystemColors.Window;
        Controls.Add(_secondary);
        Controls.Add(_value);
        Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 21,
            ForeColor = Color.FromArgb(70, 70, 70),
            Padding = new Padding(8, 3, 4, 0),
            AutoEllipsis = true,
            AccessibleName = title,
            AccessibleRole = AccessibleRole.Text
        });
    }

    public void SetValue(string value, string secondary)
    {
        _value.Text = value;
        _secondary.Text = secondary;
    }
}

internal sealed class AnalyzerSectionPanel : Panel
{
    public AnalyzerSectionPanel(string title, Control content)
    {
        AccessibleName = title;
        AccessibleRole = AccessibleRole.Grouping;
        TabStop = false;
        BorderStyle = BorderStyle.FixedSingle;
        Padding = new Padding(8, 6, 8, 8);
        Margin = new Padding(3);
        BackColor = SystemColors.Window;

        var heading = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI Semibold", 9F),
            ForeColor = Color.FromArgb(0, 92, 160),
            AutoEllipsis = true,
            AccessibleName = title,
            AccessibleRole = AccessibleRole.Text
        };
        content.Dock = DockStyle.Fill;
        Controls.Add(content);
        Controls.Add(heading);
    }
}

internal sealed class AnalyzerStatusBadge : Label
{
    public AnalyzerStatusBadge()
    {
        AccessibleRole = AccessibleRole.StaticText;
        AutoSize = true;
        Padding = new Padding(8, 4, 8, 4);
        Margin = new Padding(3);
        TextAlign = ContentAlignment.MiddleCenter;
        Font = new Font("Segoe UI Semibold", 9F);
    }

    public void SetState(string text, Color foreground, Color background)
    {
        Text = text;
        AccessibleName = text;
        ForeColor = foreground;
        BackColor = background;
    }
}

internal static class AnalyzerUi
{
    public static TableLayoutPanel MetricRow(int height, params Control[] cards)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = height,
            ColumnCount = cards.Length,
            RowCount = 1,
            Padding = new Padding(0, 2, 0, 6),
            AccessibleName = "Analyzer summary metrics",
            AccessibleRole = AccessibleRole.Grouping,
            TabStop = false
        };
        for (int i = 0; i < cards.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / cards.Length));
            row.Controls.Add(cards[i], i, 0);
        }
        return row;
    }

    public static void StylePrimary(Button button)
    {
        button.AccessibleRole = AccessibleRole.PushButton;
        if (string.IsNullOrWhiteSpace(button.AccessibleName)) button.AccessibleName = button.Text;
        button.BackColor = Color.FromArgb(0, 92, 160);
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(0, 70, 160);
        button.Margin = new Padding(3);
    }

    public static void StyleSecondary(Button button)
    {
        button.AccessibleRole = AccessibleRole.PushButton;
        if (string.IsNullOrWhiteSpace(button.AccessibleName)) button.AccessibleName = button.Text;
        button.FlatStyle = FlatStyle.Standard;
        button.Margin = new Padding(3);
    }

    public static void StyleAttention(Button button)
    {
        button.AccessibleRole = AccessibleRole.PushButton;
        if (string.IsNullOrWhiteSpace(button.AccessibleName)) button.AccessibleName = button.Text;
        button.BackColor = Color.FromArgb(255, 244, 224);
        button.ForeColor = Color.FromArgb(128, 78, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(218, 165, 72);
        button.Margin = new Padding(3);
    }

    public static FlowLayoutPanel ActionBar()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MinimumSize = new Size(0, 42),
            Padding = new Padding(0, 3, 0, 3),
            WrapContents = true,
            AutoScroll = true,
            AccessibleName = "Actions",
            AccessibleRole = AccessibleRole.ToolBar,
            TabStop = false
        };
    }

    public static FlowLayoutPanel FilterBar()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MinimumSize = new Size(0, 68),
            Padding = new Padding(0, 3, 0, 3),
            WrapContents = true,
            AutoScroll = true,
            AccessibleName = "Filters",
            AccessibleRole = AccessibleRole.ToolBar,
            TabStop = false
        };
    }
}
