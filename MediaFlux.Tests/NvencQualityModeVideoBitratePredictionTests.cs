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
        NvencQualityModePredictionResult result = Predict([Record("a", 3000), Record("b", 6000)]);
        Assert.Equal(QualityModePredictionConfidence.Low, result.Confidence);
        Assert.Equal(2, result.IndependentSourceCount);
        Assert.Equal(4500, result.PredictedVideoBitrateKbps);
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

    private static EncodingStatisticsRecord Record(string source, double actualKbps,
        int cq = 19, bool transformed = false, int width = 1920) => new()
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
                SourceVideoBitrateKbps = 3000,
                PlannedWidth = width,
                PlannedHeight = 1080,
                PlannedFps = 30,
                MaterialTransformationActive = transformed
            },
            ActualOutputCodec = "hevc",
            ActualOutputVideoBitrateKbps = actualKbps
        }
    };
}
