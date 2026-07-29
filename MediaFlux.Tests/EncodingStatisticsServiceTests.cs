using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingStatisticsServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _statisticsPath;

    public EncodingStatisticsServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-EncodingStatisticsTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _statisticsPath = Path.Combine(_root, "encoding-statistics.jsonl");
    }

    [Fact]
    public void FinalizedRecordPersistsAcrossServiceInstances()
    {
        var service = new EncodingStatisticsService(_statisticsPath);
        var record = CreateRecord(
            id: "persisted-result",
            EncodingStatisticsOutcome.Success,
            endUtc: new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc),
            sourceBytes: 4_000,
            outputBytes: 2_500,
            durationSeconds: 120,
            processingSeconds: 60);

        Assert.True(service.AppendFinalized(record));

        var reloaded = new EncodingStatisticsService(_statisticsPath);
        EncodingStatisticsRecord actual = Assert.Single(reloaded.GetAll());
        Assert.Equal(record.Id, actual.Id);
        Assert.Equal(EncodingStatisticsOutcome.Success, actual.Outcome);
        Assert.Equal(4_000, actual.SourceSizeBytes);
        Assert.Equal(2_500, actual.OutputSizeBytes);
        Assert.Equal(120, actual.MediaDurationSeconds);
        Assert.Equal(60, actual.ProcessingSeconds);
    }

    [Fact]
    public void StableRecordIdPreventsDuplicateCountingBeforeAndAfterRestart()
    {
        var first = new EncodingStatisticsService(_statisticsPath);
        EncodingStatisticsRecord record = CreateRecord(
            "same-operation",
            EncodingStatisticsOutcome.Success,
            DateTime.UtcNow,
            1_000,
            500,
            60,
            30);

        Assert.True(first.AppendFinalized(record));
        Assert.False(first.AppendFinalized(record));

        var restarted = new EncodingStatisticsService(_statisticsPath);
        Assert.False(restarted.AppendFinalized(record));
        Assert.Single(restarted.GetAll());
    }

    [Fact]
    public void IncompleteOutputsNeverContributeToStorageSavings()
    {
        var service = new EncodingStatisticsService(_statisticsPath);
        EncodingStatisticsRecord failed = CreateRecord(
            "failed",
            EncodingStatisticsOutcome.Failed,
            DateTime.UtcNow,
            10_000,
            7_500,
            60,
            12);

        Assert.True(service.AppendFinalized(failed));

        EncodingStatisticsRecord stored = Assert.Single(service.GetAll());
        Assert.Null(stored.OutputSizeBytes);
        EncodingStatisticsSnapshot snapshot =
            EncodingStatisticsCalculator.Aggregate(service.GetAll());
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(0, snapshot.FilesWithSizeData);
        Assert.Equal(0, snapshot.SpaceSavedBytes);
    }

    [Fact]
    public void AggregationCalculatesCountsSavingsPerformanceAndCodecGroups()
    {
        var records = new[]
        {
            CreateRecord(
                "success-1",
                EncodingStatisticsOutcome.Success,
                DateTime.UtcNow,
                1_000,
                600,
                100,
                50),
            CreateRecord(
                "success-2",
                EncodingStatisticsOutcome.Success,
                DateTime.UtcNow,
                2_000,
                1_000,
                300,
                100),
            CreateRecord(
                "failed",
                EncodingStatisticsOutcome.Failed,
                DateTime.UtcNow,
                900,
                null,
                80,
                30),
            CreateRecord(
                "skipped",
                EncodingStatisticsOutcome.Skipped,
                DateTime.UtcNow,
                800,
                null,
                70,
                0),
            CreateRecord(
                "cancelled",
                EncodingStatisticsOutcome.Cancelled,
                DateTime.UtcNow,
                700,
                null,
                60,
                20)
        };

        EncodingStatisticsSnapshot snapshot =
            EncodingStatisticsCalculator.Aggregate(records);

        Assert.Equal(5, snapshot.FilesProcessed);
        Assert.Equal(2, snapshot.Successful);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(1, snapshot.Skipped);
        Assert.Equal(1, snapshot.Cancelled);
        Assert.Equal(3_000, snapshot.OriginalBytes);
        Assert.Equal(1_600, snapshot.OutputBytes);
        Assert.Equal(1_400, snapshot.SpaceSavedBytes);
        Assert.Equal(46.6667, snapshot.ReductionPercent, precision: 3);
        Assert.Equal(700, snapshot.AverageSpaceSavedBytes);
        Assert.Equal(2.6667, snapshot.AverageEncodingSpeed, precision: 3);
        Assert.Equal(50, snapshot.AverageProcessingSeconds);

        EncodingStatisticsGroup group = Assert.Single(snapshot.Groups);
        Assert.Equal("H.265 / HEVC", group.Codec);
        Assert.Equal("NVIDIA NVENC", group.Encoder);
        Assert.Equal(5, group.FilesProcessed);
    }

    [Fact]
    public void PeriodRangesUseInclusiveLocalDatesAndExclusiveUtcEnd()
    {
        var now = new DateTimeOffset(
            2026,
            7,
            29,
            15,
            30,
            0,
            TimeSpan.Zero);

        EncodingStatisticsUtcRange today =
            EncodingStatisticsCalculator.GetUtcRange(
                EncodingStatisticsPeriod.Today,
                default,
                default,
                now,
                TimeZoneInfo.Utc);
        Assert.Equal(
            new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            today.StartUtc);
        Assert.Equal(
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            today.EndUtcExclusive);

        EncodingStatisticsUtcRange week =
            EncodingStatisticsCalculator.GetUtcRange(
                EncodingStatisticsPeriod.ThisWeek,
                default,
                default,
                now,
                TimeZoneInfo.Utc);
        Assert.Equal(
            new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
            week.StartUtc);
        Assert.Equal(
            new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
            week.EndUtcExclusive);

        EncodingStatisticsUtcRange custom =
            EncodingStatisticsCalculator.GetUtcRange(
                EncodingStatisticsPeriod.Custom,
                new DateTime(2026, 7, 20),
                new DateTime(2026, 7, 18),
                now,
                TimeZoneInfo.Utc);
        Assert.Equal(
            new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            custom.StartUtc);
        Assert.Equal(
            new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
            custom.EndUtcExclusive);
    }

    [Fact]
    public void AggregationHonorsRangeBoundaries()
    {
        DateTime start = new(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
        DateTime end = start.AddDays(1);
        var records = new[]
        {
            CreateRecord("before", EncodingStatisticsOutcome.Success, start.AddTicks(-1)),
            CreateRecord("at-start", EncodingStatisticsOutcome.Success, start),
            CreateRecord("inside", EncodingStatisticsOutcome.Failed, end.AddTicks(-1)),
            CreateRecord("at-end", EncodingStatisticsOutcome.Success, end)
        };

        EncodingStatisticsSnapshot snapshot =
            EncodingStatisticsCalculator.Aggregate(records, start, end);

        Assert.Equal(2, snapshot.FilesProcessed);
        Assert.Equal(1, snapshot.Successful);
        Assert.Equal(1, snapshot.Failed);
    }

    [Theory]
    [InlineData(1_048_576, "1 MB")]
    [InlineData(1_610_612_736, "1.5 GB")]
    [InlineData(1_099_511_627_776, "1 TB")]
    public void ByteFormattingUsesReadableBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, EncodingStatisticsCalculator.FormatBytes(bytes));
    }

    private static EncodingStatisticsRecord CreateRecord(
        string id,
        EncodingStatisticsOutcome outcome,
        DateTime endUtc,
        long? sourceBytes = 1_000,
        long? outputBytes = 500,
        double? durationSeconds = 100,
        double processingSeconds = 50)
    {
        return new EncodingStatisticsRecord
        {
            Id = id,
            Outcome = outcome,
            StartUtc = endUtc.AddSeconds(-processingSeconds),
            EndUtc = endUtc,
            SourcePath = $@"C:\Source\{id}.mkv",
            OutputPath = $@"C:\Output\{id}.mp4",
            Codec = "hevc_nvenc",
            Encoder = "NVIDIA NVENC",
            SourceSizeBytes = sourceBytes,
            OutputSizeBytes = outputBytes,
            MediaDurationSeconds = durationSeconds,
            ProcessingSeconds = processingSeconds
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
