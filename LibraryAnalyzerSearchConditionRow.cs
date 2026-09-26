using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

internal sealed class LibraryAnalyzerSearchConditionRow : UserControl
{
    private sealed record PropertyChoice(CatalogSearchPropertyInfo Info)
    {
        public override string ToString() => Info.DisplayName;
    }

    private sealed record OperatorChoice(CatalogSearchOperator Value)
    {
        public override string ToString() => Value switch
        {
            CatalogSearchOperator.Equal => "Equals",
            CatalogSearchOperator.NotEqual => "Does not equal",
            CatalogSearchOperator.LessThan => "Less than",
            CatalogSearchOperator.LessThanOrEqual => "At most",
            CatalogSearchOperator.GreaterThan => "Greater than",
            CatalogSearchOperator.GreaterThanOrEqual => "At least",
            CatalogSearchOperator.Between => "Between",
            CatalogSearchOperator.Is => "Is",
            CatalogSearchOperator.IsNot => "Is not",
            CatalogSearchOperator.OneOf => "One of",
            CatalogSearchOperator.Contains => "Contains",
            CatalogSearchOperator.NotContains => "Does not contain",
            CatalogSearchOperator.StartsWith => "Starts with",
            CatalogSearchOperator.NotStartsWith => "Does not start with",
            CatalogSearchOperator.Known => "Known",
            CatalogSearchOperator.Unknown => "Unknown",
            CatalogSearchOperator.Yes => "Yes",
            CatalogSearchOperator.No => "No",
            _ => Value.ToString()
        };
    }

