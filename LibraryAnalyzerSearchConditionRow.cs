using MediaFlux.Services.LibraryCatalog;
using System.Globalization;
using System.Text;

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
    internal readonly TextBox ValueText = new() { Name = "AdvancedConditionValue", Width = 165, MaxLength = CatalogSearchDefinitionValidator.MaximumTextLength, AccessibleName = "Search value" };
    internal readonly TextBox UpperText = new() { Name = "AdvancedConditionUpperValue", Width = 125, MaxLength = 512, AccessibleName = "Upper search value" };
    internal readonly ComboBox ValueChoice = new() { Name = "AdvancedConditionChoice", Width = 165, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Search value" };
    internal readonly DateTimePicker ValueDate = new() { Name = "AdvancedConditionDate", Width = 135, Format = DateTimePickerFormat.Short, AccessibleName = "Search date" };
    internal readonly DateTimePicker UpperDate = new() { Name = "AdvancedConditionUpperDate", Width = 135, Format = DateTimePickerFormat.Short, AccessibleName = "Upper search date" };
    internal readonly ComboBox Unit = new() { Name = "AdvancedConditionUnit", Width = 86, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Search unit" };
    internal readonly ComboBox UpperUnit = new() { Name = "AdvancedConditionUpperUnit", Width = 86, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Upper search unit" };
    internal readonly Button Remove = new() { Name = "AdvancedConditionRemove", Text = "Remove", AutoSize = true, AccessibleName = "Remove search condition" };

    internal event Action<bool>? Changed;
    internal event Action? RemoveRequested;
    internal event Action? SubmitRequested;
    private bool _rebuilding;
    private bool _syncingUnits;
    private bool _upperUnitWasManuallyChanged;

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
            Property, Operator, ValueText, ValueChoice, ValueDate, Unit,
            UpperText, UpperDate, UpperUnit, Remove
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
        Unit.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuilding && !_syncingUnits && !_upperUnitWasManuallyChanged && Unit.SelectedItem != null)
            {
                _syncingUnits = true;
                try { UpperUnit.SelectedItem = Unit.SelectedItem; }
                finally { _syncingUnits = false; }
            }
            SignalChanged(immediate: true);
        };
        UpperUnit.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuilding && !_syncingUnits) _upperUnitWasManuallyChanged = true;
            SignalChanged(immediate: true);
        };
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
                    ? CatalogSearchValue.ParseNumber(UpperText.Text.Trim(), (string)UpperUnit.SelectedItem!) : null);
        }

        if (operation == CatalogSearchOperator.OneOf)
        {
            string[] values = ParseOneOfValues(ValueText.Text);
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

    internal void ApplyCondition(CatalogSearchCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        CatalogSearchPropertyInfo property = CatalogSearchRegistry.Properties
            .FirstOrDefault(item => item.Id == condition.PropertyId)
            ?? throw new ArgumentException($"Unknown search property '{condition.PropertyId}'.", nameof(condition));

        _rebuilding = true;
        try
        {
            Property.SelectedItem = Property.Items.Cast<PropertyChoice>()
                .First(choice => choice.Info.Id == property.Id);
            RebuildOperators(resetValues: true);
            Operator.SelectedItem = Operator.Items.Cast<OperatorChoice>()
                .First(choice => choice.Value == condition.Operator);
            ConfigureEditors();

            if (condition.Operator is CatalogSearchOperator.Known or CatalogSearchOperator.Unknown or
                CatalogSearchOperator.Yes or CatalogSearchOperator.No)
                return;

            if (property.DataType == CatalogSearchDataType.Date)
            {
                ValueDate.Value = condition.Value!.Date!.Value.ToDateTime(TimeOnly.MinValue);
                if (condition.UpperValue?.Date is { } upperDate)
                    UpperDate.Value = upperDate.ToDateTime(TimeOnly.MinValue);
            }
            else if (property.DataType == CatalogSearchDataType.Number)
            {
                SelectUnit(condition.Value!.Unit);
                SelectUnit(UpperUnit, condition.UpperValue?.Unit);
                ValueText.Text = condition.Value.Number!.Value.ToString(CultureInfo.InvariantCulture);
                if (condition.UpperValue != null)
                    UpperText.Text = condition.UpperValue.Number!.Value.ToString(CultureInfo.InvariantCulture);
            }
            else if (condition.Operator == CatalogSearchOperator.OneOf)
            {
                ValueText.Text = string.Join(", ", condition.Values!
                    .Select(value => EscapeOneOfValue(value.Text ?? throw new ArgumentException("One-of value must be text."))));
            }
            else if (property.DataType == CatalogSearchDataType.Choice && property.Choices.Count > 0)
            {
                string value = condition.Value!.Text!;
                ValueChoice.SelectedItem = ValueChoice.Items.Cast<string>()
                    .First(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase));
            }
            else
                ValueText.Text = condition.Value!.Text ?? throw new ArgumentException("Search value must be text.");

            _upperUnitWasManuallyChanged = condition.Operator == CatalogSearchOperator.Between &&
                !Equals(Unit.SelectedItem, UpperUnit.SelectedItem);
        }
        finally { _rebuilding = false; }
    }

    private void SelectUnit(string? unit)
    {
        if (unit == null)
        {
            if (Unit.Items.Count > 0) Unit.SelectedIndex = 0;
            return;
        }

        string selected = Unit.Items.Cast<string>()
            .FirstOrDefault(value => value.Equals(unit, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unit '{unit}' is not available for {SelectedProperty.Id}.");
        Unit.SelectedItem = selected;
    }

    private static string[] ParseOneOfValues(string text)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\\' && i + 1 < text.Length && text[i + 1] == ',')
            {
                current.Append(',');
                i++;
            }
            else if (character == ',')
            {
                AddValue();
            }
            else
                current.Append(character);
        }
        AddValue();
        return values.ToArray();

        void AddValue()
        {
            string value = current.ToString().Trim();
            if (value.Length > 0) values.Add(value);
            current.Clear();
        }
    }

    private static string EscapeOneOfValue(string value) =>
        value.Replace(",", "\\,", StringComparison.Ordinal);

    private void RebuildOperators(bool resetValues)
    {
        if (Property.SelectedItem is not PropertyChoice choice) return;
        bool wasRebuilding = _rebuilding;
        _rebuilding = true;
        _upperUnitWasManuallyChanged = false;
        Operator.Items.Clear();
        foreach (CatalogSearchOperator operation in choice.Info.Operators)
            Operator.Items.Add(new OperatorChoice(operation));
        Operator.SelectedIndex = 0;
        Unit.Items.Clear();
        foreach (string unit in choice.Info.Units)
            Unit.Items.Add(unit);
        if (Unit.Items.Count > 0) Unit.SelectedIndex = 0;
        UpperUnit.Items.Clear();
        foreach (string unit in choice.Info.Units)
            UpperUnit.Items.Add(unit);
        if (UpperUnit.Items.Count > 0) UpperUnit.SelectedIndex = 0;
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
        ValueText.MaxLength = operation == CatalogSearchOperator.OneOf
            ? CatalogSearchDefinitionValidator.MaximumTextLength * CatalogSearchDefinitionValidator.MaximumOneOfValues * 2 +
              CatalogSearchDefinitionValidator.MaximumOneOfValues
            : CatalogSearchDefinitionValidator.MaximumTextLength;
        ValueText.Visible = hasValue && (property.DataType is CatalogSearchDataType.Number or CatalogSearchDataType.Text ||
            property.DataType == CatalogSearchDataType.Choice && !choiceList);
        ValueChoice.Visible = hasValue && choiceList;
        ValueDate.Visible = hasValue && property.DataType == CatalogSearchDataType.Date;
        UpperText.Visible = hasValue && range && property.DataType == CatalogSearchDataType.Number;
        UpperDate.Visible = hasValue && range && property.DataType == CatalogSearchDataType.Date;
        Unit.Visible = hasValue && property.DataType == CatalogSearchDataType.Number;
        UpperUnit.Visible = hasValue && range && property.DataType == CatalogSearchDataType.Number;
        ValueText.PlaceholderText = operation == CatalogSearchOperator.OneOf ? "Comma-separated values (\\, for comma)" : "Value";
    }

    private void SelectUnit(ComboBox target, string? unit)
    {
        if (unit == null)
        {
            if (target.Items.Count > 0) target.SelectedIndex = 0;
            return;
        }

        string selected = target.Items.Cast<string>()
            .FirstOrDefault(value => value.Equals(unit, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unit '{unit}' is not available for {SelectedProperty.Id}.");
        target.SelectedItem = selected;
    }

    private void SignalChanged(bool immediate)
    {
        if (!_rebuilding) Changed?.Invoke(immediate);
    }
}
