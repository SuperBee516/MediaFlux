using System.Diagnostics;
using System.Globalization;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux.Services.EncodingV2;

/// <summary>Resolved facts consumed by the deterministic V2 fast estimator.</summary>
public sealed record V2FastEstimateSourceFacts
{
    public string SourcePath { get; init; } = "";
    public long SourceSizeBytes { get; init; }
    public double DurationSeconds { get; init; }
    public string VideoCodec { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public double? FrameRate { get; init; }
    public double? VideoBitrateKbps { get; init; }
    public IReadOnlyList<V2FastEstimateAudioStreamFacts> AudioStreams { get; init; } = Array.Empty<V2FastEstimateAudioStreamFacts>();
    public IReadOnlyList<string> SubtitleCodecs { get; init; } = Array.Empty<string>();

    public static V2FastEstimateSourceFacts FromMediaFacts(V2MediaFacts source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MediaProbeStreamInfo video = source.Probe.Streams
            .Where(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase))
            .OrderBy(stream => stream.Index)
            .FirstOrDefault() ?? throw new V2PlanningException("NoVideoStream", "The source facts do not contain a video stream.");
        double? frameRate = Positive(video.AverageFrameRate) ?? Positive(video.FrameRate) ?? Positive(video.NominalFrameRate);
        return new V2FastEstimateSourceFacts
        {
            SourcePath = source.SourcePath,
            SourceSizeBytes = source.SourceLength,
            DurationSeconds = source.Probe.DurationSeconds ?? video.DurationSeconds ?? 0,
            VideoCodec = source.PrimaryVideoCodec,
            Width = source.SourceWidth,
            Height = source.SourceHeight,
            FrameRate = frameRate,
            VideoBitrateKbps = Positive(video.BitRate) / 1000d,
            AudioStreams = Array.AsReadOnly(source.Probe.Streams
                .Where(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                .OrderBy(stream => stream.Index)
                .Select(stream => new V2FastEstimateAudioStreamFacts
                {
                    Codec = stream.CodecName,
                    BitrateKbps = Positive(stream.BitRate) / 1000d,
                    DurationSeconds = Positive(stream.DurationSeconds) ?? source.Probe.DurationSeconds
                }).ToArray()),
            SubtitleCodecs = Array.AsReadOnly(source.Probe.Streams
                .Where(stream => stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
                .OrderBy(stream => stream.Index)
                .Select(stream => stream.CodecName).ToArray())
        };
    }

    private static double? Positive(double? value) => value is double number && double.IsFinite(number) && number > 0 ? number : null;
    private static double? Positive(long? value) => value is long number && number > 0 ? number : null;
}

public sealed class V2FastEstimateAudioStreamFacts
{
    public string Codec { get; init; } = "";
    public double? BitrateKbps { get; init; }
    public long? SizeBytes { get; init; }
    public double? DurationSeconds { get; init; }
}

/// <summary>Machine-readable headless input. Quality-set and benchmark commands reuse this same source/configuration.</summary>
public sealed record V2FastEstimateCommandRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string SourcePath { get; init; } = "";
    public V2FastEstimateSourceFacts? SourceFacts { get; init; }
    public string Encoder { get; init; } = "libx265";
    public string Codec { get; init; } = "hevc";
    public string Container { get; init; } = "mp4";
    public string QualityMode { get; init; } = "crf";
    public double QualityValue { get; init; } = 28;
    public double? ProductionPlanQualityValue { get; init; }
    public string Preset { get; init; } = "";
    public string GeometryMode { get; init; } = "source";
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public string AudioMode { get; init; } = "copy";
    public int? AudioBitrateKbps { get; init; }
    public string SubtitleMode { get; init; } = "none";
    public double LittleSavingsThresholdPercent { get; init; } = V2SavingsClassificationPolicy.ProvisionalDefaultThresholdPercent;
    public bool ProcessAll { get; init; }
    public string? FfprobePath { get; init; }
    public double[]? QualityValues { get; init; }
    public int Repetitions { get; init; } = 100;
    /// <summary>Optional rapid UI-like quality changes for coordinator certification.</summary>
    public double[]? RefreshQualitySequence { get; init; }
    /// <summary>Optional cached-facts bulk refresh count for application-layer certification.</summary>
    public int BulkItemCount { get; init; }
}

public sealed record V2FastEstimateRequest
{
    public required V2FastEstimateSourceFacts SourceFacts { get; init; }
    public string Encoder { get; init; } = "libx265";
    public string Codec { get; init; } = "hevc";
    public string Container { get; init; } = "mp4";
    public string QualityMode { get; init; } = "crf";
    public double QualityValue { get; init; } = 28;
    public double? ProductionPlanQualityValue { get; init; }
    public string Preset { get; init; } = "";
    public string GeometryMode { get; init; } = "source";
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public string AudioMode { get; init; } = "copy";
    public int? AudioBitrateKbps { get; init; }
    public string SubtitleMode { get; init; } = "none";
    public double LittleSavingsThresholdPercent { get; init; } = V2SavingsClassificationPolicy.ProvisionalDefaultThresholdPercent;
    public bool ProcessAll { get; init; }
}

public enum V2EstimateConfidence { High, Medium, Low }
public enum V2SavingsClassificationKind { NoSavings, LittleSavings, MeaningfulSavings }

public sealed record V2SavingsClassificationPolicy(double LittleSavingsThresholdPercent = V2SavingsClassificationPolicy.ProvisionalDefaultThresholdPercent,
    bool ProcessAll = false)
{
    public const double ProvisionalDefaultThresholdPercent = 5d;
}

public sealed record V2SavingsClassification(
    V2SavingsClassificationKind Kind,
    double ThresholdPercent,
    bool ProcessAll,
    bool RecommendedSkip);

public sealed record V2FastEstimateResult(
    string SourceIdentity,
    long SourceSizeBytes,
    double DurationSeconds,
    string Encoder,
    string Codec,
    string Container,
    string QualityMode,
    double QualityValue,
    string Preset,
    string GeometryMode,
    int Width,
    int Height,
    string AudioMode,
    int? AudioBitrateKbps,
    long EstimatedVideoBytes,
    long EstimatedAudioBytes,
    long EstimatedContainerOverheadBytes,
    long EstimatedTotalBytes,
    long EstimatedSavingsBytes,
    double EstimatedSavingsPercent,
    V2SavingsClassification SavingsClassification,
    V2EstimateConfidence Confidence,
    string EstimateBasis,
    double CalculationMilliseconds)
{
    public ProductionPlanIdentity? ProductionPlan { get; init; }
}

public sealed record V2FastEstimateResolvedSource(
    V2FastEstimateSourceFacts Facts,
    bool ProbeOccurred,
    double ProbeMilliseconds);

public sealed record V2FastEstimateBenchmarkResult(
    double ColdProbeMilliseconds,
    bool SourceFactsWereSupplied,
    double FirstEstimateMilliseconds,
    IReadOnlyList<double> WarmEstimateMilliseconds,
    double WarmP50Milliseconds,
    double WarmP95Milliseconds,
    double WarmMaximumMilliseconds,
    int TotalCalculations,
    int FfmpegProductionInvocationCount,
    IReadOnlyList<double> QualityValues,
    V2FastEstimateResult FirstEstimate);

/// <summary>
/// Pure V2 size arithmetic plus an optional one-time metadata boundary. This class has no FFmpeg,
/// adaptive, history, preview, research, GUI, or queue-execution dependency.
/// </summary>
public static class V2FastEstimateService
{
    private const double ContainerOverheadFractionMp4 = 0.015;
    private const double ContainerOverheadFractionMkv = 0.02;
    private const long MinimumContainerOverheadBytes = 4096;
    private const double MinimumSourceComplexity = 0.55;
    private const double MaximumSourceComplexity = 1.65;

