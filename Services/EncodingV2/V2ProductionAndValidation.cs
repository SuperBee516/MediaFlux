using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux.Services.EncodingV2;

/// <summary>Mechanical production launcher. It receives an already frozen invocation and makes one runner call.</summary>
public sealed class V2ProductionExecutor(IMediaToolProcessRunner processRunner) : IV2ProductionExecutor
{
    private readonly IMediaToolProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public async Task<V2ProcessOutcome> ExecuteAsync(
        FrozenEncodePlan plan,
        Action<TimeSpan>? processStarted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.EnsureExecutable();
        long executeRequestedAt = Stopwatch.GetTimestamp();
        TimeSpan requestToStart = TimeSpan.Zero;
        MediaToolProcessResult result = await _processRunner.RunAsync(new MediaToolProcessRequest
        {
            FileName = plan.FfmpegPath,
            Arguments = plan.Arguments,
            Timeout = TimeSpan.FromHours(12),
            SendQuitOnCancellation = true,
            DiagnosticComponent = FfmpegDiagnosticComponent.Ffmpeg,
            ProcessStartedCallback = _ =>
            {
                requestToStart = Stopwatch.GetElapsedTime(executeRequestedAt);
                try { processStarted?.Invoke(requestToStart); }
                catch { /* Observers do not control the production process. */ }
            }
        }, cancellationToken).ConfigureAwait(false);

        return new V2ProcessOutcome(
            result.ExitCode,
            result.TimedOut,
            requestToStart,
            result.ProcessLaunchElapsed,
            result.StandardError,
            result.StandardOutput);
    }
}

/// <summary>V2 validation checks the candidate against the frozen plan and source facts only.</summary>
public sealed class V2OutputValidator : IV2CandidateValidator
{
    private readonly IV2SourceMetadataProvider _metadataProvider;
    private readonly IDecodeIntegritySpotCheckService _decodeCheck;

