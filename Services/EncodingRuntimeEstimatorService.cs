using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record EncodingRuntimeWorkload(
    double SourceDurationSeconds,
    string EncoderId,
    string OutputCodec,
    string EncoderPreset,
    string SourceResolutionTier,
    string OutputResolutionTier,
    int OutputBitDepth,
    bool ScalingApplied,
    bool ConcurrentEncoderSessions);

public sealed record EncodingRuntimeEstimate
{
    public double? EstimatedProcessingSeconds { get; init; }
    public double? EstimatedSpeedX { get; init; }
    public double? FastProcessingSeconds { get; init; }
    public double? SlowProcessingSeconds { get; init; }
    public RuntimeEstimateConfidence Confidence { get; init; }
    public int SampleCount { get; init; }
    public int FallbackDepth { get; init; }
    public string CohortExplanation { get; init; } = "Insufficient successful encode history.";
    public bool IsAvailable => EstimatedProcessingSeconds is > 0;
}

public sealed record EncodingRuntimeBacktestResult(
    int PredictionCount,
    double? MedianAbsolutePercentageError,
    double? PercentWithin10,
    double? PercentWithin25);

public sealed class EncodingRuntimeEstimatorService
{
    private readonly Func<IReadOnlyList<EncodingStatisticsRecord>> _history;

    public EncodingRuntimeEstimatorService(EncodingStatisticsService statistics)
        : this(statistics.GetAll) { }

    public EncodingRuntimeEstimatorService(Func<IReadOnlyList<EncodingStatisticsRecord>> history) =>
        _history = history ?? throw new ArgumentNullException(nameof(history));

    public EncodingRuntimeEstimate Estimate(EncodingRuntimeWorkload workload) => Estimate(workload, _history());

    public string GetHistoryRevision()
    {
        IReadOnlyList<EncodingStatisticsRecord> records = _history();
        EncodingStatisticsRecord? latest = records.OrderByDescending(record => record.EndUtc).ThenBy(record => record.Id, StringComparer.Ordinal).FirstOrDefault();
        return latest == null ? "empty" : $"{records.Count}|{latest.EndUtc.Ticks}|{latest.Id}";
    }

    public EncodingRuntimeEstimate Estimate(LibraryPolicyEvaluationResult candidate)
    {
        EncodingRuntimeWorkload? workload = CreatePolicyWorkload(candidate);
        return workload == null ? Unknown("Policy candidate lacks duration or resolution metadata.") : Estimate(workload);
    }

    public static EncodingRuntimeWorkload? CreatePolicyWorkload(LibraryPolicyEvaluationResult candidate)
    {
        if (candidate.SourceDurationSeconds is not > 0 || candidate.SourceHeight is not > 0 || string.IsNullOrWhiteSpace(candidate.EncoderId)) return null;
        int outputHeight = candidate.PreserveSourceResolution || !candidate.MaximumOutputHeight.HasValue
            ? candidate.SourceHeight.Value : Math.Min(candidate.SourceHeight.Value, candidate.MaximumOutputHeight.Value);
        return new EncodingRuntimeWorkload(candidate.SourceDurationSeconds.Value, candidate.EncoderId,
            candidate.ProposedCodec.ToString(), candidate.EncoderPreset,
            ResolutionTier(candidate.SourceHeight), ResolutionTier(outputHeight), candidate.PreferredBitDepth,
            outputHeight != candidate.SourceHeight.Value, false);
    }

    public EncodingRuntimeEstimate Estimate(EncodingRuntimeWorkload workload, IEnumerable<EncodingStatisticsRecord> history)
    {
        if (workload.SourceDurationSeconds <= 0 || !double.IsFinite(workload.SourceDurationSeconds))
            return Unknown("Source media duration is unavailable.");

        Sample[] valid = history.Where(IsUseful).Select(ToSample).Where(sample => sample.SpeedX is >= 0.01 and <= 100)
            .OrderBy(sample => sample.Record.EndUtc).ToArray();
        if (valid.Length == 0) return Unknown("No successful, production-sized encode timing samples are available.");

        Sample[]? firstNonEmpty = null;
        int firstDepth = -1;
        for (int depth = 0; depth <= 5; depth++)
        {
            Sample[] cohort = valid.Where(sample => Matches(sample.Record, workload, depth)).ToArray();
            if (cohort.Length > 0 && firstNonEmpty == null) { firstNonEmpty = cohort; firstDepth = depth; }
            if (cohort.Length >= 3) return Aggregate(workload, cohort, depth);
        }
        return firstNonEmpty == null
            ? Unknown("No historical samples match the requested encoder and codec.")
            : Aggregate(workload, firstNonEmpty, firstDepth);
    }

