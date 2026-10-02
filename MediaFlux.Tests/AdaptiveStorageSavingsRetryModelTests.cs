using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AdaptiveStorageSavingsRetryModelTests
{
    private static readonly RepresentativeSample[] Samples =
    [
        new("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(10)),
        new("Middle", TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(10)),
        new("End", TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10))
    ];

    [Fact]
    public void RetryPlanUsesThePolicyCCandidateAndRetainsOriginalFrozenPlan()
    {
        (EncodingPlan original, PolicyCDecision decision) = FrozenRetryInputs();
        AdaptiveQualitySelectionEvidence selection = original.AdaptiveSelection!;
        AdaptiveCandidateEvidence selected = Assert.Single(selection.Candidates, row => row.Quality == 25);
        AdaptiveCandidateEvidence retryCandidate = Assert.Single(selection.Candidates, row => row.Quality == 27);
        int[] candidateQualities = selection.Candidates.Select(row => row.Quality).ToArray();
        double[] projections = selection.Candidates.Select(row => row.ProjectedUpperBytes).ToArray();

        EncodingPlan retry = EncodingPlanService.FreezeAdaptiveRetryQuality(original, decision, 2, 1);

        Assert.Equal(original.PlanId, retry.PlanId);
        Assert.Equal(25, original.Quality!.EffectiveQuality);
        Assert.Equal(27, retry.Quality!.EffectiveQuality);
        Assert.Equal(27, EncodingPlanService.GetExecutionValues(retry).QualityResolution.EffectiveQuality);
        Assert.Contains(retry.Quality.Reasons, reason => reason.Code == EncodingQualityReasonCode.AdaptiveStorageSavingsRetry && reason.Description.Contains("attempt 2 of 2", StringComparison.Ordinal));
        Assert.Same(selection, retry.AdaptiveSelection);
        Assert.Same(selected, Assert.Single(retry.AdaptiveSelection!.Candidates, row => row.Quality == 25));
        Assert.Same(retryCandidate, decision.Candidate);
        Assert.Equal(candidateQualities, selection.Candidates.Select(row => row.Quality));
        Assert.Equal(projections, selection.Candidates.Select(row => row.ProjectedUpperBytes));
        Assert.Equal(AdaptiveSampleClassification.Borderline, selected.Classification);
        Assert.Equal(AdaptiveSampleClassification.Borderline, retryCandidate.Classification);
    }

    [Fact]
    public void RetryPlanPreservesCodecPresetTargetContainerAndFrozenValidationSettings()
    {
        EncodingDecisionContext context = AdaptiveStorageSavingsTests.Context();
        EncodingPlan basePlan = EncodingPlanService.Create(context);
        AdaptiveQualitySelectionEvidence evidence = Evidence();
        EncodingPlan original = EncodingPlanService.FreezeAdaptiveSelection(basePlan, evidence);
        PolicyCDecision decision = Decision(evidence);
        double? targetMb = EncodingPlanService.GetExecutionValues(original).TargetMb;
        string originalPreset = context.EncoderPreset;

        EncodingPlan retry = EncodingPlanService.FreezeAdaptiveRetryQuality(original, decision, 2, 1);

        Assert.Equal(context.Encoder, EncodingPlanService.GetExecutionValues(retry).Encoder);
        Assert.Equal(originalPreset, EncodingPlanService.GetExecutionValues(retry).EncoderPreset);
        Assert.Equal(targetMb, EncodingPlanService.GetExecutionValues(retry).TargetMb);
        Assert.Equal(original.Source, retry.Source);
        Assert.Equal(original.Video, retry.Video);
        Assert.Equal(original.Quality!.Intent, retry.Quality!.Intent);
        Assert.Equal(original.Quality.Mechanism, retry.Quality.Mechanism);
        Assert.Equal(original.Audio, retry.Audio);
        Assert.Equal(original.Subtitles, retry.Subtitles);
        Assert.Equal(original.Container, retry.Container);
        Assert.Equal(original.Hardware, retry.Hardware);
        Assert.Same(original.Recovery, retry.Recovery);
        Assert.Same(original.RecoveryCapabilities, retry.RecoveryCapabilities);
        Assert.Same(original.ValidationIntent, retry.ValidationIntent);
        Assert.Same(original.FinalizationIntent, retry.FinalizationIntent);
        Assert.Same(original.Validation, retry.Validation);
        Assert.Same(original.SourceAdaptiveShadow, retry.SourceAdaptiveShadow);
        Assert.Same(original.Estimates, retry.Estimates);
        Assert.Same(original.SizePredictionCalibration, retry.SizePredictionCalibration);
        Assert.Same(original.Risks, retry.Risks);
        Assert.Same(original.DecisionReasons, retry.DecisionReasons);
        Assert.Equal(25, original.AdaptiveSelection!.PreferredQuality);
        Assert.Equal(27, original.AdaptiveSelection.WorstAcceptableQuality);
        Assert.Null(retry.SourceAdaptiveShadow);
        Assert.Null(retry.SizePredictionCalibration);
        Assert.Null(retry.Estimates.HistoricalPrediction);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(2, 0)]
    [InlineData(2, 2)]
    public void RetryPlanRejectsAttemptNumbersOutsideTheSingleRetryBudget(int attemptNumber, int completedAttempts)
    {
        (EncodingPlan original, PolicyCDecision decision) = FrozenRetryInputs();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EncodingPlanService.FreezeAdaptiveRetryQuality(original, decision, attemptNumber, completedAttempts));
    }

    [Fact]
    public void RetryPlanRejectsNoRetryDecision()
    {
        (EncodingPlan original, _) = FrozenRetryInputs();
        var declined = new PolicyCDecision(false, null, PolicyCDecisionReason.NoEligibleCandidate);

        Assert.Throws<InvalidOperationException>(() => EncodingPlanService.FreezeAdaptiveRetryQuality(original, declined, 2, 1));
    }

    [Fact]
    public void RetryPlanRejectsMissingCandidate()
    {
        (EncodingPlan original, _) = FrozenRetryInputs();
        var malformed = new PolicyCDecision(true, null, PolicyCDecisionReason.Selected);

        Assert.Throws<InvalidOperationException>(() => EncodingPlanService.FreezeAdaptiveRetryQuality(original, malformed, 2, 1));
    }

    [Fact]
    public void RetryPlanRejectsCandidateNotPresentInOriginalSelection()
    {
        (EncodingPlan original, _) = FrozenRetryInputs();
        var foreignCandidate = Candidate(27, 400_000, 800_000, AdaptiveSampleClassification.Borderline);
        var malformed = new PolicyCDecision(true, foreignCandidate, PolicyCDecisionReason.Selected);

        Assert.Throws<InvalidOperationException>(() => EncodingPlanService.FreezeAdaptiveRetryQuality(original, malformed, 2, 1));
    }

    [Fact]
    public void RetryPlanRejectsInconsistentOrIncompleteCandidateEvidence()
    {
        (EncodingPlan original, _) = FrozenRetryInputs();
        AdaptiveCandidateEvidence candidate = Assert.Single(original.AdaptiveSelection!.Candidates, row => row.Quality == 27);
        AdaptiveCandidateEvidence malformedCandidate = candidate with { Samples = Array.Empty<RepresentativeSampleEvidence>() };
        var malformed = new PolicyCDecision(true, malformedCandidate, PolicyCDecisionReason.Selected);

        Assert.Throws<InvalidOperationException>(() => EncodingPlanService.FreezeAdaptiveRetryQuality(original, malformed, 2, 1));
    }

    [Fact]
    public void RetryPlanAllowsClearlyMeetsCandidateButRejectsClearlyMisses()
    {
        AdaptiveQualitySelectionEvidence meetsEvidence = Evidence() with
        {
            Candidates = Evidence().Candidates.Select(row => row.Quality == 27
                ? row with { Classification = AdaptiveSampleClassification.ClearlyMeets }
                : row).ToArray()
        };
        EncodingPlan meetsPlan = EncodingPlanService.FreezeAdaptiveSelection(
            EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context()), meetsEvidence);
        PolicyCDecision meetsDecision = Decision(meetsEvidence);
        Assert.True(meetsDecision.ShouldRetry);
        Assert.Equal(27, EncodingPlanService.FreezeAdaptiveRetryQuality(meetsPlan, meetsDecision, 2, 1).Quality!.EffectiveQuality);

        AdaptiveQualitySelectionEvidence missEvidence = Evidence() with
        {
            Candidates = Evidence().Candidates.Select(row => row.Quality == 27
                ? row with { Classification = AdaptiveSampleClassification.ClearlyMisses }
                : row).ToArray()
        };
        EncodingPlan missPlan = EncodingPlanService.FreezeAdaptiveSelection(
            EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context()), missEvidence);
        AdaptiveCandidateEvidence clearlyMisses = Assert.Single(missEvidence.Candidates, row => row.Quality == 27);
        var forgedDecision = new PolicyCDecision(true, clearlyMisses, PolicyCDecisionReason.Selected);
        Assert.Throws<InvalidOperationException>(() => EncodingPlanService.FreezeAdaptiveRetryQuality(missPlan, forgedDecision, 2, 1));
    }

    [Fact]
    public void RetryPlanDerivationDoesNotResampleOrMutateProjectionEvidence()
    {
        (EncodingPlan original, PolicyCDecision decision) = FrozenRetryInputs();
        AdaptiveCandidateEvidence[] rows = original.AdaptiveSelection!.Candidates.ToArray();
        RepresentativeSampleEvidence[][] sampleSnapshots = rows.Select(row => row.Samples.ToArray()).ToArray();
        double[][] projections = rows.Select(row => new[] { row.ProjectedLowerBytes, row.ProjectedUpperBytes }).ToArray();

        _ = EncodingPlanService.FreezeAdaptiveRetryQuality(original, decision, 2, 1);

        Assert.Equal(rows, original.AdaptiveSelection.Candidates);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(sampleSnapshots[i], rows[i].Samples);
            Assert.Equal(projections[i], new[] { rows[i].ProjectedLowerBytes, rows[i].ProjectedUpperBytes });
        }
    }

    [Fact]
    public void AttemptEvidenceUsesOneBasedAttemptOneAndTwoAndRejectsAttemptThree()
    {
        AdaptiveStorageSavingsEncodeAttempt first = Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
            EncodingLifecycleStatus.Passed, rejected: true, contributes: false);
        AdaptiveStorageSavingsEncodeAttempt second = Attempt2(AdaptiveStorageSavingsAttemptOutcome.Accepted,
            EncodingLifecycleStatus.Passed, accepted: true, contributes: true);

        Assert.Equal(1, first.AttemptNumber);
        Assert.Equal(AdaptiveStorageSavingsAttemptKind.Initial, first.Kind);
        Assert.Equal(2, second.AttemptNumber);
        Assert.Equal(AdaptiveStorageSavingsAttemptKind.PolicyCRetry, second.Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveStorageSavingsEncodeAttempt.Create(
            3, AdaptiveStorageSavingsAttemptKind.PolicyCRetry, 28, 28, 800_000,
            Guid.NewGuid(), null, false, null, null, null, null, EncodingLifecycleStatus.NotRun,
            "", null, null, AdaptiveStorageSavingsStageDisposition.Retained,
            AdaptiveStorageSavingsAttemptOutcome.PreparationFailed, false, false));
    }

    [Fact]
    public void RetryTraceRepresentsTwoAttemptsInsideOneLogicalTerminalResult()
    {
        (EncodingPlan _, PolicyCDecision decision) = FrozenRetryInputs();
        AdaptiveStorageSavingsEncodeAttempt first = Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
            EncodingLifecycleStatus.Passed, rejected: true, contributes: false, stageDisposition: AdaptiveStorageSavingsStageDisposition.Deleted);
        AdaptiveStorageSavingsEncodeAttempt second = Attempt2(AdaptiveStorageSavingsAttemptOutcome.Accepted,
            EncodingLifecycleStatus.Passed, accepted: true, contributes: true);

        AdaptiveStorageSavingsRetryTrace trace = AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, [first, second], decision, 2, EncodingTerminalResult.Completed);

        Assert.Equal(2, trace.MaximumProductionAttempts);
        Assert.Equal(new[] { 1, 2 }, trace.Attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(EncodingTerminalResult.Completed, trace.LogicalTerminalResult);
        Assert.Equal(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected, trace.Attempts[0].Outcome);
        Assert.Equal(950_000, trace.Attempts[0].ActualCandidateOutputBytes);
        Assert.Equal(AdaptiveStorageSavingsAttemptOutcome.Accepted, trace.Attempts[1].Outcome);
        Assert.Equal(800_000, trace.Attempts[1].ActualCandidateOutputBytes);
        Assert.True(trace.IsMultiAttempt);
        Assert.Equal(25, trace.InitialSelectedQuality);
        Assert.Equal(27, trace.Attempts[1].EffectiveQuality);
        Assert.Equal(PolicyCDecisionReason.Selected, trace.DecisionAfterAttempt1!.ReasonCode);
    }

    [Fact]
    public void RetryTraceKeepsAttemptAndLogicalOutcomesDistinct()
    {
        AdaptiveStorageSavingsEncodeAttempt rejected = Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
            EncodingLifecycleStatus.Passed, rejected: true, contributes: true, stageDisposition: AdaptiveStorageSavingsStageDisposition.Retained);

        AdaptiveStorageSavingsRetryTrace trace = AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, [rejected],
            new PolicyCDecision(false, null, PolicyCDecisionReason.NoEligibleCandidate),
            1, EncodingTerminalResult.StoragePolicyRejected);

        Assert.Equal(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected, trace.Attempts[0].Outcome);
        Assert.Equal(EncodingTerminalResult.StoragePolicyRejected, trace.LogicalTerminalResult);
        Assert.False(trace.IsMultiAttempt);
    }

    [Fact]
    public void CancellationAfterPolicySelectionCanRemainAttemptOneTerminalEvidence()
    {
        (EncodingPlan _, PolicyCDecision decision) = FrozenRetryInputs();
        AdaptiveStorageSavingsEncodeAttempt attempt1 = Attempt1(
            AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
            EncodingLifecycleStatus.Passed,
            rejected: true,
            contributes: true,
            stageDisposition: AdaptiveStorageSavingsStageDisposition.Retained).WithCancellationRequested();

        AdaptiveStorageSavingsRetryTrace trace = AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, [attempt1], decision,
            AdaptiveStorageSavingsRetryLimits.InitialAttemptNumber,
            EncodingTerminalResult.Canceled);

        Assert.True(trace.DecisionAfterAttempt1!.ShouldRetry);
        Assert.True(trace.Attempts[0].CancellationRequested);
        Assert.Equal(AdaptiveStorageSavingsStageDisposition.Retained, trace.Attempts[0].StageDisposition);
        Assert.Equal(EncodingTerminalResult.Canceled, trace.LogicalTerminalResult);
    }

    [Fact]
    public void LogicalCancellationPreservesTheSelectedRetryAndAttemptFacts()
    {
        (EncodingPlan _, PolicyCDecision decision) = FrozenRetryInputs();
        AdaptiveStorageSavingsRetryTrace failedPreparation = AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq,
            [
                Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
                    EncodingLifecycleStatus.Passed, rejected: true, contributes: false,
                    stageDisposition: AdaptiveStorageSavingsStageDisposition.Deleted),
                Attempt2(AdaptiveStorageSavingsAttemptOutcome.PreparationFailed,
                    EncodingLifecycleStatus.NotRun, accepted: false, contributes: true, stageId: null)
            ],
            decision,
            AdaptiveStorageSavingsRetryLimits.RetryAttemptNumber,
            EncodingTerminalResult.EncodeFailed);

        AdaptiveStorageSavingsRetryTrace canceled = failedPreparation.WithLogicalCancellation();

        Assert.True(canceled.DecisionAfterAttempt1!.ShouldRetry);
        Assert.Equal(EncodingTerminalResult.Canceled, canceled.LogicalTerminalResult);
        Assert.True(canceled.Attempts[1].CancellationRequested);
        Assert.Equal(AdaptiveStorageSavingsAttemptOutcome.PreparationFailed, canceled.Attempts[1].Outcome);
    }

    [Fact]
    public void RetryTraceRejectsAttemptThreeAndMismatchedTerminalOrDecisionEvidence()
    {
        (EncodingPlan _, PolicyCDecision decision) = FrozenRetryInputs();
        AdaptiveStorageSavingsEncodeAttempt first = Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
            EncodingLifecycleStatus.Passed, rejected: true, contributes: false, stageDisposition: AdaptiveStorageSavingsStageDisposition.Deleted);
        AdaptiveStorageSavingsEncodeAttempt second = Attempt2(AdaptiveStorageSavingsAttemptOutcome.Accepted,
            EncodingLifecycleStatus.Passed, accepted: true, contributes: true);

        Assert.Throws<ArgumentException>(() => AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, [first, second], decision, 3, EncodingTerminalResult.Completed));
        Assert.Throws<ArgumentException>(() => AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, [first, second], decision, 2, EncodingTerminalResult.StoragePolicyRejected));
        Assert.Throws<ArgumentException>(() => AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, [first, second],
            new PolicyCDecision(false, null, PolicyCDecisionReason.NoEligibleCandidate), 2, EncodingTerminalResult.Completed));
    }

    [Fact]
    public void AttemptAndTraceCollectionsAreImmutableToConsumers()
    {
        AdaptiveStorageSavingsEncodeAttempt attempt = Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
            EncodingLifecycleStatus.Passed, rejected: true, contributes: true, stageDisposition: AdaptiveStorageSavingsStageDisposition.Retained);
        AdaptiveStorageSavingsRetryTrace trace = AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, new[] { attempt },
            new PolicyCDecision(false, null, PolicyCDecisionReason.NoEligibleCandidate),
            1, EncodingTerminalResult.StoragePolicyRejected);

        Assert.All(typeof(AdaptiveStorageSavingsEncodeAttempt).GetProperties(), property => Assert.False(property.CanWrite));
        Assert.All(typeof(AdaptiveStorageSavingsRetryTrace).GetProperties(), property => Assert.False(property.CanWrite));
        Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveStorageSavingsEncodeAttempt>)trace.Attempts).Add(attempt));
    }

    [Fact]
    public void AttemptStagesAreDistinctAndCommandIdentityNeverStoresRawArguments()
    {
        var first = Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected,
            EncodingLifecycleStatus.Passed, rejected: true, contributes: false, stageDisposition: AdaptiveStorageSavingsStageDisposition.Deleted);
        var second = Attempt2(AdaptiveStorageSavingsAttemptOutcome.Accepted,
            EncodingLifecycleStatus.Passed, accepted: true, contributes: true);
        (EncodingPlan _, PolicyCDecision decision) = FrozenRetryInputs();

        Assert.NotEqual(first.StageId, second.StageId);
        Assert.Equal(64, first.CommandSha256!.Length);
        AdaptiveStorageSavingsRetryTrace trace = AdaptiveStorageSavingsRetryTrace.Create(
            25, EncoderQualityMechanism.Cq, [first, second], decision, 2, EncodingTerminalResult.Completed);
        Assert.DoesNotContain(typeof(AdaptiveStorageSavingsEncodeAttempt).GetProperties(), property =>
            property.Name.Contains("Arguments", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("CommandLine", StringComparison.OrdinalIgnoreCase));
        Assert.All(trace.Attempts, attempt => Assert.DoesNotContain("-i ", attempt.CommandSha256 ?? ""));
    }

    [Fact]
    public void StatisticsAndHistoryExposeMultiAttemptLearningIsolationWithoutChangingLegacyDefaults()
    {
        var single = new EncodingStatisticsRecord();
        var historySingle = new JobHistoryRecord();
        Assert.Equal(1, single.ProductionEncodeCount);
        Assert.False(single.IsMultiAttempt);
        Assert.False(historySingle.IsMultiAttempt);

        var retryAttempt = Attempt2(AdaptiveStorageSavingsAttemptOutcome.PreparationFailed,
            EncodingLifecycleStatus.NotRun, accepted: false, contributes: true, stageId: null);
        (EncodingPlan _, PolicyCDecision decision) = FrozenRetryInputs();
        var trace = AdaptiveStorageSavingsRetryTrace.Create(25, EncoderQualityMechanism.Cq,
            [Attempt1(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected, EncodingLifecycleStatus.Passed,
                    rejected: true, contributes: false, stageDisposition: AdaptiveStorageSavingsStageDisposition.Deleted), retryAttempt],
            decision, 2, EncodingTerminalResult.EncodeFailed);
        var multi = new EncodingStatisticsRecord { ProductionEncodeCount = 1, AdaptiveStorageSavingsRetry = trace };
        var historyMulti = new JobHistoryRecord { AdaptiveStorageSavingsRetry = trace };

        Assert.True(multi.IsMultiAttempt);
        Assert.True(historyMulti.IsMultiAttempt);
        Assert.Equal(1, multi.ProductionEncodeCount); // Attempt 2 preparation failed before a production process started.

        EncodingStatisticsRecord? statisticsRoundTrip = JsonSerializer.Deserialize<EncodingStatisticsRecord>(JsonSerializer.Serialize(multi));
        JobHistoryRecord? historyRoundTrip = JsonSerializer.Deserialize<JobHistoryRecord>(JsonSerializer.Serialize(historyMulti));
        Assert.True(statisticsRoundTrip!.IsMultiAttempt);
        Assert.Equal(2, statisticsRoundTrip.AdaptiveStorageSavingsRetry!.Attempts.Count);
        Assert.True(historyRoundTrip!.IsMultiAttempt);
        Assert.Equal(EncodingTerminalResult.EncodeFailed, historyRoundTrip.AdaptiveStorageSavingsRetry!.LogicalTerminalResult);
    }

    [Fact]
    public void LegacyStatisticsAndHistoryJsonRemainReadableWithRetryFieldsAbsent()
    {
        EncodingStatisticsRecord? statistics = JsonSerializer.Deserialize<EncodingStatisticsRecord>(
            "{\"SchemaVersion\":5,\"Id\":\"legacy\",\"Outcome\":0}");
        JobHistoryRecord? history = JsonSerializer.Deserialize<JobHistoryRecord>(
            "{\"Id\":\"legacy\",\"Type\":0,\"Status\":0}");

        Assert.NotNull(statistics);
        Assert.Equal(1, statistics.ProductionEncodeCount);
        Assert.Null(statistics.AdaptiveStorageSavingsRetry);
        Assert.False(statistics.IsMultiAttempt);
        Assert.NotNull(history);
        Assert.Null(history.AdaptiveStorageSavingsRetry);
        Assert.False(history.IsMultiAttempt);
    }

    [Fact]
    public void RetryEvidenceIsNullableAndOmittedForExistingExecutionOutcomeShape()
    {
        var outcome = new EncodingExecutionOutcome(Guid.NewGuid(), Array.Empty<EncodingPreflightOutcome>(), Array.Empty<EncodingRecoveryOutcome>());
        JsonSerializerOptions options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        string json = JsonSerializer.Serialize(outcome, options);

        Assert.Null(outcome.AdaptiveStorageSavingsRetry);
        Assert.DoesNotContain("AdaptiveStorageSavingsRetry", json, StringComparison.Ordinal);
    }

    private static (EncodingPlan Plan, PolicyCDecision Decision) FrozenRetryInputs()
    {
        EncodingPlan initial = EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context());
        AdaptiveQualitySelectionEvidence evidence = Evidence();
        EncodingPlan frozen = EncodingPlanService.FreezeAdaptiveSelection(initial, evidence);
        return (frozen, Decision(evidence));
    }

    private static PolicyCDecision Decision(AdaptiveQualitySelectionEvidence evidence) =>
        AdaptiveStorageSavingsRetryPolicy.Select(new(evidence, true, true, 1, true, false));

    private static AdaptiveQualitySelectionEvidence Evidence() => new(
        AdaptiveSelectionDisposition.Selected,
        "hevc_nvenc", "hevc_nvenc", QualityTarget.Balanced, EncoderQualityMechanism.Cq,
        PreferredQuality: 25, SelectedQuality: 25, WorstAcceptableQuality: 27,
        MaximumIncrease: 2, AbsoluteCap: 29, PreferredAlreadyAboveCap: false,
        Ancillary: null,
        Candidates:
        [
            Candidate(25, 500_000, 950_000, AdaptiveSampleClassification.Borderline),
            Candidate(26, 450_000, 900_000, AdaptiveSampleClassification.ClearlyMisses),
            Candidate(27, 400_000, 850_000, AdaptiveSampleClassification.Borderline)
        ],
        Reason: "Frozen test evidence.");

    private static AdaptiveCandidateEvidence Candidate(
        int quality,
        double lower,
        double upper,
        AdaptiveSampleClassification classification) => new(
            quality,
            Array.AsReadOnly(Samples.Select(sample => new RepresentativeSampleEvidence(sample, 100_000, 10)).ToArray()),
            lower, upper, ContainerAllowanceBytes: 1, classification);

    private static AdaptiveStorageSavingsEncodeAttempt Attempt1(
        AdaptiveStorageSavingsAttemptOutcome outcome,
        EncodingLifecycleStatus validation,
        bool rejected,
        bool contributes,
        AdaptiveStorageSavingsStageDisposition stageDisposition = AdaptiveStorageSavingsStageDisposition.Retained) =>
        AdaptiveStorageSavingsEncodeAttempt.Create(
            1, AdaptiveStorageSavingsAttemptKind.Initial, 25, 25, 950_000,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new string('A', 64),
            true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(30), 30,
            validation, validation == EncodingLifecycleStatus.Passed ? "Full validation passed." : "",
            rejected ? 950_000 : null, rejected ? Evaluate(950_000, StorageSavingsAcceptance.Rejected) : null,
            stageDisposition, outcome, false, contributes);

    private static AdaptiveStorageSavingsEncodeAttempt Attempt2(
        AdaptiveStorageSavingsAttemptOutcome outcome,
        EncodingLifecycleStatus validation,
        bool accepted,
        bool contributes,
        Guid? stageId = null) =>
        AdaptiveStorageSavingsEncodeAttempt.Create(
            2, AdaptiveStorageSavingsAttemptKind.PolicyCRetry, 27, 27, 850_000,
            stageId ?? Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), new string('B', 64),
            stageId is not null, stageId is not null ? 0 : null,
            stageId is not null ? DateTimeOffset.UnixEpoch.AddMinutes(1) : null,
            stageId is not null ? DateTimeOffset.UnixEpoch.AddMinutes(1).AddSeconds(25) : null,
            stageId is not null ? 25 : null,
            validation, accepted ? "Full validation passed." : "",
            accepted ? 800_000 : null, accepted ? Evaluate(800_000, StorageSavingsAcceptance.Accepted) : null,
            accepted ? AdaptiveStorageSavingsStageDisposition.Promoted : stageId is null ? AdaptiveStorageSavingsStageDisposition.NotCreated : AdaptiveStorageSavingsStageDisposition.Retained,
            outcome, false, contributes);

    private static StorageSavingsEvaluation Evaluate(long bytes, StorageSavingsAcceptance acceptance) => new(
        AdaptiveStorageSavingsTests.Contract, acceptance, bytes,
        AdaptiveStorageSavingsTests.Contract.SourceSizeBytes - bytes,
        (AdaptiveStorageSavingsTests.Contract.SourceSizeBytes - bytes) * 100.0 / AdaptiveStorageSavingsTests.Contract.SourceSizeBytes!.Value,
        acceptance == StorageSavingsAcceptance.Accepted ? "Accepted." : "Rejected.");
}
