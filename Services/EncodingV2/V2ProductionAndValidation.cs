using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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
        CancellationToken cancellationToken = default) =>
        await ExecuteWithProgressAsync(plan, processStarted, null, cancellationToken).ConfigureAwait(false);

    public async Task<V2ProcessOutcome> ExecuteWithProgressAsync(
        FrozenEncodePlan plan,
        Action<TimeSpan>? processStarted,
        Action<V2FfmpegProgress>? progressUpdated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.EnsureExecutable();
        long executeRequestedAt = Stopwatch.GetTimestamp();
        TimeSpan requestToStart = TimeSpan.Zero;
        var progressParser = new V2FfmpegProgressParser(plan.SourceDurationSeconds);
        MediaToolProcessResult result = await _processRunner.RunAsync(new MediaToolProcessRequest
        {
            FileName = plan.FfmpegPath,
            Arguments = plan.Arguments,
            Timeout = TimeSpan.FromHours(12),
            SendQuitOnCancellation = true,
            DiagnosticComponent = FfmpegDiagnosticComponent.Ffmpeg,
            StandardErrorLineCallback = line =>
            {
                if (!progressParser.TryObserveLine(line, out V2FfmpegProgress? progress) || progress is null) return;
                try { progressUpdated?.Invoke(progress); } catch { /* Progress observers cannot affect the encode. */ }
            },
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

internal sealed class V2FfmpegProgressParser(double? sourceDurationSeconds)
{
    private static readonly Regex HumanStatistics = new(
        @"(?<key>frame|fps|q|size|time|bitrate|speed)=\s*(?<value>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private long? _frame;
    private double? _fps, _quantizer, _outputSeconds, _bitrate, _speed;
    private string? _outputSize;
    private bool _hasFields;

    public bool TryObserveLine(string line, out V2FfmpegProgress? progress)
    {
        progress = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        int separator = line.IndexOf('=');
        if (separator > 0 && line.IndexOf('=', separator + 1) < 0)
        {
            string key = line[..separator].Trim().ToLowerInvariant();
            string value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "frame":
                case "fps":
                case "q":
                case "total_size":
                case "size":
                case "time":
                case "out_time":
                case "out_time_us":
                case "out_time_ms":
                case "bitrate":
                case "speed":
                    ObserveMachineField(key, value);
                    return false;
                case "progress":
                    if (!_hasFields || !(value.Equals("continue", StringComparison.OrdinalIgnoreCase) ||
                                          value.Equals("end", StringComparison.OrdinalIgnoreCase))) return false;
                    progress = Snapshot();
                    _hasFields = false;
                    return true;
            }
        }

        bool found = false;
        foreach (Match match in HumanStatistics.Matches(line))
        {
            string key = match.Groups["key"].Value.ToLowerInvariant();
            string value = match.Groups["value"].Value;
            switch (key)
            {
                case "frame":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frameValue)) { _frame = frameValue; found = true; }
                    break;
                case "fps":
                    _fps = ParseNumber(value); found |= _fps.HasValue; break;
                case "q":
                    _quantizer = ParseNumber(value); found |= _quantizer.HasValue; break;
                case "size":
                    _outputSize = value; found = true; break;
                case "time":
                    if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan time))
                    { _outputSeconds = Math.Max(0, time.TotalSeconds); found = true; }
                    break;
                case "bitrate":
                    _bitrate = ParseNumber(value); found |= _bitrate.HasValue; break;
                case "speed":
                    _speed = ParseNumber(value); found |= _speed.HasValue; break;
            }
        }
        if (!found) return false;
        progress = Snapshot();
        return true;
    }

    private void ObserveMachineField(string key, string value)
    {
        switch (key)
        {
            case "frame":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frame)) _frame = frame;
                else return;
                break;
            case "fps": _fps = ParseNumber(value); break;
            case "q": _quantizer = ParseNumber(value); break;
            case "total_size":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) && size >= 0)
                    _outputSize = size.ToString(CultureInfo.InvariantCulture) + " bytes";
                else return;
                break;
            case "size": _outputSize = value; break;
            case "time":
            case "out_time":
                if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan time)) return;
                _outputSeconds = Math.Max(0, time.TotalSeconds);
                break;
            case "out_time_ms":
            case "out_time_us":
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long micros) || micros < 0) return;
                _outputSeconds = micros / 1_000_000d;
                break;
            case "bitrate": _bitrate = ParseNumber(value); break;
            case "speed": _speed = ParseNumber(value); break;
        }
        _hasFields = true;
    }

    private V2FfmpegProgress Snapshot()
    {
        double? percent = sourceDurationSeconds is > 0 && _outputSeconds.HasValue
            ? Math.Clamp(_outputSeconds.Value / sourceDurationSeconds.Value * 100d, 0d, 100d)
            : null;
        return new V2FfmpegProgress(_frame, _fps, _quantizer, _outputSize,
            _outputSeconds, _bitrate, _speed, percent);
    }

    private static double? ParseNumber(string value)
    {
        int length = 0;
        while (length < value.Length && (char.IsAsciiDigit(value[length]) || value[length] is '.' or '+' or '-')) length++;
        return length > 0 && double.TryParse(value.AsSpan(0, length), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
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
