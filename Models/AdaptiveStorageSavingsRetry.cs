using MediaFlux.Services;
using System.Text.Json.Serialization;

namespace MediaFlux.Models;

public static class AdaptiveStorageSavingsRetryLimits
{
    public const int InitialAttemptNumber = 1;
    public const int RetryAttemptNumber = 2;
    public const int MaximumProductionAttempts = 2;
}

public enum AdaptiveStorageSavingsAttemptKind { Initial, PolicyCRetry }

public enum AdaptiveStorageSavingsAttemptOutcome
{
    Accepted,
    StoragePolicyRejected,
    EncodeFailed,
    ValidationFailed,
    FinalizationFailed,
    Canceled,
    PreparationFailed
}

public enum AdaptiveStorageSavingsStageDisposition
{
    NotCreated,
    Retained,
    Deleted,
    Promoted,
    CleanupFailed
}

/// <summary>
/// Immutable facts for one candidate attempt within a logical encode job. A
/// missing stage ID represents preparation failing before staging was created.
/// Paths and raw FFmpeg arguments are deliberately not retained here.
/// </summary>
public sealed class AdaptiveStorageSavingsEncodeAttempt
{
    [JsonConstructor]
    public AdaptiveStorageSavingsEncodeAttempt(
        int attemptNumber,
        AdaptiveStorageSavingsAttemptKind kind,
        int effectiveQuality,
        int? adaptiveCandidateQuality,
        double? candidateProjectedUpperBytes,
        Guid? stageId,
        string? commandSha256,
        bool processStarted,
        int? processExitCode,
        DateTimeOffset? processStartedUtc,
        DateTimeOffset? processExitedUtc,
        double? productionEncodeSeconds,
        EncodingLifecycleStatus technicalValidationStatus,
        string? technicalValidationSummary,
        long? actualCandidateOutputBytes,
        StorageSavingsEvaluation? phaseOneResult,
        AdaptiveStorageSavingsStageDisposition stageDisposition,
        AdaptiveStorageSavingsAttemptOutcome outcome,
        bool cancellationRequested,
        bool contributesToTerminalResult)
    {
        Validate(attemptNumber, kind, effectiveQuality, adaptiveCandidateQuality, candidateProjectedUpperBytes,
            stageId, commandSha256, processStarted, processExitCode, processStartedUtc, processExitedUtc,
            productionEncodeSeconds, technicalValidationStatus, actualCandidateOutputBytes, phaseOneResult, stageDisposition,
            outcome, cancellationRequested);
        AttemptNumber = attemptNumber;
        Kind = kind;
        EffectiveQuality = effectiveQuality;
        AdaptiveCandidateQuality = adaptiveCandidateQuality;
        CandidateProjectedUpperBytes = candidateProjectedUpperBytes;
        StageId = stageId;
        CommandSha256 = commandSha256?.ToUpperInvariant();
        ProcessStarted = processStarted;
        ProcessExitCode = processExitCode;
        ProcessStartedUtc = processStartedUtc;
        ProcessExitedUtc = processExitedUtc;
        ProductionEncodeSeconds = productionEncodeSeconds;
        TechnicalValidationStatus = technicalValidationStatus;
        TechnicalValidationSummary = technicalValidationSummary ?? "";
        ActualCandidateOutputBytes = actualCandidateOutputBytes;
        PhaseOneResult = phaseOneResult;
        StageDisposition = stageDisposition;
        Outcome = outcome;
        CancellationRequested = cancellationRequested;
        ContributesToTerminalResult = contributesToTerminalResult;
    }

    public int AttemptNumber { get; }
    public AdaptiveStorageSavingsAttemptKind Kind { get; }
    public int EffectiveQuality { get; }
    public int? AdaptiveCandidateQuality { get; }
    public double? CandidateProjectedUpperBytes { get; }
    public Guid? StageId { get; }
    public string? CommandSha256 { get; }
    public bool ProcessStarted { get; }
    public int? ProcessExitCode { get; }
    public DateTimeOffset? ProcessStartedUtc { get; }
    public DateTimeOffset? ProcessExitedUtc { get; }
    public double? ProductionEncodeSeconds { get; }
    public EncodingLifecycleStatus TechnicalValidationStatus { get; }
    public string TechnicalValidationSummary { get; }
    public long? ActualCandidateOutputBytes { get; }
    public StorageSavingsEvaluation? PhaseOneResult { get; }
    public AdaptiveStorageSavingsStageDisposition StageDisposition { get; }
    public AdaptiveStorageSavingsAttemptOutcome Outcome { get; }
    public bool CancellationRequested { get; }
    public bool ContributesToTerminalResult { get; }

