namespace MediaFlux.Services;

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

public sealed record EncodingPredictionAccuracyMetrics(
    int CompletedCount, int SizePredictionCount, int EtaPredictionCount,
    double? MedianSizeAbsolutePercentageError, double? MedianEtaAbsolutePercentageError,
    long? MedianSizeSignedErrorBytes, double? MedianEtaSignedErrorSeconds,
    long? ActualSavingsBytes, long? PredictedSavingsBytes);

public sealed record EncodingPredictionAccuracyCohort(
    string SourceCodec, string TargetCodec, string ResolutionTier, string Encoder,
    string Quality, string Recommendation, int Count,
    double? MedianSizeAbsolutePercentageError, double? MedianEtaAbsolutePercentageError,
    long? MedianSizeSignedErrorBytes, double? MedianEtaSignedErrorSeconds);

public sealed class EncodingPredictionAccuracyService
{
    public IReadOnlyList<EncodingPredictionAccuracyRow> CreateRows(IEnumerable<EncodingStatisticsRecord> records) =>
        records.OrderByDescending(record => record.EndUtc).Select(CreateRow).ToArray();

    public EncodingPredictionAccuracyMetrics Summarize(IEnumerable<EncodingPredictionAccuracyRow> rows,
        bool includeRecovered = false)
    {
        EncodingPredictionAccuracyRow[] eligible = rows.Where(row => row.IsCleanCompleted ||
            (includeRecovered && row.Record.Outcome == EncodingStatisticsOutcome.Success && !row.Record.IsSampleJob)).ToArray();
        return new EncodingPredictionAccuracyMetrics(
            eligible.Length,
            eligible.Count(row => row.SizeAbsolutePercentageError.HasValue),
            eligible.Count(row => row.EtaAbsolutePercentageError.HasValue),
            Median(eligible.Select(row => row.SizeAbsolutePercentageError)),
            Median(eligible.Select(row => row.EtaAbsolutePercentageError)),
            MedianLong(eligible.Select(row => row.SizeSignedErrorBytes)),
            Median(eligible.Select(row => row.EtaSignedErrorSeconds)),
            SumSavings(eligible, actual: true), SumSavings(eligible, actual: false));
    }

    public IReadOnlyList<EncodingPredictionAccuracyCohort> BuildCohorts(
        IEnumerable<EncodingPredictionAccuracyRow> rows, bool includeRecovered = false) =>
        rows.Where(row => row.IsCleanCompleted ||
                (includeRecovered && row.Record.Outcome == EncodingStatisticsOutcome.Success && !row.Record.IsSampleJob))
            .Where(row => row.SizeAbsolutePercentageError.HasValue || row.EtaAbsolutePercentageError.HasValue)
            .GroupBy(row => new
            {
                Source = Value(row.Record.PredictionSourceCodec), Target = Value(row.Record.PredictionTargetCodec),
                Tier = Value(row.Record.OutputResolutionTier), Encoder = Value(row.Record.EncoderId, row.Record.Encoder),
                Quality = Value(row.Record.PredictionQuality), Recommendation = Value(row.Record.PredictionRecommendation)
            })
            .Select(group => new EncodingPredictionAccuracyCohort(group.Key.Source, group.Key.Target, group.Key.Tier,
                group.Key.Encoder, group.Key.Quality, group.Key.Recommendation, group.Count(),
                Median(group.Select(row => row.SizeAbsolutePercentageError)), Median(group.Select(row => row.EtaAbsolutePercentageError)),
                MedianLong(group.Select(row => row.SizeSignedErrorBytes)), Median(group.Select(row => row.EtaSignedErrorSeconds))))
            .OrderByDescending(cohort => cohort.Count).ThenBy(cohort => cohort.TargetCodec, StringComparer.OrdinalIgnoreCase).ToArray();

    private static EncodingPredictionAccuracyRow CreateRow(EncodingStatisticsRecord record)
    {
        long? sizeSigned = Difference(record.OutputSizeBytes, record.PredictedOutputSizeBytes);
        double? etaSigned = Difference(record.ProcessingSeconds, record.PredictedProcessingSeconds);
        return new(record, sizeSigned, Absolute(sizeSigned), Percent(sizeSigned, record.OutputSizeBytes), Absolute(Percent(sizeSigned, record.OutputSizeBytes)),
            etaSigned, Absolute(etaSigned), Percent(etaSigned, record.ProcessingSeconds), Absolute(Percent(etaSigned, record.ProcessingSeconds)));
    }

    private static long? Difference(long? actual, long? predicted) => actual.HasValue && predicted.HasValue ? actual.Value - predicted.Value : null;
    private static double? Difference(double actual, double? predicted) => actual > 0 && predicted is > 0 ? actual - predicted.Value : null;
    private static long? Absolute(long? value) => value.HasValue ? Math.Abs(value.Value) : null;
    private static double? Absolute(double? value) => value.HasValue ? Math.Abs(value.Value) : null;
    private static double? Percent(long? signed, long? actual) => signed.HasValue && actual is > 0 ? signed.Value * 100d / actual.Value : null;
    private static double? Percent(double? signed, double actual) => signed.HasValue && actual > 0 ? signed.Value * 100d / actual : null;
    private static double? Median(IEnumerable<double?> values) { double[] valid = values.Where(value => value.HasValue && double.IsFinite(value.Value)).Select(value => value!.Value).OrderBy(value => value).ToArray(); return valid.Length == 0 ? null : valid[(valid.Length - 1) / 2]; }
    private static long? MedianLong(IEnumerable<long?> values) { long[] valid = values.Where(value => value.HasValue).Select(value => value!.Value).OrderBy(value => value).ToArray(); return valid.Length == 0 ? null : valid[(valid.Length - 1) / 2]; }
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
