using MediaFlux.Services;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingSizePredictionCalibrationTests
{
    private static readonly EncodingSizeCalibrationContext Context = new(
        "h264", "hevc", "1080p", "1080p", "nvenc", "gpu-a", "22", "Good", false);

    [Fact]
    public void PolicyV1IsAnImmutableNamedSnapshotOfPhaseTwoParameters()
    {
        AdaptivePredictionPolicy policy = AdaptivePredictionPolicies.Current;

        Assert.Equal("PredictionCalibrationPolicyV1", policy.PolicyId);
        Assert.Same(AdaptivePredictionPolicies.PredictionCalibrationPolicyV1, policy);
        Assert.Equal(5, policy.MinimumMeaningfulSamples);
        Assert.Equal(10, policy.ModerateConfidenceSamples);
        Assert.Equal(20, policy.HighConfidenceSamples);
        Assert.Equal(25, policy.ModerateMaximumSignedIqrPercent);
        Assert.Equal(10, policy.HighMaximumSignedIqrPercent);
        Assert.Equal(30, policy.ModerateMaximumAbsoluteErrorPercent);
        Assert.Equal(15, policy.HighMaximumAbsoluteErrorPercent);
        Assert.Equal(3, policy.BiasNearTargetDeadbandPercent);
        Assert.Equal(.75, policy.HighConfidenceLearningStrength);
        Assert.Equal(.40, policy.ModerateConfidenceLearningStrength);
        Assert.Equal(20, policy.MaximumEffectiveCorrectionPercent);
        Assert.Equal(.5, policy.CalibrationEvaluationDeadbandPercentagePoints);
        Assert.Equal(5, policy.MinimumCalibrationEffectivenessSamples);
        Assert.Equal(20, policy.CalibrationEffectivenessWindowSize);
        Assert.Equal(40, policy.MaximumEffectiveWorsenedRatePercent);
        Assert.Equal(60, policy.HarmfulMinimumWorsenedRatePercent);
        Assert.Equal(10, policy.CalibrationRecoverySamples);
        Assert.Equal(1.5, policy.CalibrationRecoveryMinimumImprovementPercent);
        Assert.Equal(20, policy.CalibrationRecoveryMaximumWorsenedRatePercent);
        Assert.Equal(.5, policy.MixedCalibrationLearningStrength);
    }

    [Fact]
    public void EveryEvaluatedPredictionRecordsTheSelectedPolicyAndAuditValues()
    {
        EncodingPredictionAccuracyService service = new();
        EncodingSizePredictionCalibration applied = service.CalibrateSizePrediction(
            100, Context, History(20, 1_100, 1_000), enabled: true);
        EncodingSizePredictionCalibration disabled = service.CalibrateSizePrediction(
            100, Context, History(20, 1_100, 1_000), enabled: false);
        EncodingSizePredictionCalibration notEligible = service.CalibrateSizePrediction(
            100, Context, History(20, 1_100, 1_000), enabled: true, eligible: false);
        EncodingSizePredictionCalibration noHistory = service.CalibrateSizePrediction(
            100, Context, Array.Empty<EncodingStatisticsRecord>(), enabled: true);

        Assert.All(new[] { applied, disabled, notEligible, noHistory }, result =>
            Assert.Equal(AdaptivePredictionPolicies.Current.PolicyId, result.PolicyId));
        Assert.Equal(.75, applied.LearningStrength);
        Assert.Equal(10, applied.RawHistoricalCorrectionPercent);
        Assert.Equal(7.5, applied.AppliedCorrectionPercent);
        Assert.Equal(.75, disabled.LearningStrength);
        Assert.Equal(0, disabled.AppliedCorrectionPercent);
        Assert.Equal(EncodingCalibrationDecision.DisabledByUser, disabled.Decision);
        Assert.Equal(EncodingCalibrationDecision.NotEligible, notEligible.Decision);
        Assert.Equal(0, notEligible.LearningStrength);
        Assert.Equal(EncodingCalibrationDecision.InsufficientHistoricalConfidence, noHistory.Decision);
    }

    [Fact]
    public void PolicyEffectivenessAndComparisonsKeepLegacyAndVersionsSeparate()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        DateTime now = DateTime.UtcNow;
        EncodingStatisticsRecord v1 = EvaluationRecord(1, key, now, 1_100, 1_000, 1_050);
        EncodingStatisticsRecord v2 = v1 with { Id = "v2", CalibrationPolicyId = "PredictionCalibrationPolicyV2" };
        EncodingStatisticsRecord legacy = v1 with { Id = "legacy-unversioned", CalibrationPolicyId = "" };

        EncodingCalibrationEffectiveness[] effectiveness = EncodingPredictionAccuracyService
            .BuildCalibrationEffectiveness(new[] { v1, v2, legacy }, now).ToArray();
        EncodingCalibrationPolicyComparison[] comparisons = EncodingPredictionAccuracyService
            .BuildCalibrationPolicyComparisons(new[] { v1, v2, legacy }).ToArray();

        Assert.Equal(3, effectiveness.Length);
        Assert.Contains(effectiveness, item => item.PolicyId == AdaptivePredictionPolicies.Current.PolicyId && item.EvaluationCount == 1);
        Assert.Contains(effectiveness, item => item.PolicyId == "PredictionCalibrationPolicyV2" && item.State == EncodingCalibrationEffectivenessState.NotEvaluated);
        Assert.Contains(effectiveness, item => item.PolicyId == AdaptivePredictionPolicies.LegacyPolicyId && item.State == EncodingCalibrationEffectivenessState.NotEvaluated);
        Assert.Equal(3, comparisons.Length);
        Assert.All(comparisons, item => Assert.Equal(key, item.CohortKey));
        Assert.Contains(comparisons, item => item.PolicyId == AdaptivePredictionPolicies.Current.PolicyId);
        Assert.Contains(comparisons, item => item.PolicyId == "PredictionCalibrationPolicyV2" && item.EvaluationCount == 0);
        Assert.Contains(comparisons, item => item.PolicyId == AdaptivePredictionPolicies.LegacyPolicyId && item.EvaluationCount == 1);
    }

    [Fact]
    public void HighConfidenceCohortAppliesBoundedCorrectionToDisplayEstimate()
    {
        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(100, Context, History(20, actualBytes: 1_100, predictedBytes: 1_000), enabled: true);

        Assert.True(result.Applied);
        Assert.Equal(7.5, result.EffectiveCorrectionPercent!.Value, 6);
        Assert.Equal(107.5, result.CalibratedPredictionMb!.Value, 6);
        Assert.Equal(100, result.BasePredictionMb);
        Assert.Equal(20, result.SampleCount);
    }

    [Fact]
    public void InsufficientOrDisabledCohortRetainsBaseEstimate()
    {
        var service = new EncodingPredictionAccuracyService();
        EncodingSizePredictionCalibration insufficient = service.CalibrateSizePrediction(
            100, Context, History(4, 1_100, 1_000), enabled: true);
        EncodingSizePredictionCalibration disabled = service.CalibrateSizePrediction(
            100, Context, History(20, 1_100, 1_000), enabled: false);

        Assert.False(insufficient.Applied);
        Assert.Equal(100, insufficient.CalibratedPredictionMb);
        Assert.False(disabled.Applied);
        Assert.Equal(100, disabled.CalibratedPredictionMb);
    }

    [Theory]
    [InlineData(1_100, 107.5)]
    [InlineData(900, 92.5)]
    public void HighConfidenceCalibrationCorrectsInEitherDirection(long actualBytes, double expectedMb)
    {
        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(100, Context, History(20, actualBytes, 1_000), enabled: true);
        Assert.True(result.Applied);
        Assert.Equal(expectedMb, result.CalibratedPredictionMb!.Value, 6);
    }

    [Fact]
    public void ModerateConfidenceUsesReducedLearningStrength()
    {
        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(100, Context, History(10, 1_200, 1_000), enabled: true);
        Assert.Equal(EncodingPredictionConfidence.Moderate, result.Confidence);
        Assert.True(result.Applied);
        Assert.Equal(8, result.EffectiveCorrectionPercent!.Value, 6);
        Assert.Equal(108, result.CalibratedPredictionMb!.Value, 6);
    }

    [Fact]
    public void RecoveredAndSampleAttemptsDoNotTrainCalibration()
    {
        EncodingStatisticsRecord[] excluded = History(20, 1_100, 1_000)
            .Select((record, index) => record with { RecoveredSuccessful = index % 2 == 0, IsSampleJob = index % 2 != 0 })
            .ToArray();
        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(100, Context, excluded, enabled: true);
        Assert.Equal(0, result.SampleCount);
        Assert.False(result.Applied);
        Assert.Equal(100, result.CalibratedPredictionMb);
    }

    [Fact]
    public void CorrectionIsCappedAndCohortMustMatchAllContext()
    {
        var service = new EncodingPredictionAccuracyService();
        EncodingSizePredictionCalibration capped = service.CalibrateSizePrediction(
            100, Context, History(20, 2_000, 1_000), enabled: true);
        EncodingSizePredictionCalibration unmatched = service.CalibrateSizePrediction(
            100, Context with { Quality = "20" }, History(20, 1_100, 1_000), enabled: true);

        Assert.InRange(Math.Abs(capped.EffectiveCorrectionPercent!.Value), 0, AdaptivePredictionPolicies.Current.MaximumEffectiveCorrectionPercent);
        Assert.Equal(100, capped.CalibratedPredictionMb);
        Assert.Equal(0, unmatched.SampleCount);
        Assert.False(unmatched.Applied);
        Assert.Equal(100, unmatched.CalibratedPredictionMb);
    }

    [Fact]
    public void EvaluationSeparatesBaseAndCalibratedErrors()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        EncodingStatisticsRecord record = EvaluationRecord(0, key, DateTime.UtcNow, 1_100, 1_000, 1_075);

        EncodingCalibrationEvaluation result = EncodingPredictionAccuracyService.EvaluateCalibrations(new[] { record });

        Assert.Equal(1, result.CalibratedCount);
        Assert.Equal(10, result.MedianBaseSignedErrorPercent);
        Assert.Equal(100d * 25 / 1_075, result.MedianCalibratedSignedErrorPercent!.Value, 6);
        Assert.Equal(EncodingCalibrationOutcome.Improved, Assert.Single(result.Rows).Outcome);
    }

    [Fact]
    public void LegacyPhaseOneRecordsRemainReadableButDoNotBecomeEvaluationEvidence()
    {
        EncodingStatisticsRecord legacy = History(1, 1_100, 1_000).Single() with
        {
            CalibrationApplied = true,
            CalibrationCohortKey = EncodingPredictionAccuracyService.CalibrationCohortKey(Context),
            BasePredictedOutputSizeBytes = 1_000,
            PredictedOutputSizeBytes = 1_075
        };
        EncodingCalibrationEvaluation evaluation = EncodingPredictionAccuracyService.EvaluateCalibrations(new[] { legacy });
        EncodingCalibrationEffectiveness effectiveness = EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
            new[] { legacy }, legacy.CalibrationCohortKey, AdaptivePredictionPolicies.LegacyPolicyId, DateTime.UtcNow);
        EncodingCalibrationEffectiveness unversionedProjection = Assert.Single(
            EncodingPredictionAccuracyService.BuildCalibrationEffectiveness(new[] { legacy }));

        Assert.Equal(0, evaluation.CalibratedCount);
        Assert.Equal(EncodingCalibrationEffectivenessState.NotEvaluated, effectiveness.State);
        Assert.Equal(AdaptivePredictionPolicies.LegacyPolicyId, unversionedProjection.PolicyId);
        Assert.Equal(AdaptivePredictionPolicies.LegacyPolicyId,
            AdaptivePredictionPolicies.NormalizePolicyId(legacy.CalibrationPolicyId));
    }

    [Fact]
    public void EffectivenessStatesCoverNotEvaluatedEarlyEffectiveHarmfulAndMixed()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        DateTime now = DateTime.UtcNow;
        Assert.Equal(EncodingCalibrationEffectivenessState.NotEvaluated,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness([], key, AdaptivePredictionPolicies.Current.PolicyId, now).State);
        Assert.Equal(EncodingCalibrationEffectivenessState.Early,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
                Enumerable.Range(0, 4).Select(index => EvaluationRecord(index, key, now, 1_100, 1_000, 1_050)), key, AdaptivePredictionPolicies.Current.PolicyId, now).State);
        Assert.Equal(EncodingCalibrationEffectivenessState.Effective,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
                Enumerable.Range(0, 5).Select(index => EvaluationRecord(index, key, now, 1_100, 1_000, 1_050)), key, AdaptivePredictionPolicies.Current.PolicyId, now).State);
        Assert.Equal(EncodingCalibrationEffectivenessState.Harmful,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
                Enumerable.Range(0, 5).Select(index => EvaluationRecord(index, key, now, 900, 1_000, 1_200)), key, AdaptivePredictionPolicies.Current.PolicyId, now).State);
        EncodingStatisticsRecord[] mixed = Enumerable.Range(0, 5).Select(index => index switch
        {
            0 or 1 => EvaluationRecord(index, key, now, 1_100, 1_000, 1_050),
            2 or 3 => EvaluationRecord(index, key, now, 900, 1_000, 1_200),
            _ => EvaluationRecord(index, key, now, 1_000, 1_000, 1_000)
        }).ToArray();
        Assert.Equal(EncodingCalibrationEffectivenessState.Mixed,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(mixed, key, AdaptivePredictionPolicies.Current.PolicyId, now).State);
    }

    [Fact]
    public void MixedEffectivenessAttenuatesButDoesNotChangeEncodingPolicy()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        DateTime now = DateTime.UtcNow;
        EncodingStatisticsRecord[] mixed = Enumerable.Range(0, 5).Select(index => index switch
        {
            0 or 1 => EvaluationRecord(index, key, now.AddMinutes(index), 1_100, 1_000, 1_050),
            2 or 3 => EvaluationRecord(index, key, now.AddMinutes(index), 900, 1_000, 1_200),
            _ => EvaluationRecord(index, key, now.AddMinutes(index), 1_000, 1_000, 1_000)
        }).ToArray();
        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(100, Context, History(100, 1_100, 1_000).Concat(mixed), enabled: true,
                decisionUtc: now.AddHours(1));

        Assert.Equal(EncodingCalibrationEffectivenessState.Mixed, result.EffectivenessState);
        Assert.Equal(EncodingCalibrationDecision.Applied, result.Decision);
        Assert.Equal(3.75, result.EffectiveCorrectionPercent!.Value, 6);
        Assert.Equal(.375, result.LearningStrength, 6);
        Assert.Equal(100, result.BasePredictionMb);
    }

    [Fact]
    public void EvaluationDeadbandClassifiesBoundaryAsNeutral()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        EncodingStatisticsRecord record = EvaluationRecord(0, key, DateTime.UtcNow, 1_005, 1_000, 1_005);
        EncodingCalibrationEvaluationRow evaluated = Assert.Single(EncodingPredictionAccuracyService.EvaluateCalibrations(new[] { record }).Rows);
        Assert.Equal(EncodingCalibrationOutcome.Neutral, evaluated.Outcome);
    }

    [Fact]
    public void CalibrationPlanMetadataDoesNotChangeEncodingControlsOrRecommendation()
    {
        var context = new EncodingDecisionContext(
            new MediaProbeResult { Success = true, Streams =
            [new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 30, BitRate = 8_000_000 }] },
            EncodingInputSource.FromFile("source.mkv"),
            new VideoEncoderSelection(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265"),
            UseGpu: false, TargetMb: null, ScaleMode: EncodingService.ScaleMode.None,
            Restoration: new VideoRestorationSettings(), EncoderPreset: "slow", QualityValue: 24,
            TenBit: false, AudioChannels: null, MapMode: EncodingService.StreamMapMode.KeepAll,
            CopySubtitles: true, CopyDataStreams: true, CopyAttachments: true,
            ContainerConfigured: OutputContainerSelection.Mp4,
            CompatibilityPolicy: ContainerCompatibilityPolicy.Intelligent,
            KnownDuration: TimeSpan.FromMinutes(10),
            QualityIntent: EncodingQualityIntent.Automatic(QualityTarget.Balanced));
        EncodingPlan plain = EncodingPlanService.Create(context);
        var calibration = new EncodingSizePredictionCalibration(100, 107.5, 7.5, 10,
            EncodingPredictionConfidence.High, 20, true, "example-cohort", "calibrated",
            PolicyId: AdaptivePredictionPolicies.Current.PolicyId);
        EncodingPlan annotated = EncodingPlanService.Create(context with { SizePredictionCalibration = calibration });
        EncodingPlanService.EncodingPlanExecutionValues plainExecution = EncodingPlanService.GetExecutionValues(plain);
        EncodingPlanService.EncodingPlanExecutionValues calibratedExecution = EncodingPlanService.GetExecutionValues(annotated);

        Assert.Equal(plain.Video, annotated.Video);
        Assert.Equal(plain.Quality!.Intent, annotated.Quality!.Intent);
        Assert.Equal(plain.Quality.EffectiveQuality, annotated.Quality.EffectiveQuality);
        Assert.Equal(plain.Quality.Mechanism, annotated.Quality.Mechanism);
        Assert.Equal(plain.Quality.Assessment, annotated.Quality.Assessment);
        Assert.Equal(plain.Quality.IsSupersededByTargetSize, annotated.Quality.IsSupersededByTargetSize);
        Assert.Equal(plain.Quality.Reasons, annotated.Quality.Reasons);
        Assert.Equal(plain.Container, annotated.Container);
        Assert.Equal(plain.Recommendation!.Recommendation, annotated.Recommendation!.Recommendation);
        Assert.Equal(plain.Recommendation.PrimaryReason, annotated.Recommendation.PrimaryReason);
        Assert.Equal(plain.Recommendation.SupportingReasons, annotated.Recommendation.SupportingReasons);
        Assert.Equal(plainExecution.TargetMb, calibratedExecution.TargetMb);
        Assert.Equal(plainExecution.Encoder, calibratedExecution.Encoder);
        Assert.Equal(plainExecution.UseGpu, calibratedExecution.UseGpu);
        Assert.Equal(plainExecution.Geometry, calibratedExecution.Geometry);
        Assert.Equal(plainExecution.ContainerDecision.Requested, calibratedExecution.ContainerDecision.Requested);
        Assert.Equal(plainExecution.ContainerDecision.Resolved, calibratedExecution.ContainerDecision.Resolved);
        Assert.Equal(plainExecution.ContainerDecision.CopySubtitles, calibratedExecution.ContainerDecision.CopySubtitles);
        Assert.Equal(plainExecution.ContainerDecision.CopyDataStreams, calibratedExecution.ContainerDecision.CopyDataStreams);
        Assert.Equal(plainExecution.ContainerDecision.CopyAttachments, calibratedExecution.ContainerDecision.CopyAttachments);
        Assert.Equal(plainExecution.ContainerDecision.StreamPlans, calibratedExecution.ContainerDecision.StreamPlans);
        Assert.Equal(plainExecution.QualityResolution.EffectiveQuality, calibratedExecution.QualityResolution.EffectiveQuality);
        Assert.Same(calibration, annotated.SizePredictionCalibration);
    }

    [Fact]
    public void HarmfulCohortSuppressesDisplayButRetainsHypotheticalCandidate()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        DateTime now = DateTime.UtcNow;
        EncodingStatisticsRecord[] training = History(100, 1_100, 1_000);
        EncodingStatisticsRecord[] harmfulEvaluation = Enumerable.Range(0, 5)
            .Select(index => EvaluationRecord(index, key, now.AddMinutes(index), 900, 1_000, 1_200))
            .ToArray();
        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(100, Context, training.Concat(harmfulEvaluation), enabled: true,
                decisionUtc: now.AddHours(1));

        Assert.Equal(EncodingCalibrationDecision.ShadowEvaluationOnly, result.Decision);
        Assert.Equal(AdaptivePredictionPolicies.Current.PolicyId, result.PolicyId);
        Assert.Equal(EncodingCalibrationEffectivenessState.Harmful, result.EffectivenessState);
        EncodingCalibrationEffectiveness harmfulState = EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
            training.Concat(harmfulEvaluation), key, AdaptivePredictionPolicies.Current.PolicyId, now.AddHours(1));
        Assert.False(harmfulState.CurrentlyEligible);
        Assert.False(result.Applied);
        Assert.Equal(100, result.CalibratedPredictionMb);
        Assert.Equal(107.5, result.HypotheticalCalibratedPredictionMb!.Value, 6);
    }

    [Fact]
    public void EffectiveCohortContinuesApplyingTheFullPhaseOneCorrection()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        DateTime now = DateTime.UtcNow;
        EncodingStatisticsRecord[] effectiveEvaluation = Enumerable.Range(0, 5)
            .Select(index => EvaluationRecord(index, key, now.AddMinutes(index), 1_100, 1_000, 1_050))
            .ToArray();

        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(100, Context, History(100, 1_100, 1_000).Concat(effectiveEvaluation),
                enabled: true, decisionUtc: now.AddHours(1));

        Assert.Equal(EncodingCalibrationEffectivenessState.Effective, result.EffectivenessState);
        Assert.Equal(EncodingCalibrationDecision.Applied, result.Decision);
        Assert.Equal(7.5, result.EffectiveCorrectionPercent!.Value, 6);
        Assert.Equal(107.5, result.CalibratedPredictionMb!.Value, 6);
    }

    [Fact]
    public void ShadowEvidenceMustMeetStrongerRecoveryThresholdAndStartsFreshEpoch()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        DateTime since = DateTime.UtcNow.AddDays(-2);
        EncodingStatisticsRecord[] shadow = Enumerable.Range(0, 10)
            .Select(index => EvaluationRecord(index, key, since.AddHours(index + 1), 1_100, 1_000, 1_040,
                EncodingCalibrationDecision.ShadowEvaluationOnly, EncodingCalibrationEffectivenessState.Harmful, since))
            .ToArray();

        EncodingCalibrationEffectiveness effectiveness = EncodingPredictionAccuracyService
            .EvaluateCalibrationEffectiveness(shadow, key, AdaptivePredictionPolicies.Current.PolicyId, DateTime.UtcNow);

        Assert.Equal(10, effectiveness.EvaluationCount);
        Assert.Equal(EncodingCalibrationEffectivenessState.Effective, effectiveness.State);
        Assert.True(effectiveness.EffectivenessSinceUtc > since);
    }

    [Fact]
    public void DisabledAndLowConfidenceCohortsNeverApplyCalibration()
    {
        var service = new EncodingPredictionAccuracyService();
        EncodingSizePredictionCalibration disabled = service.CalibrateSizePrediction(
            100, Context, History(20, 1_100, 1_000), enabled: false);
        EncodingSizePredictionCalibration low = service.CalibrateSizePrediction(
            100, Context, History(5, 1_400, 1_000), enabled: true);
        Assert.Equal(EncodingCalibrationDecision.DisabledByUser, disabled.Decision);
        Assert.False(disabled.Applied);
        Assert.Equal(EncodingCalibrationDecision.InsufficientHistoricalConfidence, low.Decision);
        Assert.False(low.Applied);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidBaseEstimateIsNeverCalibrated(double baseMb)
    {
        EncodingSizePredictionCalibration result = new EncodingPredictionAccuracyService()
            .CalibrateSizePrediction(baseMb, Context, History(20, 1_100, 1_000), enabled: true);
        Assert.False(result.Applied);
    }

    private static EncodingStatisticsRecord[] History(int count, long actualBytes, long predictedBytes) =>
        Enumerable.Range(0, count).Select(index => new EncodingStatisticsRecord
        {
            Id = $"calibration-{index}", StartUtc = DateTime.UtcNow.AddMinutes(-index - 2), EndUtc = DateTime.UtcNow.AddMinutes(-index - 1),
            Outcome = EncodingStatisticsOutcome.Success, SourceSizeBytes = 3_000, OutputSizeBytes = actualBytes,
            ProcessingSeconds = 60, EncoderId = "nvenc", HardwareKey = "gpu-a", Encoder = "NVENC",
            Codec = "hevc", PredictionSourceCodec = "h264", PredictionTargetCodec = "hevc", PredictionSameCodec = false,
            SourceResolutionTier = "1080p", OutputResolutionTier = "1080p", PredictionQuality = "22",
            PredictionAssessment = "Good", PredictedOutputSizeBytes = predictedBytes
        }).ToArray();

    private static EncodingStatisticsRecord EvaluationRecord(int index, string cohortKey, DateTime decisionUtc,
        long actualBytes, long basePredictionBytes, long candidateBytes,
        EncodingCalibrationDecision decision = EncodingCalibrationDecision.Applied,
        EncodingCalibrationEffectivenessState state = EncodingCalibrationEffectivenessState.Early,
        DateTime? effectivenessSince = null) => new()
    {
        Id = $"evaluation-{index}", StartUtc = decisionUtc.AddSeconds(-30), EndUtc = decisionUtc.AddSeconds(30),
        Outcome = EncodingStatisticsOutcome.Success, SourceSizeBytes = 3_000, OutputSizeBytes = actualBytes,
        ProcessingSeconds = 60, EncoderId = "nvenc", HardwareKey = "gpu-a", Encoder = "NVENC",
        Codec = "hevc", PredictionSourceCodec = "h264", PredictionTargetCodec = "hevc", PredictionSameCodec = false,
        SourceResolutionTier = "1080p", OutputResolutionTier = "1080p", PredictionQuality = "22", PredictionAssessment = "Good",
        PredictedOutputSizeBytes = decision == EncodingCalibrationDecision.Applied ? candidateBytes : basePredictionBytes,
        BasePredictedOutputSizeBytes = basePredictionBytes, HypotheticalCalibratedOutputSizeBytes = candidateBytes,
        CalibrationApplied = decision == EncodingCalibrationDecision.Applied,
        CalibrationCohortKey = cohortKey, CalibrationPolicyId = AdaptivePredictionPolicies.Current.PolicyId,
        CalibrationDecision = decision.ToString(),
        CalibrationDecisionUtc = decisionUtc, CalibrationEvidenceCutoffUtc = decisionUtc.AddMinutes(-1),
        CalibrationEffectivenessState = state.ToString(), CalibrationEffectivenessSinceUtc = effectivenessSince,
        CalibrationConfidence = EncodingPredictionConfidence.High.ToString(), CalibrationSampleCount = 20
    };
}
