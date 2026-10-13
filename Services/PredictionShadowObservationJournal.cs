using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Separate append-only JSONL storage for research forecasts and later outcomes.
/// Numeric .oldN generations are read as immutable history; new events append only
/// to the active path. The production statistics journal is never read-modified-written.
/// </summary>
public sealed class PredictionShadowObservationJournal
{
    private readonly string _path;
    private readonly object _sync = new();
    private readonly HashSet<string> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PredictionShadowFrozenObservation> _frozen =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PredictionShadowJournalEvent> _events = new();
    private bool _historyComplete = true;
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
            RefreshExisting();
            if (_eventIds.Contains(entry.EventId))
                return false;
            AppendLine(entry);
            _eventIds.Add(entry.EventId);
            _frozen[observation.ObservationId] = observation;
            _events.Add(entry);
            return true;
        }
    }

    public bool TryGetFrozen(string observationId, out PredictionShadowFrozenObservation? observation)
    {
        lock (_sync)
        {
            RefreshExisting();
            return _frozen.TryGetValue(observationId, out observation);
        }
    }

    public bool HasFrozenExperimentAttempt(string experimentId, int slot, int attempt)
    {
        if (string.IsNullOrWhiteSpace(experimentId) || slot <= 0 || attempt <= 0)
            return false;
        lock (_sync)
        {
            RefreshExisting();
            return _frozen.Values.Any(frozen => frozen.ExperimentAssignment is { } assignment &&
                string.Equals(assignment.ExperimentId, experimentId, StringComparison.Ordinal) &&
                assignment.Slot == slot && assignment.Attempt == attempt);
        }
    }

    public bool CanRegisterExperimentAssignment(PredictionShadowExperimentAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!assignment.IsValid())
            return false;

        lock (_sync)
        {
            RefreshExisting();
            if (!_historyComplete)
                return false;
            PredictionShadowExperimentAssignment[] attempts = _frozen.Values
                .Select(frozen => frozen.ExperimentAssignment)
                .Where(value => value is not null &&
                    string.Equals(value.ExperimentId, assignment.ExperimentId, StringComparison.Ordinal) &&
                    value.Slot == assignment.Slot)
                .Select(value => value!)
                .OrderBy(value => value.Attempt)
                .ToArray();

            if (attempts.Length == 0)
                return assignment.Attempt == 1 && assignment.Role == PredictionShadowExperimentRole.Target;

            PredictionShadowExperimentAssignment initial = attempts[0];
            if (initial.Attempt != 1 || initial.Role != PredictionShadowExperimentRole.Target ||
                attempts.Where((value, index) => value.Attempt != index + 1 ||
                    value.Stratum != initial.Stratum ||
                    value.Role != (index == 0
                        ? PredictionShadowExperimentRole.Target
                        : PredictionShadowExperimentRole.Replacement)).Any())
                return false;

            return PredictionShadowExperimentAssignmentPersistence.IsExplicitReplacement(
                attempts[^1], assignment);
        }
    }

    public bool HasAnyFrozenExperimentAttempt(string experimentId, int slot)
    {
        if (string.IsNullOrWhiteSpace(experimentId) || slot <= 0)
            return false;
        lock (_sync)
        {
            RefreshExisting();
            return _frozen.Values.Any(frozen => frozen.ExperimentAssignment is { } assignment &&
                string.Equals(assignment.ExperimentId, experimentId, StringComparison.Ordinal) &&
                assignment.Slot == slot);
        }
    }

    public bool HasFrozenSourceFamily(string sourceFamilyKey)
    {
        if (string.IsNullOrWhiteSpace(sourceFamilyKey))
            return false;
        lock (_sync)
        {
            RefreshExisting();
            return _frozen.Values.Any(frozen => string.Equals(
                frozen.SourceFamilyKey, sourceFamilyKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool TryGetFrozenExperimentAssignment(
        string experimentId, int slot, int attempt,
        out PredictionShadowExperimentAssignment? assignment)
    {
        lock (_sync)
        {
            RefreshExisting();
            assignment = _frozen.Values
                .Select(frozen => frozen.ExperimentAssignment)
                .FirstOrDefault(value => value is not null &&
                    string.Equals(value.ExperimentId, experimentId, StringComparison.Ordinal) &&
                    value.Slot == slot && value.Attempt == attempt);
            return assignment is not null;
        }
    }

    public bool AppendOutcome(string observationId, PredictionShadowOutcome outcome, DateTime recordedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_sync)
        {
            RefreshExisting();
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
            _events.Add(entry);
            return true;
        }
    }

    public IReadOnlyList<PredictionShadowJournalEvent> ReadEvents()
    {
        lock (_sync)
        {
            RefreshExisting();
            return OrderedEvents();
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
            RefreshExisting();
            events = Array.Empty<PredictionShadowJournalEvent>();
            if (!_historyComplete || JournalGenerationFileDiscovery.Discover(_path).Count == 0)
                return false;

            try
            {
                IReadOnlyList<PredictionShadowJournalEvent> result = OrderedEvents();
                var eventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (PredictionShadowJournalEvent? entry in result)
                {
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
        _eventIds.Clear();
        _frozen.Clear();
        _events.Clear();
        _historyComplete = true;
        var eventsById = new Dictionary<string, (PredictionShadowJournalEvent Event, string Canonical)>(
            StringComparer.OrdinalIgnoreCase);
        var conflictingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string path in JournalGenerationFileDiscovery.Discover(_path))
            {
                foreach (string line in File.ReadLines(path))
                {
                    PredictionShadowJournalEvent? entry;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            throw new JsonException("Blank journal line.");
                        entry = JsonSerializer.Deserialize<PredictionShadowJournalEvent>(line, _json);
                    }
                    catch (JsonException)
                    {
                        _historyComplete = false;
                        continue;
                    }

                    if (entry is null || string.IsNullOrWhiteSpace(entry.EventId) ||
                        string.IsNullOrWhiteSpace(entry.ObservationId))
                    {
                        _historyComplete = false;
                        continue;
                    }

                    _eventIds.Add(entry.EventId);
                    string canonical = JsonSerializer.Serialize(entry, _json);
                    if (!eventsById.TryGetValue(entry.EventId, out var current))
                    {
                        if (!conflictingIds.Contains(entry.EventId))
                            eventsById.Add(entry.EventId, (entry, canonical));
                    }
                    else if (!string.Equals(current.Canonical, canonical, StringComparison.Ordinal))
                    {
                        eventsById.Remove(entry.EventId);
                        conflictingIds.Add(entry.EventId);
                        _historyComplete = false;
                    }
                }
            }
        }
        catch
        {
            // An unavailable research journal must not affect application startup or encoding.
            _historyComplete = false;
        }

        _events.AddRange(eventsById.Values.Select(item => item.Event));
        foreach (PredictionShadowJournalEvent entry in _events)
        {
            if (entry.EventType == "Frozen" && entry.Frozen is { } frozen &&
                !conflictingIds.Contains($"{frozen.ObservationId}:frozen"))
                _frozen[frozen.ObservationId] = frozen;
        }
    }

    private IReadOnlyList<PredictionShadowJournalEvent> OrderedEvents() => _events
        .OrderBy(entry => entry.RecordedUtc)
        .ThenBy(entry => entry.EventId, StringComparer.Ordinal)
        .ToArray();

    private void RefreshExisting() => LoadExisting();

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
