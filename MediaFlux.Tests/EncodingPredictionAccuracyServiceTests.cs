using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingPredictionAccuracyServiceTests
{
    [Fact]
    public void RowUsesActualMinusPredictedAndSafePercentages()
    {
        EncodingPredictionAccuracyRow row = new EncodingPredictionAccuracyService().CreateRows(new[] { Record("normal", 1_000, 800, 100, 80) }).Single();
        Assert.Equal(200, row.SizeSignedErrorBytes);
        Assert.Equal(200, row.SizeAbsoluteErrorBytes);
        Assert.Equal(20, row.SizePercentError);
        Assert.Equal(20, row.SizeAbsolutePercentageError);
        Assert.Equal(20, row.EtaSignedErrorSeconds);
        Assert.Equal(20, row.EtaAbsolutePercentageError);
    }

    [Fact]
    public void SummaryExcludesFailedSampleAndRecoveredFromNormalMetrics()
    {
        var service = new EncodingPredictionAccuracyService();
        EncodingPredictionAccuracyRow[] rows = service.CreateRows(new[]
        {
            Record("clean", 1_000, 800, 100, 80),
            Record("recovered", 1_000, 800, 100, 80, recovered: true),
            Record("sample", 1_000, 800, 100, 80, sample: true),
            Record("failed", 1_000, null, 100, 80, outcome: EncodingStatisticsOutcome.Failed)
        }).ToArray();
        EncodingPredictionAccuracyMetrics summary = service.Summarize(rows);
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(1, summary.SizePredictionCount);
        Assert.Equal(20, summary.MedianSizeAbsolutePercentageError);
        Assert.Single(service.BuildCohorts(rows));
    }

    [Fact]
    public void MissingPredictionOrActualNeverCreatesAnError()
    {
        EncodingStatisticsRecord record = Record("missing", null, null, 0, null);
        EncodingPredictionAccuracyRow row = new EncodingPredictionAccuracyService().CreateRows(new[] { record }).Single();
        Assert.Null(row.SizeSignedErrorBytes);
        Assert.Null(row.SizeAbsolutePercentageError);
        Assert.Null(row.EtaAbsolutePercentageError);
    }

    private static EncodingStatisticsRecord Record(string id, long? actualBytes, long? predictedBytes,
        double processing, double? predictedProcessing, bool recovered = false, bool sample = false,
        EncodingStatisticsOutcome outcome = EncodingStatisticsOutcome.Success) => new()
        {
            Id = id, StartUtc = DateTime.UtcNow.AddSeconds(-processing), EndUtc = DateTime.UtcNow,
            Outcome = outcome, SourceSizeBytes = 2_000, OutputSizeBytes = actualBytes,
            ProcessingSeconds = processing, Codec = "hevc", Encoder = "NVENC", EncoderId = "nvenc",
            PredictionSourceCodec = "h264", PredictionTargetCodec = "hevc", OutputResolutionTier = "1080p",
            PredictedOutputSizeBytes = predictedBytes, PredictedProcessingSeconds = predictedProcessing,
            PredictionQuality = "22", PredictionRecommendation = "Encode", RecoveredSuccessful = recovered, IsSampleJob = sample
        };
}
