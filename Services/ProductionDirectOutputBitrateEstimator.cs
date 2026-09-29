using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record ProductionDirectOutputRequest(
    string SourceCodec, string OutputCodec, string EncoderId, string Preset,
    int BitDepth, int Cq, string SettingsSignature, int OutputWidth, int OutputHeight,
    double Fps, QualityTarget QualityTarget, double SourceVideoKbps,
    string SourceFamilyKey, DateTime EvidenceCutoffUtc,
    bool MaterialTransformationActive);

public enum ProductionDirectOutputStatus
{
    Supported,
    Ineligible,
    InsufficientIndependentFamilies,
    InsufficientHeldOutEvidence,
    AccuracyGateFailed
}

public sealed record ProductionDirectOutputResult(
    ProductionDirectOutputStatus Status, int IndependentFamilies,
    double? PredictedVideoBitrateKbps, int HeldOutCount, int HeldOutEligibleCount,
    double? MedianAbsoluteErrorPercent, double? P90AbsoluteErrorPercent,
    DateTime EvidenceCutoffUtc)
{
    public const string ModelId = "ProductionHistoricalDirectV1";
    public bool Supported => Status == ProductionDirectOutputStatus.Supported;
    public double HeldOutCoverage => HeldOutEligibleCount == 0 ? 0 :
        HeldOutCount / (double)HeldOutEligibleCount;
}

/// <summary>
/// Production-only prediction from finalized statistics. The median is resistant to
/// single-content outliers; validation forecasts each new family using only earlier outcomes.
/// </summary>
public sealed class ProductionDirectOutputBitrateEstimator
{
    // Ten independent sources matches the existing moderate-confidence sample convention.
    // Five earlier families seed a forward-held-out evaluation of at least five later families.
    public const int MinimumIndependentFamilies = 10;
    public const int MinimumTrainingFamilies = 5;
    public const int MinimumHeldOutPredictions = 5;
    public const double MinimumHeldOutCoverage = .5;
    public const double MaximumMedianAbsoluteErrorPercent = 20;
    public const double MaximumP90AbsoluteErrorPercent = 30;

    private sealed record Observation(string Id, string Family, DateTime EndUtc,
        double OutputVideoKbps);

    public ProductionDirectOutputResult Predict(ProductionDirectOutputRequest target,
        IEnumerable<EncodingStatisticsRecord> history)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(history);
        DateTime cutoff = target.EvidenceCutoffUtc;
        if (cutoff.Kind != DateTimeKind.Utc || cutoff == default ||
            target.MaterialTransformationActive ||
            !string.Equals(target.EncoderId, VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase) ||
            !CodecEquals(target.SourceCodec, "h264") || !CodecEquals(target.OutputCodec, "hevc") ||
            target.Cq is < 0 or > 51 || target.BitDepth is not (8 or 10) ||
            string.IsNullOrWhiteSpace(target.Preset) ||
            string.IsNullOrWhiteSpace(target.SettingsSignature) ||
            string.IsNullOrWhiteSpace(target.SourceFamilyKey) ||
            target.OutputWidth <= 0 || target.OutputHeight <= 0 ||
            !Positive(target.Fps) || !Positive(target.SourceVideoKbps))
            return Result(ProductionDirectOutputStatus.Ineligible, 0, null, 0, 0, null, null, cutoff);

