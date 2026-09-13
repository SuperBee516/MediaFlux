using MediaFlux.Services;
using Xunit;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using MediaFlux;
using MediaFlux.Models;

namespace MediaFlux.Tests;

public sealed class JobHistoryPresentationTests
{
    private static JobHistoryRecord Record(JobStatus status, string source = "C:\\media\\clip.mp4", DateTime? end = null, string notes = "") => new() { Status = status, Type = JobType.Encode, SourcePath = source, OutputPath = "C:\\out\\clip.mkv", EndUtc = end ?? new DateTime(2026, 9, 12, 16, 37, 0, DateTimeKind.Utc), Notes = notes };

    [Fact]
    public void CountsAndStatusFilterUseDisplayedSet()
    {
        var records = new[] { Record(JobStatus.Success), Record(JobStatus.Failed), Record(JobStatus.Canceled) };
        var filtered = JobHistoryPresentation.Filter(records, "", "Failed", "All", JobHistoryDateFilter.All, new DateTime(2026, 9, 12, 14, 0, 0));
        Assert.Single(filtered); Assert.Equal(JobStatus.Failed, filtered[0].Status); Assert.Equal((1, 0, 1, 0), JobHistoryPresentation.Counts(filtered));
    }

    [Fact]
    public void SearchMatchesFilenameAndResult()
    {
        var record = Record(JobStatus.Failed, source: "C:\\media\\holiday.mp4", notes: "FFmpeg timeout");
        Assert.Single(JobHistoryPresentation.Filter(new[] { record }, "holiday", "All", "All", JobHistoryDateFilter.All)); Assert.Single(JobHistoryPresentation.Filter(new[] { record }, "timeout", "All", "All", JobHistoryDateFilter.All));
    }

    [Fact]
    public void DateFilterAndPresentationKeepTimestampSortable()
    {
        TimeZoneInfo context = TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-4), "Test Eastern", "Test Eastern");
        DateTime now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc); DateTime end = new(2026, 9, 11, 23, 41, 0, DateTimeKind.Utc); var record = Record(JobStatus.Success, end: end);
        Assert.Single(JobHistoryPresentation.Filter(new[] { record }, "", "All", "All", JobHistoryDateFilter.Last7Days, now, context)); Assert.Equal("Yesterday 7:41 PM", JobHistoryPresentation.FormatFinished(end, now, context)); Assert.Equal("clip.mp4", JobHistoryPresentation.FileNameOrUnavailable(record.SourcePath)); Assert.Equal(end, record.EndUtc);
    }

    [Fact]
    public void MissingOutputIsExplicit() => Assert.Equal("Unavailable", JobHistoryPresentation.FileNameOrUnavailable(""));

    [Fact]
    public void WindowBoundsRestoreClampsInvalidAndOffScreenValues()
    {
        Size minimum = new(980, 680); Rectangle area = new(0, 0, 1920, 1080);
        Rectangle invalid = JobHistoryPresentation.RestoreWindowBounds(-100000, -100000, 1, 1, minimum, new Size(1280, 820), new[] { area });
        Assert.Equal(new Size(980, 680), invalid.Size); Assert.True(area.Contains(invalid));
        Rectangle valid = JobHistoryPresentation.RestoreWindowBounds(200, 150, 1400, 800, minimum, new Size(1280, 820), new[] { area });
        Assert.Equal(new Rectangle(200, 150, 1400, 800), valid);
    }

    [Fact]
    public void JobHistoryFormConstructsAndShowsWithLegacyInvalidBounds()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null; bool visible = false; Rectangle bounds = Rectangle.Empty;
        var thread = new Thread(() =>
        {
            JobHistoryForm? form = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string root = Path.Combine(Path.GetTempPath(), "MediaFluxJobHistoryTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                var config = new Config { JobHistoryWindowWidth = 1, JobHistoryWindowHeight = 1, JobHistoryWindowX = -100000, JobHistoryWindowY = -100000 };
                form = new JobHistoryForm(new HistoryService(Path.Combine(root, "history.json")), config, Path.Combine(root, "config.json")); form.Show(); Application.DoEvents(); visible = form.Visible; bounds = form.Bounds; form.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { if (form != null && !form.IsDisposed) form.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Job History UI test timed out."); if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString()); Assert.True(visible); Assert.True(bounds.Width >= 980); Assert.True(bounds.Height >= 680);
    }
}
