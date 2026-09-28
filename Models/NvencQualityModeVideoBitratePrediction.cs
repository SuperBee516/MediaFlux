namespace MediaFlux.Models;

public enum QualityModePredictionConfidence { Unavailable, Low, Moderate, High }
public enum QualityModePredictionReason { Ineligible, MissingEvidence, NoComparableHistory, InsufficientIndependentSources, ComparableHistory }

/// <summary>Video-only observation request. SourceFamilyKey excludes the same source from history.</summary>
public sealed record NvencQualityModePredictionRequest(
    string SourceFamilyKey, string SourceCodec, string OutputCodec, string SettingsSignature,
    int Cq, double SourceVideoBitrateKbps, int Width, int Height, double Fps,
    bool MaterialTransformationActive);

/// <summary>Observational prediction. No member is an encoding execution value.</summary>
public sealed record NvencQualityModePredictionResult(
    double? PredictedVideoBitrateKbps, QualityModePredictionConfidence Confidence,
    QualityModePredictionReason Reason, int IndependentSourceCount,
    double? ObservedLowVideoBitrateKbps = null, double? ObservedHighVideoBitrateKbps = null);

/// <summary>Paired offline comparators calculated from the exact same admitted peer families.</summary>
public sealed record QualityModePairedPrediction(
    NvencQualityModePredictionResult Ratio,
    NvencQualityModePredictionResult Direct,
    IReadOnlyList<string> AdmittedPeerSourceFamilyKeys);

public sealed record QualityModePredictionError(double PredictedVideoBitrateKbps,
    double SignedErrorKbps, double AbsoluteErrorKbps,
    double SignedErrorPercent, double AbsoluteErrorPercent);

/// <summary>Strict family-held-out comparison. H50 is explicitly exploratory and offline-only.</summary>
public sealed record QualityModeHeldOutComparison(
    string SourceFamilyKey, double ActualVideoBitrateKbps,
    QualityModePredictionReason Reason, int IndependentPeerCount,
    IReadOnlyList<string> AdmittedPeerSourceFamilyKeys,
    QualityModePredictionError? RatioError,
    QualityModePredictionError? DirectError,
    QualityModePredictionError? ExploratoryH50Error);

public sealed record QualityModeHoldoutEvaluation(
    int EligibleTargetCount, IReadOnlyList<QualityModeHeldOutComparison> Targets)
{
    public int CoveredTargetCount => Targets.Count(target => target.RatioError is not null && target.DirectError is not null);
    public double? CoveragePercent => EligibleTargetCount == 0 ? null : CoveredTargetCount * 100d / EligibleTargetCount;
}

public sealed record QualityModeHeldOutError(string SourceFamilyKey, double ActualVideoBitrateKbps,
    double PredictedVideoBitrateKbps, double SignedErrorPercent);

public sealed record QualityModeHeldOutMetrics(int SourceCount, double? MedianAbsoluteErrorPercent,
    double? MeanBiasPercent, double? MedianBiasPercent, double? P90AbsoluteErrorPercent,
    int UnderpredictionCount, double? MeanUnderpredictionMagnitudePercent);
