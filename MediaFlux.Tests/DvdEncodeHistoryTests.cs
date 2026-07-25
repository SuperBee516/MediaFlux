using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdEncodeHistoryTests : IDisposable
{
    private readonly string _root;

    public DvdEncodeHistoryTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdHistoryTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DvdEncodeHistoryPersistsAsOneLogicalOperation()
    {
        string historyPath = Path.Combine(_root, "history.json");
        var service = new HistoryService(historyPath);
        var record = new JobHistoryRecord
        {
            Type = JobType.DvdEncode,
            Status = JobStatus.Success,
            StartUtc = DateTime.UtcNow.AddMinutes(-2),
            EndUtc = DateTime.UtcNow,
            SourcePath = Path.Combine(_root, "Movie", "VIDEO_TS"),
            OutputPath = Path.Combine(_root, "output", "Movie_h265.mp4"),
            DurationSec = 90,
            Notes = "TitleSet=VTS_01; Segments=4; Source deletion disabled"
        };

        service.Append(record);
        JobHistoryRecord loaded = Assert.Single(service.LoadAll());

        Assert.Equal(JobType.DvdEncode, loaded.Type);
        Assert.Equal(record.SourcePath, loaded.SourcePath);
        Assert.Equal(record.OutputPath, loaded.OutputPath);
        Assert.Contains("Segments=4", loaded.Notes);
    }

    [Fact]
    public void DvdRemuxHistoryPreservesDetailedOperationFields()
    {
        string historyPath = Path.Combine(_root, "remux-history.json");
        var service = new HistoryService(historyPath);
        service.Append(new JobHistoryRecord
        {
            Type = JobType.DvdRemux,
            Status = JobStatus.Failed,
            StartUtc = DateTime.UtcNow.AddMinutes(-1),
            EndUtc = DateTime.UtcNow,
            SourcePath = Path.Combine(_root, "Movie", "VIDEO_TS"),
            OutputPath = Path.Combine(_root, "Movie.mkv"),
            DvdTitleSet = "VTS_02",
            DvdSegmentCount = 3,
            DvdOutputMode = DvdOutputMode.LosslessRemuxToMkv.ToString(),
            SourceSizeBytes = 123_456,
            OutputSizeBytes = 12_345,
            WasRecommendedDvdTitle = false,
            ErrorSummary = "Output failed validation."
        });

        JobHistoryRecord loaded = Assert.Single(service.LoadAll());

        Assert.Equal(JobType.DvdRemux, loaded.Type);
        Assert.Equal("VTS_02", loaded.DvdTitleSet);
        Assert.Equal(3, loaded.DvdSegmentCount);
        Assert.Equal(123_456, loaded.SourceSizeBytes);
        Assert.Equal(12_345, loaded.OutputSizeBytes);
        Assert.False(loaded.WasRecommendedDvdTitle);
        Assert.Equal("Output failed validation.", loaded.ErrorSummary);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
