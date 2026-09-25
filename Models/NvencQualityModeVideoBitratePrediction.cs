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

public sealed record QualityModeHeldOutError(string SourceFamilyKey, double ActualVideoBitrateKbps,
    double PredictedVideoBitrateKbps, double SignedErrorPercent);

public sealed record QualityModeHeldOutMetrics(int SourceCount, double? MedianAbsoluteErrorPercent,
    double? MeanBiasPercent, double? MedianBiasPercent, double? P90AbsoluteErrorPercent,
    int UnderpredictionCount, double? MeanUnderpredictionMagnitudePercent);
