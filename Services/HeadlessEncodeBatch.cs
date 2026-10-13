using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record HeadlessEncodeBatchCommand(
    IReadOnlyList<string> SourcePaths,
    string? OutputDirectory,
    string? QualityModeOverride,
    bool DeleteSourceAfterCompression,
    bool RequireStorageSavingsContract = false,
    string? UserDataRoot = null)
{
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "--headless-encode", StringComparison.Ordinal);

    public static bool TryParse(string[] args, out HeadlessEncodeBatchCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        if (!IsRequested(args))
        {
            error = "Expected --headless-encode.";
            return false;
        }

        var sources = new List<string>();
        string? outputDirectory = null;
        string? qualityMode = null;
        bool deleteSource = false;
        bool requireStorageSavingsContract = false;
        string? userDataRoot = null;
        bool outputSeen = false;
        bool qualitySeen = false;
        bool userDataSeen = false;
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (option == "--delete-source")
            {
                if (deleteSource)
                {
                    error = "--delete-source may be specified only once.";
                    return false;
                }
                deleteSource = true;
                continue;
            }

            if (option == "--storage-savings")
            {
                if (requireStorageSavingsContract)
                {
                    error = "--storage-savings may be specified only once.";
                    return false;
                }
                requireStorageSavingsContract = true;
                continue;
            }

            if (option is not ("--source" or "--output-directory" or "--quality-mode" or "--user-data") || i + 1 >= args.Length)
            {
                error = $"Unsupported, incomplete, or misplaced headless option '{option}'. Use repeated --source <absolute-path>, optional --output-directory <absolute-path>, optional --quality-mode Automatic|Manual, optional --user-data <prepared-isolated-directory>, and optional --storage-savings.";
                return false;
            }

            string value = args[++i];
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"Option '{option}' requires a non-empty value.";
                return false;
            }

            switch (option)
            {
                case "--source":
                    if (!Path.IsPathFullyQualified(value))
                    {
                        error = "Every --source value must be an absolute file path.";
                        return false;
                    }
                    try { sources.Add(Path.GetFullPath(value)); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        error = "A source path is invalid: " + ex.Message;
                        return false;
                    }
                    break;
                case "--output-directory":
                    if (outputSeen)
                    {
                        error = "--output-directory may be specified only once.";
                        return false;
                    }
                    if (!Path.IsPathFullyQualified(value))
                    {
                        error = "--output-directory must be an absolute directory path.";
                        return false;
                    }
                    try { outputDirectory = Path.GetFullPath(value); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        error = "The output directory is invalid: " + ex.Message;
                        return false;
                    }
                    outputSeen = true;
                    break;
                case "--quality-mode":
                    if (qualitySeen)
                    {
                        error = "--quality-mode may be specified only once.";
                        return false;
                    }
                    if (!string.Equals(value, "Automatic", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "Manual", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "--quality-mode must be Automatic or Manual.";
                        return false;
                    }
                    qualityMode = value;
                    qualitySeen = true;
                    break;
                case "--user-data":
                    if (userDataSeen)
                    {
                        error = "--user-data may be specified only once.";
                        return false;
                    }
                    try { userDataRoot = MediaFluxStoragePathService.NormalizeIsolatedRoot(value); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        error = "The isolated UserData directory is invalid: " + ex.Message;
                        return false;
                    }
                    userDataSeen = true;
                    break;
            }
        }

        if (sources.Count == 0)
        {
            error = "At least one --source <absolute-path> is required.";
            return false;
        }
        command = new(sources.AsReadOnly(), outputDirectory, qualityMode, deleteSource, requireStorageSavingsContract, userDataRoot);
        return true;
    }
}

public enum HeadlessEncodeJobStatus
{
    Success,
    RecoveredSuccess,
    Rejected,
    Skipped,
    Failed,
    Cancelled,
    NotStarted
}

