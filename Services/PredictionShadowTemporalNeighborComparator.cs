using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Fixed-k, research-only temporal comparator. Historical family admission is
/// supplied by the existing paired predictor and is never broadened here.
/// </summary>
public sealed class PredictionShadowTemporalNeighborComparator
{
    private const int NeighborCount = 2;

    public static PredictionShadowTemporalNeighborComparison CreateAbstention(
        string reason,
        double? targetTemporal = null) => Abstain(reason, targetTemporal);

    public PredictionShadowTemporalNeighborComparison Compare(
        PredictionShadowFrozenObservation target,
        bool journalIsComplete,
        IReadOnlyList<PredictionShadowJournalEvent> journalEvents)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(journalEvents);

        double? targetTemporal = target.Complexity?.TemporalFrameDifference;
        if (target.Complexity?.Status != PredictionShadowSamplingStatus.Succeeded)
            return Abstain("TargetSamplingNotSuccessful", targetTemporal);
        if (targetTemporal is not double temporal || !IsNormalized(temporal))
            return Abstain("TargetTemporalValueUnavailable", targetTemporal);
        if (!target.RatioAndDirectSharePeers || !HasPrediction(target.Ratio) || !HasPrediction(target.Direct))
            return Abstain("BaselinePairedPredictionUnavailable", temporal);
        if (!IsPositiveFinite(target.SourceVideoBitrateKbps))
            return Abstain("TargetSourceVideoBitrateUnavailable", temporal);
        if (target.PredictionEvidenceCutoffUtc == default ||
            target.PredictionEvidenceCutoffUtc.Kind != DateTimeKind.Utc)
            return Abstain("TargetEvidenceCutoffUnavailable", temporal);
        if (!journalIsComplete || !TryIndexJournal(journalEvents, out JournalIndex index))
            return Abstain("HistoricalJournalUnavailableOrCorrupt", temporal);

