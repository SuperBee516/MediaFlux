using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingPredictionAccuracyServiceTests
{
    [Fact]
    public void RowUsesActualMinusPredictedPercentages()
    {
        EncodingPredictionAccuracyRow row = new EncodingPredictionAccuracyService().CreateRows(new[] { Record("normal", 1_000, 800, 100, 80) }).Single();
        Assert.Equal(200, row.SizeSignedErrorBytes);
        Assert.Equal(25, row.SizePercentError);
        Assert.Equal(25, row.SizeAbsolutePercentageError);
        Assert.Equal(25, row.EtaPercentError);
    }

    [Fact]
    public void RobustStatisticsUseInterpolatedOddAndEvenPercentiles()
    {
        EncodingPredictionRobustStatistics odd = EncodingPredictionAccuracyService.Describe(new double?[] { 1, 2, 3, 4, 5 });
        Assert.Equal(3, odd.MedianSignedPercent);
        Assert.Equal(2, odd.FirstQuartileSignedPercent);
        Assert.Equal(4, odd.ThirdQuartileSignedPercent);
        Assert.Equal(2, odd.SignedPercentIqr);

        EncodingPredictionRobustStatistics even = EncodingPredictionAccuracyService.Describe(new double?[] { 1, 2, 3, 4 });
        Assert.Equal(2.5, even.MedianSignedPercent);
        Assert.Equal(1.75, even.FirstQuartileSignedPercent);
        Assert.Equal(3.25, even.ThirdQuartileSignedPercent);
        Assert.Equal(1.5, even.SignedPercentIqr);
    }

    [Fact]
    public void OutlierDoesNotDominateMedianBiasOrAbsoluteError()
    {
        var service = new EncodingPredictionAccuracyService();
        EncodingPredictionAccuracyRow[] rows = service.CreateRows(new[]
        {
            Record("a", 98, 100, 100, 100), Record("b", 101, 100, 100, 100),
            Record("c", 103, 100, 100, 100), Record("d", 105, 100, 100, 100),
            Record("outlier", 300, 100, 100, 100)
        }).ToArray();
        EncodingPredictionAccuracyMetrics summary = service.Summarize(rows);
        Assert.Equal(3, summary.MedianSizeSignedPercentageError);
        Assert.Equal(3, summary.MedianSizeAbsolutePercentageError);
        Assert.Equal(4, summary.SizeSignedPercentageIqr);
    }

    [Fact]
    public void ConfidencePolicyClassifiesEachTier()
    {
        Assert.Equal(EncodingPredictionConfidence.Insufficient, EncodingPredictionAccuracyService.ClassifyConfidence(Stats(4, 1, 1)));
        Assert.Equal(EncodingPredictionConfidence.Low, EncodingPredictionAccuracyService.ClassifyConfidence(Stats(5, 26, 10)));
        Assert.Equal(EncodingPredictionConfidence.Moderate, EncodingPredictionAccuracyService.ClassifyConfidence(Stats(10, 20, 20)));
        Assert.Equal(EncodingPredictionConfidence.High, EncodingPredictionAccuracyService.ClassifyConfidence(Stats(20, 8, 10)));
    }

    [Fact]
    public void BiasPolicyUsesDeadbandAndDirection()
    {
        Assert.Equal(EncodingPredictionBiasState.InsufficientData, EncodingPredictionAccuracyService.ClassifyBias(Stats(4, 1, 1)));
        Assert.Equal(EncodingPredictionBiasState.NearTarget, EncodingPredictionAccuracyService.ClassifyBias(Stats(5, 1, 1, 3)));
        Assert.Equal(EncodingPredictionBiasState.Underestimating, EncodingPredictionAccuracyService.ClassifyBias(Stats(5, 1, 1, 3.1)));
        Assert.Equal(EncodingPredictionBiasState.Overestimating, EncodingPredictionAccuracyService.ClassifyBias(Stats(5, 1, 1, -3.1)));
    }

    [Fact]
    public void SizeAndEtaSamplesRemainSeparateWhenEtaIsMissing()
    {
        var service = new EncodingPredictionAccuracyService();
        EncodingPredictionAccuracyRow[] rows = service.CreateRows(new[]
        {
            Record("a", 100, 100, 100, 90), Record("b", 100, 100, 100, 90),
            Record("c", 100, 100, 100, 90), Record("d", 100, 100, 100, 90),
            Record("no-eta", 100, 100, 100, null)
        }).ToArray();
        EncodingPredictionAccuracyMetrics summary = service.Summarize(rows);
        Assert.Equal(5, summary.SizePredictionCount);
        Assert.Equal(4, summary.EtaPredictionCount);
        Assert.Equal(EncodingPredictionConfidence.Low, summary.Confidence);
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

    [Fact]
    public void CleanAndRecoveredCohortsRemainSeparatedUnlessRequested()
    {
        var service = new EncodingPredictionAccuracyService();
        EncodingPredictionAccuracyRow[] rows = service.CreateRows(new[]
        {
            Record("clean", 100, 100, 100, 100), Record("recovered", 100, 100, 100, 100, recovered: true)
        }).ToArray();
        Assert.Equal(1, service.Summarize(rows).CompletedCount);
        Assert.Equal(2, service.Summarize(rows, includeRecovered: true).CompletedCount);
        Assert.Single(service.BuildCohorts(rows));
        Assert.Equal(2, service.BuildCohorts(rows, includeRecovered: true).Single().Count);
    }

    private static EncodingPredictionRobustStatistics Stats(int count, double iqr, double absolute, double median = 0) =>
        new(count, median, absolute, median - iqr / 2, median + iqr / 2, iqr);

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
