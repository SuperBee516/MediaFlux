using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ProgramDurationResolverTests
{
    [Fact]
    public void SubtitleInflatedContainer_UsesPrimaryVideoDuration()
    {
        ProgramDurationDecision decision = ProgramDurationResolver.Resolve(new MediaProbeResult
        {
            Success = true, DurationSeconds = 1475.25,
            Streams = new[]
            {
                new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", FrameRate = 34045d / 1419.96d, FrameCount = 34045 },
                new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac", DurationSeconds = 1420 },
                new MediaProbeStreamInfo { Index = 2, CodecType = "subtitle", CodecName = "ass", DurationSeconds = 1475.25 }
            }
        });

        Assert.True(decision.UsedVideoFallback);
        Assert.Equal(1419.96, decision.DurationSeconds);
        Assert.Equal(34045, decision.PrimaryVideo!.FrameCount);
    }

    [Fact]
    public void NormalProgramDuration_UsesPrimaryVideoWithoutFallback()
    {
        ProgramDurationDecision decision = ProgramDurationResolver.Resolve(new MediaProbeResult
        {
            Success = true, DurationSeconds = 1420,
            Streams = new[] { new MediaProbeStreamInfo { CodecType = "video", DurationSeconds = 1419.96 } }
        });

        Assert.False(decision.UsedVideoFallback);
        Assert.Equal(1419.96, decision.DurationSeconds);
    }

    [Fact]
    public void SubtitleOutlier_DrivesSizeEstimateFromAuthoritativeTimeline()
    {
        MediaProbeResult probe = new()
        {
            Success = true, DurationSeconds = 1475.25,
            Streams = new[]
            {
                new MediaProbeStreamInfo { CodecType = "video", FrameCount = 34045, FrameRate = 34045d / 1419.96d },
                new MediaProbeStreamInfo { CodecType = "audio", DurationSeconds = 1420 },
                new MediaProbeStreamInfo { CodecType = "subtitle", DurationSeconds = 1475.25 }
            }
        };
        double authoritative = ProgramDurationResolver.Resolve(probe).DurationSeconds!.Value;
        SizeEstimateBreakdown program = SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
            1_000, authoritative, 1920, 1080, 23.976, 5_000, "hevc", "Medium Quality (Default)", "libx265", 23, null);
        SizeEstimateBreakdown inflated = SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
            1_000, probe.DurationSeconds!.Value, 1920, 1080, 23.976, 5_000, "hevc", "Medium Quality (Default)", "libx265", 23, null);

        Assert.True(program.EstimatedOutputMb < inflated.EstimatedOutputMb);
        Assert.Equal(1419.96, authoritative);
    }
}
