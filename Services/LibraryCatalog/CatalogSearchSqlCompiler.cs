using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    internal sealed record CatalogSearchParameter(string Name, object Value);

    internal sealed record CompiledCatalogSearch(
        string PredicateSql,
        IReadOnlyList<CatalogSearchParameter> Parameters,
        string? OrderExpression,
        bool Descending)
    {
        internal void Bind(SqliteCommand command)
        {
            foreach (CatalogSearchParameter parameter in Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    public static class CatalogSearchDefinitionValidator
    {
        public const int MaximumConditions = 32;
        public const int MaximumOneOfValues = 16;
        public const int MaximumTextLength = 512;

        public static void Validate(CatalogSearchDefinition definition) =>
            _ = CatalogSearchSqlCompiler.Compile(definition);
    }

    internal static class CatalogSearchSqlCompiler
    {
        internal static CompiledCatalogSearch Compile(CatalogSearchDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.Version != CatalogSearchDefinition.CurrentVersion)
                throw new ArgumentException($"Unsupported catalog search definition version {definition.Version}.", nameof(definition));
            if (definition.Conditions == null || definition.Conditions.Count > CatalogSearchDefinitionValidator.MaximumConditions)
                throw new ArgumentException("The search has too many conditions or no condition list.", nameof(definition));
            if (definition.LocationId is <= 0)
                throw new ArgumentException("Location ID must be positive.", nameof(definition));
            if (definition.Availability.HasValue && !Enum.IsDefined(definition.Availability.Value))
                throw new ArgumentException("Unknown catalog availability.", nameof(definition));
            if (definition.ProbeStatus.HasValue && !Enum.IsDefined(definition.ProbeStatus.Value))
                throw new ArgumentException("Unknown probe status.", nameof(definition));

            var predicates = new List<string>();
            var parameters = new List<CatalogSearchParameter>();
            int parameterNumber = 0;
            int requiredMetadataVersion = 0;
            string AddParameter(object value)
            {
                string name = $"$advanced_{parameterNumber++}";
                parameters.Add(new CatalogSearchParameter(name, value));
                return name;
            }

            string search = (definition.Search ?? "").Trim();
            ValidateTextLength(search);
            if (search.Length > 0)
            {
                string name = AddParameter("%" + EscapeLike(search) + "%");
                predicates.Add($"(file.file_name LIKE {name} ESCAPE '\\' OR file.full_path LIKE {name} ESCAPE '\\')");
            }
            if (definition.LocationId.HasValue)
            {
                string name = AddParameter(definition.LocationId.Value);
                predicates.Add($"EXISTS(SELECT 1 FROM file_location_memberships advanced_location WHERE advanced_location.file_id=file.id AND advanced_location.location_id={name})");
            }
            if (definition.Availability.HasValue)
                predicates.Add($"file.availability_state={AddParameter((int)definition.Availability.Value)}");
            if (definition.ProbeStatus.HasValue)
                predicates.Add($"COALESCE(metadata.probe_status,0)={AddParameter((int)definition.ProbeStatus.Value)}");

            foreach (CatalogSearchCondition condition in definition.Conditions)
            {
                if (condition == null)
                    throw new ArgumentException("Search conditions cannot be null.", nameof(definition));
                CatalogSearchPropertyDefinition property = CatalogSearchRegistry.Get(condition.PropertyId);
                if (!property.Info.Operators.Contains(condition.Operator))
                    throw new ArgumentException($"Operator {condition.Operator} is invalid for {condition.PropertyId}.", nameof(definition));
                ValidateOperands(condition, property);
                requiredMetadataVersion = Math.Max(requiredMetadataVersion, property.Info.RequiredMetadataVersion);
                predicates.Add(CompileCondition(property, condition, AddParameter));
            }

            if (requiredMetadataVersion > 0)
                predicates.Insert(0, LibraryCatalogSqlExpressions.Current(requiredMetadataVersion));

            string? orderExpression = null;
            bool descending = false;
            if (definition.Sort is { } sort)
            {
                CatalogSearchPropertyDefinition property = CatalogSearchRegistry.Get(sort.PropertyId);
                if (!property.Info.CanSort || property.SqlKind != CatalogSearchSqlKind.Scalar)
                    throw new ArgumentException($"Property {sort.PropertyId} cannot be sorted.", nameof(definition));
                orderExpression = property.Info.RequiredMetadataVersion > 0
                    ? LibraryCatalogSqlExpressions.Guard(property.Expression, property.Info.RequiredMetadataVersion)
                    : property.Expression;
                descending = sort.Descending;
            }

            return new CompiledCatalogSearch(
                predicates.Count == 0 ? "" : " AND " + string.Join(" AND ", predicates.Select(value => $"({value})")),
                parameters, orderExpression, descending);
        }

        private static string CompileCondition(
            CatalogSearchPropertyDefinition property,
            CatalogSearchCondition condition,
            Func<object, string> addParameter)
        {
            if (property.SqlKind == CatalogSearchSqlKind.LocationMembership)
                return CompileLocation(condition, addParameter, property);
            if (property.SqlKind == CatalogSearchSqlKind.JsonCodec)
                return CompileJsonCodec(condition, addParameter, property);

            string expression = property.Expression;
            if (condition.Operator == CatalogSearchOperator.Known)
                return $"({expression}) IS NOT NULL";
            if (condition.Operator == CatalogSearchOperator.Unknown)
                return $"({expression}) IS NULL";
            if (condition.Operator is CatalogSearchOperator.Yes or CatalogSearchOperator.No)
                return $"({expression})={(condition.Operator == CatalogSearchOperator.Yes ? 1 : 0)}";
            if (property.Info.DataType == CatalogSearchDataType.Date)
                return CompileDate(expression, condition, addParameter);

            if (condition.Operator == CatalogSearchOperator.Between)
            {
                object lower = ConvertValue(property, condition.Value!);
                object upper = ConvertValue(property, condition.UpperValue!);
                if (CompareNumbers(lower, upper) > 0)
                    throw new ArgumentException("Between bounds must be in ascending order.");
                return $"({expression}) BETWEEN {addParameter(lower)} AND {addParameter(upper)}";
            }
            if (condition.Operator == CatalogSearchOperator.OneOf)
            {
                string[] names = condition.Values!.Select(value => addParameter(ConvertValue(property, value))).ToArray();
                return $"({expression}) COLLATE NOCASE IN ({string.Join(",", names)})";
            }

            object converted = ConvertValue(property, condition.Value!);
            if (condition.Operator is CatalogSearchOperator.Contains or CatalogSearchOperator.NotContains or
                CatalogSearchOperator.StartsWith or CatalogSearchOperator.NotStartsWith)
            {
                string escaped = EscapeLike((string)converted);
                string pattern = condition.Operator is CatalogSearchOperator.Contains or CatalogSearchOperator.NotContains
                    ? "%" + escaped + "%" : escaped + "%";
                string operation = condition.Operator is CatalogSearchOperator.NotContains or CatalogSearchOperator.NotStartsWith
                    ? "NOT LIKE" : "LIKE";
                return $"({expression}) {operation} {addParameter(pattern)} ESCAPE '\\'";
            }

            string comparison = condition.Operator switch
            {
                CatalogSearchOperator.Equal or CatalogSearchOperator.Is => "=",
                CatalogSearchOperator.NotEqual or CatalogSearchOperator.IsNot => "<>",
                CatalogSearchOperator.LessThan => "<",
                CatalogSearchOperator.LessThanOrEqual => "<=",
                CatalogSearchOperator.GreaterThan => ">",
                CatalogSearchOperator.GreaterThanOrEqual => ">=",
                _ => throw new ArgumentException("Unsupported search operator.")
            };
            string collation = property.Info.DataType is CatalogSearchDataType.Text or CatalogSearchDataType.Choice
                && converted is string ? " COLLATE NOCASE" : "";
            return $"({expression}){collation} {comparison} {addParameter(converted)}";
        }

        private static string CompileLocation(
            CatalogSearchCondition condition,
            Func<object, string> addParameter,
            CatalogSearchPropertyDefinition property)
        {
            const string source = "SELECT 1 FROM file_location_memberships advanced_membership WHERE advanced_membership.file_id=file.id";
            if (condition.Operator == CatalogSearchOperator.Known)
                return $"EXISTS({source})";
            if (condition.Operator == CatalogSearchOperator.Unknown)
                return $"NOT EXISTS({source})";
            string locationId = addParameter(ConvertValue(property, condition.Value!));
            string match = $"EXISTS({source} AND advanced_membership.location_id={locationId})";
            return condition.Operator == CatalogSearchOperator.NotEqual
                ? $"EXISTS({source}) AND NOT {match}"
                : match;
        }

        private static string CompileJsonCodec(
            CatalogSearchCondition condition,
            Func<object, string> addParameter,
            CatalogSearchPropertyDefinition property)
        {
            string[] names = condition.Operator == CatalogSearchOperator.OneOf
                ? condition.Values!.Select(value => addParameter(ConvertValue(property, value))).ToArray()
                : new[] { addParameter(ConvertValue(property, condition.Value!)) };
            string safeJson = $"CASE WHEN ({LibraryCatalogSqlExpressions.JsonArrayContainsOnlyStreamObjects(property.Expression)}) " +
                $"THEN {property.Expression} ELSE '[]' END";
            return $"EXISTS(SELECT 1 FROM json_each({safeJson}) advanced_stream " +
                   $"WHERE lower(CAST(CASE WHEN advanced_stream.type='object' " +
                   $"THEN json_extract(advanced_stream.value,'$.codec') END AS TEXT)) " +
                   $"IN ({string.Join(",", names.Select(name => $"lower({name})"))}))";
        }

        private static string CompileDate(string expression, CatalogSearchCondition condition, Func<object, string> addParameter)
        {
            long lower = StartTicks(condition.Value!);
            long upper = checked(lower + TimeSpan.TicksPerDay);
            return condition.Operator switch
            {
                CatalogSearchOperator.Equal => $"({expression}) >= {addParameter(lower)} AND ({expression}) < {addParameter(upper)}",
                CatalogSearchOperator.NotEqual => $"(({expression}) < {addParameter(lower)} OR ({expression}) >= {addParameter(upper)})",
                CatalogSearchOperator.LessThan => $"({expression}) < {addParameter(lower)}",
                CatalogSearchOperator.LessThanOrEqual => $"({expression}) < {addParameter(upper)}",
                CatalogSearchOperator.GreaterThan => $"({expression}) >= {addParameter(upper)}",
                CatalogSearchOperator.GreaterThanOrEqual => $"({expression}) >= {addParameter(lower)}",
                CatalogSearchOperator.Between => CompileDateBetween(expression, condition, lower, addParameter),
                _ => throw new ArgumentException("Unsupported date operator.")
            };
        }

        private static string CompileDateBetween(string expression, CatalogSearchCondition condition,
            long lower, Func<object, string> addParameter)
        {
            long upper = StartTicks(condition.UpperValue!);
            if (lower > upper)
                throw new ArgumentException("Between bounds must be in ascending order.");
            return $"({expression}) >= {addParameter(lower)} AND ({expression}) < {addParameter(checked(upper + TimeSpan.TicksPerDay))}";
        }

        private static long StartTicks(CatalogSearchValue value) =>
            value.Date!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Ticks;

        private static int CompareNumbers(object lower, object upper) =>
            Convert.ToDecimal(lower, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDecimal(upper, CultureInfo.InvariantCulture));

        private static object ConvertValue(CatalogSearchPropertyDefinition property, CatalogSearchValue value)
        {
            switch (property.Info.DataType)
            {
                case CatalogSearchDataType.Number:
                    if (value.Number is not { } number || value.Text != null || value.Date != null)
                        throw new ArgumentException("A typed numeric value is required.");
                    string unit = value.Unit ?? property.UnitFactors!.Keys.First();
                    if (!property.UnitFactors!.TryGetValue(unit, out decimal factor))
                        throw new ArgumentException($"Unit '{unit}' is invalid for {property.Info.Id}.");
                    decimal native;
                    try { native = checked(number * factor); }
                    catch (OverflowException ex) { throw new ArgumentException("Numeric value is out of range.", ex); }
                    if (property.Integral)
                    {
                        if (native != decimal.Truncate(native) || native < long.MinValue || native > long.MaxValue)
                            throw new ArgumentException("An integral value within the SQLite range is required.");
                        return (long)native;
                    }
                    double real = (double)native;
                    if (!double.IsFinite(real))
                        throw new ArgumentException("A finite number is required.");
                    return real;
                case CatalogSearchDataType.Text:
                case CatalogSearchDataType.Choice:
                    if (value.Text == null || value.Number != null || value.Date != null || value.Unit != null)
                        throw new ArgumentException("A typed text value is required.");
                    string text = value.Text.Trim();
                    ValidateTextLength(text);
                    if (text.Length == 0)
                        throw new ArgumentException("Search text cannot be empty.");
                    if (property.ChoiceValues != null)
                    {
                        if (!property.ChoiceValues.TryGetValue(text, out object? choice))
                            throw new ArgumentException($"'{text}' is invalid for {property.Info.Id}.");
                        return choice;
                    }
                    return text;
                default:
                    throw new ArgumentException("This property does not accept a value.");
            }
        }

        private static void ValidateOperands(CatalogSearchCondition condition, CatalogSearchPropertyDefinition property)
        {
            bool nullary = condition.Operator is CatalogSearchOperator.Known or CatalogSearchOperator.Unknown or
                CatalogSearchOperator.Yes or CatalogSearchOperator.No;
            if (nullary)
            {
                if (condition.Value != null || condition.UpperValue != null || condition.Values != null)
                    throw new ArgumentException("This operator takes no values.");
                return;
            }
            if (condition.Operator == CatalogSearchOperator.OneOf)
            {
                if (condition.Value != null || condition.UpperValue != null || condition.Values == null ||
                    condition.Values.Count is < 1 or > CatalogSearchDefinitionValidator.MaximumOneOfValues ||
                    condition.Values.Any(value => value == null))
                    throw new ArgumentException("One-of requires 1 to 16 typed values.");
                return;
            }
            if (condition.Operator == CatalogSearchOperator.Between)
            {
                if (condition.Value == null || condition.UpperValue == null || condition.Values != null)
                    throw new ArgumentException("Between requires two typed bounds.");
            }
            else if (condition.Value == null || condition.UpperValue != null || condition.Values != null)
                throw new ArgumentException("This operator requires one typed value.");

            if (property.Info.DataType == CatalogSearchDataType.Date)
            {
                foreach (CatalogSearchValue value in new[] { condition.Value, condition.UpperValue }.OfType<CatalogSearchValue>())
                {
                    if (value.Date == null || value.Text != null || value.Number != null || value.Unit != null)
                        throw new ArgumentException("A typed UTC date is required.");
                }
            }
        }

        private static void ValidateTextLength(string text)
        {
            if (text.Length > CatalogSearchDefinitionValidator.MaximumTextLength)
                throw new ArgumentException("Search text is too long.");
        }

        internal static string EscapeLike(string text) =>
            text.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