    public AdaptiveStorageSavingsEncodeAttempt WithCancellationRequested(bool? contributesToTerminalResult = null) =>
        new(AttemptNumber, Kind, EffectiveQuality, AdaptiveCandidateQuality,
            CandidateProjectedUpperBytes, StageId, CommandSha256, ProcessStarted,
            ProcessExitCode, ProcessStartedUtc, ProcessExitedUtc, ProductionEncodeSeconds,
            TechnicalValidationStatus, TechnicalValidationSummary, ActualCandidateOutputBytes,
            PhaseOneResult, StageDisposition, Outcome, cancellationRequested: true,
            contributesToTerminalResult: contributesToTerminalResult ?? ContributesToTerminalResult);

    public static AdaptiveStorageSavingsEncodeAttempt Create(
        int attemptNumber,
        AdaptiveStorageSavingsAttemptKind kind,
        int effectiveQuality,
        int? adaptiveCandidateQuality,
        double? candidateProjectedUpperBytes,
        Guid? stageId,
        string? commandSha256,
        bool processStarted,
        int? processExitCode,
        DateTimeOffset? processStartedUtc,
        DateTimeOffset? processExitedUtc,
        double? productionEncodeSeconds,
        EncodingLifecycleStatus technicalValidationStatus,
        string? technicalValidationSummary,
        long? actualCandidateOutputBytes,
        StorageSavingsEvaluation? phaseOneResult,
        AdaptiveStorageSavingsStageDisposition stageDisposition,
        AdaptiveStorageSavingsAttemptOutcome outcome,
        bool cancellationRequested,
        bool contributesToTerminalResult)
    {
        return new(attemptNumber, kind, effectiveQuality, adaptiveCandidateQuality,
            candidateProjectedUpperBytes, stageId, commandSha256, processStarted, processExitCode,
            processStartedUtc, processExitedUtc, productionEncodeSeconds, technicalValidationStatus,
            technicalValidationSummary ?? "", actualCandidateOutputBytes, phaseOneResult,
            stageDisposition, outcome, cancellationRequested, contributesToTerminalResult);
    }

