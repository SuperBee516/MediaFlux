using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Shared application lifecycle around one production encode attempt. UI adapters
/// provide presentation and supplemental history fields; this service owns the
/// production executor, terminal statistics, durable history, and failed-output
/// cleanup semantics used by GUI and headless callers.
/// </summary>
public sealed class EncodeExecutionLifecycleService
{
    private readonly EncodeExecutionOrchestrator _orchestrator;
    private readonly Lazy<HistoryService> _history;
    private readonly Action<string>? _diagnostic;
    private readonly object _historyGate = new();
    private readonly HashSet<string> _appendedHistoryIds = new(StringComparer.Ordinal);

    public EncodeExecutionLifecycleService(
        EncodeExecutionOrchestrator orchestrator,
        Func<HistoryService> historyFactory,
        Action<string>? diagnostic = null,
        object? historyGate = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        ArgumentNullException.ThrowIfNull(historyFactory);
        _history = new Lazy<HistoryService>(historyFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        _diagnostic = diagnostic;
        _historyGate = historyGate ?? new object();
    }

    public EncodeExecutionAttempt CreateAttempt(EncodeExecutionSnapshot snapshot) =>
        _orchestrator.CreateAttempt(snapshot);

    public void ValidateForPreflight(EncodeExecutionAttempt attempt) =>
        _orchestrator.ValidateForPreflight(attempt);

    public Task<EncodeExecutionResult> ExecuteAsync(
        EncodeExecutionAttempt attempt,
        EncodeExecutionCallbacks callbacks,
        EncodeLifecycleDiagnostics? lifecycleDiagnostics,
        CancellationToken cancellationToken) =>
        _orchestrator.ExecuteAsync(attempt, callbacks, lifecycleDiagnostics, cancellationToken);

    public EncodeExecutionLifecycleCompletion CompleteSuccessfulExecution(
        EncodeExecutionAttempt attempt,
        DateTime endUtc,
        long? outputSizeBytes,
        double processingSeconds,
        string notes,
        EncodingDiagnosticSummary? diagnosticSummary,
        bool recoveredSuccessful,
        MediaProbeResult? finalOutputProbe,
        JobHistoryRecord historyRecord)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(historyRecord);

        PopulateSuccessHistory(attempt, endUtc, outputSizeBytes, diagnosticSummary, historyRecord);
        bool historyRecorded = TryAppendHistory(historyRecord, attempt.ExecutionOutcome);
        bool statisticsRecorded = false;
        try
        {
            statisticsRecorded = _orchestrator.RecordSuccessfulExecution(
                attempt,
                endUtc,
                outputSizeBytes,
                processingSeconds,
                notes,
                diagnosticSummary,
                recoveredSuccessful,
                finalOutputProbe);
        }
        catch (Exception ex)
        {
            Report("Could not record successful encode statistics", ex);
        }

        return new(statisticsRecorded, historyRecorded);
    }