        Observation[] eligible = history
            .Where(record => record.Outcome == EncodingStatisticsOutcome.Success &&
                !record.IsSampleJob && !record.RecoveredSuccessful &&
                record.EndUtc.Kind == DateTimeKind.Utc && record.EndUtc < cutoff &&
                string.Equals(record.TerminalResult, "Completed", StringComparison.OrdinalIgnoreCase) &&
                record.ScalingApplied == false && record.OutputBitDepth == target.BitDepth &&
                string.Equals(record.EncoderId, target.EncoderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.EncoderPreset, target.Preset, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.QualityModeSettingsSignature, target.SettingsSignature, StringComparison.Ordinal) &&
                CodecEquals(record.PredictionSourceCodec, "h264") &&
                CodecEquals(record.PredictionTargetCodec, "hevc") &&
                record.SourceAdaptiveShadow is
                {
                    Decision.IsPrimaryCalibrationCandidate: true,
                    Decision.MaterialTransformationActive: false,
                    ActualOutputVideoBitrateKbps: > 0
                } &&
                record.SourceAdaptiveShadow.Decision.FinalExecutionCq == target.Cq &&
                record.SourceAdaptiveShadow.Decision.QualityTarget == target.QualityTarget &&
                record.SourceAdaptiveShadow.Decision.SourceVideoBitrateKbps is > 0 &&
                BitrateCompatible(record.SourceAdaptiveShadow.Decision.SourceVideoBitrateKbps!.Value,
                    target.SourceVideoKbps) &&
                record.SourceAdaptiveShadow.Decision.SourceTotalBytes is > 0 &&
                record.MediaDurationSeconds is > 0 &&
                CodecEquals(record.SourceAdaptiveShadow.ActualOutputCodec, "hevc") &&
                record.SourceAdaptiveShadow.Decision.PlannedWidth is > 0 &&
                record.SourceAdaptiveShadow.Decision.PlannedHeight is > 0 &&
                record.SourceAdaptiveShadow.Decision.PlannedFps is > 0 &&
                GeometryCompatible(record.SourceAdaptiveShadow.Decision.PlannedWidth!.Value,
                    record.SourceAdaptiveShadow.Decision.PlannedHeight!.Value,
                    target.OutputWidth, target.OutputHeight) &&
                FpsCompatible(record.SourceAdaptiveShadow.Decision.PlannedFps!.Value, target.Fps))
            .Select(record => new Observation(record.Id,
                SourceFamily(record.SourceAdaptiveShadow!.Decision,
                    record.MediaDurationSeconds, record.SourcePath), record.EndUtc,
                record.SourceAdaptiveShadow.ActualOutputVideoBitrateKbps!.Value))
            .Where(observation => !string.IsNullOrWhiteSpace(observation.Family) &&
                !string.Equals(observation.Family, target.SourceFamilyKey,
                    StringComparison.OrdinalIgnoreCase) && Positive(observation.OutputVideoKbps))
            .OrderBy(observation => observation.EndUtc)
            .ThenBy(observation => observation.Id, StringComparer.Ordinal)
            .ToArray();

        Observation[] latestByFamily = eligible
            .GroupBy(observation => observation.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(observation => observation.EndUtc)
                .ThenBy(observation => observation.Id, StringComparer.Ordinal).First())
            .OrderBy(observation => observation.EndUtc)
            .ThenBy(observation => observation.Id, StringComparer.Ordinal)
            .ToArray();
        int count = latestByFamily.Length;
        if (count < MinimumIndependentFamilies)
            return Result(ProductionDirectOutputStatus.InsufficientIndependentFamilies,
                count, null, 0, count, null, null, cutoff);

        // Each family is evaluated only on its first occurrence. For that point in time,
        // later repeats cannot enter training; earlier repeats collapse to their latest
        // completed outcome before the validation target's own cutoff.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<double>();
        int heldOutEligible = 0;
        foreach (Observation candidate in eligible)
        {
            if (!seen.Add(candidate.Family))
                continue;
            heldOutEligible++;
            double[] training = eligible
                .Where(row => row.EndUtc < candidate.EndUtc &&
                    !string.Equals(row.Family, candidate.Family, StringComparison.OrdinalIgnoreCase))
                .GroupBy(row => row.Family, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(row => row.EndUtc)
                    .ThenBy(row => row.Id, StringComparer.Ordinal).First().OutputVideoKbps)
                .ToArray();
            if (training.Length < MinimumTrainingFamilies)
                continue;
            double prediction = Median(training);
            errors.Add(Math.Abs(prediction / candidate.OutputVideoKbps - 1) * 100);
        }

