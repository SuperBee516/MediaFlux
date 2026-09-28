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
        var entry = new PredictionShadowJournalEvent
        {
            EventId = $"{observationId}:outcome",
            ObservationId = observationId,
            EventType = "Outcome",
            RecordedUtc = recordedUtc,
            Outcome = outcome
        };
        lock (_sync)
        {
            if (!_frozen.ContainsKey(observationId) || _eventIds.Contains(entry.EventId))
                return false;
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
