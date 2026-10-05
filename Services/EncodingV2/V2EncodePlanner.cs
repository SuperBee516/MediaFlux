using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services.EncodingV2;

public interface IV2SourceMetadataProvider
{
    Task<V2MediaFacts> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default);
}

public interface IV2ProductionExecutor
{
    Task<V2ProcessOutcome> ExecuteAsync(
        FrozenEncodePlan plan,
        Action<TimeSpan>? processStarted = null,
        CancellationToken cancellationToken = default);

    Task<V2ProcessOutcome> ExecuteWithProgressAsync(
        FrozenEncodePlan plan,
        Action<TimeSpan>? processStarted,
        Action<V2FfmpegProgress>? progressUpdated,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(plan, processStarted, cancellationToken);
}

public interface IV2CandidateValidator
{
    Task<V2ValidationOutcome> ValidateAsync(
        FrozenEncodePlan plan,
        V2MediaFacts source,
        string candidatePath,
        CancellationToken cancellationToken = default);
}

public interface IV2OutputPromoter
{
    void Promote(string stagingPath, string outputPath);
}

/// <summary>Final source removal remains inside the trusted single-item V2 lifecycle.</summary>
public interface IV2SourceDeletionService
{
    bool TryDelete(FrozenEncodePlan plan, V2ValidationOutcome validation,
        CancellationToken cancellationToken, out string message);
}

public sealed class V2SafeSourceDeletionService : IV2SourceDeletionService
{
    public bool TryDelete(FrozenEncodePlan plan, V2ValidationOutcome validation,
        CancellationToken cancellationToken, out string message)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo promoted = new(plan.OutputPath);
            if (!promoted.Exists || (promoted.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                promoted.Length != validation.OutputLength || promoted.LastWriteTimeUtc.Ticks != validation.OutputLastWriteUtcTicks)
            {
                message = "Source was retained because the promoted output could not be verified.";
                return false;
            }
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
}

public sealed record V2ProcessOutcome(
    int ExitCode,
    bool TimedOut,
    TimeSpan ExecuteRequestToProcessStart,
    TimeSpan ProcessLaunchElapsed,
    string StandardError,
    string StandardOutput = "");

public sealed record V2ValidationOutcome(
    bool Success,
    string Summary,
    long OutputLength = 0,
    long OutputLastWriteUtcTicks = 0,
    string ErrorCode = "ValidationFailed");

public sealed class V2PlanningException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Captures source facts without consulting GUI, config, history, or V1 planning services.</summary>
public sealed class V2SourceMetadataProvider(IMediaProbeService probeService) : IV2SourceMetadataProvider
{
    private readonly IMediaProbeService _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));

    public async Task<V2MediaFacts> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        string fullPath = NormalizeExistingPath(sourcePath, "SourcePath");
        FileInfo before = new(fullPath);
        long beforeLength = before.Length;
        long beforeWriteTicks = before.LastWriteTimeUtc.Ticks;
        if (beforeLength <= 0)
            throw new V2PlanningException("InvalidSource", "The source file is empty.");

        MediaProbeResult probe = await _probeService.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!probe.Success)
            throw new V2PlanningException("ProbeFailed", "FFprobe could not inspect the source: " + probe.ErrorMessage);

        FileInfo after = new(fullPath);
        if (!after.Exists || after.Length != beforeLength || after.LastWriteTimeUtc.Ticks != beforeWriteTicks)
            throw new V2PlanningException("SourceChangedDuringProbe", "The source changed while it was being probed.");

        MediaProbeStreamInfo? video = probe.Streams
            .Where(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase))
            .OrderBy(stream => stream.Index)
            .FirstOrDefault();
        if (video is null || video.Width is not > 0 || video.Height is not > 0)
            throw new V2PlanningException("NoVideoStream", "The source must contain a readable video stream with known dimensions.");

        return new V2MediaFacts
        {
            SourcePath = fullPath,
            Probe = probe,
            SourceLength = beforeLength,
            SourceLastWriteUtcTicks = beforeWriteTicks,
            PrimaryVideoCodec = video.CodecName,
            SourceWidth = video.Width.Value,
            SourceHeight = video.Height.Value,
            AudioCodecs = Array.AsReadOnly(probe.Streams
                .Where(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                .OrderBy(stream => stream.Index)
                .Select(stream => stream.CodecName)
                .ToArray()),
            SubtitleCodecs = Array.AsReadOnly(probe.Streams
                .Where(stream => stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
                .OrderBy(stream => stream.Index)
                .Select(stream => stream.CodecName)
                .ToArray())
        };
    }

    internal static string NormalizeExistingPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new V2PlanningException("InvalidPath", $"{name} must be a fully qualified path.");
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new V2PlanningException("InvalidPath", $"{name} is invalid: {ex.Message}"); }
        if (!File.Exists(fullPath))
            throw new V2PlanningException("FileNotFound", $"{name} does not exist: {fullPath}");
        return fullPath;
    }
}

