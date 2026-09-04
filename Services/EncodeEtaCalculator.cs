namespace MediaFlux.Services;

/// <summary>Calculates encode ETA from the same media-time and FFmpeg speed values used for progress.</summary>
public static class EncodeEtaCalculator
{
    public static double? CalculateSeconds(double authoritativeDurationSeconds, double encodedMediaSeconds, double ffmpegSpeed)
    {
        if (!double.IsFinite(authoritativeDurationSeconds) || authoritativeDurationSeconds <= 0 ||
            !double.IsFinite(encodedMediaSeconds) || !double.IsFinite(ffmpegSpeed) || ffmpegSpeed <= 0)
            return null;

        double remaining = Math.Max(0, authoritativeDurationSeconds - Math.Max(0, encodedMediaSeconds));
        double eta = remaining / ffmpegSpeed;
        return double.IsFinite(eta) && eta >= 0 ? eta : null;
    }

    public static double? CalculateAggregateSeconds(double remainingMediaSeconds, double combinedSpeed)
    {
        if (!double.IsFinite(remainingMediaSeconds) || remainingMediaSeconds < 0 ||
            !double.IsFinite(combinedSpeed) || combinedSpeed <= 0)
            return null;
        double eta = remainingMediaSeconds / combinedSpeed;
        return double.IsFinite(eta) && eta >= 0 ? eta : null;
    }
}
