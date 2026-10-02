using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Why Policy C selected a candidate or abstained from a retry.</summary>
public enum PolicyCDecisionReason
{
    Selected,
    Disabled,
    Canceled,
    RetryConsumed,
    WrongTrigger,
    ValidationFailed,
    NonBorderlineSelection,
    IncompleteEvidence,
    NoLowerQualitySampledCandidate,
    OutsideQualityEnvelope,
    CandidateIneligible,
    NoEligibleCandidate
}

/// <summary>
/// Frozen pre-retry facts needed by the pure Policy C selector. This contains no
/// process result, actual output bytes, filesystem identity, or mutable plan.
/// </summary>
public sealed record PolicyCEligibility(
    AdaptiveQualitySelectionEvidence? Selection,
    bool IsValidatedPhaseOneRejection,
    bool TechnicalValidationPassed,
    int ProductionEncodeCount,
    bool RetryEnabled,
    bool CancellationRequested);

/// <summary>A deterministic selection or an explicit no-retry decision.</summary>
public sealed record PolicyCDecision(
    bool ShouldRetry,
    AdaptiveCandidateEvidence? Candidate,
    PolicyCDecisionReason ReasonCode);

/// <summary>
/// Selects one already-sampled lower-quality candidate from the original
/// adaptive evidence. This policy performs no IO and does not change evidence.
/// ClearlyMisses candidates are ineligible; Borderline and ClearlyMeets
/// candidates may qualify when all other frozen evidence checks pass. The
/// original selected candidate must still be Borderline.
/// </summary>
public static class AdaptiveStorageSavingsRetryPolicy
{
    public static PolicyCDecision Select(PolicyCEligibility input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.RetryEnabled)
            return NoRetry(PolicyCDecisionReason.Disabled);
        if (input.CancellationRequested)
            return NoRetry(PolicyCDecisionReason.Canceled);
        if (input.ProductionEncodeCount > 1)
            return NoRetry(PolicyCDecisionReason.RetryConsumed);
        if (input.ProductionEncodeCount != 1 || !input.IsValidatedPhaseOneRejection)
            return NoRetry(PolicyCDecisionReason.WrongTrigger);
        if (!input.TechnicalValidationPassed)
            return NoRetry(PolicyCDecisionReason.ValidationFailed);

        AdaptiveQualitySelectionEvidence? selection = input.Selection;
        if (selection is null)
            return NoRetry(PolicyCDecisionReason.IncompleteEvidence);
        if (selection.Disposition != AdaptiveSelectionDisposition.Selected)
            return NoRetry(PolicyCDecisionReason.WrongTrigger);
        if (selection.SelectedQuality is null || selection.Candidates is null)
            return NoRetry(PolicyCDecisionReason.IncompleteEvidence);

        int selectedQuality = selection.SelectedQuality.Value;
        if (selectedQuality < selection.PreferredQuality || selectedQuality > selection.WorstAcceptableQuality)
            return NoRetry(PolicyCDecisionReason.IncompleteEvidence);
        AdaptiveCandidateEvidence?[] rows = selection.Candidates
            .Select(candidate => (AdaptiveCandidateEvidence?)candidate)
            .ToArray();
        AdaptiveCandidateEvidence?[] selectedRows = rows
            .Where(candidate => candidate?.Quality == selectedQuality)
            .ToArray();
        if (selectedRows.Length != 1 || selectedRows[0] is not { } selected)
            return NoRetry(PolicyCDecisionReason.NonBorderlineSelection);
        if (selected.Classification != AdaptiveSampleClassification.Borderline)
            return NoRetry(PolicyCDecisionReason.NonBorderlineSelection);
        if (!TryGetComparableSampleKeys(selected, expected: null, out HashSet<SampleIdentity>? expectedSamples))
            return NoRetry(PolicyCDecisionReason.IncompleteEvidence);

