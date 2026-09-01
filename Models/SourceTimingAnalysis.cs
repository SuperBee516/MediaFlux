namespace MediaFlux.Models;
public enum SourceTimingClassification { Unknown, Cfr, CfrMinorVariance, Vfr, IrregularUnsafe }
public enum AiTimingEligibility { Unknown, EligibleCurrentCfrPipeline, PotentialFutureTimestampAware, UnsafeUnsupported }
public sealed record TimingWindowEvidence(double PositionFraction, IReadOnlyList<double> PresentationTimestamps, bool ProbeSucceeded = true);
public sealed record SourceTimingEvidence(double? NominalFps, double? AverageFps, string TimeBase, double? StartTime, double? StreamDuration, double? ContainerDuration, IReadOnlyList<double> PresentationTimestamps, IReadOnlyList<TimingWindowEvidence>? Windows = null);
public sealed record SourceTimingWindowResult(double PositionFraction, int FrameCount, double MedianInterval, double IntervalVariance, bool HasDiscontinuity, bool HasNonMonotonicTimestamps);
public sealed record SourceTimingAnalysis(SourceTimingClassification Classification, AiTimingEligibility AiEligibility, int Confidence, double? NominalFps, double? AverageFps, double IntervalVariance, bool HasDiscontinuity, bool HasNonMonotonicTimestamps, string Reason, IReadOnlyList<SourceTimingWindowResult>? Windows = null);