    public static V2FastEstimateResult Estimate(V2FastEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceFacts);
        long started = Stopwatch.GetTimestamp();
        V2FastEstimateSourceFacts facts = request.SourceFacts;
        ValidateFacts(facts);

        string encoder = Normalize(request.Encoder, "encoder");
        string codec = Normalize(request.Codec, "codec");
        string qualityMode = Normalize(request.QualityMode, "qualityMode");
        V2EncodePlanner.ValidateEncoderCodec(encoder, codec);
        V2EncodePlanner.ValidateQuality(encoder, qualityMode, request.QualityValue);
        string preset = V2EncodePlanner.NormalizePreset(encoder, request.Preset);
        string container = Normalize(request.Container, "container");
        if (container is not ("mp4" or "mkv"))
            throw new V2PlanningException("UnsupportedContainer", "Phase 1 supports mp4 and mkv containers.");
        string audioMode = Normalize(request.AudioMode, "audioMode");
        if (audioMode is not ("copy" or "aac" or "none"))
            throw new V2PlanningException("InvalidAudioMode", "audioMode must be copy, aac, or none.");
        string subtitleMode = Normalize(request.SubtitleMode, "subtitleMode");
        if (subtitleMode == "auto")
            subtitleMode = facts.SubtitleCodecs.Count == 0 ? "none" : "copy";
        if (subtitleMode is not ("copy" or "none"))
            throw new V2PlanningException("InvalidSubtitleMode", "subtitleMode must be copy or none.");
        if (audioMode == "aac" && request.AudioBitrateKbps is not (>= 32 and <= 512))
            throw new V2PlanningException("InvalidAudioBitrate", "AAC audio bitrate must be between 32 and 512 kbps.");
        if (audioMode != "aac" && request.AudioBitrateKbps.HasValue)
            throw new V2PlanningException("UnexpectedAudioBitrate", "audioBitrateKbps is valid only when audioMode is aac.");

