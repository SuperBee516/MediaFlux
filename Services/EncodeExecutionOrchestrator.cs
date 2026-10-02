using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>The production encode boundary, kept injectable for orchestration tests.</summary>
public interface IEncodeRequestExecutor
{
    Task<EncodingService.EncodeResult> EncodeWithResultAsync(EncodingRequest request);
}

/// <summary>A source-bound research assignment failed mandatory pre-encode validation.</summary>
public sealed class EncodeExecutionAssignmentValidationException : InvalidOperationException
{
    public EncodeExecutionAssignmentValidationException(
        PredictionShadowExperimentAssignment? assignment,
        string reason,
        Exception? innerException = null)
        : base(FormatMessage(assignment, reason), innerException)
    {
        Assignment = assignment;
        Reason = reason;
        IsMissingSource = innerException is FileNotFoundException or DirectoryNotFoundException;
    }

    public PredictionShadowExperimentAssignment? Assignment { get; }
    public string Reason { get; }
    public bool IsMissingSource { get; }

    private static string FormatMessage(PredictionShadowExperimentAssignment? assignment, string reason)
    {
        string identity = assignment is null
            ? "assigned research item"
            : $"experiment '{assignment.ExperimentId}', slot {assignment.Slot}, attempt {assignment.Attempt}";
        return $"Execution rejected for {identity}: {reason}";
    }
}

/// <summary>
/// Immutable, UI-independent inputs captured for one logical queue item. Mutable
/// inputs are copied when an execution attempt is created.
/// </summary>
public sealed record EncodeExecutionSnapshot
{
    public required string OperationId { get; init; }
    public required DateTime StatisticsStartUtc { get; init; }
    public required string SourceFilePath { get; init; }
    public required string LogicalSourcePath { get; init; }
    public required EncodingInputSource Input { get; init; }
    public required string OutputFolder { get; init; }
    public required string Suffix { get; init; }
    public required VideoEncoderSelection Encoder { get; init; }
    public required bool UseGpu { get; init; }
    public double? TargetMb { get; init; }
    public StorageSavingsContract StorageSavingsContract { get; init; } = StorageSavingsContract.Disabled;
    public bool AdaptiveStorageSavingsEnabled { get; init; }
    internal bool ExperimentalPolicyCRetryEnabled { get; init; }
    public EncodingSizePredictionCalibration? SizePredictionCalibration { get; init; }
    public EncodingService.ScaleMode ScaleMode { get; init; }
    public required VideoRestorationSettings Restoration { get; init; }
    public string? EncoderPreset { get; init; }
    public int? QualityValue { get; init; }
    public EncodingQualityIntent? QualityIntent { get; init; }
    public bool TenBit { get; init; }
    public int? AudioChannels { get; init; }
    public bool ConcurrentEncoderSessions { get; init; }
    public EncodingService.StreamMapMode MapMode { get; init; } = EncodingService.StreamMapMode.KeepAll;
    public bool CopySubtitles { get; init; } = true;
    public bool CopyDataStreams { get; init; } = true;
    public bool CopyAttachments { get; init; } = true;
    public required OutputContainerSelection OutputContainer { get; init; }
    public bool ContainerCompatibilityConfirmed { get; init; }
    public ContainerCompatibilityPolicy CompatibilityPolicy { get; init; } = ContainerCompatibilityPolicy.Intelligent;
    public PredictionShadowExperimentAssignmentBinding? PredictionShadowExperimentAssignment { get; init; }
    public long? SourceSizeBytes { get; init; }
    public double? MediaDurationSeconds { get; init; }
    public int? SourceHeight { get; init; }
    public int? OutputHeight { get; init; }
    public required string EncoderText { get; init; }
    public required string Codec { get; init; }
    public required string MediaFluxVersion { get; init; }
    public required string StatisticsPath { get; init; }
    public bool DeleteSourceAfterCompression { get; init; }

    internal EncodeExecutionSnapshot CopyForExecution() => this with
    {
        Input = CopyInput(Input),
        Restoration = Restoration.Clone(),
        PredictionShadowExperimentAssignment = PredictionShadowExperimentAssignment is { } binding
            ? binding with
            {
                Assignment = binding.Assignment is { } assignment
                    ? assignment with { }
                    : null!
            }
            : null
    };

    private static EncodingInputSource CopyInput(EncodingInputSource input) => new()
    {
        Kind = input.Kind,
        InputPath = input.InputPath,
        SourcePath = input.SourcePath,
        SourceFiles = Array.AsReadOnly(input.SourceFiles.ToArray()),
        OutputBaseName = input.OutputBaseName,
        KnownDurationSeconds = input.KnownDurationSeconds,
        KnownAudioBitrateKbps = input.KnownAudioBitrateKbps,
        KnownAudioStreamCount = input.KnownAudioStreamCount,
        KnownMappedAncillaryBitrateKbps = input.KnownMappedAncillaryBitrateKbps,
        VideoStreamIndexes = Array.AsReadOnly(input.VideoStreamIndexes.ToArray()),
        AudioStreamIndexes = Array.AsReadOnly(input.AudioStreamIndexes.ToArray()),
        SubtitleStreamIndexes = Array.AsReadOnly(input.SubtitleStreamIndexes.ToArray()),
        AllowSourceDeletion = input.AllowSourceDeletion
    };
}

