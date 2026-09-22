using System.Collections.ObjectModel;

namespace MediaFlux.Models;

/// <summary>
/// Immutable, behavior-versioned parameters for historical size-prediction calibration.
/// Shipped definitions are permanent: add a new definition instead of editing an old one.
/// </summary>
public sealed class AdaptivePredictionPolicy
{
    internal AdaptivePredictionPolicy(
        string policyId,
        int minimumMeaningfulSamples,
        int moderateConfidenceSamples,
        int highConfidenceSamples,
        double moderateMaximumSignedIqrPercent,
        double highMaximumSignedIqrPercent,
        double moderateMaximumAbsoluteErrorPercent,
        double highMaximumAbsoluteErrorPercent,
        double biasNearTargetDeadbandPercent,
        double highConfidenceLearningStrength,
        double moderateConfidenceLearningStrength,
        double maximumEffectiveCorrectionPercent,
        double calibrationEvaluationDeadbandPercentagePoints,
        int minimumCalibrationEffectivenessSamples,
        int calibrationEffectivenessWindowSize,
        double maximumEffectiveWorsenedRatePercent,
        double harmfulMinimumWorsenedRatePercent,
        int calibrationRecoverySamples,
        double calibrationRecoveryMinimumImprovementPercent,
        double calibrationRecoveryMaximumWorsenedRatePercent,
        double mixedCalibrationLearningStrength)
    {
        PolicyId = policyId;
        MinimumMeaningfulSamples = minimumMeaningfulSamples;
        ModerateConfidenceSamples = moderateConfidenceSamples;
        HighConfidenceSamples = highConfidenceSamples;
        ModerateMaximumSignedIqrPercent = moderateMaximumSignedIqrPercent;
        HighMaximumSignedIqrPercent = highMaximumSignedIqrPercent;
        ModerateMaximumAbsoluteErrorPercent = moderateMaximumAbsoluteErrorPercent;
        HighMaximumAbsoluteErrorPercent = highMaximumAbsoluteErrorPercent;
        BiasNearTargetDeadbandPercent = biasNearTargetDeadbandPercent;
        HighConfidenceLearningStrength = highConfidenceLearningStrength;
        ModerateConfidenceLearningStrength = moderateConfidenceLearningStrength;
        MaximumEffectiveCorrectionPercent = maximumEffectiveCorrectionPercent;
        CalibrationEvaluationDeadbandPercentagePoints = calibrationEvaluationDeadbandPercentagePoints;
        MinimumCalibrationEffectivenessSamples = minimumCalibrationEffectivenessSamples;
        CalibrationEffectivenessWindowSize = calibrationEffectivenessWindowSize;
        MaximumEffectiveWorsenedRatePercent = maximumEffectiveWorsenedRatePercent;
        HarmfulMinimumWorsenedRatePercent = harmfulMinimumWorsenedRatePercent;
        CalibrationRecoverySamples = calibrationRecoverySamples;
        CalibrationRecoveryMinimumImprovementPercent = calibrationRecoveryMinimumImprovementPercent;
        CalibrationRecoveryMaximumWorsenedRatePercent = calibrationRecoveryMaximumWorsenedRatePercent;
        MixedCalibrationLearningStrength = mixedCalibrationLearningStrength;
    }

    public string PolicyId { get; }
    public int MinimumMeaningfulSamples { get; }
    public int ModerateConfidenceSamples { get; }
    public int HighConfidenceSamples { get; }
    public double ModerateMaximumSignedIqrPercent { get; }
    public double HighMaximumSignedIqrPercent { get; }
    public double ModerateMaximumAbsoluteErrorPercent { get; }
    public double HighMaximumAbsoluteErrorPercent { get; }
    public double BiasNearTargetDeadbandPercent { get; }
    public double HighConfidenceLearningStrength { get; }
    public double ModerateConfidenceLearningStrength { get; }
    public double MaximumEffectiveCorrectionPercent { get; }
    public double CalibrationEvaluationDeadbandPercentagePoints { get; }
    public int MinimumCalibrationEffectivenessSamples { get; }
    public int CalibrationEffectivenessWindowSize { get; }
    public double MaximumEffectiveWorsenedRatePercent { get; }
    public double HarmfulMinimumWorsenedRatePercent { get; }
    public int CalibrationRecoverySamples { get; }
    public double CalibrationRecoveryMinimumImprovementPercent { get; }
    public double CalibrationRecoveryMaximumWorsenedRatePercent { get; }
    public double MixedCalibrationLearningStrength { get; }
}

/// <summary>
/// Registry for permanent adaptive-policy definitions. To introduce V2, add its immutable
/// definition here and deliberately change <see cref="Current"/>; never edit V1 in place.
/// The policy ID, not the application version, identifies adaptive behavior in the journal.
/// </summary>
public static class AdaptivePredictionPolicies
{
    public const string LegacyPolicyId = "Legacy / Unversioned";

    // These values reproduce Adaptive Learning Phase 2 exactly. Keep this definition stable
    // after release; regression tests pin every parameter before a future policy is added.
    public static AdaptivePredictionPolicy PredictionCalibrationPolicyV1 { get; } = new(
        policyId: "PredictionCalibrationPolicyV1",
        minimumMeaningfulSamples: 5,
        moderateConfidenceSamples: 10,
        highConfidenceSamples: 20,
        moderateMaximumSignedIqrPercent: 25,
        highMaximumSignedIqrPercent: 10,
        moderateMaximumAbsoluteErrorPercent: 30,
        highMaximumAbsoluteErrorPercent: 15,
        biasNearTargetDeadbandPercent: 3,
        highConfidenceLearningStrength: .75,
        moderateConfidenceLearningStrength: .40,
        maximumEffectiveCorrectionPercent: 20,
        calibrationEvaluationDeadbandPercentagePoints: .5,
        minimumCalibrationEffectivenessSamples: 5,
        calibrationEffectivenessWindowSize: 20,
        maximumEffectiveWorsenedRatePercent: 40,
        harmfulMinimumWorsenedRatePercent: 60,
        calibrationRecoverySamples: 10,
        calibrationRecoveryMinimumImprovementPercent: 1.5,
        calibrationRecoveryMaximumWorsenedRatePercent: 20,
        mixedCalibrationLearningStrength: .5);

    private static readonly IReadOnlyDictionary<string, AdaptivePredictionPolicy> Definitions =
        new ReadOnlyDictionary<string, AdaptivePredictionPolicy>(new Dictionary<string, AdaptivePredictionPolicy>(StringComparer.Ordinal)
        {
            [PredictionCalibrationPolicyV1.PolicyId] = PredictionCalibrationPolicyV1
        });

    /// <summary>The deliberate selector used for new predictions.</summary>
    public static AdaptivePredictionPolicy Current => PredictionCalibrationPolicyV1;

    public static AdaptivePredictionPolicy? Find(string? policyId) =>
        Definitions.TryGetValue(NormalizePolicyId(policyId), out AdaptivePredictionPolicy? policy) ? policy : null;

    /// <summary>Empty IDs are historical data, not an alias for the current policy.</summary>
    public static string NormalizePolicyId(string? policyId) =>
        string.IsNullOrWhiteSpace(policyId) ? LegacyPolicyId : policyId.Trim();
}
