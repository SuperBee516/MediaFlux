using System.Diagnostics;
using MediaFlux.Services;

namespace MediaFlux.Services.EncodingV2;

/// <summary>The V2 lifecycle owner. It contains no GUI, policy, retry, statistics, or V1 orchestration dependency.</summary>
public sealed class V2EncodingCore
{
    private readonly IV2SourceMetadataProvider _metadataProvider;
    private readonly V2EncodePlanner _planner;
    private readonly IV2ProductionExecutor _executor;
    private readonly IV2CandidateValidator _validator;
    private readonly IV2OutputPromoter _promoter;
    private readonly string _ffmpegPath;

    public V2EncodingCore(
        IV2SourceMetadataProvider metadataProvider,
        V2EncodePlanner planner,
        IV2ProductionExecutor executor,
        IV2CandidateValidator validator,
        IV2OutputPromoter promoter,
        string ffmpegPath)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _promoter = promoter ?? throw new ArgumentNullException(nameof(promoter));
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
    }

    public Task<V2MediaFacts> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        _metadataProvider.ProbeAsync(sourcePath, cancellationToken);

    public FrozenEncodePlan CreatePlan(EncodingV2Request request, V2MediaFacts source) =>
        _planner.CreatePlan(request, source, _ffmpegPath);

    public async Task<FrozenEncodePlan> CreatePlanAsync(
        EncodingV2Request request,
        CancellationToken cancellationToken = default)
    {
        V2MediaFacts source = await _metadataProvider.ProbeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
        return _planner.CreatePlan(request, source, _ffmpegPath);
    }

    public async Task<V2ExecutionResult> RunAsync(
        EncodingV2Request request,
        Action<V2LifecycleEvent>? eventObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        long totalStartedAt = Stopwatch.GetTimestamp();
        string jobId = Guid.TryParse(request.JobId, out Guid requestedId)
            ? requestedId.ToString("N")
            : Guid.NewGuid().ToString("N");
        EncodingV2Request runRequest = CopyWithOperationId(request, jobId);
        var events = new List<V2LifecycleEvent>();
        var warnings = new List<string>();
        int sequence = 0;
        bool stageOwned = false;
        int productionAttempts = 0;
        int? ffmpegExit = null;
        FrozenEncodePlan? plan = null;
        V2MediaFacts? source = null;
        V2Failure? failure = null;
        V2LifecycleState currentState = V2LifecycleState.Queued;
        bool outputValidated = false;
        bool outputPromoted = false;
        long? outputSizeBytes = null;
        TimeSpan probeTime = TimeSpan.Zero;
        TimeSpan planTime = TimeSpan.Zero;
        TimeSpan executeRequestToStart = TimeSpan.Zero;
        TimeSpan encodeTime = TimeSpan.Zero;
        TimeSpan validateTime = TimeSpan.Zero;
        TimeSpan promoteTime = TimeSpan.Zero;
        TimeSpan cleanupTime = TimeSpan.Zero;
        bool? sourceDeleted = null;

        void Emit(V2LifecycleState state, string stage, string message, double? elapsedMs = null,
            int? exitCode = null, string? failureCode = null)
        {
            currentState = state;
            var item = new V2LifecycleEvent(1, jobId, ++sequence, DateTime.UtcNow,
                state, stage, message, elapsedMs, exitCode, failureCode,
                plan?.SourcePath ?? runRequest.SourcePath,
                plan?.OutputPath ?? runRequest.OutputPath,
                plan?.StagingPath,
                plan?.Encoder ?? runRequest.Encoder,
                plan?.QualityMode ?? runRequest.QualityMode,
                plan?.QualityValue ?? runRequest.QualityValue);
            events.Add(item);
            try { eventObserver?.Invoke(item); }
            catch { /* Lifecycle observers cannot make or change production decisions. */ }
        }

        Emit(V2LifecycleState.Queued, "job.queued", "V2 job accepted.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Emit(V2LifecycleState.Probing, "source.probe.started", "Probing source metadata.");
            long probeStartedAt = Stopwatch.GetTimestamp();
            source = await _metadataProvider.ProbeAsync(runRequest.SourcePath, cancellationToken).ConfigureAwait(false);
            probeTime = Stopwatch.GetElapsedTime(probeStartedAt);
            Emit(V2LifecycleState.Probing, "source.probe.completed", "Source metadata captured.", probeTime.TotalMilliseconds);

            cancellationToken.ThrowIfCancellationRequested();
            long planStartedAt = Stopwatch.GetTimestamp();
            plan = _planner.CreatePlan(runRequest, source, _ffmpegPath);
            planTime = Stopwatch.GetElapsedTime(planStartedAt);
            jobId = plan.JobId;
            Emit(V2LifecycleState.Planned, "plan.frozen", "Frozen FFmpeg invocation is ready.", planTime.TotalMilliseconds);

            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceUnchanged(plan);
            EnsureDestinationStillAbsent(plan);
            using (var stage = new FileStream(plan.StagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stageOwned = true;
                stage.Flush(flushToDisk: true);
            }

            Emit(V2LifecycleState.Encoding, "ffmpeg.process.launching", "Starting the single production FFmpeg invocation.");
            productionAttempts = 1;
            long encodeStartedAt = Stopwatch.GetTimestamp();
            V2ProcessOutcome process = await _executor.ExecuteAsync(plan, started =>
            {
                executeRequestToStart = started;
                Emit(V2LifecycleState.Encoding, "ffmpeg.process.started", "Production FFmpeg process started.", started.TotalMilliseconds);
            }, cancellationToken).ConfigureAwait(false);
            encodeTime = Stopwatch.GetElapsedTime(encodeStartedAt);
            executeRequestToStart = process.ExecuteRequestToProcessStart;
            ffmpegExit = process.ExitCode;
            Emit(V2LifecycleState.Encoding, "ffmpeg.process.completed",
                process.TimedOut ? "Production FFmpeg invocation timed out." : $"Production FFmpeg invocation exited with code {process.ExitCode}.",
                encodeTime.TotalMilliseconds, process.ExitCode,
                process.TimedOut ? "FfmpegTimeout" : process.ExitCode == 0 ? null : "FfmpegExitCode");
            if (process.TimedOut)
                throw new V2ExecutionException("FfmpegTimeout", "The production FFmpeg invocation timed out.", process.ExitCode);
            if (process.ExitCode != 0)
            {
                string detail = LastUsefulLine(process.StandardError);
                throw new V2ExecutionException("FfmpegExitCode",
                    $"The production FFmpeg invocation failed with exit code {process.ExitCode}" +
                    (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"), process.ExitCode);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceUnchanged(plan);
            Emit(V2LifecycleState.Validating, "output.validation.started", "Validating staged output against the frozen plan.");
            long validationStartedAt = Stopwatch.GetTimestamp();
            V2ValidationOutcome validation = await _validator.ValidateAsync(
                plan, source, plan.StagingPath, cancellationToken).ConfigureAwait(false);
            validateTime = Stopwatch.GetElapsedTime(validationStartedAt);
            Emit(V2LifecycleState.Validating, "output.validation.completed", validation.Summary,
                validateTime.TotalMilliseconds, failureCode: validation.Success ? null : validation.ErrorCode);
            if (!validation.Success)
                throw new V2ExecutionException(validation.ErrorCode, validation.Summary);
            if (validation.OutputLength <= 0 || !File.Exists(plan.StagingPath))
                throw new V2ExecutionException("ValidationEvidenceMissing", "The validator did not establish a stable staged output identity.");
            EnsureValidatedStageIdentity(plan.StagingPath, validation);
            outputValidated = true;

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationStillAbsent(plan);
            Emit(V2LifecycleState.Promoting, "output.promotion.started", "Promoting the validated staging file.");
            long promoteStartedAt = Stopwatch.GetTimestamp();
            try { _promoter.Promote(plan.StagingPath, plan.OutputPath); }
            catch (Exception ex)
            {
                string code = File.Exists(plan.OutputPath) || Directory.Exists(plan.OutputPath)
                    ? "DestinationExists"
                    : "PromotionFailed";
                throw new V2ExecutionException(code, "Validated output promotion failed: " + ex.Message);
            }
            promoteTime = Stopwatch.GetElapsedTime(promoteStartedAt);
            stageOwned = false;
            outputPromoted = true;
            EnsurePromotedOutputIdentity(plan.OutputPath, validation);
            outputSizeBytes = validation.OutputLength;
            Emit(V2LifecycleState.Promoting, "output.promotion.completed", "Validated output was promoted without overwrite.", promoteTime.TotalMilliseconds);

            cancellationToken.ThrowIfCancellationRequested();
            if (plan.DeleteSourceOnSuccess)
            {
                Emit(V2LifecycleState.Promoting, "source.deletion.requested", "Source deletion was requested after successful validation and promotion.");
                sourceDeleted = TryDeleteSourceAfterPromotion(plan, validation, cancellationToken, out string deletionMessage);
                if (!sourceDeleted.Value) warnings.Add(deletionMessage);
                Emit(V2LifecycleState.Promoting,
                    sourceDeleted.Value ? "source.deletion.completed" : "source.deletion.warning",
                    deletionMessage, failureCode: sourceDeleted.Value ? null : "SourceDeletionFailed");
            }
            else
            {
                sourceDeleted = false;
                Emit(V2LifecycleState.Promoting, "source.deletion.not-requested", "Source retained because deletion was not requested.");
            }

            Emit(V2LifecycleState.Completed, "job.completed", "V2 job completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new V2Failure("Canceled", "The V2 job was canceled; the source was retained.");
            if ((plan?.DeleteSourceOnSuccess ?? runRequest.DeleteSourceOnSuccess) && sourceDeleted is null)
                sourceDeleted = false;
            int priorWarningCount = warnings.Count;
            cleanupTime = CleanupOwnedStage(plan, stageOwned, warnings);
            stageOwned = false;
            Emit(currentState, "staging.cleanup.completed",
                warnings.Count == priorWarningCount ? "Owned staging cleanup completed or no owned artifact remained." : warnings[^1],
                cleanupTime.TotalMilliseconds,
                failureCode: warnings.Count == priorWarningCount ? null : "CleanupFailed");
            Emit(V2LifecycleState.Canceled, "job.canceled", failure.Message, failureCode: failure.Code);
        }
        catch (Exception ex)
        {
            failure = ex is V2ExecutionException known
                ? new V2Failure(known.Code, known.Message, known.ProcessExitCode)
                : ex is V2PlanningException planning
                    ? new V2Failure(planning.Code, planning.Message)
                    : new V2Failure("ExecutionFailed", ex.Message);
            if ((plan?.DeleteSourceOnSuccess ?? runRequest.DeleteSourceOnSuccess) && sourceDeleted is null)
                sourceDeleted = false;
            int priorWarningCount = warnings.Count;
            cleanupTime = CleanupOwnedStage(plan, stageOwned, warnings);
            stageOwned = false;
            Emit(currentState, "staging.cleanup.completed",
                warnings.Count == priorWarningCount ? "Owned staging cleanup completed or no owned artifact remained." : warnings[^1],
                cleanupTime.TotalMilliseconds,
                failureCode: warnings.Count == priorWarningCount ? null : "CleanupFailed");
            Emit(V2LifecycleState.Failed, "job.failed", failure.Message,
                exitCode: failure.ProcessExitCode, failureCode: failure.Code);
        }

        TimeSpan total = Stopwatch.GetElapsedTime(totalStartedAt);
        return new V2ExecutionResult
        {
            JobId = plan?.JobId ?? jobId,
            State = currentState,
            SourcePath = plan?.SourcePath ?? runRequest.SourcePath,
            OutputPath = plan?.OutputPath ?? runRequest.OutputPath,
            StagingPath = plan?.StagingPath ?? "",
            OutputValidated = outputValidated,
            OutputPromoted = outputPromoted,
            OutputSizeBytes = outputSizeBytes,
            SourceDeletionRequested = plan?.DeleteSourceOnSuccess ?? runRequest.DeleteSourceOnSuccess,
            SourceDeleted = sourceDeleted,
            Failure = failure,
            Warnings = Array.AsReadOnly(warnings.ToArray()),
            Events = Array.AsReadOnly(events.ToArray()),
            FfmpegExitCode = ffmpegExit,
            ProductionAttemptCount = productionAttempts,
            Timings = new V2StageDurations(
                probeTime.TotalMilliseconds,
                planTime.TotalMilliseconds,
                executeRequestToStart.TotalMilliseconds,
                encodeTime.TotalMilliseconds,
                validateTime.TotalMilliseconds,
                promoteTime.TotalMilliseconds,
                cleanupTime.TotalMilliseconds,
                total.TotalMilliseconds)
        };
    }

    private static void EnsureSourceUnchanged(FrozenEncodePlan plan)
    {
        FileInfo source = new(plan.SourcePath);
        if (!source.Exists || source.Length != plan.SourceLength ||
            source.LastWriteTimeUtc.Ticks != plan.SourceLastWriteUtcTicks)
            throw new V2ExecutionException("SourceChanged", "The source changed after planning; it was preserved.");
    }

    private static void EnsureDestinationStillAbsent(FrozenEncodePlan plan)
    {
        if (File.Exists(plan.OutputPath) || Directory.Exists(plan.OutputPath))
            throw new V2ExecutionException("DestinationExists", "The final destination exists; V2 will not overwrite it.");
    }

    private static void EnsureValidatedStageIdentity(string path, V2ValidationOutcome validation)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length != validation.OutputLength ||
            file.LastWriteTimeUtc.Ticks != validation.OutputLastWriteUtcTicks)
            throw new V2ExecutionException("CandidateChangedAfterValidation", "The staged candidate changed after validation.");
    }

    private static void EnsurePromotedOutputIdentity(string path, V2ValidationOutcome validation)
    {
        FileInfo file = new(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.Length != validation.OutputLength ||
            file.LastWriteTimeUtc.Ticks != validation.OutputLastWriteUtcTicks)
            throw new V2ExecutionException("PromotedOutputChanged", "The final output could not be confirmed after promotion; the source was retained.");
    }

    private static bool TryDeleteSourceAfterPromotion(FrozenEncodePlan plan,
        V2ValidationOutcome validation, CancellationToken cancellationToken, out string message)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePromotedOutputIdentity(plan.OutputPath, validation);
            FileInfo source = new(plan.SourcePath);
            if (!source.Exists)
            {
                message = "Source deletion was requested, but the source no longer exists.";
                return false;
            }
            if (source.Length != plan.SourceLength || source.LastWriteTimeUtc.Ticks != plan.SourceLastWriteUtcTicks)
            {
                message = "Source was retained because it changed after planning.";
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFullPath(plan.SourcePath), Path.GetFullPath(plan.OutputPath), StringComparison.OrdinalIgnoreCase))
            {
                message = "Source was retained because the final output resolves to the source path.";
                return false;
            }
            File.Delete(plan.SourcePath);
            if (File.Exists(plan.SourcePath))
            {
                message = "Source deletion was requested, but the source still exists.";
                return false;
            }
            message = "Source deleted after validated promotion and final-output verification.";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            message = "Valid output was retained; source deletion did not complete: " + ex.Message;
            return false;
        }
    }

    private static TimeSpan CleanupOwnedStage(FrozenEncodePlan? plan, bool stageOwned, List<string> warnings)
    {
        if (!stageOwned || plan is null) return TimeSpan.Zero;
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            FileInfo stage = new(plan.StagingPath);
            if (!stage.Exists) return Stopwatch.GetElapsedTime(startedAt);
            if ((stage.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                warnings.Add("Owned staging cleanup was skipped because the path no longer names a regular file.");
                return Stopwatch.GetElapsedTime(startedAt);
            }
            File.Delete(plan.StagingPath);
        }
        catch (Exception ex) { warnings.Add("Owned staging cleanup failed: " + ex.Message); }
        return Stopwatch.GetElapsedTime(startedAt);
    }

    private static string LastUsefulLine(string stderr) =>
        (stderr ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";

    private static EncodingV2Request CopyWithOperationId(EncodingV2Request request, string jobId) => new()
    {
        SchemaVersion = request.SchemaVersion,
        JobId = string.IsNullOrWhiteSpace(request.JobId) ? jobId : request.JobId,
        SourcePath = request.SourcePath,
        OutputPath = request.OutputPath,
        Encoder = request.Encoder,
        Codec = request.Codec,
        Container = request.Container,
        QualityMode = request.QualityMode,
        QualityValue = request.QualityValue,
        Preset = request.Preset,
        PixelFormat = request.PixelFormat,
        GeometryMode = request.GeometryMode,
        MaxWidth = request.MaxWidth,
        MaxHeight = request.MaxHeight,
        AudioMode = request.AudioMode,
        AudioBitrateKbps = request.AudioBitrateKbps,
        SubtitleMode = request.SubtitleMode,
        DeleteSourceOnSuccess = request.DeleteSourceOnSuccess,
        FfmpegPath = request.FfmpegPath,
        FfprobePath = request.FfprobePath,
        EventLogPath = request.EventLogPath
    };

    private sealed class V2ExecutionException(string code, string message, int? processExitCode = null)
        : InvalidOperationException(message)
    {
        public string Code { get; } = code;
        public int? ProcessExitCode { get; } = processExitCode;
    }
}