/// <summary>
/// Resolves every production decision and builds the complete FFmpeg argument list
/// before the lifecycle enters Planned.
/// </summary>
public sealed class V2EncodePlanner
{
    private static readonly HashSet<string> PixelFormats = new(StringComparer.OrdinalIgnoreCase)
    { "yuv420p", "yuv420p10le", "yuv422p", "yuv422p10le", "yuv444p", "yuv444p10le" };

    public FrozenEncodePlan CreatePlan(EncodingV2Request request, V2MediaFacts source, string ffmpegPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (request.SchemaVersion != 1)
            throw new V2PlanningException("UnsupportedSchema", $"Unsupported V2 request schema version {request.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            throw new V2PlanningException("FfmpegUnavailable", "A valid FFmpeg executable path is required.");
        string fullFfmpeg = Path.GetFullPath(ffmpegPath);
        string sourcePath = source.SourcePath;
        string outputPath = NormalizeDestinationPath(request.OutputPath);
        if (SamePath(sourcePath, outputPath))
            throw new V2PlanningException("SourceIsOutput", "The output path must be different from the source path.");
        if (!string.Equals(sourcePath, Path.GetFullPath(request.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new V2PlanningException("SourceIdentityMismatch", "The probed source does not match the request source path.");
        FileInfo sourceFile = new(sourcePath);
        if (!sourceFile.Exists || sourceFile.Length != source.SourceLength ||
            sourceFile.LastWriteTimeUtc.Ticks != source.SourceLastWriteUtcTicks)
            throw new V2PlanningException("SourceChanged", "The source changed after probing; create a new plan.");

        string encoder = NormalizeChoice(request.Encoder, "encoder");
        string codec = NormalizeChoice(request.Codec, "codec");
        ValidateEncoderCodec(encoder, codec);
        string container = NormalizeChoice(request.Container, "container");
        ValidateContainer(container, outputPath);
        string qualityMode = NormalizeChoice(request.QualityMode, "qualityMode");
        ValidateQuality(encoder, qualityMode, request.QualityValue);
        string preset = NormalizePreset(encoder, request.Preset);
        string pixelFormat = NormalizeChoice(request.PixelFormat, "pixelFormat");
        if (!PixelFormats.Contains(pixelFormat))
            throw new V2PlanningException("InvalidPixelFormat", $"Pixel format '{pixelFormat}' is not supported by the Phase 1 planner.");
        pixelFormat = PixelFormats.Single(value => value.Equals(pixelFormat, StringComparison.OrdinalIgnoreCase));

        string geometryMode = NormalizeChoice(request.GeometryMode, "geometryMode");
        (int width, int height, string? scaleFilter) = ResolveGeometry(request, source, geometryMode);
        string audioMode = NormalizeChoice(request.AudioMode, "audioMode");
        int? audioBitrate = request.AudioBitrateKbps;
        if (audioMode is not ("copy" or "aac" or "none"))
            throw new V2PlanningException("InvalidAudioMode", "audioMode must be copy, aac, or none.");
        if (audioMode == "aac" && audioBitrate is not (>= 32 and <= 512))
            throw new V2PlanningException("InvalidAudioBitrate", "AAC audio bitrate must be between 32 and 512 kbps.");
        if (audioMode != "aac" && audioBitrate.HasValue)
            throw new V2PlanningException("UnexpectedAudioBitrate", "audioBitrateKbps is valid only when audioMode is aac.");
        string subtitleMode = NormalizeChoice(request.SubtitleMode, "subtitleMode");
        if (subtitleMode is not ("copy" or "none"))
            throw new V2PlanningException("InvalidSubtitleMode", "subtitleMode must be copy or none.");

        string jobId = NormalizeJobId(request.JobId);
        string extension = Path.GetExtension(outputPath);
        string stagingPath = Path.Combine(Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.mediaflux-v2-{jobId}.partial{extension}");
        if (File.Exists(outputPath))
            throw new V2PlanningException("DestinationExists", "The output destination already exists; V2 will not overwrite it.");
        if (File.Exists(stagingPath) || Directory.Exists(stagingPath))
            throw new V2PlanningException("StagingPathExists", "The planned MediaFlux staging path already exists.");

        string[] arguments = BuildArguments(request, sourcePath, stagingPath, encoder, codec, container,
            qualityMode, preset, pixelFormat, audioMode, audioBitrate, subtitleMode, width, height, scaleFilter);

        return new FrozenEncodePlan(
            jobId, sourcePath, outputPath, stagingPath, fullFfmpeg, encoder, codec, container,
            qualityMode, request.QualityValue, preset, pixelFormat, audioMode, audioBitrate,
            subtitleMode, width, height, request.DeleteSourceOnSuccess,
            source.SourceLength, source.SourceLastWriteUtcTicks, source.Probe.DurationSeconds,
            source.Probe.FormatName, source.AudioCodecs.ToArray(), source.SubtitleCodecs.ToArray(),
            arguments, DateTime.UtcNow);
    }

    private static string[] BuildArguments(EncodingV2Request request, string sourcePath, string stagingPath,
        string encoder, string codec, string container, string qualityMode, string preset, string pixelFormat,
        string audioMode, int? audioBitrate, string subtitleMode, int width, int height, string? scaleFilter)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-nostdin", "-v", "error", "-xerror",
            "-progress", "pipe:2", "-stats_period", "1", "-i", sourcePath,
            "-map", "0:v:0"
        };
        if (audioMode == "none") args.Add("-an");
        else args.AddRange(["-map", "0:a?", "-c:a", audioMode == "aac" ? "aac" : "copy"]);
        if (audioMode == "aac") args.AddRange(["-b:a", audioBitrate!.Value.ToString(CultureInfo.InvariantCulture) + "k"]);
        if (subtitleMode == "copy") args.AddRange(["-map", "0:s?", "-c:s", "copy"]);
        else args.Add("-sn");
        args.AddRange(["-map_metadata", "0", "-map_chapters", "0", "-c:v", encoder]);
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", preset]);
            if (qualityMode == "cq") args.AddRange(["-rc", "vbr", "-cq", QualityText(request.QualityValue), "-b:v", "0"]);
            else args.AddRange(["-rc", "constqp", "-qp", QualityText(request.QualityValue)]);
        }
        else
        {
            args.AddRange(["-preset", preset, "-" + qualityMode, QualityText(request.QualityValue)]);
        }
        args.AddRange(["-pix_fmt", pixelFormat]);
        if (scaleFilter is not null) args.AddRange(["-vf", scaleFilter]);
        if (container == "mp4") args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", container == "mkv" ? "matroska" : "mp4", stagingPath]);
        return args.ToArray();
    }

    private static (int Width, int Height, string? Filter) ResolveGeometry(
        EncodingV2Request request, V2MediaFacts source, string mode)
    {
        if (mode == "source")
        {
            if (request.MaxWidth.HasValue || request.MaxHeight.HasValue)
                throw new V2PlanningException("UnexpectedGeometry", "maxWidth and maxHeight require geometryMode fitBox.");
            return (source.SourceWidth, source.SourceHeight, null);
        }
        if (mode != "fitbox" || request.MaxWidth is not (> 0) || request.MaxHeight is not (> 0) ||
            request.MaxWidth.Value % 2 != 0 || request.MaxHeight.Value % 2 != 0)
            throw new V2PlanningException("InvalidGeometry", "fitBox requires positive, even maxWidth and maxHeight values.");

        double ratio = Math.Min(1d, Math.Min((double)request.MaxWidth.Value / source.SourceWidth,
            (double)request.MaxHeight.Value / source.SourceHeight));
        int width = Math.Max(2, (int)Math.Floor(source.SourceWidth * ratio / 2) * 2);
        int height = Math.Max(2, (int)Math.Floor(source.SourceHeight * ratio / 2) * 2);
        return (width, height, $"scale={width}:{height}:flags=lanczos");
    }

    internal static void ValidateEncoderCodec(string encoder, string codec)
    {
        string expected = encoder switch
        {
            "libx264" or "h264_nvenc" => "h264",
            "libx265" or "hevc_nvenc" => "hevc",
            "libsvtav1" or "av1_nvenc" => "av1",
            _ => throw new V2PlanningException("UnsupportedEncoder", $"Encoder '{encoder}' is not supported in Phase 1.")
        };
        if (codec != expected)
            throw new V2PlanningException("CodecEncoderMismatch", $"Encoder '{encoder}' produces '{expected}', not '{codec}'.");
    }

    internal static void ValidateQuality(string encoder, string mode, double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > (encoder == "libsvtav1" ? 63 : 51))
            throw new V2PlanningException("InvalidQuality", "qualityValue is outside the supported 0–51/63 range.");
        bool nvenc = encoder.EndsWith("_nvenc", StringComparison.Ordinal);
        if (nvenc && mode is not ("cq" or "qp"))
            throw new V2PlanningException("InvalidQualityMode", "NVENC encoders require qualityMode cq or qp.");
        if (!nvenc && mode != "crf")
            throw new V2PlanningException("InvalidQualityMode", "Software Phase 1 encoders require qualityMode crf.");
    }

    internal static string NormalizePreset(string encoder, string preset)
    {
        string value = string.IsNullOrWhiteSpace(preset)
            ? encoder switch
            {
                "libx264" or "libx265" => "medium",
                "libsvtav1" => "6",
                _ => "p5"
            }
            : NormalizeChoice(preset, "preset");
        if (encoder is "libx264" or "libx265")
        {
            if (value is not ("ultrafast" or "superfast" or "veryfast" or "faster" or "fast" or "medium" or "slow" or "slower" or "veryslow"))
                throw new V2PlanningException("InvalidPreset", "x264/x265 preset is not in the supported fixed list.");
        }
        else if (encoder == "libsvtav1")
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int presetNumber) || presetNumber is < 0 or > 13)
                throw new V2PlanningException("InvalidPreset", "SVT-AV1 preset must be an integer from 0 through 13.");
        }
        else if (value.Length != 2 || value[0] != 'p' || value[1] is < '1' or > '7')
            throw new V2PlanningException("InvalidPreset", "NVENC preset must be p1 through p7.");
        return value;
    }

    private static void ValidateContainer(string container, string outputPath)
    {
        if (container is not ("mp4" or "mkv"))
            throw new V2PlanningException("UnsupportedContainer", "Phase 1 supports mp4 and mkv containers.");
        string extension = Path.GetExtension(outputPath);
        if (!extension.Equals("." + container, StringComparison.OrdinalIgnoreCase))
            throw new V2PlanningException("ContainerExtensionMismatch", "The output extension must match the requested container.");
    }

    private static string NormalizeDestinationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new V2PlanningException("InvalidPath", "OutputPath must be a fully qualified path.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new V2PlanningException("InvalidPath", "OutputPath is invalid: " + ex.Message); }
        string? directory = Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory))
            throw new V2PlanningException("OutputDirectoryMissing", "The output directory must already exist; V2 does not create it implicitly.");
        if (Directory.Exists(full))
            throw new V2PlanningException("OutputIsDirectory", "OutputPath names a directory.");
        return full;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeChoice(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new V2PlanningException("InvalidRequest", $"{name} is required.");
        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeJobId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("N");
        if (!Guid.TryParse(value, out Guid id)) throw new V2PlanningException("InvalidJobId", "jobId must be a GUID.");
        return id.ToString("N");
    }

    private static string QualityText(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