/// <summary>Presentation callbacks supplied by an adapter; no callback uses WinForms types.</summary>
public sealed record EncodeExecutionCallbacks
{
    public Action<string>? Progress { get; init; }
    public Action<EncodingService.EncodeProgress>? StructuredProgress { get; init; }
    public Action<AiIntermediateProgress>? AiProgress { get; init; }
    public Action<string>? OutputPathChanged { get; init; }
    public Action<string>? StagingPathChanged { get; init; }
    public Action<string>? FinalizationStatusChanged { get; init; }
    public Action? FaststartStarted { get; init; }
    public Action<OutputContainerDecision>? ContainerDecision { get; init; }
    public Action<EncodingPlanSnapshot>? PlanSnapshot { get; init; }
    public Action<EncodingQualityResolution>? QualityResolved { get; init; }
    public Action<EncodingPlanDivergence>? PlanDiverged { get; init; }
    public Action<EncodingExecutionOutcome>? ExecutionOutcome { get; init; }
    public Action<EncodingRecoveryStatusUpdate>? RecoveryStatus { get; init; }
    public Action<string>? FailureDiagnosticReport { get; init; }
    public Action<string>? Diagnostic { get; init; }
}

/// <summary>Per-attempt execution facts associated with exactly one captured snapshot.</summary>
public sealed class EncodeExecutionAttempt
{
    private int _researchCaptureStarted;

    internal EncodeExecutionAttempt(EncodeExecutionSnapshot snapshot) => Snapshot = snapshot.CopyForExecution();

    internal EncodeExecutionSnapshot Snapshot { get; }
    public EncodingPlan? Plan { get; internal set; }
    public EncodingExecutionOutcome? ExecutionOutcome { get; internal set; }
    public EncodingQualityResolution? QualityResolution { get; internal set; }
    public AdaptiveQualitySelectionEvidence? AdaptiveSelection { get; internal set; }
    public EncodingService.EncodeResult? EncodeResult { get; internal set; }
    public EncodeFinalizationResult? FinalizationResult { get; internal set; }
    public SourceDeletionResult? SourceDeletion { get; internal set; }
    public string AttemptedOutputPath { get; internal set; } = "";
    public string StagedOutputPath { get; internal set; } = "";
    public OutputContainerDecision? ContainerDecision { get; internal set; }
    public DateTime? FinalizationStartedUtc { get; internal set; }
    internal PredictionShadowExperimentAssignment? ValidatedExperimentAssignment { get; set; }
    internal string ValidatedSourceFamilyKey { get; set; } = "";
    internal EncodeExecutionAssignmentValidationException? AssignmentValidationFailure { get; set; }
    internal EncodeExecutionCallbacks? Callbacks { get; set; }
    internal bool TryStartResearchCapture() => Interlocked.Exchange(ref _researchCaptureStarted, 1) == 0;
}

public sealed record EncodeExecutionResult(
    EncodingService.EncodeResult EncodedOutput,
    SourceDeletionResult SourceDeletion);

public sealed record EncodeExecutionStatisticsCompletion(
    DateTime EndUtc,
    EncodingStatisticsOutcome Outcome,
    string OutputPath,
    long? OutputSizeBytes,
    double ProcessingSeconds,
    string Notes = "",
    EncodingDiagnosticSummary? DiagnosticSummary = null,
    bool RecoveredSuccessful = false,
    MediaProbeResult? FinalOutputProbe = null);

/// <summary>
/// Owns the non-presentation policy/capture/encode/statistics/Outcome path for
/// one immutable queue-item snapshot. Queue selection and progress rendering
/// remain in the caller.
/// </summary>
public sealed class EncodeExecutionOrchestrator
{
    private readonly IEncodeRequestExecutor _encodingService;
    private readonly NvencQualityModePredictionShadowService _predictionShadowService;
    private readonly EncodingStatisticsService _encodingStatisticsService;
    private readonly Action? _statisticsAppended;
    private readonly Func<string> _hardwareKey;
    private readonly PredictionShadowExperimentFreezeStore _experimentFreezeStore;

    public EncodeExecutionOrchestrator(
        IEncodeRequestExecutor encodingService,
        NvencQualityModePredictionShadowService predictionShadowService,
        EncodingStatisticsService encodingStatisticsService,
        Action? statisticsAppended = null,
        Func<string>? hardwareKey = null,
        PredictionShadowExperimentFreezeStore? experimentFreezeStore = null)
    {
        _encodingService = encodingService ?? throw new ArgumentNullException(nameof(encodingService));
        _predictionShadowService = predictionShadowService ?? throw new ArgumentNullException(nameof(predictionShadowService));
        _encodingStatisticsService = encodingStatisticsService ?? throw new ArgumentNullException(nameof(encodingStatisticsService));
        _statisticsAppended = statisticsAppended;
        _hardwareKey = hardwareKey ?? HardwarePerformanceService.DetectGpuIdentity;
        _experimentFreezeStore = experimentFreezeStore ?? new PredictionShadowExperimentFreezeStore();
    }

    public EncodeExecutionAttempt CreateAttempt(EncodeExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        return new EncodeExecutionAttempt(snapshot);
    }

    /// <summary>Runs the same immutable assignment/freeze checks used at execution start without capturing Frozen.</summary>
    public void ValidateForPreflight(EncodeExecutionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateAssignedExecution(attempt);
    }

