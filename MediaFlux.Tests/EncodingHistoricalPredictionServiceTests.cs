using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingHistoricalPredictionServiceTests
{
    [Fact]
    public void StrongCleanHardwareCohortUsesRobustMedianAndLeavesPlanValuesAdvisory()
    {
        EncodingStatisticsRecord[] history = Enumerable.Range(0, 11).Select(index => Record(index, index == 10 ? 20 : 2)).ToArray();
        EncodingDecisionContext context = Context();
        EncodingHistoricalPrediction prediction = EncodingHistoricalPredictionService.Predict(context, history);

        Assert.Equal(EncodingHistoricalConfidence.High, prediction.Confidence);
        Assert.Equal(1, prediction.MatchTier);
        Assert.Equal(2, prediction.PredictedSpeedX);
        Assert.Equal(100, context.TargetMb);
    }

    [Fact]
    public void FailedRecoveredAndDifferentHardwareObservationsDoNotCreateStrongPrediction()
    {
        EncodingStatisticsRecord[] history =
        [
            Record(1, 2) with { Outcome = EncodingStatisticsOutcome.Failed },
            Record(2, 2) with { RecoveredSuccessful = true },
            Record(3, 2) with { HardwareKey = "other gpu" }
        ];
        EncodingHistoricalPrediction prediction = EncodingHistoricalPredictionService.Predict(Context(), history);

        Assert.Equal(EncodingHistoricalConfidence.None, prediction.Confidence);
    }

    private static EncodingDecisionContext Context() => new(
        new MediaProbeResult { Success = true, Streams = new[] { new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080 } } },
        EncodingInputSource.FromFile("missing.mkv"), new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"), true, 100,
        EncodingService.ScaleMode.None, new VideoRestorationSettings(), "p5", 24, false, null,
        EncodingService.StreamMapMode.KeepAll, true, true, true, OutputContainerSelection.Mp4,
        ContainerCompatibilityPolicy.Intelligent, TimeSpan.FromMinutes(10));

    private static EncodingStatisticsRecord Record(int index, double speed) => new()
    {
        Id = $"r{index}", Outcome = EncodingStatisticsOutcome.Success, Codec = "hevc_nvenc", EncoderId = VideoEncoderIds.Nvenc,
        HardwareKey = HardwarePerformanceService.DetectGpuIdentity(), OutputResolutionTier = "1080p",
        MediaDurationSeconds = 600, ProcessingSeconds = 600 / speed,
        SourceSizeBytes = 1000_000_000, OutputSizeBytes = 500_000_000
    };
}
