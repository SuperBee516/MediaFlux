namespace MediaFlux.Models;

public readonly record struct RationalFrameRate(long Numerator, long Denominator)
{
    public bool IsValid => Numerator > 0 && Denominator > 0;
    public double FramesPerSecond => IsValid ? (double)Numerator / Denominator : 0;
    public string Text => $"{Numerator}/{Denominator}";

    public static bool TryParse(string? value, out RationalFrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] parts = value.Trim().Split('/');
        long denominator = 1;
        if ((parts.Length != 1 && parts.Length != 2) ||
            !long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long numerator) ||
            (parts.Length == 2 && !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out denominator)) ||
            numerator <= 0 || denominator <= 0)
        {
            return false;
        }

        long gcd = GreatestCommonDivisor(numerator, denominator);
        rate = new(numerator / gcd, denominator / gcd);
        return true;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Abs(left);
    }
}

public sealed record SourceTimelinePacket(
    double? PresentationTimeSeconds,
    double? DecodeTimeSeconds,
    double? DurationSeconds);

public sealed record SourceTimelineEvidence(
    RationalFrameRate? NominalFrameRate,
    long? SourceFrameCount,
    double? AuthoritativeDurationSeconds,
    double? StartTimeSeconds,
    IReadOnlyList<SourceTimelinePacket> Packets);

public enum SourceTimelineRecoveryClassification
{
    None,
    LocalizedSourceTimelineCorruption
}

public sealed record SourceTimelineRecoveryAnalysis(
    SourceTimelineRecoveryClassification Classification,
    string Reason,
    RationalFrameRate? NominalFrameRate = null,
    long? SourceFrameCount = null,
    int PacketCount = 0,
    int StablePacketCount = 0,
    int ShortDurationPacketCount = 0,
    int ShortRegionStart = -1,
    int ShortRegionEnd = -1,
    double ShortRegionDurationSeconds = 0,
    double CompensatingDurationSeconds = 0,
    double ReconstructedDurationSeconds = 0,
    double AuthoritativeDurationSeconds = 0)
{
    public bool IsEligible => Classification == SourceTimelineRecoveryClassification.LocalizedSourceTimelineCorruption;
    public string DescribeEvidence() =>
        $"classification={Classification}; rate={NominalFrameRate?.Text ?? "unknown"}; " +
        $"source-frames={SourceFrameCount?.ToString() ?? "unknown"}; packets={PacketCount}; " +
        $"stable={StablePacketCount}; short-region={ShortDurationPacketCount} " +
        $"[{ShortRegionStart}-{ShortRegionEnd}]/{ShortRegionDurationSeconds:0.######}s; " +
        $"compensating={CompensatingDurationSeconds:0.######}s; " +
        $"reconstructed-duration={ReconstructedDurationSeconds:0.######}s; " +
        $"authoritative-duration={AuthoritativeDurationSeconds:0.######}s";
}
