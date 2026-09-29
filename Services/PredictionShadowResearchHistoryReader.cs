using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Research-only reader for finalized encoding evidence across journal generations.
/// Production estimate/calibration services continue to read EncodingStatisticsService's active journal only.
/// </summary>
public sealed class PredictionShadowResearchHistoryReader
{
    private readonly string _activeStatisticsPath;

    public PredictionShadowResearchHistoryReader(string activeStatisticsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeStatisticsPath);
        _activeStatisticsPath = Path.GetFullPath(activeStatisticsPath);
    }

    public IReadOnlyList<EncodingStatisticsRecord> ReadFinalizedStatistics()
    {
        var recordsById = new Dictionary<string, (EncodingStatisticsRecord Record, string Canonical)>(
            StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (string path in JournalGenerationFileDiscovery.Discover(_activeStatisticsPath))
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path))
            {
                lineNumber++;
                EncodingStatisticsRecord? record;
                try
                {
                    if (string.IsNullOrWhiteSpace(line))
                        throw new JsonException("Blank lines are not valid finalized statistics records.");
                    record = JsonSerializer.Deserialize<EncodingStatisticsRecord>(line, json);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Research statistics history is malformed at '{path}', line {lineNumber}.", ex);
                }

                if (record is null || string.IsNullOrWhiteSpace(record.Id) || record.EndUtc == default)
                    throw new InvalidDataException(
                        $"Research statistics history has an incomplete record at '{path}', line {lineNumber}.");

                record.EndUtc = NormalizeUtc(record.EndUtc);

                string canonical = JsonSerializer.Serialize(record, json);
                if (!recordsById.TryGetValue(record.Id, out var current))
                {
                    recordsById.Add(record.Id, (record, canonical));
                    continue;
                }

                if (!string.Equals(current.Canonical, canonical, StringComparison.Ordinal))
                    conflicts.Add(record.Id);
            }
        }

        if (conflicts.Count > 0)
            throw new InvalidDataException(
                "Conflicting finalized statistics IDs occur across journal generations: " +
                string.Join(", ", conflicts.OrderBy(id => id, StringComparer.Ordinal)));

        return recordsById.Values
            .Select(item => item.Record)
            .OrderBy(record => record.EndUtc)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
