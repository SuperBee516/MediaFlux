namespace MediaFlux;

internal sealed record AnalyzerNavigationEntry(string Group, string Label, int PageIndex);

internal sealed class AnalyzerNavigationRail : UserControl
{
    private readonly FlowLayoutPanel _items = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(8, 6, 8, 8),
        Margin = Padding.Empty,
        TabStop = false
    };
    private readonly List<AnalyzerNavigationItem> _navigationItems = new();
    private IReadOnlyList<AnalyzerNavigationEntry> _entries = Array.Empty<AnalyzerNavigationEntry>();
    private int _selectedIndex = -1;

    public AnalyzerNavigationRail()
    {
        Name = "LibraryAnalyzerNavigationRail";
        Dock = DockStyle.Fill;
        AccessibleName = "Library Analyzer navigation";
        AccessibleRole = AccessibleRole.Grouping;
        BackColor = SystemColors.Control;
        MinimumSize = new Size(165, 0);
        Padding = Padding.Empty;
        Controls.Add(_items);
        Resize += (_, _) => ResizeNavigationItems();
    }

    public event EventHandler<int>? DestinationActivated;

    public IReadOnlyList<AnalyzerNavigationEntry> Entries => _entries;

    public bool VerticalScrollVisible => _items.VerticalScroll.Visible;
    public int NavigationContentHeight => _items.Padding.Vertical + _items.Controls.Cast<Control>().Sum(control => control.Height + control.Margin.Vertical);
    public int NavigationViewportHeight => _items.ClientSize.Height;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int normalized = _entries.Count == 0 ? -1 : Math.Clamp(value, 0, _entries.Count - 1);
            if (_selectedIndex == normalized)
            {
                UpdateItemStates();
                return;
            }

            _selectedIndex = normalized;
            UpdateItemStates();
        }
    }

    public void SetEntries(IReadOnlyList<AnalyzerNavigationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
        _navigationItems.Clear();
        _items.Controls.Clear();

        string? currentGroup = null;
        for (int i = 0; i < entries.Count; i++)
        {
            AnalyzerNavigationEntry entry = entries[i];
            if (!string.Equals(currentGroup, entry.Group, StringComparison.Ordinal))
            {
                currentGroup = entry.Group;
                _items.Controls.Add(new Label
                {
                    Text = currentGroup,
                    AutoSize = false,
                    Height = 24,
                    Margin = new Padding(4, i == 0 ? 2 : 8, 4, 0),
                    Padding = new Padding(8, 4, 4, 0),
                    ForeColor = SystemColors.GrayText,
                    Font = new Font(Font, FontStyle.Bold),
                    AccessibleName = currentGroup,
                    AccessibleRole = AccessibleRole.Grouping
                });
            }

            AnalyzerNavigationItem item = new(entry.Label)
            {
                Name = $"AnalyzerNavigationItem{entry.PageIndex}",
                AccessibleName = $"Library Analyzer {entry.Label}",
                AccessibleRole = AccessibleRole.PageTab,
                AccessibleDescription = $"Navigate to {entry.Label}",
                TabIndex = i
            };
            int itemIndex = i;
            item.Click += (_, _) => DestinationActivated?.Invoke(this, itemIndex);
            item.MoveRequested += (_, direction) => MoveFocus(itemIndex, direction);
            _navigationItems.Add(item);
            _items.Controls.Add(item);
        }

        ResizeNavigationItems();
        SelectedIndex = _selectedIndex < 0 ? 0 : _selectedIndex;
    }

    public void FocusSelectedItem()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _navigationItems.Count)
            _navigationItems[_selectedIndex].Focus();
    }

    public void Activate(int itemIndex)
    {
        if (itemIndex < 0 || itemIndex >= _entries.Count) return;
        SelectedIndex = itemIndex;
        DestinationActivated?.Invoke(this, itemIndex);
    }

    private void MoveFocus(int index, int direction)
    {
        int target = Math.Clamp(index + direction, 0, _navigationItems.Count - 1);
        if (target == index) return;
        _navigationItems[target].Focus();
    }

    private void ResizeNavigationItems()
    {
        _items.AutoScroll = false;
        int contentHeight = _items.Padding.Vertical + _items.Controls.Cast<Control>().Sum(control => control.Height + control.Margin.Vertical);
        _items.AutoScroll = contentHeight > _items.ClientSize.Height;
        int scrollWidth = _items.AutoScroll ? SystemInformation.VerticalScrollBarWidth : 0;
        int width = Math.Max(0, _items.ClientSize.Width - _items.Padding.Horizontal - scrollWidth - 2);
        foreach (AnalyzerNavigationItem item in _navigationItems)
            item.Width = width;
    }

    private void UpdateItemStates()
    {
        for (int i = 0; i < _navigationItems.Count; i++)
        {
            bool selected = i == _selectedIndex;
            _navigationItems[i].Selected = selected;
            _navigationItems[i].AccessibleDescription = selected
                ? $"Navigate to {_navigationItems[i].Text}. Selected."
                : $"Navigate to {_navigationItems[i].Text}";
        }
    }
}

internal sealed class AnalyzerNavigationItem : Button
{
    private bool _selected;
    private bool _hovered;

    public AnalyzerNavigationItem(string text)
    {
        Text = text;
        AutoSize = false;
        Height = 32;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseDownBackColor = Color.FromArgb(214, 228, 240);
        FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 239, 246);
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(14, 0, 8, 0);
        Margin = new Padding(4, 1, 4, 1);
        UseVisualStyleBackColor = false;
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        TabStop = true;
        KeyDown += NavigationItem_KeyDown;
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
    }

    public event EventHandler<int>? MoveRequested;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Font = new Font(Font, value ? FontStyle.Bold : FontStyle.Regular);
            AccessibleDescription = value ? $"Navigate to {Text}. Selected." : $"Navigate to {Text}";
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_selected)
        {
            using var accent = new SolidBrush(Color.FromArgb(0, 92, 160));
            e.Graphics.FillRectangle(accent, new Rectangle(0, 0, 4, ClientSize.Height));
            using var selectedBack = new SolidBrush(Color.FromArgb(220, 233, 244));
            e.Graphics.FillRectangle(selectedBack, new Rectangle(4, 0, ClientSize.Width - 4, ClientSize.Height));
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Padding.Left, 0, ClientSize.Width - Padding.Horizontal, ClientSize.Height), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else if (_hovered)
        {
            using var hover = new SolidBrush(Color.FromArgb(232, 239, 246));
            e.Graphics.FillRectangle(hover, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Padding.Left, 0, ClientSize.Width - Padding.Horizontal, ClientSize.Height), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        if (Focused)
        {
            using var focus = new Pen(Color.FromArgb(0, 92, 160));
            Rectangle bounds = ClientRectangle;
            bounds.Inflate(-2, -2);
            e.Graphics.DrawRectangle(focus, bounds);
        }
    }

    private void NavigationItem_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            MoveRequested?.Invoke(this, e.KeyCode == Keys.Up ? -1 : 1);
        }
    }
}