    internal readonly ComboBox Property = new() { Name = "AdvancedConditionProperty", Width = 195, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Search property" };
    internal readonly ComboBox Operator = new() { Name = "AdvancedConditionOperator", Width = 128, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Search operator" };
    internal readonly TextBox ValueText = new() { Name = "AdvancedConditionValue", Width = 165, MaxLength = 512, AccessibleName = "Search value" };
    internal readonly TextBox UpperText = new() { Name = "AdvancedConditionUpperValue", Width = 125, MaxLength = 512, AccessibleName = "Upper search value" };
    internal readonly ComboBox ValueChoice = new() { Name = "AdvancedConditionChoice", Width = 165, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Search value" };
    internal readonly DateTimePicker ValueDate = new() { Name = "AdvancedConditionDate", Width = 135, Format = DateTimePickerFormat.Short, AccessibleName = "Search date" };
    internal readonly DateTimePicker UpperDate = new() { Name = "AdvancedConditionUpperDate", Width = 135, Format = DateTimePickerFormat.Short, AccessibleName = "Upper search date" };
    internal readonly ComboBox Unit = new() { Name = "AdvancedConditionUnit", Width = 86, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Search unit" };
    internal readonly Button Remove = new() { Name = "AdvancedConditionRemove", Text = "Remove", AutoSize = true, AccessibleName = "Remove search condition" };

    internal event Action<bool>? Changed;
    internal event Action? RemoveRequested;
    internal event Action? SubmitRequested;
    private bool _rebuilding;

    internal LibraryAnalyzerSearchConditionRow(string? propertyId = null)
    {
        Name = "AdvancedSearchConditionRow";
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = "Advanced Search condition";
        Height = 38;
        MinimumSize = new Size(0, 36);
        Margin = new Padding(0, 2, 0, 2);
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(1, 1, 0, 0),
            Margin = Padding.Empty
        };
        layout.Controls.AddRange(new Control[]
        {
            Property, Operator, ValueText, ValueChoice, ValueDate,
            UpperText, UpperDate, Unit, Remove
        });
        Controls.Add(layout);

        foreach (CatalogSearchPropertyInfo info in CatalogSearchRegistry.Properties.OrderBy(info => info.DisplayName))
            Property.Items.Add(new PropertyChoice(info));
        Property.SelectedIndexChanged += (_, _) =>
        {
            RebuildOperators(resetValues: true);
            SignalChanged(immediate: true);
        };
        Operator.SelectedIndexChanged += (_, _) =>
        {
            ConfigureEditors();
            SignalChanged(immediate: true);
        };
        ValueText.TextChanged += (_, _) => SignalChanged(immediate: false);
        UpperText.TextChanged += (_, _) => SignalChanged(immediate: false);
        ValueChoice.SelectedIndexChanged += (_, _) => SignalChanged(immediate: true);
        Unit.SelectedIndexChanged += (_, _) => SignalChanged(immediate: true);
        ValueDate.ValueChanged += (_, _) => SignalChanged(immediate: true);
        UpperDate.ValueChanged += (_, _) => SignalChanged(immediate: true);
        foreach (TextBox editor in new[] { ValueText, UpperText })
            editor.KeyDown += (_, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                SubmitRequested?.Invoke();
            };
        Remove.Click += (_, _) => RemoveRequested?.Invoke();

        _rebuilding = true;
        Property.SelectedItem = Property.Items.Cast<PropertyChoice>()
            .FirstOrDefault(choice => choice.Info.Id == propertyId) ?? Property.Items[0];
        RebuildOperators(resetValues: true);
        _rebuilding = false;
    }

    internal CatalogSearchPropertyInfo SelectedProperty => ((PropertyChoice)Property.SelectedItem!).Info;
    internal CatalogSearchOperator SelectedOperator => ((OperatorChoice)Operator.SelectedItem!).Value;

    internal void SelectOperator(CatalogSearchOperator value)
    {
        Operator.SelectedItem = Operator.Items.Cast<OperatorChoice>()
            .First(choice => choice.Value == value);
    }

    internal CatalogSearchCondition BuildCondition()
    {
        CatalogSearchPropertyInfo property = SelectedProperty;
        CatalogSearchOperator operation = SelectedOperator;
        if (operation is CatalogSearchOperator.Known or CatalogSearchOperator.Unknown or
            CatalogSearchOperator.Yes or CatalogSearchOperator.No)
            return new CatalogSearchCondition(property.Id, operation);

        if (property.DataType == CatalogSearchDataType.Date)
            return new CatalogSearchCondition(property.Id, operation,
                new CatalogSearchValue(Date: DateOnly.FromDateTime(ValueDate.Value)),
                operation == CatalogSearchOperator.Between
                    ? new CatalogSearchValue(Date: DateOnly.FromDateTime(UpperDate.Value)) : null);

        if (property.DataType == CatalogSearchDataType.Number)
        {
            string unit = (string)Unit.SelectedItem!;
            CatalogSearchValue value = CatalogSearchValue.ParseNumber(ValueText.Text.Trim(), unit);
            return new CatalogSearchCondition(property.Id, operation, value,
                operation == CatalogSearchOperator.Between
                    ? CatalogSearchValue.ParseNumber(UpperText.Text.Trim(), unit) : null);
        }

        if (operation == CatalogSearchOperator.OneOf)
        {
            string[] values = ValueText.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0)
                throw new ArgumentException($"Enter one or more values for {property.DisplayName}.");
            return new CatalogSearchCondition(property.Id, operation,
                Values: values.Select(value => new CatalogSearchValue(Text: value)).ToArray());
        }

        string text = property.DataType == CatalogSearchDataType.Choice && property.Choices.Count > 0
            ? ValueChoice.SelectedItem?.ToString() ?? ""
            : ValueText.Text.Trim();
        if (text.Length == 0)
            throw new ArgumentException($"Enter a value for {property.DisplayName}.");
        return new CatalogSearchCondition(property.Id, operation, new CatalogSearchValue(Text: text));
    }

    private void RebuildOperators(bool resetValues)
    {
        if (Property.SelectedItem is not PropertyChoice choice) return;
        bool wasRebuilding = _rebuilding;
        _rebuilding = true;
        Operator.Items.Clear();
        foreach (CatalogSearchOperator operation in choice.Info.Operators)
            Operator.Items.Add(new OperatorChoice(operation));
        Operator.SelectedIndex = 0;
        Unit.Items.Clear();
        foreach (string unit in choice.Info.Units)
            Unit.Items.Add(unit);
        if (Unit.Items.Count > 0) Unit.SelectedIndex = 0;
        ValueChoice.Items.Clear();
        foreach (string value in choice.Info.Choices)
            ValueChoice.Items.Add(value);
        if (ValueChoice.Items.Count > 0) ValueChoice.SelectedIndex = 0;
        if (resetValues)
        {
            ValueText.Clear();
            UpperText.Clear();
        }
        ConfigureEditors();
        _rebuilding = wasRebuilding;
    }

    private void ConfigureEditors()
    {
        if (Property.SelectedItem is not PropertyChoice || Operator.SelectedItem is not OperatorChoice)
            return;
        CatalogSearchPropertyInfo property = SelectedProperty;
        CatalogSearchOperator operation = SelectedOperator;
        bool hasValue = operation is not (CatalogSearchOperator.Known or CatalogSearchOperator.Unknown or
            CatalogSearchOperator.Yes or CatalogSearchOperator.No);
        bool range = operation == CatalogSearchOperator.Between;
        bool choiceList = property.DataType == CatalogSearchDataType.Choice &&
            property.Choices.Count > 0 && operation != CatalogSearchOperator.OneOf;
        ValueText.Visible = hasValue && (property.DataType is CatalogSearchDataType.Number or CatalogSearchDataType.Text ||
            property.DataType == CatalogSearchDataType.Choice && !choiceList);
        ValueChoice.Visible = hasValue && choiceList;
        ValueDate.Visible = hasValue && property.DataType == CatalogSearchDataType.Date;
        UpperText.Visible = hasValue && range && property.DataType == CatalogSearchDataType.Number;
        UpperDate.Visible = hasValue && range && property.DataType == CatalogSearchDataType.Date;
        Unit.Visible = hasValue && property.DataType == CatalogSearchDataType.Number;
        ValueText.PlaceholderText = operation == CatalogSearchOperator.OneOf ? "Comma-separated values" : "Value";
    }

    private void SignalChanged(bool immediate)
    {
        if (!_rebuilding) Changed?.Invoke(immediate);
    }
}
