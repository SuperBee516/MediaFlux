using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingRuntimeEstimatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-RuntimeEstimatorTests", Guid.NewGuid().ToString("N"));

    public EncodingRuntimeEstimatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExactStableCohortUsesMedianAndReturnsHighConfidenceRange()
    {
        EncodingStatisticsRecord[] history = Enumerable.Range(0, 7).Select(index => Record(index, speed: 2 + (index - 3) * .03)).ToArray();
        EncodingRuntimeEstimate estimate = Estimator(history).Estimate(Workload(duration: 3600));
        Assert.Equal(1800, estimate.EstimatedProcessingSeconds!.Value, precision: 6);
        Assert.Equal(2, estimate.EstimatedSpeedX!.Value, precision: 6);
        Assert.Equal(RuntimeEstimateConfidence.High, estimate.Confidence);
        Assert.Equal(7, estimate.SampleCount);
        Assert.True(estimate.FastProcessingSeconds < estimate.EstimatedProcessingSeconds);
        Assert.True(estimate.SlowProcessingSeconds > estimate.EstimatedProcessingSeconds);
    }

    [Fact]
    public void CohortsRelaxConcurrencyThenPresetWithReducedConfidence()
    {
        EncodingStatisticsRecord[] concurrency = Enumerable.Range(0, 4).Select(index => Record(index, 2, concurrent: true)).ToArray();
        EncodingRuntimeEstimate relaxedConcurrency = Estimator(concurrency).Estimate(Workload(concurrent: false));
        Assert.Equal(1, relaxedConcurrency.FallbackDepth);
        Assert.Equal(RuntimeEstimateConfidence.Medium, relaxedConcurrency.Confidence);

        EncodingStatisticsRecord[] preset = Enumerable.Range(0, 4).Select(index => Record(index, 2, preset: "p7")).ToArray();
        EncodingRuntimeEstimate relaxedPreset = Estimator(preset).Estimate(Workload(preset: "p5"));
        Assert.Equal(4, relaxedPreset.FallbackDepth);
        Assert.Equal(RuntimeEstimateConfidence.Low, relaxedPreset.Confidence);
    }

    [Fact]
    public void ResolutionBitDepthEncoderAndPresetSelectTheMatchingWorkload()
    {
        var history = new List<EncodingStatisticsRecord>();
        history.AddRange(Enumerable.Range(0, 5).Select(index => Record(index, 1, encoder: "nvenc", preset: "p5", outputTier: "1080p", bitDepth: 10)));
        history.AddRange(Enumerable.Range(10, 5).Select(index => Record(index, 4, encoder: "qsv", preset: "slow", outputTier: "720p", bitDepth: 8)));
        EncodingRuntimeEstimate estimate = Estimator(history).Estimate(Workload(encoder: "qsv", preset: "slow", outputTier: "720p", bitDepth: 8));
        Assert.Equal(4, estimate.EstimatedSpeedX);
        Assert.Equal(0, estimate.FallbackDepth);
    }

    [Fact]
    public void InvalidFailedCancelledSampleTinyAndUnmatchedHistoryProducesUnknown()
    {
        EncodingStatisticsRecord[] history =
        {
            Record(1, 2) with { Outcome = EncodingStatisticsOutcome.Failed },
            Record(2, 2) with { Outcome = EncodingStatisticsOutcome.Cancelled },
            Record(3, 2) with { IsSampleJob = true },
            Record(4, 2) with { MediaDurationSeconds = 20 },
            Record(5, 2) with { Codec = "av1_nvenc" }
        };
        EncodingRuntimeEstimate estimate = Estimator(history).Estimate(Workload());
        Assert.False(estimate.IsAvailable);
        Assert.Equal(RuntimeEstimateConfidence.Unknown, estimate.Confidence);
    }

    [Fact]
    public void LegacyRecordsRemainUsableOnlyAsLowConfidenceFallback()
    {
        EncodingStatisticsRecord[] history = Enumerable.Range(0, 4).Select(index => Record(index, 2) with
        {
            SchemaVersion = 1, EncoderId = "", EncoderPreset = "", SourceResolutionTier = "",
            OutputResolutionTier = "", OutputBitDepth = null, ScalingApplied = null,
            ConcurrentEncoderSessions = null, Encoder = "NVIDIA NVENC"
        }).ToArray();
        EncodingRuntimeEstimate estimate = Estimator(history).Estimate(Workload());
        Assert.True(estimate.IsAvailable);
        Assert.Equal(5, estimate.FallbackDepth);
        Assert.Equal(RuntimeEstimateConfidence.Low, estimate.Confidence);
    }

    [Fact]
    public void MedianMadFilteringResistsAnExtremeSpeedOutlier()
    {
        EncodingStatisticsRecord[] history = Enumerable.Range(0, 6).Select(index => Record(index, index == 5 ? 90 : 2)).ToArray();
        EncodingRuntimeEstimate estimate = Estimator(history).Estimate(Workload(duration: 3600));
        Assert.Equal(2, estimate.EstimatedSpeedX);
        Assert.Equal(5, estimate.SampleCount);
        Assert.Equal(1800, estimate.EstimatedProcessingSeconds);
    }

    [Fact]
    public void SuccessfulFinalizedObservationImmediatelyImprovesFutureEstimate()
    {
        string path = Path.Combine(_root, "statistics.jsonl");
        var statistics = new EncodingStatisticsService(path);
        statistics.AppendFinalized(Record(0, 1));
        var estimator = new EncodingRuntimeEstimatorService(statistics);
        Assert.Equal(1, estimator.Estimate(Workload()).EstimatedSpeedX);
        foreach (int index in Enumerable.Range(1, 5)) statistics.AppendFinalized(Record(index, 2));
        EncodingRuntimeEstimate improved = estimator.Estimate(Workload());
        Assert.Equal(2, improved.EstimatedSpeedX);
        Assert.Equal(RuntimeEstimateConfidence.High, improved.Confidence);
        EncodingRuntimeEstimate afterRestart = new EncodingRuntimeEstimatorService(new EncodingStatisticsService(path)).Estimate(Workload());
        Assert.Equal(improved.EstimatedSpeedX, afterRestart.EstimatedSpeedX);
        Assert.Equal(improved.Confidence, afterRestart.Confidence);
    }

    [Fact]
    public void SequentialBacktestUsesOnlyPriorObservations()
    {
        EncodingStatisticsRecord[] records = Enumerable.Range(0, 10).Select(index => Record(index, 2)).ToArray();
        EncodingRuntimeBacktestResult result = Estimator(records).Backtest(records);
        Assert.Equal(9, result.PredictionCount);
        Assert.Equal(0, result.MedianAbsolutePercentageError);
        Assert.Equal(100, result.PercentWithin10);
        Assert.Equal(100, result.PercentWithin25);
    }

    private static EncodingRuntimeEstimatorService Estimator(IEnumerable<EncodingStatisticsRecord> records)
    {
        EncodingStatisticsRecord[] snapshot = records.ToArray();
        return new EncodingRuntimeEstimatorService(() => snapshot);
    }

    private static EncodingRuntimeWorkload Workload(double duration = 1800, string encoder = "nvenc", string preset = "p5",
        string outputTier = "1080p", int bitDepth = 10, bool concurrent = false) =>
        new(duration, encoder, "hevc", preset, "1080p", outputTier, bitDepth, false, concurrent);

    private static EncodingStatisticsRecord Record(int index, double speed, string encoder = "nvenc", string preset = "p5",
        string outputTier = "1080p", int bitDepth = 10, bool concurrent = false)
    {
        double mediaSeconds = 1800;
        double processing = mediaSeconds / speed;
        DateTime end = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(index);
        return new EncodingStatisticsRecord
        {
            Id = $"record-{index}-{encoder}-{preset}-{outputTier}-{bitDepth}-{concurrent}", StartUtc = end.AddSeconds(-processing), EndUtc = end,
            Outcome = EncodingStatisticsOutcome.Success, SourcePath = $@"D:\Media\{index}.mkv", OutputPath = $@"D:\Output\{index}.mkv",
            Codec = "hevc_nvenc", Encoder = encoder, EncoderId = encoder, EncoderPreset = preset,
            SourceResolutionTier = "1080p", OutputResolutionTier = outputTier, OutputBitDepth = bitDepth,
            ScalingApplied = false, ConcurrentEncoderSessions = concurrent, MediaDurationSeconds = mediaSeconds,
            ProcessingSeconds = processing, SourceSizeBytes = 20L * 1024 * 1024 * 1024, OutputSizeBytes = 10L * 1024 * 1024 * 1024
        };
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
