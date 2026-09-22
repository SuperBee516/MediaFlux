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
        EncodingStatisticsRecord record = History(1, 1_100, 1_000).Single() with
        {
            BasePredictedOutputSizeBytes = 1_000,
            PredictedOutputSizeBytes = 1_075,
            CalibrationApplied = true
        };

        EncodingCalibrationEvaluation result = EncodingPredictionAccuracyService.EvaluateCalibrations(new[] { record });

        Assert.Equal(1, result.CalibratedCount);
        Assert.Equal(10, result.MedianBaseSignedErrorPercent);
        Assert.Equal(100d * 25 / 1_075, result.MedianCalibratedSignedErrorPercent!.Value, 6);
        Assert.Equal(EncodingCalibrationOutcome.Improved, Assert.Single(result.Rows).Outcome);
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
}