    private static void Validate(
        int attemptNumber,
        AdaptiveStorageSavingsAttemptKind kind,
        int effectiveQuality,
        int? adaptiveCandidateQuality,
        double? candidateProjectedUpperBytes,
        Guid? stageId,
        string? commandSha256,
        bool processStarted,
        int? processExitCode,
        DateTimeOffset? processStartedUtc,
        DateTimeOffset? processExitedUtc,
        double? productionEncodeSeconds,
        EncodingLifecycleStatus technicalValidationStatus,
        long? actualCandidateOutputBytes,
        StorageSavingsEvaluation? phaseOneResult,
        AdaptiveStorageSavingsStageDisposition stageDisposition,
        AdaptiveStorageSavingsAttemptOutcome outcome,
        bool cancellationRequested)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(technicalValidationStatus) ||
            !Enum.IsDefined(stageDisposition) || !Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(kind), "Attempt status values must be defined.");
        bool initial = attemptNumber == AdaptiveStorageSavingsRetryLimits.InitialAttemptNumber && kind == AdaptiveStorageSavingsAttemptKind.Initial;
        bool retry = attemptNumber == AdaptiveStorageSavingsRetryLimits.RetryAttemptNumber && kind == AdaptiveStorageSavingsAttemptKind.PolicyCRetry;
        if (!initial && !retry) throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Only attempts 1 and 2 are valid.");
        if (effectiveQuality < 0) throw new ArgumentOutOfRangeException(nameof(effectiveQuality));
        if (adaptiveCandidateQuality is null || adaptiveCandidateQuality != effectiveQuality ||
            candidateProjectedUpperBytes is not double upper || !double.IsFinite(upper) || upper <= 0)
            throw new ArgumentException("Each attempt must identify its selected candidate and valid recorded upper projection.", nameof(adaptiveCandidateQuality));
        if (candidateProjectedUpperBytes is double projection && (!double.IsFinite(projection) || projection <= 0))
            throw new ArgumentOutOfRangeException(nameof(candidateProjectedUpperBytes));
        if (stageId == Guid.Empty) throw new ArgumentException("A stage ID must be non-empty when present.", nameof(stageId));
        if (commandSha256 is not null && !IsSha256(commandSha256))
            throw new ArgumentException("Command identity must be a 64-character SHA-256 hex digest.", nameof(commandSha256));
        if (processStarted && (stageId is null || processStartedUtc is null || commandSha256 is null || productionEncodeSeconds is null))
            throw new ArgumentException("A started production process requires stage, command hash, start time, and monotonic duration evidence.", nameof(processStarted));
        if ((processStartedUtc is DateTimeOffset startedUtc && startedUtc.Offset != TimeSpan.Zero) ||
            (processExitedUtc is DateTimeOffset exitedUtc && exitedUtc.Offset != TimeSpan.Zero))
            throw new ArgumentException("Process timestamps must be UTC.", nameof(processStartedUtc));
        if (!processStarted && (processStartedUtc is not null || processExitedUtc is not null || processExitCode is not null))
            throw new ArgumentException("Process timestamps and exit code require a started process.", nameof(processStarted));
        if (processExitedUtc is DateTimeOffset exited && processStartedUtc is DateTimeOffset started && exited < started)
            throw new ArgumentException("Process exit time cannot precede process start time.", nameof(processExitedUtc));
        if (productionEncodeSeconds is double seconds && (!double.IsFinite(seconds) || seconds < 0))
            throw new ArgumentOutOfRangeException(nameof(productionEncodeSeconds));
        if (actualCandidateOutputBytes < 0) throw new ArgumentOutOfRangeException(nameof(actualCandidateOutputBytes));
        if (phaseOneResult is not null && actualCandidateOutputBytes != phaseOneResult.CandidateOutputBytes)
            throw new ArgumentException("Phase 1 evidence must describe the same actual candidate bytes.", nameof(phaseOneResult));
        if (outcome == AdaptiveStorageSavingsAttemptOutcome.Canceled && !cancellationRequested)
            throw new ArgumentException("A canceled attempt must record cancellation.", nameof(cancellationRequested));
        if (stageId is null && stageDisposition != AdaptiveStorageSavingsStageDisposition.NotCreated)
            throw new ArgumentException("An attempt without a stage must use the NotCreated disposition.", nameof(stageDisposition));
        if (outcome == AdaptiveStorageSavingsAttemptOutcome.Accepted &&
            (technicalValidationStatus != EncodingLifecycleStatus.Passed || phaseOneResult?.Acceptance != StorageSavingsAcceptance.Accepted ||
             actualCandidateOutputBytes is null || stageDisposition != AdaptiveStorageSavingsStageDisposition.Promoted))
            throw new ArgumentException("An accepted attempt requires validation, Phase 1 acceptance, actual bytes, and promotion.", nameof(outcome));
        if (outcome == AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected &&
            (technicalValidationStatus != EncodingLifecycleStatus.Passed || phaseOneResult?.Acceptance != StorageSavingsAcceptance.Rejected || actualCandidateOutputBytes is null))
            throw new ArgumentException("A storage rejection requires validated output and a rejected Phase 1 result.", nameof(outcome));
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}

/// <summary>A compact Policy C decision snapshot; full sampled evidence stays in AdaptiveSelection.</summary>
public sealed record AdaptiveStorageSavingsRetryDecisionEvidence(
    bool ShouldRetry,
    PolicyCDecisionReason ReasonCode,
    int? CandidateQuality,
    double? CandidateProjectedUpperBytes)
{
    public static AdaptiveStorageSavingsRetryDecisionEvidence From(PolicyCDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.ShouldRetry != (decision.Candidate is not null) ||
            decision.ShouldRetry != (decision.ReasonCode == PolicyCDecisionReason.Selected))
            throw new ArgumentException("Policy C decision fields are inconsistent.", nameof(decision));
        return new(decision.ShouldRetry, decision.ReasonCode, decision.Candidate?.Quality,
            decision.Candidate?.ProjectedUpperBytes);
    }
}