        var admittedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? family in target.AdmittedPeerSourceFamilyKeys ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(family) &&
                !string.Equals(family, target.SourceFamilyKey, StringComparison.OrdinalIgnoreCase))
                admittedFamilies.Add(family);
        }

        var peerByFamily = new Dictionary<string, EligiblePeer>(StringComparer.OrdinalIgnoreCase);
        foreach ((string observationId, PredictionShadowFrozenObservation frozen) in index.FrozenById)
        {
            if (string.Equals(observationId, target.ObservationId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(frozen.SourceFamilyKey) ||
                !admittedFamilies.Contains(frozen.SourceFamilyKey) ||
                string.Equals(frozen.SourceFamilyKey, target.SourceFamilyKey, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(frozen.SettingsSignature, target.SettingsSignature, StringComparison.Ordinal))
                continue;

            if (!IsSuccessfulTemporalPeer(frozen, index.OutcomeById, target.PredictionEvidenceCutoffUtc,
                    out PredictionShadowJournalEvent? outcomeEvent, out double temporalValue,
                    out double sourceKbps, out double outputKbps))
                continue;

            double ratio = outputKbps / sourceKbps;
            if (!double.IsFinite(ratio) || ratio <= 0)
                continue;
            var peer = new EligiblePeer(frozen.SourceFamilyKey, temporalValue, sourceKbps,
                outputKbps, ratio, outcomeEvent!.RecordedUtc, observationId);
            if (!peerByFamily.TryGetValue(frozen.SourceFamilyKey, out EligiblePeer? current) ||
                IsLater(peer, current))
                peerByFamily[frozen.SourceFamilyKey] = peer;
        }

        EligiblePeer[] eligible = peerByFamily.Values.ToArray();
        if (eligible.Length == 0)
            return Abstain("FewerThanTwoEligibleTemporalPeers", temporal, 0);

        double minimum = eligible.Min(peer => peer.TemporalFrameDifference);
        double maximum = eligible.Max(peer => peer.TemporalFrameDifference);
        if (eligible.Length < NeighborCount)
            return Abstain("FewerThanTwoEligibleTemporalPeers", temporal, eligible.Length, minimum, maximum);
        if (temporal < minimum || temporal > maximum)
            return Abstain("TargetTemporalValueOutsideEligiblePeerRange", temporal, eligible.Length, minimum, maximum);

        PredictionShadowTemporalNeighbor[] selected = eligible
            .Select(peer => new
            {
                Peer = peer,
                Distance = Math.Abs(temporal - peer.TemporalFrameDifference)
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Peer.SourceFamilyKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Peer.SourceFamilyKey, StringComparer.Ordinal)
            .Take(NeighborCount)
            .Select(item => new PredictionShadowTemporalNeighbor
            {
                SourceFamilyKey = item.Peer.SourceFamilyKey,
                TemporalFrameDifference = item.Peer.TemporalFrameDifference,
                TemporalDistance = item.Distance,
                SourceVideoBitrateKbps = item.Peer.SourceVideoBitrateKbps,
                ActualOutputVideoBitrateKbps = item.Peer.ActualOutputVideoBitrateKbps,
                ActualOutputToSourceVideoBitrateRatio = item.Peer.ActualOutputToSourceVideoBitrateRatio
            })
            .ToArray();

        double meanRatio = (selected[0].ActualOutputToSourceVideoBitrateRatio +
            selected[1].ActualOutputToSourceVideoBitrateRatio) / 2d;
        double ratioPrediction = target.SourceVideoBitrateKbps!.Value * meanRatio;
        double directPrediction = (selected[0].ActualOutputVideoBitrateKbps +
            selected[1].ActualOutputVideoBitrateKbps) / 2d;
        if (!IsPositiveFinite(ratioPrediction) || !IsPositiveFinite(directPrediction))
            return Abstain("TemporalNeighborPredictionNotFinite", temporal, eligible.Length, minimum, maximum);

        return new PredictionShadowTemporalNeighborComparison
        {
            ComparatorVersion = PredictionShadowTemporalNeighborVersions.K2,
            K = NeighborCount,
            TargetTemporalFrameDifference = temporal,
            EligiblePeerCount = eligible.Length,
            EligiblePeerTemporalMinimum = minimum,
            EligiblePeerTemporalMaximum = maximum,
            MeanNeighborOutputToSourceVideoBitrateRatio = meanRatio,
            SelectedNeighbors = selected,
            Ratio = new PredictionShadowForecast
            {
                PredictedVideoBitrateKbps = ratioPrediction,
                Confidence = "Low",
                Reason = "TemporalNeighborsSupported",
                IndependentPeerCount = NeighborCount
            },
            Direct = new PredictionShadowForecast
            {
                PredictedVideoBitrateKbps = directPrediction,
                Confidence = "Low",
                Reason = "TemporalNeighborsSupported",
                IndependentPeerCount = NeighborCount
            }
        };
    }

    private static bool TryIndexJournal(
        IReadOnlyList<PredictionShadowJournalEvent> events,
        out JournalIndex index)
    {
        var frozenById = new Dictionary<string, PredictionShadowFrozenObservation>(StringComparer.OrdinalIgnoreCase);
        var frozenEventById = new Dictionary<string, PredictionShadowJournalEvent>(StringComparer.OrdinalIgnoreCase);
        var outcomeById = new Dictionary<string, PredictionShadowJournalEvent>(StringComparer.OrdinalIgnoreCase);
        var eventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PredictionShadowJournalEvent? entry in events)
        {
            if (entry is null || entry.SchemaVersion is not (1 or 2) ||
                string.IsNullOrWhiteSpace(entry.ObservationId) || entry.RecordedUtc == default ||
                entry.RecordedUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(entry.EventId) ||
                !eventIds.Add(entry.EventId))
            {
                index = null!;
                return false;
            }

            if (entry.EventType == "Frozen" && entry.Frozen is { } frozen && entry.Outcome is null &&
                frozen.SchemaVersion == entry.SchemaVersion &&
                string.Equals(entry.EventId, $"{entry.ObservationId}:frozen", StringComparison.Ordinal) &&
                string.Equals(frozen.ObservationId, entry.ObservationId, StringComparison.Ordinal) &&
                frozen.PredictionEvidenceCutoffUtc != default &&
                frozen.PredictionEvidenceCutoffUtc.Kind == DateTimeKind.Utc &&
                frozen.FrozenUtc != default && frozen.FrozenUtc.Kind == DateTimeKind.Utc &&
                entry.RecordedUtc == frozen.FrozenUtc && frozen.FrozenUtc >= frozen.PredictionEvidenceCutoffUtc &&
                !frozenById.ContainsKey(entry.ObservationId))
            {
                frozenById.Add(entry.ObservationId, frozen);
                frozenEventById.Add(entry.ObservationId, entry);
            }
            else if (entry.EventType == "Outcome" && entry.Outcome is not null && entry.Frozen is null &&
                string.Equals(entry.EventId, $"{entry.ObservationId}:outcome", StringComparison.Ordinal) &&
                !outcomeById.ContainsKey(entry.ObservationId))
            {
                outcomeById.Add(entry.ObservationId, entry);
            }
            else
            {
                index = null!;
                return false;
            }
        }

        foreach ((string observationId, PredictionShadowJournalEvent outcomeEvent) in outcomeById)
        {
            if (!frozenEventById.TryGetValue(observationId, out PredictionShadowJournalEvent? frozenEvent) ||
                frozenEvent.SchemaVersion != outcomeEvent.SchemaVersion ||
                outcomeEvent.RecordedUtc < frozenEvent.RecordedUtc)
            {
                index = null!;
                return false;
            }
        }

        index = new JournalIndex(frozenById, outcomeById);
        return true;
    }

    private static bool IsSuccessfulTemporalPeer(
        PredictionShadowFrozenObservation frozen,
        IReadOnlyDictionary<string, PredictionShadowJournalEvent> outcomes,
        DateTime targetCutoffUtc,
        out PredictionShadowJournalEvent? outcomeEvent,
        out double temporal,
        out double sourceKbps,
        out double outputKbps)
    {
        outcomeEvent = null;
        temporal = sourceKbps = outputKbps = 0;
        if (!outcomes.TryGetValue(frozen.ObservationId, out outcomeEvent) ||
            frozen.PredictionEvidenceCutoffUtc >= targetCutoffUtc || frozen.FrozenUtc >= targetCutoffUtc ||
            outcomeEvent.RecordedUtc >= targetCutoffUtc)
            return false;

        PredictionShadowOutcome? outcome = outcomeEvent.Outcome;
        if (outcome is not { RecoveredSuccessful: false } ||
            !string.Equals(outcome.State, "Completed", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(outcome.TerminalResult, "Completed", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(outcome.ValidationState, "Passed", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(outcome.FinalizationState, "Passed", StringComparison.OrdinalIgnoreCase) ||
            !IsPositiveFinite(outcome.ActualOutputVideoBitrateKbps) ||
            !IsPositiveFinite(frozen.SourceVideoBitrateKbps) ||
            frozen.Complexity?.Status != PredictionShadowSamplingStatus.Succeeded ||
            frozen.Complexity.TemporalFrameDifference is not double temporalValue || !IsNormalized(temporalValue))
            return false;

        temporal = temporalValue;
        sourceKbps = frozen.SourceVideoBitrateKbps!.Value;
        outputKbps = outcome.ActualOutputVideoBitrateKbps!.Value;
        return true;
    }

    private static bool IsLater(EligiblePeer candidate, EligiblePeer current) =>
        candidate.OutcomeRecordedUtc > current.OutcomeRecordedUtc ||
        (candidate.OutcomeRecordedUtc == current.OutcomeRecordedUtc &&
            string.Compare(candidate.ObservationId, current.ObservationId, StringComparison.Ordinal) < 0);

    private static bool HasPrediction(PredictionShadowForecast? forecast) =>
        forecast is not null && IsPositiveFinite(forecast.PredictedVideoBitrateKbps);

    private static bool IsPositiveFinite(double? value) =>
        value is > 0 && double.IsFinite(value.Value);

    private static bool IsNormalized(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    private static PredictionShadowTemporalNeighborComparison Abstain(
        string reason,
        double? targetTemporal,
        int eligiblePeerCount = 0,
        double? minimum = null,
        double? maximum = null) => new()
    {
        ComparatorVersion = PredictionShadowTemporalNeighborVersions.K2,
        K = NeighborCount,
        TargetTemporalFrameDifference = targetTemporal,
        EligiblePeerCount = eligiblePeerCount,
        EligiblePeerTemporalMinimum = minimum,
        EligiblePeerTemporalMaximum = maximum,
        Ratio = new PredictionShadowForecast
        {
            Confidence = "Unavailable",
            Reason = reason,
            IndependentPeerCount = eligiblePeerCount
        },
        Direct = new PredictionShadowForecast
        {
            Confidence = "Unavailable",
            Reason = reason,
            IndependentPeerCount = eligiblePeerCount
        },
        AbstentionReason = reason
    };

    private sealed record EligiblePeer(
        string SourceFamilyKey,
        double TemporalFrameDifference,
        double SourceVideoBitrateKbps,
        double ActualOutputVideoBitrateKbps,
        double ActualOutputToSourceVideoBitrateRatio,
        DateTime OutcomeRecordedUtc,
        string ObservationId);

    private sealed record JournalIndex(
        IReadOnlyDictionary<string, PredictionShadowFrozenObservation> FrozenById,
        IReadOnlyDictionary<string, PredictionShadowJournalEvent> OutcomeById);
}
