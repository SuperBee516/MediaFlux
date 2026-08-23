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
            VideoSplitterPreviewSplitterDistance = 215
        }.Save(path);

        Config loaded = Config.Load(path);
        Assert.Equal(220, loaded.VideoSplitterWindowX);
        Assert.Equal(160, loaded.VideoSplitterWindowY);
        Assert.Equal(1080, loaded.VideoSplitterWindowWidth);
        Assert.Equal(740, loaded.VideoSplitterWindowHeight);
        Assert.Equal(215, loaded.VideoSplitterPreviewSplitterDistance);
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
                    Panel editing = (Panel)form.Controls.Find("SplitterEditingSurface", true).Single();
                    Assert.True(editing.AutoScroll);
                    editing.AutoScrollPosition = new Point(0, editing.VerticalScroll.Maximum);
                    Application.DoEvents();
                    Rectangle editingBounds = editing.RectangleToScreen(editing.ClientRectangle);
                    foreach (string name in new[] { "AddSegmentButton", "UpdateSegmentButton", "RemoveSegmentButton", "ClearSegmentsButton", "PreviewSelectionButton" })
                    {
                        Control button = form.Controls.Find(name, true).Single();
                        Assert.True(button.Visible, $"{name} should be visible.");
                        Assert.True(editingBounds.IntersectsWith(button.RectangleToScreen(button.ClientRectangle)), $"{name} should be reachable in the editing surface.");
                    }
                    SplitContainer workspace = (SplitContainer)form.Controls.Find("SplitterPreviewEditorSplit", true).Single();
                    Assert.True(workspace.Panel1MinSize >= 150);
                    Assert.True(workspace.Panel2MinSize >= 180);
                    Assert.True(workspace.SplitterDistance >= workspace.Panel1MinSize);
                    Assert.True(form.Controls.Find("SplitAtCurrentPositionButton", true).Single().Visible);
                    Control export = form.Controls.Find("SplitterExportPanel", true).Single();
                    Assert.True(export.Visible);
                    Assert.False(export.Bounds.IntersectsWith(editing.Bounds));
                    Rectangle exportBounds = export.RectangleToScreen(export.ClientRectangle);
                    foreach (string name in new[] { "SplitterOutputFolder", "SplitterProcessingMode", "SplitterPlayOutput", "SplitterExportButton", "SplitterCancelExportButton", "SplitterOpenOutputButton" })
                    {
                        Control control = form.Controls.Find(name, true).Single();
                        Assert.True(control.Visible, $"{name} should be visible.");
                        Assert.True(exportBounds.IntersectsWith(control.RectangleToScreen(control.ClientRectangle)), $"{name} should remain inside the export panel.");
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
