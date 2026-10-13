using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class BoundedRetryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-RetryPersistence", Guid.NewGuid().ToString("N"));
    private string StatisticsPath => Path.Combine(_root, "statistics.jsonl");
    private string HistoryPath => Path.Combine(_root, "history.json");
    public BoundedRetryPersistenceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(EncodingTerminalResult.Completed, EncodingStatisticsOutcome.Success, JobStatus.Success)]
    [InlineData(EncodingTerminalResult.StoragePolicyRejected, EncodingStatisticsOutcome.StoragePolicyRejected, JobStatus.Skipped)]
    [InlineData(EncodingTerminalResult.EncodeFailed, EncodingStatisticsOutcome.Failed, JobStatus.Failed)]
    [InlineData(EncodingTerminalResult.ValidationFailed, EncodingStatisticsOutcome.ValidationFailed, JobStatus.Failed)]
    [InlineData(EncodingTerminalResult.Canceled, EncodingStatisticsOutcome.Cancelled, JobStatus.Canceled)]
    public void TerminalAttemptOverridesStaleInitialRejectionAndPersistsOneLogicalRow(
        EncodingTerminalResult terminal, EncodingStatisticsOutcome expected, JobStatus historyStatus)
    {
        var trace = BoundedRetryEvidence.Trace(terminal);
        var stats = new EncodingStatisticsService(StatisticsPath);
        var record = new EncodingStatisticsRecord
        {
            Id = "logical-job", Outcome = EncodingStatisticsOutcome.StoragePolicyRejected,
            StartUtc = DateTime.UtcNow.AddSeconds(-65), EndUtc = DateTime.UtcNow,
            SourceSizeBytes = 1_000_000, OutputSizeBytes = 950_000, ProcessingSeconds = 60,
            PredictionQuality = "25", RecoveredSuccessful = true,
            StorageSavings = trace.Attempts[0].PhaseOneResult,
            AdaptiveSelection = BoundedRetryEvidence.Selection(), AdaptiveStorageSavingsRetry = trace
        };
        Assert.True(stats.AppendFinalized(record));
        Assert.False(stats.AppendFinalized(record));
        var persisted = Assert.Single(new EncodingStatisticsService(StatisticsPath).GetAll());
        Assert.Equal(expected, persisted.Outcome);
        Assert.Equal(terminal.ToString(), persisted.TerminalResult);
        Assert.Equal(2, persisted.ProductionEncodeCount);
        Assert.Equal("27", persisted.PredictionQuality);
        Assert.Equal(60, persisted.ProcessingSeconds);
        Assert.Equal(terminal == EncodingTerminalResult.Completed ? 800_000L : (long?)null, persisted.OutputSizeBytes);
        Assert.False(persisted.RecoveredSuccessful);
        Assert.False(persisted.IsEligibleForSingleEncodeLearning);
        Assert.Null(persisted.SourceAdaptiveShadow);
        Assert.Equal(25, persisted.AdaptiveSelection!.SelectedQuality);
        AssertTrace(trace, persisted.AdaptiveStorageSavingsRetry!);
        Assert.Equal(terminal is EncodingTerminalResult.Completed or EncodingTerminalResult.StoragePolicyRejected,
            persisted.StorageSavings is not null);

        var history = new HistoryService(HistoryPath);
        var outcome = new EncodingExecutionOutcome(Guid.NewGuid(), [], [], ProductionEncodeCount: 2,
            TerminalResult: terminal) { AdaptiveStorageSavingsRetry = trace };
        history.AppendEncodingOutcome(new JobHistoryRecord
        {
            Id = "logical-job", Status = JobStatus.Skipped, StorageSavings = trace.Attempts[0].PhaseOneResult,
            AdaptiveSelection = BoundedRetryEvidence.Selection(), OutputSizeBytes = 950_000
        }, outcome);
        var loaded = Assert.Single(new HistoryService(HistoryPath).LoadAll());
        Assert.Equal(historyStatus, loaded.Status);
        Assert.Equal(terminal, loaded.TerminalResult);
        Assert.Equal(2, loaded.ProductionEncodeCount);
        Assert.Equal(persisted.OutputSizeBytes, loaded.OutputSizeBytes);
        AssertTrace(trace, loaded.AdaptiveStorageSavingsRetry!);
        string json = File.ReadAllText(StatisticsPath);
        using var document = JsonDocument.Parse(json.Trim());
        Assert.True(document.RootElement.GetProperty("AdaptiveSelection").TryGetProperty("Candidates", out _));
        Assert.DoesNotContain("Samples", document.RootElement.GetProperty("AdaptiveStorageSavingsRetry").GetRawText());
        Assert.Single(File.ReadAllLines(StatisticsPath));
        Assert.Single(File.ReadAllLines(Path.Combine(_root, "history.jsonl")));
        var totals = EncodingStatisticsCalculator.Aggregate(stats.GetAll());
        Assert.Equal(1, totals.FilesProcessed);
        Assert.Equal(terminal == EncodingTerminalResult.Completed ? 1 : 0, totals.Successful);
    }

    [Fact]
    public void CancellationDuringTransitionKeepsAttemptEvidenceButOverridesLogicalRejection()
    {
        var trace = BoundedRetryEvidence.Trace(EncodingTerminalResult.StoragePolicyRejected);
        var stats = new EncodingStatisticsService(StatisticsPath);
        stats.AppendFinalized(new() { Id = "canceled", Outcome = EncodingStatisticsOutcome.Cancelled,
            AdaptiveStorageSavingsRetry = trace, StorageSavings = trace.Attempts[0].PhaseOneResult });
        var loaded = Assert.Single(new EncodingStatisticsService(StatisticsPath).GetAll());
        Assert.Equal(EncodingStatisticsOutcome.Cancelled, loaded.Outcome);
        Assert.Equal(EncodingTerminalResult.Canceled, loaded.AdaptiveStorageSavingsRetry!.LogicalTerminalResult);
        Assert.Equal(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected, loaded.AdaptiveStorageSavingsRetry.Attempts[1].Outcome);
        Assert.True(loaded.AdaptiveStorageSavingsRetry.Attempts[1].CancellationRequested);
        Assert.Null(loaded.StorageSavings);
        new HistoryService(HistoryPath).AppendEncodingOutcome(new() { Status = JobStatus.Canceled },
            new(Guid.NewGuid(), [], [], ProductionEncodeCount: 2) { AdaptiveStorageSavingsRetry = trace });
        Assert.Equal(EncodingTerminalResult.Canceled, Assert.Single(new HistoryService(HistoryPath).LoadAll()).TerminalResult);
    }

    [Fact]
    public void PreLaunchRetryFailureKeepsOneProductionStartButCannotTrain()
    {
        var trace = BoundedRetryEvidence.Trace(EncodingTerminalResult.EncodeFailed, preparationFailure: true);
        var stats = new EncodingStatisticsService(StatisticsPath);
        stats.AppendFinalized(new() { Id = "prepare-failure", AdaptiveStorageSavingsRetry = trace });
        var loaded = Assert.Single(new EncodingStatisticsService(StatisticsPath).GetAll());
        Assert.Equal(1, loaded.ProductionEncodeCount);
        Assert.False(loaded.IsEligibleForSingleEncodeLearning);
        Assert.Equal(2, loaded.AdaptiveStorageSavingsRetry!.Attempts.Count);
        Assert.False(loaded.AdaptiveStorageSavingsRetry.Attempts[1].ProcessStarted);
    }

    [Fact]
    public void LegacyFilesLoadWithoutRewritingOrChangingOneAttemptSemantics()
    {
        const string statistics = "{\"SchemaVersion\":5,\"Id\":\"legacy\",\"Outcome\":0,\"OutputSizeBytes\":123}\r\n";
        const string history = "{\"Id\":\"legacy\",\"Status\":0,\"OutputSizeBytes\":123}\r\n";
        File.WriteAllText(StatisticsPath, statistics);
        File.WriteAllText(Path.Combine(_root, "history.jsonl"), history);
        var stats = Assert.Single(new EncodingStatisticsService(StatisticsPath).GetAll());
        var job = Assert.Single(new HistoryService(HistoryPath).LoadAll());
        Assert.Equal(1, stats.ProductionEncodeCount);
        Assert.True(stats.IsEligibleForSingleEncodeLearning);
        Assert.Equal(1, job.ProductionEncodeCount);
        Assert.False(job.IsMultiAttempt);
        Assert.Null(stats.AdaptiveStorageSavingsRetry);
        Assert.Null(job.AdaptiveStorageSavingsRetry);
        Assert.Equal(123, job.OutputSizeBytes);
        Assert.Equal(statistics, File.ReadAllText(StatisticsPath));
        Assert.Equal(history, File.ReadAllText(Path.Combine(_root, "history.jsonl")));
        File.Delete(Path.Combine(_root, "history.jsonl"));
        const string legacyArray = "[{\"Id\":\"old-array\",\"Status\":0}]";
        File.WriteAllText(HistoryPath, legacyArray);
        Assert.Equal("old-array", Assert.Single(new HistoryService(HistoryPath).LoadAll()).Id);
        Assert.Equal(legacyArray, File.ReadAllText(HistoryPath));
    }

    [Theory]
    [InlineData(EncodingStatisticsOutcome.Success, false, false)]
    [InlineData(EncodingStatisticsOutcome.Success, true, false)]
    [InlineData(EncodingStatisticsOutcome.Success, false, true)]
    [InlineData(EncodingStatisticsOutcome.StoragePolicyRejected, true, false)]
    [InlineData(EncodingStatisticsOutcome.Failed, false, false)]
    [InlineData(EncodingStatisticsOutcome.ValidationFailed, false, false)]
    [InlineData(EncodingStatisticsOutcome.Cancelled, true, false)]
    public void OneAttemptTerminalAndRecoverySemanticsRemainIntact(EncodingStatisticsOutcome outcome, bool adaptive, bool recovered)
    {
        var savings = outcome == EncodingStatisticsOutcome.StoragePolicyRejected ? BoundedRetryEvidence.Savings(950_000) : null;
        var record = new EncodingStatisticsRecord { Id = "single", Outcome = outcome, RecoveredSuccessful = recovered,
            StorageSavings = savings, OutputSizeBytes = 800_000,
            AdaptiveSelection = adaptive ? BoundedRetryEvidence.Selection() : null };
        new EncodingStatisticsService(StatisticsPath).AppendFinalized(record);
        var loaded = Assert.Single(new EncodingStatisticsService(StatisticsPath).GetAll());
        Assert.Equal(outcome, loaded.Outcome);
        Assert.Equal(recovered, loaded.RecoveredSuccessful);
        Assert.Equal(1, loaded.ProductionEncodeCount);
        Assert.Null(loaded.AdaptiveStorageSavingsRetry);
        Assert.True(loaded.IsEligibleForSingleEncodeLearning);
        Assert.Equal(outcome == EncodingStatisticsOutcome.Success ? 800_000L : (long?)null, loaded.OutputSizeBytes);
        var job = new JobHistoryRecord { Id = "single", Status = outcome == EncodingStatisticsOutcome.Success ? JobStatus.Success : JobStatus.Failed,
            OutputSizeBytes = 800_000 };
        new HistoryService(HistoryPath).AppendEncodingOutcome(job, null);
        var storedJob = Assert.Single(new HistoryService(HistoryPath).LoadAll());
        Assert.Equal(job.Status, storedJob.Status);
        Assert.Equal(800_000, storedJob.OutputSizeBytes);
        Assert.Equal(1, storedJob.ProductionEncodeCount);
        Assert.Null(storedJob.AdaptiveStorageSavingsRetry);
    }

    [Fact]
    public void RawResearchJournalRetainsRetryFactsAndLearningConsumersStillRejectThem()
    {
        var statistics = new EncodingStatisticsService(StatisticsPath);
        statistics.AppendFinalized(new() { Id = "retry", EndUtc = DateTime.UtcNow, Outcome = EncodingStatisticsOutcome.Success,
            AdaptiveStorageSavingsRetry = BoundedRetryEvidence.Trace(), SourceSizeBytes = 1_000_000,
            MediaDurationSeconds = 120, ProcessingSeconds = 60 });
        var raw = Assert.Single(new PredictionShadowResearchHistoryReader(StatisticsPath).ReadFinalizedStatistics());
        Assert.Equal(2, raw.ProductionEncodeCount);
        Assert.Equal(2, raw.AdaptiveStorageSavingsRetry!.Attempts.Count);
        Assert.Equal(800_000, raw.OutputSizeBytes);
        Assert.Equal(0, new EncodingPredictionAccuracyService().Summarize(
            new EncodingPredictionAccuracyService().CreateRows([raw]), includeRecovered: true).CompletedCount);
    }

    internal static void AssertTrace(AdaptiveStorageSavingsRetryTrace expected, AdaptiveStorageSavingsRetryTrace actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(2, actual.Attempts.Count);
        Assert.Equal(new[] { 25, 27 }, actual.Attempts.Select(attempt => attempt.EffectiveQuality));
        Assert.Equal(PolicyCDecisionReason.Selected, actual.DecisionAfterAttempt1!.ReasonCode);
        Assert.Equal(950_000, actual.Attempts[0].ActualCandidateOutputBytes);
        Assert.Equal(30, actual.Attempts[0].ProductionEncodeSeconds);
        Assert.Equal(27, actual.DecisionAfterAttempt1.CandidateQuality);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
