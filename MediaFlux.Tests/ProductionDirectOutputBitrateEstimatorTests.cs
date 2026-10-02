using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ProductionDirectOutputBitrateEstimatorTests
{
    private static readonly DateTime Epoch = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string Signature = NvencQualityModeSettingsSignature.Create(
        "nvenc", "hevc_nvenc", "p5", 10, true);
    private static readonly ProductionDirectOutputBitrateEstimator Estimator = new();

    private static ProductionDirectOutputRequest Target(DateTime? cutoff = null,
        string? family = null) => new("h264", "hevc_nvenc", "nvenc", "p5", 10, 22,
        Signature, 1920, 1080, 29.97, QualityTarget.Balanced, 9000,
        family ?? "target-family", cutoff ?? Epoch.AddDays(2), false);

    private static EncodingStatisticsRecord Record(int index, double outputVideoKbps = 9400) => new()
    {
        Id = $"synthetic-{index}", EndUtc = Epoch.AddHours(index),
        Outcome = EncodingStatisticsOutcome.Success, TerminalResult = "Completed",
        IsSampleJob = false, RecoveredSuccessful = false, ScalingApplied = false,
        OutputBitDepth = 10, EncoderId = "nvenc", EncoderPreset = "p5",
        PredictionSourceCodec = "h264", PredictionTargetCodec = "hevc_nvenc",
        QualityModeSettingsSignature = Signature, MediaDurationSeconds = 1200,
        SourcePath = $"synthetic-{index}.mp4", SourceSizeBytes = 1_000_000_000 + index,
        OutputSizeBytes = 1_420_000_000,
        SourceAdaptiveShadow = new SourceAdaptiveShadowOutcome
        {
            Decision = new SourceAdaptiveShadowCalibration
            {
                Status = SourceAdaptiveShadowStatus.CalibrationCandidate,
                SourceCodec = "h264", OutputCodec = "hevc_nvenc", EncoderId = "nvenc",
                Preset = "p5", QualityTarget = QualityTarget.Balanced,
                FinalExecutionCq = 22, SourceVideoBitrateKbps = 9000,
                SourceTotalBytes = 1_000_000_000 + index,
                PlannedWidth = 1920, PlannedHeight = 1080, PlannedFps = 29.97,
                MaterialTransformationActive = false
            },
            ActualOutputCodec = "hevc", ActualOutputVideoBitrateKbps = outputVideoKbps,
            ActualOutputWidth = 1920, ActualOutputHeight = 1080, ActualOutputFps = 29.97
        }
    };

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RetryJobsCannotSupplyDirectOutputSupportOrHoldoutTargets(int mode)
    {
        var clean = Enumerable.Range(0, 12).Select(i => Record(i)).ToArray();
        var excluded = Enumerable.Range(20, 20).Select(i => BoundedRetryEvidence.Exclude(Record(i, 100_000), mode)).ToArray();
        Assert.Equal(0, Estimator.Predict(Target(), excluded).IndependentFamilies);
        var baseline = Estimator.Predict(Target(), clean);
        var mixed = Estimator.Predict(Target(), clean.Concat(excluded));
        Assert.Equal(baseline.IndependentFamilies, mixed.IndependentFamilies);
        Assert.Equal(baseline.PredictedVideoBitrateKbps, mixed.PredictedVideoBitrateKbps);
        Assert.Equal(baseline.HeldOutEligibleCount, mixed.HeldOutEligibleCount);
        Assert.Equal(baseline.HeldOutCount, mixed.HeldOutCount);
        Assert.Equal(baseline.MedianAbsoluteErrorPercent, mixed.MedianAbsoluteErrorPercent);
    }

    [Fact]
    public void ExactCohortUsesIndependentFamilyMedianAndForwardValidation()
    {
        EncodingStatisticsRecord[] history = Enumerable.Range(0, 12)
            .Select(i => Record(i, 9300 + 20 * i)).ToArray();
        ProductionDirectOutputResult result = Estimator.Predict(Target(), history);

        Assert.Equal(12, result.IndependentFamilies);
        Assert.Equal(ProductionDirectOutputStatus.Supported, result.Status);
        Assert.Equal(9410, result.PredictedVideoBitrateKbps);
        Assert.Equal(7, result.HeldOutCount);
        Assert.Equal(12, result.HeldOutEligibleCount);
        Assert.True(result.HeldOutCoverage >= .5);
        Assert.True(result.MedianAbsoluteErrorPercent <= 20);
        Assert.Equal(Epoch.AddDays(2), result.EvidenceCutoffUtc);
    }

    [Fact]
    public void RepeatsUseLatestPastOutcomeAndTargetFamilyIsExcluded()
    {
        EncodingStatisticsRecord[] history = Enumerable.Range(0, 12)
            .Select(i => Record(i)).ToArray();
        EncodingStatisticsRecord repeat = Record(20, 9700) with
        {
            SourceAdaptiveShadow = Record(5).SourceAdaptiveShadow,
            MediaDurationSeconds = Record(5).MediaDurationSeconds
        };
        string targetFamily = ProductionDirectOutputBitrateEstimator.SourceFamily(
            history[0].SourceAdaptiveShadow!.Decision, history[0].MediaDurationSeconds,
            history[0].SourcePath);
        ProductionDirectOutputResult result = Estimator.Predict(Target(family: targetFamily),
            history.Append(repeat));

        Assert.Equal(11, result.IndependentFamilies);
        Assert.Equal(9400, result.PredictedVideoBitrateKbps);
        ProductionDirectOutputResult noTarget = Estimator.Predict(Target(), history.Append(repeat));
        Assert.Equal(12, noTarget.IndependentFamilies);
        Assert.Equal(9400, noTarget.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void FutureOutcomeCannotChangeEarlierPrediction()
    {
        EncodingStatisticsRecord[] history = Enumerable.Range(0, 12)
            .Select(i => Record(i)).ToArray();
        DateTime cutoff = Epoch.AddHours(15);
        ProductionDirectOutputResult before = Estimator.Predict(Target(cutoff), history);
        ProductionDirectOutputResult after = Estimator.Predict(Target(cutoff),
            history.Append(Record(16, 100_000)));
        Assert.Equal(before, after);
        Assert.Equal(ProductionDirectOutputStatus.Supported, before.Status);
    }

    [Fact]
    public void MinimumSupportAndBadAccuracyBothAbstain()
    {
        EncodingStatisticsRecord[] stable = Enumerable.Range(0, 9).Select(i => Record(i)).ToArray();
        Assert.Equal(ProductionDirectOutputStatus.InsufficientIndependentFamilies,
            Estimator.Predict(Target(), stable).Status);
        EncodingStatisticsRecord[] unstable = Enumerable.Range(0, 12)
            .Select(i => Record(i, i % 2 == 0 ? 4000 : 14000)).ToArray();
        ProductionDirectOutputResult failure = Estimator.Predict(Target(), unstable);
        Assert.Equal(ProductionDirectOutputStatus.AccuracyGateFailed, failure.Status);
        Assert.Equal(7, failure.HeldOutCount);
        Assert.True(failure.HeldOutCoverage >= .5);
        Assert.True(failure.MedianAbsoluteErrorPercent > 20);
        Assert.Null(failure.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void IncompatibleOrIncompleteRecordDoesNotSupplySupport()
    {
        EncodingStatisticsRecord original = Record(9);
        EncodingStatisticsRecord[] bad =
        [
            original with { Outcome = EncodingStatisticsOutcome.Failed },
            original with { RecoveredSuccessful = true },
            original with { IsSampleJob = true },
            original with { TerminalResult = "CompletedAfterRecovery" },
            original with { EncoderId = "cpu" },
            original with { EncoderPreset = "p6" },
            original with { OutputBitDepth = 8 },
            original with { PredictionSourceCodec = "av1" },
            original with { PredictionTargetCodec = "h264" },
            original with { QualityModeSettingsSignature = "different" },
            original with { ScalingApplied = true },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { ActualOutputCodec = "h264" } },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { ActualOutputVideoBitrateKbps = null } },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { Decision = original.SourceAdaptiveShadow.Decision with { FinalExecutionCq = 21 } } },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { Decision = original.SourceAdaptiveShadow.Decision with { QualityTarget = QualityTarget.HighQuality } } },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { Decision = original.SourceAdaptiveShadow.Decision with { MaterialTransformationActive = true } } },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { Decision = original.SourceAdaptiveShadow.Decision with { PlannedWidth = 1280 } } },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { Decision = original.SourceAdaptiveShadow.Decision with { PlannedFps = 24 } } },
            original with { SourceAdaptiveShadow = original.SourceAdaptiveShadow! with
                { Decision = original.SourceAdaptiveShadow.Decision with { SourceVideoBitrateKbps = 3000 } } }
        ];
        EncodingStatisticsRecord[] baseNine = Enumerable.Range(0, 9)
            .Select(i => Record(i)).ToArray();
        foreach (EncodingStatisticsRecord invalid in bad)
        {
            ProductionDirectOutputResult result = Estimator.Predict(Target(), baseNine.Append(invalid));
            Assert.Equal(ProductionDirectOutputStatus.InsufficientIndependentFamilies, result.Status);
            Assert.Equal(9, result.IndependentFamilies);
        }
    }

    [Fact]
    public void SupportedDirectVideoBuildsDurationAudioAncillaryAndContainerSize()
    {
        const double durationSeconds = 1200;
        SizeEstimateBreakdown generic = new()
        {
            EstimatedOutputMb = 500, TargetVideoBitrateKbps = 3500,
            PlannedAudioBitrateKbps = 192, PlannedMappedAncillaryBitrateKbps = 8
        };
        ProductionDirectOutputResult supported = Estimator.Predict(Target(),
            Enumerable.Range(0, 12).Select(i => Record(i, 9400)));
        SizeEstimateBreakdown direct =
            ProductionDirectOutputBitrateEstimator.SelectSizeBreakdown(generic, durationSeconds, supported);
        Assert.Equal(9400, direct.TargetVideoBitrateKbps);
        Assert.Equal(192, direct.PlannedAudioBitrateKbps);
        Assert.Equal(8, direct.PlannedMappedAncillaryBitrateKbps);
        Assert.True(direct.TargetTotalBitrateKbps > 9600);
        Assert.Equal(direct.TargetTotalBitrateKbps * durationSeconds / 8192,
            direct.EstimatedOutputMb, 8);
        Assert.Same(generic, ProductionDirectOutputBitrateEstimator.SelectSizeBreakdown(
            generic, durationSeconds, Estimator.Predict(Target(), [Record(0)])));
    }

    [Fact]
    public void SyntheticNaturalCq22CohortReplacesSeverelyLowGenericEstimate()
    {
        const double durationSeconds = 1200;
        SizeEstimateBreakdown generic =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                1250, durationSeconds, 1920, 1080, 29.97, 9000, "h264",
                "Medium Quality (Default)", "hevc_nvenc", 22, null, 192, 1);
        ProductionDirectOutputResult result = Estimator.Predict(Target(),
            Enumerable.Range(0, 12).Select(i => Record(i, 9340 + (i % 3 - 1) * 80)));
        SizeEstimateBreakdown direct =
            ProductionDirectOutputBitrateEstimator.SelectSizeBreakdown(generic, durationSeconds, result);
        Assert.Equal(ProductionDirectOutputStatus.Supported, result.Status);
        Assert.InRange(result.PredictedVideoBitrateKbps!.Value, 9200, 9500);
        Assert.True(direct.EstimatedOutputMb > generic.EstimatedOutputMb * 2);
    }

    [Fact]
    public void SignatureMatchesExistingProductionStatisticsSignature()
    {
        Assert.Equal(NvencQualityModeVideoBitratePredictionService.EffectiveSettingsSignature(
            "nvenc", "hevc_nvenc", "p5", 10, true), Signature);
        Assert.NotEqual(Signature, NvencQualityModeSettingsSignature.Create(
            "nvenc", "hevc_nvenc", "p5", 10, false));
    }
}
