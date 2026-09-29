using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Separate append-only JSONL storage for research forecasts and later outcomes.
/// A torn/invalid line is ignored when loading; the production statistics journal
/// is never read-modified-written by this service.
/// </summary>
public sealed class PredictionShadowObservationJournal
{
    private readonly string _path;
    private readonly object _sync = new();
    private readonly HashSet<string> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PredictionShadowFrozenObservation> _frozen =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _json = CreateJsonOptions();

    public PredictionShadowObservationJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        LoadExisting();
    }

    public string PathName => _path;

    public bool AppendFrozen(PredictionShadowFrozenObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var entry = new PredictionShadowJournalEvent
        {
            SchemaVersion = observation.SchemaVersion,
            EventId = $"{observation.ObservationId}:frozen",
            ObservationId = observation.ObservationId,
            EventType = "Frozen",
            RecordedUtc = observation.FrozenUtc,
            Frozen = observation
        };
        lock (_sync)
        {
            if (_eventIds.Contains(entry.EventId))
                return false;
            AppendLine(entry);
            _eventIds.Add(entry.EventId);
            _frozen[observation.ObservationId] = observation;
            return true;
        }
    }

    public bool TryGetFrozen(string observationId, out PredictionShadowFrozenObservation? observation)
    {
        lock (_sync)
            return _frozen.TryGetValue(observationId, out observation);
    }

    public bool AppendOutcome(string observationId, PredictionShadowOutcome outcome, DateTime recordedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_sync)
        {
            if (!_frozen.TryGetValue(observationId, out PredictionShadowFrozenObservation? frozen) ||
                _eventIds.Contains($"{observationId}:outcome"))
                return false;
            PredictionShadowOutcome outcomeWithFrozenAssignment = outcome with
            {
                ExperimentAssignment = frozen.ExperimentAssignment
            };
            var entry = new PredictionShadowJournalEvent
            {
                SchemaVersion = frozen.SchemaVersion,
                EventId = $"{observationId}:outcome",
                ObservationId = observationId,
                EventType = "Outcome",
                RecordedUtc = recordedUtc,
                Outcome = outcomeWithFrozenAssignment
            };
            AppendLine(entry);
            _eventIds.Add(entry.EventId);
            return true;
        }
    }

    public IReadOnlyList<PredictionShadowJournalEvent> ReadEvents()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
                return Array.Empty<PredictionShadowJournalEvent>();
            var result = new List<PredictionShadowJournalEvent>();
            foreach (string line in File.ReadLines(_path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<PredictionShadowJournalEvent>(line, _json) is { } entry)
                        result.Add(entry);
                }
                catch (JsonException) { /* An incomplete/torn line is non-authoritative. */ }
            }
            return result;
        }
    }

    /// <summary>
    /// Reads a complete, structurally valid journal snapshot for strict temporal
    /// chronology checks. Unlike <see cref="ReadEvents"/>, this does not silently
    /// skip damaged lines because doing so could hide an earlier outcome.
    /// </summary>
    public bool TryReadEventsForTemporalComparison(out IReadOnlyList<PredictionShadowJournalEvent> events)
    {
        lock (_sync)
        {
            events = Array.Empty<PredictionShadowJournalEvent>();
            if (!File.Exists(_path))
                return false;

            try
            {
                var result = new List<PredictionShadowJournalEvent>();
                var eventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        return false;
                    PredictionShadowJournalEvent? entry = JsonSerializer.Deserialize<PredictionShadowJournalEvent>(line, _json);
                    if (entry is null || entry.SchemaVersion is not (1 or 2 or 3) ||
                        string.IsNullOrWhiteSpace(entry.EventId) || string.IsNullOrWhiteSpace(entry.ObservationId) ||
                        entry.RecordedUtc == default || entry.RecordedUtc.Kind != DateTimeKind.Utc ||
                        !eventIds.Add(entry.EventId))
                        return false;

                    bool validPayload = entry.EventType switch
                    {
                        "Frozen" => entry.Frozen is { } frozen && entry.Outcome is null &&
                            frozen.SchemaVersion == entry.SchemaVersion &&
                            string.Equals(frozen.ObservationId, entry.ObservationId, StringComparison.Ordinal) &&
                            string.Equals(entry.EventId, $"{entry.ObservationId}:frozen", StringComparison.Ordinal),
                        "Outcome" => entry.Outcome is not null && entry.Frozen is null &&
                            string.Equals(entry.EventId, $"{entry.ObservationId}:outcome", StringComparison.Ordinal),
                        _ => false
                    };
                    if (!validPayload)
                        return false;
                    result.Add(entry);
                }

                events = result;
                return true;
            }
            catch
            {
                // Strict comparator admission treats unreadable evidence as an abstention.
                return false;
            }
        }
    }

    private void LoadExisting()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            foreach (string line in File.ReadLines(_path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<PredictionShadowJournalEvent>(line, _json) is not { } entry ||
                        string.IsNullOrWhiteSpace(entry.EventId) || string.IsNullOrWhiteSpace(entry.ObservationId))
                        continue;
                    _eventIds.Add(entry.EventId);
                    if (entry.EventType == "Frozen" && entry.Frozen is { } frozen)
                        _frozen[frozen.ObservationId] = frozen;
                }
                catch (JsonException) { /* Preserve valid entries after a damaged line. */ }
            }
        }
        catch { /* An unavailable research journal must not affect application startup or encoding. */ }
    }

    private void AppendLine(PredictionShadowJournalEvent entry)
    {
        string? parent = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
        File.AppendAllText(_path, JsonSerializer.Serialize(entry, _json) + Environment.NewLine, Encoding.UTF8);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