        (int width, int height) = ResolveGeometry(request, facts);
        double fps = Positive(facts.FrameRate) ?? 30d;
        double totalSourceKbps = facts.SourceSizeBytes * 8d / facts.DurationSeconds / 1000d;
        (long audioBytes, bool outputAudioFallback) = EstimateAudio(facts, audioMode, request.AudioBitrateKbps);
        (double sourceAudioKbps, bool sourceAudioFallback) = EstimateSourceAudioBitrate(facts);
        double measuredVideoKbps = Positive(facts.VideoBitrateKbps) ?? 0;
        double sourceVideoKbps = measuredVideoKbps > 0
            ? measuredVideoKbps
            : Math.Max(1d, totalSourceKbps - sourceAudioKbps - Math.Max(16d, totalSourceKbps * 0.01d));
        double sourcePixelsPerSecond = (double)facts.Width * facts.Height * fps;
        double sourceBpp = sourceVideoKbps * 1000d / sourcePixelsPerSecond;
        double targetPixelsPerSecond = (double)width * height * fps;
        double sourceCodecBpp = SourceCodecBitsPerPixel(facts.VideoCodec, out bool sourceCodecKnown);
        double rawSourceComplexity = Math.Sqrt(sourceBpp / sourceCodecBpp);
        double sourceComplexity = Math.Clamp(rawSourceComplexity, MinimumSourceComplexity, MaximumSourceComplexity);
        double targetCodecBpp = TargetCodecBitsPerPixel(codec);
        double targetEncoderFactor = TargetEncoderFactor(encoder);
        double qualityAnchor = QualityAnchor(encoder, qualityMode);
        double qualityScale = Math.Pow(2d, (qualityAnchor - request.QualityValue) / 6d);
        double presetScale = GetPresetRateMultiplier(encoder, preset);
        double targetBpp = targetCodecBpp * targetEncoderFactor * sourceComplexity * qualityScale * presetScale;
        double targetVideoKbps = targetBpp * targetPixelsPerSecond / 1000d;
        long videoBytes = ToBytes(targetVideoKbps * 1000d / 8d * facts.DurationSeconds);
        double containerRate = container == "mkv" ? ContainerOverheadFractionMkv : ContainerOverheadFractionMp4;
        long containerBytes = Math.Max(MinimumContainerOverheadBytes,
            ToBytes((videoBytes + (double)audioBytes) * containerRate));
        long estimatedTotal = SaturatingAdd(SaturatingAdd(videoBytes, audioBytes), containerBytes);
        long savingsBytes = facts.SourceSizeBytes - estimatedTotal;
        double savingsPercent = (double)savingsBytes / facts.SourceSizeBytes * 100d;
        double threshold = request.LittleSavingsThresholdPercent;
        if (!double.IsFinite(threshold) || threshold < 0 || threshold > 100)
            throw new V2PlanningException("InvalidSavingsThreshold", "littleSavingsThresholdPercent must be between 0 and 100.");
        V2SavingsClassificationKind classification = estimatedTotal >= facts.SourceSizeBytes
            ? V2SavingsClassificationKind.NoSavings
            : savingsPercent < threshold
                ? V2SavingsClassificationKind.LittleSavings
                : V2SavingsClassificationKind.MeaningfulSavings;
        bool recommendedSkip = classification == V2SavingsClassificationKind.NoSavings ||
            (classification == V2SavingsClassificationKind.LittleSavings && !request.ProcessAll);

