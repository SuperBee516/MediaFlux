namespace MediaFlux.Services;

public enum EncodingPredictionConfidence { Insufficient, Low, Moderate, High }
public enum EncodingPredictionBiasState { InsufficientData, Overestimating, NearTarget, Underestimating }

/// <summary>Read-only projections of frozen predictions against finalized output.</summary>
public sealed record EncodingPredictionAccuracyRow(
    EncodingStatisticsRecord Record,
    long? SizeSignedErrorBytes,
    long? SizeAbsoluteErrorBytes,
    double? SizePercentError,
    double? SizeAbsolutePercentageError,
    double? EtaSignedErrorSeconds,
    double? EtaAbsoluteErrorSeconds,
    double? EtaPercentError,
    double? EtaAbsolutePercentageError)
{
    public bool IsCleanCompleted => Record.Outcome == EncodingStatisticsOutcome.Success &&
        !Record.IsSampleJob && !Record.RecoveredSuccessful;
}

/// <summary>
/// Percentages use (actual - predicted) / predicted * 100. Percentiles use
/// linear interpolation at (n - 1) * p, so even-count medians are averaged.
/// </summary>
public sealed record EncodingPredictionRobustStatistics(
    int SampleCount, double? MedianSignedPercent, double? MedianAbsolutePercentageError,
    double? FirstQuartileSignedPercent, double? ThirdQuartileSignedPercent, double? SignedPercentIqr)
{
    public static readonly EncodingPredictionRobustStatistics Empty = new(0, null, null, null, null, null);
}

public sealed record EncodingPredictionAccuracyMetrics(
    int CompletedCount, int SizePredictionCount, int EtaPredictionCount,
    double? MedianSizeSignedPercentageError, double? MedianSizeAbsolutePercentageError, double? SizeSignedPercentageIqr,
    double? MedianEtaSignedPercentageError, double? MedianEtaAbsolutePercentageError, double? EtaSignedPercentageIqr,
    EncodingPredictionConfidence Confidence, EncodingPredictionBiasState BiasState,
    long? ActualSavingsBytes, long? PredictedSavingsBytes);

public sealed record EncodingPredictionAccuracyCohort(
    string SourceCodec, string TargetCodec, string ResolutionTier, string Encoder,
    string Quality, string Recommendation, int Count, int SizePredictionCount, int EtaPredictionCount,
    double? MedianSizeSignedPercentageError, double? MedianSizeAbsolutePercentageError, double? SizeSignedPercentageIqr,
    double? MedianEtaSignedPercentageError, double? MedianEtaAbsolutePercentageError, double? EtaSignedPercentageIqr,
    EncodingPredictionConfidence Confidence, EncodingPredictionBiasState BiasState,
    long? ActualSavingsBytes, long? PredictedSavingsBytes);

public sealed class EncodingPredictionAccuracyService
{
    // Intentionally conservative and centralized for future Adaptive Learning tuning.
    public const int MinimumMeaningfulSamples = 5;
    public const int ModerateConfidenceSamples = 10;
    public const int HighConfidenceSamples = 20;
    public const double ModerateMaximumSignedIqrPercent = 25;
    public const double HighMaximumSignedIqrPercent = 10;
    public const double ModerateMaximumAbsoluteErrorPercent = 30;
    public const double HighMaximumAbsoluteErrorPercent = 15;
    public const double BiasNearTargetDeadbandPercent = 3;

    public IReadOnlyList<EncodingPredictionAccuracyRow> CreateRows(IEnumerable<EncodingStatisticsRecord> records) =>
        records.OrderByDescending(record => record.EndUtc).Select(CreateRow).ToArray();

    public EncodingPredictionAccuracyMetrics Summarize(IEnumerable<EncodingPredictionAccuracyRow> rows,
        bool includeRecovered = false)
    {
        EncodingPredictionAccuracyRow[] eligible = Eligible(rows, includeRecovered).ToArray();
        EncodingPredictionRobustStatistics size = Describe(eligible.Select(row => row.SizePercentError));
        EncodingPredictionRobustStatistics eta = Describe(eligible.Select(row => row.EtaPercentError));
        return new EncodingPredictionAccuracyMetrics(
            eligible.Length, size.SampleCount, eta.SampleCount,
            size.MedianSignedPercent, size.MedianAbsolutePercentageError, size.SignedPercentIqr,
            eta.MedianSignedPercent, eta.MedianAbsolutePercentageError, eta.SignedPercentIqr,
            ClassifyConfidence(size), ClassifyBias(size),
            SumSavings(eligible, actual: true), SumSavings(eligible, actual: false));
    }

    public IReadOnlyList<EncodingPredictionAccuracyCohort> BuildCohorts(
        IEnumerable<EncodingPredictionAccuracyRow> rows, bool includeRecovered = false) =>
        Eligible(rows, includeRecovered)
            .Where(row => row.SizePercentError.HasValue || row.EtaPercentError.HasValue)
            .GroupBy(row => new
            {
                Source = Value(row.Record.PredictionSourceCodec), Target = Value(row.Record.PredictionTargetCodec),
                Tier = Value(row.Record.OutputResolutionTier), Encoder = Value(row.Record.EncoderId, row.Record.Encoder),
                Quality = Value(row.Record.PredictionQuality), Recommendation = Value(row.Record.PredictionRecommendation)
            })
            .Select(group => CreateCohort(group.Key.Source, group.Key.Target, group.Key.Tier, group.Key.Encoder,
                group.Key.Quality, group.Key.Recommendation, group.ToArray()))
            .OrderByDescending(cohort => cohort.Count).ThenBy(cohort => cohort.TargetCodec, StringComparer.OrdinalIgnoreCase).ToArray();

