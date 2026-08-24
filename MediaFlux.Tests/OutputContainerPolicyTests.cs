using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class OutputContainerPolicyTests
{
    [Fact]
    public void Auto_UsesMp4_ForSimpleCompatibleStreams()
    {
        OutputContainerDecision decision = Decide(
            OutputContainerSelection.Auto,
            Stream("video", "h264"),
            Stream("audio", "aac"));

        Assert.Equal(OutputContainer.Mp4, decision.Resolved);
        Assert.Empty(decision.CompatibilityWarnings);
        Assert.Equal(".mp4", decision.Extension);
    }

    [Theory]
    [InlineData("subtitle", "hdmv_pgs_subtitle")]
    [InlineData("subtitle", "ass")]
    [InlineData("attachment", "ttf")]
    [InlineData("audio", "dts")]
    public void Auto_UsesMatroska_WhenMp4WouldLoseOrRiskSelectedStream(
        string type,
        string codec)
    {
        OutputContainerDecision decision = Decide(
            OutputContainerSelection.Auto,
            Stream("video", "h264"),
            Stream("audio", "aac"),
            Stream(type, codec));

        Assert.Equal(OutputContainer.Matroska, decision.Resolved);
        Assert.NotEmpty(decision.CompatibilityWarnings);
        Assert.Equal(".mkv", decision.Extension);
    }

    [Fact]
    public void ExplicitMp4_RemainsMp4_AndRequiresReview()
    {
        OutputContainerDecision decision = Decide(
            OutputContainerSelection.Mp4,
            Stream("video", "hevc"),
            Stream("subtitle", "ass"),
            Stream("attachment", "ttf"));

        Assert.Equal(OutputContainer.Mp4, decision.Resolved);
        Assert.True(decision.RequiresConfirmation);
        Assert.False(decision.CopySubtitles);
        Assert.False(decision.CopyAttachments);
    }

    [Fact]
    public void ExplicitMatroska_OmitsUnsupportedDataStreams()
    {
        OutputContainerDecision decision = Decide(
            OutputContainerSelection.Matroska,
            Stream("video", "av1"),
            Stream("subtitle", "ass"),
            Stream("attachment", "ttf"),
            Stream("data", "bin_data"));

        Assert.Equal(OutputContainer.Matroska, decision.Resolved);
        Assert.True(decision.CopySubtitles);
        Assert.True(decision.CopyAttachments);
        Assert.False(decision.CopyDataStreams);
    }

    [Fact]
    public void Auto_DoesNotSelectMatroskaSolelyForUnsupportedDataStreams()
    {
        OutputContainerDecision decision = Decide(
            OutputContainerSelection.Auto,
            Stream("video", "h264"),
            Stream("audio", "aac"),
            Stream("data", "rtp"));

        Assert.Equal(OutputContainer.Mp4, decision.Resolved);
        Assert.False(decision.CopyDataStreams);
        Assert.Empty(decision.CompatibilityWarnings);
    }

    [Fact]
    public void MissingOrUnknownPersistedValue_FallsBackToLegacyMp4()
    {
        Assert.Equal(OutputContainerSelection.Mp4, OutputContainerPolicy.ParseSelection(null));
        Assert.Equal(OutputContainerSelection.Mp4, OutputContainerPolicy.ParseSelection("future-value"));
    }

    [Fact]
    public void Auto_IgnoresUnselectedDvdSubtitles()
    {
        var probe = new MediaProbeResult
        {
            Success = true,
            Streams = new[]
            {
                new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "mpeg2video" },
                new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "ac3" },
                new MediaProbeStreamInfo { Index = 2, CodecType = "subtitle", CodecName = "dvd_subtitle" }
            }
        };
        var input = new EncodingInputSource
        {
            Kind = EncodingInputKind.DvdPhysicalConcat,
            InputPath = "concat.txt",
            SourcePath = "VIDEO_TS",
            VideoStreamIndexes = new[] { 0 },
            AudioStreamIndexes = new[] { 1 },
            SubtitleStreamIndexes = Array.Empty<int>()
        };

        OutputContainerDecision decision = OutputContainerPolicy.Decide(
            OutputContainerSelection.Auto,
            probe,
            input,
            EncodingService.StreamMapMode.KeepAll);

        Assert.Equal(OutputContainer.Mp4, decision.Resolved);
    }

    private static OutputContainerDecision Decide(
        OutputContainerSelection selection,
        params MediaProbeStreamInfo[] streams)
    {
        var probe = new MediaProbeResult { Success = true, Streams = streams };
        return OutputContainerPolicy.Decide(
            selection,
            probe,
            EncodingInputSource.FromFile("source.mkv"),
            EncodingService.StreamMapMode.KeepAll);
    }

    private static MediaProbeStreamInfo Stream(string type, string codec) => new()
    {
        Index = Random.Shared.Next(1, int.MaxValue),
        CodecType = type,
        CodecName = codec
    };
}
