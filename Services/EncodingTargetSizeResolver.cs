using System.Globalization;

namespace MediaFlux.Services;

/// <summary>
/// Resolves the configured manual target only. Auto estimates are deliberately
/// resolved per source by the existing metadata estimator.
/// </summary>
internal static class EncodingTargetSizeResolver
{
    public static double? ResolveConfiguredManualTargetMb(
        bool autoTargetSize,
        string? configuredManualTarget)
    {
        if (autoTargetSize ||
            !double.TryParse(
                configuredManualTarget,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double targetMb) ||
            !double.IsFinite(targetMb) ||
            targetMb <= 0)
        {
            return null;
        }

        return targetMb;
    }
}
