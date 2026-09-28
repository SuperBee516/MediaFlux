using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class NvencQualityModeVideoBitratePredictionTests
{
    private static readonly string Signature = NvencQualityModeVideoBitratePredictionService
        .EffectiveSettingsSignature(VideoEncoderIds.Nvenc, "hevc", "p5", 10, true);

    [Fact]
    public void SignatureCapturesEffectiveTuningAndRejectsUnknownSettings()
    {
        Assert.Contains("aq=1:8", Signature);
        Assert.Contains("depth=10", Signature);
        Assert.NotEqual(Signature, NvencQualityModeVideoBitratePredictionService
            .EffectiveSettingsSignature(VideoEncoderIds.Nvenc, "hevc", "p5", 10, false));
        Assert.NotEqual(Signature, NvencQualityModeVideoBitratePredictionService
            .EffectiveSettingsSignature(VideoEncoderIds.Nvenc, "hevc", "p6", 10, true));
        Assert.Equal("", NvencQualityModeVideoBitratePredictionService
            .EffectiveSettingsSignature(VideoEncoderIds.Nvenc, "hevc", "p5", null, true));
    }

    [Fact]
    public void ColdStartAndSingleIndependentSourceAbstain()
    {
        Assert.Equal(QualityModePredictionReason.NoComparableHistory, Predict([]).Reason);
        var oneSource = new[] { Record("a", 3000), Record("a", 7000) };
        NvencQualityModePredictionResult prediction = Predict(oneSource);
        Assert.Equal(QualityModePredictionReason.InsufficientIndependentSources, prediction.Reason);
        Assert.Equal(1, prediction.IndependentSourceCount);
        Assert.Null(prediction.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void ComparableSameCqSourcesPredictVideoRatioWithoutTotalSize()
    {
        QualityModePairedPrediction pair = PredictBoth([Record("a", 3000), Record("b", 6000)]);
        Assert.Equal(QualityModePredictionConfidence.Low, pair.Ratio.Confidence);
        Assert.Equal(2, pair.Ratio.IndependentSourceCount);
        Assert.Equal(4500, pair.Ratio.PredictedVideoBitrateKbps);
        Assert.Equal(4500, pair.Direct.PredictedVideoBitrateKbps);
        Assert.Equal(pair.Ratio.IndependentSourceCount, pair.Direct.IndependentSourceCount);
        Assert.Equal(pair.AdmittedPeerSourceFamilyKeys, new[] { "a", "b" });
    }

    [Fact]
    public void DirectUsesOddAndEvenMediansAndRequiresTwoIndependentPeers()
    {
        QualityModePairedPrediction odd = PredictBoth([Record("a", 1000), Record("b", 5000), Record("c", 9000)]);
        Assert.Equal(5000, odd.Direct.PredictedVideoBitrateKbps);

        QualityModePairedPrediction even = PredictBoth([Record("a", 1000), Record("b", 8000)]);
        Assert.Equal(4500, even.Direct.PredictedVideoBitrateKbps);

        QualityModePairedPrediction none = PredictBoth([]);
        Assert.Equal(QualityModePredictionReason.NoComparableHistory, none.Direct.Reason);
        Assert.Null(none.Direct.PredictedVideoBitrateKbps);

        QualityModePairedPrediction one = PredictBoth([Record("a", 1000)]);
        Assert.Equal(QualityModePredictionReason.InsufficientIndependentSources, one.Direct.Reason);
        Assert.Null(one.Ratio.PredictedVideoBitrateKbps);
        Assert.Null(one.Direct.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void DuplicateFamilyCollapsesToLatestBeforeMinimumPeerCheck()
    {
        EncodingStatisticsRecord first = Record("repeat", 1000) with { EndUtc = DateTime.UnixEpoch };
        EncodingStatisticsRecord latest = Record("repeat", 9000) with { EndUtc = DateTime.UnixEpoch.AddDays(1) };
        EncodingStatisticsRecord independent = Record("independent", 7000);
        QualityModePairedPrediction pair = PredictBoth([first, latest, independent]);
        Assert.Equal(2, pair.Direct.IndependentSourceCount);
        Assert.Equal(8000, pair.Direct.PredictedVideoBitrateKbps);

        QualityModePairedPrediction onlyDuplicateFamily = PredictBoth([first, latest]);
        Assert.Equal(1, onlyDuplicateFamily.Direct.IndependentSourceCount);
        Assert.Equal(QualityModePredictionReason.InsufficientIndependentSources, onlyDuplicateFamily.Direct.Reason);
        Assert.Null(onlyDuplicateFamily.Direct.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void RatioAndDirectShareAllAdmissionGatesAndExcludeTargetFamily()
    {
        EncodingStatisticsRecord targetFamily = Record("heldout", 100_000);
        EncodingStatisticsRecord[] history =
        [
            Record("a", 3000), Record("b", 6000), targetFamily,
            Record("old-settings", 10000) with { QualityModeSettingsSignature = "old" },
            Record("other-cq", 10000, cq: 20),
            Record("far-bpp", 10000, sourceKbps: 7000),
            Record("far-pixels", 10000, width: 4000),
            Record("failed", 10000) with { Outcome = EncodingStatisticsOutcome.Failed },
            Record("recovered", 10000) with { RecoveredSuccessful = true },
            Record("no-output-rate", 10000) with { SourceAdaptiveShadow = Record("tmp", 1).SourceAdaptiveShadow! with { ActualOutputVideoBitrateKbps = null } }
        ];
        QualityModePairedPrediction pair = PredictBoth(history);
        Assert.Equal(new[] { "a", "b" }, pair.AdmittedPeerSourceFamilyKeys);
        Assert.Equal(pair.Ratio.IndependentSourceCount, pair.Direct.IndependentSourceCount);
        Assert.Equal(pair.Ratio.Reason, pair.Direct.Reason);
        Assert.Equal(4500, pair.Direct.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void StrictFiveFamilyHoldoutReproducesOfflineRatioDirectAndExploratoryH50Results()
    {
        EncodingStatisticsRecord[] records =
        [
            ExperimentRecord("A", 10000.097, 9435.927),
            ExperimentRecord("B", 9998.658, 9494.684),
            ExperimentRecord("C", 14753.081, 9478.285),
            ExperimentRecord("D", 14724.512, 9487.395),
            ExperimentRecord("E", 8159.936, 9304.079)
        ];
        QualityModeHoldoutEvaluation evaluation =
            NvencQualityModeVideoBitratePredictionService.EvaluateSourceHoldoutComparison(records);
        Assert.Equal(5, evaluation.EligibleTargetCount);
        Assert.Equal(5, evaluation.CoveredTargetCount);
        Assert.Equal(100, evaluation.CoveragePercent);
        Assert.All(evaluation.Targets, target =>
        {
            Assert.Equal(4, target.IndependentPeerCount);
            Assert.DoesNotContain(target.SourceFamilyKey, target.AdmittedPeerSourceFamilyKeys,
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(4, target.AdmittedPeerSourceFamilyKeys.Count);
            Assert.NotNull(target.RatioError);
            Assert.NotNull(target.DirectError);
            Assert.NotNull(target.ExploratoryH50Error);
        });
        double ratioMae = evaluation.Targets.Average(target => target.RatioError!.AbsoluteErrorPercent);
        double directMae = evaluation.Targets.Average(target => target.DirectError!.AbsoluteErrorPercent);
        double directWorst = evaluation.Targets.Max(target => target.DirectError!.AbsoluteErrorPercent);
        double h50Mae = evaluation.Targets.Average(target => target.ExploratoryH50Error!.AbsoluteErrorPercent);
        Assert.InRange(ratioMae, 30.5, 32.2);
        Assert.InRange(directMae, 0.60, 0.75);
        Assert.InRange(directWorst, 1.80, 2.00);
        Assert.InRange(h50Mae, 14.5, 16.3);
    }

    [Fact]
    public void SingleRecordHoldoutHasZeroCoverageForBothComparators()
    {
        QualityModeHoldoutEvaluation evaluation =
            NvencQualityModeVideoBitratePredictionService.EvaluateSourceHoldoutComparison(
                [ExperimentRecord("only", 8159.936, 9304.079)]);
        QualityModeHeldOutComparison result = Assert.Single(evaluation.Targets);
        Assert.Equal(1, evaluation.EligibleTargetCount);
        Assert.Equal(0, evaluation.CoveredTargetCount);
        Assert.Equal(0, evaluation.CoveragePercent);
        Assert.Equal(QualityModePredictionReason.NoComparableHistory, result.Reason);
        Assert.Null(result.RatioError);
        Assert.Null(result.DirectError);
        Assert.Null(result.ExploratoryH50Error);
    }

    [Fact]
    public void PathAliasesOfOneMeasuredSourceCountOnce()
    {
        EncodingStatisticsRecord a = Record("original.mkv", 3000);
        a.MediaDurationSeconds = 600;
        a.SourceAdaptiveShadow = a.SourceAdaptiveShadow! with
        {
            Decision = a.SourceAdaptiveShadow.Decision with { SourceTotalBytes = 123_456_789 }
        };
        EncodingStatisticsRecord alias = a with { SourcePath = "temporary-hardlink.mkv", Id = "alias" };
        NvencQualityModePredictionResult result = Predict([a, alias]);
        Assert.Equal(1, result.IndependentSourceCount);
        Assert.Null(result.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void PredictionDoesNotMutateExecutionQualityOrJournalEvidence()
    {
        EncodingStatisticsRecord[] history = [Record("a", 3000), Record("b", 6000)];
        int? beforeCq = history[0].SourceAdaptiveShadow!.Decision.FinalExecutionCq;
        long? beforeTotal = history[0].OutputSizeBytes;
        _ = Predict(history);
        Assert.Equal(beforeCq, history[0].SourceAdaptiveShadow!.Decision.FinalExecutionCq);
        Assert.Equal(beforeTotal, history[0].OutputSizeBytes);
    }

    [Fact]
    public void IncompatibleFailedCancelledSampleRecoveredAndTransformedRecordsAreExcluded()
    {
        EncodingStatisticsRecord[] records =
        [
            Record("a", 3000), Record("b", 6000),
            Record("other-settings", 10000) with { QualityModeSettingsSignature = "old" },
            Record("other-cq", 10000, cq: 20),
            Record("failed", 10000) with { Outcome = EncodingStatisticsOutcome.Failed },
            Record("cancelled", 10000) with { Outcome = EncodingStatisticsOutcome.Cancelled },
            Record("sample", 10000) with { IsSampleJob = true },
            Record("recovered", 10000) with { RecoveredSuccessful = true },
            Record("transformed", 10000, transformed: true),
            Record("other-geometry", 10000, width: 320)
        ];
        Assert.Equal(4500, Predict(records).PredictedVideoBitrateKbps);
        Assert.Equal(QualityModePredictionReason.Ineligible,
            Predict(records, transformed: true).Reason);
    }

    [Fact]
    public void HoldoutExcludesSameSourceAndReportsUnderpredictionMetrics()
    {
        EncodingStatisticsRecord[] records = [Record("a", 3000), Record("b", 4500), Record("c", 6000)];
        IReadOnlyList<QualityModeHeldOutError> errors =
            NvencQualityModeVideoBitratePredictionService.EvaluateSourceHoldout(records);
        Assert.Equal(3, errors.Count);
        Assert.Equal(3, errors.Select(error => error.SourceFamilyKey).Distinct().Count());
        QualityModeHeldOutMetrics metrics = NvencQualityModeVideoBitratePredictionService.Summarize(errors);
        Assert.Equal(3, metrics.SourceCount);
        Assert.True(metrics.UnderpredictionCount > 0);
        Assert.Null(metrics.P90AbsoluteErrorPercent);
    }

    [Fact]
    public void LegacyJournalLoadsButCannotEnterVersionedCohort()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MediaFlux-quality-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path,
                "{\"SchemaVersion\":1,\"Id\":\"legacy\",\"Outcome\":0,\"SourcePath\":\"old.mkv\"}" + Environment.NewLine);
            EncodingStatisticsRecord legacy = Assert.Single(new EncodingStatisticsService(path).GetAll());
            Assert.Equal("", legacy.QualityModeSettingsSignature);
            Assert.Equal(QualityModePredictionReason.NoComparableHistory,
                Predict([legacy]).Reason);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static NvencQualityModePredictionResult Predict(
        IEnumerable<EncodingStatisticsRecord> history, bool transformed = false) =>
        NvencQualityModeVideoBitratePredictionService.Predict(
            new("heldout", "h264", "hevc_nvenc", Signature, 19, 3000, 1920, 1080, 30, transformed),
            history);

    private static QualityModePairedPrediction PredictBoth(IEnumerable<EncodingStatisticsRecord> history) =>
        NvencQualityModeVideoBitratePredictionService.PredictBoth(
            new("heldout", "h264", "hevc_nvenc", Signature, 19, 3000, 1920, 1080, 30, false), history);

    private static EncodingStatisticsRecord ExperimentRecord(string family, double sourceKbps, double actualKbps)
    {
        EncodingStatisticsRecord record = Record(family, actualKbps, cq: 22, sourceKbps: sourceKbps, fps: 29.97002997002997);
        record.EndUtc = DateTime.UnixEpoch.AddDays(family[0]);
        record.SourceAdaptiveShadow = record.SourceAdaptiveShadow! with
        {
            Decision = record.SourceAdaptiveShadow.Decision with
            {
                SourceTotalBytes = 100_000_000 + family[0],
                Preset = "p5"
            }
        };
        return record;
    }

    private static EncodingStatisticsRecord Record(string source, double actualKbps,
        int cq = 19, bool transformed = false, int width = 1920,
        double sourceKbps = 3000, double fps = 30) => new()
    {
        Id = source,
        SourcePath = source,
        EndUtc = DateTime.UtcNow,
        Outcome = EncodingStatisticsOutcome.Success,
        QualityModeSettingsSignature = Signature,
        OutputSizeBytes = 999_999_999, // Must never be used as the video-bitrate target.
        SourceAdaptiveShadow = new SourceAdaptiveShadowOutcome
        {
            Decision = new SourceAdaptiveShadowCalibration
            {
                Status = SourceAdaptiveShadowStatus.CalibrationCandidate,
                SourceCodec = "h264",
                OutputCodec = "hevc_nvenc",
                EncoderId = VideoEncoderIds.Nvenc,
                FinalExecutionCq = cq,
                SourceVideoBitrateKbps = sourceKbps,
                PlannedWidth = width,
                PlannedHeight = 1080,
                PlannedFps = fps,
                MaterialTransformationActive = transformed
            },
            ActualOutputCodec = "hevc",
            ActualOutputVideoBitrateKbps = actualKbps
        }
    };
}
