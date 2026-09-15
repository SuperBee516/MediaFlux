namespace MediaFlux.Models;

/// <summary>Persistent user intent for source-adaptive constant-quality encoding.</summary>
public enum QualityTarget
{
    SmallerFile,
    Efficient,
    Balanced,
    HighQuality,
    MaximumQuality
}

public enum EncodingQualityIntentKind
{
    LegacyNumeric,
    QualityTarget
}

/// <summary>
/// Describes user intent without treating a derived encoder quality value as a
/// persistent preference. Existing numeric settings remain explicit legacy intent.
/// </summary>
public sealed record EncodingQualityIntent(
    EncodingQualityIntentKind Kind,
    int? LegacyQualityValue = null,
    QualityTarget? Target = null)
{
    public static EncodingQualityIntent LegacyNumeric(int? value) =>
        new(EncodingQualityIntentKind.LegacyNumeric, value);

    public static EncodingQualityIntent Automatic(QualityTarget target) =>
        new(EncodingQualityIntentKind.QualityTarget, Target: target);
}

public enum EncoderQualityMechanism { Crf, Cq, Icq, Unknown }

public enum EncodingQualityAssessment
{
    Unknown,
    CompressedSource,
    TypicalSource,
    HighQualitySource
}

public enum EncodingQualityReasonCode
{
    LegacyNumericIntent,
    QualityTargetSelected,
    TargetSizeSupersedesQuality,
    ProviderDefaultBaseline,
    CompressedSource,
    HighQualitySource,
    HighResolutionSource,
    Downscale,
    ProviderNormalized
}

public sealed record EncodingQualityReason(
    EncodingQualityReasonCode Code,
    string Description);

/// <summary>
/// Immutable, source/configuration-specific resolution consumed by execution and
/// retained by the encoding plan for later UI presentation.
/// </summary>
public sealed record EncodingQualityResolution(
    EncodingQualityIntent Intent,
    int? EffectiveQuality,
    EncoderQualityMechanism Mechanism,
    EncodingQualityAssessment Assessment,
    bool IsSupersededByTargetSize,
    IReadOnlyList<EncodingQualityReason> Reasons);
