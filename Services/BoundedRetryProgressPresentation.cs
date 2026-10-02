using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Small user-facing projection of a frozen bounded-retry execution plan.</summary>
public static class BoundedRetryProgressPresentation
{
    public static bool IsRetryPlan(EncodingPlan? plan) =>
        plan?.Quality?.Reasons.Any(reason => reason.Code == EncodingQualityReasonCode.AdaptiveStorageSavingsRetry) == true;

    public static string QualityLabel(EncodingQualityResolution quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        string mechanism = quality.Mechanism switch
        {
            EncoderQualityMechanism.Cq => "CQ",
            EncoderQualityMechanism.Crf => "CRF",
            EncoderQualityMechanism.Icq => "ICQ",
            _ => "Quality"
        };
        return $"{mechanism}{quality.EffectiveQuality?.ToString() ?? "Unavailable"}";
    }

    public static string RetryStatus(EncodingQualityResolution quality) =>
        $"Retrying at {QualityLabel(quality)} — attempt {AdaptiveStorageSavingsRetryLimits.RetryAttemptNumber} of {AdaptiveStorageSavingsRetryLimits.MaximumProductionAttempts}";

    public static string RetryQualityCell(EncodingQualityResolution quality) =>
        $"{QualityLabel(quality)} executing (attempt {AdaptiveStorageSavingsRetryLimits.RetryAttemptNumber} of {AdaptiveStorageSavingsRetryLimits.MaximumProductionAttempts})";
}