        bool frameRateKnown = Positive(facts.FrameRate).HasValue;
        bool sourceRateOutsideModel = rawSourceComplexity < MinimumSourceComplexity || rawSourceComplexity > MaximumSourceComplexity;
        bool audioFallback = sourceAudioFallback || outputAudioFallback;
        V2EstimateConfidence confidence = !sourceCodecKnown || sourceRateOutsideModel
            ? V2EstimateConfidence.Low
            : measuredVideoKbps > 0 && frameRateKnown && !audioFallback &&
              !encoder.EndsWith("_nvenc", StringComparison.Ordinal) && encoder != "libsvtav1"
                ? V2EstimateConfidence.High
                : V2EstimateConfidence.Medium;
        string rateBasis = measuredVideoKbps > 0 ? "measured source video bitrate" : "source file rate minus estimated audio/container allowance";
        string basis = FormattableString.Invariant(
            $"{rateBasis}; source={facts.VideoCodec} {facts.Width}x{facts.Height} at {fps:0.###} fps, {sourceBpp:0.000000} bits/pixel/frame; normalized source complexity=sqrt(source BPP/{sourceCodecBpp:0.###}) clamped to [{MinimumSourceComplexity:0.##},{MaximumSourceComplexity:0.##}] => {sourceComplexity:0.###}; target={targetCodecBpp:0.###} codec BPP x encoder factor {targetEncoderFactor:0.###}, quality rate factor={qualityScale:0.###}, preset factor={presetScale:0.###}, output={width}x{height}; audio={audioMode}; container overhead={containerRate:P1} plus {MinimumContainerOverheadBytes} byte minimum.");

        return new V2FastEstimateResult(
            facts.SourcePath, facts.SourceSizeBytes, facts.DurationSeconds, encoder, codec, container,
            qualityMode, request.QualityValue, preset, Normalize(request.GeometryMode, "geometryMode"), width, height,
            audioMode, request.AudioBitrateKbps, videoBytes, audioBytes, containerBytes, estimatedTotal,
            savingsBytes, savingsPercent,
            new V2SavingsClassification(classification, threshold, request.ProcessAll, recommendedSkip),
            confidence, basis, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
        {
            ProductionPlan = ProductionPlanFingerprintService.CreateV2(
                facts.SourcePath, encoder, codec, qualityMode, request.ProductionPlanQualityValue ?? request.QualityValue, preset,
                "yuv420p", Normalize(request.GeometryMode, "geometryMode"), height,
                audioMode, request.AudioBitrateKbps, subtitleMode, container, outputWidth: width)
        };
    }

    public static async Task<V2FastEstimateResolvedSource> ResolveSourceFactsAsync(
        V2FastEstimateCommandRequest request,
        IV2SourceMetadataProvider? metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceFacts is { } supplied)
        {
            string requestedPath = request.SourcePath;
            if (!string.IsNullOrWhiteSpace(requestedPath) && !Path.IsPathFullyQualified(requestedPath))
                throw new V2PlanningException("InvalidPath", "sourcePath must be a fully qualified path.");
            if (!string.IsNullOrWhiteSpace(supplied.SourcePath) && !Path.IsPathFullyQualified(supplied.SourcePath))
                throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.sourcePath must be a fully qualified path.");
            if (!string.IsNullOrWhiteSpace(requestedPath) && !string.IsNullOrWhiteSpace(supplied.SourcePath) &&
                !SamePath(requestedPath, supplied.SourcePath))
                throw new V2PlanningException("SourceIdentityMismatch", "The supplied source facts identify a different source path.");
            string path = string.IsNullOrWhiteSpace(requestedPath) ? supplied.SourcePath : Path.GetFullPath(requestedPath);
            if (string.IsNullOrWhiteSpace(path))
                throw new V2PlanningException("InvalidSourceFacts", "sourcePath or sourceFacts.sourcePath is required.");
            return new V2FastEstimateResolvedSource(WithSourcePath(supplied, path), false, 0);
        }

