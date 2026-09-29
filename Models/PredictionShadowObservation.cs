namespace MediaFlux.Models;

public enum PredictionShadowSamplingStatus
{
    Succeeded,
    Partial,
    Unavailable
}

public sealed record PredictionShadowSampleWindow(
    string Label,
    double StartSeconds,
    double DurationSeconds,
    int FramesAnalyzed);

public sealed record PredictionShadowSamplingObservation
{
    public string SamplerVersion { get; init; } = "";
    public PredictionShadowSamplingStatus Status { get; init; }
    /// <summary>Median normalized adjacent-frame mean absolute luma difference, in [0,1].</summary>
    public double? TemporalFrameDifference { get; init; }
    /// <summary>Median normalized within-frame gradient magnitude, in [0,1].</summary>
    public double? SpatialGradientEnergy { get; init; }
    public int FrameCount { get; init; }
    public int TemporalPairCount { get; init; }
    public int WindowCount { get; init; }
    public int? AnalysisFrameWidth { get; init; }
    public int? AnalysisFrameHeight { get; init; }
    public IReadOnlyList<PredictionShadowSampleWindow> Windows { get; init; } = Array.Empty<PredictionShadowSampleWindow>();
    public double WallClockMilliseconds { get; init; }
    public double? FfmpegCpuMilliseconds { get; init; }
    public long TemporaryBytesWritten { get; init; }
    public bool UsedHardwareDecode { get; init; }
    public string NoiseGrainProxyStatus { get; init; } = "OmittedNotValidated";
    public string? FailureReason { get; init; }
}

public sealed record PredictionShadowForecast
{
    public double? PredictedVideoBitrateKbps { get; init; }
    public string Confidence { get; init; } = "";
    public string Reason { get; init; } = "";
    public int IndependentPeerCount { get; init; }
    public double? ObservedLowVideoBitrateKbps { get; init; }
    public double? ObservedHighVideoBitrateKbps { get; init; }
}

public static class PredictionShadowTemporalNeighborVersions
{
    public const string K2 = "temporal-neighbor-k2-v1";
}

public enum PredictionShadowExperimentStratum
{
    Low,
    Control,
    High
}

public enum PredictionShadowExperimentRole
{
    Target,
    Replacement
}

/// <summary>Explicit research-only identity for one assigned experiment attempt.</summary>
public sealed record PredictionShadowExperimentAssignment(
    string ExperimentId,
    int Slot,
    int Attempt,
    PredictionShadowExperimentStratum Stratum,
    PredictionShadowExperimentRole Role)
{
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ExperimentId) &&
        Slot > 0 && Attempt > 0 &&
        Enum.IsDefined(Stratum) && Enum.IsDefined(Role);
}

public sealed record PredictionShadowTemporalNeighbor
{
    public required string SourceFamilyKey { get; init; }
    public double TemporalFrameDifference { get; init; }
    public double TemporalDistance { get; init; }
    public double SourceVideoBitrateKbps { get; init; }
    public double ActualOutputVideoBitrateKbps { get; init; }
    public double ActualOutputToSourceVideoBitrateRatio { get; init; }
}

/// <summary>Frozen, research-only nearest-neighbor evidence; k is fixed at two.</summary>
public sealed record PredictionShadowTemporalNeighborComparison
{
    public string ComparatorVersion { get; init; } = PredictionShadowTemporalNeighborVersions.K2;
    public int K { get; init; } = 2;
    public double? TargetTemporalFrameDifference { get; init; }
    public int EligiblePeerCount { get; init; }
    public double? EligiblePeerTemporalMinimum { get; init; }
    public double? EligiblePeerTemporalMaximum { get; init; }
    public double? MeanNeighborOutputToSourceVideoBitrateRatio { get; init; }
    public IReadOnlyList<PredictionShadowTemporalNeighbor> SelectedNeighbors { get; init; } =
        Array.Empty<PredictionShadowTemporalNeighbor>();
    public PredictionShadowForecast Ratio { get; init; } = new();
    public PredictionShadowForecast Direct { get; init; } = new();
    public string AbstentionReason { get; init; } = "";
}