        Dictionary<int, int> qualityCounts = rows
            .Where(candidate => candidate is not null)
            .GroupBy(candidate => candidate!.Quality)
            .ToDictionary(group => group.Key, group => group.Count());

        var eligible = new List<AdaptiveCandidateEvidence>();
        bool sawLowerQuality = false;
        bool sawOutsideEnvelope = false;
        bool sawIneligible = false;
        bool sawIncomplete = rows.Any(candidate => candidate is null);

        foreach (AdaptiveCandidateEvidence? candidate in rows)
        {
            if (candidate is null || candidate.Quality <= selectedQuality)
                continue;

            sawLowerQuality = true;
            if (qualityCounts[candidate.Quality] != 1)
            {
                sawIncomplete = true;
                continue;
            }

            if (candidate.Quality < selection.PreferredQuality ||
                candidate.Quality > selection.WorstAcceptableQuality)
            {
                sawOutsideEnvelope = true;
                continue;
            }

            if (candidate.Classification == AdaptiveSampleClassification.ClearlyMisses ||
                !Enum.IsDefined(candidate.Classification))
            {
                sawIneligible = true;
                continue;
            }

            if (!TryGetComparableSampleKeys(candidate, expectedSamples, out _))
            {
                sawIncomplete = true;
                continue;
            }

            eligible.Add(candidate);
        }

        if (eligible.Count == 0)
        {
            if (sawIncomplete)
                return NoRetry(PolicyCDecisionReason.IncompleteEvidence);
            if (sawIneligible)
                return NoRetry(PolicyCDecisionReason.CandidateIneligible);
            if (sawOutsideEnvelope)
                return NoRetry(PolicyCDecisionReason.OutsideQualityEnvelope);
            return NoRetry(sawLowerQuality
                ? PolicyCDecisionReason.NoEligibleCandidate
                : PolicyCDecisionReason.NoLowerQualitySampledCandidate);
        }

        AdaptiveCandidateEvidence winner = eligible
            .OrderBy(candidate => candidate.ProjectedUpperBytes)
            .ThenBy(candidate => candidate.Quality)
            .First();
        return new(true, winner, PolicyCDecisionReason.Selected);
    }

    private static bool TryGetComparableSampleKeys(
        AdaptiveCandidateEvidence candidate,
        HashSet<SampleIdentity>? expected,
        out HashSet<SampleIdentity>? keys)
    {
        keys = null;
        if (candidate.Samples is null || candidate.Samples.Count == 0 ||
            !double.IsFinite(candidate.ProjectedLowerBytes) || candidate.ProjectedLowerBytes <= 0 ||
            !double.IsFinite(candidate.ProjectedUpperBytes) || candidate.ProjectedUpperBytes <= 0 ||
            candidate.ProjectedUpperBytes < candidate.ProjectedLowerBytes ||
            !double.IsFinite(candidate.ContainerAllowanceBytes) || candidate.ContainerAllowanceBytes < 0 ||
            !Enum.IsDefined(candidate.Classification))
            return false;

        var actual = new HashSet<SampleIdentity>();
        foreach (RepresentativeSampleEvidence? evidence in candidate.Samples)
        {
            RepresentativeSample? sample = evidence?.Sample;
            if (evidence is null || sample is null || string.IsNullOrWhiteSpace(sample.Label) ||
                sample.Start < TimeSpan.Zero || sample.Duration <= TimeSpan.Zero ||
                evidence.VideoBytes <= 0 || !double.IsFinite(evidence.MeasuredSeconds) || evidence.MeasuredSeconds <= 0)
                return false;

            if (!actual.Add(new SampleIdentity(sample.Label, sample.Start.Ticks, sample.Duration.Ticks)))
                return false;
        }

        if (expected is not null && !actual.SetEquals(expected))
            return false;

        keys = actual;
        return true;
    }

    private static PolicyCDecision NoRetry(PolicyCDecisionReason reason) => new(false, null, reason);

    private readonly record struct SampleIdentity(string Label, long StartTicks, long DurationTicks);
}
