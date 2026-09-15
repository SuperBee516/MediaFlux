using MediaFlux.Models;

namespace MediaFlux.Models;

/// <summary>Formats structured quality-policy results without recalculating them.</summary>
public static class EncodingQualityPresentation
{
    public static IReadOnlyList<EncodingPlanItem> CreateItems(EncodingQualityResolution? resolution)
    {
        if (resolution == null)
            return Array.Empty<EncodingPlanItem>();

        if (resolution.IsSupersededByTargetSize)
        {
            string configuredTarget = resolution.Intent.Target?.ToString() ?? "Configured quality";
            return
            [
                new("Quality target", configuredTarget),
                new("Quality mode", "Target Size / Bitrate"),
                new("Effective quality", "Not used"),
                new("Quality explanation", "Target-size mode supersedes constant-quality mode.")
            ];
        }

        bool automatic = resolution.Intent.Kind == EncodingQualityIntentKind.QualityTarget;
        string target = automatic
            ? resolution.Intent.Target!.Value.ToString()
            : "Legacy numeric";
        string mechanism = Mechanism(resolution.Mechanism);
        string effective = resolution.EffectiveQuality is { } value
            ? $"{mechanism} {value}"
            : "Not used";
        var items = new List<EncodingPlanItem>
        {
            new("Quality target", target),
            new("Quality mode", automatic ? "Automatic / Source Adaptive" : "Manual / Legacy Numeric"),
            new("Effective quality", effective),
            new("Mechanism", mechanism)
        };
        if (automatic && resolution.Assessment != EncodingQualityAssessment.Unknown)
            items.Add(new("Source assessment", Assessment(resolution.Assessment)));
        return items;
    }

    public static IReadOnlyList<string> CreateReasons(EncodingQualityResolution? resolution) =>
        resolution?.Reasons.Select(reason => DescribeReason(reason)).ToArray() ?? Array.Empty<string>();

    public static string BuildCompactSummary(EncodingQualityResolution? resolution)
    {
        if (resolution == null)
            return string.Empty;
        if (resolution.IsSupersededByTargetSize)
            return "Target Size / Bitrate (quality superseded)";
        string target = resolution.Intent.Target?.ToString() ?? "Legacy numeric";
        string value = resolution.EffectiveQuality is { } quality
            ? $"{Mechanism(resolution.Mechanism)} {quality}"
            : "Not used";
        return $"{target} • {value}";
    }

    public static string Mechanism(EncoderQualityMechanism mechanism) => mechanism switch
    {
        EncoderQualityMechanism.Cq => "CQ",
        EncoderQualityMechanism.Crf => "CRF",
        EncoderQualityMechanism.Icq => "ICQ",
        _ => "Quality"
    };

    private static string Assessment(EncodingQualityAssessment assessment) => assessment switch
    {
        EncodingQualityAssessment.CompressedSource => "Compressed source",
        EncodingQualityAssessment.TypicalSource => "Typical source",
        EncodingQualityAssessment.HighQualitySource => "High-quality source",
        _ => "Unknown"
    };

    private static string DescribeReason(EncodingQualityReason reason) => reason.Code switch
    {
        EncodingQualityReasonCode.QualityTargetSelected => $"Selected target: {reason.Description}",
        EncodingQualityReasonCode.ProviderDefaultBaseline => "The selected encoder default established the quality baseline.",
        EncodingQualityReasonCode.CompressedSource => "Low source density permits greater compression.",
        EncodingQualityReasonCode.HighQualitySource => "High source density favors additional preservation.",
        EncodingQualityReasonCode.HighResolutionSource => "High-resolution source favors additional preservation.",
        EncodingQualityReasonCode.Downscale => "Planned downscale reduces the required preservation setting.",
        EncodingQualityReasonCode.TargetSizeSupersedesQuality => "Target-size bitrate mode supersedes constant quality.",
        EncodingQualityReasonCode.LegacyNumericIntent => "Existing numeric quality intent is retained.",
        EncodingQualityReasonCode.ProviderNormalized => "The encoder provider normalized the result to its legal range.",
        _ => reason.Description
    };
}