/// <summary>Immutable attempt history and terminal result for one logical encode job.</summary>
public sealed class AdaptiveStorageSavingsRetryTrace
{
    [JsonConstructor]
    public AdaptiveStorageSavingsRetryTrace(
        int initialSelectedQuality,
        EncoderQualityMechanism qualityMechanism,
        IReadOnlyList<AdaptiveStorageSavingsEncodeAttempt> attempts,
        AdaptiveStorageSavingsRetryDecisionEvidence? decisionAfterAttempt1,
        int terminalAttemptNumber,
        EncodingTerminalResult logicalTerminalResult)
    {
        Validate(initialSelectedQuality, qualityMechanism, attempts, decisionAfterAttempt1,
            terminalAttemptNumber, logicalTerminalResult);
        InitialSelectedQuality = initialSelectedQuality;
        QualityMechanism = qualityMechanism;
        Attempts = Array.AsReadOnly(attempts.ToArray());
        DecisionAfterAttempt1 = decisionAfterAttempt1;
        TerminalAttemptNumber = terminalAttemptNumber;
        LogicalTerminalResult = logicalTerminalResult;
    }

    public int InitialSelectedQuality { get; }
    public EncoderQualityMechanism QualityMechanism { get; }
    public int MaximumProductionAttempts => AdaptiveStorageSavingsRetryLimits.MaximumProductionAttempts;
    public IReadOnlyList<AdaptiveStorageSavingsEncodeAttempt> Attempts { get; }
    public AdaptiveStorageSavingsRetryDecisionEvidence? DecisionAfterAttempt1 { get; }
    public int TerminalAttemptNumber { get; }
    public EncodingTerminalResult LogicalTerminalResult { get; }
    public bool IsMultiAttempt => Attempts.Count > 1 || DecisionAfterAttempt1?.ShouldRetry == true;