/// <summary>
/// Immutable facts captured after plan creation and before FFmpeg execution.
/// Outcome data intentionally lives in a later journal event.
/// </summary>
public sealed record PredictionShadowFrozenObservation
{
    /// <summary>Defaults to legacy v1 when a historical JSON line omits this field; capture writes v3.</summary>
    public int SchemaVersion { get; init; } = 1;
    public required string ObservationId { get; init; }
    public Guid PlanId { get; init; }
    public required string SourceFamilyKey { get; init; }
    public string SourcePathSha256 { get; init; } = "";
    public DateTime PredictionEvidenceCutoffUtc { get; init; }
    public DateTime FrozenUtc { get; init; }
    public string MediaFluxVersion { get; init; } = "";

    public string SourceCodec { get; init; } = "";
    public int? SourceWidth { get; init; }
    public int? SourceHeight { get; init; }
    public double? SourceFps { get; init; }
    public double? SourceVideoBitrateKbps { get; init; }
    public double? SourceBitsPerPixel { get; init; }
    public double? SourcePixelsPerSecond { get; init; }
    public long? SourceBytes { get; init; }

    public string OutputCodec { get; init; } = "";
    public string EncoderId { get; init; } = "";
    public string Preset { get; init; } = "";
    public int? Cq { get; init; }
    public string SettingsSignature { get; init; } = "";
    public int? ComparatorWidth { get; init; }
    public int? ComparatorHeight { get; init; }
    public double? ComparatorFps { get; init; }

    public PredictionShadowForecast Ratio { get; init; } = new();
    public PredictionShadowForecast Direct { get; init; } = new();
    public IReadOnlyList<string> AdmittedPeerSourceFamilyKeys { get; init; } = Array.Empty<string>();
    public bool RatioAndDirectSharePeers { get; init; }
    public PredictionShadowSamplingObservation Complexity { get; init; } = new();
    public PredictionShadowTemporalNeighborComparison? TemporalNeighbor { get; init; }
    public PredictionShadowExperimentAssignment? ExperimentAssignment { get; init; }
}

/// <summary>Later finalization facts; never used to recompute a frozen forecast.</summary>
public sealed record PredictionShadowOutcome
{
    public PredictionShadowExperimentAssignment? ExperimentAssignment { get; init; }
    public string State { get; init; } = "";
    public string TerminalResult { get; init; } = "";
    public string ValidationState { get; init; } = "";
    public string FinalizationState { get; init; } = "";
    public bool RecoveredSuccessful { get; init; }
    public double? ActualOutputVideoBitrateKbps { get; init; }
    public long? SourceBytes { get; init; }
    public long? OutputBytes { get; init; }
    public double? OutputToSourceVideoBitrateRatio { get; init; }
    public long? SizeChangeBytes { get; init; }
    public double? SizeChangePercent { get; init; }
    public double? RatioSignedErrorKbps { get; init; }
    public double? RatioSignedErrorPercent { get; init; }
    public double? RatioAbsoluteErrorPercent { get; init; }
    public double? DirectSignedErrorKbps { get; init; }
    public double? DirectSignedErrorPercent { get; init; }
    public double? DirectAbsoluteErrorPercent { get; init; }
    public double? TemporalNeighborRatioSignedErrorKbps { get; init; }
    public double? TemporalNeighborRatioSignedErrorPercent { get; init; }
    public double? TemporalNeighborRatioAbsoluteErrorPercent { get; init; }
    public double? TemporalNeighborDirectSignedErrorKbps { get; init; }
    public double? TemporalNeighborDirectSignedErrorPercent { get; init; }
    public double? TemporalNeighborDirectAbsoluteErrorPercent { get; init; }
}

/// <summary>Append-only lifecycle event in the separate research JSONL journal.</summary>
public sealed record PredictionShadowJournalEvent
{
    /// <summary>Defaults to legacy v1 when a historical JSON line omits this field; journal appends use the Frozen version.</summary>
    public int SchemaVersion { get; init; } = 1;
    public required string EventId { get; init; }
    public required string ObservationId { get; init; }
    public required string EventType { get; init; }
    public DateTime RecordedUtc { get; init; }
    public PredictionShadowFrozenObservation? Frozen { get; init; }
    public PredictionShadowOutcome? Outcome { get; init; }
}
