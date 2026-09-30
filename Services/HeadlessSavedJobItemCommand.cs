using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

public enum HeadlessSavedJobMode { Preflight, Run }

public enum HeadlessSavedJobExitCode
{
    Success = 0,
    CommandOrSelectorError = 2,
    PreflightOrValidationRejected = 3,
    EncodeFailure = 4,
    Cancelled = 5
}

public sealed record HeadlessSavedJobCommand(
    HeadlessSavedJobMode Mode,
    Guid JobId,
    string ItemSelector)
{
    public static bool TryParse(string[] args, out HeadlessSavedJobCommand? command, out string error)
    {
        command = null;
        error = "";
        if (args.Length == 0 || args[0] is not ("--run-saved-job-item" or "--preflight-saved-job-item"))
        {
            error = "Expected --run-saved-job-item or --preflight-saved-job-item.";
            return false;
        }
        if (args.Length != 4 || args[2] != "--item")
        {
            error = "Syntax: MediaFlux.exe (--run-saved-job-item|--preflight-saved-job-item) <job-id> --item <selector>. Exactly one item selector is required.";
            return false;
        }
        if (!Guid.TryParse(args[1], out Guid jobId))
        {
            error = "The saved-job ID must be a GUID.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(args[3]))
        {
            error = "Exactly one non-empty item selector is required.";
            return false;
        }
        command = new HeadlessSavedJobCommand(
            args[0] == "--run-saved-job-item" ? HeadlessSavedJobMode.Run : HeadlessSavedJobMode.Preflight,
            jobId,
            args[3]);
        return true;
    }
}

public sealed record HeadlessSavedJobSelection(EncodeJob Job, EncodeJobFile Item);

public static class HeadlessSavedJobSelector
{
    public static HeadlessSavedJobSelection Select(
        IEnumerable<EncodeJob> jobs,
        HeadlessSavedJobCommand command)
    {
        EncodeJob[] matchingJobs = jobs.Where(job => job.Id == command.JobId).ToArray();
        if (matchingJobs.Length != 1)
            throw new HeadlessSavedJobSelectionException(matchingJobs.Length == 0
                ? $"No saved job has ID {command.JobId}."
                : $"Saved job ID {command.JobId} is ambiguous ({matchingJobs.Length} matching records).");

        EncodeJob job = matchingJobs[0];
        EncodeJobFile[] matchingItems = job.Files.Where(item => Matches(item, command.ItemSelector)).ToArray();
        if (matchingItems.Length != 1)
            throw new HeadlessSavedJobSelectionException(matchingItems.Length == 0
                ? $"No item in saved job '{job.Name}' matches selector '{command.ItemSelector}'."
                : $"Selector '{command.ItemSelector}' matches {matchingItems.Length} items in saved job '{job.Name}'; selection must be unique.");
        return new HeadlessSavedJobSelection(job, matchingItems[0]);
    }

    private static bool Matches(EncodeJobFile item, string selector)
    {
        if (selector.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
        {
            string path = selector[5..];
            if (string.IsNullOrWhiteSpace(path))
                throw new HeadlessSavedJobSelectionException("A path selector must include a full source path.");
            if (!Path.IsPathFullyQualified(path))
                throw new HeadlessSavedJobSelectionException("A path selector must be an absolute persisted source path.");
            try
            {
                return string.Equals(Path.GetFullPath(path), Path.GetFullPath(item.SourcePath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new HeadlessSavedJobSelectionException($"The path selector is invalid: {ex.Message}");
            }
        }

        if (!selector.StartsWith("experiment=", StringComparison.OrdinalIgnoreCase))
            throw new HeadlessSavedJobSelectionException("Use path=<full-source-path> or experiment=<id>,slot=<n>[,attempt=<n>] as the item selector.");

        string? experimentId = null;
        int? slot = null;
        int? attempt = null;
        foreach (string part in selector.Split(','))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[1]))
                throw new HeadlessSavedJobSelectionException("Experiment selector fields must use key=value form.");
            switch (pair[0].Trim().ToLowerInvariant())
            {
                case "experiment" when experimentId is null: experimentId = pair[1].Trim(); break;
                case "slot" when slot is null && int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSlot) && parsedSlot > 0: slot = parsedSlot; break;
                case "attempt" when attempt is null && int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedAttempt) && parsedAttempt > 0: attempt = parsedAttempt; break;
                default: throw new HeadlessSavedJobSelectionException($"Invalid or repeated experiment selector field '{pair[0]}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(experimentId) || slot is null)
            throw new HeadlessSavedJobSelectionException("Experiment selectors require experiment=<id> and slot=<positive integer>.");
        PredictionShadowExperimentAssignment? assignment = item.PredictionShadowExperimentAssignment?.Assignment;
        return assignment is not null &&
            string.Equals(assignment.ExperimentId, experimentId, StringComparison.Ordinal) &&
            assignment.Slot == slot &&
            (!attempt.HasValue || assignment.Attempt == attempt.Value);
    }
}

public sealed class HeadlessSavedJobSelectionException(string message) : InvalidOperationException(message);

public sealed record HeadlessSavedJobReport(
    HeadlessSavedJobExitCode ExitCode,
    string Message,
    string? JobName = null,
    string? SourcePath = null,
    EncodeExecutionSnapshot? Snapshot = null);

/// <summary>One-item command flow. It has no queue loop and can call the pipeline once at most.</summary>
public static class HeadlessSavedJobItemRunner
{
    public static async Task<HeadlessSavedJobReport> RunAsync(
        HeadlessSavedJobCommand command,
        IEnumerable<EncodeJob> jobs,
        IHeadlessSavedJobItemPipeline pipeline,
        Action<string> writeLine,
        CancellationToken cancellationToken)
    {
        HeadlessSavedJobSelection selection;
        try { selection = HeadlessSavedJobSelector.Select(jobs, command); }
        catch (HeadlessSavedJobSelectionException ex)
        {
            writeLine("Selection rejected: " + ex.Message);
            return new(HeadlessSavedJobExitCode.CommandOrSelectorError, ex.Message);
        }

        EncodeJob executionJob = EncodeJobService.CreateExecutionSnapshot(selection.Job);
        EncodeJobFile executionItem = executionJob.Files[selection.Job.Files.IndexOf(selection.Item)];
        EncodeExecutionSnapshot snapshot;
        try { snapshot = await pipeline.BuildSnapshotAsync(executionJob, executionItem, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writeLine("Terminal result: cancelled during source inspection or estimate preflight.");
            return new(HeadlessSavedJobExitCode.Cancelled, "Preflight cancelled.", executionJob.Name, executionItem.SourcePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            writeLine("Snapshot rejected: " + ex.Message);
            return new(HeadlessSavedJobExitCode.PreflightOrValidationRejected, ex.Message, executionJob.Name, executionItem.SourcePath);
        }

        WriteSnapshot(writeLine, executionJob, executionItem, snapshot);
        try
        {
            if (command.Mode == HeadlessSavedJobMode.Preflight)
            {
                pipeline.ValidateForPreflight(snapshot);
                writeLine("Preflight passed. No Frozen, Outcome, statistics, or encode was produced.");
                return new(HeadlessSavedJobExitCode.Success, "Preflight passed.", executionJob.Name, executionItem.SourcePath, snapshot);
            }

            string terminal = await pipeline.ExecuteOneAsync(snapshot, cancellationToken).ConfigureAwait(false);
            writeLine("Terminal encode result: " + terminal);
            return new(HeadlessSavedJobExitCode.Success, terminal, executionJob.Name, executionItem.SourcePath, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writeLine("Terminal encode result: cancelled.");
            return new(HeadlessSavedJobExitCode.Cancelled, "Execution cancelled.", executionJob.Name, executionItem.SourcePath, snapshot);
        }
        catch (Exception ex)
        {
            HeadlessSavedJobExitCode code = ex is HeadlessSavedJobValidationException or EncodeExecutionAssignmentValidationException
                ? HeadlessSavedJobExitCode.PreflightOrValidationRejected
                : HeadlessSavedJobExitCode.EncodeFailure;
            writeLine($"Terminal encode result: {code}; {ex.Message}");
            return new(code, ex.Message, executionJob.Name, executionItem.SourcePath, snapshot);
        }
    }

    private static void WriteSnapshot(Action<string> write, EncodeJob job, EncodeJobFile item, EncodeExecutionSnapshot snapshot)
    {
        PredictionShadowExperimentAssignment? assignment = snapshot.PredictionShadowExperimentAssignment?.Assignment;
        string quality = snapshot.QualityIntent is { } intent
            ? $"Automatic / {intent.Target} (CQ resolved by EncodingService)"
            : $"Manual {snapshot.QualityValue?.ToString(CultureInfo.InvariantCulture) ?? "unset"}";
        string targetSize = snapshot.TargetMb is > 0
            ? $"fixed {snapshot.TargetMb.Value.ToString("0.###", CultureInfo.InvariantCulture)} MiB"
            : snapshot.QualityIntent is not null ? "automatic best target size" : "quality-based / no fixed target";
        write($"Job: {job.Name} ({job.Id}) [{(job.Enabled ? "enabled" : "disabled")}, {job.ScheduleType}]");
        write($"Selected item: {item.SourcePath}");
        write($"Source: {snapshot.LogicalSourcePath}; bytes={snapshot.SourceSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; duration={snapshot.MediaDurationSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} s");
        write($"Output: folder={snapshot.OutputFolder}; suffix='{snapshot.Suffix}' (collision-safe filename/container resolved during execution)");
        write($"Encoder: {snapshot.EncoderText}; codec={snapshot.Codec}; preset={snapshot.EncoderPreset ?? "default"}; depth={(snapshot.TenBit ? 10 : 8)}-bit");
        write($"Quality: {quality}; Target Size: {targetSize}");
        if (assignment is not null)
            write($"Experiment: {assignment.ExperimentId}; slot={assignment.Slot}; attempt={assignment.Attempt}; role={assignment.Role}");
    }
}

public interface IHeadlessSavedJobItemPipeline
{
    Task<EncodeExecutionSnapshot> BuildSnapshotAsync(EncodeJob job, EncodeJobFile item, CancellationToken cancellationToken);
    void ValidateForPreflight(EncodeExecutionSnapshot snapshot);
    Task<string> ExecuteOneAsync(EncodeExecutionSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class HeadlessSavedJobValidationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Production non-UI composition of the shared builder and orchestrator.</summary>
public sealed class HeadlessSavedJobItemPipeline : IHeadlessSavedJobItemPipeline
{
    private readonly Config _config;
    private readonly string _userDataDirectory;
    private readonly MediaInfoService _mediaInfo;
    private readonly SizeEstimateService _sizeEstimates;
    private readonly EncodeExecutionSnapshotBuilder _builder;
    private readonly EncodeExecutionOrchestrator _orchestrator;
    private readonly FfprobeService _ffprobe;
    private readonly EncodingStatisticsService _statistics;

    public HeadlessSavedJobItemPipeline(Config config, string userDataDirectory, string appDirectory)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _userDataDirectory = Path.GetFullPath(userDataDirectory);
        _mediaInfo = new MediaInfoService(appDirectory, config.FfprobePath, persistentCacheEnabled: false);
        _sizeEstimates = new SizeEstimateService(_mediaInfo);
        _builder = new EncodeExecutionSnapshotBuilder(_sizeEstimates, _mediaInfo.GetBitrateKbps);
        _ffprobe = new FfprobeService(appDirectory, config.FfprobePath);
        _statistics = new EncodingStatisticsService(Path.Combine(_userDataDirectory, "data", "encoding-statistics.jsonl"));
        var shadow = new NvencQualityModePredictionShadowService(
            new PredictionShadowObservationJournal(Path.Combine(_userDataDirectory, "data", "prediction-shadow-observations.jsonl")),
            new PredictionShadowComplexitySamplingService(appDirectory, config.FfmpegPath),
            message => Console.Error.WriteLine(message));
        var encoding = new EncodingService(appDirectory, _ => { }, Console.Error.WriteLine, config.FfmpegPath, config.FfprobePath);
        _orchestrator = new EncodeExecutionOrchestrator(
            encoding,
            shadow,
            _statistics,
            experimentFreezeStore: new PredictionShadowExperimentFreezeStore(_userDataDirectory));
    }

    public async Task<EncodeExecutionSnapshot> BuildSnapshotAsync(EncodeJob job, EncodeJobFile item, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(item.SourcePath))
            throw new HeadlessSavedJobValidationException("Saved-job source path must be absolute.");
        if (string.IsNullOrWhiteSpace(item.SourcePath) || !File.Exists(item.SourcePath))
            throw new HeadlessSavedJobValidationException($"Selected source is missing: {item.SourcePath}");
        EncodeExecutionSnapshotSettings settings = ResolveSettingsForItem(job.Settings, _config, item.SourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        MediaInfoService.MediaInfo info = _mediaInfo.GetInfo(item.SourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (info.DurationSeconds is not > 0)
            throw new HeadlessSavedJobValidationException("FFprobe did not provide a valid source duration; the execution snapshot cannot be constructed safely.");
        long length = new FileInfo(item.SourcePath).Length;
        string? entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        string appVersion = !string.IsNullOrWhiteSpace(entryAssemblyPath)
            ? FileVersionInfo.GetVersionInfo(entryAssemblyPath).ProductVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"
            : Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var identity = new EncodeExecutionSnapshotIdentity
        {
            OperationId = Guid.NewGuid().ToString("N"),
            StatisticsStartUtc = DateTime.UtcNow,
            MediaFluxVersion = appVersion,
            StatisticsPath = Path.Combine(_userDataDirectory, "data", "encoding-statistics.jsonl"),
            ContainerCompatibilityConfirmed = false
        };
        var snapshotItem = new EncodeExecutionSnapshotItem
        {
            SourceFilePath = item.SourcePath,
            LogicalSourcePath = item.SourcePath,
            SourceMeasurements = new EncodeExecutionSourceMeasurements
            {
                AudioBitrateKbps = info.AudioBitrateKbps,
                AudioStreamCount = info.AudioStreamCount,
                SubtitleBitrateKbps = info.SubtitleBitrateKbps,
                SubtitleStreamCount = info.SubtitleStreamCount,
                Height = info.Height
            },
            CustomCompressionProfile = item.CustomCompressionProfile,
            CustomTargetMb = item.CustomTargetMb,
            SourceSizeBytes = length,
            MediaDurationSeconds = info.DurationSeconds,
            PredictionShadowExperimentAssignment = item.PredictionShadowExperimentAssignment
        };
        EncodeExecutionSnapshot preliminary;
        try
        {
            preliminary = _builder.Build(identity, settings, snapshotItem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new HeadlessSavedJobValidationException("Could not construct the production execution snapshot: " + ex.Message, ex); }

        EstimateBackgroundService.SmartEstimateResult estimate;
        try
        {
            estimate = await EstimateSavedJobItemAsync(item, settings, preliminary, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new HeadlessSavedJobValidationException("The shared production estimate/calibration service could not resolve this saved item: " + ex.Message, ex);
        }
        cancellationToken.ThrowIfCancellationRequested();

        EncodeExecutionSnapshotBuildResult built;
        try
        {
            built = _builder.BuildWithDetails(
                identity,
                settings,
                snapshotItem with
                {
                    SourceMeasurements = snapshotItem.SourceMeasurements! with
                    {
                        EstimatedPlannedAudioBitrateKbps = estimate.PlannedAudioBitrateKbps
                    },
                    EstimatedTargetMb = estimate.EstimatedMb > 0 ? estimate.EstimatedMb : null,
                    SizePredictionCalibration = estimate.SizeCalibration
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new HeadlessSavedJobValidationException("Could not construct the production execution snapshot: " + ex.Message, ex); }
        return built.Snapshot;

    }

    /// <summary>
    /// Resolves only the output-folder primitive for the selected item, then
    /// delegates all remaining saved-job validation to the strict shared mapper.
    /// A blank folder preserves MainForm's established source-directory behavior.
    /// </summary>
    internal static EncodeExecutionSnapshotSettings ResolveSettingsForItem(
        EncodeJobSettings savedSettings,
        Config config,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(savedSettings);
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            throw new HeadlessSavedJobValidationException("Saved-job source path must be absolute.");

        string effectiveOutputFolder;
        if (string.IsNullOrWhiteSpace(savedSettings.OutputFolder))
        {
            try
            {
                string fullSourcePath = Path.GetFullPath(sourcePath);
                effectiveOutputFolder = Path.GetDirectoryName(fullSourcePath) ??
                    throw new HeadlessSavedJobValidationException("The source-relative output folder could not be determined from the selected source.");
            }
            catch (HeadlessSavedJobValidationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new HeadlessSavedJobValidationException("The source-relative output folder could not be determined from the selected source.", ex);
            }
        }
        else
        {
            if (!Path.IsPathFullyQualified(savedSettings.OutputFolder))
                throw new HeadlessSavedJobValidationException("Saved job output folder must be an absolute path.");
            if (HasInvalidPathComponent(savedSettings.OutputFolder))
                throw new HeadlessSavedJobValidationException("Saved job output folder is malformed.");

            string normalizedOutputFolder;
            try { normalizedOutputFolder = Path.GetFullPath(savedSettings.OutputFolder); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new HeadlessSavedJobValidationException("Saved job output folder is malformed.", ex); }

            if (!Directory.Exists(normalizedOutputFolder))
                throw new HeadlessSavedJobValidationException("Saved job output folder does not currently exist; headless preflight will not create or alter it.");

            // Preserve the persisted absolute path as the production snapshot value.
            effectiveOutputFolder = savedSettings.OutputFolder;
        }

        EncodeJobSettings effectiveSettings = savedSettings.Clone();
        effectiveSettings.OutputFolder = effectiveOutputFolder;
        try { return EncodeExecutionSnapshotSettings.FromSavedJob(effectiveSettings, config); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        { throw new HeadlessSavedJobValidationException("Saved-job execution settings are incomplete: " + ex.Message, ex); }
    }

    private static bool HasInvalidPathComponent(string path)
    {
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return true;

        string root = Path.GetPathRoot(path) ?? string.Empty;
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        return path[root.Length..]
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
    }

    private async Task<EstimateBackgroundService.SmartEstimateResult> EstimateSavedJobItemAsync(
        EncodeJobFile item,
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshot preliminary,
        CancellationToken cancellationToken)
    {
        bool isCustom = item.CustomTargetMb.HasValue || !string.IsNullOrWhiteSpace(item.CustomCompressionProfile);
        double manualTargetMb = item.CustomTargetMb.GetValueOrDefault();
        if (manualTargetMb <= 0 && !settings.AutoTargetSize)
            _ = double.TryParse(settings.TargetSizeText, NumberStyles.Float, CultureInfo.CurrentCulture, out manualTargetMb);
        EncodingQualityIntent? qualityIntent = isCustom ? null : preliminary.QualityIntent;
        bool rowAuto = item.CustomTargetMb.HasValue
            ? false
            : !string.IsNullOrWhiteSpace(item.CustomCompressionProfile) ||
              SizeEstimateService.ShouldUseProfileEstimate(settings.AutoTargetSize, manualTargetMb);
        string profile = !string.IsNullOrWhiteSpace(item.CustomCompressionProfile)
            ? item.CustomCompressionProfile!
            : qualityIntent is not null ? "Medium Quality (Default)" : settings.CompressionProfile;
        StorageSavingsOptions storage = settings.StorageSavings.CloneNormalized();
        if (isCustom) storage.Enabled = false;
        VideoRestorationPipelinePlan restorationPlan = VideoRestorationPipeline.BuildPlan(
            preliminary.Restoration, preliminary.ScaleMode);
        bool sourceAdaptiveEligible = qualityIntent is not null && !restorationPlan.UsesAi &&
            string.IsNullOrWhiteSpace(restorationPlan.ConventionalFilterChain) &&
            string.IsNullOrWhiteSpace(restorationPlan.PreAiFilterChain) &&
            string.IsNullOrWhiteSpace(restorationPlan.PostAiFilterChain);
        var queueItemId = Guid.NewGuid();
        using var estimateService = new EstimateBackgroundService(_mediaInfo, _statistics);
        estimateService.QueueSmartEstimate(
            item.SourcePath,
            rowAuto,
            profile,
            manualTargetMb,
            preliminary.Encoder,
            preliminary.QualityValue ?? 0,
            RequestedTargetHeight(preliminary.ScaleMode),
            preliminary.AudioChannels,
            isCustom,
            _config.SmartRecommendationsEnabled,
            _config.MinimumExpectedSavingsPercent,
            storage,
            qualityIntent,
            sourceAdaptiveEligible,
            _config.UseHistoricalSizePredictionCalibration,
            queueItemId,
            preliminary.EncoderPreset ?? "",
            preliminary.TenBit ? 10 : 8,
            preliminary.ConcurrentEncoderSessions);
        while (true)
        {
            while (estimateService.TryDequeueSmart(out EstimateBackgroundService.SmartEstimateResult result))
                if (result.QueueItemId == queueItemId)
                    return result;
            if (estimateService.PendingEstimates == 0)
                throw new InvalidOperationException("The estimate worker ended without publishing a result for the selected saved-job item.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int? RequestedTargetHeight(EncodingService.ScaleMode mode) => mode switch
    {
        EncodingService.ScaleMode.To720p => 720,
        EncodingService.ScaleMode.To1080p => 1080,
        EncodingService.ScaleMode.To1440p => 1440,
        EncodingService.ScaleMode.To4K => 2160,
        _ => null
    };

    public void ValidateForPreflight(EncodeExecutionSnapshot snapshot)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            EncodeExecutionAttempt attempt = _orchestrator.CreateAttempt(snapshot);
            _orchestrator.ValidateForPreflight(attempt);
            ValidateContainerConsent(snapshot);
        }
        catch (EncodeExecutionAssignmentValidationException) { throw; }
        catch (HeadlessSavedJobValidationException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new HeadlessSavedJobValidationException("Preflight validation rejected the selected item: " + ex.Message, ex); }
    }

    public async Task<string> ExecuteOneAsync(EncodeExecutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContainerConsent(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        EncodeExecutionAttempt attempt = _orchestrator.CreateAttempt(snapshot);
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            EncodeExecutionResult result = await _orchestrator.ExecuteAsync(
                attempt,
                new EncodeExecutionCallbacks
                {
                    Progress = line => Console.WriteLine(line),
                    OutputPathChanged = path => Console.WriteLine("Output: " + path),
                    FinalizationStatusChanged = status => Console.WriteLine(status),
                    Diagnostic = message => Console.Error.WriteLine(message)
                },
                lifecycleDiagnostics: null,
                cancellationToken).ConfigureAwait(false);
            timer.Stop();
            _orchestrator.RecordSuccessfulExecution(
                attempt,
                DateTime.UtcNow,
                result.EncodedOutput.FinalOutputSizeBytes,
                timer.Elapsed.TotalSeconds,
                "Headless saved-job item execution.",
                diagnosticSummary: null,
                recoveredSuccessful: false,
                result.EncodedOutput.FinalOutputProbe);
            return $"success; output='{result.EncodedOutput.OutputPath}'; finalized=True; bytes={result.EncodedOutput.FinalOutputSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
        }
        catch (Exception ex)
        {
            timer.Stop();
            _orchestrator.RecordFailedExecution(
                attempt,
                DateTime.UtcNow,
                ex is OperationCanceledException,
                finalizationFailureKind: null,
                outputPath: attempt.AttemptedOutputPath,
                processingSeconds: timer.Elapsed.TotalSeconds,
                notes: "Headless saved-job item execution failed: " + ex.Message,
                diagnosticSummary: null,
                retryQueued: false);
            throw;
        }
    }

    private void ValidateContainerConsent(EncodeExecutionSnapshot snapshot)
    {
        if (snapshot.CompatibilityPolicy != ContainerCompatibilityPolicy.AlwaysAsk)
            return;
        MediaProbeResult probe = _ffprobe.ProbeAsync(snapshot.Input.SourcePath).GetAwaiter().GetResult();
        if (!probe.Success)
            throw new HeadlessSavedJobValidationException("Always Ask container policy cannot be evaluated because FFprobe failed: " + probe.ErrorMessage);
        OutputContainerDecision decision = OutputContainerPolicy.Decide(
            snapshot.OutputContainer, probe, snapshot.Input, snapshot.MapMode);
        if (decision.RequiresConfirmation)
            throw new HeadlessSavedJobValidationException(
                "The selected item requires MP4/container compatibility confirmation under the persisted Always Ask policy. Headless mode has no persisted consent decision, so execution was rejected before Frozen capture.");
    }
}