        if (metadata is null)
            throw new V2PlanningException("SourceFactsRequired", "Source facts must be supplied or an FFprobe metadata provider must be available.");
        long started = Stopwatch.GetTimestamp();
        V2MediaFacts probed = await metadata.ProbeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
        return new V2FastEstimateResolvedSource(V2FastEstimateSourceFacts.FromMediaFacts(probed), true,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public static async Task<(V2FastEstimateResolvedSource Source, IReadOnlyList<V2FastEstimateResult> Results)> EstimateQualitySetAsync(
        V2FastEstimateCommandRequest request,
        IReadOnlyList<double> qualityValues,
        IV2SourceMetadataProvider? metadata,
        CancellationToken cancellationToken = default)
    {
        if (qualityValues is null || qualityValues.Count == 0)
            throw new V2PlanningException("QualityValuesRequired", "At least one quality value is required.");
        if (qualityValues.Count > 100)
            throw new V2PlanningException("TooManyQualityValues", "A quality set may contain at most 100 values.");
        V2FastEstimateResolvedSource source = await ResolveSourceFactsAsync(request, metadata, cancellationToken).ConfigureAwait(false);
        V2FastEstimateResult[] results = qualityValues.Select(value => Estimate(CreateRequest(request, source.Facts, value))).ToArray();
        return (source, Array.AsReadOnly(results));
    }

    public static V2FastEstimateRequest CreateRequest(V2FastEstimateCommandRequest command,
        V2FastEstimateSourceFacts sourceFacts, double? qualityValue = null) => new()
    {
        SourceFacts = sourceFacts,
        Encoder = command.Encoder,
        Codec = command.Codec,
        Container = command.Container,
        QualityMode = command.QualityMode,
        QualityValue = qualityValue ?? command.QualityValue,
        ProductionPlanQualityValue = command.ProductionPlanQualityValue,
        Preset = command.Preset,
        GeometryMode = command.GeometryMode,
        MaxWidth = command.MaxWidth,
        MaxHeight = command.MaxHeight,
        AudioMode = command.AudioMode,
        AudioBitrateKbps = command.AudioBitrateKbps,
        SubtitleMode = command.SubtitleMode,
        LittleSavingsThresholdPercent = command.LittleSavingsThresholdPercent,
        ProcessAll = command.ProcessAll
    };

    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values is null || values.Count == 0) return 0;
        if (!double.IsFinite(percentile) || percentile < 0 || percentile > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        double[] ordered = values.Order().ToArray();
        int rank = Math.Max(1, (int)Math.Ceiling(percentile / 100d * ordered.Length));
        return ordered[rank - 1];
    }

    private static void ValidateFacts(V2FastEstimateSourceFacts facts)
    {
        if (facts.AudioStreams is null || facts.SubtitleCodecs is null)
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts audio and subtitle streams must be arrays when supplied.");
        if (string.IsNullOrWhiteSpace(facts.SourcePath))
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.sourcePath is required.");
        if (!Path.IsPathFullyQualified(facts.SourcePath))
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.sourcePath must be a fully qualified path.");
        if (facts.SourceSizeBytes <= 0)
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.sourceSizeBytes must be positive.");
        if (!double.IsFinite(facts.DurationSeconds) || facts.DurationSeconds <= 0)
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.durationSeconds must be finite and positive.");
        if (facts.Width <= 0 || facts.Height <= 0)
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts width and height must be positive.");
        if (facts.FrameRate.HasValue && (!double.IsFinite(facts.FrameRate.Value) || facts.FrameRate.Value <= 0))
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.frameRate must be finite and positive when supplied.");
        if (facts.VideoBitrateKbps.HasValue && (!double.IsFinite(facts.VideoBitrateKbps.Value) || facts.VideoBitrateKbps.Value <= 0))
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.videoBitrateKbps must be finite and positive when supplied.");
        if (string.IsNullOrWhiteSpace(facts.VideoCodec))
            throw new V2PlanningException("InvalidSourceFacts", "sourceFacts.videoCodec is required.");
        foreach (V2FastEstimateAudioStreamFacts stream in facts.AudioStreams)
        {
            if ((stream.BitrateKbps.HasValue && (!double.IsFinite(stream.BitrateKbps.Value) || stream.BitrateKbps.Value <= 0)) ||
                (stream.SizeBytes.HasValue && stream.SizeBytes.Value <= 0) ||
                (stream.DurationSeconds.HasValue && (!double.IsFinite(stream.DurationSeconds.Value) || stream.DurationSeconds.Value <= 0)))
                throw new V2PlanningException("InvalidSourceFacts", "Audio stream size, bitrate, and duration facts must be positive when supplied.");
        }
    }

