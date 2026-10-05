using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
    Cancelled = 5,
    StoragePolicyRejected = 6,
    AdaptiveStorageSavingsSkipped = 7
}

public sealed record HeadlessSavedJobCommand(
    HeadlessSavedJobMode Mode,
    Guid JobId,
    string ItemSelector,
    string? UserDataRoot = null)
{
    internal static bool IsRequested(string[] args) =>
        (args.Length > 0 && (args[0].StartsWith("--run-saved-job", StringComparison.Ordinal) ||
                            args[0].StartsWith("--preflight-saved-job", StringComparison.Ordinal))) ||
        args.Any(arg => arg.StartsWith("--user-data", StringComparison.OrdinalIgnoreCase));

    public static bool TryParse(string[] args, out HeadlessSavedJobCommand? command, out string error)
    {
        command = null;
        error = "";
        if (args.Length == 0 || args[0] is not ("--run-saved-job-item" or "--preflight-saved-job-item"))
        {
            error = "Expected --run-saved-job-item or --preflight-saved-job-item.";
            return false;
        }
        if (args.Length is not (4 or 6) || args[2] != "--item")
        {
            error = "Syntax: MediaFlux.exe (--run-saved-job-item|--preflight-saved-job-item) <job-id> --item <selector> [--user-data <absolute-directory>]. Exactly one item selector and at most one UserData override are permitted.";
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
        string? userDataRoot = null;
        if (args.Length == 6)
        {
            if (args[4] != "--user-data")
            {
                error = "The only supported trailing option is --user-data <absolute-directory>.";
                return false;
            }
            try { userDataRoot = MediaFluxStoragePathService.NormalizeIsolatedRoot(args[5]); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = "Invalid isolated UserData path: " + ex.Message;
                return false;
            }
        }
        command = new HeadlessSavedJobCommand(
            args[0] == "--run-saved-job-item" ? HeadlessSavedJobMode.Run : HeadlessSavedJobMode.Preflight,
            jobId,
            args[3],
            userDataRoot);
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
        DateTime snapshotStartedUtc = DateTime.UtcNow;
        try { snapshot = await pipeline.BuildSnapshotAsync(executionJob, executionItem, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writeLine("Terminal result: cancelled during source inspection or estimate preflight.");
            if (command.Mode == HeadlessSavedJobMode.Run)
                pipeline.RecordBeforeAttemptOutcome(
                    executionJob, executionItem, JobStatus.Canceled, snapshotStartedUtc, DateTime.UtcNow,
                    "Cancelled during source inspection or estimate preflight.");
            return new(HeadlessSavedJobExitCode.Cancelled, "Preflight cancelled.", executionJob.Name, executionItem.SourcePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            writeLine("Snapshot rejected: " + ex.Message);
            if (command.Mode == HeadlessSavedJobMode.Run)
                pipeline.RecordBeforeAttemptOutcome(
                    executionJob, executionItem, JobStatus.Failed, snapshotStartedUtc, DateTime.UtcNow, ex.Message);
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
            HeadlessSavedJobExitCode code = ex is AdaptiveStorageSavingsSkippedException or HeadlessSavedJobPolicySkipException { StorageContractRejected: false }
                ? HeadlessSavedJobExitCode.AdaptiveStorageSavingsSkipped
                : ex is HeadlessSavedJobPolicySkipException { StorageContractRejected: true } or EncodeFinalizationException { Result.FailureKind: EncodeFinalizationFailureKind.StoragePolicyRejected }
                ? HeadlessSavedJobExitCode.StoragePolicyRejected
                : ex is HeadlessSavedJobValidationException or EncodeExecutionAssignmentValidationException
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
    string RecordBeforeAttemptOutcome(
        EncodeJob job,
        EncodeJobFile item,
        JobStatus status,
        DateTime startedUtc,
        DateTime endedUtc,
        string message) => string.Empty;
}

public sealed class HeadlessSavedJobValidationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class HeadlessSavedJobPolicySkipException(string message, bool storageContractRejected)
    : InvalidOperationException(message)
{
    public bool StorageContractRejected { get; } = storageContractRejected;
}

/// <summary>Production non-UI composition of the shared builder and orchestrator.</summary>
public sealed class HeadlessSavedJobItemPipeline : IHeadlessSavedJobItemPipeline, IHeadlessEncodeBatchProcessor
{
    private readonly Config _config;
    private readonly string _userDataDirectory;
    private readonly MediaInfoService _mediaInfo;
    private readonly SizeEstimateService _sizeEstimates;
    private readonly EncodeExecutionSnapshotBuilder _builder;
    private readonly EncodeExecutionOrchestrator _orchestrator;
    private readonly FfprobeService _ffprobe;
    private readonly EncodingStatisticsService _statistics;
    private readonly EncodeExecutionLifecycleService _lifecycle;

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
        _lifecycle = new EncodeExecutionLifecycleService(
            _orchestrator,
            () => new HistoryService(Path.Combine(_userDataDirectory, "data", "history.json")),
            message => Console.Error.WriteLine("[HeadlessLifecycle] " + message));
    }

    public Task<EncodeExecutionSnapshot> BuildSnapshotAsync(EncodeJob job, EncodeJobFile item, CancellationToken cancellationToken) =>
        BuildSnapshotCoreAsync(job, item, disablePolicyCRetry: false, requireStorageSavingsContract: false, cancellationToken);

    private async Task<EncodeExecutionSnapshot> BuildSnapshotCoreAsync(
        EncodeJob job,
        EncodeJobFile item,
        bool disablePolicyCRetry,
        bool requireStorageSavingsContract,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(item.SourcePath))
            throw new HeadlessSavedJobValidationException("Saved-job source path must be absolute.");
        if (string.IsNullOrWhiteSpace(item.SourcePath) || !File.Exists(item.SourcePath))
            throw new HeadlessSavedJobValidationException($"Selected source is missing: {item.SourcePath}");
        EncodeExecutionSnapshotSettings settings = ResolveSettingsForItem(job.Settings, _config, item.SourcePath);
        if (disablePolicyCRetry || requireStorageSavingsContract)
        {
            StorageSavingsOptions safeStorageSavings = ResolveTransientStorageSavingsOptions(
                settings.StorageSavings, requireStorageSavingsContract);
            settings = settings with { StorageSavings = safeStorageSavings };
        }
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

    public Task<EncodeExecutionSnapshot> BuildTransientSnapshotAsync(
        string sourcePath,
        string? outputDirectory,
        string? qualityModeOverride,
        bool deleteSourceAfterCompression,
        bool requireStorageSavingsContract,
        CancellationToken cancellationToken)
    {
        EncodeJobSettings settings = CreateTransientSettings(
            _config,
            sourcePath,
            outputDirectory,
            qualityModeOverride,
            deleteSourceAfterCompression);
        var transientJob = new EncodeJob
        {
            Name = "Transient headless encode",
            Enabled = false,
            Settings = settings
        };
        return BuildSnapshotCoreAsync(
            transientJob,
            new EncodeJobFile { SourcePath = sourcePath },
            disablePolicyCRetry: true,
            requireStorageSavingsContract,
            cancellationToken);
    }

    internal static StorageSavingsOptions ResolveTransientStorageSavingsOptions(
        StorageSavingsOptions configured,
        bool requireStorageSavingsContract)
    {
        ArgumentNullException.ThrowIfNull(configured);
        StorageSavingsOptions transient = configured.CloneNormalized();
        if (requireStorageSavingsContract)
            transient.Enabled = true;
        transient.ExperimentalPolicyCRetryEnabled = false;
        return transient;
    }

    internal static EncodeJobSettings CreateTransientSettings(
        Config config,
        string sourcePath,
        string? outputDirectory,
        string? qualityModeOverride,
        bool deleteSourceAfterCompression)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            throw new HeadlessSavedJobValidationException("Transient source paths must be absolute.");
        if (!File.Exists(sourcePath))
            throw new HeadlessSavedJobValidationException($"Transient source does not exist: {sourcePath}");

        string effectiveOutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            effectiveOutputDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ??
                throw new HeadlessSavedJobValidationException("The source directory could not be determined.");
        }
        else
        {
            if (!Path.IsPathFullyQualified(outputDirectory))
                throw new HeadlessSavedJobValidationException("The output directory must be an absolute path.");
            effectiveOutputDirectory = Path.GetFullPath(outputDirectory);
            if (!Directory.Exists(effectiveOutputDirectory))
                throw new HeadlessSavedJobValidationException("The output directory must already exist; headless mode will not create it.");
        }

        string qualityMode = string.IsNullOrWhiteSpace(qualityModeOverride)
            ? config.LastQualityMode
            : qualityModeOverride.Trim();
        if (!string.Equals(qualityMode, "Automatic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(qualityMode, "Manual", StringComparison.OrdinalIgnoreCase))
            throw new HeadlessSavedJobValidationException("Quality mode must be Automatic or Manual.");

        VideoCodecFamily codecFamily = VideoEncoderCompatibility.ParseCodecFamily(config.LastVideoCodec);
        string format = codecFamily switch
        {
            VideoCodecFamily.H264 => "H.264 / AVC (x264)",
            VideoCodecFamily.Hevc => "H.265 / HEVC (x265)",
            VideoCodecFamily.Av1 => "AV1",
            _ => throw new HeadlessSavedJobValidationException("The configured video codec is unsupported.")
        };

        return new EncodeJobSettings
        {
            OutputFolder = effectiveOutputDirectory,
            CompressionProfile = config.LastCompressionProfile,
            EncoderId = config.LastEncoderId,
            VideoCodec = config.LastVideoCodec,
            EncoderPreset = config.LastEncoderPreset,
            OutputContainer = config.LastOutputContainer,
            QualityValue = config.LastQualityValue,
            QualityMode = qualityMode,
            QualityTarget = config.LastQualityTarget,
            TenBit = false,
            AudioChannels = "Keep source layout",
            VideoFormat = format,
            AutoTargetSize = config.LastChkAutoTargetSize,
            TargetSize = string.Empty,
            Resolution = "Original",
            DeleteSourceAfterCompression = deleteSourceAfterCompression,
            EnableOutputSuffix = config.EnableOutputSuffix,
            EnableCodecSuffix = config.EnableCodecSuffix,
            OutputSuffix = config.OutputSuffix,
            Restoration = config.VideoRestoration?.Clone() ?? new VideoRestorationSettings()
        };
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
            EncodeExecutionAttempt attempt = _lifecycle.CreateAttempt(snapshot);
            _lifecycle.ValidateForPreflight(attempt);
            ValidateContainerConsent(snapshot);
        }
        catch (EncodeExecutionAssignmentValidationException) { throw; }
        catch (HeadlessSavedJobValidationException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new HeadlessSavedJobValidationException("Preflight validation rejected the selected item: " + ex.Message, ex); }
    }

    public async Task<string> ExecuteOneAsync(EncodeExecutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        HeadlessEncodeJobResult outcome = await ExecuteSnapshotAndCompleteAsync(
            snapshot,
            DateTime.UtcNow,
            cancellationToken,
            progress: Console.WriteLine,
            batchIndex: 1).ConfigureAwait(false);
        return outcome.Status switch
        {
            HeadlessEncodeJobStatus.Success or HeadlessEncodeJobStatus.RecoveredSuccess =>
                $"success; output='{outcome.OutputPath}'; finalized=True; bytes={outcome.OutputBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
            HeadlessEncodeJobStatus.Cancelled => throw new OperationCanceledException(outcome.Error ?? "Headless encode was cancelled.", cancellationToken),
            HeadlessEncodeJobStatus.Rejected => throw new HeadlessSavedJobValidationException(outcome.Error ?? "The selected item was rejected."),
            HeadlessEncodeJobStatus.Skipped => throw new HeadlessSavedJobPolicySkipException(
                outcome.Error ?? "Storage savings policy skipped this item.",
                outcome.StorageContractAccepted == false),
            _ => throw new InvalidOperationException(outcome.Error ?? "Headless encode failed.")
        };
    }

    public string RecordBeforeAttemptOutcome(
        EncodeJob job,
        EncodeJobFile item,
        JobStatus status,
        DateTime startedUtc,
        DateTime endedUtc,
        string message)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(item);
        var record = new JobHistoryRecord
        {
            Type = JobType.Encode,
            Status = status,
            StartUtc = startedUtc,
            EndUtc = endedUtc,
            SourcePath = item.SourcePath,
            SourceSizeBytes = TryGetSourceSize(item.SourcePath),
            ErrorSummary = message,
            Notes = "Headless encode was rejected before an execution attempt. Original source retained.",
            FinalizationOutcome = "PreflightRejected",
            TerminalResult = status == JobStatus.Canceled
                ? EncodingTerminalResult.Canceled
                : EncodingTerminalResult.ValidationFailed,
            SourceDeletionResult = "Original source retained; no production encode was started."
        };
        _lifecycle.RecordRejectedBeforeAttempt(record);
        return record.Id;
    }

    async Task<HeadlessEncodeJobResult> IHeadlessEncodeBatchProcessor.ExecuteOneAsync(
        string sourcePath,
        string? outputDirectory,
        string? qualityModeOverride,
        bool deleteSourceAfterCompression,
        bool requireStorageSavingsContract,
        int index,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        DateTime startedUtc = DateTime.UtcNow;
        var transientJob = new EncodeJob
        {
            Name = "Transient headless encode",
            Enabled = false,
            Settings = new EncodeJobSettings()
        };
        var transientItem = new EncodeJobFile { SourcePath = sourcePath };
        EncodeExecutionSnapshot snapshot;
        try
        {
            snapshot = await BuildTransientSnapshotAsync(
                sourcePath,
                outputDirectory,
                qualityModeOverride,
                deleteSourceAfterCompression,
                requireStorageSavingsContract,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            string id = RecordBeforeAttemptOutcome(transientJob, transientItem, JobStatus.Canceled,
                startedUtc, DateTime.UtcNow, "Cancelled during source inspection or estimate preflight.");
            return new(index, id, sourcePath, HeadlessEncodeJobStatus.Cancelled, startedUtc, DateTime.UtcNow,
                CleanupResult: "not applicable; no output was allocated.",
                SourceDisposition: "retained; no production encode was started.",
                HistoryRecorded: true, Error: "Cancelled during source inspection or estimate preflight.");
        }
        catch (HeadlessSavedJobValidationException ex)
        {
            string id = RecordBeforeAttemptOutcome(transientJob, transientItem, JobStatus.Skipped,
                startedUtc, DateTime.UtcNow, ex.Message);
            return new(index, id, sourcePath, HeadlessEncodeJobStatus.Rejected, startedUtc, DateTime.UtcNow,
                CleanupResult: "not applicable; no output was allocated.",
                SourceDisposition: "retained; no production encode was started.",
                HistoryRecorded: true, Error: ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            string id = RecordBeforeAttemptOutcome(transientJob, transientItem, JobStatus.Failed,
                startedUtc, DateTime.UtcNow, ex.Message);
            return new(index, id, sourcePath, HeadlessEncodeJobStatus.Failed, startedUtc, DateTime.UtcNow,
                CleanupResult: "not applicable; no output was allocated.",
                SourceDisposition: "retained; no production encode was started.",
                HistoryRecorded: true, Error: ex.Message, FatalBatchFailure: true);
        }

        return await ExecuteSnapshotAndCompleteAsync(snapshot, startedUtc, cancellationToken, progress, index)
            .ConfigureAwait(false);
    }

    private async Task<HeadlessEncodeJobResult> ExecuteSnapshotAndCompleteAsync(
        EncodeExecutionSnapshot snapshot,
        DateTime startedUtc,
        CancellationToken cancellationToken,
        Action<string>? progress,
        int batchIndex)
    {
        var log = new StringBuilder();
        void Write(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                if (log.Length < 512 * 1024)
                {
                    int remaining = 512 * 1024 - log.Length;
                    log.AppendLine(line.Length <= remaining ? line : line[..remaining]);
                }
                try { progress?.Invoke(line); } catch { }
            }
        }

        EncodeExecutionAttempt? attempt = null;
        var history = new JobHistoryRecord
        {
            Id = snapshot.OperationId,
            Type = JobType.Encode,
            StartUtc = snapshot.StatisticsStartUtc,
            SourcePath = snapshot.LogicalSourcePath,
            SourceSizeBytes = snapshot.SourceSizeBytes,
            EncoderMode = snapshot.EncoderText,
            TargetMb = snapshot.TargetMb,
            DurationSec = snapshot.MediaDurationSeconds
        };
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateContainerConsent(snapshot);
            attempt = _lifecycle.CreateAttempt(snapshot);
            EncodeExecutionResult result = await _lifecycle.ExecuteAsync(
                attempt,
                new EncodeExecutionCallbacks
                {
                    Progress = Write,
                    OutputPathChanged = path => Write("Output: " + path),
                    StagingPathChanged = path => Write("Staging: " + path),
                    FinalizationStatusChanged = Write,
                    QualityResolved = quality => Write($"Resolved quality: {quality.Mechanism} {quality.EffectiveQuality?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}"),
                    RecoveryStatus = update => Write("Recovery: " + update.Detail),
                    Diagnostic = message => Write("Diagnostic: " + message)
                },
                lifecycleDiagnostics: null,
                cancellationToken).ConfigureAwait(false);
            timer.Stop();
            DateTime endedUtc = DateTime.UtcNow;
            string sourceDisposition = result.SourceDeletion.Message;
            if (string.IsNullOrWhiteSpace(sourceDisposition))
                sourceDisposition = snapshot.DeleteSourceAfterCompression
                    ? "source deletion was requested; see orchestrator outcome."
                    : "retained; automatic source deletion is disabled.";
            history.Log = log.ToString();
            history.Notes = $"Headless production encode completed and validated. {sourceDisposition}";
            history.SourceDeletionResult = sourceDisposition;
            EncodeExecutionLifecycleCompletion completion = _lifecycle.CompleteSuccessfulExecution(
                attempt,
                endedUtc,
                result.EncodedOutput.FinalOutputSizeBytes,
                timer.Elapsed.TotalSeconds,
                history.Notes,
                diagnosticSummary: null,
                recoveredSuccessful: IsRecovered(attempt),
                finalOutputProbe: result.EncodedOutput.FinalOutputProbe,
                historyRecord: history);

            HeadlessEncodeJobStatus status = IsRecovered(attempt)
                ? HeadlessEncodeJobStatus.RecoveredSuccess
                : HeadlessEncodeJobStatus.Success;
            return CreateStructuredResult(
                batchIndex, snapshot, attempt, status, startedUtc, endedUtc,
                completion, "not applicable; encode succeeded.", sourceDisposition, null,
                ffmpegExitCode: 0);
        }
        catch (Exception ex)
        {
            timer.Stop();
            DateTime endedUtc = DateTime.UtcNow;
            bool canceled = ex is OperationCanceledException;
            bool storageRejected = ex is AdaptiveStorageSavingsSkippedException or
                EncodeFinalizationException { Result.FailureKind: EncodeFinalizationFailureKind.StoragePolicyRejected };
            string message = ex.Message;

            if (attempt is null)
            {
                history.Status = canceled ? JobStatus.Canceled : JobStatus.Skipped;
                history.EndUtc = endedUtc;
                history.ErrorSummary = message;
                history.Notes = "Headless encode was rejected before a production attempt. Original source retained.";
                history.FinalizationOutcome = canceled ? "Canceled" : "PreflightRejected";
                history.TerminalResult = canceled ? EncodingTerminalResult.Canceled : EncodingTerminalResult.ValidationFailed;
                history.SourceDeletionResult = "Original source retained; no production encode was started.";
                bool recorded = _lifecycle.RecordRejectedBeforeAttempt(history).HistoryRecorded;
                return new(batchIndex, history.Id, snapshot.LogicalSourcePath,
                    canceled ? HeadlessEncodeJobStatus.Cancelled : HeadlessEncodeJobStatus.Rejected,
                    startedUtc, endedUtc, SelectedMode: GetSelectedMode(snapshot),
                    AdaptiveSelection: null, FinalEncodePlan: null,
                    CleanupResult: "not applicable; no output was allocated.",
                    SourceDisposition: history.SourceDeletionResult,
                    HistoryRecorded: recorded, StatisticsRecorded: false, Error: message);
            }

            string notes = storageRejected
                ? "Headless encode skipped by the active storage-savings policy: " + message
                : canceled ? "Headless encode cancelled." : "Headless production encode failed: " + message;
            history.Log = log.ToString();
            history.Notes = notes;
            history.ErrorSummary = message;
            EncodeExecutionLifecycleCompletion failed = await _lifecycle.CompleteFailedExecutionAsync(
                attempt,
                history,
                endedUtc,
                canceled,
                storageRejected,
                _config.DeleteFailedEncodeOutputs,
                snapshot.LogicalSourcePath,
                attempt.AttemptedOutputPath,
                attempt.StagedOutputPath,
                (ex as EncodeFinalizationException)?.Result.RecoverableOutputPath ??
                    (ex as EncodeFinalizationCanceledException)?.Result.RecoverableOutputPath ?? "",
                timer.Elapsed.TotalSeconds,
                diagnosticSummary: null,
                finalizationFailureKind: (ex as EncodeFinalizationException)?.Result.FailureKind ??
                    (ex as EncodeFinalizationCanceledException)?.Result.FailureKind,
                retryQueued: false,
                errorMessage: message).ConfigureAwait(false);
            HeadlessEncodeJobStatus status = canceled
                ? HeadlessEncodeJobStatus.Cancelled
                : storageRejected ? HeadlessEncodeJobStatus.Skipped
                : ex is HeadlessSavedJobValidationException or EncodeExecutionAssignmentValidationException
                    ? HeadlessEncodeJobStatus.Rejected
                    : HeadlessEncodeJobStatus.Failed;
            return CreateStructuredResult(
                batchIndex, snapshot, attempt, status, startedUtc, endedUtc,
                failed, failed.CleanupResult, failed.SourceRetentionResult, message,
                ParseFfmpegExitCode(message + Environment.NewLine + log),
                fatalBatchFailure: ex is HeadlessEncodeFatalException or System.ComponentModel.Win32Exception);
        }
    }

    internal static HeadlessEncodeJobResult CreateStructuredResult(
        int index,
        EncodeExecutionSnapshot snapshot,
        EncodeExecutionAttempt attempt,
        HeadlessEncodeJobStatus status,
        DateTime startedUtc,
        DateTime endedUtc,
        EncodeExecutionLifecycleCompletion completion,
        string cleanup,
        string sourceDisposition,
        string? error,
        int? ffmpegExitCode,
        bool fatalBatchFailure = false)
    {
        EncodingService.EncodeResult? output = attempt.EncodeResult;
        EncodeFinalizationResult? finalization = attempt.FinalizationResult;
        EncodeOutputValidationResult? validation = finalization?.PromotedValidationResult ?? finalization?.StagedValidationResult;
        EncodingValidationOutcome? executionValidation = attempt.ExecutionOutcome?.Validation;
        bool? validated = validation?.Success ?? ValidationStatus(executionValidation?.Status) ??
            (output?.FinalizationSucceeded == true ? true : null);
        StorageSavingsEvaluation? storageSavings = finalization?.StorageSavings ??
            output?.StorageSavings ?? attempt.ExecutionOutcome?.StorageSavings;
        return new(
            index,
            snapshot.OperationId,
            snapshot.LogicalSourcePath,
            status,
            startedUtc,
            endedUtc,
            OutputPath: output?.OutputPath ?? attempt.AttemptedOutputPath,
            SelectedMode: GetSelectedMode(snapshot),
            AdaptiveSelection: attempt.AdaptiveSelection is null ? null : JsonSerializer.Serialize(attempt.AdaptiveSelection),
            ResolvedQuality: attempt.QualityResolution?.EffectiveQuality?.ToString(CultureInfo.InvariantCulture),
            FinalEncodePlan: SerializePlan(attempt.Plan),
            FfmpegExitCode: ffmpegExitCode,
            RecoveryOutcome: attempt.ExecutionOutcome?.TerminalResult.ToString(),
            FfprobeValidated: validation is not null
                ? validation.Success
                : ValidationStatus(executionValidation?.OutputProbe) ?? validated,
            DecodeIntegrityValidated: validation is not null
                ? validation.Success && validation.Summary.Contains("decode-integrity", StringComparison.OrdinalIgnoreCase)
                : ValidationStatus(executionValidation?.Integrity),
            MetadataValidated: validation is not null
                ? validation.Success
                : CombineValidationStatuses(executionValidation?.Streams, executionValidation?.Container),
            OutputBytes: output?.FinalOutputSizeBytes ?? finalization?.FinalOutputSizeBytes,
            StorageContractAccepted: storageSavings?.Acceptance switch
            {
                StorageSavingsAcceptance.Accepted => true,
                StorageSavingsAcceptance.Rejected => false,
                _ => null
            },
            StorageContractResult: storageSavings?.Acceptance.ToString() ??
                (snapshot.StorageSavingsContract.Applies ? "not-recorded" : "not-applicable"),
            Promoted: output?.FinalizationSucceeded ?? (finalization?.Success == true),
            CleanupResult: cleanup,
            SourceDisposition: sourceDisposition,
            HistoryRecorded: completion.HistoryRecorded,
            StatisticsRecorded: completion.StatisticsRecorded,
            Error: error,
            SourceSizeBytes: snapshot.SourceSizeBytes,
            SourceDurationSeconds: snapshot.MediaDurationSeconds,
            SourceVideoCodec: attempt.Plan?.Source?.Codec,
            SourceWidth: attempt.Plan?.Source?.Width,
            SourceHeight: attempt.Plan?.Source?.Height,
            SourceFrameRate: attempt.Plan?.Source?.FrameRate,
            FatalBatchFailure: fatalBatchFailure);
    }

    private static bool? ValidationStatus(EncodingLifecycleStatus? status) => status switch
    {
        EncodingLifecycleStatus.Passed => true,
        EncodingLifecycleStatus.Failed => false,
        _ => null
    };

    private static bool? CombineValidationStatuses(
        EncodingLifecycleStatus? first,
        EncodingLifecycleStatus? second)
    {
        bool? firstResult = ValidationStatus(first);
        bool? secondResult = ValidationStatus(second);
        if (firstResult == false || secondResult == false)
            return false;
        return firstResult == true && secondResult == true ? true : null;
    }

    private static string GetSelectedMode(EncodeExecutionSnapshot snapshot) => snapshot.QualityIntent is { } intent
        ? $"Automatic / {intent.Target}"
        : $"Manual / {snapshot.QualityValue?.ToString(CultureInfo.InvariantCulture) ?? "unset"}";

    private static bool IsRecovered(EncodeExecutionAttempt attempt) =>
        attempt.ExecutionOutcome?.TerminalResult is EncodingTerminalResult.CompletedAfterRecovery or
            EncodingTerminalResult.CompletedAfterDegradedSalvage;

    private static string? SerializePlan(EncodingPlan? plan)
    {
        if (plan is null) return null;
        try { return JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch { return plan.IsAvailable ? $"PlanId={plan.PlanId}; Codec={plan.Video?.Codec}; Encoder={plan.Video?.Encoder}" : plan.UnavailableReason; }
    }

    private static int? ParseFfmpegExitCode(string text)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            text, @"ffmpeg\s+exited\s+with\s+code\s+(?<code>-?\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
            ? code
            : null;
    }

    private static long? TryGetSourceSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
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
