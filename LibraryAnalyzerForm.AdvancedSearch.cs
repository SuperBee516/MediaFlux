using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private const int AdvancedSearchDebounceMilliseconds = 300;

    private sealed record QuickChoice(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly LibraryFilesRequestCoordinator _fileRequests = new();
    private readonly System.Windows.Forms.Timer _advancedSearchDebounceTimer = new()
    {
        Interval = AdvancedSearchDebounceMilliseconds
    };
    private readonly ToolTip _advancedSearchToolTip = new();
    private readonly Panel _advancedSearchPanel = new()
    {
        Name = "AdvancedSearchPanel",
        Dock = DockStyle.Top,
        Height = 36,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Window,
        AccessibleName = "Advanced Search",
        AccessibleRole = AccessibleRole.Grouping
    };
    private readonly Button _advancedSearchToggle = new()
    {
        Name = "AdvancedSearchToggle",
        Text = "Advanced Search ▸",
        AutoSize = true,
        AccessibleName = "Expand Advanced Search"
    };
    private readonly Label _advancedSearchCount = new()
    {
        Name = "AdvancedSearchCount",
        AutoSize = true,
        Padding = new Padding(8, 6, 8, 0),
        ForeColor = SystemColors.GrayText
    };
    private readonly Button _advancedSearchClear = new()
    {
        Name = "AdvancedSearchClear",
        Text = "Clear",
        AutoSize = true,
        AccessibleName = "Clear Advanced Search"
    };
    private readonly Label _advancedSearchValidation = new()
    {
        Name = "AdvancedSearchValidation",
        AutoSize = true,
        AutoEllipsis = true,
        MaximumSize = new Size(550, 30),
        Padding = new Padding(8, 6, 0, 0),
        ForeColor = AnalyzerUi.AttentionTextColor,
        AccessibleRole = AccessibleRole.StaticText
    };
    private readonly TableLayoutPanel _advancedSearchBody = new()
    {
        Name = "AdvancedSearchBody",
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 3,
        Visible = false
    };
    private readonly FlowLayoutPanel _advancedConditionRows = new()
    {
        Name = "AdvancedConditionRows",
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(4, 0, 4, 0),
        AccessibleName = "Search conditions",
        AccessibleRole = AccessibleRole.Grouping
    };
    private readonly List<LibraryAnalyzerSearchConditionRow> _advancedRows = new();
    private readonly ComboBox _quickCodec = QuickCombo("AdvancedQuickCodec", "Quick video codec");
    private readonly ComboBox _quickResolution = QuickCombo("AdvancedQuickResolution", "Quick resolution class");
    private readonly ComboBox _quickBitDepth = QuickCombo("AdvancedQuickBitDepth", "Quick bit depth");
    private readonly ComboBox _quickScanType = QuickCombo("AdvancedQuickScanType", "Quick scan type");
    private readonly ComboBox _quickDynamicRange = QuickCombo("AdvancedQuickDynamicRange", "Quick HDR or SDR");
    private readonly TextBox _quickFpsMin = QuickText("AdvancedQuickFpsMin", "Minimum FPS");
    private readonly TextBox _quickFpsMax = QuickText("AdvancedQuickFpsMax", "Maximum FPS");
    private readonly TextBox _quickBitrateMin = QuickText("AdvancedQuickBitrateMin", "Minimum video bitrate in decimal Mbps");
    private readonly TextBox _quickBitrateMax = QuickText("AdvancedQuickBitrateMax", "Maximum video bitrate in decimal Mbps");
    private bool _suppressAdvancedSearchEvents;
    private bool _advancedSearchExpanded;

    private static ComboBox QuickCombo(string name, string accessibleName) => new()
    {
        Name = name,
        Width = 118,
        DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = accessibleName
    };

    private static TextBox QuickText(string name, string accessibleName) => new()
    {
        Name = name,
        Width = 72,
        MaxLength = 24,
        AccessibleName = accessibleName
    };

    private Control BuildAdvancedSearchPanel()
    {
        _suppressAdvancedSearchEvents = true;
        FillQuickChoice(_quickCodec, new[]
        {
            new QuickChoice("h264", "H.264"), new QuickChoice("hevc", "H.265 / HEVC"),
            new QuickChoice("av1", "AV1"), new QuickChoice("vp9", "VP9")
        });
        FillQuickChoice(_quickResolution, RegistryChoices("video.resolution_class"));
        FillQuickChoice(_quickBitDepth, new[]
        {
            new QuickChoice("8", "8-bit"), new QuickChoice("10", "10-bit"),
            new QuickChoice("12", "12-bit")
        });
        FillQuickChoice(_quickScanType, RegistryChoices("video.scan_type"));
        FillQuickChoice(_quickDynamicRange, RegistryChoices("video.dynamic_range"));
        _suppressAdvancedSearchEvents = false;
        InitializeSavedSearchUi();

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(4, 2, 4, 0)
        };
        AnalyzerUi.StyleSecondary(_advancedSearchToggle);
        AnalyzerUi.StyleSecondary(_advancedSearchClear);
        header.Controls.AddRange(new Control[]
        {
            _advancedSearchToggle, _advancedSearchCount, _savedSearchPicker,
            _saveSavedSearchButton, _manageSavedSearchesButton, _advancedSearchClear,
            _advancedSearchValidation
        });
        _advancedSearchToggle.Click += (_, _) => SetAdvancedSearchExpanded(!_advancedSearchExpanded);
        _advancedSearchClear.Click += (_, _) => ClearAdvancedSearch();

        var quick = new FlowLayoutPanel
        {
            Name = "AdvancedQuickFilters",
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoScroll = true,
            Padding = new Padding(4, 0, 4, 0),
            AccessibleName = "Quick Filters",
            AccessibleRole = AccessibleRole.Grouping
        };
        quick.Controls.AddRange(new Control[]
        {
            Labeled("Codec", _quickCodec), Labeled("Resolution", _quickResolution),
            Labeled("FPS min", _quickFpsMin), Labeled("FPS max", _quickFpsMax),
            Labeled("Video Mbps min", _quickBitrateMin), Labeled("Video Mbps max", _quickBitrateMax),
            Labeled("Bit depth", _quickBitDepth), Labeled("Scan", _quickScanType),
            Labeled("HDR/SDR", _quickDynamicRange)
        });
        _advancedSearchToolTip.SetToolTip(_quickBitrateMin, "Decimal Mbps: 1 Mbps = 1,000,000 video-stream bits per second.");
        _advancedSearchToolTip.SetToolTip(_quickBitrateMax, "Decimal Mbps: 1 Mbps = 1,000,000 video-stream bits per second.");

        var conditionBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(4, 0, 4, 0)
        };
        var add = new Button
        {
            Name = "AdvancedAddCondition",
            Text = "+ Add condition",
            AutoSize = true,
            AccessibleName = "Add Advanced Search condition"
        };
        AnalyzerUi.StyleSecondary(add);
        add.Click += (_, _) => AddAdvancedCondition();
        conditionBar.Controls.Add(add);
        conditionBar.Controls.Add(new Label
        {
            Text = "Property  |  Operator  |  Value",
            AutoSize = true,
            Padding = new Padding(8, 6, 0, 0),
            ForeColor = SystemColors.GrayText
        });
        _advancedConditionRows.ClientSizeChanged += (_, _) =>
        {
            int width = Math.Max(650, _advancedConditionRows.ClientSize.Width - 20);
            foreach (LibraryAnalyzerSearchConditionRow row in _advancedRows)
                row.Width = width;
        };

        _advancedSearchBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _advancedSearchBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        _advancedSearchBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _advancedSearchBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _advancedSearchBody.Controls.Add(quick, 0, 0);
        _advancedSearchBody.Controls.Add(conditionBar, 0, 1);
        _advancedSearchBody.Controls.Add(_advancedConditionRows, 0, 2);
        _advancedSearchPanel.Controls.Add(_advancedSearchBody);
        _advancedSearchPanel.Controls.Add(header);

        foreach (ComboBox choice in new[] { _quickCodec, _quickResolution, _quickBitDepth, _quickScanType, _quickDynamicRange })
            choice.SelectedIndexChanged += (_, _) => ScheduleAdvancedSearchRefresh(immediate: true);
        foreach (TextBox editor in new[] { _quickFpsMin, _quickFpsMax, _quickBitrateMin, _quickBitrateMax })
        {
            editor.TextChanged += (_, _) => ScheduleAdvancedSearchRefresh(immediate: false);
            editor.KeyDown += (_, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                ScheduleAdvancedSearchRefresh(immediate: true);
            };
        }
        _advancedSearchDebounceTimer.Tick += (_, _) =>
        {
            _advancedSearchDebounceTimer.Stop();
            _ = RefreshFilesAsync();
        };
        UpdateAdvancedFilterCount();
        return _advancedSearchPanel;
    }

    private static IEnumerable<QuickChoice> RegistryChoices(string propertyId)
    {
        if (!CatalogSearchRegistry.TryGet(propertyId, out CatalogSearchPropertyInfo? property) || property == null)
            throw new InvalidOperationException($"Missing catalog search property {propertyId}.");
        return property.Choices.Select(value => new QuickChoice(value, value));
    }

    private static void FillQuickChoice(ComboBox combo, IEnumerable<QuickChoice> choices)
    {
        combo.Items.Add(new QuickChoice(null, "Any"));
        foreach (QuickChoice choice in choices)
            combo.Items.Add(choice);
        combo.SelectedIndex = 0;
    }

    private void SetAdvancedSearchExpanded(bool expanded)
    {
        _advancedSearchExpanded = expanded;
        _advancedSearchPanel.SuspendLayout();
        try
        {
            _advancedSearchBody.Visible = expanded;
            _advancedSearchPanel.Height = expanded ? 240 : 36;
            _advancedSearchToggle.Text = expanded ? "Advanced Search ▾" : "Advanced Search ▸";
            _advancedSearchToggle.AccessibleName = expanded ? "Collapse Advanced Search" : "Expand Advanced Search";
        }
        finally { _advancedSearchPanel.ResumeLayout(); }
    }

    private void AddAdvancedCondition(string? propertyId = null)
    {
        LibraryAnalyzerSearchConditionRow row = CreateAdvancedConditionRow(propertyId);
        ScheduleAdvancedSearchRefresh(immediate: true);
        row.Property.Focus();
    }

    private LibraryAnalyzerSearchConditionRow CreateAdvancedConditionRow(string? propertyId)
    {
        var row = new LibraryAnalyzerSearchConditionRow(propertyId)
        {
            Width = Math.Max(650, _advancedConditionRows.ClientSize.Width - 20)
        };
        AttachAdvancedConditionRow(row);
        return row;
    }

    private void AttachAdvancedConditionRow(LibraryAnalyzerSearchConditionRow row)
    {
        row.Width = Math.Max(650, _advancedConditionRows.ClientSize.Width - 20);
        row.Changed += immediate => ScheduleAdvancedSearchRefresh(immediate);
        row.SubmitRequested += () => ScheduleAdvancedSearchRefresh(immediate: true);
        row.RemoveRequested += () => RemoveAdvancedCondition(row);
        _advancedRows.Add(row);
        _advancedConditionRows.Controls.Add(row);
    }

    private void RemoveAdvancedCondition(LibraryAnalyzerSearchConditionRow row)
    {
        _advancedRows.Remove(row);
        _advancedConditionRows.Controls.Remove(row);
        row.Dispose();
        ScheduleAdvancedSearchRefresh(immediate: true);
    }

    private void ClearAdvancedSearch()
    {
        _suppressAdvancedSearchEvents = true;
        try
        {
            foreach (ComboBox choice in new[] { _quickCodec, _quickResolution, _quickBitDepth, _quickScanType, _quickDynamicRange })
                choice.SelectedIndex = 0;
            foreach (TextBox editor in new[] { _quickFpsMin, _quickFpsMax, _quickBitrateMin, _quickBitrateMax })
                editor.Clear();
            foreach (LibraryAnalyzerSearchConditionRow row in _advancedRows)
            {
                _advancedConditionRows.Controls.Remove(row);
                row.Dispose();
            }
            _advancedRows.Clear();
        }
        finally { _suppressAdvancedSearchEvents = false; }
        _activeSavedSearchTemplate = null;
        ClearSavedSearchAssociation(selectPlaceholder: true);
        SetAdvancedSearchValidation("");
        ScheduleAdvancedSearchRefresh(immediate: true);
    }

    private void ScheduleAdvancedSearchRefresh(bool immediate)
    {
        if (_suppressAdvancedSearchEvents || !CanUseFormUi) return;
        _page = 0;
        _fileRequests.Invalidate();
        _advancedSearchDebounceTimer.Stop();
        _previous.Enabled = false;
        _next.Enabled = false;
        UpdateAdvancedFilterCount();
        if (immediate)
            _ = RefreshFilesAsync();
        else
            _advancedSearchDebounceTimer.Start();
    }

    private void UpdateAdvancedFilterCount()
    {
        int count = _advancedRows.Count +
            new[] { _quickCodec, _quickResolution, _quickBitDepth, _quickScanType, _quickDynamicRange }
                .Count(choice => choice.SelectedItem is QuickChoice { Value: not null }) +
            (string.IsNullOrWhiteSpace(_quickFpsMin.Text) && string.IsNullOrWhiteSpace(_quickFpsMax.Text) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(_quickBitrateMin.Text) && string.IsNullOrWhiteSpace(_quickBitrateMax.Text) ? 0 : 1) +
            SavedSearchTemplateFilterCount();
        _advancedSearchCount.Text = $"{count} advanced filter{(count == 1 ? "" : "s")}";
        _advancedSearchClear.Enabled = count > 0;
        RefreshSavedSearchDirtyState();
    }

    private CatalogSearchDefinition? BuildAdvancedSearchDefinition()
    {
        var conditions = new List<CatalogSearchCondition>();
        AddChoice("video.codec", _quickCodec);
        AddChoice("video.resolution_class", _quickResolution);
        AddRange("video.fps", _quickFpsMin, _quickFpsMax, "fps");
        AddRange("video.bitrate", _quickBitrateMin, _quickBitrateMax, "mbps");
        if (_quickBitDepth.SelectedItem is QuickChoice { Value: { } depth })
            conditions.Add(new CatalogSearchCondition("video.bit_depth", CatalogSearchOperator.Equal,
                CatalogSearchValue.ParseNumber(depth, "count")));
        AddChoice("video.scan_type", _quickScanType);
        AddChoice("video.dynamic_range", _quickDynamicRange);
        for (int i = 0; i < _advancedRows.Count; i++)
        {
            try { conditions.Add(_advancedRows[i].BuildCondition()); }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"Condition {i + 1}: {exception.Message}", exception);
            }
        }
        CatalogSearchDefinition? template = _activeSavedSearchTemplate;
        bool hasTemplateFilters = template != null && SavedSearchTemplateFilterCount() > 0;
        if (conditions.Count == 0 && !hasTemplateFilters) return null;
        var definition = new CatalogSearchDefinition(template?.Version ?? CatalogSearchDefinition.CurrentVersion,
            conditions,
            template?.Search ?? "",
            template?.LocationId,
            template?.Availability,
            template?.ProbeStatus,
            template?.Sort);
        CatalogSearchDefinitionValidator.Validate(definition);
        return definition;

        void AddChoice(string id, ComboBox combo)
        {
            if (combo.SelectedItem is QuickChoice { Value: { } value })
                conditions.Add(new CatalogSearchCondition(id, CatalogSearchOperator.Is, new CatalogSearchValue(Text: value)));
        }

        void AddRange(string id, TextBox minimum, TextBox maximum, string unit)
        {
            bool hasMinimum = !string.IsNullOrWhiteSpace(minimum.Text);
            bool hasMaximum = !string.IsNullOrWhiteSpace(maximum.Text);
            if (!hasMinimum && !hasMaximum) return;
            CatalogSearchValue? low = hasMinimum ? CatalogSearchValue.ParseNumber(minimum.Text.Trim(), unit) : null;
            CatalogSearchValue? high = hasMaximum ? CatalogSearchValue.ParseNumber(maximum.Text.Trim(), unit) : null;
            conditions.Add(hasMinimum && hasMaximum
                ? new CatalogSearchCondition(id, CatalogSearchOperator.Between, low, high)
                : new CatalogSearchCondition(id,
                    hasMinimum ? CatalogSearchOperator.GreaterThanOrEqual : CatalogSearchOperator.LessThanOrEqual,
                    hasMinimum ? low : high));
        }
    }

    private void SetAdvancedSearchValidation(string message)
    {
        _advancedSearchValidation.Text = message;
        _advancedSearchValidation.AccessibleName = string.IsNullOrEmpty(message) ? "" : $"Search validation: {message}";
    }

    private void CleanupAdvancedSearchUi()
    {
        _advancedSearchDebounceTimer.Stop();
        _advancedSearchDebounceTimer.Dispose();
        _fileRequests.Dispose();
        _advancedSearchToolTip.Dispose();
    }
}
