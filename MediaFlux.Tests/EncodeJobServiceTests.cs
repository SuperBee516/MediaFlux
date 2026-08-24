using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeJobServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void PersistenceRoundTripKeepsIndependentSettingsAndOrderedFiles()
    {
        var service = new EncodeJobService(Path.Combine(_root, "jobs.json"));
        var job = new EncodeJob { Name = "Night", Settings = new EncodeJobSettings { OutputFolder = "D:\\Out", QualityValue = 19 }, Files = new() { new() { SourcePath = "b.mkv" }, new() { SourcePath = "a.mkv", CustomTargetMb = 100 } } };
        service.Save(new[] { job }); job.Settings.QualityValue = 30; job.Files.Reverse();
        var loaded = Assert.Single(service.Load());
        Assert.Equal(19, loaded.Settings.QualityValue); Assert.Equal(new[] { "b.mkv", "a.mkv" }, loaded.Files.Select(file => file.SourcePath));
    }

    [Fact]
    public void ScopeAndManualVsOnceSchedulingAreExplicit()
    {
        string[] all = ["first", "second", "third"];
        Assert.Equal(new[] { "second" }, EncodeJobService.SelectScope(all, new[] { "second" }, true));
        Assert.Equal(all, EncodeJobService.SelectScope(all, new[] { "second" }, false));
        var manual = new EncodeJob { ScheduleType = EncodeJobScheduleType.Manual, Status = EncodeJobStatus.Ready };
        var due = new EncodeJob { ScheduleType = EncodeJobScheduleType.Once, ScheduledLocalTime = DateTime.Now.AddMinutes(-1), Status = EncodeJobStatus.Scheduled };
        Assert.Equal(due, Assert.Single(EncodeJobService.Due(new[] { manual, due }, DateTime.Now)));
    }

    [Fact]
    public void StatusRefreshAndDueSelectionRespectEnabledAndState()
    {
        var job = new EncodeJob { ScheduleType = EncodeJobScheduleType.Once, ScheduledLocalTime = DateTime.Now.AddMinutes(1) };
        EncodeJobService.RefreshStatus(job); Assert.Equal(EncodeJobStatus.Scheduled, job.Status);
        job.Enabled = false; EncodeJobService.RefreshStatus(job); Assert.Equal(EncodeJobStatus.Disabled, job.Status);
        Assert.Empty(EncodeJobService.Due(new[] { job }, DateTime.Now.AddHours(1)));
    }

    [Fact]
    public void MissingSourcesAreSeparatedWithoutChangingFileOrder()
    {
        Directory.CreateDirectory(_root); string availablePath = Path.Combine(_root, "present.mkv"); File.WriteAllText(availablePath, "test");
        var result = EncodeJobService.SplitAvailableFiles(new[] { new EncodeJobFile { SourcePath = availablePath }, new EncodeJobFile { SourcePath = Path.Combine(_root, "gone.mkv") } });
        Assert.Equal(availablePath, Assert.Single(result.Available).SourcePath); Assert.Single(result.Missing);
    }

    [Fact]
    public void EditedEncodeSettingsRoundTripWithoutChangingTheOriginalSnapshot()
    {
        var service = new EncodeJobService(Path.Combine(_root, "jobs.json"));
        var original = new EncodeJobSettings { CompressionProfile = "Medium Quality (Default)", VideoCodec = "Hevc", QualityValue = 22, Resolution = "1080p", AudioChannels = "Stereo (2.0)" };
        var edited = original.Clone(); edited.CompressionProfile = "High Quality"; edited.QualityValue = 19; edited.Resolution = "720p"; edited.EncoderId = "libx265"; edited.OutputContainer = "Matroska"; edited.TenBit = true;
        Assert.Equal("Medium Quality (Default)", original.CompressionProfile); // Cancel/discard keeps the original snapshot intact.
        service.Save(new[] { new EncodeJob { Settings = edited } });
        var loaded = Assert.Single(service.Load()).Settings;
        Assert.Equal("High Quality", loaded.CompressionProfile); Assert.Equal(19, loaded.QualityValue); Assert.Equal("720p", loaded.Resolution); Assert.Equal("Stereo (2.0)", loaded.AudioChannels); Assert.Equal("libx265", loaded.EncoderId); Assert.Equal("Matroska", loaded.OutputContainer); Assert.True(loaded.TenBit);
    }
}
