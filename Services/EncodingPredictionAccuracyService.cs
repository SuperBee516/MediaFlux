using MediaFlux.Models;

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

public sealed record EncodingSizeCalibrationContext(
    string SourceCodec, string TargetCodec, string SourceResolutionTier, string OutputResolutionTier,
    string EncoderId, string HardwareKey, string Quality, string Assessment, bool SameCodec);

public enum EncodingCalibrationOutcome { Improved, Neutral, Worsened }
public sealed record EncodingCalibrationEvaluationRow(EncodingStatisticsRecord Record,
    double BaseSignedErrorPercent, double BaseAbsoluteErrorPercent,
    double CalibratedSignedErrorPercent, double CalibratedAbsoluteErrorPercent,
    double AbsoluteErrorImprovement, EncodingCalibrationOutcome Outcome);
public sealed record EncodingCalibrationEvaluation(int CalibratedCount, double? MedianBaseSignedErrorPercent,
    double? MedianCalibratedSignedErrorPercent, double? MedianAbsoluteErrorImprovement,
    int Improved, int Neutral, int Worsened, IReadOnlyList<EncodingCalibrationEvaluationRow> Rows);
public sealed record EncodingCalibrationEffectiveness(string CohortKey, int EvaluationCount,
    double? MedianBaseAbsoluteErrorPercent, double? MedianCalibratedAbsoluteErrorPercent,
    double? MedianImprovementPercent, int Improved, int Neutral, int Worsened,
    double? ImprovedRatePercent, double? WorsenedRatePercent,
    EncodingPredictionConfidence HistoricalConfidence, int HistoricalSampleCount,
    EncodingCalibrationEffectivenessState State, bool CurrentlyEligible,
    DateTime? EffectivenessSinceUtc,
    IReadOnlyList<EncodingCalibrationEvaluationRow> Rows);

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
    public const double HighConfidenceLearningStrength = .75;
    public const double ModerateConfidenceLearningStrength = .40;
    public const double MaximumEffectiveCorrectionPercent = 20;
    public const double CalibrationEvaluationDeadbandPercentagePoints = .5;
    public const int MinimumCalibrationEffectivenessSamples = 5;
    public const int CalibrationEffectivenessWindowSize = 20;
    public const double MaximumEffectiveWorsenedRatePercent = 40;
    public const double HarmfulMinimumWorsenedRatePercent = 60;
    public const int CalibrationRecoverySamples = 10;
    public const double CalibrationRecoveryMinimumImprovementPercent = 1.5;
    public const double CalibrationRecoveryMaximumWorsenedRatePercent = 20;
    public const double MixedCalibrationLearningStrength = .5;

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

    public EncodingSizePredictionCalibration CalibrateSizePrediction(double basePredictionMb,
        EncodingSizeCalibrationContext context, IEnumerable<EncodingStatisticsRecord> history, bool enabled,
        bool eligible = true, string ineligibleReason = "This estimate is not eligible for historical calibration.",
        DateTime? decisionUtc = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(history);
        if (!double.IsFinite(basePredictionMb) || basePredictionMb <= 0)
            return EncodingSizePredictionCalibration.Unavailable(null, "Base estimate is unavailable or invalid.");

        DateTime now = decisionUtc.HasValue ? NormalizeUtc(decisionUtc.Value) : DateTime.UtcNow;
        EncodingStatisticsRecord[] historySnapshot = history.ToArray();
        string cohortKey = CalibrationCohortKey(context);
        EncodingStatisticsRecord[] matching = historySnapshot.Where(record =>
                record.Outcome == EncodingStatisticsOutcome.Success && !record.IsSampleJob && !record.RecoveredSuccessful &&
                record.SourceSizeBytes is > 0 && record.OutputSizeBytes is > 0 &&
                (record.BasePredictedOutputSizeBytes ?? record.PredictedOutputSizeBytes) is > 0 &&
                CodecFamilyEquals(record.PredictionSourceCodec, context.SourceCodec) &&
                CodecFamilyEquals(record.PredictionTargetCodec, context.TargetCodec) &&
                Equal(record.SourceResolutionTier, context.SourceResolutionTier) &&
                Equal(record.OutputResolutionTier, context.OutputResolutionTier) &&
                Equal(record.EncoderId, context.EncoderId) && Equal(record.HardwareKey, context.HardwareKey) &&
                Equal(record.PredictionQuality, context.Quality) && Equal(record.PredictionAssessment, context.Assessment) &&
                record.PredictionSameCodec == context.SameCodec)
            .ToArray();
        EncodingPredictionAccuracyRow[] baselineRows = CreateRows(matching.Select(record => record with
        {
            PredictedOutputSizeBytes = record.BasePredictedOutputSizeBytes ?? record.PredictedOutputSizeBytes
        })).ToArray();
        EncodingPredictionRobustStatistics evidence = Describe(baselineRows.Select(row => row.SizePercentError));
        EncodingPredictionConfidence confidence = ClassifyConfidence(evidence);
        DateTime? evidenceCutoffUtc = matching.Length == 0 ? now : matching.Max(record => NormalizeUtc(record.EndUtc));
        EncodingCalibrationEffectiveness effectiveness = EvaluateCalibrationEffectiveness(historySnapshot, cohortKey, now);
        double? rawBias = evidence.MedianSignedPercent;
        if (!eligible)
            return CalibrationResult(basePredictionMb, basePredictionMb, 0, rawBias, confidence,
                evidence.SampleCount, EncodingCalibrationDecision.NotEligible, cohortKey, ineligibleReason,
                evidenceCutoffUtc, now, effectiveness, basePredictionMb);
        if (!enabled)
            return CalibrationResult(basePredictionMb, basePredictionMb, 0, rawBias, confidence,
                evidence.SampleCount, EncodingCalibrationDecision.DisabledByUser, cohortKey,
                "Historical size calibration is disabled by the user.", evidenceCutoffUtc, now, effectiveness, basePredictionMb);
        if (!rawBias.HasValue)
            return CalibrationResult(basePredictionMb, basePredictionMb, 0, null, confidence, 0,
                EncodingCalibrationDecision.InsufficientHistoricalConfidence, cohortKey,
                "No comparable clean historical cohort.", evidenceCutoffUtc, now, effectiveness);

        double strength = confidence switch
        {
            EncodingPredictionConfidence.High => HighConfidenceLearningStrength,
            EncodingPredictionConfidence.Moderate => ModerateConfidenceLearningStrength,
            _ => 0
        };
        double effectivePercent = Math.Clamp(rawBias.Value * strength,
            -MaximumEffectiveCorrectionPercent, MaximumEffectiveCorrectionPercent);
        double hypothetical = basePredictionMb * (1 + effectivePercent / 100d);
        if (!double.IsFinite(hypothetical) || hypothetical <= 0)
            return EncodingSizePredictionCalibration.Unavailable(basePredictionMb, "Calibration result was invalid; base estimate retained.");

        if (strength <= 0)
            return CalibrationResult(basePredictionMb, basePredictionMb, 0, rawBias, confidence,
                evidence.SampleCount, EncodingCalibrationDecision.InsufficientHistoricalConfidence, cohortKey,
                confidence == EncodingPredictionConfidence.Low
                    ? "Historical cohort confidence is low; calibration is not eligible."
                    : "Historical cohort has insufficient observations for calibration.",
                evidenceCutoffUtc, now, effectiveness, basePredictionMb);
        if (effectiveness.State == EncodingCalibrationEffectivenessState.Harmful)
            return CalibrationResult(basePredictionMb, basePredictionMb, effectivePercent, rawBias, confidence,
                evidence.SampleCount, EncodingCalibrationDecision.ShadowEvaluationOnly, cohortKey,
                "Calibration suppressed because post-calibration evidence is harmful; hypothetical candidate retained for shadow evaluation.",
                evidenceCutoffUtc, now, effectiveness, hypothetical);

        if (effectiveness.State == EncodingCalibrationEffectivenessState.Mixed)
            effectivePercent *= MixedCalibrationLearningStrength;
        double calibrated = basePredictionMb * (1 + effectivePercent / 100d);
        if (!double.IsFinite(calibrated) || calibrated <= 0)
            return EncodingSizePredictionCalibration.Unavailable(basePredictionMb, "Calibration result was invalid; base estimate retained.");
        string reason = effectiveness.State == EncodingCalibrationEffectivenessState.Mixed
            ? "Mixed post-calibration outcomes; confidence-weighted correction was further reduced."
            : "Comparable historical evidence applied with a confidence-weighted correction.";
        return CalibrationResult(basePredictionMb, calibrated, effectivePercent, rawBias, confidence,
            evidence.SampleCount, EncodingCalibrationDecision.Applied, cohortKey, reason,
            evidenceCutoffUtc, now, effectiveness, calibrated);
    }

    public static string CalibrationCohortKey(EncodingSizeCalibrationContext context) => string.Join("|",
        NormalizeCodec(context.SourceCodec), NormalizeCodec(context.TargetCodec), Normalize(context.SourceResolutionTier),
        Normalize(context.OutputResolutionTier), Normalize(context.EncoderId), Normalize(context.HardwareKey),
        Normalize(context.Quality), Normalize(context.Assessment), context.SameCodec ? "same" : "conversion");

    public static EncodingCalibrationEvaluation EvaluateCalibrations(IEnumerable<EncodingStatisticsRecord> records)
    {
        EncodingCalibrationEvaluationRow[] rows = CreateCalibrationEvaluationRows(records).ToArray();
        return new(rows.Length, Median(rows.Select(row => (double?)row.BaseSignedErrorPercent)),
            Median(rows.Select(row => (double?)row.CalibratedSignedErrorPercent)),
            Median(rows.Select(row => (double?)row.AbsoluteErrorImprovement)),
            rows.Count(row => row.Outcome == EncodingCalibrationOutcome.Improved),
            rows.Count(row => row.Outcome == EncodingCalibrationOutcome.Neutral),
            rows.Count(row => row.Outcome == EncodingCalibrationOutcome.Worsened), rows);
    }

    public static IReadOnlyList<EncodingCalibrationEffectiveness> BuildCalibrationEffectiveness(
        IEnumerable<EncodingStatisticsRecord> records, DateTime? decisionUtc = null)
    {
        EncodingStatisticsRecord[] snapshot = records.ToArray();
        return snapshot.Where(record => !string.IsNullOrWhiteSpace(record.CalibrationCohortKey))
            .Select(record => record.CalibrationCohortKey)
            .Distinct(StringComparer.Ordinal)
            .Select(key => EvaluateCalibrationEffectiveness(snapshot, key,
                decisionUtc.HasValue ? NormalizeUtc(decisionUtc.Value) : DateTime.UtcNow))
            .OrderBy(item => item.CohortKey, StringComparer.Ordinal)
            .ToArray();
    }

    public static EncodingCalibrationEffectiveness EvaluateCalibrationEffectiveness(
        IEnumerable<EncodingStatisticsRecord> records, string cohortKey, DateTime? decisionUtc = null)
    {
        DateTime now = decisionUtc.HasValue ? NormalizeUtc(decisionUtc.Value) : DateTime.UtcNow;
        EncodingStatisticsRecord[] cohort = records.Where(record =>
            string.Equals(record.CalibrationCohortKey, cohortKey, StringComparison.Ordinal)).ToArray();
        EncodingStatisticsRecord? latest = cohort.Where(record => record.CalibrationDecisionUtc.HasValue)
            .OrderByDescending(record => NormalizeUtc(record.CalibrationDecisionUtc!.Value)).FirstOrDefault();
        EncodingCalibrationEffectivenessState priorState = Enum.TryParse(latest?.CalibrationEffectivenessState, out EncodingCalibrationEffectivenessState parsed)
            ? parsed : EncodingCalibrationEffectivenessState.NotEvaluated;
        DateTime? epoch = latest?.CalibrationEffectivenessSinceUtc;

        EncodingCalibrationEvaluationRow[] allRows = CreateCalibrationEvaluationRows(cohort).ToArray();
        bool recovering = priorState == EncodingCalibrationEffectivenessState.Harmful;
        EncodingCalibrationEvaluationRow[] inEpoch = allRows
            .Where(row => !epoch.HasValue || row.Record.CalibrationDecisionUtc >= epoch)
            .OrderBy(row => row.Record.EndUtc)
            .ToArray();
        EncodingCalibrationEvaluationRow[] stateRows = inEpoch
            .Where(row => recovering
                ? row.Record.CalibrationDecision == EncodingCalibrationDecision.ShadowEvaluationOnly.ToString()
                : row.Record.CalibrationDecision == EncodingCalibrationDecision.Applied.ToString())
            .TakeLast(CalibrationEffectivenessWindowSize)
            .ToArray();
        EncodingCalibrationEvaluationRow[] relevant = (recovering ? inEpoch : stateRows)
            .TakeLast(CalibrationEffectivenessWindowSize).ToArray();

        double[] improvements = stateRows.Select(row => row.AbsoluteErrorImprovement).ToArray();
        double? stateMedianImprovement = Median(improvements.Select(value => (double?)value));
        double? reportedMedianImprovement = Median(relevant.Select(row => (double?)row.AbsoluteErrorImprovement));
        int improved = relevant.Count(row => row.Outcome == EncodingCalibrationOutcome.Improved);
        int neutral = relevant.Count(row => row.Outcome == EncodingCalibrationOutcome.Neutral);
        int worsened = relevant.Count(row => row.Outcome == EncodingCalibrationOutcome.Worsened);
        double? improvedRate = relevant.Length < MinimumCalibrationEffectivenessSamples ? null : improved * 100d / relevant.Length;
        double? worsenedRate = relevant.Length < MinimumCalibrationEffectivenessSamples ? null : worsened * 100d / relevant.Length;
        double? stateWorsenedRate = stateRows.Length == 0 ? null :
            stateRows.Count(row => row.Outcome == EncodingCalibrationOutcome.Worsened) * 100d / stateRows.Length;
        EncodingCalibrationEffectivenessState state;
        DateTime? since = epoch;
        if (recovering)
        {
            bool recovered = stateRows.Length >= CalibrationRecoverySamples &&
                stateMedianImprovement > CalibrationRecoveryMinimumImprovementPercent &&
                stateWorsenedRate <= CalibrationRecoveryMaximumWorsenedRatePercent;
            state = recovered ? EncodingCalibrationEffectivenessState.Effective : EncodingCalibrationEffectivenessState.Harmful;
            if (recovered) since = now;
        }
        else
        {
            state = ClassifyEffectiveness(stateRows.Length, stateMedianImprovement, stateWorsenedRate);
            if (state == EncodingCalibrationEffectivenessState.Harmful && priorState != EncodingCalibrationEffectivenessState.Harmful)
                since = now;
        }

        EncodingStatisticsRecord? evidence = latest ?? cohort.OrderByDescending(record => record.CalibrationDecisionUtc ?? record.EndUtc).FirstOrDefault();
        return new(cohortKey, relevant.Length,
            Median(relevant.Select(row => (double?)row.BaseAbsoluteErrorPercent)),
            Median(relevant.Select(row => (double?)row.CalibratedAbsoluteErrorPercent)), reportedMedianImprovement,
            improved, neutral, worsened, improvedRate, worsenedRate,
            Enum.TryParse(evidence?.CalibrationConfidence, out EncodingPredictionConfidence confidence)
                ? confidence : EncodingPredictionConfidence.Insufficient,
            evidence?.CalibrationSampleCount ?? 0, state,
            state != EncodingCalibrationEffectivenessState.Harmful, since, relevant);
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
    private static double? Median(IEnumerable<double?> values)
    {
        double[] sorted = values.Where(value => value.HasValue && double.IsFinite(value.Value)).Select(value => value!.Value).OrderBy(value => value).ToArray();
        return sorted.Length == 0 ? null : Percentile(sorted, .5);
    }
    private static double? Median(IEnumerable<double> values) => Median(values.Select(value => (double?)value));
    private static EncodingCalibrationEffectivenessState ClassifyEffectiveness(
        int count, double? medianImprovement, double? worsenedRate)
    {
        if (count == 0) return EncodingCalibrationEffectivenessState.NotEvaluated;
        if (count < MinimumCalibrationEffectivenessSamples) return EncodingCalibrationEffectivenessState.Early;
        if (medianImprovement < -CalibrationEvaluationDeadbandPercentagePoints ||
            worsenedRate >= HarmfulMinimumWorsenedRatePercent)
            return EncodingCalibrationEffectivenessState.Harmful;
        if (medianImprovement > CalibrationEvaluationDeadbandPercentagePoints &&
            worsenedRate <= MaximumEffectiveWorsenedRatePercent)
            return EncodingCalibrationEffectivenessState.Effective;
        return EncodingCalibrationEffectivenessState.Mixed;
    }
    private static EncodingSizePredictionCalibration CalibrationResult(double baseMb, double displayedMb,
        double? correction, double? bias, EncodingPredictionConfidence confidence, int sampleCount,
        EncodingCalibrationDecision decision, string cohortKey, string reason, DateTime? cutoff, DateTime now,
        EncodingCalibrationEffectiveness effectiveness, double? hypotheticalMb = null)
    {
        bool applied = decision == EncodingCalibrationDecision.Applied;
        return new(baseMb, displayedMb, correction, bias, confidence, sampleCount, applied, cohortKey, reason,
            hypotheticalMb ?? displayedMb, decision, effectiveness.State, effectiveness.EvaluationCount,
            effectiveness.MedianImprovementPercent, effectiveness.ImprovedRatePercent,
            effectiveness.WorsenedRatePercent, cutoff, now, effectiveness.EffectivenessSinceUtc);
    }
    private static IEnumerable<EncodingCalibrationEvaluationRow> CreateCalibrationEvaluationRows(
        IEnumerable<EncodingStatisticsRecord> records) => records
        .Where(record => record.Outcome == EncodingStatisticsOutcome.Success && !record.IsSampleJob &&
            !record.RecoveredSuccessful &&
            record.CalibrationDecision is nameof(EncodingCalibrationDecision.Applied) or nameof(EncodingCalibrationDecision.ShadowEvaluationOnly) &&
            record.CalibrationEvidenceCutoffUtc.HasValue &&
            NormalizeUtc(record.EndUtc) > NormalizeUtc(record.CalibrationEvidenceCutoffUtc.Value) &&
            record.OutputSizeBytes is > 0 && record.BasePredictedOutputSizeBytes is > 0 &&
            (record.HypotheticalCalibratedOutputSizeBytes ?? record.PredictedOutputSizeBytes) is > 0)
        .Select(record =>
        {
            double actual = record.OutputSizeBytes!.Value;
            double basePredicted = record.BasePredictedOutputSizeBytes!.Value;
            double calibratedPredicted = (record.HypotheticalCalibratedOutputSizeBytes ?? record.PredictedOutputSizeBytes)!.Value;
            double baseError = (actual - basePredicted) * 100d / basePredicted;
            double calibratedError = (actual - calibratedPredicted) * 100d / calibratedPredicted;
            double improvement = Math.Abs(baseError) - Math.Abs(calibratedError);
            EncodingCalibrationOutcome outcome = improvement > CalibrationEvaluationDeadbandPercentagePoints
                ? EncodingCalibrationOutcome.Improved
                : improvement < -CalibrationEvaluationDeadbandPercentagePoints
                    ? EncodingCalibrationOutcome.Worsened : EncodingCalibrationOutcome.Neutral;
            return new EncodingCalibrationEvaluationRow(record, baseError, Math.Abs(baseError), calibratedError,
                Math.Abs(calibratedError), improvement, outcome);
        });
    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    private static bool CodecFamilyEquals(string left, string right) => NormalizeCodec(left) == NormalizeCodec(right);
    private static string NormalizeCodec(string? value)
    {
        string normalized = Normalize(value);
        return normalized.Contains("hevc") || normalized.Contains("265") ? "hevc" :
            normalized.Contains("avc") || normalized.Contains("264") ? "h264" :
            normalized.Contains("av1") ? "av1" : normalized;
    }
    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
    private static bool Equal(string? left, string? right) => Normalize(left) == Normalize(right);
}
