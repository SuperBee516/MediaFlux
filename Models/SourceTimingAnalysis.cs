namespace MediaFlux.Models;
public enum SourceTimingClassification { Unknown, Cfr, CfrMinorVariance, Vfr, IrregularUnsafe }
public enum AiTimingEligibility { Unknown, EligibleCurrentCfrPipeline, PotentialFutureTimestampAware, UnsafeUnsupported }
public sealed record TimingWindowEvidence(double PositionFraction, IReadOnlyList<double> PresentationTimestamps, bool ProbeSucceeded = true);
public sealed record SourceTimingEvidence(double? NominalFps, double? AverageFps, string TimeBase, double? StartTime, double? StreamDuration, double? ContainerDuration, IReadOnlyList<double> PresentationTimestamps, IReadOnlyList<TimingWindowEvidence>? Windows = null);
/// <summary>One bounded FFprobe frame-PTS backward step, expressed in seconds.</summary>
public sealed record SourceTimingDiscontinuity(
    string TimestampDomain,
    int PreviousSampleIndex,
    int CurrentSampleIndex,
    double PreviousTimestampSeconds,
    double CurrentTimestampSeconds,
    double BackwardDeltaSeconds);
public sealed record SourceTimingWindowResult(
    double PositionFraction, int FrameCount, double MedianInterval, double IntervalVariance,
    bool HasDiscontinuity, bool HasNonMonotonicTimestamps,
    int NonMonotonicEventCount = 0,
    SourceTimingDiscontinuity? FirstNonMonotonicEvent = null,
    double LargestBackwardDeltaSeconds = 0);
public sealed record SourceTimingAnalysis(
    SourceTimingClassification Classification, AiTimingEligibility AiEligibility, int Confidence,
    double? NominalFps, double? AverageFps, double IntervalVariance, bool HasDiscontinuity,
    bool HasNonMonotonicTimestamps, string Reason, IReadOnlyList<SourceTimingWindowResult>? Windows = null,
    int SamplesInspected = 0, int NonMonotonicEventCount = 0,
    SourceTimingDiscontinuity? FirstNonMonotonicEvent = null,
    double LargestBackwardDeltaSeconds = 0);
