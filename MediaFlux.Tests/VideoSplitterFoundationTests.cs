using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class VideoSplitterFoundationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-VideoSplitterTests", Guid.NewGuid().ToString("N"));

    public VideoSplitterFoundationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("1:02.500", 62.5)]
    [InlineData("1:02:03.250", 3723.25)]
    [InlineData("15.75", 15.75)]
    public void TimestampParserAcceptsEditableTimelineValues(string text, double expected)
    {
        Assert.True(VideoSplitterForm.TryParseTime(text, out double seconds));
        Assert.Equal(expected, seconds, 3);
    }

    [Theory]
    [InlineData(0, "0:00.000")]
    [InlineData(62.5, "1:02.500")]
    [InlineData(3723.25, "1:02:03.250")]
    public void TimestampFormatterIsPreciseAndStable(double seconds, string expected) =>
        Assert.Equal(expected, VideoSplitterForm.FormatTime(seconds));

    [Fact]
    public void WindowPlacementRoundTripsWithoutPersistingTrimState()
    {
        string path = Path.Combine(_root, "config.json");
        new Config
        {
            VideoSplitterWindowX = 220,
            VideoSplitterWindowY = 160,
            VideoSplitterWindowWidth = 1080,
            VideoSplitterWindowHeight = 740
        }.Save(path);

        Config loaded = Config.Load(path);
        Assert.Equal(220, loaded.VideoSplitterWindowX);
        Assert.Equal(160, loaded.VideoSplitterWindowY);
        Assert.Equal(1080, loaded.VideoSplitterWindowWidth);
        Assert.Equal(740, loaded.VideoSplitterWindowHeight);
    }

    [Fact]
    public void SegmentRulesRejectInvalidRangesAndProduceStableOutputNames()
    {
        Assert.False(VideoSplitterSegmentRules.TryValidate(4, 4, 10, out string samePoint));
        Assert.Contains("earlier", samePoint, StringComparison.OrdinalIgnoreCase);
        Assert.False(VideoSplitterSegmentRules.TryValidate(-1, 4, 10, out string outside));
        Assert.Contains("within", outside, StringComparison.OrdinalIgnoreCase);
        Assert.True(VideoSplitterSegmentRules.TryValidate(1.25, 9.75, 10, out _));
        Assert.Equal("movie-Part03.mkv", VideoSplitterSegmentRules.CreateOutputFileName("C:\\clips\\movie.mkv", 3));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
