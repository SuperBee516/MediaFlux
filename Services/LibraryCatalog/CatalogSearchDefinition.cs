using System.Globalization;
using System.Text.Json.Serialization;

namespace MediaFlux.Services.LibraryCatalog
{
    [JsonConverter(typeof(JsonStringEnumConverter<CatalogSearchOperator>))]
    public enum CatalogSearchOperator
    {
        Equal,
        NotEqual,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
        Between,
        Is,
        IsNot,
        OneOf,
        Contains,
        NotContains,
        StartsWith,
        NotStartsWith,
        Known,
        Unknown,
        Yes,
        No
    }

    public enum CatalogSearchDataType
    {
        Text,
        Number,
        Date,
        Choice,
        Boolean
    }

    // Exactly one of Text, Number, or Date is set for a condition value.
    // Unit applies only to Number and is a stable, invariant identifier.
    public sealed record CatalogSearchValue(
        string? Text = null,
        decimal? Number = null,
        DateOnly? Date = null,
        string? Unit = null)
    {
        public static CatalogSearchValue ParseNumber(string text, string? unit = null)
        {
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
                throw new ArgumentException("A finite, invariant-culture number is required.", nameof(text));
            return new CatalogSearchValue(Number: number, Unit: unit);
        }
    }

    public sealed record CatalogSearchCondition(
        string PropertyId,
        CatalogSearchOperator Operator,
        CatalogSearchValue? Value = null,
        CatalogSearchValue? UpperValue = null,
        IReadOnlyList<CatalogSearchValue>? Values = null);

    public sealed record CatalogSearchSort(string PropertyId, bool Descending = false);

    // Saved definitions contain durable intent. Offset and page size belong to
    // LibraryFileQuery, and selection/layout state belongs to the future UI.
    public sealed record CatalogSearchDefinition(
        int Version,
        IReadOnlyList<CatalogSearchCondition> Conditions,
        string Search = "",
        long? LocationId = null,
        IndexedFileAvailability? Availability = null,
        LibraryProbeStatus? ProbeStatus = null,
        CatalogSearchSort? Sort = null)
    {
        public const int CurrentVersion = 1;
    }

    public sealed record CatalogSearchPropertyInfo(
        string Id,
        string DisplayName,
        CatalogSearchDataType DataType,
        IReadOnlyList<CatalogSearchOperator> Operators,
        IReadOnlyList<string> Units,
        IReadOnlyList<string> Choices,
        bool CanSort,
        int RequiredMetadataVersion);

    public sealed record CatalogSearchProjection(
        string MetadataState,
        long? VideoBitRateBps,
        double? EffectiveFps,
        double? AverageFps,
        double? NominalFps,
        string? FrameRateBasis,
        int? BitDepth,
        int? VideoStreamCount,
        int? AudioStreamCount,
        int? SubtitleStreamCount,
        double? PixelsPerSecond,
        double? SourceVideoBpp,
        string? CodedOrientation,
        string? ResolutionClass);
}
