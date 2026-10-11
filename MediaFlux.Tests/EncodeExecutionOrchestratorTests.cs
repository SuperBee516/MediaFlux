using System.Globalization;
using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeExecutionOrchestratorTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-EncodeExecutionOrchestratorTests", Guid.NewGuid().ToString("N"));

    public EncodeExecutionOrchestratorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AssignedSuccessCapturesBeforeExecutionAndPersistsStatisticsAndOutcomeOnce()
    {
        string source = CreateFile("assigned-target.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "encoding-statistics.jsonl"));
        var executor = new SyntheticExecutor(MakePlan(), reportRecoveredOutcome: true);
        PredictionShadowExperimentFreezeStore freezeStore = CreateFreezeStore(source);
        var orchestrator = CreateOrchestrator(executor, journal, statistics, freezeStore);
        PredictionShadowExperimentAssignment assignment = Assignment();
        EncodeExecutionSnapshot assignedSnapshot = Snapshot(
            source,
            PredictionShadowExperimentAssignmentPersistence.Capture(source, assignment)) with
        {
            AdaptiveStorageSavingsEnabled = true,
            ExperimentalPolicyCRetryEnabled = true
        };
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(assignedSnapshot);

        EncodeExecutionResult result = await orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None);

        Assert.Equal(new[] { "plan", "capture", "encode" }, executor.Events);
        Assert.Equal(EncodingQualityIntent.Automatic(QualityTarget.Balanced), executor.Request!.QualityIntent);
        Assert.Equal(25, executor.Request.QualityValue);
        Assert.False(executor.Request.ExperimentalPolicyCRetryEnabled);
        Assert.False(result.SourceDeletion.Deleted);
        PredictionShadowJournalEvent frozen = Assert.Single(journal.ReadEvents());
        Assert.Equal("Frozen", frozen.EventType);
        Assert.Equal(assignment, frozen.Frozen!.ExperimentAssignment);
        Assert.Equal(attempt.Plan!.PlanId, executor.Plan.PlanId);

        Assert.True(orchestrator.RecordSuccessfulExecution(
            attempt, Now, 123_456_789, 42.5, "synthetic success", null, true, null));
        Assert.False(orchestrator.RecordSuccessfulExecution(
            attempt, Now.AddSeconds(1), 123_456_789, 42.5, "duplicate completion", null, true, null));

        EncodingStatisticsRecord record = Assert.Single(statistics.GetAll());
        Assert.Equal(EncodingStatisticsOutcome.Success, record.Outcome);
        Assert.True(record.RecoveredSuccessful);
        Assert.Equal(EncodingTerminalResult.CompletedAfterRecovery.ToString(), record.TerminalResult);
        Assert.Equal(attempt.Plan!.PlanId.ToString("N"), record.PredictionPlanId);
        Assert.Equal(source, record.SourcePath);
        PredictionShadowJournalEvent[] events = journal.ReadEvents().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("Outcome", events[1].EventType);
        Assert.Equal(assignment, events[1].Outcome!.ExperimentAssignment);
    }

    [Fact]
    public void HeadlessStructuredResultProjectsSuccessfulEvidenceFromExecutionOutcome()
    {
        string source = CreateFile("headless-result-projection.mp4");
        long sourceBytes = new FileInfo(source).Length;
        long outputBytes = sourceBytes - 4;
        var contract = new StorageSavingsContract(
            true, sourceBytes, 10, null, sourceBytes - 2, "Synthetic accepted storage contract.");
        StorageSavingsEvaluation accepted = StorageSavingsContractService.Evaluate(contract, outputBytes);
        var orchestrator = CreateOrchestrator(
            new SyntheticExecutor(MakePlan()),
            new PredictionShadowObservationJournal(Path.Combine(_root, "result-projection-research.jsonl")),
            new EncodingStatisticsService(Path.Combine(_root, "result-projection-statistics.jsonl")));
        EncodeExecutionSnapshot snapshot = Snapshot(source, null) with { StorageSavingsContract = contract };
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(snapshot);
        attempt.ExecutionOutcome = new EncodingExecutionOutcome(
            Guid.NewGuid(), [], [],
            new EncodingValidationOutcome(
                EncodingLifecycleStatus.Passed,
                EncodingLifecycleStatus.Passed,
                EncodingLifecycleStatus.Passed,
                EncodingLifecycleStatus.Passed,
                EncodingLifecycleStatus.Passed,
                EncodingLifecycleStatus.Passed),
            TerminalResult: EncodingTerminalResult.Completed,
            StorageSavings: accepted,
            ProductionEncodeCount: 1);
        attempt.EncodeResult = new EncodingService.EncodeResult(
            success: true,
            outputPath: Path.Combine(_root, "headless-result-projection-output.mp4"),
            finalizationSucceeded: true,
            finalOutputSizeBytes: outputBytes,
            storageSavingsContract: contract,
            storageSavings: accepted);

        HeadlessEncodeJobResult result = HeadlessSavedJobItemPipeline.CreateStructuredResult(
            1, snapshot, attempt, HeadlessEncodeJobStatus.Success,
            Now, Now.AddSeconds(1), new EncodeExecutionLifecycleCompletion(true, true),
            "not applicable; encode succeeded.", "Source retained.", null, ffmpegExitCode: 0);

        Assert.True(result.FfprobeValidated);
        Assert.True(result.DecodeIntegrityValidated);
        Assert.True(result.MetadataValidated);
        Assert.True(result.StorageContractAccepted);
        Assert.Equal("Accepted", result.StorageContractResult);
        Assert.True(result.Promoted);
        Assert.Equal(outputBytes, result.OutputBytes);
    }

    [Fact]
    public async Task ExperimentalPolicyCRetryGateIsForwardedOnlyForOrdinaryAdaptiveExecution()
    {
        string source = CreateFile("policy-c-gate.mp4");
        var contract = new StorageSavingsContract(true, 1_000_000_000, 0, null,
            long.MaxValue, "Synthetic accepted storage contract for request forwarding.");
        StorageSavingsEvaluation accepted = StorageSavingsContractService.Evaluate(contract, 123_456_789);
        var executor = new SyntheticExecutor(MakePlan(), storageSavings: accepted);
        var orchestrator = CreateOrchestrator(
            executor,
            new PredictionShadowObservationJournal(Path.Combine(_root, "gate-research.jsonl")),
            new EncodingStatisticsService(Path.Combine(_root, "gate-statistics.jsonl")));
        EncodeExecutionSnapshot snapshot = Snapshot(source, null) with
        {
            AdaptiveStorageSavingsEnabled = true,
            ExperimentalPolicyCRetryEnabled = true,
            StorageSavingsContract = contract
        };

        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(snapshot);
        await orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None);

        Assert.True(executor.Request!.AdaptiveStorageSavingsEnabled);
        Assert.True(executor.Request.ExperimentalPolicyCRetryEnabled);
    }

    [Fact]
    public async Task OrdinarySuccessCompletesTheSameLifecycleWithoutExperimentMembership()
    {
        string source = CreateFile("ordinary-target.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "ordinary-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "ordinary-statistics.jsonl"));
        var executor = new SyntheticExecutor(MakePlan());
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(source, null));

        await orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None);
        orchestrator.RecordSuccessfulExecution(attempt, Now, 12_345, 1.5, "ordinary success", null, false, null);

        PredictionShadowJournalEvent[] events = journal.ReadEvents().ToArray();
        Assert.Equal(new[] { "Frozen", "Outcome" }, events.Select(entry => entry.EventType));
        Assert.All(events, entry => Assert.Null(entry.Frozen?.ExperimentAssignment ?? entry.Outcome?.ExperimentAssignment));
        Assert.Equal(EncodingStatisticsOutcome.Success, Assert.Single(statistics.GetAll()).Outcome);
        Assert.Equal(new[] { "plan", "capture", "encode" }, executor.Events);
    }

    [Fact]
    public async Task SharedLifecycleWritesRecoveredProductionHistoryAndStatistics()
    {
        string source = CreateFile("lifecycle-recovered-source.mp4");
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "lifecycle-success-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(
            new SyntheticExecutor(MakePlan(), reportRecoveredOutcome: true),
            new PredictionShadowObservationJournal(Path.Combine(_root, "lifecycle-success-research.jsonl")),
            statistics);
        EncodeExecutionSnapshot snapshot = Snapshot(source, null);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(snapshot);
        await orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None);
        var lifecycle = new EncodeExecutionLifecycleService(
            orchestrator,
            () => new HistoryService(Path.Combine(_root, "history-success", "history.json")));
        var record = new JobHistoryRecord { Id = snapshot.OperationId, Type = JobType.Encode, Log = "captured log" };

        EncodeExecutionLifecycleCompletion completion = lifecycle.CompleteSuccessfulExecution(
            attempt, Now, attempt.EncodeResult!.FinalOutputSizeBytes, 42.5, "Recovered and finalized.",
            null, recoveredSuccessful: true, null, record);

        Assert.True(completion.HistoryRecorded);
        Assert.True(completion.StatisticsRecorded);
        JobHistoryRecord history = Assert.Single(new HistoryService(Path.Combine(_root, "history-success", "history.json")).LoadAll());
        Assert.Equal(JobStatus.Success, history.Status);
        Assert.Equal(snapshot.OperationId, history.Id);
        Assert.Equal(source, history.SourcePath);
        Assert.Equal(attempt.EncodeResult.OutputPath, history.OutputPath);
        Assert.Equal(EncodingTerminalResult.CompletedAfterRecovery, history.TerminalResult);
        EncodingStatisticsRecord statisticsRecord = Assert.Single(statistics.GetAll());
        Assert.Equal(source, statisticsRecord.SourcePath);
        Assert.True(statisticsRecord.RecoveredSuccessful);
    }

    [Theory]
    [InlineData(false, JobStatus.Failed, EncodingStatisticsOutcome.Failed)]
    [InlineData(true, JobStatus.Canceled, EncodingStatisticsOutcome.Cancelled)]
    public async Task SharedFailureLifecycleCleansOnlyStagingAndRecordsFailureWithoutDeletingSource(
        bool canceled,
        JobStatus expectedHistoryStatus,
        EncodingStatisticsOutcome expectedStatisticsOutcome)
    {
        string source = CreateFile("lifecycle-failed-source.mp4");
        string staging = Path.Combine(_root, "lifecycle-failed.partial");
        string recoverable = Path.Combine(_root, "lifecycle-recoverable.partial");
        File.WriteAllBytes(staging, new byte[4096]);
        File.WriteAllBytes(recoverable, new byte[2048]);
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "lifecycle-failure-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(
            new SyntheticExecutor(MakePlan(), canceled
                ? new OperationCanceledException("synthetic cancellation")
                : new InvalidOperationException("synthetic encoder failure")),
            new PredictionShadowObservationJournal(Path.Combine(_root, "lifecycle-failure-research.jsonl")),
            statistics);
        EncodeExecutionSnapshot snapshot = Snapshot(source, null);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(snapshot);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        attempt.AttemptedOutputPath = staging;
        attempt.StagedOutputPath = staging;
        var lifecycle = new EncodeExecutionLifecycleService(
            orchestrator,
            () => new HistoryService(Path.Combine(_root, "history-failure", "history.json")));
        var record = new JobHistoryRecord { Id = snapshot.OperationId, Type = JobType.Encode, ErrorSummary = "synthetic encoder failure" };

        EncodeExecutionLifecycleCompletion completion = await lifecycle.CompleteFailedExecutionAsync(
            attempt, record, Now, isCanceled: canceled, isStoragePolicyRejected: false,
            cleanupEnabled: true, source, staging, staging, recoverable, 3.5, null, null,
            retryQueued: false, "synthetic encoder failure");

        Assert.True(completion.HistoryRecorded);
        Assert.True(completion.StatisticsRecorded);
        Assert.True(File.Exists(staging));
        Assert.False(File.Exists(recoverable));
        Assert.True(File.Exists(source));
        JobHistoryRecord history = Assert.Single(new HistoryService(Path.Combine(_root, "history-failure", "history.json")).LoadAll());
        Assert.Equal(expectedHistoryStatus, history.Status);
        Assert.Contains("deleted", history.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, history.SourcePath);
        Assert.Equal(expectedStatisticsOutcome, Assert.Single(statistics.GetAll()).Outcome);
    }

    [Fact]
    public async Task SharedFailureLifecycleProtectsAValidatedPromotedOutput()
    {
        string source = CreateFile("lifecycle-promoted-source.mp4");
        string promoted = CreateFile("lifecycle-promoted-output.mp4");
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "lifecycle-promoted-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(
            new SyntheticExecutor(MakePlan()),
            new PredictionShadowObservationJournal(Path.Combine(_root, "lifecycle-promoted-research.jsonl")),
            statistics);
        EncodeExecutionSnapshot snapshot = Snapshot(source, null);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(snapshot);
        attempt.EncodeResult = new EncodingService.EncodeResult(
            success: true, outputPath: promoted, finalizationSucceeded: true, finalOutputSizeBytes: new FileInfo(promoted).Length);
        attempt.FinalizationResult = new EncodeFinalizationResult
        {
            Success = true,
            FinalOutputPath = promoted,
            RecoverableOutputPath = promoted
        };
        attempt.AttemptedOutputPath = promoted;
        var lifecycle = new EncodeExecutionLifecycleService(
            orchestrator,
            () => new HistoryService(Path.Combine(_root, "history-promoted", "history.json")));

        EncodeExecutionLifecycleCompletion completion = await lifecycle.CompleteFailedExecutionAsync(
            attempt,
            new JobHistoryRecord { Id = snapshot.OperationId, Type = JobType.Encode },
            Now,
            isCanceled: false,
            isStoragePolicyRejected: false,
            cleanupEnabled: true,
            sourcePath: source,
            attemptedOutputPath: promoted,
            stagedOutputPath: promoted,
            recoverableOutputPath: promoted,
            processingSeconds: 1,
            diagnosticSummary: null,
            finalizationFailureKind: EncodeFinalizationFailureKind.Promotion,
            retryQueued: false,
            errorMessage: "A later lifecycle error occurred.");

        Assert.Contains("validated promoted output", completion.CleanupResult, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(promoted));
        Assert.True(File.Exists(source));
        JobHistoryRecord history = Assert.Single(new HistoryService(Path.Combine(_root, "history-promoted", "history.json")).LoadAll());
        Assert.Contains("had already been promoted", history.SourceDeletionResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanupErrorIsRecordedWithoutMaskingTheOriginalFailureOrSource()
    {
        string source = CreateFile("lifecycle-cleanup-error-source.mp4");
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "lifecycle-cleanup-error-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(
            new SyntheticExecutor(MakePlan()),
            new PredictionShadowObservationJournal(Path.Combine(_root, "lifecycle-cleanup-error-research.jsonl")),
            statistics);
        EncodeExecutionSnapshot snapshot = Snapshot(source, null);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(snapshot);
        const string originalFailure = "original encoder failure";
        var lifecycle = new EncodeExecutionLifecycleService(
            orchestrator,
            () => new HistoryService(Path.Combine(_root, "history-cleanup-error", "history.json")));

        EncodeExecutionLifecycleCompletion completion = await lifecycle.CompleteFailedExecutionAsync(
            attempt,
            new JobHistoryRecord { Id = snapshot.OperationId, Type = JobType.Encode, ErrorSummary = originalFailure },
            Now,
            isCanceled: false,
            isStoragePolicyRejected: false,
            cleanupEnabled: true,
            sourcePath: source,
            attemptedOutputPath: "\0",
            stagedOutputPath: "\0",
            recoverableOutputPath: "\0",
            processingSeconds: 1,
            diagnosticSummary: null,
            finalizationFailureKind: null,
            retryQueued: false,
            errorMessage: originalFailure);

        Assert.Contains("invalid", completion.CleanupResult, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        JobHistoryRecord history = Assert.Single(new HistoryService(Path.Combine(_root, "history-cleanup-error", "history.json")).LoadAll());
        Assert.Equal(originalFailure, history.ErrorSummary);
        Assert.Contains(completion.CleanupResult, history.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleExecutionPathBindingFailsBeforeExecutorAndNeverFallsBackToOrdinary()
    {
        string assignedSource = CreateFile("original.mp4");
        string queuedSource = CreateFile("replacement-path.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "stale-binding.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "stale-statistics.jsonl"));
        var executor = new SyntheticExecutor(MakePlan());
        PredictionShadowExperimentFreezeStore freezeStore = CreateFreezeStore(assignedSource);
        var orchestrator = CreateOrchestrator(executor, journal, statistics, freezeStore);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(
            queuedSource,
            PredictionShadowExperimentAssignmentPersistence.Capture(assignedSource, Assignment())));

        EncodeExecutionAssignmentValidationException failure = await Assert.ThrowsAsync<EncodeExecutionAssignmentValidationException>(
            () => orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        orchestrator.RecordFailedExecution(attempt, Now, false, EncodeFinalizationFailureKind.Validation,
            "", 0, failure.Message, null, retryQueued: false);

        Assert.Equal(0, executor.InvocationCount);
        Assert.Contains("source path", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(journal.ReadEvents());
        Assert.Equal(EncodingStatisticsOutcome.ValidationFailed, Assert.Single(statistics.GetAll()).Outcome);
    }

    [Fact]
    public async Task StaleLengthBindingFailsBeforeExecutorWithoutFrozenOrOutcome()
    {
        string source = CreateFile("stale-length.mp4");
        PredictionShadowExperimentFreezeStore freezeStore = CreateFreezeStore(source);
        PredictionShadowExperimentAssignmentBinding binding = PredictionShadowExperimentAssignmentPersistence.Capture(source, Assignment())!;
        File.AppendAllText(source, "changed length");
        await AssertAssignedRejected(source, binding, freezeStore, "length changed");
    }

    [Fact]
    public async Task StaleLastWriteBindingFailsBeforeExecutorWithoutFrozenOrOutcome()
    {
        string source = CreateFile("stale-time.mp4");
        PredictionShadowExperimentFreezeStore freezeStore = CreateFreezeStore(source);
        PredictionShadowExperimentAssignmentBinding binding = PredictionShadowExperimentAssignmentPersistence.Capture(source, Assignment())!;
        File.SetLastWriteTimeUtc(source, new DateTime(binding.SourceLastWriteTimeUtcTicks, DateTimeKind.Utc).AddMinutes(2));
        await AssertAssignedRejected(source, binding, freezeStore, "last-write time changed");
    }

    [Fact]
    public async Task MissingAssignedSourcePreservesMissingSourceFailureAndRejectsBeforeExecutor()
    {
        string source = CreateFile("missing-assigned-source.mp4");
        PredictionShadowExperimentFreezeStore freezeStore = CreateFreezeStore(source);
        PredictionShadowExperimentAssignmentBinding binding = PredictionShadowExperimentAssignmentPersistence.Capture(source, Assignment())!;
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "missing-assigned-source-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "missing-assigned-source-statistics.jsonl"));
        var executor = new SyntheticExecutor(MakePlan());
        var orchestrator = CreateOrchestrator(executor, journal, statistics, freezeStore);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(source, binding));
        File.Delete(source);

        EncodeExecutionAssignmentValidationException failure = await Assert.ThrowsAsync<EncodeExecutionAssignmentValidationException>(
            () => orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        orchestrator.RecordFailedExecution(attempt, Now, false, EncodeFinalizationFailureKind.Validation,
            "", 0, failure.Message, null, retryQueued: false);

        Assert.True(failure.IsMissingSource);
        Assert.IsType<FileNotFoundException>(failure.InnerException);
        Assert.Equal(0, executor.InvocationCount);
        Assert.Empty(journal.ReadEvents());
        Assert.Equal(EncodingStatisticsOutcome.ValidationFailed, Assert.Single(statistics.GetAll()).Outcome);
    }

    [Fact]
    public async Task MissingFreezeAndConflictingAssignmentFailBeforeExecutor()
    {
        string source = CreateFile("missing-freeze.mp4");
        PredictionShadowExperimentFreezeStore freezeStore = CreateFreezeStore(source);
        PredictionShadowExperimentAssignmentBinding valid = PredictionShadowExperimentAssignmentPersistence.Capture(source, Assignment())!;
        await AssertAssignedRejected(source,
            valid with { Assignment = valid.Assignment with { ExperimentId = "missing-freeze" } },
            freezeStore, "No immutable freeze");
        await AssertAssignedRejected(source,
            valid with { Assignment = valid.Assignment with { Slot = 2 } },
            freezeStore, "slot or stratum");
        await AssertAssignedRejected(source,
            valid with { Assignment = null! },
            freezeStore, "incomplete or invalid");
    }

    [Fact]
    public async Task FailedRetryRecordsResearchOutcomeAndDefersProductionStatistics()
    {
        string source = CreateFile("retry-target.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "retry-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "retry-statistics.jsonl"));
        var executor = new SyntheticExecutor(MakePlan(), exceptionAfterCapture: new InvalidOperationException("Synthetic encoder failure after Frozen capture."));
        PredictionShadowExperimentFreezeStore freezeStore = CreateFreezeStore(source);
        var orchestrator = CreateOrchestrator(executor, journal, statistics, freezeStore);
        PredictionShadowExperimentAssignment assignment = Assignment();
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(
            source,
            PredictionShadowExperimentAssignmentPersistence.Capture(source, assignment)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));

        Assert.False(orchestrator.RecordFailedExecution(
            attempt, Now, isCanceled: false, finalizationFailureKind: null,
            outputPath: "", processingSeconds: 3, notes: "retry scheduled",
            diagnosticSummary: null, retryQueued: true));
        Assert.Empty(statistics.GetAll());
        PredictionShadowJournalEvent[] events = journal.ReadEvents().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("Frozen", events[0].EventType);
        Assert.Equal("Outcome", events[1].EventType);
        Assert.Equal("Failed", events[1].Outcome!.State);
        Assert.Equal(assignment, events[1].Outcome!.ExperimentAssignment);
    }

    [Fact]
    public async Task TerminalEncodeFailureRecordsFailedStatisticsAndOutcome()
    {
        string source = CreateFile("terminal-failure.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "failed-research.jsonl"));
        string statisticsPath = Path.Combine(_root, "failed-statistics.jsonl");
        var statistics = new EncodingStatisticsService(statisticsPath);
        var executor = new SyntheticExecutor(
            MakePlan(),
            exceptionAfterCapture: new InvalidOperationException("Synthetic terminal failure."),
            terminalResultBeforeFailure: EncodingTerminalResult.EncodeFailed);
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(source, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        orchestrator.RecordFailedExecution(
            attempt, Now, isCanceled: false, finalizationFailureKind: null,
            outputPath: "", processingSeconds: 5, notes: "terminal failure",
            diagnosticSummary: null, retryQueued: false);

        EncodingStatisticsRecord failedRecord = Assert.Single(statistics.GetAll());
        Assert.Equal(EncodingStatisticsOutcome.Failed, failedRecord.Outcome);
        Assert.Equal(EncodingTerminalResult.EncodeFailed.ToString(), failedRecord.TerminalResult);
        EncodingStatisticsRecord persistedRecord = Assert.Single(new EncodingStatisticsService(statisticsPath).GetAll());
        Assert.Equal(EncodingStatisticsOutcome.Failed, persistedRecord.Outcome);
        Assert.Equal(EncodingTerminalResult.EncodeFailed.ToString(), persistedRecord.TerminalResult);
        PredictionShadowJournalEvent[] events = journal.ReadEvents().ToArray();
        Assert.Equal(new[] { "Frozen", "Outcome" }, events.Select(entry => entry.EventType));
        Assert.Equal("Failed", events[1].Outcome!.State);
    }

    [Fact]
    public async Task PrelaunchFailureCanRetainNotRunTerminalState()
    {
        string source = CreateFile("prelaunch-failure.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "prelaunch-research.jsonl"));
        string statisticsPath = Path.Combine(_root, "prelaunch-statistics.jsonl");
        var statistics = new EncodingStatisticsService(statisticsPath);
        var executor = new SyntheticExecutor(
            MakePlan(),
            exceptionAfterCapture: new InvalidOperationException("Synthetic failure before FFmpeg launch."),
            failBeforeFfmpegLaunch: true);
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(source, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        orchestrator.RecordFailedExecution(
            attempt, Now, isCanceled: false, finalizationFailureKind: null,
            outputPath: "", processingSeconds: 1, notes: "failed before launch",
            diagnosticSummary: null, retryQueued: false);

        EncodingStatisticsRecord record = Assert.Single(new EncodingStatisticsService(statisticsPath).GetAll());
        Assert.Equal(EncodingStatisticsOutcome.Failed, record.Outcome);
        Assert.Equal(EncodingTerminalResult.NotRun.ToString(), record.TerminalResult);
    }

    [Fact]
    public async Task CancellationRecordsCancelledStatisticsAndOutcome()
    {
        string source = CreateFile("canceled-target.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "canceled-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "canceled-statistics.jsonl"));
        var executor = new SyntheticExecutor(MakePlan(), exceptionAfterCapture: new OperationCanceledException("Synthetic cancellation."));
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(source, null));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        orchestrator.RecordFailedExecution(
            attempt, Now, isCanceled: true, finalizationFailureKind: null,
            outputPath: "", processingSeconds: 2, notes: "canceled",
            diagnosticSummary: null, retryQueued: false);

        Assert.Equal(EncodingStatisticsOutcome.Cancelled, Assert.Single(statistics.GetAll()).Outcome);
        Assert.Equal("Cancelled", journal.ReadEvents().Single(entry => entry.Outcome is not null).Outcome!.State);
    }

    [Fact]
    public async Task InvalidFinalizationCannotBeRecordedAsSuccess()
    {
        string source = CreateFile("invalid-finalization.mp4");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "invalid-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "invalid-statistics.jsonl"));
        var executor = new SyntheticExecutor(MakePlan(), finalizationSucceeded: false);
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(source, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => orchestrator.RecordSuccessfulExecution(
            attempt, Now, 10, 1, "must not succeed", null, false, null));
        orchestrator.RecordFailedExecution(
            attempt, Now, isCanceled: false,
            finalizationFailureKind: EncodeFinalizationFailureKind.FinalVerification,
            outputPath: "staged.mp4", processingSeconds: 1, notes: "verification failed",
            diagnosticSummary: null, retryQueued: false);

        Assert.Equal(EncodingStatisticsOutcome.FinalVerificationFailed, Assert.Single(statistics.GetAll()).Outcome);
        Assert.Equal("FinalVerificationFailed", journal.ReadEvents().Single(entry => entry.Outcome is not null).Outcome!.State);
    }

    [Fact]
    public async Task PolicyRejectionPersistsAsSkippedWithoutRetryAndQueueContinues()
    {
        string source = CreateFile("policy-target.mp4");
        var contract = StorageSavingsContractService.Capture(true, EncodingInputSource.FromFile(source));
        var evidence = StorageSavingsContractService.Evaluate(contract, new FileInfo(source).Length);
        var rejection = new EncodeFinalizationResult
        {
            FailureKind = EncodeFinalizationFailureKind.StoragePolicyRejected,
            StorageSavings = evidence, ErrorMessage = evidence.Reason
        };
        var rejected = new SyntheticExecutor(MakePlan(), new EncodeFinalizationException(rejection));
        var accepted = new SyntheticExecutor(MakePlan());
        var executor = new SequentialExecutor(rejected, accepted);
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "policy-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "policy-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        var first = orchestrator.CreateAttempt(Snapshot(source, null) with { StorageSavingsContract = contract });
        var second = orchestrator.CreateAttempt(Snapshot(source, null) with { OperationId = "next-job" });
        await new EncodeQueueRunner().RunAsync(new[] { first, second }, async attempt =>
        {
            try
            {
                await orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None);
                orchestrator.RecordSuccessfulExecution(attempt, Now, 123_456_789, 42.5, "accepted", null, false, null);
            }
            catch (EncodeFinalizationException ex)
            {
                Assert.Equal(EncodeFinalizationFailureKind.StoragePolicyRejected, ex.Result.FailureKind);
                Assert.False(EncodingRetryPolicy.AllowsAutomaticRetry(EncodingService.ResolveTerminalResult(ex.Result, false)));
                // A mistaken retry flag cannot suppress the terminal policy observation.
                Assert.True(orchestrator.RecordFailedExecution(attempt, Now, false, null, "", 42.5,
                    ex.Message, null, retryQueued: true));
            }
        }, maxParallel: 1, () => false, () => false);

        Assert.Equal(contract, rejected.Request!.StorageSavingsContract);
        Assert.Equal(1, rejected.InvocationCount);
        Assert.Equal(1, accepted.InvocationCount);
        Assert.True(File.Exists(source));
        var records = statistics.GetAll();
        var skipped = Assert.Single(records, record => record.Outcome == EncodingStatisticsOutcome.StoragePolicyRejected);
        Assert.Equal(evidence, skipped.StorageSavings);
        Assert.Null(skipped.OutputSizeBytes);
        Assert.Equal("StoragePolicyRejected", skipped.TerminalResult);
        Assert.Single(records, record => record.Outcome == EncodingStatisticsOutcome.Success);
        Assert.Contains(journal.ReadEvents(), entry => entry.Outcome?.State == "StoragePolicyRejected");
    }

    [Fact]
    public async Task AdaptiveAllMissIsRecordedBeforeEncodeAndTheNextQueueJobContinues()
    {
        string source = CreateFile("adaptive-all-miss-source.mp4");
        AdaptiveQualitySelectionEvidence evidence = AllMissEvidence();
        EncodingPlan skippedPlan = new()
        {
            IsAvailable = true,
            PlanId = Guid.NewGuid(),
            Source = new EncodingPlanSource("h264", 1920, 1080, 29.97, 120),
            Video = new EncodingPlanVideo("Reencode", "hevc_nvenc", VideoEncoderIds.Nvenc,
                1920, 1080, 1920, 1080, "p010le"),
            Validation = new EncodingPlanValidation("Production", true, false),
            AdaptiveSelection = evidence
        };
        var rejectedExecutor = new SyntheticExecutor(
            new EncodingPlanSnapshot(skippedPlan.PlanId, skippedPlan),
            adaptive: evidence,
            skipBeforeEncode: true);
        var acceptedExecutor = new SyntheticExecutor(MakePlan());
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "adaptive-all-miss-statistics.jsonl"));
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "adaptive-all-miss-research.jsonl"));
        var orchestrator = CreateOrchestrator(new SequentialExecutor(rejectedExecutor, acceptedExecutor), journal, statistics);
        EncodeExecutionAttempt rejectedAttempt = orchestrator.CreateAttempt(Snapshot(source, null) with
        {
            OperationId = "adaptive-all-miss",
            AdaptiveStorageSavingsEnabled = true
        });
        EncodeExecutionAttempt acceptedAttempt = orchestrator.CreateAttempt(
            Snapshot(CreateFile("following-job.mp4"), null) with { OperationId = "following-job" });

        await new EncodeQueueRunner().RunAsync([rejectedAttempt, acceptedAttempt], async attempt =>
        {
            try
            {
                await orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None);
                orchestrator.RecordSuccessfulExecution(attempt, Now, 123_456_789, 42.5, "accepted", null, false, null);
            }
            catch (AdaptiveStorageSavingsSkippedException ex)
            {
                Assert.Equal(evidence, ex.Evidence);
                orchestrator.RecordFailedExecution(attempt, Now, isCanceled: false,
                    finalizationFailureKind: null, outputPath: "", processingSeconds: 0,
                    notes: ex.Message, diagnosticSummary: null, retryQueued: false);
            }
        }, maxParallel: 1, () => false, () => false);

        Assert.Equal(new[] { "plan" }, rejectedExecutor.Events);
        Assert.Equal(new[] { "plan", "capture", "encode" }, acceptedExecutor.Events);
        Assert.Equal(0, rejectedAttempt.ExecutionOutcome!.ProductionEncodeCount);
        Assert.Equal(EncodingTerminalResult.AdaptiveStorageSavingsSkipped, rejectedAttempt.ExecutionOutcome.TerminalResult);
        Assert.Empty(rejectedAttempt.AttemptedOutputPath);
        Assert.Empty(rejectedAttempt.StagedOutputPath);
        Assert.True(File.Exists(source));

        EncodingStatisticsRecord[] records = statistics.GetAll().OrderBy(record => record.StartUtc).ToArray();
        EncodingStatisticsRecord skipped = Assert.Single(records, record => record.Outcome == EncodingStatisticsOutcome.AdaptiveStorageSavingsSkipped);
        Assert.Equal(EncodingTerminalResult.AdaptiveStorageSavingsSkipped.ToString(), skipped.TerminalResult);
        Assert.Equal(evidence, skipped.AdaptiveSelection);
        Assert.Null(skipped.OutputSizeBytes);
        Assert.Single(records, record => record.Outcome == EncodingStatisticsOutcome.Success);
    }

    [Fact]
    public async Task ApplicableContractCannotBypassAcceptanceThroughExecutorSuccess()
    {
        string source = CreateFile("unaccepted-target.mp4");
        var executor = new SyntheticExecutor(MakePlan());
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "unaccepted-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "unaccepted-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        var attempt = orchestrator.CreateAttempt(Snapshot(source, null) with
        {
            StorageSavingsContract = StorageSavingsContractService.Resolve(true, new FileInfo(source).Length),
            DeleteSourceAfterCompression = true
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => orchestrator.RecordSuccessfulExecution(
            attempt, Now, 123_456_789, 42.5, "incorrect success", null, false, null));
        Assert.True(File.Exists(source));
        Assert.Empty(statistics.GetAll());
    }

    [Fact]
    public async Task InconclusiveAdaptiveEvidenceFallsBackToConfiguredQualityAndContinuesQueue()
    {
        string source = CreateFile("adaptive-source.mp4");
        File.WriteAllBytes(source, new byte[1_000_000]);
        var evidence = await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples((_, position) =>
                position % 2 == 0 ? 8_000 : 12_000))
            .SelectAsync(AdaptiveStorageSavingsTests.Request(), null, CancellationToken.None);
        Assert.Equal(AdaptiveSelectionDisposition.NotSuitable, evidence.Disposition);
        Assert.All(evidence.Candidates, candidate => Assert.Equal(AdaptiveSampleClassification.Borderline, candidate.Classification));
        EncodingPlan initial = EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context());
        EncodingPlan fallback = EncodingPlanService.PreserveConfiguredQualityAfterAdaptiveAbstention(initial, evidence);
        Assert.Equal(initial.Quality!.EffectiveQuality, fallback.Quality!.EffectiveQuality);
        StorageSavingsEvaluation accepted = StorageSavingsContractService.Evaluate(AdaptiveStorageSavingsTests.Contract, 800_000);
        var firstExecutor = new SyntheticExecutor(new(fallback.PlanId, fallback), storageSavings: accepted, adaptive: evidence);
        var next = new SyntheticExecutor(MakePlan());
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "adaptive-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "adaptive-statistics.jsonl"));
        var history = new HistoryService(Path.Combine(_root, "adaptive-history.json"));
        var orchestrator = CreateOrchestrator(new SequentialExecutor(firstExecutor, next), journal, statistics);
        var callbacks = new EncodeExecutionCallbacks();
        var first = orchestrator.CreateAttempt(Snapshot(source, null) with
        { AdaptiveStorageSavingsEnabled = true, StorageSavingsContract = AdaptiveStorageSavingsTests.Contract, DeleteSourceAfterCompression = false });
        var second = orchestrator.CreateAttempt(Snapshot(source, null) with { OperationId = "next-adaptive-job" });
        await new EncodeQueueRunner().RunAsync(new[] { first, second }, async attempt =>
        {
            await orchestrator.ExecuteAsync(attempt, callbacks, null, CancellationToken.None);
            Assert.True(orchestrator.RecordSuccessfulExecution(attempt, Now, 800_000, 10, "success", null, false, null));
        }, 1, () => false, () => false);
        Assert.True(firstExecutor.Request!.AdaptiveStorageSavingsEnabled);
        Assert.Contains("encode", firstExecutor.Events);
        Assert.Equal(1, next.Events.Count(e => e == "encode"));
        Assert.Equal(1_000_000, new FileInfo(source).Length);
        EncodingStatisticsRecord record = Assert.Single(statistics.GetAll(), r => r.Id == first.Snapshot.OperationId);
        Assert.Equal(EncodingStatisticsOutcome.Success, record.Outcome);
        Assert.Equal(1, record.ProductionEncodeCount);
        Assert.Equal(800_000, record.OutputSizeBytes);
        Assert.Equal(AdaptiveSelectionDisposition.NotSuitable, record.AdaptiveSelection!.Disposition);
        Assert.Equal(evidence.PreferredQuality, record.AdaptiveSelection!.PreferredQuality);
        var totals = EncodingStatisticsCalculator.Aggregate(statistics.GetAll());
        Assert.Equal(0, totals.Skipped);
        Assert.Equal(0, totals.Failed);
        Assert.Equal(2, totals.Successful);
        Assert.Equal(new[] { "Frozen", "Outcome" }, journal.ReadEvents().Select(e => e.EventType));

        history.AppendEncodingOutcome(new JobHistoryRecord
        {
            Id = first.Snapshot.OperationId,
            Status = JobStatus.Success,
            TerminalResult = EncodingTerminalResult.Completed
        }, first.ExecutionOutcome);
        JobHistoryRecord historyRecord = Assert.Single(new HistoryService(Path.Combine(_root, "adaptive-history.json")).LoadAll());
        Assert.Equal(JobStatus.Success, historyRecord.Status);
        Assert.Equal(EncodingTerminalResult.Completed, historyRecord.TerminalResult);
        Assert.Equal(1, historyRecord.ProductionEncodeCount);
    }

    [Fact]
    public async Task SelectedAdaptiveQualityDoesNotOverrideActualByteRejectionOrCauseAnotherFullEncode()
    {
        string source = CreateFile("adaptive-rejected.mp4");
        File.WriteAllBytes(source, new byte[1_000_000]);
        var selection = await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples((q, _) => q < 26 ? 10_000 : 4_000))
            .SelectAsync(AdaptiveStorageSavingsTests.Request(), null, CancellationToken.None);
        EncodingPlan plan = EncodingPlanService.FreezeAdaptiveSelection(EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context()), selection);
        StorageSavingsEvaluation actual = StorageSavingsContractService.Evaluate(AdaptiveStorageSavingsTests.Contract, 900_001);
        var error = new EncodeFinalizationException(new EncodeFinalizationResult
        { FailureKind = EncodeFinalizationFailureKind.StoragePolicyRejected, StorageSavings = actual, ErrorMessage = "Actual output misses." });
        var executor = new SyntheticExecutor(new(plan.PlanId, plan), error, adaptive: selection);
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "adaptive-rejected-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "adaptive-rejected-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        var attempt = orchestrator.CreateAttempt(Snapshot(source, null) with
        { AdaptiveStorageSavingsEnabled = true, StorageSavingsContract = AdaptiveStorageSavingsTests.Contract, DeleteSourceAfterCompression = true });
        await Assert.ThrowsAsync<EncodeFinalizationException>(() => orchestrator.ExecuteAsync(attempt, new(), null, CancellationToken.None));
        Assert.True(orchestrator.RecordFailedExecution(attempt, Now, false, null, "", 10, "rejected", null, retryQueued: true));
        Assert.Equal(1, executor.Events.Count(e => e == "encode"));
        Assert.Equal(26, EncodingPlanService.GetExecutionValues(attempt.Plan!).QualityResolution.EffectiveQuality);
        var record = Assert.Single(statistics.GetAll());
        Assert.Equal(EncodingStatisticsOutcome.StoragePolicyRejected, record.Outcome);
        Assert.Null(record.OutputSizeBytes);
        Assert.Equal(26, record.AdaptiveSelection!.SelectedQuality);
        Assert.Equal(25, record.AdaptiveSelection.SamplesExecuted);
        Assert.True(record.AdaptiveSelection.SevenFirstEarlyPositive);
        Assert.Equal(1, record.ProductionEncodeCount);
        Assert.True(File.Exists(source));
        Assert.Empty(journal.ReadEvents());
    }

    [Fact]
    public async Task AdaptiveAcceptedFullOutputPersistsSelectionWithoutSampleTimeOrResearch()
    {
        string source = CreateFile("adaptive-accepted.mp4");
        File.WriteAllBytes(source, new byte[1_000_000]);
        var selection = await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples((q, _) => q < 25 ? 10_000 : 4_000))
            .SelectAsync(AdaptiveStorageSavingsTests.Request(), null, CancellationToken.None);
        selection = selection with { SamplingSeconds = 12 };
        EncodingPlan plan = EncodingPlanService.FreezeAdaptiveSelection(EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context()), selection);
        var actual = StorageSavingsContractService.Evaluate(AdaptiveStorageSavingsTests.Contract, 800_000);
        var executor = new SyntheticExecutor(new(plan.PlanId, plan), storageSavings: actual, adaptive: selection);
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "adaptive-accepted-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "adaptive-accepted-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        var attempt = orchestrator.CreateAttempt(Snapshot(source, null) with
        { AdaptiveStorageSavingsEnabled = true, StorageSavingsContract = AdaptiveStorageSavingsTests.Contract });
        await orchestrator.ExecuteAsync(attempt, new(), null, CancellationToken.None);
        Assert.True(orchestrator.RecordSuccessfulExecution(attempt, Now, 800_000, 42.5, "accepted", null, false, null));
        var record = Assert.Single(statistics.GetAll());
        Assert.Equal(EncodingStatisticsOutcome.Success, record.Outcome);
        Assert.Equal(800_000, record.OutputSizeBytes);
        Assert.Equal(30.5, record.ProcessingSeconds);
        Assert.Equal(25, record.AdaptiveSelection!.SelectedQuality);
        Assert.Equal(16, record.AdaptiveSelection.SamplesExecuted);
        Assert.True(record.AdaptiveSelection.SevenFirstEarlyPositive);
        Assert.Equal(1, record.ProductionEncodeCount);
        Assert.Null(record.SourceAdaptiveShadow);
        Assert.Equal("", record.PredictionConfidence);
        Assert.Empty(journal.ReadEvents());
        Assert.Single(executor.Events, e => e == "encode");
        var history = new HistoryService(Path.Combine(_root, "adaptive-history.json"));
        history.Append(new JobHistoryRecord { Status = JobStatus.Success, AdaptiveSelection = selection, OutputSizeBytes = 800_000 });
        var readBack = Assert.Single(history.LoadAll());
        Assert.Equal(25, readBack.AdaptiveSelection!.SelectedQuality);
        Assert.Equal(16, readBack.AdaptiveSelection.SamplesExecuted);
        Assert.True(readBack.AdaptiveSelection.SevenFirstEarlyPositive);
        Assert.Equal(800_000, readBack.OutputSizeBytes);
    }

    private sealed class SequentialExecutor(params IEncodeRequestExecutor[] executors) : IEncodeRequestExecutor
    {
        private readonly Queue<IEncodeRequestExecutor> _executors = new(executors);
        public Task<EncodingService.EncodeResult> EncodeWithResultAsync(EncodingRequest request) =>
            _executors.Dequeue().EncodeWithResultAsync(request);
    }

    [Fact]
    public async Task AcceptedContractRecordsOrdinarySuccessWithAcceptanceEvidence()
    {
        string source = CreateFile("accepted-contract.mp4");
        var contract = StorageSavingsContractService.Capture(true, EncodingInputSource.FromFile(source));
        var evidence = StorageSavingsContractService.Evaluate(contract, new FileInfo(source).Length / 2);
        var executor = new SyntheticExecutor(MakePlan(), storageSavings: evidence);
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "accepted-contract-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "accepted-contract-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        var attempt = orchestrator.CreateAttempt(Snapshot(source, null) with { StorageSavingsContract = contract });
        var result = await orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None);
        Assert.True(orchestrator.RecordSuccessfulExecution(attempt, Now, result.EncodedOutput.FinalOutputSizeBytes,
            42.5, "accepted", null, false, null));
        var record = Assert.Single(statistics.GetAll());
        Assert.Equal(EncodingStatisticsOutcome.Success, record.Outcome);
        Assert.Equal(evidence, record.StorageSavings);
        Assert.Equal(evidence.CandidateOutputBytes, record.OutputSizeBytes);
    }

    [Fact]
    public async Task SharedStatisticsWriterCannotEmitSuccessOrLearningBytesForRejectedEvidence()
    {
        string source = CreateFile("mislabelled-target.mp4");
        var contract = StorageSavingsContractService.Capture(true, EncodingInputSource.FromFile(source));
        var evidence = StorageSavingsContractService.Evaluate(contract, contract.SourceSizeBytes!.Value);
        var rejection = new EncodeFinalizationResult
        {
            FailureKind = EncodeFinalizationFailureKind.StoragePolicyRejected,
            StorageSavings = evidence, ErrorMessage = evidence.Reason
        };
        var executor = new SyntheticExecutor(MakePlan(), new EncodeFinalizationException(rejection));
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "mislabelled-research.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "mislabelled-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        var attempt = orchestrator.CreateAttempt(Snapshot(source, null) with { StorageSavingsContract = contract });
        await Assert.ThrowsAsync<EncodeFinalizationException>(() => orchestrator.ExecuteAsync(
            attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        orchestrator.RecordEncodingStatistics("mislabelled", Now, Now, EncodingStatisticsOutcome.Success,
            source, "", "hevc_nvenc", "NVENC", contract.SourceSizeBytes, evidence.CandidateOutputBytes, 120, 42.5,
            predictionPlan: attempt.Plan, storageSavings: evidence);
        var record = Assert.Single(statistics.GetAll());
        Assert.Equal(EncodingStatisticsOutcome.StoragePolicyRejected, record.Outcome);
        Assert.Null(record.OutputSizeBytes);
        Assert.Null(record.SourceAdaptiveShadow!.ActualOutputBytes);
        Assert.Contains(journal.ReadEvents(), entry => entry.Outcome?.State == "StoragePolicyRejected");
        Assert.DoesNotContain(journal.ReadEvents(), entry => entry.Outcome?.State == "Completed");
    }

    [Theory]
    [InlineData(EncodingTerminalResult.Completed)]
    [InlineData(EncodingTerminalResult.StoragePolicyRejected)]
    [InlineData(EncodingTerminalResult.EncodeFailed)]
    [InlineData(EncodingTerminalResult.ValidationFailed)]
    [InlineData(EncodingTerminalResult.Canceled)]
    public async Task BoundedRetryPublishesOnlyOneTerminalStatisticsAndHistoryRecord(EncodingTerminalResult terminal)
    {
        string source = CreateFile("retry.mp4");
        string statisticsPath = Path.Combine(_root, "retry-statistics.jsonl");
        var statistics = new EncodingStatisticsService(statisticsPath);
        var history = new HistoryService(Path.Combine(_root, "retry-history.json"));
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "retry-research.jsonl"));
        var trace = BoundedRetryEvidence.Trace(terminal);
        var plan = BoundedRetryEvidence.Plan();
        Exception? failure = terminal == EncodingTerminalResult.Completed ? null :
            terminal == EncodingTerminalResult.Canceled ? new OperationCanceledException("Retry canceled") : new InvalidOperationException("Retry failed");
        var executor = new SyntheticExecutor(new(plan.PlanId, plan), exceptionAfterCapture: failure,
            storageSavings: trace.Attempts[1].PhaseOneResult, adaptive: plan.AdaptiveSelection, retryTrace: trace);
        var orchestrator = CreateOrchestrator(executor, journal, statistics);
        var attempt = orchestrator.CreateAttempt(Snapshot(source, null) with
        {
            AdaptiveStorageSavingsEnabled = true, ExperimentalPolicyCRetryEnabled = true,
            StorageSavingsContract = AdaptiveStorageSavingsTests.Contract
        });
        int callbackCount = 0;
        var callbacks = new EncodeExecutionCallbacks { ExecutionOutcome = _ =>
        {
            callbackCount++;
            Assert.Empty(statistics.GetAll());
            Assert.Empty(history.LoadAll());
        } };
        if (failure is null)
        {
            await orchestrator.ExecuteAsync(attempt, callbacks, null, CancellationToken.None);
            Assert.True(orchestrator.RecordSuccessfulExecution(attempt, Now, 800_000, 75, "success", null, false, null));
            Assert.False(orchestrator.RecordSuccessfulExecution(attempt, Now, 800_000, 75, "repeat", null, false, null));
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteAsync(attempt, callbacks, null, CancellationToken.None));
            Assert.True(orchestrator.RecordFailedExecution(attempt, Now, terminal == EncodingTerminalResult.Canceled,
                terminal == EncodingTerminalResult.ValidationFailed ? EncodeFinalizationFailureKind.Validation : null,
                "", 75, "terminal failure", null, retryQueued: false));
            Assert.False(orchestrator.RecordFailedExecution(attempt, Now, false, null, "", 75, "repeat", null, retryQueued: false));
        }
        Assert.Equal(2, callbackCount); // initial rejection and later authoritative terminal callback
        var stored = Assert.Single(new EncodingStatisticsService(statisticsPath).GetAll());
        Assert.Equal(terminal.ToString(), stored.TerminalResult);
        Assert.Equal(2, stored.ProductionEncodeCount);
        Assert.Equal(75 - plan.AdaptiveSelection!.SamplingSeconds, stored.ProcessingSeconds);
        Assert.Equal("27", stored.PredictionQuality);
        BoundedRetryPersistenceTests.AssertTrace(trace, stored.AdaptiveStorageSavingsRetry!);
        history.AppendEncodingOutcome(new() { Id = attempt.Snapshot.OperationId,
            Status = terminal == EncodingTerminalResult.Canceled ? JobStatus.Canceled : JobStatus.Failed,
            AdaptiveSelection = plan.AdaptiveSelection }, attempt.ExecutionOutcome);
        var job = Assert.Single(new HistoryService(Path.Combine(_root, "retry-history.json")).LoadAll());
        Assert.Equal(2, job.ProductionEncodeCount);
        Assert.Equal(terminal, job.TerminalResult);
        BoundedRetryPersistenceTests.AssertTrace(trace, job.AdaptiveStorageSavingsRetry!);
        Assert.Empty(journal.ReadEvents());
        Assert.Null(stored.SourceAdaptiveShadow);
        Assert.False(stored.RecoveredSuccessful);
    }

    [Fact]
    public void RetryOutcomeCannotFabricateSourceAdaptiveOrResearchObservationEvenWithShadowPlan()
    {
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "blocked-shadow.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "blocked-statistics.jsonl"));
        var trace = BoundedRetryEvidence.Trace();
        var plan = MakePlan().Plan;
        var orchestrator = CreateOrchestrator(new SyntheticExecutor(new(plan.PlanId, plan)), journal, statistics);
        orchestrator.RecordEncodingStatistics("retry", Now, Now, EncodingStatisticsOutcome.Success,
            "source", "output", "hevc_nvenc", "nvenc", 1_000_000, 800_000, 120, 60,
            predictionPlan: plan, recoveredSuccessful: true, executionOutcome:
            new(plan.PlanId, [], [], TerminalResult: EncodingTerminalResult.Completed, ProductionEncodeCount: 2)
            { AdaptiveStorageSavingsRetry = trace });
        Assert.Null(Assert.Single(statistics.GetAll()).SourceAdaptiveShadow);
        Assert.False(Assert.Single(statistics.GetAll()).RecoveredSuccessful);
        Assert.Empty(journal.ReadEvents());
    }

    [Fact]
    public void ExternalEngineOutcomePersistsExactlyOneHistoryAndStatisticsRecord()
    {
        string source = CreateFile("external-engine-source.mp4");
        string historyPath = Path.Combine(_root, "external-engine-history", "history.json");
        var statistics = new EncodingStatisticsService(Path.Combine(_root, "external-engine-statistics.jsonl"));
        var orchestrator = CreateOrchestrator(
            new SyntheticExecutor(MakePlan()),
            new PredictionShadowObservationJournal(Path.Combine(_root, "external-engine-research.jsonl")),
            statistics);
        var lifecycle = new EncodeExecutionLifecycleService(orchestrator, () => new HistoryService(historyPath));
        string id = Guid.NewGuid().ToString("N");
        var history = new JobHistoryRecord
        {
            Id = id,
            Type = JobType.Encode,
            Status = JobStatus.Success,
            StartUtc = Now,
            EndUtc = Now.AddSeconds(2),
            SourcePath = source,
            OutputPath = Path.Combine(_root, "external-engine-output.mp4"),
            ProductionEncodeCount = 1,
            TerminalResult = EncodingTerminalResult.Completed
        };
        var statisticsRecord = new EncodingStatisticsRecord
        {
            Id = id,
            StartUtc = Now,
            EndUtc = Now.AddSeconds(2),
            Outcome = EncodingStatisticsOutcome.Success,
            SourcePath = source,
            OutputPath = history.OutputPath,
            Codec = "hevc",
            Encoder = "libx265",
            ProductionEncodeCount = 1,
            ProcessingSeconds = 2
        };

        EncodeExecutionLifecycleCompletion first = lifecycle.RecordExternalExecution(history, statisticsRecord);
        EncodeExecutionLifecycleCompletion duplicate = lifecycle.RecordExternalExecution(history, statisticsRecord);

        Assert.True(first.HistoryRecorded);
        Assert.True(first.StatisticsRecorded);
        Assert.True(duplicate.HistoryRecorded);
        Assert.False(duplicate.StatisticsRecorded);
        JobHistoryRecord persistedHistory = Assert.Single(new HistoryService(historyPath).LoadAll());
        Assert.Equal(1, persistedHistory.ProductionEncodeCount);
        Assert.Single(statistics.GetAll());
    }

    private EncodeExecutionOrchestrator CreateOrchestrator(
        IEncodeRequestExecutor executor,
        PredictionShadowObservationJournal journal,
        EncodingStatisticsService statistics,
        PredictionShadowExperimentFreezeStore? freezeStore = null)
    {
        string ffmpeg = CreateFile("ffmpeg.exe");
        var sampler = new PredictionShadowComplexitySamplingService(ffmpeg, new PgmWritingRunner());
        var shadow = new NvencQualityModePredictionShadowService(journal, sampler, utcNow: () => Now);
        return new EncodeExecutionOrchestrator(
            executor, shadow, statistics, hardwareKey: () => "synthetic-gpu",
            experimentFreezeStore: freezeStore);
    }

    private async Task AssertAssignedRejected(
        string currentSource,
        PredictionShadowExperimentAssignmentBinding binding,
        PredictionShadowExperimentFreezeStore freezeStore,
        string reason)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, $"rejected-{suffix}.jsonl"));
        var statistics = new EncodingStatisticsService(Path.Combine(_root, $"rejected-statistics-{suffix}.jsonl"));
        var executor = new SyntheticExecutor(MakePlan());
        var orchestrator = CreateOrchestrator(executor, journal, statistics, freezeStore);
        EncodeExecutionAttempt attempt = orchestrator.CreateAttempt(Snapshot(currentSource, binding));

        EncodeExecutionAssignmentValidationException failure = await Assert.ThrowsAsync<EncodeExecutionAssignmentValidationException>(
            () => orchestrator.ExecuteAsync(attempt, new EncodeExecutionCallbacks(), null, CancellationToken.None));
        orchestrator.RecordFailedExecution(attempt, Now, false, EncodeFinalizationFailureKind.Validation,
            "", 0, failure.Message, null, retryQueued: false);

        Assert.Equal(0, executor.InvocationCount);
        Assert.Contains(reason, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(journal.ReadEvents());
        Assert.DoesNotContain(statistics.GetAll(), record => record.Outcome == EncodingStatisticsOutcome.Success);
        Assert.Single(statistics.GetAll());
        Assert.Equal(EncodingStatisticsOutcome.ValidationFailed, statistics.GetAll()[0].Outcome);
    }

    private PredictionShadowExperimentFreezeStore CreateFreezeStore(string targetSourcePath)
    {
        const string experimentId = "MF-ORCHESTRATOR-TEST";
        PredictionShadowFreezeSource Source(int number)
        {
            string path = number == 1 ? targetSourcePath : CreateFile($"freeze-source-{number}.mp4");
            var info = new FileInfo(path);
            string familyKey = number == 1
                ? NvencQualityModeVideoBitratePredictionService.GetSourceFamilyKey(
                    MakePlan().Plan.SourceAdaptiveShadow,
                    MakePlan().Plan.Source?.DurationSeconds,
                    path)
                : $"fixture-family-{number}";
            return new(number, path, info.Length, info.LastWriteTimeUtc.Ticks, familyKey);
        }

        var strata = Enum.GetValues<PredictionShadowExperimentStratum>();
        var ranks = new int[3];
        var targets = Enumerable.Range(1, 24).Select(slot =>
        {
            PredictionShadowExperimentStratum stratum = strata[(slot - 1) % 3];
            return new PredictionShadowFreezeTarget(
                slot, stratum, Source(slot), ++ranks[(int)stratum], PredictionShadowExperimentRole.Target);
        }).ToArray();
        var reserves = strata.SelectMany((stratum, index) => Enumerable.Range(1, 2).Select(order =>
            new PredictionShadowFreezeReserve(stratum, order, Source(25 + index * 2 + order - 1), 8 + order))).ToArray();
        var freeze = new PredictionShadowExperimentFreeze
        {
            SchemaVersion = 1,
            ExperimentId = experimentId,
            ProtocolRevision = "test-v1",
            FrozenUtc = Now,
            MediaFluxVersion = "1.7.3",
            GitCommit = new string('a', 40),
            Settings = new PredictionShadowFreezeSettings
            {
                SourceCodec = "h264", SourceWidth = 1920, SourceHeight = 1080,
                MinimumFps = 29.97003, MaximumFps = 30,
                QualityMode = "Automatic", QualityResolutionPolicy = "Source Adaptive",
                QualityTarget = "Balanced", ExpectedSourceAssessment = "HighQualitySource", ExpectedCq = 25,
                Encoder = "NVENC", OutputCodec = "HEVC", Preset = "p5", BitDepth = 10,
                EncoderSettingsSignature = "test-signature", AutoTargetSize = true,
                NoPerItemTargetSizeOverride = true, NoExplicitCqOverride = true,
                ConcurrencyPolicy = "Automatic NVENC; start one selected Target at a time",
                AutomaticNvencConcurrency = 2, StartOneTargetAtATime = true,
                AllowScaling = false, AllowRestoration = false,
                AllowFilteringOrMaterialTransformation = false,
                TransformationRestrictions = "No material transformations"
            },
            Comparators = new PredictionShadowFreezeComparators
            {
                K = 2,
                BaselineRatio = new("ratio-v1", "ratio definition"),
                BaselineDirect = new("direct-v1", "direct definition"),
                TemporalRatio = new("temporal-ratio-v1", "temporal ratio definition"),
                TemporalDirect = new("temporal-direct-v1", "temporal direct definition"),
                NoExtrapolation = true, NoWeighting = true, NoSpatialCorrection = true,
                NoFittedCoefficient = true, NoCqMixing = true,
                ChronologyAndCutoffRule = "Prior finalized observations only",
                FamilyIndependenceRule = "Independent source families",
                OtherRestrictions = "No changes to production predictors"
            },
            Strata =
            [
                new(PredictionShadowExperimentStratum.Low, 7_500_000, 9_000_000),
                new(PredictionShadowExperimentStratum.Control, 10_000_000, 12_000_000),
                new(PredictionShadowExperimentStratum.High, 13_000_000, 14_500_000)
            ],
            Targets = targets,
            Reserves = reserves,
            Exclusions = [new(Source(99), "Synthetic exclusion")],
            ReplacementPolicy = new PredictionShadowFreezeReplacementPolicy
            {
                InitialAttempt = 1, ReplacementAttempt = 2,
                MaximumReplacementsPerSlot = 1, MaximumReplacementsPerStratum = 2,
                SameStratumRequired = true, ConsumeReservesInOrder = true,
                PreserveExperimentSlotAndStratum = true, PreserveInvalidAttemptRecords = true,
                ReplacementRole = PredictionShadowExperimentRole.Replacement,
                ValidReasons = ["synthetic validation"],
                ProhibitedOutcomeBasedReasons = ["outcome preference"]
            },
            AcceptanceCriteria = new PredictionShadowFreezeAcceptanceCriteria
            {
                RequiredValidIndependentOutcomes = 24, RequiredPostBootstrapBaselineAvailability = 23,
                MinimumJointlySupportedTemporalTargets = 15, MinimumJointlySupportedTargetsPerStratum = 4,
                ApeDefinition = "APE", P90Definition = "P90", JointlySupportedSetRule = "Joint set",
                DirectComparisonAppliesToBaselineAndTemporal = true,
                NoUnexplainedCompatibilityOrInfrastructureAbstentions = true,
                MaximumTemporalRatioMedianApePercent = 20, MaximumTemporalRatioP90ApePercent = 30,
                MaximumTemporalDirectMedianApePercent = 20, MaximumTemporalDirectP90ApePercent = 30,
                MinimumMeanApeImprovementPercentagePoints = 2, MinimumMeanApeImprovementRelativePercent = 10,
                TemporalMedianMustNotWorsen = true, DirectMeanMustBeStrictlyLowerThanRatio = true,
                DirectMedianMustBeNoHigherThanRatio = true, MaximumSupportedTemporalApePercent = 50
            },
            JournalSnapshot =
            [
                new(PredictionShadowFreezeJournalType.ResearchShadowObservations, 0, Path.Combine(_root, "freeze-research.jsonl"), 0, new string('a', 64)),
                new(PredictionShadowFreezeJournalType.FinalizedStatistics, 0, Path.Combine(_root, "freeze-statistics.jsonl"), 0, new string('b', 64))
            ]
        };
        var store = new PredictionShadowExperimentFreezeStore(_root);
        store.Create(freeze);
        return store;
    }

    private EncodeExecutionSnapshot Snapshot(
        string source,
        PredictionShadowExperimentAssignmentBinding? assignment) => new()
    {
        OperationId = Guid.NewGuid().ToString("N"),
        StatisticsStartUtc = Now.AddMinutes(-1),
        SourceFilePath = source,
        LogicalSourcePath = source,
        Input = EncodingInputSource.FromFile(source, knownAudioStreamCount: 1),
        OutputFolder = _root,
        Suffix = "_encoded",
        Encoder = new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"),
        UseGpu = true,
        ScaleMode = EncodingService.ScaleMode.None,
        Restoration = new VideoRestorationSettings(),
        EncoderPreset = "p5",
        QualityValue = 25,
        QualityIntent = EncodingQualityIntent.Automatic(QualityTarget.Balanced),
        TenBit = true,
        AudioChannels = 2,
        ConcurrentEncoderSessions = true,
        OutputContainer = OutputContainerSelection.Mp4,
        CompatibilityPolicy = ContainerCompatibilityPolicy.Intelligent,
        PredictionShadowExperimentAssignment = assignment,
        SourceSizeBytes = new FileInfo(source).Length,
        MediaDurationSeconds = 120,
        SourceHeight = 1080,
        OutputHeight = 1080,
        EncoderText = "NVENC HEVC p5",
        Codec = "hevc_nvenc",
        MediaFluxVersion = "1.7.3",
        StatisticsPath = Path.Combine(_root, "encoding-statistics.jsonl")
    };

    private static EncodingPlanSnapshot MakePlan()
    {
        var decision = new SourceAdaptiveShadowCalibration
        {
            Status = SourceAdaptiveShadowStatus.CalibrationCandidate,
            SourceCodec = "h264",
            OutputCodec = "hevc_nvenc",
            EncoderId = VideoEncoderIds.Nvenc,
            Preset = "p5",
            FinalExecutionCq = 25,
            SourceVideoBitrateKbps = 8000,
            SourceTotalBytes = 800_000_000,
            PlannedWidth = 1920,
            PlannedHeight = 1080,
            PlannedFps = 29.97002997002997,
            MaterialTransformationActive = false
        };
        var plan = new EncodingPlan
        {
            IsAvailable = true,
            PlanId = Guid.NewGuid(),
            Source = new EncodingPlanSource("h264", 1920, 1080, 29.97002997002997, 120)
            {
                SizeBytes = 800_000_000,
                BitrateKbps = 8000
            },
            Video = new EncodingPlanVideo("Reencode", "hevc_nvenc", VideoEncoderIds.Nvenc, 1920, 1080, 1920, 1080, "p010le"),
            Validation = new EncodingPlanValidation("Production", true, false),
            SourceAdaptiveShadow = decision
        };
        return new EncodingPlanSnapshot(plan.PlanId, plan);
    }

    private static PredictionShadowExperimentAssignment Assignment() => new(
        "MF-ORCHESTRATOR-TEST", 1, 1,
        PredictionShadowExperimentStratum.Low,
        PredictionShadowExperimentRole.Target);

    private static AdaptiveQualitySelectionEvidence AllMissEvidence()
    {
        RepresentativeSampleEvidence[] samples = StorageSavingsSampleSelector.Positions(TimeSpan.FromSeconds(120))
            .Select(sample => new RepresentativeSampleEvidence(sample, 4_000_000, 25))
            .ToArray();
        int[] qualities = [19, 20, 21];
        AdaptiveCandidateEvidence[] candidates = qualities
            .Select((quality, index) => new AdaptiveCandidateEvidence(
                quality, Array.AsReadOnly(samples), 300_000_000 + index, 400_000_000 + index, 4_000_000,
                AdaptiveSampleClassification.ClearlyMisses)
            { ProjectedTypicalBytes = 350_000_000 + index })
            .ToArray();
        return new AdaptiveQualitySelectionEvidence(
            AdaptiveSelectionDisposition.Skipped, VideoEncoderIds.Nvenc, "hevc_nvenc", QualityTarget.HighQuality,
            EncoderQualityMechanism.Cq, 19, null, 21, 2, 25, false,
            new AdaptiveAncillaryAllowance(0, 0, 0, 0), Array.AsReadOnly(candidates),
            "Every acceptable candidate clearly misses the storage objective.")
        { SamplingSeconds = 3, SourceDurationSeconds = 120 };
    }

    private string CreateFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "synthetic fixture");
        return path;
    }

    private sealed class SyntheticExecutor(
        EncodingPlanSnapshot plan,
        Exception? exceptionAfterCapture = null,
        bool finalizationSucceeded = true,
        bool reportRecoveredOutcome = false,
        StorageSavingsEvaluation? storageSavings = null,
        AdaptiveQualitySelectionEvidence? adaptive = null,
        bool skipBeforeEncode = false,
        EncodingTerminalResult? terminalResultBeforeFailure = null,
        bool failBeforeFfmpegLaunch = false,
        AdaptiveStorageSavingsRetryTrace? retryTrace = null) : IEncodeRequestExecutor
    {
        public List<string> Events { get; } = [];
        public EncodingRequest? Request { get; private set; }
        public EncodingPlanSnapshot Plan => plan;
        public int InvocationCount { get; private set; }

        public async Task<EncodingService.EncodeResult> EncodeWithResultAsync(EncodingRequest request)
        {
            InvocationCount++;
            Request = request;
            Events.Add("plan");
            request.EncodingPlanSnapshotCallback?.Invoke(plan);
            if (adaptive is not null) request.AdaptiveSelectionCallback?.Invoke(adaptive);
            if (skipBeforeEncode) throw new AdaptiveStorageSavingsSkippedException(adaptive!);
            request.PreEncodeExecutionValidationCallback?.Invoke(plan);
            Events.Add("capture");
            if (request.PreEncodeResearchCallback is { } capture)
            {
                await capture(plan, request.CancellationToken);
                // A repeated callback must not create a second Frozen event.
                await capture(plan, request.CancellationToken);
            }
            if (reportRecoveredOutcome)
            {
                request.EncodingExecutionOutcomeCallback?.Invoke(new EncodingExecutionOutcome(
                    plan.PlanId, [], [], TerminalResult: EncodingTerminalResult.CompletedAfterRecovery));
            }
            if (failBeforeFfmpegLaunch && exceptionAfterCapture is not null)
            {
                request.EncodingExecutionOutcomeCallback?.Invoke(new EncodingExecutionOutcome(
                    plan.PlanId, [], [], TerminalResult: EncodingTerminalResult.NotRun));
                throw exceptionAfterCapture;
            }
            Events.Add("encode");
            if (retryTrace is not null)
            {
                request.EncodingExecutionOutcomeCallback?.Invoke(new(plan.PlanId, [], [],
                    TerminalResult: EncodingTerminalResult.StoragePolicyRejected,
                    StorageSavings: retryTrace.Attempts[0].PhaseOneResult, ProductionEncodeCount: 1));
                request.EncodingExecutionOutcomeCallback?.Invoke(new(plan.PlanId, [], [],
                    TerminalResult: retryTrace.LogicalTerminalResult,
                    StorageSavings: retryTrace.Attempts[1].PhaseOneResult, ProductionEncodeCount: 2)
                    { AdaptiveStorageSavingsRetry = retryTrace });
            }
            if (exceptionAfterCapture is not null)
            {
                if (terminalResultBeforeFailure is { } terminal)
                    request.EncodingExecutionOutcomeCallback?.Invoke(new EncodingExecutionOutcome(
                        plan.PlanId, [], [], TerminalResult: terminal));
                throw exceptionAfterCapture;
            }
            return new EncodingService.EncodeResult(
                success: true,
                outputPath: Path.Combine(Path.GetTempPath(), "synthetic-final.mp4"),
                finalizationSucceeded: finalizationSucceeded,
                validationSummary: "Synthetic validated output.",
                finalOutputSizeBytes: storageSavings?.CandidateOutputBytes ?? 123_456_789,
                requestedOutputContainer: OutputContainerSelection.Mp4,
                resolvedOutputContainer: OutputContainer.Mp4,
                storageSavingsContract: request.StorageSavingsContract,
                storageSavings: storageSavings);
        }
    }

    private sealed class PgmWritingRunner : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string pattern = request.Arguments[^1];
            for (int frame = 1; frame <= 5; frame++)
            {
                string path = pattern.Replace("%03d", frame.ToString("D3", CultureInfo.InvariantCulture), StringComparison.Ordinal);
                byte[] pixels = new byte[16];
                for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    pixels[y * 4 + x] = (byte)Math.Clamp(x * 55 + frame * 5, 0, 255);
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("P5\n4 4\n255\n").Concat(pixels).ToArray());
            }
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
