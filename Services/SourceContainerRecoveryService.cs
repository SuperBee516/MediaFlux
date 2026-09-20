using MediaFlux.Models;

namespace MediaFlux.Services;

internal enum SourceContainerRecoveryFailureKind
{
    None,
    Infrastructure,
    Media,
    Validation
}

internal sealed record SourceContainerRecoveryResult(
    bool Success,
    string RepairedPath,
    MediaProbeResult? RepairedProbe,
    string Reason,
    bool ProcessCompleted,
    bool ValidationPassed,
    SourceContainerRecoveryFailureKind FailureKind = SourceContainerRecoveryFailureKind.None)
{
    public bool IndicatesMediaFailure =>
        FailureKind is SourceContainerRecoveryFailureKind.Media or SourceContainerRecoveryFailureKind.Validation;

    public static SourceContainerRecoveryResult Failed(
        string reason,
        bool processCompleted = false,
        SourceContainerRecoveryFailureKind failureKind = SourceContainerRecoveryFailureKind.Infrastructure) =>
        new(false, "", null, reason, processCompleted, false, failureKind);
}

/// <summary>
/// Performs the single non-destructive stream-copy remux allowed after strong
/// source-integrity evidence. A successful remux process is never treated as
/// proof that the repaired source is decodable.
/// </summary>
internal sealed class SourceContainerRecoveryService
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly IDecodeIntegritySpotCheckService _decodeIntegrity;
    private readonly Action<string>? _log;

    public SourceContainerRecoveryService(
        string ffmpegPath,
        string ffprobePath,
        IMediaToolProcessRunner? runner = null,
        IDecodeIntegritySpotCheckService? decodeIntegrity = null,
        Action<string>? log = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _runner = runner ?? new MediaToolProcessRunner();
        _decodeIntegrity = decodeIntegrity ?? new FfmpegDecodeIntegritySpotCheckService(_ffmpegPath, _runner);
        _log = log;
    }

    public async Task<SourceContainerRecoveryResult> TryRemuxAndValidateAsync(
        string sourcePath,
        string repairedPath,
        MediaProbeResult originalProbe,
        double? durationSeconds,
        CancellationToken cancellationToken,
        Action? validationStarted = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(repairedPath) ||
            sourcePath.Equals(repairedPath, StringComparison.OrdinalIgnoreCase))
            return SourceContainerRecoveryResult.Failed("The temporary source-recovery path was invalid or replaced the original source.");

        string stagingPath = repairedPath + ".partial";
        try
        {
            TryDelete(stagingPath);
            TryDelete(repairedPath);
            MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffmpegPath,
                Arguments = ["-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", sourcePath,
                    "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-dn",
                    "-c", "copy", "-map_metadata", "0", "-map_chapters", "0", "-f", "matroska", stagingPath],
                Timeout = TimeSpan.FromMinutes(5)
            }, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || process.TimedOut || !File.Exists(stagingPath) || new FileInfo(stagingPath).Length == 0)
                return SourceContainerRecoveryResult.Failed(
                    $"Container remux did not complete: exit={process.ExitCode}; timed-out={process.TimedOut}; diagnostics={process.StandardError.Trim()}",
                    processCompleted: !process.TimedOut,
                    failureKind: IsInfrastructureFailure(process) ? SourceContainerRecoveryFailureKind.Infrastructure : SourceContainerRecoveryFailureKind.Media);

            MediaProbeResult repairedProbe = await new FfprobeService(_ffprobePath, _runner)
                .ProbeAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            if (!repairedProbe.Success)
                return SourceContainerRecoveryResult.Failed("The remuxed temporary source could not be re-probed: " + repairedProbe.ErrorMessage, true, SourceContainerRecoveryFailureKind.Validation);
            if (!SourceTimelineRecoveryService.IsEquivalent(originalProbe, repairedProbe, out string equivalenceError))
                return SourceContainerRecoveryResult.Failed("Container remux changed the required source structure: " + equivalenceError, true, SourceContainerRecoveryFailureKind.Validation);

            validationStarted?.Invoke();
            DecodeIntegritySpotCheckResult decode = await _decodeIntegrity
                .CheckAsync(stagingPath, durationSeconds, cancellationToken).ConfigureAwait(false);
            if (!decode.Success)
                return SourceContainerRecoveryResult.Failed(
                    "Recovery validation failed; the remuxed source remains undecodable: " + decode.ErrorMessage, true, SourceContainerRecoveryFailureKind.Validation);

            if (HasDisqualifyingStreamCopyCorruption(process.DiagnosticSummary))
                return SourceContainerRecoveryResult.Failed(
                    "Recovery validation rejected the stream-copy artifact because the complete remux emitted affirmative source corruption evidence; sampled decode validation passed but cannot repair copied encoded payload.",
                    true,
                    SourceContainerRecoveryFailureKind.Validation);

            File.Move(stagingPath, repairedPath, overwrite: false);
            _log?.Invoke($"[EncodingRecovery] Container remux validation passed: repaired={repairedPath}; samples={string.Join(",", decode.PositionsSeconds.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}.");
            return new(true, repairedPath, repairedProbe,
                "Container stream-copy remux completed and sampled decode validation passed.", true, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return SourceContainerRecoveryResult.Failed("Container remux recovery failed: " + ex.Message); }
        finally
        {
            TryDelete(stagingPath);
            if (!File.Exists(repairedPath))
                TryDelete(repairedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static bool IsInfrastructureFailure(MediaToolProcessResult process)
    {
        if (process.TimedOut || process.ExitCode == 0)
            return true;

        string error = process.StandardError;
        return error.Contains("Unable to choose an output format", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Error initializing the muxer", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Error initializing output", StringComparison.OrdinalIgnoreCase) ||
               (error.Contains("Invalid argument", StringComparison.OrdinalIgnoreCase) &&
                !error.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasDisqualifyingStreamCopyCorruption(FfmpegDiagnosticSummary? summary)
    {
        if (summary is null)
            return false;

        HashSet<string> families = summary.Families
            .Where(family => family.Category is FfmpegDiagnosticCategory.SourceIntegrity or FfmpegDiagnosticCategory.SourceDecode)
            .Select(family => family.Family)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool pairedH264Corruption = families.Contains("Invalid NAL unit size") &&
            families.Contains("Missing picture in access unit");
        bool strongDecoderFailure = families.Contains("Error splitting input into NAL units") ||
            families.Contains("Decoder packet submission failure") ||
            families.Contains("Decoder packet processing failure") ||
            families.Contains("Invalid input data");
        return pairedH264Corruption || strongDecoderFailure;
    }
}