        double? medianError = errors.Count > 0 ? Percentile(errors, .5) : null;
        double? p90Error = errors.Count > 0 ? Percentile(errors, .9) : null;
        if (errors.Count < MinimumHeldOutPredictions ||
            errors.Count / (double)heldOutEligible < MinimumHeldOutCoverage)
            return Result(ProductionDirectOutputStatus.InsufficientHeldOutEvidence,
                count, null, errors.Count, heldOutEligible, medianError, p90Error, cutoff);
        if (medianError > MaximumMedianAbsoluteErrorPercent ||
            p90Error > MaximumP90AbsoluteErrorPercent)
            return Result(ProductionDirectOutputStatus.AccuracyGateFailed,
                count, null, errors.Count, heldOutEligible, medianError, p90Error, cutoff);

        return Result(ProductionDirectOutputStatus.Supported, count,
            Median(latestByFamily.Select(row => row.OutputVideoKbps)),
            errors.Count, heldOutEligible, medianError, p90Error, cutoff);
    }

    internal static SizeEstimateBreakdown SelectSizeBreakdown(
        SizeEstimateBreakdown generic,
        double durationSeconds, ProductionDirectOutputResult result) =>
        result.Supported && result.PredictedVideoBitrateKbps is > 0
            ? SizeEstimateService.WithTargetVideoBitrate(generic, durationSeconds,
                result.PredictedVideoBitrateKbps.Value)
            : generic;

    public static string SourceFamily(SourceAdaptiveShadowCalibration? decision,
        double? durationSeconds, string? sourcePath)
    {
        if (decision?.SourceTotalBytes is > 0 && durationSeconds is > 0 &&
            decision.SourceVideoBitrateKbps is > 0 && decision.PlannedWidth is > 0 &&
            decision.PlannedHeight is > 0 && decision.PlannedFps is > 0)
            return SourceFamily(decision.SourceTotalBytes.Value, durationSeconds.Value,
                decision.SourceVideoBitrateKbps.Value, decision.PlannedWidth.Value,
                decision.PlannedHeight.Value, decision.PlannedFps.Value);
        return (sourcePath ?? "").Trim();
    }

    public static string SourceFamily(long sourceBytes, double durationSeconds,
        double sourceVideoKbps, int width, int height, double fps) =>
        $"media:{sourceBytes}:{Math.Round(durationSeconds, 2)}:" +
        $"{Math.Round(sourceVideoKbps, 1)}:{width}x{height}:{Math.Round(fps, 3)}";

    private static ProductionDirectOutputResult Result(ProductionDirectOutputStatus status,
        int count, double? prediction, int heldOut, int eligible, double? median,
        double? p90, DateTime cutoff) =>
        new(status, count, prediction, heldOut, eligible, median, p90, cutoff);

    private static bool CodecEquals(string? value, string family)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return family == "h264" ? v.Contains("h264") || v.Contains("avc") || v.Contains("264") :
            family == "hevc" && (v.Contains("hevc") || v.Contains("265"));
    }

    private static bool GeometryCompatible(int width, int height, int targetWidth, int targetHeight) =>
        Math.Abs(width / (double)targetWidth - 1) <= .05 &&
        Math.Abs(height / (double)targetHeight - 1) <= .05;

    private static bool FpsCompatible(double fps, double targetFps) =>
        Positive(fps) && Math.Abs(fps / targetFps - 1) <= .01;

    private static bool BitrateCompatible(double sourceKbps, double targetKbps) =>
        Positive(sourceKbps) && sourceKbps / targetKbps is >= .5 and <= 2;

    private static bool Positive(double value) => value > 0 && double.IsFinite(value);

    private static double Median(IEnumerable<double> values) => Percentile(values, .5);

    private static double Percentile(IEnumerable<double> values, double p)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        double position = (sorted.Length - 1) * p;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