    public EncodingRuntimeBacktestResult Backtest(IEnumerable<EncodingStatisticsRecord> records)
    {
        EncodingStatisticsRecord[] ordered = records.Where(IsUseful).OrderBy(record => record.EndUtc).ToArray();
        var errors = new List<double>();
        for (int index = 0; index < ordered.Length; index++)
        {
            EncodingStatisticsRecord actual = ordered[index];
            EncodingRuntimeWorkload? workload = WorkloadFromRecord(actual);
            if (workload == null) continue;
            EncodingRuntimeEstimate estimate = Estimate(workload, ordered.Take(index));
            if (estimate.EstimatedProcessingSeconds is not > 0 || actual.ProcessingSeconds <= 0) continue;
            errors.Add(Math.Abs(estimate.EstimatedProcessingSeconds.Value - actual.ProcessingSeconds) / actual.ProcessingSeconds);
        }
        if (errors.Count == 0) return new(0, null, null, null);
        errors.Sort();
        return new(errors.Count, Median(errors) * 100, errors.Count(value => value <= .10) * 100d / errors.Count,
            errors.Count(value => value <= .25) * 100d / errors.Count);
    }

    public static string ResolutionTier(int? height) => height switch
    {
        null or <= 0 => "",
        <= 576 => "SD",
        <= 720 => "720p",
        <= 1080 => "1080p",
        <= 1440 => "1440p",
        _ => "4K+"
    };

    private static EncodingRuntimeEstimate Aggregate(EncodingRuntimeWorkload workload, Sample[] raw, int depth)
    {
        double[] speeds = RemoveOutliers(raw.Select(sample => sample.SpeedX).OrderBy(value => value).ToArray());
        if (speeds.Length == 0) return Unknown("Matching history was rejected as invalid or extreme.");
        double median = Median(speeds);
        double q1 = Percentile(speeds, .25);
        double q3 = Percentile(speeds, .75);
        double dispersion = median > 0 ? (q3 - q1) / median : double.MaxValue;
        RuntimeEstimateConfidence confidence = depth == 0 && speeds.Length >= 5 && dispersion <= .35
            ? RuntimeEstimateConfidence.High
            : depth <= 2 && speeds.Length >= 3 && dispersion <= .65
                ? RuntimeEstimateConfidence.Medium
                : RuntimeEstimateConfidence.Low;
        string[] descriptions =
        {
            "Exact encoder, codec, preset, resolution, bit-depth, scaling, and concurrency cohort.",
            "Matched workload with concurrency relaxed.",
            "Matched encoder, codec, preset, and resolution; bit-depth/scaling relaxed.",
            "Matched encoder, codec, and preset; resolution details relaxed.",
            "Matched encoder and codec; preset and media-shape details relaxed.",
            "Legacy encoder/codec cohort; newer workload fields were unavailable."
        };
        return new EncodingRuntimeEstimate
        {
            EstimatedProcessingSeconds = workload.SourceDurationSeconds / median,
            EstimatedSpeedX = median,
            FastProcessingSeconds = workload.SourceDurationSeconds / Math.Max(q3, .001),
            SlowProcessingSeconds = workload.SourceDurationSeconds / Math.Max(q1, .001),
            Confidence = confidence,
            SampleCount = speeds.Length,
            FallbackDepth = depth,
            CohortExplanation = $"{descriptions[Math.Clamp(depth, 0, descriptions.Length - 1)]} Median speed from {speeds.Length} robust sample(s)."
        };
    }