    private static (int Width, int Height) ResolveGeometry(V2FastEstimateRequest request, V2FastEstimateSourceFacts facts)
    {
        string mode = Normalize(request.GeometryMode, "geometryMode");
        if (mode == "source")
        {
            if (request.MaxWidth.HasValue || request.MaxHeight.HasValue)
                throw new V2PlanningException("UnexpectedGeometry", "maxWidth and maxHeight require geometryMode fitBox.");
            return (facts.Width, facts.Height);
        }
        if (mode != "fitbox" || request.MaxWidth is not (> 0) || request.MaxHeight is not (> 0) ||
            request.MaxWidth.Value % 2 != 0 || request.MaxHeight.Value % 2 != 0)
            throw new V2PlanningException("InvalidGeometry", "fitBox requires positive, even maxWidth and maxHeight values.");
        double ratio = Math.Min(1d, Math.Min((double)request.MaxWidth.Value / facts.Width,
            (double)request.MaxHeight.Value / facts.Height));
        return (Math.Max(2, (int)Math.Floor(facts.Width * ratio / 2d) * 2),
            Math.Max(2, (int)Math.Floor(facts.Height * ratio / 2d) * 2));
    }

    private static (long Bytes, bool UsedFallback) EstimateAudio(
        V2FastEstimateSourceFacts facts, string mode, int? bitrateKbps)
    {
        if (mode == "none" || facts.AudioStreams.Count == 0) return (0, false);
        if (mode == "aac")
        {
            long bytes = ToBytes(bitrateKbps!.Value * 1000d / 8d * facts.DurationSeconds * facts.AudioStreams.Count);
            return (bytes, false);
        }

        long totalBytes = 0;
        bool fallback = false;
        foreach (V2FastEstimateAudioStreamFacts stream in facts.AudioStreams)
        {
            double duration = Positive(stream.DurationSeconds) ?? facts.DurationSeconds;
            if (stream.SizeBytes is > 0)
            {
                totalBytes = SaturatingAdd(totalBytes, stream.SizeBytes.Value);
            }
            else if (Positive(stream.BitrateKbps) is double streamKbps)
            {
                long bytes = ToBytes(streamKbps * 1000d / 8d * duration);
                totalBytes = SaturatingAdd(totalBytes, bytes);
            }
            else
            {
                const double fallbackKbps = 128d;
                long bytes = ToBytes(fallbackKbps * 1000d / 8d * duration);
                totalBytes = SaturatingAdd(totalBytes, bytes);
                fallback = true;
            }
        }
        return (totalBytes, fallback);
    }

    private static (double Kbps, bool UsedFallback) EstimateSourceAudioBitrate(V2FastEstimateSourceFacts facts)
    {
        double kbps = 0;
        bool fallback = false;
        foreach (V2FastEstimateAudioStreamFacts stream in facts.AudioStreams)
        {
            double duration = Positive(stream.DurationSeconds) ?? facts.DurationSeconds;
            if (stream.SizeBytes is > 0)
                kbps += stream.SizeBytes.Value * 8d / duration / 1000d;
            else if (Positive(stream.BitrateKbps) is double streamKbps)
                kbps += streamKbps;
            else
            {
                kbps += 128d;
                fallback = true;
            }
        }
        return (kbps, fallback);
    }