    public async Task<EncodeExecutionResult> ExecuteAsync(
        EncodeExecutionAttempt attempt,
        EncodeExecutionCallbacks callbacks,
        EncodeLifecycleDiagnostics? lifecycleDiagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(callbacks);
        ValidateAssignedExecution(attempt);
        attempt.Callbacks = callbacks;
        EncodeExecutionSnapshot snapshot = attempt.Snapshot;

        var request = new EncodingRequest
        {
            Input = snapshot.Input,
            StorageSavingsContract = snapshot.StorageSavingsContract,
            AdaptiveStorageSavingsEnabled = snapshot.AdaptiveStorageSavingsEnabled && snapshot.PredictionShadowExperimentAssignment is null,
            ExperimentalPolicyCRetryEnabled = snapshot.ExperimentalPolicyCRetryEnabled &&
                snapshot.AdaptiveStorageSavingsEnabled && snapshot.PredictionShadowExperimentAssignment is null,
            AdaptiveSelectionCallback = evidence => attempt.AdaptiveSelection = evidence,
            OutputFolder = snapshot.OutputFolder,
            Suffix = snapshot.Suffix,
            Encoder = snapshot.Encoder,
            UseGpu = snapshot.UseGpu,
            TargetMb = snapshot.TargetMb,
            SizePredictionCalibration = snapshot.SizePredictionCalibration,
            ScaleMode = snapshot.ScaleMode,
            Restoration = snapshot.Restoration.Clone(),
            EncoderPreset = snapshot.EncoderPreset,
            QualityValue = snapshot.QualityValue,
            QualityIntent = snapshot.QualityIntent,
            QualityResolutionCallback = resolution =>
            {
                attempt.QualityResolution = resolution;
                callbacks.QualityResolved?.Invoke(resolution);
            },
            TenBit = snapshot.TenBit,
            AudioChannels = snapshot.AudioChannels,
            ConcurrentEncoderSessions = snapshot.ConcurrentEncoderSessions,
            MapMode = snapshot.MapMode,
            CopySubtitles = snapshot.CopySubtitles,
            CopyDataStreams = snapshot.CopyDataStreams,
            CopyAttachments = snapshot.CopyAttachments,
            CancellationToken = cancellationToken,
            OutputPathCallback = path =>
            {
                attempt.AttemptedOutputPath = path;
                callbacks.OutputPathChanged?.Invoke(path);
            },
            StagingPathCallback = path =>
            {
                attempt.StagedOutputPath = path;
                callbacks.StagingPathChanged?.Invoke(path);
            },
            FinalizationStatusCallback = status =>
            {
                attempt.FinalizationStartedUtc ??= DateTime.UtcNow;
                callbacks.FinalizationStatusChanged?.Invoke(status);
            },
            LifecycleDiagnostics = lifecycleDiagnostics,
            FaststartStartedCallback = callbacks.FaststartStarted,
            OutputContainer = snapshot.OutputContainer,
            ContainerCompatibilityConfirmed = snapshot.ContainerCompatibilityConfirmed,
            CompatibilityPolicy = snapshot.CompatibilityPolicy,
            ContainerDecisionCallback = decision =>
            {
                attempt.ContainerDecision = decision;
                callbacks.ContainerDecision?.Invoke(decision);
            },
            EncodingPlanSnapshotCallback = planSnapshot =>
            {
                attempt.Plan = planSnapshot.Plan;
                callbacks.PlanSnapshot?.Invoke(planSnapshot);
            },
            PreEncodeExecutionValidationCallback = planSnapshot =>
                ValidateAssignedPlan(attempt, planSnapshot),
            PreEncodeResearchCallback = (planSnapshot, token) =>
                CaptureResearchAsync(attempt, planSnapshot, callbacks, token),
            EncodingPlanDivergenceCallback = callbacks.PlanDiverged,
            EncodingExecutionOutcomeCallback = outcome =>
            {
                attempt.ExecutionOutcome = outcome;
                callbacks.ExecutionOutcome?.Invoke(outcome);
            },
            RecoveryStatusCallback = callbacks.RecoveryStatus,
            FailureDiagnosticReportCallback = callbacks.FailureDiagnosticReport,
            ProgressCallback = callbacks.Progress,
            StructuredProgressCallback = callbacks.StructuredProgress,
            AiProgressCallback = callbacks.AiProgress
        };

        EncodingService.EncodeResult encoded;
        try
        {
            encoded = await _encodingService.EncodeWithResultAsync(request).ConfigureAwait(false);
        }
        catch (EncodeFinalizationException ex)
        {
            attempt.FinalizationResult = ex.Result;
            throw;
        }
        attempt.EncodeResult = encoded;
        if (!encoded.Success || !encoded.FinalizationSucceeded)
            throw new InvalidOperationException("Encoding did not complete validated output finalization.");
        if (!StorageSavingsContractService.HasAcceptedEvidence(
            snapshot.StorageSavingsContract, encoded.StorageSavings, encoded.FinalOutputSizeBytes))
            throw new InvalidOperationException("Encoding did not establish required storage-policy acceptance.");

        SourceDeletionResult deletion = SourceDeletionService.DeleteAfterFinalization(
            snapshot.SourceFilePath,
            snapshot.Input,
            snapshot.DeleteSourceAfterCompression,
            encoded);
        attempt.SourceDeletion = deletion;
        return new EncodeExecutionResult(encoded, deletion);
    }

