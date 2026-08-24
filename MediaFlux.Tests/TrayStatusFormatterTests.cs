using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class TrayStatusFormatterTests
{
    [Fact]
    public void RunningEncodeTakesPrecedenceOverScheduledJobs()
    {
        var scheduled = Scheduled("Night run", DateTime.Today.AddHours(22));
        TrayStatusInfo status = TrayStatusFormatter.Build(2, true, new[] { scheduled }, DateTime.Now);
        Assert.Equal("Encoding 2 jobs", status.Status); Assert.Null(status.NextRun);
    }

    [Fact]
    public void NextScheduledJobIsChosenChronologically()
    {
        DateTime now = DateTime.Today.AddHours(9);
        var later = Scheduled("Later", now.AddHours(3)); var next = Scheduled("Soon", now.AddHours(1));
        TrayStatusInfo status = TrayStatusFormatter.Build(0, false, new[] { later, next }, now);
        Assert.Equal("2 jobs scheduled", status.Status); Assert.Equal($"Soon — {next.ScheduledLocalTime!.Value:g}", status.NextRun);
    }

    [Fact]
    public void ReadyFallbackAndTooltipTruncationAreSafe()
    {
        Assert.Equal("Ready", TrayStatusFormatter.Build(0, false, null, DateTime.Now).Status);
        string text = TrayStatusFormatter.ToNotifyIconText(new string('x', 100));
        Assert.True(text.Length <= 63); Assert.StartsWith("MediaFlux", text);
    }

    private static EncodeJob Scheduled(string name, DateTime when) => new() { Name = name, Enabled = true, ScheduleType = EncodeJobScheduleType.Once, ScheduledLocalTime = when, Status = EncodeJobStatus.Scheduled };
}
