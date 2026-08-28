using MediaFlux.Models;
using System.Drawing;
using System.Windows.Forms;
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
            VideoSplitterWindowHeight = 740,
            VideoSplitterPreviewSplitterDistance = 215,
            VideoSplitterMediaEditorSplitterDistance = 390,
            VideoSplitterTimelineDetailsSplitterDistance = 225,
            VideoSplitterBoundarySegmentsSplitterDistance = 315,
            VideoSplitterSegmentsOutputSplitterDistance = 230
        }.Save(path);

        Config loaded = Config.Load(path);
        Assert.Equal(220, loaded.VideoSplitterWindowX);
        Assert.Equal(160, loaded.VideoSplitterWindowY);
        Assert.Equal(1080, loaded.VideoSplitterWindowWidth);
        Assert.Equal(740, loaded.VideoSplitterWindowHeight);
        Assert.Equal(215, loaded.VideoSplitterPreviewSplitterDistance);
        Assert.Equal(390, loaded.VideoSplitterMediaEditorSplitterDistance);
        Assert.Equal(225, loaded.VideoSplitterTimelineDetailsSplitterDistance);
        Assert.Equal(315, loaded.VideoSplitterBoundarySegmentsSplitterDistance);
        Assert.Equal(230, loaded.VideoSplitterSegmentsOutputSplitterDistance);
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

    [Fact]
    public void AutomaticSegmentNamesSkipCurrentNamesAndExistingOutputs()
    {
        File.WriteAllText(Path.Combine(_root, "movie-Part01.mp4"), "existing");

        string name = VideoSplitterSegmentRules.CreateUniqueOutputFileName(
            Path.Combine(_root, "movie.mp4"),
            1,
            _root,
            new[] { "movie-Part02.mp4" });

        Assert.Equal("movie-Part03.mp4", name);
    }

    [Fact]
    public void AutoRenameProducesUnusedDeterministicNames()
    {
        File.WriteAllText(Path.Combine(_root, "movie-Part01.mp4"), "existing");
        VideoSplitterSegment[] renamed = VideoSplitterForm.AutoRenameConflictingOutputs(new[]
        {
            new VideoSplitterSegment(1, 0, 1, "movie-Part01.mp4"),
            new VideoSplitterSegment(2, 1, 2, "movie-Part02.mp4")
        }, _root);

        Assert.Equal("movie-Part01 (2).mp4", renamed[0].OutputFileName);
        Assert.Equal("movie-Part02.mp4", renamed[1].OutputFileName);
        Assert.Equal(2, renamed.Select(segment => segment.OutputFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void MarkersCanBeReplacedInEitherOrderAndExposeInvalidRanges()
    {
        var timeline = new TimelineControl();
        timeline.SetDuration(20);
        timeline.OutSeconds = 4;
        timeline.InSeconds = 12;

        Assert.Equal(12, timeline.InSeconds);
        Assert.Equal(4, timeline.OutSeconds);
        Assert.False(VideoSplitterSegmentRules.TryValidate(timeline.InSeconds, timeline.OutSeconds, 20, out _));

        timeline.OutSeconds = 16;
        Assert.True(VideoSplitterSegmentRules.TryValidate(timeline.InSeconds, timeline.OutSeconds, 20, out _));
    }

    [Fact]
    public void SplitAtPointCreatesExactlyTwoContiguousEditableSegments()
    {
        Assert.True(VideoSplitterForm.TryCreateSplitSegments("C:\\clips\\movie.mkv", 12.5, 50, out VideoSplitterSegment[] segments, out _));
        Assert.Collection(segments,
            first => { Assert.Equal(0, first.StartSeconds); Assert.Equal(12.5, first.EndSeconds); Assert.Equal("movie-Part01.mkv", first.OutputFileName); },
            second => { Assert.Equal(12.5, second.StartSeconds); Assert.Equal(50, second.EndSeconds); Assert.Equal("movie-Part02.mkv", second.OutputFileName); });
        Assert.False(VideoSplitterForm.TryCreateSplitSegments("movie.mkv", 0, 50, out _, out string firstFrameError));
        Assert.Contains("after", firstFrameError, StringComparison.OrdinalIgnoreCase);
        Assert.False(VideoSplitterForm.TryCreateSplitSegments("movie.mkv", 50, 50, out _, out _));
    }

    [Theory]
    [InlineData(VideoSplitterSplitKeep.BothSides, 2, 0, 12.5, 12.5, 50)]
    [InlineData(VideoSplitterSplitKeep.BeforeNow, 1, 0, 12.5, 0, 0)]
    [InlineData(VideoSplitterSplitKeep.AfterNow, 1, 12.5, 50, 0, 0)]
    public void SplitKeepSelectorCreatesOnlyRequestedRanges(VideoSplitterSplitKeep keep, int count, double firstStart, double firstEnd, double secondStart, double secondEnd)
    {
        Assert.True(VideoSplitterForm.TryCreateSplitSegments("movie.mkv", 12.5, 50, keep, out VideoSplitterSegment[] segments, out _));

        Assert.Equal(count, segments.Length);
        Assert.Equal(firstStart, segments[0].StartSeconds);
        Assert.Equal(firstEnd, segments[0].EndSeconds);
        if (count == 2)
        {
            Assert.Equal(secondStart, segments[1].StartSeconds);
            Assert.Equal(secondEnd, segments[1].EndSeconds);
        }
    }

    [Theory]
    [InlineData(VideoSplitterSplitKeep.BothSides, 0)]
    [InlineData(VideoSplitterSplitKeep.BeforeNow, 0.0005)]
    [InlineData(VideoSplitterSplitKeep.AfterNow, 49.9995)]
    public void SplitKeepSelectorRejectsNearBoundaryZeroLengthRanges(VideoSplitterSplitKeep keep, double position)
    {
        Assert.False(VideoSplitterForm.TryCreateSplitSegments("movie.mkv", position, 50, keep, out _, out string error));
        Assert.Contains("after", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreviewSeekBeforeFirstPlaybackWaitsForASeekablePlayerAndUsesCurrentPosition()
    {
        var previewSeek = new PreviewSeekCoordinator();
        previewSeek.Request(0);
        previewSeek.Request(14.25);

        Assert.True(previewSeek.TryGet(out double position));
        Assert.Equal(14.25, position);
        Assert.False(PreviewSeekCoordinator.CanSeek(9));
        Assert.True(PreviewSeekCoordinator.CanSeek(10));
        previewSeek.Complete();
        Assert.False(previewSeek.TryGet(out _));
    }

    [Theory]
    [InlineData(640)]
    [InlineData(1200)]
    public void TimelineMapsFullDurationToItsVisibleClientRectangle(int width)
    {
        using var timeline = new TimelineControl { Size = new Size(width, 100) };
        timeline.SetDuration(100);
        timeline.InSeconds = 25;
        timeline.OutSeconds = 99.5;
        timeline.PositionSeconds = 75;
        Rectangle track = timeline.TrackRectangleForTesting;

        foreach (double percent in new[] { 0d, 0.25, 0.5, 0.75, 0.995, 1d })
        {
            float x = timeline.XForSecondsForTesting(percent * 100);
            Assert.InRange(x, track.Left, track.Right);
            Assert.Equal(track.Left + track.Width * percent, x, 1);
            Assert.Equal(percent * 100, timeline.SecondsForXForTesting((int)Math.Round(x)), 0.25);
        }

        timeline.Width += 137;
        Assert.Equal(25, timeline.InSeconds);
        Assert.Equal(99.5, timeline.OutSeconds);
        Assert.Equal(75, timeline.PositionSeconds);
        Assert.Equal(timeline.TrackRectangleForTesting.Right, timeline.XForSecondsForTesting(100), 1);
    }

    [Fact]
    public void SplitterLayoutKeepsSegmentActionsAccessibleAcrossSupportedSizes()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string configPath = Path.Combine(_root, "layout.json");
                using var form = new VideoSplitterForm(new Config(), configPath);
                foreach ((Size? size, bool maximized) in new[]
                {
                    ((Size?)new Size(1120, 760), false),
                    ((Size?)form.MinimumSize, false),
                    ((Size?)new Size(1440, 960), false),
                    ((Size?)null, true)
                })
                {
                    form.WindowState = FormWindowState.Normal;
                    if (maximized) form.WindowState = FormWindowState.Maximized;
                    else form.Size = size!.Value;
                    form.Show();
                    Application.DoEvents();
                    string[] splitterNames = { "SplitterMediaEditor", "SplitterTimelineDetails", "SplitterBoundarySegments", "SplitterSegmentsOutput" };
                    foreach (string splitterName in splitterNames)
                    {
                        SplitContainer splitter = (SplitContainer)form.Controls.Find(splitterName, true).Single();
                        Assert.True(splitter.Visible, $"{splitterName} should be visible.");
                        int available = (splitter.Orientation == Orientation.Vertical ? splitter.ClientSize.Width : splitter.ClientSize.Height) - splitter.SplitterWidth;
                        Assert.InRange(splitter.SplitterDistance, splitter.Panel1MinSize, available - splitter.Panel2MinSize);
                    }
                    Assert.DoesNotContain(form.Controls.Find("SplitterEditingSurface", true), control => control is ScrollableControl scrollable && scrollable.AutoScroll);

                    Control boundary = form.Controls.Find("SplitterBoundaryPanel", true).Single();
                    Control segments = form.Controls.Find("SplitterSegmentsPanel", true).Single();
                    Control output = form.Controls.Find("SplitterExportPanel", true).Single();
                    Assert.False(boundary.RectangleToScreen(boundary.ClientRectangle).IntersectsWith(segments.RectangleToScreen(segments.ClientRectangle)));
                    Assert.False(segments.RectangleToScreen(segments.ClientRectangle).IntersectsWith(output.RectangleToScreen(output.ClientRectangle)));

                    foreach (string name in new[]
                    {
                        "SetInButton", "SetOutButton", "AddSegmentButton", "UpdateSegmentButton", "RemoveSegmentButton",
                        "ClearSegmentsButton", "PreviewSelectionButton", "SplitKeepSelector", "SplitAtCurrentPositionButton", "SplitterOutputFolder",
                        "SplitterProcessingMode", "SplitterPlayOutput", "SplitterExportButton", "SplitterCancelExportButton", "SplitterOpenOutputButton"
                    })
                    {
                        Control control = form.Controls.Find(name, true).Single();
                        Assert.True(control.Visible, $"{name} should be visible.");
                    }
                }
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Splitter layout test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
