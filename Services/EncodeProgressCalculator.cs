namespace MediaFlux.Services;

/// <summary>Shared monotonic percentage calculation for the queue and current operation.</summary>
public static class EncodeProgressCalculator
{
    public static int CalculatePercent(double encodedMediaSeconds, double? expectedDurationSeconds, int previousPercent = 0)
    {
        return (int)Math.Round(CalculatePercentValue(encodedMediaSeconds, expectedDurationSeconds, previousPercent), MidpointRounding.AwayFromZero);
    }

    public static double CalculatePercentValue(double encodedMediaSeconds, double? expectedDurationSeconds, double previousPercent = 0)
    {
        if (expectedDurationSeconds is not > 0 || !double.IsFinite(encodedMediaSeconds))
            return Math.Clamp(previousPercent, 0, 100);

        double percent =
            Math.Clamp(Math.Max(0, encodedMediaSeconds) / expectedDurationSeconds.Value * 100d, 0, 100);
        return Math.Clamp(Math.Max(previousPercent, percent), 0, 100);
    }
}