    public bool RecordSuccessfulExecution(
        EncodeExecutionAttempt attempt,
        DateTime endUtc,
        long? outputSizeBytes,
        double processingSeconds,
        string notes,
        EncodingDiagnosticSummary? diagnosticSummary,
        bool recoveredSuccessful,
        MediaProbeResult? finalOutputProbe)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        EncodingService.EncodeResult output = attempt.EncodeResult ??
            throw new InvalidOperationException("A completed encode result is required before statistics can be finalized.");
        if (!output.Success || !output.FinalizationSucceeded)
            throw new InvalidOperationException("Only validated and finalized output can be recorded as a successful encode.");
        if (!StorageSavingsContractService.HasAcceptedEvidence(
            attempt.Snapshot.StorageSavingsContract, output.StorageSavings, output.FinalOutputSizeBytes))
            throw new InvalidOperationException("Storage-policy acceptance is required before recording successful statistics.");

        EncodeExecutionSnapshot snapshot = attempt.Snapshot;
        return RecordStatistics(
            snapshot.OperationId,
            snapshot.StatisticsStartUtc,
            endUtc,
            EncodingStatisticsOutcome.Success,
            snapshot.LogicalSourcePath,
            output.OutputPath,
            snapshot.Codec,
            snapshot.EncoderText,
            snapshot.SourceSizeBytes,
            outputSizeBytes,
            snapshot.MediaDurationSeconds,
            processingSeconds,
            notes,
            encoderId: snapshot.Encoder.EncoderId,
            encoderPreset: snapshot.EncoderPreset ?? "",
            sourceResolutionTier: EncodingRuntimeEstimatorService.ResolutionTier(snapshot.SourceHeight),
            outputResolutionTier: EncodingRuntimeEstimatorService.ResolutionTier(snapshot.OutputHeight),
            outputBitDepth: snapshot.TenBit ? 10 : 8,
            scalingApplied: snapshot.SourceHeight is int sourceHeight &&
                snapshot.OutputHeight is int outputHeight && sourceHeight != outputHeight,
            concurrentEncoderSessions: snapshot.ConcurrentEncoderSessions,
            diagnosticSummary: diagnosticSummary,
            recoveredSuccessful: recoveredSuccessful,
            predictionPlan: attempt.Plan,
            executionOutcome: attempt.ExecutionOutcome,
            finalOutputProbe: finalOutputProbe,
            storageSavings: output.StorageSavings);
    }

    public bool RecordFailedExecution(
        EncodeExecutionAttempt attempt,
        DateTime endUtc,
        bool isCanceled,
        EncodeFinalizationFailureKind? finalizationFailureKind,
        string outputPath,
        double processingSeconds,
        string notes,
        EncodingDiagnosticSummary? diagnosticSummary,
        bool retryQueued)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        EncodeExecutionAssignmentValidationException? assignmentFailure = attempt.AssignmentValidationFailure;
        if (assignmentFailure is not null)
            finalizationFailureKind = EncodeFinalizationFailureKind.Validation;
        finalizationFailureKind ??= attempt.FinalizationResult?.FailureKind;
        if (isCanceled)
        {
            finalizationFailureKind = null;
            retryQueued = false;
            if (attempt.ExecutionOutcome is { } canceledOutcome)
                attempt.ExecutionOutcome = canceledOutcome with
                {
                    TerminalResult = EncodingTerminalResult.Canceled,
                    StorageSavings = null,
                    AdaptiveStorageSavingsRetry = canceledOutcome.AdaptiveStorageSavingsRetry?.WithLogicalCancellation()
                };
        }
        else if (attempt.ExecutionOutcome?.TerminalResult == EncodingTerminalResult.StoragePolicyRejected)
            finalizationFailureKind = EncodeFinalizationFailureKind.StoragePolicyRejected;
        if (finalizationFailureKind == EncodeFinalizationFailureKind.StoragePolicyRejected)
            retryQueued = false;
        if (attempt.AdaptiveSelection is not null)
            retryQueued = false;
        if (retryQueued)
        {
            if (assignmentFailure is null)
            {
                RecordPredictionShadowOutcome(
                    attempt.Plan,
                    EncodingStatisticsOutcome.Failed,
                    endUtc,
                    outputSizeBytes: null,
                    executionOutcome: attempt.ExecutionOutcome,
                    finalOutputProbe: null,
                    recoveredSuccessful: false);
                return false;
            }
        }

        EncodeExecutionSnapshot snapshot = attempt.Snapshot;
        EncodingStatisticsOutcome outcome = finalizationFailureKind switch
        {
            _ when isCanceled => EncodingStatisticsOutcome.Cancelled,
            EncodeFinalizationFailureKind.StoragePolicyRejected => EncodingStatisticsOutcome.StoragePolicyRejected,
            EncodeFinalizationFailureKind.Validation => EncodingStatisticsOutcome.ValidationFailed,
            EncodeFinalizationFailureKind.Promotion => EncodingStatisticsOutcome.PromotionFailed,
            EncodeFinalizationFailureKind.FinalVerification => EncodingStatisticsOutcome.FinalVerificationFailed,
            _ => isCanceled ? EncodingStatisticsOutcome.Cancelled : EncodingStatisticsOutcome.Failed
        };
        if (!isCanceled && attempt.AdaptiveSelection?.Disposition == AdaptiveSelectionDisposition.Skipped)
            outcome = EncodingStatisticsOutcome.AdaptiveStorageSavingsSkipped;
        return RecordStatistics(
            snapshot.OperationId,
            snapshot.StatisticsStartUtc,
            endUtc,
            outcome,
            snapshot.LogicalSourcePath,
            outputPath,
            snapshot.Codec,
            snapshot.EncoderText,
            snapshot.SourceSizeBytes,
            outputSizeBytes: null,
            snapshot.MediaDurationSeconds,
            processingSeconds,
            notes,
            encoderId: snapshot.Encoder.EncoderId,
            encoderPreset: snapshot.EncoderPreset ?? "",
            sourceResolutionTier: EncodingRuntimeEstimatorService.ResolutionTier(snapshot.SourceHeight),
            outputResolutionTier: EncodingRuntimeEstimatorService.ResolutionTier(snapshot.OutputHeight),
            outputBitDepth: snapshot.TenBit ? 10 : 8,
            scalingApplied: snapshot.SourceHeight is int sourceHeight &&
                snapshot.OutputHeight is int outputHeight && sourceHeight != outputHeight,
            concurrentEncoderSessions: snapshot.ConcurrentEncoderSessions,
            diagnosticSummary: diagnosticSummary,
            predictionPlan: assignmentFailure is null ? attempt.Plan : null,
            executionOutcome: assignmentFailure is null ? attempt.ExecutionOutcome : null,
            storageSavings: isCanceled ? null : attempt.FinalizationResult?.StorageSavings);
    }

    public void RecordEncodingStatistics(
        string operationId,
        DateTime startUtc,
        DateTime endUtc,
        EncodingStatisticsOutcome outcome,
        string sourcePath,
        string outputPath,
        string codec,
        string encoder,
        long? sourceSizeBytes,
        long? outputSizeBytes,
        double? mediaDurationSeconds,
        double processingSeconds,
        string notes = "",
        string encoderId = "",
        string encoderPreset = "",
        string sourceResolutionTier = "",
        string outputResolutionTier = "",
        int? outputBitDepth = null,
        bool? scalingApplied = null,
        bool? concurrentEncoderSessions = null,
        bool isSampleJob = false,
        EncodingDiagnosticSummary? diagnosticSummary = null,
        bool recoveredSuccessful = false,
        EncodingPlan? predictionPlan = null,
        EncodingExecutionOutcome? executionOutcome = null,
        MediaProbeResult? finalOutputProbe = null,
        StorageSavingsEvaluation? storageSavings = null)
    {
        RecordStatistics(
            operationId, startUtc, endUtc, outcome, sourcePath, outputPath, codec, encoder,
            sourceSizeBytes, outputSizeBytes, mediaDurationSeconds, processingSeconds, notes,
            encoderId, encoderPreset, sourceResolutionTier, outputResolutionTier, outputBitDepth,
            scalingApplied, concurrentEncoderSessions, isSampleJob, diagnosticSummary,
            recoveredSuccessful, predictionPlan, executionOutcome, finalOutputProbe, storageSavings);
    }

    private async Task CaptureResearchAsync(
        EncodeExecutionAttempt attempt,
        EncodingPlanSnapshot planSnapshot,
        EncodeExecutionCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        if (planSnapshot.Plan.AdaptiveSelection is not null)
            return;
        if (!attempt.TryStartResearchCapture())
        {
            callbacks.Diagnostic?.Invoke("[PredictionShadow] Duplicate pre-encode capture callback ignored for this execution attempt.");
            return;
        }

        EncodeExecutionSnapshot snapshot = attempt.Snapshot;
        string settingsSignature = NvencQualityModeVideoBitratePredictionService.EffectiveSettingsSignature(
            snapshot.Encoder.EncoderId,
            snapshot.Codec,
            snapshot.EncoderPreset ?? "",
            snapshot.TenBit ? 10 : 8,
            snapshot.ConcurrentEncoderSessions);
        PredictionShadowExperimentAssignment? assignment = attempt.ValidatedExperimentAssignment;
        try
        {
            if (assignment is not null)
                ValidateAssignedPlan(attempt, planSnapshot);

            PredictionShadowFrozenObservation? captured = await _predictionShadowService.CaptureAsync(
                planSnapshot,
                snapshot.Input.SourcePath,
                settingsSignature,
                new PredictionShadowResearchHistoryReader(snapshot.StatisticsPath).ReadFinalizedStatistics(),
                snapshot.MediaFluxVersion,
                cancellationToken,
                assignment).ConfigureAwait(false);
            if (captured is null && assignment is not null)
                throw new InvalidDataException("The assigned attempt did not produce a Frozen observation; assigned execution cannot continue without experiment membership.");
            if (captured is not null)
            {
                callbacks.Diagnostic?.Invoke($"[PredictionShadow] Observation {captured.ObservationId} frozen before FFmpeg launch.");
                if (assignment is not null && captured.ExperimentAssignment != assignment)
                    throw new InvalidDataException("The Frozen observation did not retain the validated source-bound assignment.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (assignment is not null)
        {
            var failure = new EncodeExecutionAssignmentValidationException(assignment, ex.Message, ex);
            attempt.AssignmentValidationFailure = failure;
            throw failure;
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic?.Invoke($"[PredictionShadow] Pre-encode capture failed; ordinary encoding continues without research capture: {ex.Message}");
        }
    }

    private void ValidateAssignedExecution(EncodeExecutionAttempt attempt)
    {
        EncodeExecutionSnapshot snapshot = attempt.Snapshot;
        PredictionShadowExperimentAssignmentBinding? binding = snapshot.PredictionShadowExperimentAssignment;
        if (binding is null)
            return;

        PredictionShadowExperimentAssignment? assignment = binding.Assignment;
        try
        {
            if (assignment?.IsValid() != true)
                throw new InvalidDataException("The persisted experiment assignment is incomplete or invalid.");
            string familyKey = _experimentFreezeStore.ValidateExecutionAssignment(
                binding, snapshot.Input.SourcePath);
            if (!_predictionShadowService.CanRegisterExperimentAssignment(assignment))
                throw new InvalidDataException("The assigned attempt conflicts with active or archived Frozen experiment history or is not the next valid attempt.");
            attempt.ValidatedExperimentAssignment = assignment;
            attempt.ValidatedSourceFamilyKey = familyKey;
        }
        catch (Exception ex) when (ex is not EncodeExecutionAssignmentValidationException and not OperationCanceledException)
        {
            var failure = new EncodeExecutionAssignmentValidationException(assignment, ex.Message, ex);
            attempt.AssignmentValidationFailure = failure;
            throw failure;
        }
    }

    private void ValidateAssignedPlan(EncodeExecutionAttempt attempt, EncodingPlanSnapshot planSnapshot)
    {
        PredictionShadowExperimentAssignment? assignment = attempt.ValidatedExperimentAssignment;
        if (assignment is null)
            return;

        EncodeExecutionSnapshot snapshot = attempt.Snapshot;
        try
        {
            string? observedFamilyKey = NvencQualityModeVideoBitratePredictionService.GetSourceFamilyKey(
                planSnapshot.Plan.SourceAdaptiveShadow,
                planSnapshot.Plan.Source?.DurationSeconds,
                snapshot.Input.SourcePath);
            string freezeFamilyKey = _experimentFreezeStore.ValidateExecutionAssignment(
                snapshot.PredictionShadowExperimentAssignment,
                snapshot.Input.SourcePath,
                observedFamilyKey);
            if (!string.Equals(freezeFamilyKey, attempt.ValidatedSourceFamilyKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The frozen source-family identity changed between snapshot validation and plan creation.");
            if (!_predictionShadowService.CanRegisterExperimentAssignment(assignment))
                throw new InvalidDataException("The assigned attempt ceased to be the next valid Frozen attempt before capture.");
        }
        catch (Exception ex) when (ex is not EncodeExecutionAssignmentValidationException and not OperationCanceledException)
        {
            var failure = new EncodeExecutionAssignmentValidationException(assignment, ex.Message, ex);
            attempt.AssignmentValidationFailure = failure;
            throw failure;
        }
    }

    private bool RecordStatistics(
        string operationId,
        DateTime startUtc,
        DateTime endUtc,
        EncodingStatisticsOutcome outcome,
        string sourcePath,
        string outputPath,
        string codec,
        string encoder,
        long? sourceSizeBytes,
        long? outputSizeBytes,
        double? mediaDurationSeconds,
        double processingSeconds,
        string notes = "",
        string encoderId = "",
        string encoderPreset = "",
        string sourceResolutionTier = "",
        string outputResolutionTier = "",
        int? outputBitDepth = null,
        bool? scalingApplied = null,
        bool? concurrentEncoderSessions = null,
        bool isSampleJob = false,
        EncodingDiagnosticSummary? diagnosticSummary = null,
        bool recoveredSuccessful = false,
        EncodingPlan? predictionPlan = null,
        EncodingExecutionOutcome? executionOutcome = null,
        MediaProbeResult? finalOutputProbe = null,
        StorageSavingsEvaluation? storageSavings = null)
    {
        storageSavings ??= executionOutcome?.StorageSavings;
        AdaptiveQualitySelectionEvidence? adaptive = predictionPlan?.AdaptiveSelection;
        if (adaptive?.Disposition == AdaptiveSelectionDisposition.Skipped)
        {
            outcome = EncodingStatisticsOutcome.AdaptiveStorageSavingsSkipped;
            outputSizeBytes = null;
            finalOutputProbe = null;
            recoveredSuccessful = false;
        }
        if (adaptive is not null)
            processingSeconds = outcome == EncodingStatisticsOutcome.AdaptiveStorageSavingsSkipped ? 0
                : Math.Max(0, processingSeconds - adaptive.SamplingSeconds);
        if (outcome != EncodingStatisticsOutcome.Cancelled &&
            storageSavings?.Acceptance == StorageSavingsAcceptance.Rejected)
        {
            outcome = EncodingStatisticsOutcome.StoragePolicyRejected;
            outputSizeBytes = null;
            finalOutputProbe = null;
            recoveredSuccessful = false;
        }
        bool added = false;
        try
        {
            var statisticsRecord = new EncodingStatisticsRecord
            {
                Id = operationId,
                ProductionEncodeCount = executionOutcome?.ProductionEncodeCount ?? 1,
                AdaptiveStorageSavingsRetry = executionOutcome?.AdaptiveStorageSavingsRetry,
                StartUtc = startUtc,
                EndUtc = endUtc,
                Outcome = outcome,
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Codec = codec,
                Encoder = encoder,
                SourceSizeBytes = sourceSizeBytes,
                OutputSizeBytes = outputSizeBytes,
                MediaDurationSeconds = mediaDurationSeconds,
                ProcessingSeconds = processingSeconds,
                EncoderId = encoderId,
                EncoderPreset = encoderPreset,
                SourceResolutionTier = sourceResolutionTier,
                OutputResolutionTier = outputResolutionTier,
                OutputBitDepth = outputBitDepth,
                ScalingApplied = scalingApplied,
                ConcurrentEncoderSessions = concurrentEncoderSessions,
                IsSampleJob = isSampleJob,
                DiagnosticSummary = diagnosticSummary,
                HardwareKey = encoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase)
                    ? _hardwareKey()
                    : "cpu",
                RecoveredSuccessful = recoveredSuccessful,
                Notes = notes,
                PredictionPlanId = predictionPlan?.PlanId.ToString("N") ?? "",
                PredictionSourceCodec = predictionPlan?.Source?.Codec ?? "",
                PredictionTargetCodec = predictionPlan?.Video?.Codec ?? "",
                PredictionSameCodec = predictionPlan?.Source != null && predictionPlan.Video != null
                    ? CodecFamily(predictionPlan.Source.Codec) == CodecFamily(predictionPlan.Video.Codec)
                    : null,
                PredictedOutputSizeBytes = PredictionBytes(predictionPlan),
                BasePredictedOutputSizeBytes = PredictionBaseBytes(predictionPlan),
                CalibrationApplied = predictionPlan?.SizePredictionCalibration?.Applied,
                CalibrationCorrectionPercent = predictionPlan?.SizePredictionCalibration?.EffectiveCorrectionPercent,
                CalibrationMedianSignedErrorPercent = predictionPlan?.SizePredictionCalibration?.MedianSignedErrorPercent,
                CalibrationConfidence = predictionPlan?.SizePredictionCalibration?.Confidence.ToString() ?? "",
                CalibrationSampleCount = predictionPlan?.SizePredictionCalibration?.SampleCount,
                CalibrationCohortKey = predictionPlan?.SizePredictionCalibration?.CohortKey ?? "",
                CalibrationReason = predictionPlan?.SizePredictionCalibration?.Reason ?? "",
                HypotheticalCalibratedOutputSizeBytes = PredictionHypotheticalBytes(predictionPlan),
                CalibrationEvidenceCutoffUtc = predictionPlan?.SizePredictionCalibration?.EvidenceCutoffUtc,
                CalibrationDecisionUtc = predictionPlan?.SizePredictionCalibration?.DecisionUtc,
                CalibrationDecision = predictionPlan?.SizePredictionCalibration?.Decision.ToString() ?? "",
                CalibrationEffectivenessState = predictionPlan?.SizePredictionCalibration?.EffectivenessState.ToString() ?? "",
                CalibrationEvaluationCount = predictionPlan?.SizePredictionCalibration?.EvaluationSampleCount,
                CalibrationMedianImprovementPercent = predictionPlan?.SizePredictionCalibration?.MedianCalibrationImprovementPercent,
                CalibrationImprovedRatePercent = predictionPlan?.SizePredictionCalibration?.ImprovedRatePercent,
                CalibrationWorsenedRatePercent = predictionPlan?.SizePredictionCalibration?.WorsenedRatePercent,
                CalibrationEffectivenessSinceUtc = predictionPlan?.SizePredictionCalibration?.EffectivenessSinceUtc,
                CalibrationPolicyId = predictionPlan?.SizePredictionCalibration?.PolicyId ?? "",
                CalibrationLearningStrength = predictionPlan?.SizePredictionCalibration?.LearningStrength,
                CalibrationRawHistoricalCorrectionPercent = predictionPlan?.SizePredictionCalibration?.RawHistoricalCorrectionPercent,
                CalibrationAppliedCorrectionPercent = predictionPlan?.SizePredictionCalibration?.AppliedCorrectionPercent,
                EstimateModelId = predictionPlan?.SizePredictionCalibration?.EstimateModelId ?? "",
                EstimateStatus = predictionPlan?.SizePredictionCalibration?.EstimateStatus ?? "",
                EstimateIndependentFamilyCount = predictionPlan?.SizePredictionCalibration?.EstimateIndependentFamilyCount,
                EstimateHeldOutCount = predictionPlan?.SizePredictionCalibration?.EstimateHeldOutCount,
                EstimateHeldOutEligibleCount = predictionPlan?.SizePredictionCalibration?.EstimateHeldOutEligibleCount,
                EstimateMedianAbsoluteErrorPercent = predictionPlan?.SizePredictionCalibration?.EstimateMedianAbsoluteErrorPercent,
                EstimatePredictedVideoBitrateKbps = predictionPlan?.SizePredictionCalibration?.EstimatePredictedVideoBitrateKbps,
                PredictedCompressionRatio = predictionPlan?.Estimates.HistoricalPrediction?.PredictedCompressionRatio ??
                    predictionPlan?.Estimates.EstimatedCompressionRatio,
                PredictedProcessingSeconds = predictionPlan?.Estimates.HistoricalPrediction?.PredictedDuration?.TotalSeconds,
                PredictionConfidence = predictionPlan?.Estimates.HistoricalPrediction?.Confidence.ToString() ?? "",
                PredictionRecommendation = predictionPlan?.Recommendation?.Recommendation.ToString() ?? "",
                PredictionQuality = predictionPlan?.Quality?.EffectiveQuality?.ToString() ?? "",
                PredictionAssessment = predictionPlan?.Quality?.Assessment.ToString() ?? "",
                TerminalResult = outcome == EncodingStatisticsOutcome.StoragePolicyRejected
                    ? EncodingTerminalResult.StoragePolicyRejected.ToString() : executionOutcome?.TerminalResult.ToString() ?? "",
                StorageSavings = storageSavings ?? executionOutcome?.StorageSavings,
                AdaptiveSelection = adaptive,
                SourceAdaptiveShadow = executionOutcome?.ProductionEncodeCount is not > 1 &&
                    executionOutcome?.AdaptiveStorageSavingsRetry is null && predictionPlan?.SourceAdaptiveShadow is { } shadow
                    ? SourceAdaptiveShadowOutcome.ForTerminalOutcome(
                        shadow,
                        outcome == EncodingStatisticsOutcome.Success,
                        finalOutputProbe,
                        outputSizeBytes)
                    : null,
                QualityModeSettingsSignature = predictionPlan?.SourceAdaptiveShadow?.IsPrimaryCalibrationCandidate == true
                    ? NvencQualityModeVideoBitratePredictionService.EffectiveSettingsSignature(
                        encoderId, codec, encoderPreset, outputBitDepth, concurrentEncoderSessions)
                    : ""
            };
            added = _encodingStatisticsService.AppendFinalized(statisticsRecord);
            if (statisticsRecord.SourceAdaptiveShadow is { } shadowOutcome)
                Debug.WriteLine(shadowOutcome.Describe());
            if (added)
                _statisticsAppended?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Statistics append failed: {ex}");
        }

        RecordPredictionShadowOutcome(
            predictionPlan, outcome, endUtc, outputSizeBytes, executionOutcome,
            finalOutputProbe, recoveredSuccessful);
        return added;
    }

    private void RecordPredictionShadowOutcome(
        EncodingPlan? predictionPlan,
        EncodingStatisticsOutcome outcome,
        DateTime recordedUtc,
        long? outputSizeBytes,
        EncodingExecutionOutcome? executionOutcome,
        MediaProbeResult? finalOutputProbe,
        bool recoveredSuccessful)
    {
        if (predictionPlan == null || predictionPlan.AdaptiveSelection is not null ||
            executionOutcome?.ProductionEncodeCount > 1 || executionOutcome?.AdaptiveStorageSavingsRetry is not null)
            return;
        try
        {
            MediaProbeStreamInfo? outputVideo = finalOutputProbe?.Streams.FirstOrDefault(stream =>
                stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            double? actualVideoKbps = outputVideo?.BitRate is > 0
                ? outputVideo.BitRate.Value / 1000d
                : null;
            bool appended = _predictionShadowService.RecordOutcome(
                predictionPlan.PlanId.ToString("N"),
                outcome == EncodingStatisticsOutcome.Success ? "Completed" : outcome.ToString(),
                executionOutcome?.TerminalResult.ToString() ?? outcome.ToString(),
                executionOutcome?.Validation?.Status.ToString() ?? "NotRecorded",
                executionOutcome?.Finalization?.Status.ToString() ?? "NotRecorded",
                recoveredSuccessful,
                actualVideoKbps,
                outputSizeBytes,
                recordedUtc);
            if (appended)
                Debug.WriteLine($"[PredictionShadow] Outcome appended for plan {predictionPlan.PlanId:N}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PredictionShadow] Outcome capture failed; encode result is unchanged: {ex.Message}");
        }
    }

    private static void ValidateSnapshot(EncodeExecutionSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.SourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.LogicalSourcePath);
        ArgumentNullException.ThrowIfNull(snapshot.Input);
        ArgumentNullException.ThrowIfNull(snapshot.Encoder);
        ArgumentNullException.ThrowIfNull(snapshot.Restoration);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.StatisticsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.MediaFluxVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.EncoderText);
        if (snapshot.StatisticsStartUtc == default)
            throw new ArgumentException("A statistics start time is required.", nameof(snapshot));
    }

    private static long? PredictionBytes(EncodingPlan? plan)
    {
        double? megabytes = plan?.SizePredictionCalibration?.CalibratedPredictionMb ??
            plan?.Estimates.HistoricalPrediction?.PredictedOutputSizeMb ??
            plan?.Estimates.EstimatedOutputSizeMb;
        return megabytes is >= 0 && double.IsFinite(megabytes.Value)
            ? (long)Math.Round(megabytes.Value * 1024d * 1024d)
            : null;
    }

    private static long? PredictionBaseBytes(EncodingPlan? plan)
    {
        double? megabytes = plan?.SizePredictionCalibration?.BasePredictionMb ??
            plan?.Estimates.HistoricalPrediction?.PredictedOutputSizeMb ??
            plan?.Estimates.EstimatedOutputSizeMb;
        return megabytes is >= 0 && double.IsFinite(megabytes.Value)
            ? (long)Math.Round(megabytes.Value * 1024d * 1024d)
            : null;
    }

    private static long? PredictionHypotheticalBytes(EncodingPlan? plan)
    {
        double? megabytes = plan?.SizePredictionCalibration?.HypotheticalCalibratedPredictionMb ??
            plan?.SizePredictionCalibration?.CalibratedPredictionMb;
        return megabytes is >= 0 && double.IsFinite(megabytes.Value)
            ? (long)Math.Round(megabytes.Value * 1024d * 1024d)
            : null;
    }

    private static string CodecFamily(string? value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized.Contains("265") || normalized.Contains("hevc") ? "hevc" :
            normalized.Contains("264") || normalized.Contains("avc") ? "h264" :
            normalized.Contains("av1") ? "av1" : normalized;
    }
}
