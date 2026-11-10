using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class TolerantAudioRecoveryFilterPolicyTests
{
    [Fact]
    public void KnownStereoCorruptStreamGetsStablePassThroughAtItsOutputOrdinal()
    {
        MediaProbeResult source = new()
        {
            Success = true,
            Streams = new[]
            {
                new MediaProbeStreamInfo
                {
                    Index = 3,
                    CodecType = "audio",
                    Channels = 2,
                    ChannelLayout = "stereo"
                }
            }
        };
        OutputContainerDecision decision = RecoveredDecision(
            new StreamCompatibilityPlan(1, "audio", "aac", StreamCompatibilityAction.Copy, "healthy"),
            new StreamCompatibilityPlan(3, "audio", "aac", StreamCompatibilityAction.Transcode, "damaged", "aac"));

        IReadOnlyDictionary<int, string> filters = TolerantAudioRecoveryFilterPolicy.BuildOverrides(
            source, decision, damagedAudioStreamIndex: 3, requestedAudioChannels: null);

        Assert.Equal(TolerantAudioRecoveryFilterPolicy.StereoPassThroughFilter, Assert.Single(filters).Value);
        Assert.Equal(1, Assert.Single(filters).Key);
    }

    [Theory]
    [InlineData("", 2, null)]
    [InlineData("5.1", 6, null)]
    [InlineData("stereo", 2, 6)]
    [InlineData("stereo", 2, 1)]
    public void UnknownLayoutsAndExplicitNonStereoOutputRemainUnchanged(
        string channelLayout,
        int channels,
        int? requestedChannels)
    {
        MediaProbeResult source = new()
        {
            Success = true,
            Streams = new[]
            {
                new MediaProbeStreamInfo
                {
                    Index = 3,
                    CodecType = "audio",
                    Channels = channels,
                    ChannelLayout = channelLayout
                }
            }
        };

        IReadOnlyDictionary<int, string> filters = TolerantAudioRecoveryFilterPolicy.BuildOverrides(
            source,
            RecoveredDecision(new StreamCompatibilityPlan(
                3, "audio", "aac", StreamCompatibilityAction.Transcode, "damaged", "aac")),
            damagedAudioStreamIndex: 3,
            requestedAudioChannels: requestedChannels);

        Assert.Empty(filters);
    }

    private static OutputContainerDecision RecoveredDecision(params StreamCompatibilityPlan[] plans) => new()
    {
        Requested = OutputContainerSelection.Mp4,
        Resolved = OutputContainer.Mp4,
        Reason = "test",
        StreamPlans = plans
    };
}
