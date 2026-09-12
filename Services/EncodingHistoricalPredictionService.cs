using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Read-only, deterministic estimates from finalized statistics observations.</summary>
public sealed class EncodingHistoricalPredictionService
{
    private readonly Func<IReadOnlyList<EncodingStatisticsRecord>> _history;
    public EncodingHistoricalPredictionService(EncodingStatisticsService statistics) : this(statistics.GetAll) { }
    public EncodingHistoricalPredictionService(Func<IReadOnlyList<EncodingStatisticsRecord>> history) => _history = history;

    public EncodingHistoricalPrediction Predict(EncodingDecisionContext context) => Predict(context, _history());
    public static EncodingHistoricalPrediction Predict(EncodingDecisionContext context, IEnumerable<EncodingStatisticsRecord> history)
    {
        string codec = Family(context.Encoder.FfmpegCodec);
        string encoder = Normalize(context.Encoder.EncoderId);
        string hardware = context.UseGpu ? Normalize(HardwarePerformanceService.DetectGpuIdentity()) : "cpu";
        string tier = EncodingRuntimeEstimatorService.ResolutionTier(context.Source.Streams.FirstOrDefault(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase))?.Height);
        EncodingStatisticsRecord[] usable = history.Where(record => record.Outcome == EncodingStatisticsOutcome.Success && !record.IsSampleJob && !record.RecoveredSuccessful && record.MediaDurationSeconds is > 0 && record.ProcessingSeconds > 0 && record.OutputSizeBytes is > 0 && record.SourceSizeBytes is > 0).ToArray();
        for (int tierMatch = 1; tierMatch <= 3; tierMatch++)
        {
            EncodingStatisticsRecord[] matches = usable.Where(record =>
                Family(record.Codec) == codec && Normalize(record.EncoderId) == encoder &&
                (tierMatch >= 3
                    ? string.IsNullOrWhiteSpace(record.HardwareKey) || Normalize(record.HardwareKey) == hardware
                    : Normalize(record.HardwareKey) == hardware) &&
                (tierMatch >= 2 || string.Equals(record.OutputResolutionTier, tier, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length > 0) return Aggregate(context, matches, tierMatch);
        }
        return new(0, 0, EncodingHistoricalConfidence.None, null, null, null, null, null, null, null, null, null, null, "InsufficientComparableHistory");
    }

    private static EncodingHistoricalPrediction Aggregate(EncodingDecisionContext context, EncodingStatisticsRecord[] matches, int tier)
    {
        double[] speeds = Trim(matches.Select(record => record.MediaDurationSeconds!.Value / record.ProcessingSeconds).OrderBy(value => value).ToArray());
        double[] ratios = Trim(matches.Select(record => record.OutputSizeBytes!.Value / (double)record.SourceSizeBytes!.Value).OrderBy(value => value).ToArray());
        double median = Percentile(speeds, .5), low = Percentile(speeds, .25), high = Percentile(speeds, .75);
        TimeSpan duration = context.KnownDuration > TimeSpan.Zero ? TimeSpan.FromSeconds(context.KnownDuration.TotalSeconds / median) : TimeSpan.Zero;
        TimeSpan slow = context.KnownDuration > TimeSpan.Zero ? TimeSpan.FromSeconds(context.KnownDuration.TotalSeconds / low) : TimeSpan.Zero;
        TimeSpan fast = context.KnownDuration > TimeSpan.Zero ? TimeSpan.FromSeconds(context.KnownDuration.TotalSeconds / high) : TimeSpan.Zero;
        double? sourceMb = context.Input.Kind == EncodingInputKind.File && File.Exists(context.Input.SourcePath) ? new FileInfo(context.Input.SourcePath).Length / 1048576d : null;
        double ratio = Percentile(ratios, .5);
        EncodingHistoricalConfidence confidence = tier == 1 && matches.Length >= 10 ? EncodingHistoricalConfidence.High : speeds.Length >= 5 && tier <= 2 ? EncodingHistoricalConfidence.Medium : EncodingHistoricalConfidence.Low;
        return new(speeds.Length, tier, confidence, median, low, high, duration == TimeSpan.Zero ? null : duration, fast == TimeSpan.Zero ? null : fast, slow == TimeSpan.Zero ? null : slow, sourceMb * ratio, sourceMb * Percentile(ratios, .25), sourceMb * Percentile(ratios, .75), ratio, $"Tier{tier} finalized clean observations");
    }
    private static double[] Trim(double[] values) => values.Length < 5 ? values : values.Skip(1).Take(values.Length - 2).ToArray();
    private static double Percentile(IReadOnlyList<double> values, double p) { double x = (values.Count - 1) * p; int a = (int)Math.Floor(x), b = (int)Math.Ceiling(x); return a == b ? values[a] : values[a] + (values[b] - values[a]) * (x - a); }
    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant().Replace(" ", "");
    private static string Family(string? value) { string v = Normalize(value); return v.Contains("hevc") || v.Contains("265") ? "hevc" : v.Contains("264") || v.Contains("avc") ? "h264" : v.Contains("av1") ? "av1" : v; }
}
