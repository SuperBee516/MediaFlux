namespace MediaFlux.Models;

public enum SourceAdaptiveShadowStatus
{
    NotApplicable,
    InsufficientEvidence,
    CalibrationCandidate,
    PredictedExpansion,
    PredictionUnavailable,
    TransformationExcluded,
    ExplicitUserPolicy
}

/// <summary>Observational size-policy evidence frozen with an encode plan.</summary>
public sealed record SourceAdaptiveShadowCalibration
{
    public SourceAdaptiveShadowStatus Status { get; init; }
    public string EligibilityReason { get; init; } = "";
    public string SourceCodec { get; init; } = "";
    public string OutputCodec { get; init; } = "";
    public string EncoderId { get; init; } = "";
    public string Preset { get; init; } = "";
    public QualityTarget? QualityTarget { get; init; }
    public int? InitialCq { get; init; }
    public int? FinalExecutionCq { get; init; }
    public double? SourceVideoBitrateKbps { get; init; }
    public string SourceBitrateProvenance { get; init; } = "Unavailable";
    public double? SourceTotalBitrateKbps { get; init; }
    public double? PredictedOutputVideoBitrateKbps { get; init; }
    public long? PredictedTotalOutputBytes { get; init; }
    public double? PredictedVideoRatio { get; init; }
    public double? PredictedTotalRatio { get; init; }
    public long? SourceTotalBytes { get; init; }
    public int? PlannedWidth { get; init; }
    public int? PlannedHeight { get; init; }
    public double? PlannedFps { get; init; }
    public bool MaterialTransformationActive { get; init; }
    public string PredictionCohort { get; init; } = "Existing heuristic; uncalibrated";
    public string ShadowReason { get; init; } = "";
    public bool IsPrimaryCalibrationCandidate => Status is
        SourceAdaptiveShadowStatus.CalibrationCandidate or SourceAdaptiveShadowStatus.PredictedExpansion;
}

/// <summary>Actual successful output measurements paired with the frozen shadow prediction.</summary>
public sealed record SourceAdaptiveShadowOutcome
{
    public SourceAdaptiveShadowCalibration Decision { get; init; } = new();
    public double? ActualOutputVideoBitrateKbps { get; init; }
    public double? ActualOutputTotalBitrateKbps { get; init; }
    public long? ActualOutputBytes { get; init; }
    public double? ActualOutputDurationSeconds { get; init; }
    public string ActualOutputCodec { get; init; } = "";
    public int? ActualOutputWidth { get; init; }
    public int? ActualOutputHeight { get; init; }
    public double? ActualOutputFps { get; init; }
    public double? ActualVideoRatio { get; init; }
    public double? PredictionErrorPercent { get; init; }

    public static SourceAdaptiveShadowOutcome FromOutput(
        SourceAdaptiveShadowCalibration decision,
        MediaProbeResult? output,
        long? outputBytes)
    {
        ArgumentNullException.ThrowIfNull(decision);
        MediaProbeStreamInfo? video = output?.Streams.FirstOrDefault(stream =>
            stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        double? actualVideoKbps = video?.BitRate is > 0 ? video.BitRate.Value / 1000d : null;
        double? actualTotalKbps = output?.BitRate is > 0
            ? output.BitRate.Value / 1000d
            : outputBytes is > 0 && output?.DurationSeconds is > 0
                ? outputBytes.Value * 8d / 1000d / output.DurationSeconds.Value
                : null;
        return new SourceAdaptiveShadowOutcome
        {
            Decision = decision,
            ActualOutputVideoBitrateKbps = actualVideoKbps,
            ActualOutputTotalBitrateKbps = actualTotalKbps,
            ActualOutputBytes = outputBytes,
            ActualOutputDurationSeconds = output?.DurationSeconds,
            ActualOutputCodec = video?.CodecName ?? "",
            ActualOutputWidth = video?.Width,
            ActualOutputHeight = video?.Height,
            ActualOutputFps = video?.FrameRate,
            ActualVideoRatio = SafeRatio(actualVideoKbps, decision.SourceVideoBitrateKbps),
            PredictionErrorPercent = SafeRatio(actualVideoKbps, decision.PredictedOutputVideoBitrateKbps) is double errorRatio
                ? (errorRatio - 1d) * 100d
                : null
        };
    }

    public static SourceAdaptiveShadowOutcome ForTerminalOutcome(
        SourceAdaptiveShadowCalibration decision,
        bool succeeded,
        MediaProbeResult? output,
        long? outputBytes) =>
        succeeded
            ? FromOutput(decision, output, outputBytes)
            : new SourceAdaptiveShadowOutcome { Decision = decision };

    public static double? SafeRatio(double? numerator, double? denominator) =>
        numerator is > 0 && denominator is > 0 && double.IsFinite(numerator.Value) && double.IsFinite(denominator.Value)
            ? numerator.Value / denominator.Value
            : null;

    public string Describe() =>
        $"[SourceAdaptiveShadow] {Decision.SourceCodec} → {Decision.OutputCodec} {Decision.EncoderId} {Decision.Preset}; " +
        $"CQ={Decision.InitialCq?.ToString() ?? "unknown"} (unchanged); " +
        $"source-video={Decision.SourceVideoBitrateKbps?.ToString("0") ?? "unknown"} kbps [{Decision.SourceBitrateProvenance}]; " +
        $"predicted-video={Decision.PredictedOutputVideoBitrateKbps?.ToString("0") ?? "unknown"} kbps; " +
        $"actual-video={ActualOutputVideoBitrateKbps?.ToString("0") ?? "unknown"} kbps; " +
        $"predicted-ratio={Decision.PredictedVideoRatio?.ToString("0.##") ?? "unknown"}x; " +
        $"actual-ratio={ActualVideoRatio?.ToString("0.##") ?? "unknown"}x; " +
        $"prediction-error={PredictionErrorPercent?.ToString("+0.##;-0.##;0") ?? "unknown"}%; " +
        $"status={Decision.Status}; reason={Decision.EligibilityReason}; execution-adjustment=none (shadow mode).";
}
