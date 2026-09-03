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
                new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", DurationSeconds = 1419.96, FrameCount = 34045 },
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
}