public sealed record HeadlessProductionDiagnostics(
    string? SourceIdentity,
    string? PlanFingerprint,
    string? EstimatePlanFingerprint,
    long? EstimatedOutputBytes,
    string? EstimateMethod,
    string? EstimateAuthority,
    int? EstimateSampleCount,
    string? EstimateConfidence,
    long? EstimatedLowerBytes,
    long? EstimatedUpperBytes,
    long? ActualOutputBytes,
    string? SelectedMode,
    double? SelectedQuality,
    string? StorageSavingsClassification,
    string? Error,
    string? FinalizationReason,
    int ProductionAttemptCount,
    ProductionPlanFingerprintInput? PlanInputs = null,
    string? ExecutionArguments = null,
    IReadOnlyList<EmpiricalPreviewSampleEvidence>? EstimateSampleEvidence = null,
    string? EstimateRangeMethod = null)
{
    public bool? PlanFingerprintsIdentical => EstimatePlanFingerprint is null
        ? null
        : string.Equals(PlanFingerprint, EstimatePlanFingerprint, StringComparison.Ordinal);

    public string? EstimatePlanEngine { get; init; }
    public string? ExecutionPlanEngine { get; init; }
    public string? SelectedProfile { get; init; }
    public string? PreparedPlanId { get; init; }

    public string? PlanFingerprintComparison => EstimatePlanFingerprint is null
        ? null
        : PlanFingerprintsIdentical == true
            ? $"Fingerprint values are identical (estimate engine={EstimatePlanEngine ?? "unknown"}, execution engine={ExecutionPlanEngine ?? "unknown"})."
            : !string.IsNullOrWhiteSpace(EstimatePlanEngine) &&
              !string.IsNullOrWhiteSpace(ExecutionPlanEngine) &&
              !string.Equals(EstimatePlanEngine, ExecutionPlanEngine, StringComparison.Ordinal)
                ? $"Different engine-specific representations ({EstimatePlanEngine} estimate vs {ExecutionPlanEngine} execution); fingerprint equality is not required across engines."
                : "Different fingerprints with no confirmed engine distinction; investigate the handoff.";

    public double? PredictionErrorPercent => EstimatedOutputBytes is > 0 && ActualOutputBytes is > 0
        ? (ActualOutputBytes.Value - EstimatedOutputBytes.Value) / (double)EstimatedOutputBytes.Value * 100d
        : null;
}

/// <summary>Versioned machine-readable terminal evidence for one transient source.</summary>
public sealed record HeadlessEncodeJobResult(
    int Index,
    string OperationId,
    string SourcePath,
    HeadlessEncodeJobStatus Status,
    DateTime StartedUtc,
    DateTime EndedUtc,
    string? OutputPath = null,
    string? SelectedMode = null,
    string? AdaptiveSelection = null,
    string? ResolvedQuality = null,
    string? FinalEncodePlan = null,
    int? FfmpegExitCode = null,
    string? RecoveryOutcome = null,
    bool? FfprobeValidated = null,
    bool? DecodeIntegrityValidated = null,
    bool? MetadataValidated = null,
    long? OutputBytes = null,
    bool? StorageContractAccepted = null,
    string? StorageContractResult = null,
    bool? Promoted = null,
    string CleanupResult = "",
    string SourceDisposition = "",
    bool HistoryRecorded = false,
    bool StatisticsRecorded = false,
    string? Error = null,
    long? SourceSizeBytes = null,
    double? SourceDurationSeconds = null,
    string? SourceVideoCodec = null,
    int? SourceWidth = null,
    int? SourceHeight = null,
    double? SourceFrameRate = null,
    bool FatalBatchFailure = false,
    AutomaticEncodePerformanceDiagnostics? Performance = null,
    string? PerformanceSummary = null,
    HeadlessProductionDiagnostics? ProductionDiagnostics = null);

public sealed record HeadlessEncodeBatchResult(
    int SchemaVersion,
    string Status,
    int ExitCode,
    DateTime StartedUtc,
    DateTime EndedUtc,
    IReadOnlyList<HeadlessEncodeJobResult> Jobs,
    string? Error = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    });
}