    private static bool IsUseful(EncodingStatisticsRecord record) =>
        record.Outcome == EncodingStatisticsOutcome.Success && !record.IsSampleJob &&
        record.MediaDurationSeconds is >= 60 && record.ProcessingSeconds >= 5 &&
        double.IsFinite(record.MediaDurationSeconds.Value) && double.IsFinite(record.ProcessingSeconds);

    private static bool Matches(EncodingStatisticsRecord record, EncodingRuntimeWorkload workload, int depth)
    {
        if (!CodecEquals(record.Codec, workload.OutputCodec) || !EncoderMatches(record, workload.EncoderId)) return false;
        if (depth < 5 && string.IsNullOrWhiteSpace(record.EncoderId)) return false;
        if (depth <= 3 && !Equal(record.EncoderPreset, workload.EncoderPreset)) return false;
        if (depth <= 2 && (!Equal(record.SourceResolutionTier, workload.SourceResolutionTier) || !Equal(record.OutputResolutionTier, workload.OutputResolutionTier))) return false;
        if (depth <= 1 && (record.OutputBitDepth != workload.OutputBitDepth || record.ScalingApplied != workload.ScalingApplied)) return false;
        if (depth == 0 && record.ConcurrentEncoderSessions != workload.ConcurrentEncoderSessions) return false;
        return true;
    }

    private static bool EncoderMatches(EncodingStatisticsRecord record, string requested) =>
        Equal(record.EncoderId, requested) || (string.IsNullOrWhiteSpace(record.EncoderId) &&
            Normalize(record.Encoder).Contains(Normalize(requested), StringComparison.OrdinalIgnoreCase));

    private static bool CodecEquals(string left, string right)
    {
        static string Family(string value)
        {
            string normalized = Normalize(value);
            if (normalized.Contains("265") || normalized.Contains("hevc")) return "hevc";
            if (normalized.Contains("264") || normalized.Contains("avc")) return "h264";
            if (normalized.Contains("av1") || normalized.Contains("av01")) return "av1";
            return normalized;
        }
        return Family(left) == Family(right);
    }

    private static bool Equal(string? left, string? right) => Normalize(left) == Normalize(right);
    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
    private static Sample ToSample(EncodingStatisticsRecord record) => new(record, record.MediaDurationSeconds!.Value / record.ProcessingSeconds);

    private static double[] RemoveOutliers(double[] values)
    {
        if (values.Length < 5) return values;
        double median = Median(values);
        double[] deviations = values.Select(value => Math.Abs(value - median)).OrderBy(value => value).ToArray();
        double mad = Median(deviations);
        double limit = Math.Max(median * .20, mad * 3.5);
        return values.Where(value => Math.Abs(value - median) <= limit).ToArray();
    }

    private static double Median(IReadOnlyList<double> values) => Percentile(values, .5);
    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        double position = (values.Count - 1) * percentile;
        int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
        return lower == upper ? values[lower] : values[lower] + (values[upper] - values[lower]) * (position - lower);
    }

    private static EncodingRuntimeWorkload? WorkloadFromRecord(EncodingStatisticsRecord record) =>
        string.IsNullOrWhiteSpace(record.EncoderId) || string.IsNullOrWhiteSpace(record.EncoderPreset) ||
        string.IsNullOrWhiteSpace(record.SourceResolutionTier) || string.IsNullOrWhiteSpace(record.OutputResolutionTier) ||
        !record.OutputBitDepth.HasValue || !record.ScalingApplied.HasValue || !record.ConcurrentEncoderSessions.HasValue
            ? null
            : new(record.MediaDurationSeconds ?? 0, record.EncoderId, record.Codec, record.EncoderPreset,
                record.SourceResolutionTier, record.OutputResolutionTier, record.OutputBitDepth.Value,
                record.ScalingApplied.Value, record.ConcurrentEncoderSessions.Value);

    private static EncodingRuntimeEstimate Unknown(string reason) => new() { CohortExplanation = reason };
    private sealed record Sample(EncodingStatisticsRecord Record, double SpeedX);
}
