using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Projects immutable Policy C evidence onto one logical terminal record.</summary>
internal static class EncodingRetryPersistence
{
    public static void Apply(EncodingStatisticsRecord record)
    {
        if (record.AdaptiveStorageSavingsRetry is not { } trace) return;
        if (record.Outcome == EncodingStatisticsOutcome.Cancelled && trace.LogicalTerminalResult != EncodingTerminalResult.Canceled)
            trace = trace.WithLogicalCancellation();
        record.AdaptiveStorageSavingsRetry = trace;
        record.ProductionEncodeCount = Math.Max(record.ProductionEncodeCount, trace.Attempts.Count(attempt => attempt.ProcessStarted));
        AdaptiveStorageSavingsEncodeAttempt terminal = trace.Attempts[trace.TerminalAttemptNumber - 1];
        record.TerminalResult = trace.LogicalTerminalResult.ToString();
        record.Outcome = trace.LogicalTerminalResult switch
        {
            EncodingTerminalResult.Completed => EncodingStatisticsOutcome.Success,
            EncodingTerminalResult.StoragePolicyRejected => EncodingStatisticsOutcome.StoragePolicyRejected,
            EncodingTerminalResult.Canceled => EncodingStatisticsOutcome.Cancelled,
            EncodingTerminalResult.ValidationFailed => EncodingStatisticsOutcome.ValidationFailed,
            EncodingTerminalResult.FinalizationFailed when record.Outcome is EncodingStatisticsOutcome.PromotionFailed or EncodingStatisticsOutcome.FinalVerificationFailed => record.Outcome,
            EncodingTerminalResult.FinalizationFailed => EncodingStatisticsOutcome.PromotionFailed,
            _ => EncodingStatisticsOutcome.Failed
        };
        record.OutputSizeBytes = record.Outcome == EncodingStatisticsOutcome.Success ? terminal.ActualCandidateOutputBytes : null;
        record.StorageSavings = TerminalSavings(trace, terminal);
        record.PredictionQuality = terminal.EffectiveQuality.ToString(CultureInfo.InvariantCulture);
        record.RecoveredSuccessful = false;
        record.SourceAdaptiveShadow = null;
    }

    public static void Apply(JobHistoryRecord record)
    {
        if (record.AdaptiveStorageSavingsRetry is not { } trace) return;
        if (record.Status == JobStatus.Canceled && trace.LogicalTerminalResult != EncodingTerminalResult.Canceled)
            trace = trace.WithLogicalCancellation();
        record.AdaptiveStorageSavingsRetry = trace;
        record.ProductionEncodeCount = Math.Max(record.ProductionEncodeCount, trace.Attempts.Count(attempt => attempt.ProcessStarted));
        AdaptiveStorageSavingsEncodeAttempt terminal = trace.Attempts[trace.TerminalAttemptNumber - 1];
        record.TerminalResult = trace.LogicalTerminalResult;
        record.Status = trace.LogicalTerminalResult switch
        {
            EncodingTerminalResult.Completed => JobStatus.Success,
            EncodingTerminalResult.StoragePolicyRejected => JobStatus.Skipped,
            EncodingTerminalResult.Canceled => JobStatus.Canceled,
            _ => JobStatus.Failed
        };
        record.OutputSizeBytes = record.Status == JobStatus.Success ? terminal.ActualCandidateOutputBytes : null;
        record.StorageSavings = TerminalSavings(trace, terminal);
    }

    private static StorageSavingsEvaluation? TerminalSavings(AdaptiveStorageSavingsRetryTrace trace, AdaptiveStorageSavingsEncodeAttempt terminal) =>
        trace.LogicalTerminalResult is EncodingTerminalResult.Completed or EncodingTerminalResult.StoragePolicyRejected
            ? terminal.PhaseOneResult : null;
}