public interface IHeadlessEncodeBatchProcessor
{
    Task<HeadlessEncodeJobResult> ExecuteOneAsync(
        string sourcePath,
        string? outputDirectory,
        string? qualityModeOverride,
        bool deleteSourceAfterCompression,
        bool requireStorageSavingsContract,
        int index,
        CancellationToken cancellationToken,
        Action<string>? progress = null);
}

public sealed class HeadlessEncodeFatalException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Serial executor: an item settles before the next item is submitted.</summary>
public static class HeadlessEncodeBatchRunner
{
    public static async Task<HeadlessEncodeBatchResult> RunAsync(
        HeadlessEncodeBatchCommand command,
        IHeadlessEncodeBatchProcessor processor,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(processor);
        DateTime startedUtc = DateTime.UtcNow;
        var jobs = new List<HeadlessEncodeJobResult>(command.SourcePaths.Count);
        bool fatal = false;

        for (int index = 0; index < command.SourcePaths.Count; index++)
        {
            string source = command.SourcePaths[index];
            if (fatal || cancellationToken.IsCancellationRequested)
            {
                jobs.Add(NotStarted(index + 1, source));
                continue;
            }

            try
            {
                progress?.Invoke($"Starting headless job {index + 1}/{command.SourcePaths.Count}: {source}");
                HeadlessEncodeJobResult result = await processor.ExecuteOneAsync(
                    source,
                    command.OutputDirectory,
                    command.QualityModeOverride,
                    command.DeleteSourceAfterCompression,
                    command.RequireStorageSavingsContract,
                    index + 1,
                    cancellationToken,
                    progress).ConfigureAwait(false);
                jobs.Add(result);
                progress?.Invoke($"Settled headless job {index + 1}/{command.SourcePaths.Count}: {result.Status}");
                if (result.FatalBatchFailure)
                    fatal = true;
                if (result.Status == HeadlessEncodeJobStatus.Cancelled)
                    cancellationToken = new CancellationToken(canceled: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                jobs.Add(new(index + 1, string.Empty, source, HeadlessEncodeJobStatus.Cancelled,
                    DateTime.UtcNow, DateTime.UtcNow, Error: "Batch cancellation requested."));
                cancellationToken = new CancellationToken(canceled: true);
            }
            catch (HeadlessEncodeFatalException ex)
            {
                fatal = true;
                jobs.Add(new(index + 1, string.Empty, source, HeadlessEncodeJobStatus.Failed,
                    DateTime.UtcNow, DateTime.UtcNow, Error: "Fatal batch failure: " + ex.Message));
            }
            catch (Exception ex)
            {
                fatal = true;
                jobs.Add(new(index + 1, string.Empty, source, HeadlessEncodeJobStatus.Failed,
                    DateTime.UtcNow, DateTime.UtcNow, Error: "Unexpected pipeline failure; batch stopped: " + ex.Message));
            }
        }

        DateTime endedUtc = DateTime.UtcNow;
        bool cancelled = jobs.Any(job => job.Status == HeadlessEncodeJobStatus.Cancelled);
        bool failed = jobs.Any(job => job.Status == HeadlessEncodeJobStatus.Failed);
        bool rejected = jobs.Any(job => job.Status is HeadlessEncodeJobStatus.Rejected or HeadlessEncodeJobStatus.Skipped);
        bool recovered = jobs.Any(job => job.Status == HeadlessEncodeJobStatus.RecoveredSuccess);
        (string Status, int ExitCode) aggregate = fatal
            ? ("FatalFailure", 8)
            : cancelled
                ? ("Cancelled", 5)
                : failed
                    ? ("Failed", 4)
                    : rejected
                        ? ("CompletedWithRejections", 6)
                        : recovered
                            ? ("RecoveredSuccess", 0)
                            : ("Success", 0);
        return new(1, aggregate.Status, aggregate.ExitCode, startedUtc, endedUtc, jobs.AsReadOnly());
    }

    private static HeadlessEncodeJobResult NotStarted(int index, string source) =>
        new(index, string.Empty, source, HeadlessEncodeJobStatus.NotStarted,
            DateTime.UtcNow, DateTime.UtcNow, Error: "Not started because the batch was cancelled or a fatal process-wide failure occurred.");
}