    public static AdaptiveStorageSavingsRetryTrace Create(
        int initialSelectedQuality,
        EncoderQualityMechanism qualityMechanism,
        IEnumerable<AdaptiveStorageSavingsEncodeAttempt> attempts,
        PolicyCDecision? decisionAfterAttempt1,
        int terminalAttemptNumber,
        EncodingTerminalResult logicalTerminalResult)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        AdaptiveStorageSavingsRetryDecisionEvidence? decision = decisionAfterAttempt1 is null
            ? null
            : AdaptiveStorageSavingsRetryDecisionEvidence.From(decisionAfterAttempt1);
        return new(initialSelectedQuality, qualityMechanism, attempts.ToArray(), decision,
            terminalAttemptNumber, logicalTerminalResult);
    }

    public AdaptiveStorageSavingsRetryTrace WithLogicalCancellation()
    {
        AdaptiveStorageSavingsEncodeAttempt[] attempts = Attempts.ToArray();
        attempts[TerminalAttemptNumber - 1] = attempts[TerminalAttemptNumber - 1].WithCancellationRequested();
        return new(InitialSelectedQuality, QualityMechanism, attempts, DecisionAfterAttempt1,
            TerminalAttemptNumber, EncodingTerminalResult.Canceled);
    }

    private static void Validate(
        int initialSelectedQuality,
        EncoderQualityMechanism qualityMechanism,
        IReadOnlyList<AdaptiveStorageSavingsEncodeAttempt>? attempts,
        AdaptiveStorageSavingsRetryDecisionEvidence? decisionAfterAttempt1,
        int terminalAttemptNumber,
        EncodingTerminalResult logicalTerminalResult)
    {
        if (initialSelectedQuality < 0) throw new ArgumentOutOfRangeException(nameof(initialSelectedQuality));
        if (!Enum.IsDefined(qualityMechanism) || qualityMechanism == EncoderQualityMechanism.Unknown)
            throw new ArgumentOutOfRangeException(nameof(qualityMechanism));
        if (!Enum.IsDefined(logicalTerminalResult) || logicalTerminalResult == EncodingTerminalResult.NotRun)
            throw new ArgumentOutOfRangeException(nameof(logicalTerminalResult));
        if (attempts is null || attempts.Count is < 1 or > AdaptiveStorageSavingsRetryLimits.MaximumProductionAttempts)
            throw new ArgumentException("A logical job must contain one or two attempt records.", nameof(attempts));
        for (int i = 0; i < attempts.Count; i++)
            if (attempts[i] is null || attempts[i].AttemptNumber != i + 1)
                throw new ArgumentException("Attempt records must be ordered and numbered 1, then optionally 2.", nameof(attempts));
        if (attempts[0].EffectiveQuality != initialSelectedQuality ||
            attempts[0].AdaptiveCandidateQuality != initialSelectedQuality)
            throw new ArgumentException("Attempt 1 must retain the original selected quality.", nameof(attempts));
        if (decisionAfterAttempt1 is { } policyDecision &&
            (!Enum.IsDefined(policyDecision.ReasonCode) ||
             policyDecision.ShouldRetry != (policyDecision.ReasonCode == PolicyCDecisionReason.Selected) ||
             policyDecision.ShouldRetry != policyDecision.CandidateQuality.HasValue ||
             policyDecision.ShouldRetry != policyDecision.CandidateProjectedUpperBytes.HasValue ||
             policyDecision.CandidateProjectedUpperBytes is double policyUpper && (!double.IsFinite(policyUpper) || policyUpper <= 0) ||
             policyDecision.ShouldRetry && policyDecision.CandidateQuality is int candidateQuality && candidateQuality <= initialSelectedQuality))
            throw new ArgumentException("Policy C decision evidence is inconsistent.", nameof(decisionAfterAttempt1));
        if (terminalAttemptNumber < 1 || terminalAttemptNumber > attempts.Count ||
            terminalAttemptNumber != attempts.Count ||
            attempts.Count(attempt => attempt.ContributesToTerminalResult) != 1 ||
            !attempts[terminalAttemptNumber - 1].ContributesToTerminalResult)
            throw new ArgumentException("Exactly the terminal attempt must contribute to the logical result.", nameof(terminalAttemptNumber));

        if (attempts.Count == 2)
        {
            AdaptiveStorageSavingsEncodeAttempt retry = attempts[1];
            if (attempts[0].Outcome != AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected ||
                decisionAfterAttempt1 is not { ShouldRetry: true } || decisionAfterAttempt1.CandidateQuality != retry.AdaptiveCandidateQuality ||
                retry.EffectiveQuality != decisionAfterAttempt1.CandidateQuality || retry.Kind != AdaptiveStorageSavingsAttemptKind.PolicyCRetry ||
                retry.CandidateProjectedUpperBytes != decisionAfterAttempt1.CandidateProjectedUpperBytes)
                throw new ArgumentException("Attempt 2 must match a successful Policy C decision.", nameof(attempts));
            if (attempts[0].StageId is Guid firstStage && retry.StageId == firstStage)
                throw new ArgumentException("Each attempt must use a distinct staging identity.", nameof(attempts));
        }
        else if (decisionAfterAttempt1?.ShouldRetry == true &&
                 !(attempts.Count == 1 && attempts[0].Outcome == AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected &&
                   ((attempts[0].StageDisposition == AdaptiveStorageSavingsStageDisposition.CleanupFailed &&
                     logicalTerminalResult == EncodingTerminalResult.StoragePolicyRejected) ||
                    (logicalTerminalResult == EncodingTerminalResult.Canceled && attempts[0].CancellationRequested &&
                     attempts[0].StageDisposition is AdaptiveStorageSavingsStageDisposition.Retained or AdaptiveStorageSavingsStageDisposition.CleanupFailed))))
        {
            throw new ArgumentException("A selected retry must be represented by attempt 2 unless cleanup failure or cancellation prevented attempt 2.", nameof(decisionAfterAttempt1));
        }

        EncodingTerminalResult expectedTerminal = attempts[terminalAttemptNumber - 1].Outcome switch
        {
            AdaptiveStorageSavingsAttemptOutcome.Accepted => EncodingTerminalResult.Completed,
            AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected => EncodingTerminalResult.StoragePolicyRejected,
            AdaptiveStorageSavingsAttemptOutcome.ValidationFailed => EncodingTerminalResult.ValidationFailed,
            AdaptiveStorageSavingsAttemptOutcome.FinalizationFailed => EncodingTerminalResult.FinalizationFailed,
            AdaptiveStorageSavingsAttemptOutcome.Canceled => EncodingTerminalResult.Canceled,
            AdaptiveStorageSavingsAttemptOutcome.EncodeFailed or AdaptiveStorageSavingsAttemptOutcome.PreparationFailed => EncodingTerminalResult.EncodeFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(logicalTerminalResult))
        };
        bool canceledAfterAttemptCompletion = logicalTerminalResult == EncodingTerminalResult.Canceled &&
            attempts[terminalAttemptNumber - 1].CancellationRequested;
        if (logicalTerminalResult != expectedTerminal && !canceledAfterAttemptCompletion)
            throw new ArgumentException("Logical terminal result must match the terminal attempt outcome.", nameof(logicalTerminalResult));
    }
}