    public async Task<EncodeExecutionLifecycleCompletion> CompleteFailedExecutionAsync(
        EncodeExecutionAttempt? attempt,
        JobHistoryRecord historyRecord,
        DateTime endUtc,
        bool isCanceled,
        bool isStoragePolicyRejected,
        bool cleanupEnabled,
        string sourcePath,
        string attemptedOutputPath,
        string? stagedOutputPath,
        string? recoverableOutputPath,
        double processingSeconds,
        EncodingDiagnosticSummary? diagnosticSummary,
        EncodeFinalizationFailureKind? finalizationFailureKind,
        bool retryQueued,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(historyRecord);
        string incompletePath = FirstNonEmpty(
            recoverableOutputPath,
            attempt?.FinalizationResult?.RecoverableOutputPath,
            stagedOutputPath,
            attempt?.StagedOutputPath);
        string protectedPromotedPath = GetProtectedPromotedPath(attempt);
        string cleanupResult;
        try
        {
            cleanupResult = await IncompleteEncodeOutputCleanupService.CleanupAsync(
                sourcePath,
                incompletePath,
                isCanceled || cleanupEnabled,
                isCanceled ? "canceled" : "failed",
                protectedPromotedPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            cleanupResult = "cleanup service failed without changing the original encode result: " + ex.Message;
            Report("Incomplete encode-output cleanup failed", ex);
        }

        string sourceRetention = attempt?.AssignmentValidationFailure is not null
            ? "Original source retained because assigned experiment validation failed before encode execution."
            : "Original source retained because validated finalization did not complete.";
        if (attempt?.EncodeResult is { Success: true, FinalizationSucceeded: true })
            sourceRetention = "Original source retained; a validated output had already been promoted before the later lifecycle error.";

        PopulateFailureHistory(
            attempt,
            historyRecord,
            endUtc,
            isCanceled,
            isStoragePolicyRejected,
            attemptedOutputPath,
            incompletePath,
            cleanupResult,
            sourceRetention,
            diagnosticSummary,
            finalizationFailureKind,
            errorMessage);

        bool statisticsRecorded = false;
        if (attempt is not null)
        {
            try
            {
                statisticsRecorded = _orchestrator.RecordFailedExecution(
                    attempt,
                    endUtc,
                    isCanceled,
                    finalizationFailureKind,
                    attemptedOutputPath,
                    processingSeconds,
                    historyRecord.Notes,
                    diagnosticSummary,
                    retryQueued);
            }
            catch (Exception ex)
            {
                Report("Could not record failed encode statistics", ex);
            }
        }

        bool historyRecorded = TryAppendHistory(historyRecord, attempt?.ExecutionOutcome);
        return new(statisticsRecorded, historyRecorded, cleanupResult, sourceRetention);
    }

    /// <summary>Records a source/snapshot rejection that occurred before an attempt existed.</summary>
    public EncodeExecutionLifecycleCompletion RecordRejectedBeforeAttempt(JobHistoryRecord historyRecord)
    {
        ArgumentNullException.ThrowIfNull(historyRecord);
        historyRecord.FinalizationOutcome ??= "PreflightRejected";
        historyRecord.TerminalResult ??= historyRecord.Status == JobStatus.Canceled
            ? EncodingTerminalResult.Canceled
            : EncodingTerminalResult.ValidationFailed;
        return new(false, TryAppendHistory(historyRecord, null));
    }

    public bool TryAppendHistory(JobHistoryRecord historyRecord, EncodingExecutionOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(historyRecord);
        try
        {
            lock (_historyGate)
            {
                if (_appendedHistoryIds.Contains(historyRecord.Id))
                    return true;
                _history.Value.AppendEncodingOutcome(historyRecord, outcome);
                _appendedHistoryIds.Add(historyRecord.Id);
            }
            return true;
        }
        catch (Exception ex)
        {
            Report("History append failed", ex);
            return false;
        }
    }

    public static string ResolveFailureFinalizationOutcome(
        EncodeFinalizationResult? finalizationResult,
        bool assignmentValidationFailed,
        bool researchEvidenceFailed,
        bool adaptivePolicySkipped,
        bool isCanceled) =>
        finalizationResult?.FailureKind.ToString() ??
        (assignmentValidationFailed
            ? "ExperimentAssignmentValidationFailed"
            : researchEvidenceFailed
                ? "ResearchEvidenceCaptureFailed"
                : adaptivePolicySkipped
                ? "NotRun"
                    : isCanceled ? "Canceled" : "FfmpegFailed");

    private static void PopulateSuccessHistory(
        EncodeExecutionAttempt attempt,
        DateTime endUtc,
        long? outputSizeBytes,
        EncodingDiagnosticSummary? diagnosticSummary,
        JobHistoryRecord record)
    {
        EncodeExecutionSnapshot snapshot = attempt.Snapshot;
        EncodingService.EncodeResult output = attempt.EncodeResult ??
            throw new InvalidOperationException("A completed encode result is required before success history can be written.");
        record.Id = snapshot.OperationId;
        record.Status = JobStatus.Success;
        record.StartUtc = snapshot.StatisticsStartUtc;
        record.EndUtc = endUtc;
        record.SourcePath = snapshot.LogicalSourcePath;
        record.OutputPath = output.OutputPath;
        record.EncoderMode = snapshot.EncoderText;
        record.TargetMb = snapshot.TargetMb;
        record.DurationSec = snapshot.MediaDurationSeconds;
        record.SourceSizeBytes = snapshot.SourceSizeBytes ?? record.SourceSizeBytes;
        record.OutputSizeBytes = outputSizeBytes ?? output.FinalOutputSizeBytes;
        record.FinalizationOutcome = "ValidatedAndFinalized";
        record.StagingPath = output.StagingPath;
        record.SourceDeletionResult = attempt.SourceDeletion?.Message ?? record.SourceDeletionResult;
        record.RequestedOutputContainer = output.RequestedOutputContainer.ToString();
        record.ResolvedOutputContainer = output.ResolvedOutputContainer.ToString();
        record.ContainerDecisionReason = output.ContainerDecisionReason;
        record.DiagnosticSummary = diagnosticSummary;
        record.StorageSavings = output.StorageSavings;
        record.AdaptiveSelection = attempt.AdaptiveSelection;
        record.TerminalResult = attempt.ExecutionOutcome?.TerminalResult ?? EncodingTerminalResult.Completed;
    }

    private static void PopulateFailureHistory(
        EncodeExecutionAttempt? attempt,
        JobHistoryRecord record,
        DateTime endUtc,
        bool isCanceled,
        bool isStoragePolicyRejected,
        string attemptedOutputPath,
        string incompletePath,
        string cleanupResult,
        string sourceRetention,
        EncodingDiagnosticSummary? diagnosticSummary,
        EncodeFinalizationFailureKind? finalizationFailureKind,
        string errorMessage)
    {
        EncodeExecutionSnapshot? snapshot = attempt?.Snapshot;
        bool adaptiveSkipped = attempt?.ExecutionOutcome?.TerminalResult == EncodingTerminalResult.AdaptiveStorageSavingsSkipped;
        EncodeFinalizationResult? finalization = attempt?.FinalizationResult;
        record.Id = snapshot?.OperationId ?? record.Id;
        record.Status = isCanceled
            ? JobStatus.Canceled
            : isStoragePolicyRejected ? JobStatus.Skipped : JobStatus.Failed;
        record.StartUtc = snapshot?.StatisticsStartUtc ?? record.StartUtc;
        record.EndUtc = endUtc;
        record.SourcePath = snapshot?.LogicalSourcePath ?? record.SourcePath;
        record.OutputPath = attemptedOutputPath;
        record.EncoderMode = snapshot?.EncoderText ?? record.EncoderMode;
        record.TargetMb = snapshot?.TargetMb ?? record.TargetMb;
        record.DurationSec = snapshot?.MediaDurationSeconds ?? record.DurationSec;
        record.SourceSizeBytes = snapshot?.SourceSizeBytes ?? record.SourceSizeBytes;
        record.OutputSizeBytes = adaptiveSkipped ? null : TryGetFileSize(incompletePath);
        record.ErrorSummary ??= errorMessage;
        record.FinalizationOutcome = ResolveFailureFinalizationOutcome(
            finalization,
            attempt?.AssignmentValidationFailure is not null,
            errorMessage.Contains(nameof(AdaptivePreAttemptEvidenceCaptureException), StringComparison.Ordinal),
            isStoragePolicyRejected,
            isCanceled);
        record.StagingPath = FirstNonEmpty(attempt?.StagedOutputPath, record.StagingPath);
        record.SourceDeletionResult = sourceRetention;
        record.RequestedOutputContainer = attempt?.ContainerDecision?.Requested.ToString() ?? record.RequestedOutputContainer;
        record.ResolvedOutputContainer = attempt?.ContainerDecision?.Resolved.ToString() ?? record.ResolvedOutputContainer;
        record.ContainerDecisionReason = attempt?.ContainerDecision?.Reason ?? record.ContainerDecisionReason;
        record.DiagnosticSummary = diagnosticSummary ?? record.DiagnosticSummary;
        record.StorageSavings = isCanceled ? null : finalization?.StorageSavings ?? record.StorageSavings;
        record.AdaptiveSelection = attempt?.AdaptiveSelection ?? record.AdaptiveSelection;
        record.TerminalResult = isCanceled
            ? EncodingTerminalResult.Canceled
            : attempt?.ExecutionOutcome?.TerminalResult ?? record.TerminalResult ?? EncodingTerminalResult.EncodeFailed;
        record.Notes = string.Join(" ", new[]
        {
            record.Notes,
            sourceRetention,
            "Incomplete output cleanup: " + cleanupResult
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (finalizationFailureKind == EncodeFinalizationFailureKind.StoragePolicyRejected)
            record.TerminalResult = EncodingTerminalResult.StoragePolicyRejected;
    }

    private static string GetProtectedPromotedPath(EncodeExecutionAttempt? attempt)
    {
        if (attempt?.EncodeResult is { Success: true, FinalizationSucceeded: true } output)
            return output.OutputPath;
        if (attempt?.FinalizationResult is { Success: true } finalization)
            return finalization.FinalOutputPath;
        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static long? TryGetFileSize(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
    }

    private void Report(string message, Exception ex)
    {
        try { _diagnostic?.Invoke(message + ": " + ex.Message); }
        catch { }
    }
}

public sealed record EncodeExecutionLifecycleCompletion(
    bool StatisticsRecorded,
    bool HistoryRecorded,
    string CleanupResult = "",
    string SourceRetentionResult = "");
