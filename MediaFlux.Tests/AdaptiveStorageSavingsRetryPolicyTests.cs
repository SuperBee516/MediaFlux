using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AdaptiveStorageSavingsRetryPolicyTests
{
    private static readonly RepresentativeSample[] Windows =
    [
        new("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(10)),
        new("Middle", TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(10)),
        new("End", TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10))
    ];

    [Fact]
    public void SelectsCandidateWithSmallestRecordedUpperProjection()
    {
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 75, 110),
            Candidate(27, 70, 95),
            Candidate(28, 65, 102));

        PolicyCDecision result = Select(selection);

        Assert.True(result.ShouldRetry);
        Assert.Equal(27, result.Candidate!.Quality);
        Assert.Equal(95, result.Candidate.ProjectedUpperBytes);
        Assert.Equal(PolicyCDecisionReason.Selected, result.ReasonCode);
    }

    [Fact]
    public void EqualUpperProjectionUsesLowestQualityNumberRegardlessOfOrder()
    {
        AdaptiveCandidateEvidence[] rows =
        [
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(28, 60, 95),
            Candidate(26, 60, 95),
            Candidate(27, 60, 95)
        ];

        foreach (AdaptiveCandidateEvidence[] order in new[] { rows, rows.Reverse().ToArray() })
        {
            PolicyCDecision result = Select(Selection(order));
            Assert.True(result.ShouldRetry);
            Assert.Equal(26, result.Candidate!.Quality);
        }
    }

    [Fact]
    public void ExcludesHigherAndEqualQualityCandidates()
    {
        // The selected row is equal in quality to the rejected candidate; the
        // only other row has a lower CQ (higher quality) and cannot be retried.
        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(24, 1, 1)));

        AssertNoRetry(result, PolicyCDecisionReason.NoLowerQualitySampledCandidate);
    }

    [Fact]
    public void ExcludesCandidateOutsideOriginalApprovedEnvelope()
    {
        PolicyCDecision result = Select(Selection(new[]
        {
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(28, 1, 1)
        }, worstAcceptable: 27));

        AssertNoRetry(result, PolicyCDecisionReason.OutsideQualityEnvelope);
    }

    [Fact]
    public void DoesNotSelectAnUnsampledCandidate()
    {
        // Candidate 27 exists only in the caller's later set; the frozen
        // selection evidence contains no sample row for it.
        AdaptiveCandidateEvidence laterCandidate = Candidate(27, 1, 1);
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline));

        PolicyCDecision result = Select(selection);

        Assert.Equal(27, laterCandidate.Quality);
        AssertNoRetry(result, PolicyCDecisionReason.NoLowerQualitySampledCandidate);
    }

    [Fact]
    public void ExcludesCandidateWithIncompleteProjectionEvidence()
    {
        AdaptiveCandidateEvidence incomplete = Candidate(26, 70, 95) with
        {
            Samples = Array.Empty<RepresentativeSampleEvidence>()
        };

        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline), incomplete));

        AssertNoRetry(result, PolicyCDecisionReason.IncompleteEvidence);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    public void ExcludesCandidateWithMissingOrInvalidUpperProjection(double upper)
    {
        AdaptiveCandidateEvidence invalid = Candidate(26, 70, 95) with { ProjectedUpperBytes = upper };

        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline), invalid));

        AssertNoRetry(result, PolicyCDecisionReason.IncompleteEvidence);
    }

    [Fact]
    public void ExcludesNonComparableSampleWindows()
    {
        AdaptiveCandidateEvidence nonComparable = Candidate(26, 70, 95) with
        {
            Samples = MakeSamples(Windows.Select(window => window with { Start = window.Start + TimeSpan.FromSeconds(1) }))
        };

        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline), nonComparable));

        AssertNoRetry(result, PolicyCDecisionReason.IncompleteEvidence);
    }

    [Fact]
    public void ExcludesCandidateMarkedClearlyMissesAsOtherwiseIneligible()
    {
        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 4, 5, AdaptiveSampleClassification.ClearlyMisses),
            Candidate(27, 70, 99)));

        Assert.True(result.ShouldRetry);
        Assert.Equal(27, result.Candidate!.Quality);
        Assert.Equal(PolicyCDecisionReason.Selected, result.ReasonCode);
    }

    [Fact]
    public void BorderlineRetryCandidateCanQualify()
    {
        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 70, 99, AdaptiveSampleClassification.Borderline)));

        Assert.True(result.ShouldRetry);
        Assert.Equal(26, result.Candidate!.Quality);
        Assert.Equal(AdaptiveSampleClassification.Borderline, result.Candidate.Classification);
    }

    [Fact]
    public void ClearlyMeetsRetryCandidateCanQualify()
    {
        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 70, 99, AdaptiveSampleClassification.ClearlyMeets)));

        Assert.True(result.ShouldRetry);
        Assert.Equal(26, result.Candidate!.Quality);
        Assert.Equal(AdaptiveSampleClassification.ClearlyMeets, result.Candidate.Classification);
    }

    [Fact]
    public void EveryLowerQualityCandidateMarkedClearlyMissesDeclinesRetry()
    {
        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 70, 99, AdaptiveSampleClassification.ClearlyMisses),
            Candidate(27, 65, 100, AdaptiveSampleClassification.ClearlyMisses)));

        AssertNoRetry(result, PolicyCDecisionReason.CandidateIneligible);
    }

    [Fact]
    public void ClassificationFilteringIsDeterministicRegardlessOfInputOrder()
    {
        AdaptiveCandidateEvidence[] rows =
        [
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 4, 5, AdaptiveSampleClassification.ClearlyMisses),
            Candidate(27, 70, 99, AdaptiveSampleClassification.Borderline),
            Candidate(28, 65, 101, AdaptiveSampleClassification.ClearlyMeets)
        ];

        PolicyCDecision forward = Select(Selection(rows));
        PolicyCDecision reversed = Select(Selection(rows.Reverse().ToArray()));

        Assert.Equal(forward, reversed);
        Assert.True(forward.ShouldRetry);
        Assert.Equal(27, forward.Candidate!.Quality);
    }

    [Fact]
    public void SelectionIsDeterministicRegardlessOfCandidateCollectionOrder()
    {
        AdaptiveCandidateEvidence[] rows =
        [
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(27, 60, 99),
            Candidate(26, 60, 99),
            Candidate(28, 60, 105)
        ];

        PolicyCDecision first = Select(Selection(rows));
        PolicyCDecision reversed = Select(Selection(rows.Reverse().ToArray()));

        Assert.Equal(first, reversed);
        Assert.Equal(26, first.Candidate!.Quality);
    }

    [Fact]
    public void OrdersRecordedDoubleProjectionsWithoutIntegerConversion()
    {
        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 60, 90.75),
            Candidate(27, 60, 90.25)));

        Assert.True(result.ShouldRetry);
        Assert.Equal(27, result.Candidate!.Quality);
        Assert.Equal(90.25, result.Candidate.ProjectedUpperBytes);
    }

    [Fact]
    public void EmptyCandidateCollectionReturnsExplicitNoRetry()
    {
        PolicyCDecision result = Select(Selection());

        AssertNoRetry(result, PolicyCDecisionReason.NonBorderlineSelection);
    }

    [Fact]
    public void SingleQualifyingCandidateIsSelected()
    {
        PolicyCDecision result = Select(Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 70, 99)));

        Assert.True(result.ShouldRetry);
        Assert.Equal(26, result.Candidate!.Quality);
    }

    [Fact]
    public void OutsideEnvelopeCandidateCannotWinWithSmallerProjection()
    {
        PolicyCDecision result = Select(Selection(new[]
        {
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 70, 101),
            Candidate(28, 1, 2)
        }, worstAcceptable: 27));

        Assert.True(result.ShouldRetry);
        Assert.Equal(26, result.Candidate!.Quality);
    }

    [Fact]
    public void MissingCandidateCollectionFailsClosed()
    {
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline)) with
        {
            Candidates = null!
        };

        AssertNoRetry(Select(selection), PolicyCDecisionReason.IncompleteEvidence);
    }

    [Fact]
    public void SelectionInputContainsNoLaterFullFileOutcome()
    {
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 70, 99));
        var laterFullFileOutcome = new { Quality = 26, ActualBytes = 1 };

        PolicyCDecision result = Select(selection);

        Assert.Equal(26, laterFullFileOutcome.Quality);
        Assert.Equal(26, result.Candidate!.Quality);
        Assert.DoesNotContain(typeof(PolicyCEligibility).GetProperties(), property =>
            property.Name.Contains("Outcome", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Actual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectorDoesNotMutateSelectionCandidateOrSampleEvidence()
    {
        AdaptiveCandidateEvidence[] candidates =
        [
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline),
            Candidate(26, 70, 99)
        ];
        RepresentativeSampleEvidence[][] samplesBefore = candidates.Select(candidate => candidate.Samples.ToArray()).ToArray();
        AdaptiveQualitySelectionEvidence selection = Selection(candidates);
        int[] qualitiesBefore = selection.Candidates.Select(candidate => candidate.Quality).ToArray();

        _ = Select(selection);

        Assert.Equal(qualitiesBefore, selection.Candidates.Select(candidate => candidate.Quality));
        for (int i = 0; i < candidates.Length; i++)
            Assert.Equal(samplesBefore[i], candidates[i].Samples);
    }

    [Theory]
    [InlineData(false, false, 1, true, true, PolicyCDecisionReason.Disabled)]
    [InlineData(true, true, 1, true, true, PolicyCDecisionReason.Canceled)]
    [InlineData(true, false, 2, true, true, PolicyCDecisionReason.RetryConsumed)]
    [InlineData(true, false, 1, false, true, PolicyCDecisionReason.WrongTrigger)]
    [InlineData(true, false, 1, true, false, PolicyCDecisionReason.ValidationFailed)]
    public void EligibilitySnapshotDeclinesWhenPreconditionsFail(
        bool enabled,
        bool canceled,
        int attempts,
        bool phaseOneRejected,
        bool technicallyValid,
        PolicyCDecisionReason reason)
    {
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline), Candidate(26, 70, 99));
        PolicyCEligibility input = new(selection, phaseOneRejected, technicallyValid, attempts, enabled, canceled);

        AssertNoRetry(AdaptiveStorageSavingsRetryPolicy.Select(input), reason);
    }

    [Fact]
    public void NonBorderlineOriginalSelectionCannotTriggerRetry()
    {
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 100, AdaptiveSampleClassification.ClearlyMeets), Candidate(26, 70, 90));

        AssertNoRetry(Select(selection), PolicyCDecisionReason.NonBorderlineSelection);
    }

    [Fact]
    public void NonSelectedAdaptiveEvidenceIsNotARetryTrigger()
    {
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline), Candidate(26, 70, 99)) with
        {
            Disposition = AdaptiveSelectionDisposition.NotSuitable
        };

        PolicyCDecision result = AdaptiveStorageSavingsRetryPolicy.Select(new(
            selection, true, true, 1, true, false));

        AssertNoRetry(result, PolicyCDecisionReason.WrongTrigger);
    }

    [Fact]
    public void SelectedQualityOutsideOriginalEnvelopeFailsClosed()
    {
        AdaptiveQualitySelectionEvidence selection = Selection(
            Candidate(25, 80, 120, AdaptiveSampleClassification.Borderline), Candidate(26, 70, 99)) with
        {
            PreferredQuality = 26
        };

        AssertNoRetry(Select(selection), PolicyCDecisionReason.IncompleteEvidence);
    }

    private static PolicyCDecision Select(AdaptiveQualitySelectionEvidence selection) =>
        AdaptiveStorageSavingsRetryPolicy.Select(new(
            selection,
            IsValidatedPhaseOneRejection: true,
            TechnicalValidationPassed: true,
            ProductionEncodeCount: 1,
            RetryEnabled: true,
            CancellationRequested: false));

    private static AdaptiveQualitySelectionEvidence Selection(
        params AdaptiveCandidateEvidence[] candidates) => Selection(candidates, worstAcceptable: 27);

    private static AdaptiveQualitySelectionEvidence Selection(
        IEnumerable<AdaptiveCandidateEvidence> candidates,
        int worstAcceptable) => new(
            AdaptiveSelectionDisposition.Selected,
            "hevc_nvenc",
            "hevc_nvenc",
            QualityTarget.Balanced,
            EncoderQualityMechanism.Cq,
            PreferredQuality: 25,
            SelectedQuality: 25,
            WorstAcceptableQuality: worstAcceptable,
            MaximumIncrease: 2,
            AbsoluteCap: 29,
            PreferredAlreadyAboveCap: false,
            Ancillary: null,
            Candidates: candidates.ToArray(),
            Reason: "Synthetic frozen selection evidence.");

    private static AdaptiveCandidateEvidence Candidate(
        int quality,
        double lower,
        double upper,
        AdaptiveSampleClassification classification = AdaptiveSampleClassification.Borderline) => new(
            quality,
            MakeSamples(Windows),
            lower,
            upper,
            ContainerAllowanceBytes: 1,
            classification);

    private static RepresentativeSampleEvidence[] MakeSamples(IEnumerable<RepresentativeSample> windows) => windows
        .Select((sample, index) => new RepresentativeSampleEvidence(sample, VideoBytes: 100 + index, MeasuredSeconds: 10))
        .ToArray();

    private static void AssertNoRetry(PolicyCDecision result, PolicyCDecisionReason expectedReason)
    {
        Assert.False(result.ShouldRetry);
        Assert.Null(result.Candidate);
        Assert.Equal(expectedReason, result.ReasonCode);
    }
}