    public static EncodingPredictionRobustStatistics Describe(IEnumerable<double?> values)
    {
        double[] signed = values.Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value).OrderBy(value => value).ToArray();
        if (signed.Length == 0) return EncodingPredictionRobustStatistics.Empty;
        double[] absolute = signed.Select(Math.Abs).OrderBy(value => value).ToArray();
        double q1 = Percentile(signed, .25), q3 = Percentile(signed, .75);
        return new(signed.Length, Percentile(signed, .5), Percentile(absolute, .5), q1, q3, q3 - q1);
    }

    public static EncodingPredictionConfidence ClassifyConfidence(EncodingPredictionRobustStatistics statistics)
    {
        if (statistics.SampleCount < MinimumMeaningfulSamples) return EncodingPredictionConfidence.Insufficient;
        if (statistics.SampleCount >= HighConfidenceSamples &&
            statistics.SignedPercentIqr is <= HighMaximumSignedIqrPercent &&
            statistics.MedianAbsolutePercentageError is <= HighMaximumAbsoluteErrorPercent)
            return EncodingPredictionConfidence.High;
        if (statistics.SampleCount >= ModerateConfidenceSamples &&
            statistics.SignedPercentIqr is <= ModerateMaximumSignedIqrPercent &&
            statistics.MedianAbsolutePercentageError is <= ModerateMaximumAbsoluteErrorPercent)
            return EncodingPredictionConfidence.Moderate;
        return EncodingPredictionConfidence.Low;
    }

    public static EncodingPredictionBiasState ClassifyBias(EncodingPredictionRobustStatistics statistics)
    {
        if (statistics.SampleCount < MinimumMeaningfulSamples || !statistics.MedianSignedPercent.HasValue)
            return EncodingPredictionBiasState.InsufficientData;
        if (statistics.MedianSignedPercent.Value > BiasNearTargetDeadbandPercent)
            return EncodingPredictionBiasState.Underestimating;
        if (statistics.MedianSignedPercent.Value < -BiasNearTargetDeadbandPercent)
            return EncodingPredictionBiasState.Overestimating;
        return EncodingPredictionBiasState.NearTarget;
    }

    private static EncodingPredictionAccuracyCohort CreateCohort(string source, string target, string tier,
        string encoder, string quality, string recommendation, EncodingPredictionAccuracyRow[] rows)
    {
        EncodingPredictionRobustStatistics size = Describe(rows.Select(row => row.SizePercentError));
        EncodingPredictionRobustStatistics eta = Describe(rows.Select(row => row.EtaPercentError));
        return new(source, target, tier, encoder, quality, recommendation, rows.Length, size.SampleCount, eta.SampleCount,
            size.MedianSignedPercent, size.MedianAbsolutePercentageError, size.SignedPercentIqr,
            eta.MedianSignedPercent, eta.MedianAbsolutePercentageError, eta.SignedPercentIqr,
            ClassifyConfidence(size), ClassifyBias(size), SumSavings(rows, actual: true), SumSavings(rows, actual: false));
    }

    private static IEnumerable<EncodingPredictionAccuracyRow> Eligible(IEnumerable<EncodingPredictionAccuracyRow> rows,
        bool includeRecovered) => rows.Where(row => row.IsCleanCompleted ||
            (includeRecovered && row.Record.Outcome == EncodingStatisticsOutcome.Success && !row.Record.IsSampleJob));

    private static EncodingPredictionAccuracyRow CreateRow(EncodingStatisticsRecord record)
    {
        long? sizeSigned = Difference(record.OutputSizeBytes, record.PredictedOutputSizeBytes);
        double? etaSigned = Difference(record.ProcessingSeconds, record.PredictedProcessingSeconds);
        return new(record, sizeSigned, Absolute(sizeSigned), Percent(sizeSigned, record.PredictedOutputSizeBytes), Absolute(Percent(sizeSigned, record.PredictedOutputSizeBytes)),
            etaSigned, Absolute(etaSigned), Percent(etaSigned, record.PredictedProcessingSeconds), Absolute(Percent(etaSigned, record.PredictedProcessingSeconds)));
    }

    private static long? Difference(long? actual, long? predicted) => actual.HasValue && predicted.HasValue ? actual.Value - predicted.Value : null;
    private static double? Difference(double actual, double? predicted) => actual > 0 && predicted is > 0 ? actual - predicted.Value : null;
    private static long? Absolute(long? value) => value.HasValue ? Math.Abs(value.Value) : null;
    private static double? Absolute(double? value) => value.HasValue ? Math.Abs(value.Value) : null;
    private static double? Percent(long? signed, long? predicted) => signed.HasValue && predicted is > 0 ? signed.Value * 100d / predicted.Value : null;
    private static double? Percent(double? signed, double? predicted) => signed.HasValue && predicted is > 0 ? signed.Value * 100d / predicted.Value : null;
    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        double position = (sortedValues.Count - 1) * percentile;
        int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
        return lower == upper ? sortedValues[lower] : sortedValues[lower] +
            (sortedValues[upper] - sortedValues[lower]) * (position - lower);
    }
    private static long? SumSavings(IEnumerable<EncodingPredictionAccuracyRow> rows, bool actual)
    {
        long total = 0; bool any = false;
        foreach (EncodingPredictionAccuracyRow row in rows)
        {
            long? output = actual ? row.Record.OutputSizeBytes : row.Record.PredictedOutputSizeBytes;
            if (row.Record.SourceSizeBytes is not >= 0 || output is not >= 0) continue;
            total += row.Record.SourceSizeBytes.Value - output.Value; any = true;
        }
        return any ? total : null;
    }
    private static string Value(string? primary, string? fallback = null) => string.IsNullOrWhiteSpace(primary) ? (string.IsNullOrWhiteSpace(fallback) ? "Unknown" : fallback.Trim()) : primary.Trim();
}