    public V2OutputValidator(
        IV2SourceMetadataProvider metadataProvider,
        IDecodeIntegritySpotCheckService decodeCheck)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _decodeCheck = decodeCheck ?? throw new ArgumentNullException(nameof(decodeCheck));
    }

    public async Task<V2ValidationOutcome> ValidateAsync(
        FrozenEncodePlan plan,
        V2MediaFacts source,
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        string fullCandidate;
        try { fullCandidate = Path.GetFullPath(candidatePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return Invalid("InvalidCandidatePath", ex.Message); }
        if (string.Equals(fullCandidate, plan.SourcePath, StringComparison.OrdinalIgnoreCase))
            return Invalid("SourceIsCandidate", "The candidate path resolves to the original source.");
        if (!File.Exists(fullCandidate))
            return Invalid("CandidateMissing", "The staged candidate file does not exist.");

        long length;
        long lastWriteTicks;
        try
        {
            FileInfo file = new(fullCandidate);
            if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return Invalid("UnsafeCandidateType", "The candidate is not a regular MediaFlux staging file.");
            length = file.Length;
            lastWriteTicks = file.LastWriteTimeUtc.Ticks;
            if (length <= 0) return Invalid("EmptyCandidate", "FFmpeg produced an empty candidate.");
        }
        catch (Exception ex) { return Invalid("CandidateInspectionFailed", ex.Message); }

        V2MediaFacts candidate;
        try { candidate = await _metadataProvider.ProbeAsync(fullCandidate, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Invalid("OutputProbeFailed", ex.Message); }

        MediaProbeStreamInfo? video = candidate.Probe.Streams
            .Where(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase))
            .OrderBy(stream => stream.Index)
            .FirstOrDefault();
        if (video is null || !video.CodecName.Equals(plan.Codec, StringComparison.OrdinalIgnoreCase))
            return Invalid("VideoCodecMismatch", $"Output video codec was '{video?.CodecName ?? "missing"}', expected '{plan.Codec}'.");
        if (video.Width != plan.Width || video.Height != plan.Height)
            return Invalid("GeometryMismatch", $"Output dimensions were {video.Width}x{video.Height}, expected {plan.Width}x{plan.Height}.");
        if (!video.PixelFormat.Equals(plan.PixelFormat, StringComparison.OrdinalIgnoreCase))
            return Invalid("PixelFormatMismatch", $"Output pixel format was '{video.PixelFormat}', expected '{plan.PixelFormat}'.");
        if (!ContainerMatches(plan.Container, candidate.Probe.FormatName))
            return Invalid("ContainerMismatch", $"Output container '{candidate.Probe.FormatName}' does not match '{plan.Container}'.");
        if (plan.SourceDurationSeconds is > 0 && candidate.Probe.DurationSeconds is > 0)
        {
            double tolerance = Math.Max(.75, plan.SourceDurationSeconds.Value * .02);
            if (Math.Abs(candidate.Probe.DurationSeconds.Value - plan.SourceDurationSeconds.Value) > tolerance)
                return Invalid("DurationMismatch", "Output duration differs from the probed source duration beyond the Phase 1 tolerance.");
        }

        if (plan.AudioMode == "none" && candidate.AudioCodecs.Count != 0)
            return Invalid("AudioMappingMismatch", "Audio was present although audioMode is none.");
        if (plan.AudioMode != "none")
        {
            if (candidate.AudioCodecs.Count != plan.SourceAudioCodecs.Count)
                return Invalid("AudioMappingMismatch", "The output audio stream count differs from the frozen source mapping.");
            if (plan.AudioMode == "aac" && candidate.AudioCodecs.Any(codec => !codec.Equals("aac", StringComparison.OrdinalIgnoreCase)))
                return Invalid("AudioCodecMismatch", "An output audio stream was not AAC as specified by the frozen plan.");
            if (plan.AudioMode == "copy" && !SameCodecs(candidate.AudioCodecs, plan.SourceAudioCodecs))
                return Invalid("AudioCodecMismatch", "Copied output audio codecs differ from the source.");
        }
        if (plan.SubtitleMode == "none" && candidate.SubtitleCodecs.Count != 0)
            return Invalid("SubtitleMappingMismatch", "Subtitles were present although subtitleMode is none.");
        if (plan.SubtitleMode == "copy" &&
            (candidate.SubtitleCodecs.Count != plan.SourceSubtitleCodecs.Count ||
             !SameCodecs(candidate.SubtitleCodecs, plan.SourceSubtitleCodecs)))
            return Invalid("SubtitleMappingMismatch", "Copied output subtitle streams differ from the source mapping.");

        DecodeIntegritySpotCheckResult decode = await _decodeCheck.CheckAsync(
            fullCandidate, candidate.Probe.DurationSeconds, cancellationToken).ConfigureAwait(false);
        if (!decode.Success)
            return Invalid("DecodeIntegrityFailed", decode.ErrorMessage);

        try
        {
            FileInfo after = new(fullCandidate);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc.Ticks != lastWriteTicks)
                return Invalid("CandidateChangedDuringValidation", "The candidate changed while it was being validated.");
        }
        catch (Exception ex) { return Invalid("CandidateRecheckFailed", ex.Message); }

        return new V2ValidationOutcome(true,
            "FFprobe stream, codec, geometry, format, duration, and decode-integrity checks passed.",
            length, lastWriteTicks, "");
    }

    private static bool ContainerMatches(string container, string probedFormat)
    {
        string expected = container == "mp4" ? "mp4" : "matroska";
        return probedFormat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(format => format.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameCodecs(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase));

    private static V2ValidationOutcome Invalid(string code, string message) => new(false, message, ErrorCode: code);
}

/// <summary>Thin adapter over the existing non-overwriting atomic file-move primitive.</summary>
public sealed class V2FileOutputPromoter : IV2OutputPromoter
{
    private readonly FileEncodeOutputPromoter _promoter = new();
    public void Promote(string stagingPath, string outputPath) => _promoter.Promote(stagingPath, outputPath);
}
