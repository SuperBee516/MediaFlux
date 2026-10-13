using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>The exact selector request and plan at the frozen pre-production boundary.</summary>
public sealed record AdaptivePreAttemptObservation(
    EncodingPlanSnapshot Plan,
    AdaptiveQualitySelectionRequest? SelectionRequest,
    VideoRestorationSettings Restoration);

public sealed record AdaptivePreAttemptSourceIdentity(
    string CanonicalPath, long ByteLength, DateTime LastWriteUtc,
    string? CampaignCaseId, string? StableSourceId, string? Sha256);

public sealed record AdaptivePreAttemptRuntimeIdentity(
    string MediaFluxVersion, string? ImplementationId, string? ExecutableSha256,
    string? FfmpegSha256, string? FfprobeSha256, string? ResearchConfigSha256,
    string ResearchRoot);

public sealed record AdaptivePreAttemptStreamPlan(
    Guid PlanId, EncodingPlanSource? Source, EncodingPlanVideo? Video,
    EncodingPlanContainer? Container, EncodingPlanHardware? Hardware,
    IReadOnlyList<EncodingPlanStream> Audio, IReadOnlyList<EncodingPlanStream> Subtitles)
{
    public EncodingService.StreamMapMode MapMode { get; init; }
    public bool CopySubtitles { get; init; }
    public bool CopyDataStreams { get; init; }
    public bool CopyAttachments { get; init; }
    public OutputContainerDecision? ContainerDecision { get; init; }
}

/// <summary>Schema 1 contains only facts available before production, never terminal outcomes.</summary>
public sealed record AdaptivePreAttemptEvidenceRecord(
    int SchemaVersion, string CaptureRevision, string OperationId,
    Guid? SavedJobId, long? QueueRowId, AdaptivePreAttemptSourceIdentity Source,
    DateTime CapturedUtc, AdaptivePreAttemptRuntimeIdentity Runtime,
    StorageSavingsContract Contract, AdaptiveQualityEnvelope Envelope, AdaptiveVideoSettings Video,
    TimeSpan SourceDuration, VideoRestorationSettings Restoration,
    AdaptivePreAttemptStreamPlan StreamPlan, AdaptiveQualitySelectionEvidence Selection,
    bool AdaptiveStorageSavingsEnabled, bool ExperimentalPolicyCRetryEnabled,
    bool ResearchCaptureEnabled,
    bool ProductionAttempt1Started = false, int ProductionAttemptCount = 0,
    bool OrdinaryAdaptiveApplicable = true);

public sealed record AdaptivePreAttemptCaptureAcknowledgment(
    bool Success, string RecordPath, string Sha256, DateTime CapturedUtc,
    double SerializationSeconds, double DurableWriteSeconds, double TotalSeconds);

public sealed class AdaptivePreAttemptEvidenceCaptureException : InvalidOperationException
{
    public AdaptivePreAttemptEvidenceCaptureException(string reason, Exception? inner = null)
        : base("Research pre-attempt evidence capture failed; production attempt 1 was not started: " + reason, inner) { }
}

public interface IAdaptivePreAttemptEvidenceSink
{
    Task<AdaptivePreAttemptCaptureAcknowledgment> CaptureAsync(
        AdaptivePreAttemptEvidenceRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Uses the immutable freeze-store pattern: same-directory CreateNew/WriteThrough,
/// disk flush, and atomic no-overwrite rename. Construction has no filesystem effects.
/// </summary>
public sealed class AdaptivePreAttemptEvidenceStore : IAdaptivePreAttemptEvidenceSink
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string _root;
    public AdaptivePreAttemptEvidenceStore(string userDataDirectory) => _root = Path.GetFullPath(userDataDirectory);
    public string DirectoryPath => Path.Combine(_root, "data", "policy-c-pre-attempt");

    public async Task<AdaptivePreAttemptCaptureAcknowledgment> CaptureAsync(
        AdaptivePreAttemptEvidenceRecord record, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve the stable operation ID in its filename and reject all path syntax.
            if (!Guid.TryParseExact(record.OperationId, "N", out Guid operation) && !Guid.TryParseExact(record.OperationId, "D", out operation))
                throw new InvalidDataException("The evidence operation ID must be a GUID in N or D format.");
            if (record.SchemaVersion != 1 || record.ProductionAttempt1Started || record.ProductionAttemptCount != 0)
                throw new InvalidDataException("Invalid pre-attempt stage/schema.");
            if (!string.Equals(Path.GetFullPath(record.Runtime.ResearchRoot), _root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Evidence root differs from the captured execution root.");
            var serialization = Stopwatch.StartNew();
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            serialization.Stop();
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var write = Stopwatch.StartNew();
            string destination = Path.Combine(DirectoryPath, operation.ToString("N") + ".json");
            Directory.CreateDirectory(DirectoryPath);
            if (File.Exists(destination)) throw new IOException("An immutable pre-attempt record already exists for this operation.");
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            write.Stop();
            return new(true, destination, hash, record.CapturedUtc,
                serialization.Elapsed.TotalSeconds, write.Elapsed.TotalSeconds, total.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new AdaptivePreAttemptEvidenceCaptureException(ex.Message, ex);
        }
        finally
        {
            // Only this invocation's unpublished temporary file can be removed.
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
