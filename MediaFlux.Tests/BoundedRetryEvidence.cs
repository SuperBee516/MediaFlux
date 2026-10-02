using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux.Tests;

internal static class BoundedRetryEvidence
{
    internal static AdaptiveQualitySelectionEvidence Selection() => new(
        AdaptiveSelectionDisposition.Selected, "hevc_nvenc", "hevc_nvenc", QualityTarget.Balanced,
        EncoderQualityMechanism.Cq, 25, 25, 27, 2, 29, false, null,
        [Candidate(25, 950_000), Candidate(27, 850_000)], "Frozen sample evidence.");

    private static AdaptiveCandidateEvidence Candidate(int quality, long upper) => new(quality,
        Array.AsReadOnly(new[] { new RepresentativeSampleEvidence(
            new RepresentativeSample("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(10)), 100_000, 10) }),
        400_000, upper, 1, AdaptiveSampleClassification.Borderline);

    internal static EncodingPlan Plan()
    {
        var selection = Selection();
        return EncodingPlanService.FreezeAdaptiveRetryQuality(
            EncodingPlanService.FreezeAdaptiveSelection(EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context()), selection),
            AdaptiveStorageSavingsRetryPolicy.Select(new(selection, true, true, 1, true, false)), 2, 1);
    }

    private static PolicyCDecision Decision() => AdaptiveStorageSavingsRetryPolicy.Select(new(Selection(), true, true, 1, true, false));

    internal static AdaptiveStorageSavingsRetryTrace Trace(EncodingTerminalResult result = EncodingTerminalResult.Completed,
        bool preparationFailure = false)
    {
        var first = AdaptiveStorageSavingsEncodeAttempt.Create(1, AdaptiveStorageSavingsAttemptKind.Initial,
            25, 25, 950_000, Guid.NewGuid(), new string('A', 64), true, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(30), 30,
            EncodingLifecycleStatus.Passed, "Validation passed", 950_000, Savings(950_000),
            AdaptiveStorageSavingsStageDisposition.Deleted, AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected, false, false);
        bool completed = result == EncodingTerminalResult.Completed;
        bool rejected = result == EncodingTerminalResult.StoragePolicyRejected;
        bool canceled = result == EncodingTerminalResult.Canceled;
        bool validationFailed = result == EncodingTerminalResult.ValidationFailed;
        var second = AdaptiveStorageSavingsEncodeAttempt.Create(2, AdaptiveStorageSavingsAttemptKind.PolicyCRetry,
            27, 27, 850_000, preparationFailure ? null : Guid.NewGuid(), preparationFailure ? null : new string('B', 64),
            !preparationFailure, preparationFailure || canceled ? null : completed || rejected || validationFailed ? 0 : 1,
            preparationFailure ? null : DateTimeOffset.UnixEpoch.AddSeconds(35),
            preparationFailure ? null : DateTimeOffset.UnixEpoch.AddSeconds(60), preparationFailure ? null : 25,
            completed || rejected ? EncodingLifecycleStatus.Passed : validationFailed ? EncodingLifecycleStatus.Failed : EncodingLifecycleStatus.NotRun,
            "Terminal validation evidence", completed ? 800_000 : rejected ? 950_000 : null,
            completed ? Savings(800_000) : rejected ? Savings(950_000) : null,
            completed ? AdaptiveStorageSavingsStageDisposition.Promoted : preparationFailure ? AdaptiveStorageSavingsStageDisposition.NotCreated : AdaptiveStorageSavingsStageDisposition.Retained,
            completed ? AdaptiveStorageSavingsAttemptOutcome.Accepted : rejected ? AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected :
            canceled ? AdaptiveStorageSavingsAttemptOutcome.Canceled : validationFailed ? AdaptiveStorageSavingsAttemptOutcome.ValidationFailed :
            preparationFailure ? AdaptiveStorageSavingsAttemptOutcome.PreparationFailed : AdaptiveStorageSavingsAttemptOutcome.EncodeFailed,
            canceled, true);
        return AdaptiveStorageSavingsRetryTrace.Create(25, EncoderQualityMechanism.Cq, [first, second], Decision(), 2, result);
    }

    internal static StorageSavingsEvaluation Savings(long bytes) => new(AdaptiveStorageSavingsTests.Contract,
        bytes == 800_000 ? StorageSavingsAcceptance.Accepted : StorageSavingsAcceptance.Rejected,
        bytes, AdaptiveStorageSavingsTests.Contract.SourceSizeBytes - bytes,
        (AdaptiveStorageSavingsTests.Contract.SourceSizeBytes - bytes) * 100.0 / AdaptiveStorageSavingsTests.Contract.SourceSizeBytes!.Value,
        "Actual-byte evaluation.");

    // Count-only, trace-only (including pre-launch failures), and both are independently authoritative.
    internal static EncodingStatisticsRecord Exclude(EncodingStatisticsRecord record, int mode) => record with
    {
        ProductionEncodeCount = mode == 0 ? 2 : mode == 3 ? 0 : 1,
        AdaptiveStorageSavingsRetry = mode is 1 or 2 ? Trace(preparationFailure: mode == 2,
            result: mode == 2 ? EncodingTerminalResult.EncodeFailed : EncodingTerminalResult.Completed) : null
    };
}
