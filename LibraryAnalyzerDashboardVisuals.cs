using System.Drawing.Drawing2D;

namespace MediaFlux;

/// <summary>Small, DPI-friendly drawing vocabulary shared by Library Analyzer dashboard controls.</summary>
internal static class DashboardVisuals
{
    public static readonly Color Accent = Color.FromArgb(0, 92, 160);
    public static readonly Color Warning = Color.FromArgb(181, 110, 0);
    public static readonly Color PrimaryText = Color.FromArgb(35, 45, 55);
    public static readonly Color MutedText = Color.FromArgb(92, 102, 112);
    public static readonly Color Border = Color.FromArgb(205, 215, 224);
    public static readonly Color Track = Color.FromArgb(232, 238, 243);
    public static readonly Color Grid = Color.FromArgb(225, 232, 238);
    public static readonly Color HoverBackground = Color.FromArgb(246, 250, 253);
    public static readonly Color FocusBackground = Color.FromArgb(232, 242, 250);
    public static readonly Font LabelFont = new("Segoe UI", 8.5F);
    public static readonly Font DetailFont = new("Segoe UI", 8F);
    public static readonly Font MetricFont = new("Segoe UI Semibold", 16F);
    public static readonly StringFormat NearStringFormat = new() { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    public static readonly StringFormat FarStringFormat = new() { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

    public static Color ChartColor(int index) => (index % 4) switch { 1 => Color.FromArgb(46, 125, 80), 2 => Color.FromArgb(104, 75, 155), 3 => Color.FromArgb(186, 98, 0), _ => Accent };

    public static void DrawCard(Graphics graphics, Rectangle bounds, Color accent)
    {
        Rectangle rect = Rectangle.Inflate(bounds, -1, -1);
        DrawRoundedFill(graphics, rect, SystemColors.Window, 6);
        using var border = new Pen(Border); DrawRoundedOutline(graphics, rect, border, 6);
        using var stripe = new SolidBrush(accent); graphics.FillRectangle(stripe, rect.Left, rect.Top + 7, 3, Math.Max(0, rect.Height - 14));
    }

    public static void DrawProgress(Graphics graphics, Rectangle bounds, double percent, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        DrawRoundedFill(graphics, bounds, Track, Math.Min(bounds.Height / 2, 5));
        int fillWidth = (int)Math.Round(bounds.Width * Math.Clamp(percent, 0, 100) / 100d);
        if (fillWidth > 0) DrawRoundedFill(graphics, new Rectangle(bounds.X, bounds.Y, Math.Max(bounds.Height, fillWidth), bounds.Height), color, Math.Min(bounds.Height / 2, 5));
    }

    public static void DrawEmptyState(Graphics graphics, Rectangle bounds, string text)
    {
        using var brush = new SolidBrush(MutedText);
        SizeF size = graphics.MeasureString(text, DetailFont);
        graphics.DrawString(text, DetailFont, brush, Math.Max(6, (bounds.Width - size.Width) / 2), Math.Max(4, (bounds.Height - size.Height) / 2));
    }

    public static void DrawRoundedFill(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        using var brush = new SolidBrush(color); using var path = RoundedPath(bounds, radius); graphics.FillPath(brush, path);
    }

    public static void DrawRoundedOutline(Graphics graphics, Rectangle bounds, Pen pen, int radius)
    {
        using var path = RoundedPath(bounds, radius); graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        if (diameter <= 2) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
}

internal sealed class OverviewDashboardPanel : Panel
{
    public OverviewDashboardPanel(string title, Control content)
    {
        AccessibleName = title; AccessibleRole = AccessibleRole.Grouping; TabStop = false; Dock = DockStyle.Fill; Margin = new Padding(3); Padding = new Padding(8, 6, 8, 8); BackColor = SystemColors.Window;
        var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 25, Font = new Font("Segoe UI Semibold", 9F), ForeColor = DashboardVisuals.Accent, Padding = new Padding(0, 2, 4, 2), AutoEllipsis = true, AccessibleName = title, AccessibleRole = AccessibleRole.Text };
        content.Dock = DockStyle.Fill; Controls.Add(content); Controls.Add(heading);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(DashboardVisuals.Border); DashboardVisuals.DrawRoundedOutline(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1), border, 6);
    }
}

internal enum OverviewStatusKind { Info, Success, Warning, Error }

internal sealed class OverviewStatusBadge : Label
{
    public OverviewStatusBadge()
    {
        AutoSize = true; Anchor = AnchorStyles.Right; TextAlign = ContentAlignment.MiddleCenter; Padding = new Padding(9, 4, 9, 4); Margin = new Padding(3); Font = new Font("Segoe UI Semibold", 9F); AccessibleRole = AccessibleRole.StaticText;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void SetState(string text, OverviewStatusKind state)
    {
        Text = text; AccessibleName = text;
        (Color foreground, Color background, Color border) = state switch
        {
            OverviewStatusKind.Warning => (Color.FromArgb(128, 78, 0), Color.FromArgb(255, 244, 224), Color.FromArgb(218, 165, 72)),
            OverviewStatusKind.Error => (Color.FromArgb(150, 35, 35), Color.FromArgb(255, 232, 232), Color.FromArgb(210, 120, 120)),
            OverviewStatusKind.Success => (Color.FromArgb(24, 105, 62), Color.FromArgb(231, 247, 237), Color.FromArgb(126, 190, 151)),
            _ => (DashboardVisuals.Accent, Color.FromArgb(232, 242, 250), Color.FromArgb(145, 184, 211))
        };
        ForeColor = foreground; BackColor = background; _border = border; Invalidate();
    }

    private Color _border = DashboardVisuals.Border;
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; DashboardVisuals.DrawRoundedFill(e.Graphics, ClientRectangle, BackColor, 8); using var pen = new Pen(_border); DashboardVisuals.DrawRoundedOutline(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1), pen, 8); TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

internal sealed class OverviewProgressBar : Control
{
    private double _percent;
    public double Percent { get => _percent; set { _percent = Math.Clamp(value, 0, 100); Invalidate(); } }
    public OverviewProgressBar() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; DashboardVisuals.DrawProgress(e.Graphics, new Rectangle(4, Math.Max(0, Height / 2 - 2), Math.Max(0, Width - 8), 4), Percent, DashboardVisuals.Accent); }
}
