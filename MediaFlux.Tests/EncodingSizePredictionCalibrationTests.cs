using MediaFlux.Services;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingSizePredictionCalibrationTests
{
    private static readonly EncodingSizeCalibrationContext Context = new(
        "h264", "hevc", "1080p", "1080p", "nvenc", "gpu-a", "22", "Good", false);

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

        Assert.InRange(Math.Abs(capped.EffectiveCorrectionPercent!.Value), 0, EncodingPredictionAccuracyService.MaximumEffectiveCorrectionPercent);
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
            new[] { legacy }, legacy.CalibrationCohortKey, DateTime.UtcNow);

        Assert.Equal(0, evaluation.CalibratedCount);
        Assert.Equal(EncodingCalibrationEffectivenessState.NotEvaluated, effectiveness.State);
    }

    [Fact]
    public void EffectivenessStatesCoverNotEvaluatedEarlyEffectiveHarmfulAndMixed()
    {
        string key = EncodingPredictionAccuracyService.CalibrationCohortKey(Context);
        DateTime now = DateTime.UtcNow;
        Assert.Equal(EncodingCalibrationEffectivenessState.NotEvaluated,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness([], key, now).State);
        Assert.Equal(EncodingCalibrationEffectivenessState.Early,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
                Enumerable.Range(0, 4).Select(index => EvaluationRecord(index, key, now, 1_100, 1_000, 1_050)), key, now).State);
        Assert.Equal(EncodingCalibrationEffectivenessState.Effective,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
                Enumerable.Range(0, 5).Select(index => EvaluationRecord(index, key, now, 1_100, 1_000, 1_050)), key, now).State);
        Assert.Equal(EncodingCalibrationEffectivenessState.Harmful,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
                Enumerable.Range(0, 5).Select(index => EvaluationRecord(index, key, now, 900, 1_000, 1_200)), key, now).State);
        EncodingStatisticsRecord[] mixed = Enumerable.Range(0, 5).Select(index => index switch
        {
            0 or 1 => EvaluationRecord(index, key, now, 1_100, 1_000, 1_050),
            2 or 3 => EvaluationRecord(index, key, now, 900, 1_000, 1_200),
            _ => EvaluationRecord(index, key, now, 1_000, 1_000, 1_000)
        }).ToArray();
        Assert.Equal(EncodingCalibrationEffectivenessState.Mixed,
            EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(mixed, key, now).State);
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
            EncodingPredictionConfidence.High, 20, true, "example-cohort", "calibrated");
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
        Assert.Equal(EncodingCalibrationEffectivenessState.Harmful, result.EffectivenessState);
        EncodingCalibrationEffectiveness harmfulState = EncodingPredictionAccuracyService.EvaluateCalibrationEffectiveness(
            training.Concat(harmfulEvaluation), key, now.AddHours(1));
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
            .EvaluateCalibrationEffectiveness(shadow, key, DateTime.UtcNow);

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
        CalibrationCohortKey = cohortKey, CalibrationDecision = decision.ToString(),
        CalibrationDecisionUtc = decisionUtc, CalibrationEvidenceCutoffUtc = decisionUtc.AddMinutes(-1),
        CalibrationEffectivenessState = state.ToString(), CalibrationEffectivenessSinceUtc = effectivenessSince,
        CalibrationConfidence = EncodingPredictionConfidence.High.ToString(), CalibrationSampleCount = 20
    };
}
