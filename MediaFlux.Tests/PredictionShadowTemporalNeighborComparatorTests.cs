using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PredictionShadowTemporalNeighborComparatorTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-TemporalNeighborTests", Guid.NewGuid().ToString("N"));
    private readonly PredictionShadowTemporalNeighborComparator _comparator = new();

    public PredictionShadowTemporalNeighborComparatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SelectsExactlyTwoNearestIndependentFamiliesAndUsesExactFormulas()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["family-a", "family-b", "family-c"]);
        PredictionShadowTemporalNeighborComparison result = Compare(target,
        [
            Peer("obs-a", "family-a", 0.2, 4000, 8000),
            Peer("obs-b", "family-b", 0.4, 8000, 6000),
            Peer("obs-c", "family-c", 0.9, 6000, 3000)
        ]);

        Assert.Equal(2, result.K);
        Assert.Equal(3, result.EligiblePeerCount);
        Assert.Equal(0.2, result.EligiblePeerTemporalMinimum);
        Assert.Equal(0.9, result.EligiblePeerTemporalMaximum);
        Assert.Equal(new[] { "family-a", "family-b" }, result.SelectedNeighbors.Select(peer => peer.SourceFamilyKey));
        Assert.Equal(0.1, result.SelectedNeighbors[0].TemporalDistance, 12);
        Assert.Equal(0.1, result.SelectedNeighbors[1].TemporalDistance, 12);
        Assert.Equal("TemporalNeighborsSupported", result.Ratio.Reason);
        Assert.Equal("TemporalNeighborsSupported", result.Direct.Reason);
        Assert.Equal(13_750, result.Ratio.PredictedVideoBitrateKbps); // 10,000 × mean(2.0, 0.75)
        Assert.Equal(7_000, result.Direct.PredictedVideoBitrateKbps); // mean(8,000, 6,000)
        Assert.Equal(1.375, result.MeanNeighborOutputToSourceVideoBitrateRatio);
        Assert.Equal(2, result.SelectedNeighbors.Count);
        Assert.Equal(2, result.Ratio.IndependentPeerCount);
        Assert.Equal(2, result.Direct.IndependentPeerCount);
        Assert.Equal(8000, Assert.Single(result.SelectedNeighbors,
            peer => peer.SourceFamilyKey == "family-a").ActualOutputVideoBitrateKbps);
    }

    [Fact]
    public void SelectsByDistanceBeforeFamilyKey()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.5, ["far", "near-b", "near-c", "near-a"]);
        PredictionShadowTemporalNeighborComparison result = Compare(target,
        [
            Peer("far", "far", 0.9, 7000, 7000),
            Peer("b", "near-b", 0.51, 7000, 7000),
            Peer("c", "near-c", 0.7, 7000, 7000),
            Peer("a", "near-a", 0.2, 7000, 7000)
        ]);

        Assert.Equal(new[] { "near-b", "near-c" }, result.SelectedNeighbors.Select(peer => peer.SourceFamilyKey));
        Assert.Equal(0.01, result.SelectedNeighbors[0].TemporalDistance, 12);
        Assert.Equal(0.2, result.SelectedNeighbors[1].TemporalDistance, 12);
    }

    [Fact]
    public void BreaksEqualDistanceTiesByFamilyKeyDeterministically()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.5, ["z-family", "a-family", "m-family"]);
        PredictionShadowJournalEvent[] events =
        [
            .. Peer("obs-z", "z-family", 0.25, 4000, 8000),
            .. Peer("obs-m", "m-family", 0.75, 4000, 8000),
            .. Peer("obs-a", "a-family", 0.75, 4000, 8000)
        ];

        PredictionShadowTemporalNeighborComparison first = _comparator.Compare(target, true, events);
        PredictionShadowTemporalNeighborComparison second = _comparator.Compare(target, true, events);

        Assert.Equal(new[] { "a-family", "m-family" }, first.SelectedNeighbors.Select(peer => peer.SourceFamilyKey));
        Assert.Equal(first.SelectedNeighbors, second.SelectedNeighbors);
        Assert.Equal(first.Ratio.PredictedVideoBitrateKbps, second.Ratio.PredictedVideoBitrateKbps);
        Assert.Equal(first.Direct.PredictedVideoBitrateKbps, second.Direct.PredictedVideoBitrateKbps);
    }

    [Fact]
    public void IntersectsOnlyBaselineAdmittedFamiliesAndExcludesTargetAndDuplicateKeys()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3,
            ["allowed-a", "allowed-a", "allowed-b", "target-family", "own-family"]);
        var events = new List<PredictionShadowJournalEvent>();
        events.AddRange(Peer("allowed-a-id", "allowed-a", 0.2, 4000, 8000));
        events.AddRange(Peer("allowed-b-id", "allowed-b", 0.4, 4000, 8000));
        events.AddRange(Peer("excluded-id", "not-admitted", 0.3, 4000, 1000));
        events.AddRange(Peer("target-family-id", "target-family", 0.3, 4000, 1000));
        events.AddRange(Peer(target.ObservationId, "own-family", 0.3, 4000, 1000));

        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, true, events);

        Assert.Equal(2, result.EligiblePeerCount);
        Assert.Equal(new[] { "allowed-a", "allowed-b" }, result.SelectedNeighbors.Select(peer => peer.SourceFamilyKey));
    }

    [Fact]
    public void CountsRepeatedObservationsOfOneFamilyOnlyOnceAndUsesLatestCompletedEvidence()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.4, ["same-family", "other-family", "third-family"]);
        var events = new List<PredictionShadowJournalEvent>();
        events.AddRange(Peer("same-old", "same-family", 0.2, 4000, 2000,
            outcomeUtc: Cutoff.AddMinutes(-10)));
        events.AddRange(Peer("same-new", "same-family", 0.4, 4000, 9000,
            outcomeUtc: Cutoff.AddMinutes(-5)));
        events.AddRange(Peer("other", "other-family", 0.3, 4000, 5000));
        events.AddRange(Peer("third", "third-family", 0.9, 4000, 1000));

        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, true, events);

        Assert.Equal(3, result.EligiblePeerCount);
        PredictionShadowTemporalNeighbor selected = Assert.Single(result.SelectedNeighbors,
            peer => peer.SourceFamilyKey == "same-family");
        Assert.Equal(0.4, selected.TemporalFrameDifference);
        Assert.Equal(9000, selected.ActualOutputVideoBitrateKbps);
    }

    [Fact]
    public void RequiresSuccessfulTargetAndHistoricalTemporalSamples()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["a", "b"]);
        PredictionShadowTemporalNeighborComparison targetUnavailable = _comparator.Compare(
            target with { Complexity = Sampling(PredictionShadowSamplingStatus.Unavailable, null) }, true,
            [.. Peer("a", "a", 0.2, 4000, 5000), .. Peer("b", "b", 0.4, 4000, 6000)]);
        PredictionShadowTemporalNeighborComparison peerPartial = Compare(target,
        [
            Peer("a", "a", 0.2, 4000, 5000),
            Peer("b", "b", 0.4, 4000, 6000, sampleStatus: PredictionShadowSamplingStatus.Partial)
        ]);

        Assert.Equal("TargetSamplingNotSuccessful", targetUnavailable.AbstentionReason);
        Assert.Null(targetUnavailable.Ratio.PredictedVideoBitrateKbps);
        Assert.Equal("FewerThanTwoEligibleTemporalPeers", peerPartial.AbstentionReason);
    }

    [Fact]
    public void RequiresTwoSuccessfullySampledIndependentCompletedPeers()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["a", "b"]);
        IReadOnlyList<PredictionShadowJournalEvent> events =
        [
            .. Peer("a", "a", 0.2, 4000, 5000),
            .. Peer("b", "b", 0.4, 4000, 6000, state: "Failed")
        ];

        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, true, events);

        Assert.Equal("FewerThanTwoEligibleTemporalPeers", result.AbstentionReason);
        Assert.Equal(1, result.EligiblePeerCount);
        Assert.Null(result.Ratio.PredictedVideoBitrateKbps);
        Assert.Null(result.Direct.PredictedVideoBitrateKbps);
    }

    [Theory]
    [InlineData("Failed", "Completed", "Passed", "Passed", false)]
    [InlineData("Completed", "Failed", "Passed", "Passed", false)]
    [InlineData("Completed", "Completed", "Failed", "Passed", false)]
    [InlineData("Completed", "Completed", "Passed", "Failed", false)]
    [InlineData("Completed", "Completed", "Passed", "Passed", true)]
    public void RequiresCompletedValidatedFinalizedNonRecoveredOutcome(
        string state, string terminal, string validation, string finalization, bool recovered)
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["a", "b"]);
        IReadOnlyList<PredictionShadowJournalEvent> events =
        [
            .. Peer("a", "a", 0.2, 4000, 5000, state, terminal, validation, finalization, recovered),
            .. Peer("b", "b", 0.4, 4000, 6000)
        ];

        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, true, events);

        Assert.Equal("FewerThanTwoEligibleTemporalPeers", result.AbstentionReason);
    }

    [Fact]
    public void RequiresBaselinePairedPredictionsAndSuccessfulSampling()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["a", "b"]);
        PredictionShadowFrozenObservation abstainingBaseline = target with
        {
            Ratio = new PredictionShadowForecast { Reason = "InsufficientIndependentSources" }
        };

        PredictionShadowTemporalNeighborComparison result = Compare(abstainingBaseline,
        [
            Peer("a", "a", 0.2, 4000, 5000),
            Peer("b", "b", 0.4, 4000, 6000)
        ]);

        Assert.Equal("BaselinePairedPredictionUnavailable", result.AbstentionReason);
    }

    [Fact]
    public void RequiresAvailableTargetTemporalValueAndSharedBaselinePeerSet()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["a", "b"]);
        PredictionShadowTemporalNeighborComparison missingTemporal = _comparator.Compare(
            target with { Complexity = Sampling(PredictionShadowSamplingStatus.Succeeded, null) }, true, []);
        PredictionShadowTemporalNeighborComparison unpairedBaseline = _comparator.Compare(
            target with { RatioAndDirectSharePeers = false }, true, []);

        Assert.Equal("TargetTemporalValueUnavailable", missingTemporal.AbstentionReason);
        Assert.Equal("BaselinePairedPredictionUnavailable", unpairedBaseline.AbstentionReason);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    public void AbstainsOutsideEligiblePeerRange(double targetTemporal)
    {
        PredictionShadowFrozenObservation target = MakeTarget(targetTemporal, ["a", "b"]);
        PredictionShadowTemporalNeighborComparison result = Compare(target,
        [
            Peer("a", "a", 0.2, 4000, 5000),
            Peer("b", "b", 0.4, 4000, 6000)
        ]);

        Assert.Equal("TargetTemporalValueOutsideEligiblePeerRange", result.AbstentionReason);
        Assert.Equal(2, result.EligiblePeerCount);
        Assert.Equal(0.2, result.EligiblePeerTemporalMinimum);
        Assert.Equal(0.4, result.EligiblePeerTemporalMaximum);
        Assert.Empty(result.SelectedNeighbors);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.3)]
    [InlineData(0.4)]
    public void PeerRangeEndpointsAreInclusive(double targetTemporal)
    {
        PredictionShadowTemporalNeighborComparison result = Compare(
            MakeTarget(targetTemporal, ["a", "b"]),
        [
            Peer("a", "a", 0.2, 4000, 5000),
            Peer("b", "b", 0.4, 4000, 6000)
        ]);

        Assert.Equal("", result.AbstentionReason);
    }

    [Fact]
    public void ExcludesFutureOutcomesLaterObservationsAndOutcomesAtCutoff()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["past", "future-outcome", "later-frozen", "at-cutoff"]);
        var events = new List<PredictionShadowJournalEvent>();
        events.AddRange(Peer("past", "past", 0.2, 4000, 5000));
        events.AddRange(Peer("future-outcome", "future-outcome", 0.3, 4000, 6000,
            outcomeUtc: Cutoff.AddMinutes(1)));
        events.AddRange(Peer("later-frozen", "later-frozen", 0.4, 4000, 7000,
            outcomeUtc: Cutoff.AddMinutes(3), evidenceCutoffUtc: Cutoff.AddMinutes(1)));
        events.AddRange(Peer("at-cutoff", "at-cutoff", 0.3, 4000, 8000,
            outcomeUtc: Cutoff));

        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, true, events);

        Assert.Equal("FewerThanTwoEligibleTemporalPeers", result.AbstentionReason);
        Assert.Equal(1, result.EligiblePeerCount);
    }

    [Fact]
    public void ExcludesTargetObservationAndTargetFamilyEvenIfJournalContainsItsOutcome()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["self", "a", "b"]);
        IReadOnlyList<PredictionShadowJournalEvent> events =
        [
            .. Peer(target.ObservationId, "self", 0.3, 4000, 50_000),
            .. Peer("target-family", target.SourceFamilyKey, 0.3, 4000, 50_000),
            .. Peer("a", "a", 0.2, 4000, 5000),
            .. Peer("b", "b", 0.4, 4000, 6000)
        ];

        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, true, events);

        Assert.Equal(2, result.EligiblePeerCount);
        Assert.DoesNotContain(result.SelectedNeighbors, peer => peer.SourceFamilyKey == target.SourceFamilyKey);
        Assert.DoesNotContain(result.SelectedNeighbors, peer => peer.SourceFamilyKey == "self");
    }

    [Fact]
    public void ExcludesSettingsIncompatibleHistory()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["a", "b", "c"]);
        PredictionShadowJournalEvent[] events =
        [
            .. Peer("a", "a", 0.2, 4000, 5000),
            .. Peer("b", "b", 0.3, 4000, 6000),
            .. Peer("c", "c", 0.4, 4000, 7000)
        ];
        PredictionShadowJournalEvent[] mismatched = events.Select(entry =>
            entry.EventType == "Frozen" && entry.Frozen!.SourceFamilyKey == "b"
                ? entry with { Frozen = entry.Frozen with { SettingsSignature = "other-settings" } }
                : entry).ToArray();

        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, true, mismatched);

        Assert.Equal(2, result.EligiblePeerCount);
        Assert.DoesNotContain(result.SelectedNeighbors, peer => peer.SourceFamilyKey == "b");
    }

    [Fact]
    public void CorruptOrUnavailableJournalProducesSpecificAbstention()
    {
        PredictionShadowFrozenObservation target = MakeTarget(0.3, ["a", "b"]);
        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(target, false,
        [
            .. Peer("a", "a", 0.2, 4000, 5000),
            .. Peer("b", "b", 0.4, 4000, 6000)
        ]);

        Assert.Equal("HistoricalJournalUnavailableOrCorrupt", result.AbstentionReason);
    }

    [Fact]
    public void OldSchemaPairsRemainReadableAndCanSupplyPreviouslySampledTemporalEvidence()
    {
        string path = Path.Combine(_root, "old-journal.jsonl");
        var journal = new PredictionShadowObservationJournal(path);
        foreach (PredictionShadowJournalEvent entry in Peer("old-a", "a", 0.2, 4000, 5000, schemaVersion: 1))
        {
            if (entry.Frozen is not null)
                Assert.True(journal.AppendFrozen(entry.Frozen));
            else
                Assert.True(journal.AppendOutcome(entry.ObservationId, entry.Outcome!, entry.RecordedUtc));
        }
        foreach (PredictionShadowJournalEvent entry in Peer("old-b", "b", 0.4, 4000, 6000, schemaVersion: 1))
        {
            if (entry.Frozen is not null)
                Assert.True(journal.AppendFrozen(entry.Frozen));
            else
                Assert.True(journal.AppendOutcome(entry.ObservationId, entry.Outcome!, entry.RecordedUtc));
        }

        Assert.True(journal.TryReadEventsForTemporalComparison(out IReadOnlyList<PredictionShadowJournalEvent> events));
        Assert.All(events, entry => Assert.Equal(1, entry.SchemaVersion));
        Assert.All(events.Where(entry => entry.Frozen is not null), entry => Assert.Null(entry.Frozen!.TemporalNeighbor));
        PredictionShadowTemporalNeighborComparison result = _comparator.Compare(
            MakeTarget(0.3, ["a", "b"]), true, events);
        Assert.Equal("", result.AbstentionReason);
    }

    [Fact]
    public void NewFrozenAndOutcomeSchemaRoundTripWithoutChangingFrozenNeighbors()
    {
        string path = Path.Combine(_root, "new-journal.jsonl");
        var journal = new PredictionShadowObservationJournal(path);
        PredictionShadowFrozenObservation frozen = MakeTarget(0.3, ["a", "b"]) with
        {
            SchemaVersion = 2,
            TemporalNeighbor = new PredictionShadowTemporalNeighborComparison
            {
                EligiblePeerCount = 2,
                MeanNeighborOutputToSourceVideoBitrateRatio = 1.0,
                SelectedNeighbors =
                [
                    new PredictionShadowTemporalNeighbor
                    {
                        SourceFamilyKey = "a", TemporalFrameDifference = 0.2, TemporalDistance = 0.1,
                        SourceVideoBitrateKbps = 4000, ActualOutputVideoBitrateKbps = 5000,
                        ActualOutputToSourceVideoBitrateRatio = 1.25
                    },
                    new PredictionShadowTemporalNeighbor
                    {
                        SourceFamilyKey = "b", TemporalFrameDifference = 0.4, TemporalDistance = 0.1,
                        SourceVideoBitrateKbps = 8000, ActualOutputVideoBitrateKbps = 6000,
                        ActualOutputToSourceVideoBitrateRatio = 0.75
                    }
                ],
                Ratio = new PredictionShadowForecast { PredictedVideoBitrateKbps = 8000 },
                Direct = new PredictionShadowForecast { PredictedVideoBitrateKbps = 5500 }
            }
        };
        Assert.True(journal.AppendFrozen(frozen));
        Assert.True(journal.AppendOutcome(frozen.ObservationId, new PredictionShadowOutcome
        {
            State = "Completed",
            TerminalResult = "Completed",
            ValidationState = "Passed",
            FinalizationState = "Passed",
            ActualOutputVideoBitrateKbps = 7000,
            TemporalNeighborRatioSignedErrorKbps = 1000,
            TemporalNeighborDirectSignedErrorKbps = -1500
        }, Cutoff.AddSeconds(1)));

        PredictionShadowJournalEvent[] roundTrip = journal.ReadEvents().ToArray();

        Assert.Equal(new[] { 2, 2 }, roundTrip.Select(entry => entry.SchemaVersion));
        Assert.Equal(new[] { "a", "b" }, roundTrip[0].Frozen!.TemporalNeighbor!.SelectedNeighbors
            .Select(peer => peer.SourceFamilyKey));
        Assert.Equal(8000, roundTrip[0].Frozen!.TemporalNeighbor!.Ratio.PredictedVideoBitrateKbps);
        Assert.Equal(1.0, roundTrip[0].Frozen!.TemporalNeighbor!.MeanNeighborOutputToSourceVideoBitrateRatio);
        Assert.Equal(1000, roundTrip[1].Outcome!.TemporalNeighborRatioSignedErrorKbps);
        Assert.Equal(-1500, roundTrip[1].Outcome!.TemporalNeighborDirectSignedErrorKbps);
    }

    [Fact]
    public void OutcomeErrorsUseFrozenForecastsAndNeverRecomputeNeighbors()
    {
        string path = Path.Combine(_root, "frozen-outcome.jsonl");
        var journal = new PredictionShadowObservationJournal(path);
        PredictionShadowFrozenObservation frozen = MakeTarget(0.3, ["a", "b"]) with
        {
            SchemaVersion = 2,
            TemporalNeighbor = new PredictionShadowTemporalNeighborComparison
            {
                SelectedNeighbors = [new PredictionShadowTemporalNeighbor { SourceFamilyKey = "frozen-a", TemporalFrameDifference = 0.2 }],
                Ratio = new PredictionShadowForecast { PredictedVideoBitrateKbps = 1500 },
                Direct = new PredictionShadowForecast { PredictedVideoBitrateKbps = 3000 }
            }
        };
        Assert.True(journal.AppendFrozen(frozen));
        var service = new NvencQualityModePredictionShadowService(
            journal, new PredictionShadowComplexitySamplingService("ffmpeg", new NoOpRunner()));

        Assert.True(service.RecordOutcome(frozen.ObservationId, "Completed", "Completed", "Passed", "Passed",
            false, 2000, 1000, Cutoff.AddSeconds(1)));
        PredictionShadowJournalEvent[] events = journal.ReadEvents().ToArray();

        Assert.Equal("frozen-a", events[0].Frozen!.TemporalNeighbor!.SelectedNeighbors[0].SourceFamilyKey);
        Assert.Equal(-500, events[1].Outcome!.TemporalNeighborRatioSignedErrorKbps);
        Assert.Equal(-25, events[1].Outcome!.TemporalNeighborRatioSignedErrorPercent);
        Assert.Equal(25, events[1].Outcome!.TemporalNeighborRatioAbsoluteErrorPercent);
        Assert.Equal(1000, events[1].Outcome!.TemporalNeighborDirectSignedErrorKbps);
        Assert.Equal(50, events[1].Outcome!.TemporalNeighborDirectSignedErrorPercent);
        Assert.Equal(50, events[1].Outcome!.TemporalNeighborDirectAbsoluteErrorPercent);
    }

    [Fact]
    public void InvalidFinalizationDoesNotProduceTemporalErrorsEvenWhenAnActualBitrateExists()
    {
        string path = Path.Combine(_root, "invalid-outcome.jsonl");
        var journal = new PredictionShadowObservationJournal(path);
        PredictionShadowFrozenObservation frozen = MakeTarget(0.3, ["a", "b"]) with
        {
            SchemaVersion = 2,
            TemporalNeighbor = new PredictionShadowTemporalNeighborComparison
            {
                Ratio = new PredictionShadowForecast { PredictedVideoBitrateKbps = 1500 },
                Direct = new PredictionShadowForecast { PredictedVideoBitrateKbps = 3000 }
            }
        };
        Assert.True(journal.AppendFrozen(frozen));
        var service = new NvencQualityModePredictionShadowService(
            journal, new PredictionShadowComplexitySamplingService("ffmpeg", new NoOpRunner()));

        Assert.True(service.RecordOutcome(frozen.ObservationId, "Failed", "EncodeFailed", "Failed", "NotRun",
            false, 2000, 1000, Cutoff.AddSeconds(1)));
        PredictionShadowOutcome outcome = journal.ReadEvents().Last().Outcome!;

        Assert.Equal(5000, outcome.RatioSignedErrorKbps); // Existing baseline outcome semantics remain as-is.
        Assert.Null(outcome.TemporalNeighborRatioSignedErrorKbps);
        Assert.Null(outcome.TemporalNeighborDirectAbsoluteErrorPercent);
    }

    [Fact]
    public void MalformedLineMakesStrictTemporalSnapshotUnavailableWithoutBreakingLegacyRead()
    {
        string path = Path.Combine(_root, "corrupt-journal.jsonl");
        var journal = new PredictionShadowObservationJournal(path);
        foreach (PredictionShadowJournalEvent entry in Peer("valid-a", "a", 0.2, 4000, 5000))
        {
            if (entry.Frozen is not null)
                Assert.True(journal.AppendFrozen(entry.Frozen));
            else
                Assert.True(journal.AppendOutcome(entry.ObservationId, entry.Outcome!, entry.RecordedUtc));
        }
        File.AppendAllText(path, "{not-json}" + Environment.NewLine);

        Assert.Equal(2, journal.ReadEvents().Count); // legacy API still skips a torn line
        Assert.False(journal.TryReadEventsForTemporalComparison(out _));
    }

    private PredictionShadowTemporalNeighborComparison Compare(
        PredictionShadowFrozenObservation target,
        params PredictionShadowJournalEvent[][] peers) =>
        _comparator.Compare(target, true, peers.SelectMany(peer => peer).ToArray());

    private static PredictionShadowFrozenObservation MakeTarget(double temporal, IReadOnlyList<string> admitted) => new()
    {
        SchemaVersion = 2,
        ObservationId = "target-observation",
        SourceFamilyKey = "target-family",
        SourceVideoBitrateKbps = 10_000,
        SettingsSignature = "shared-settings",
        PredictionEvidenceCutoffUtc = Cutoff,
        FrozenUtc = Cutoff,
        Ratio = new PredictionShadowForecast
        {
            PredictedVideoBitrateKbps = 7000,
            Reason = "ComparableHistory",
            Confidence = "Low"
        },
        Direct = new PredictionShadowForecast
        {
            PredictedVideoBitrateKbps = 6500,
            Reason = "ComparableHistory",
            Confidence = "Low"
        },
        AdmittedPeerSourceFamilyKeys = admitted,
        RatioAndDirectSharePeers = true,
        Complexity = Sampling(PredictionShadowSamplingStatus.Succeeded, temporal)
    };

    private static PredictionShadowSamplingObservation Sampling(
        PredictionShadowSamplingStatus status, double? temporal) => new()
    {
        Status = status,
        TemporalFrameDifference = temporal
    };

    private static PredictionShadowJournalEvent[] Peer(
        string observationId,
        string family,
        double temporal,
        double sourceKbps,
        double outputKbps,
        string state = "Completed",
        string terminal = "Completed",
        string validation = "Passed",
        string finalization = "Passed",
        bool recovered = false,
        DateTime? outcomeUtc = null,
        DateTime? evidenceCutoffUtc = null,
        int schemaVersion = 2,
        PredictionShadowSamplingStatus sampleStatus = PredictionShadowSamplingStatus.Succeeded)
    {
        DateTime outcomeTime = outcomeUtc ?? Cutoff.AddMinutes(-1);
        DateTime frozenTime = outcomeTime.AddSeconds(-1);
        DateTime evidenceTime = evidenceCutoffUtc ?? frozenTime.AddSeconds(-1);
        var frozen = new PredictionShadowFrozenObservation
        {
            SchemaVersion = schemaVersion,
            ObservationId = observationId,
            SourceFamilyKey = family,
            SourceVideoBitrateKbps = sourceKbps,
            SettingsSignature = "shared-settings",
            PredictionEvidenceCutoffUtc = evidenceTime,
            FrozenUtc = frozenTime,
            Complexity = Sampling(sampleStatus, temporal)
        };
        var outcome = new PredictionShadowOutcome
        {
            State = state,
            TerminalResult = terminal,
            ValidationState = validation,
            FinalizationState = finalization,
            RecoveredSuccessful = recovered,
            ActualOutputVideoBitrateKbps = outputKbps
        };
        return
        [
            new PredictionShadowJournalEvent
            {
                SchemaVersion = schemaVersion,
                EventId = $"{observationId}:frozen",
                ObservationId = observationId,
                EventType = "Frozen",
                RecordedUtc = frozenTime,
                Frozen = frozen
            },
            new PredictionShadowJournalEvent
            {
                SchemaVersion = schemaVersion,
                EventId = $"{observationId}:outcome",
                ObservationId = observationId,
                EventType = "Outcome",
                RecordedUtc = outcomeTime,
                Outcome = outcome
            }
        ];
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class NoOpRunner : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
    }
}
