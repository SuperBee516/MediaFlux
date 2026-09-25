using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Pure, video-only research predictor. Its output is never consumed by encoding policy.</summary>
public static class NvencQualityModeVideoBitratePredictionService
{
    // Bump this version whenever NvencEncoderProvider quality-mode tuning changes.
    public const string SettingsVersion = "nvenc-hevc-quality-v1";

    public static string EffectiveSettingsSignature(string encoderId, string outputCodec,
        string preset, int? bitDepth, bool? concurrent)
    {
        if (!string.Equals(encoderId, VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase) ||
            !outputCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(preset) || bitDepth is not (8 or 10) || concurrent is null)
            return "";

        string concurrency = concurrent.Value
            ? "lookahead=12;aq=1:8;temporal=1;surfaces=24;bf=3;refs=3;multipass=default"
            : "lookahead=32;aq=1:12;temporal=1;surfaces=48;bf=4;refs=4;multipass=fullres";
        return $"{SettingsVersion};codec=hevc_nvenc;mode=vbr-cq;preset={preset.Trim().ToLowerInvariant()};" +
            $"tune=hq;depth={bitDepth};{concurrency};bref=middle";
    }

    public static NvencQualityModePredictionResult Predict(
        NvencQualityModePredictionRequest request, IEnumerable<EncodingStatisticsRecord> history)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);
        if (request.MaterialTransformationActive ||
            !request.SourceCodec.Equals("h264", StringComparison.OrdinalIgnoreCase) ||
            !request.OutputCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase))
            return Unavailable(QualityModePredictionReason.Ineligible);
        if (string.IsNullOrWhiteSpace(request.SourceFamilyKey) ||
            string.IsNullOrWhiteSpace(request.SettingsSignature) || request.Cq is < 0 or > 51 ||
            !Positive(request.SourceVideoBitrateKbps) || request.Width <= 0 || request.Height <= 0 ||
            !Positive(request.Fps))
            return Unavailable(QualityModePredictionReason.MissingEvidence);

        double sourcePixelsPerSecond = (double)request.Width * request.Height * request.Fps;
        double sourceBpp = request.SourceVideoBitrateKbps * 1000 / sourcePixelsPerSecond;
        var comparable = history
            .Where(record => record.Outcome == EncodingStatisticsOutcome.Success &&
                !record.IsSampleJob && !record.RecoveredSuccessful &&
                string.Equals(record.QualityModeSettingsSignature, request.SettingsSignature, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(record.SourcePath) &&
                !string.Equals(FamilyKey(record), request.SourceFamilyKey, StringComparison.OrdinalIgnoreCase))
            .Select(record => (Record: record, Shadow: record.SourceAdaptiveShadow))
            .Where(item => item.Shadow is { Decision.IsPrimaryCalibrationCandidate: true,
                Decision.MaterialTransformationActive: false, ActualOutputVideoBitrateKbps: > 0 } &&
                item.Shadow.Decision.FinalExecutionCq == request.Cq &&
                item.Shadow.Decision.SourceCodec.Equals(request.SourceCodec, StringComparison.OrdinalIgnoreCase) &&
                item.Shadow.Decision.OutputCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) &&
                item.Shadow.ActualOutputCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase) &&
                item.Shadow.Decision.SourceVideoBitrateKbps is > 0 &&
                item.Shadow.Decision.PlannedWidth is > 0 && item.Shadow.Decision.PlannedHeight is > 0 &&
                item.Shadow.Decision.PlannedFps is > 0)
            .Where(item =>
            {
                var decision = item.Shadow!.Decision;
                double pixels = (double)decision.PlannedWidth!.Value * decision.PlannedHeight!.Value * decision.PlannedFps!.Value;
                double bpp = decision.SourceVideoBitrateKbps!.Value * 1000 / pixels;
                return WithinFactor(pixels, sourcePixelsPerSecond, 2) && WithinFactor(bpp, sourceBpp, 2);
            })
            // Repeated encodes of one source contribute only its latest validated result.
            .GroupBy(item => FamilyKey(item.Record), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Record.EndUtc).First().Shadow!)
            .ToArray();

        if (comparable.Length == 0)
            return Unavailable(QualityModePredictionReason.NoComparableHistory);
        if (comparable.Length < 2)
            return new(null, QualityModePredictionConfidence.Unavailable,
                QualityModePredictionReason.InsufficientIndependentSources, comparable.Length);

        double[] ratios = comparable.Select(item =>
                item.ActualOutputVideoBitrateKbps!.Value / item.Decision.SourceVideoBitrateKbps!.Value)
            .OrderBy(value => value).ToArray();
        double prediction = Median(ratios) * request.SourceVideoBitrateKbps;
        // Moderate and High require a larger, independently held-out corpus; neither is awarded here.
        return new(prediction, QualityModePredictionConfidence.Low,
            QualityModePredictionReason.ComparableHistory, comparable.Length,
            ratios[0] * request.SourceVideoBitrateKbps,
            ratios[^1] * request.SourceVideoBitrateKbps);
    }

    public static IReadOnlyList<QualityModeHeldOutError> EvaluateSourceHoldout(
        IEnumerable<EncodingStatisticsRecord> history)
    {
        EncodingStatisticsRecord[] records = history.ToArray();
        var errors = new List<QualityModeHeldOutError>();
        foreach (EncodingStatisticsRecord record in records)
        {
            SourceAdaptiveShadowOutcome? outcome = record.SourceAdaptiveShadow;
            SourceAdaptiveShadowCalibration? decision = outcome?.Decision;
            if (record.Outcome != EncodingStatisticsOutcome.Success || record.IsSampleJob ||
                record.RecoveredSuccessful || decision?.IsPrimaryCalibrationCandidate != true ||
                outcome?.ActualOutputVideoBitrateKbps is not > 0 ||
                decision.SourceVideoBitrateKbps is not > 0 || decision.FinalExecutionCq is null ||
                decision.PlannedWidth is not > 0 || decision.PlannedHeight is not > 0 ||
                decision.PlannedFps is not > 0 || string.IsNullOrWhiteSpace(record.SourcePath))
                continue;
            var request = new NvencQualityModePredictionRequest(FamilyKey(record),
                decision.SourceCodec, decision.OutputCodec, record.QualityModeSettingsSignature,
                decision.FinalExecutionCq.Value, decision.SourceVideoBitrateKbps.Value,
                decision.PlannedWidth.Value, decision.PlannedHeight.Value, decision.PlannedFps.Value,
                decision.MaterialTransformationActive);
            NvencQualityModePredictionResult prediction = Predict(request, records);
            if (prediction.PredictedVideoBitrateKbps is double predicted)
                errors.Add(new(FamilyKey(record), outcome.ActualOutputVideoBitrateKbps.Value,
                    predicted, (predicted / outcome.ActualOutputVideoBitrateKbps.Value - 1) * 100));
        }
        // One result per held-out source, even if it has multiple completed encodes.
        return errors.GroupBy(error => error.SourceFamilyKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).ToArray();
    }

    public static QualityModeHeldOutMetrics Summarize(IEnumerable<QualityModeHeldOutError> errors)
    {
        double[] signed = errors.Select(error => error.SignedErrorPercent).Where(double.IsFinite).ToArray();
        if (signed.Length == 0) return new(0, null, null, null, null, 0, null);
        double[] absolute = signed.Select(Math.Abs).OrderBy(value => value).ToArray();
        double[] sorted = signed.OrderBy(value => value).ToArray();
        double[] under = signed.Where(value => value < 0).Select(value => -value).ToArray();
        return new(signed.Length, Median(absolute), signed.Average(), Median(sorted),
            signed.Length >= 10 ? absolute[(int)Math.Ceiling(.9 * signed.Length) - 1] : null,
            under.Length, under.Length > 0 ? under.Average() : null);
    }

    private static NvencQualityModePredictionResult Unavailable(QualityModePredictionReason reason) =>
        new(null, QualityModePredictionConfidence.Unavailable, reason, 0);

    private static string FamilyKey(EncodingStatisticsRecord record)
    {
        SourceAdaptiveShadowCalibration? decision = record.SourceAdaptiveShadow?.Decision;
        // A media fingerprint groups aliases of the same complete source file.
        // If source measurements are missing, fall back to the path without inventing identity.
        if (decision?.SourceTotalBytes is > 0 && record.MediaDurationSeconds is > 0 &&
            decision.SourceVideoBitrateKbps is > 0 && decision.PlannedWidth is > 0 &&
            decision.PlannedHeight is > 0 && decision.PlannedFps is > 0)
            return $"media:{decision.SourceTotalBytes}:{Math.Round(record.MediaDurationSeconds.Value, 2)}:" +
                $"{Math.Round(decision.SourceVideoBitrateKbps.Value, 1)}:{decision.PlannedWidth}x{decision.PlannedHeight}:" +
                $"{Math.Round(decision.PlannedFps.Value, 3)}";
        return record.SourcePath.Trim();
    }

    private static bool Positive(double value) => value > 0 && double.IsFinite(value);
    private static bool WithinFactor(double value, double reference, double factor) =>
        Positive(value) && Positive(reference) && value / reference >= 1 / factor && value / reference <= factor;
    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
}