    private static double SourceCodecBitsPerPixel(string codec, out bool known)
    {
        string value = codec.Trim().ToLowerInvariant();
        (double Bpp, bool Known) result = value switch
        {
            "h264" or "avc" or "avc1" => (0.085d, true),
            "hevc" or "h265" or "hev1" or "hvc1" => (0.055d, true),
            "av1" or "av01" => (0.045d, true),
            "vp9" or "vp09" => (0.055d, true),
            "theora" or "mpeg4" => (0.10d, true),
            "mpeg2video" => (0.14d, true),
            "prores" => (0.35d, true),
            "ffv1" => (1d, true),
            "rawvideo" => (24d, true),
            _ => (0.10d, false)
        };
        known = result.Known;
        return result.Bpp;
    }

    private static double TargetCodecBitsPerPixel(string codec) => codec switch
    {
        "h264" => 0.085d,
        "hevc" => 0.055d,
        "av1" => 0.045d,
        _ => throw new V2PlanningException("UnsupportedCodec", $"Codec '{codec}' has no V2 fast-estimate model.")
    };

    private static double TargetEncoderFactor(string encoder) => encoder switch
    {
        "libx264" or "libx265" or "libsvtav1" => 1d,
        "h264_nvenc" => 1.18d,
        "hevc_nvenc" => 1.25d,
        "av1_nvenc" => 1.30d,
        _ => throw new V2PlanningException("UnsupportedEncoder", $"Encoder '{encoder}' has no V2 fast-estimate model.")
    };

    private static double QualityAnchor(string encoder, string qualityMode) => encoder switch
    {
        "libx264" or "h264_nvenc" => qualityMode == "qp" ? 23d : 23d,
        "libx265" => 27d,
        "hevc_nvenc" => 25d,
        "libsvtav1" => 30d,
        "av1_nvenc" => 25d,
        _ => 23d
    };

    internal static double GetPresetRateMultiplier(string? encoder, string? preset)
    {
        encoder = encoder?.Trim().ToLowerInvariant() ?? "";
        preset = preset?.Trim().ToLowerInvariant() ?? "";
        if (encoder is "libx264" or "libx265") return preset switch
        {
            "ultrafast" => 1.38d,
            "superfast" => 1.25d,
            "veryfast" => 1.16d,
            "faster" => 1.10d,
            "fast" => 1.05d,
            "medium" => 1.00d,
            "slow" => 0.96d,
            "slower" => 0.92d,
            "veryslow" => 0.88d,
            _ => 1d
        };
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal)) return preset switch
        {
            "p1" => 1.25d,
            "p2" => 1.15d,
            "p3" => 1.07d,
            "p4" => 1.00d,
            "p5" => 0.96d,
            "p6" => 0.93d,
            "p7" => 0.90d,
            _ => 1d
        };
        if (encoder == "libsvtav1" && int.TryParse(preset, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            return 1d + (value - 6) * 0.035d;
        return 1d;
    }

    private static string Normalize(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new V2PlanningException("InvalidRequest", $"{name} is required.");
        return value.Trim().ToLowerInvariant();
    }

    private static double? Positive(double? value) => value is double number && double.IsFinite(number) && number > 0 ? number : null;
    private static string CanonicalPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool SamePath(string left, string right) => string.Equals(CanonicalPath(left), CanonicalPath(right), StringComparison.OrdinalIgnoreCase);
    private static long ToBytes(double value) => !double.IsFinite(value) || value <= 0 ? 0 : value >= long.MaxValue ? long.MaxValue : (long)Math.Round(value, MidpointRounding.AwayFromZero);
    private static long SaturatingAdd(long left, long right) => right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private static V2FastEstimateSourceFacts WithSourcePath(V2FastEstimateSourceFacts facts, string path) => new()
    {
        SourcePath = path,
        SourceSizeBytes = facts.SourceSizeBytes,
        DurationSeconds = facts.DurationSeconds,
        VideoCodec = facts.VideoCodec,
        Width = facts.Width,
        Height = facts.Height,
        FrameRate = facts.FrameRate,
        VideoBitrateKbps = facts.VideoBitrateKbps,
        AudioStreams = facts.AudioStreams
    };
}
