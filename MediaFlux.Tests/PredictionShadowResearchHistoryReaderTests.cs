using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PredictionShadowResearchHistoryReaderTests : IDisposable
{
    private static readonly DateTime BaseUtc = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxResearchHistoryTests", Guid.NewGuid().ToString("N"));

    public PredictionShadowResearchHistoryReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ReadsActiveOnlyAndArchivedOnlyStatistics()
    {
        string active = ActivePath("active-only.jsonl");
        WriteRecords(active, [Record("active", BaseUtc)]);
        Assert.Equal("active", Assert.Single(new PredictionShadowResearchHistoryReader(active).ReadFinalizedStatistics()).Id);

        string archivedOnly = ActivePath("archived-only.jsonl");
        WriteRecords(ArchivePath(archivedOnly, 1), [Record("archived", BaseUtc)]);
        Assert.Equal("archived", Assert.Single(
            new PredictionShadowResearchHistoryReader(archivedOnly).ReadFinalizedStatistics()).Id);
    }

    [Fact]
    public void MergesChronologicallyAndDeduplicatesExactIdsAcrossGenerations()
    {
        string active = ActivePath("merge.jsonl");
        EncodingStatisticsRecord first = Record("first", BaseUtc);
        EncodingStatisticsRecord second = Record("second", BaseUtc.AddMinutes(1));
        EncodingStatisticsRecord third = Record("third", BaseUtc.AddMinutes(1));
        WriteRecords(ArchivePath(active, 1), [third, first]);
        WriteRecords(active, [second, first]);

        EncodingStatisticsRecord[] history = new PredictionShadowResearchHistoryReader(active)
            .ReadFinalizedStatistics().ToArray();
        Assert.Equal(new[] { "first", "second", "third" }, history.Select(item => item.Id));
    }

    [Fact]
    public void ConflictingDuplicateStatisticsAndMalformedLinesFailClosed()
    {
        string active = ActivePath("conflict.jsonl");
        EncodingStatisticsRecord original = Record("same-id", BaseUtc);
        WriteRecords(ArchivePath(active, 1), [original]);
        WriteRecords(active, [original with { OutputSizeBytes = 1234 }]);
        Assert.Throws<InvalidDataException>(() => new PredictionShadowResearchHistoryReader(active).ReadFinalizedStatistics());

        string malformed = ActivePath("malformed.jsonl");
        WriteRecords(malformed, [Record("valid", BaseUtc)]);
        File.AppendAllText(malformed, "{not-json}" + Environment.NewLine);
        Assert.Throws<InvalidDataException>(() => new PredictionShadowResearchHistoryReader(malformed).ReadFinalizedStatistics());
    }

    [Fact]
    public void ProductionStatisticsServiceContinuesReadingOnlyTheActiveJournal()
    {
        string active = ActivePath("production.jsonl");
        WriteRecords(active, [Record("active", BaseUtc)]);
        WriteRecords(ArchivePath(active, 1), [Record("archived", BaseUtc.AddMinutes(-1))]);

        Assert.Equal(new[] { "active" }, new EncodingStatisticsService(active).GetAll().Select(item => item.Id));
        Assert.Equal(new[] { "archived", "active" }, new PredictionShadowResearchHistoryReader(active)
            .ReadFinalizedStatistics().Select(item => item.Id));
    }

    [Fact]
    public void UnspecifiedLegacyEndUtcIsInterpretedAsUtcLikeProductionStatistics()
    {
        string active = ActivePath("unspecified-end-time.jsonl");
        EncodingStatisticsRecord legacy = Record("legacy", DateTime.SpecifyKind(BaseUtc, DateTimeKind.Unspecified));
        WriteRecords(active, [legacy]);

        EncodingStatisticsRecord loaded = Assert.Single(
            new PredictionShadowResearchHistoryReader(active).ReadFinalizedStatistics());
        Assert.Equal(DateTimeKind.Utc, loaded.EndUtc.Kind);
        Assert.Equal(BaseUtc.Ticks, loaded.EndUtc.Ticks);
    }

    private string ActivePath(string fileName) => Path.Combine(_root, fileName);
    private static string ArchivePath(string activePath, int generation) =>
        Path.Combine(Path.GetDirectoryName(activePath)!, Path.GetFileNameWithoutExtension(activePath) + $".old{generation}");

    private static EncodingStatisticsRecord Record(string id, DateTime endUtc) => new()
    {
        Id = id,
        StartUtc = endUtc.AddMinutes(-1),
        EndUtc = endUtc,
        Outcome = EncodingStatisticsOutcome.Success,
        SourcePath = "source-" + id,
        OutputPath = "output-" + id
    };

    private static void WriteRecords(string path, IEnumerable<EncodingStatisticsRecord> records) =>
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
}
