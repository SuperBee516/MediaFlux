using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PredictionShadowObservationJournalGenerationTests : IDisposable
{
    private static readonly DateTime BaseUtc = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxJournalGenerationTests", Guid.NewGuid().ToString("N"));

    public PredictionShadowObservationJournalGenerationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ReadsActiveOnlyAndArchivedOnlyGenerations()
    {
        string active = ActivePath("active-only.jsonl");
        WriteEvents(active, [FrozenEvent("active", 3, BaseUtc)]);
        PredictionShadowObservationJournal activeJournal = new(active);

        Assert.True(activeJournal.TryReadEventsForTemporalComparison(out IReadOnlyList<PredictionShadowJournalEvent> activeEvents));
        Assert.Equal("active", Assert.Single(activeEvents).ObservationId);

        string archivedOnly = ActivePath("archived-only.jsonl");
        WriteEvents(ArchivePath(archivedOnly, 1), [FrozenEvent("archived", 2, BaseUtc.AddMinutes(1))]);
        PredictionShadowObservationJournal archivedJournal = new(archivedOnly);

        Assert.True(archivedJournal.TryReadEventsForTemporalComparison(out IReadOnlyList<PredictionShadowJournalEvent> archivedEvents));
        Assert.Equal("archived", Assert.Single(archivedEvents).ObservationId);
        Assert.Equal(2, Assert.Single(archivedEvents).SchemaVersion);
    }

    [Fact]
    public void MergesGenerationsInDeterministicChronologicalOrderAndDeduplicatesExactEvents()
    {
        string active = ActivePath("merge.jsonl");
        PredictionShadowJournalEvent oldFrozen = FrozenEvent("older", 1, BaseUtc);
        PredictionShadowJournalEvent newerFrozen = FrozenEvent("newer", 3, BaseUtc.AddMinutes(2));
        PredictionShadowJournalEvent tiedFrozen = FrozenEvent("tie", 2, BaseUtc.AddMinutes(2));
        WriteEvents(ArchivePath(active, 1), [newerFrozen, oldFrozen]);
        WriteEvents(active, [tiedFrozen, oldFrozen]);

        var journal = new PredictionShadowObservationJournal(active);
        Assert.True(journal.TryReadEventsForTemporalComparison(out IReadOnlyList<PredictionShadowJournalEvent> events));
        Assert.Equal(new[] { "older:frozen", "newer:frozen", "tie:frozen" }, events.Select(item => item.EventId));
        Assert.Equal(new[] { 1, 3, 2 }, events.Select(item => item.SchemaVersion));
    }

    [Fact]
    public void ConflictingDuplicateEventFailsClosedAndIsNotSelectedByGenerationOrder()
    {
        string active = ActivePath("conflict.jsonl");
        PredictionShadowJournalEvent archived = FrozenEvent("same-id", 3, BaseUtc);
        PredictionShadowJournalEvent conflict = archived with
        {
            RecordedUtc = BaseUtc.AddSeconds(1),
            Frozen = archived.Frozen! with { FrozenUtc = BaseUtc.AddSeconds(1) }
        };
        WriteEvents(ArchivePath(active, 1), [archived]);
        WriteEvents(active, [conflict]);

        var journal = new PredictionShadowObservationJournal(active);
        Assert.False(journal.TryReadEventsForTemporalComparison(out _));
        Assert.Empty(journal.ReadEvents());
    }

    [Fact]
    public void OutcomeCanBeAppendedToActiveWhenItsFrozenRecordIsArchivedAndKeepsFrozenAssignment()
    {
        string active = ActivePath("cross-generation-pair.jsonl");
        PredictionShadowExperimentAssignment assignment = new(
            "MF-3C3-G3-R1", 2, 1, PredictionShadowExperimentStratum.Control, PredictionShadowExperimentRole.Target);
        PredictionShadowJournalEvent frozen = FrozenEvent("paired", 3, BaseUtc, assignment);
        WriteEvents(ArchivePath(active, 1), [frozen]);

        var journal = new PredictionShadowObservationJournal(active);
        Assert.True(journal.AppendOutcome("paired", SuccessfulOutcome(), BaseUtc.AddMinutes(1)));
        Assert.True(journal.TryReadEventsForTemporalComparison(out IReadOnlyList<PredictionShadowJournalEvent> events));
        Assert.Equal(new[] { "paired:frozen", "paired:outcome" }, events.Select(item => item.EventId));
        Assert.Equal(assignment, Assert.Single(events, item => item.Outcome is not null).Outcome!.ExperimentAssignment);
        Assert.Equal(assignment, Assert.Single(events, item => item.Frozen is not null).Frozen!.ExperimentAssignment);
    }

    [Fact]
    public void RegistrationRequiresFirstTargetThenSequentialExplicitReplacements()
    {
        string active = ActivePath("assignment-sequence.jsonl");
        var journal = new PredictionShadowObservationJournal(active);
        PredictionShadowExperimentAssignment target = Assignment(1, 1, PredictionShadowExperimentRole.Target);
        PredictionShadowExperimentAssignment skippedAttempt = Assignment(1, 2, PredictionShadowExperimentRole.Replacement);
        Assert.True(journal.CanRegisterExperimentAssignment(target));
        Assert.False(journal.CanRegisterExperimentAssignment(skippedAttempt));
        Assert.False(journal.CanRegisterExperimentAssignment(
            Assignment(1, 2, PredictionShadowExperimentRole.Target)));

        Assert.True(journal.AppendFrozen(FrozenEvent("target", 3, BaseUtc, target).Frozen!));
        Assert.False(journal.CanRegisterExperimentAssignment(target));
        PredictionShadowExperimentAssignment replacement = Assignment(
            1, 2, PredictionShadowExperimentRole.Replacement);
        Assert.True(journal.CanRegisterExperimentAssignment(replacement));
        Assert.True(journal.AppendFrozen(FrozenEvent("replacement", 3, BaseUtc.AddMinutes(1), replacement).Frozen!));
        Assert.False(journal.CanRegisterExperimentAssignment(Assignment(
            1, 3, PredictionShadowExperimentRole.Target)));
        Assert.True(journal.CanRegisterExperimentAssignment(Assignment(
            1, 3, PredictionShadowExperimentRole.Replacement)));
    }

    [Fact]
    public void IncompleteOrConflictingHistoryBlocksNewResearchAssignments()
    {
        string active = ActivePath("assignment-conflict.jsonl");
        PredictionShadowJournalEvent first = FrozenEvent("same", 3, BaseUtc, Assignment(1, 1, PredictionShadowExperimentRole.Target));
        PredictionShadowJournalEvent conflict = first with
        {
            RecordedUtc = BaseUtc.AddSeconds(1),
            Frozen = first.Frozen! with { FrozenUtc = BaseUtc.AddSeconds(1) }
        };
        WriteEvents(ArchivePath(active, 1), [first]);
        WriteEvents(active, [conflict]);

        Assert.False(new PredictionShadowObservationJournal(active).CanRegisterExperimentAssignment(
            Assignment(2, 1, PredictionShadowExperimentRole.Target)));
    }

    [Fact]
    public void SchemaVersionsOneTwoAndThreeRemainReadableAcrossGenerations()
    {
        string active = ActivePath("schemas.jsonl");
        WriteEvents(ArchivePath(active, 1), [FrozenEvent("v1", 1, BaseUtc)]);
        WriteEvents(ArchivePath(active, 2), [FrozenEvent("v2", 2, BaseUtc.AddMinutes(1))]);
        WriteEvents(active, [FrozenEvent("v3", 3, BaseUtc.AddMinutes(2))]);

        var journal = new PredictionShadowObservationJournal(active);
        Assert.True(journal.TryReadEventsForTemporalComparison(out IReadOnlyList<PredictionShadowJournalEvent> events));
        Assert.Equal(new[] { 1, 2, 3 }, events.Select(item => item.SchemaVersion));
    }

    [Fact]
    public void ArchivedFrozenFamilyRemainsKnownWhenCheckingFutureResearchTargets()
    {
        string active = ActivePath("known-family.jsonl");
        WriteEvents(ArchivePath(active, 1), [FrozenEvent("known-family", 3, BaseUtc)]);
        var journal = new PredictionShadowObservationJournal(active);

        Assert.True(journal.HasFrozenSourceFamily("family-known-family"));
        Assert.False(journal.HasFrozenSourceFamily("different-family"));
    }

    [Fact]
    public void ReadsExternalAppendsAndDamagedLinesWithoutTreatingPartialHistoryAsComplete()
    {
        string active = ActivePath("external-append.jsonl");
        var journal = new PredictionShadowObservationJournal(active);
        PredictionShadowFrozenObservation frozen = FrozenEvent("external", 3, BaseUtc).Frozen!;
        Assert.True(journal.AppendFrozen(frozen));
        Assert.Single(new PredictionShadowObservationJournal(active).ReadEvents());

        File.AppendAllText(active, "{not-json}" + Environment.NewLine);
        Assert.Single(journal.ReadEvents());
        Assert.False(journal.TryReadEventsForTemporalComparison(out _));
    }

    private string ActivePath(string fileName) => Path.Combine(_root, fileName);
    private static string ArchivePath(string activePath, int generation) =>
        Path.Combine(Path.GetDirectoryName(activePath)!, Path.GetFileNameWithoutExtension(activePath) + $".old{generation}");

    private static PredictionShadowJournalEvent FrozenEvent(
        string id,
        int schema,
        DateTime recordedUtc,
        PredictionShadowExperimentAssignment? assignment = null)
    {
        var frozen = new PredictionShadowFrozenObservation
        {
            SchemaVersion = schema,
            ObservationId = id,
            SourceFamilyKey = "family-" + id,
            PredictionEvidenceCutoffUtc = recordedUtc.AddSeconds(-1),
            FrozenUtc = recordedUtc,
            ExperimentAssignment = assignment
        };
        return new PredictionShadowJournalEvent
        {
            SchemaVersion = schema,
            EventId = id + ":frozen",
            ObservationId = id,
            EventType = "Frozen",
            RecordedUtc = recordedUtc,
            Frozen = frozen
        };
    }

    private static PredictionShadowOutcome SuccessfulOutcome() => new()
    {
        State = "Completed",
        TerminalResult = "Completed",
        ValidationState = "Passed",
        FinalizationState = "Passed",
        ActualOutputVideoBitrateKbps = 5000
    };

    private static PredictionShadowExperimentAssignment Assignment(
        int slot, int attempt, PredictionShadowExperimentRole role) =>
        new("MF-3C3-G3-R1", slot, attempt, PredictionShadowExperimentStratum.Control, role);

    private static void WriteEvents(string path, IEnumerable<PredictionShadowJournalEvent> events)
    {
        File.WriteAllLines(path, events.Select(item => JsonSerializer.Serialize(item, Json)));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
