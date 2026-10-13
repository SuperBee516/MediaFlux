using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>The output-affecting portion of one resolved production plan.</summary>
public sealed record ProductionPlanFingerprintInput(
    string EncoderId,
    string Codec,
    string RateControlMode,
    string QualityIntent,
    double? QualityValue,
    string Preset,
    int BitDepth,
    string GeometryMode,
    int? OutputHeight,
    string FrameRateBehavior,
    string CodecOptions,
    string AudioMode,
    int? AudioChannels,
    int? AudioBitrateKbps,
    string SubtitleMode,
    bool CopyDataStreams,
    bool CopyAttachments,
    string Container,
    string ContainerCompatibility,
    string HardwareImplementation,
    bool ConcurrentEncoderSessions,
    string StreamMapMode,
    double? TargetMegabytes)
{
    public int? OutputWidth { get; init; }
    public string StorageSavingsPolicy { get; init; } = "";
    public double? TargetVideoBitrateKbps { get; init; }
    public double? MaximumVideoBitrateKbps { get; init; }
    public double? VideoBufferSizeKbps { get; init; }
    public double? PlannedAudioBitrateKbps { get; init; }
    public double? PlannedMappedAncillaryBitrateKbps { get; init; }
    public double? TargetOutputBudgetKbps { get; init; }
    public string SelectedVideoStreams { get; init; } = "";
    public string SelectedAudioStreams { get; init; } = "";
    public string SelectedSubtitleStreams { get; init; } = "";
    public string AutomaticTargetCalculationVersion { get; init; } = "";
    public long? ProjectedOutputBytes { get; init; }
}

/// <summary>Stable source and production-plan identities carried with an estimate.</summary>
public sealed record ProductionPlanIdentity(
    string SourceIdentity,
    string PlanFingerprint,
    ProductionPlanFingerprintInput Inputs);

public enum EstimateAuthorityLevel { Preliminary, Measured }

public sealed record PlanBoundEstimate(
    ProductionPlanIdentity Plan,
    long Generation,
    string Method,
    long ProjectedBytes,
    EstimateAuthorityLevel Authority,
    int? SampleCount = null,
    long? LowerBytes = null,
    long? UpperBytes = null,
    string Confidence = "")
{
    public string? RangeMethod { get; init; }
    public IReadOnlyList<EmpiricalPreviewSampleEvidence>? SampleEvidence { get; init; }

    public bool Matches(ProductionPlanIdentity? current) => current is not null &&
        string.Equals(Plan.SourceIdentity, current.SourceIdentity, StringComparison.Ordinal) &&
        string.Equals(Plan.PlanFingerprint, current.PlanFingerprint, StringComparison.Ordinal);
}

/// <summary>Production-purpose adaptive selection frozen for reuse by the full encode.</summary>
public sealed record PreparedAdaptiveProductionPlan(
    ProductionPlanIdentity Identity,
    AdaptiveQualitySelectionEvidence Selection,
    PlanBoundEstimate? BoundEstimate);

/// <summary>
/// Creates the shared, deterministic identity used by queue estimates, V1 plans,
/// saved jobs, and the V2 frozen request path. Output paths and random job IDs are
/// intentionally excluded because they do not change the encoded bytes.
/// </summary>
public static class ProductionPlanFingerprintService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ProductionPlanIdentity Create(
        ProductionPlanFingerprintInput inputs,
        IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        string sourceIdentity = CaptureSourceIdentity(sourcePaths);
        ProductionPlanFingerprintInput canonical = Canonicalize(inputs);
        string json = JsonSerializer.Serialize(canonical, JsonOptions);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new(sourceIdentity, fingerprint, canonical);
    }

    public static ProductionPlanIdentity Create(EncodeExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Create(FromSnapshot(snapshot), SourcePaths(snapshot.Input, snapshot.SourceFilePath));
    }

    public static ProductionPlanFingerprintInput FromSnapshot(EncodeExecutionSnapshot snapshot)
    {
        AutomaticTargetPlan? target = snapshot.PreparedPlan?.AutomaticTarget;
        return new ProductionPlanFingerprintInput(
        snapshot.Encoder.EncoderId,
        snapshot.Codec,
        RateControlMode(snapshot.TargetMb, snapshot.QualityIntent, snapshot.Encoder),
        QualityIntentText(snapshot.QualityIntent),
        target is null ? snapshot.QualityValue : null,
        snapshot.EncoderPreset ?? "",
        snapshot.TenBit ? 10 : 8,
        snapshot.ScaleMode.ToString(),
        snapshot.OutputHeight,
        "preserve-source-timing",
        JsonSerializer.Serialize(snapshot.Restoration, JsonOptions),
        AudioMode(snapshot.AudioChannels),
        snapshot.AudioChannels,
        null,
        snapshot.CopySubtitles ? "copy-compatible" : "omit",
        snapshot.CopyDataStreams,
        snapshot.CopyAttachments,
        snapshot.OutputContainer.ToString(),
        snapshot.CompatibilityPolicy.ToString(),
        snapshot.UseGpu ? "hardware" : "software",
        snapshot.ConcurrentEncoderSessions,
        snapshot.MapMode.ToString(),
        snapshot.TargetMb)
        {
            OutputWidth = snapshot.OutputWidth,
            StorageSavingsPolicy = JsonSerializer.Serialize(snapshot.StorageSavingsPolicy.CloneNormalized(), JsonOptions),
            TargetVideoBitrateKbps = target?.TargetVideoBitrateKbps,
            MaximumVideoBitrateKbps = target?.MaximumVideoBitrateKbps,
            VideoBufferSizeKbps = target?.VideoBufferSizeKbps,
            PlannedAudioBitrateKbps = target?.PlannedAudioBitrateKbps,
            PlannedMappedAncillaryBitrateKbps = target?.PlannedMappedAncillaryBitrateKbps,
            TargetOutputBudgetKbps = target?.TargetOutputBudgetKbps,
            SelectedVideoStreams = string.Join(",", snapshot.Input.VideoStreamIndexes),
            SelectedAudioStreams = string.Join(",", snapshot.Input.AudioStreamIndexes),
            SelectedSubtitleStreams = string.Join(",", snapshot.Input.SubtitleStreamIndexes),
            AutomaticTargetCalculationVersion = target?.CalculationVersion ?? "",
            ProjectedOutputBytes = target?.ProjectedOutputBytes
        };
    }

    internal static ProductionPlanIdentity Create(
        EncodingDecisionContext context,
        EncodingPlanService.EncodingPlanExecutionValues execution)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(execution);
        EncodingQualityResolution quality = execution.QualityResolution;
        ProductionPlanFingerprintInput inputs = new(
            execution.Encoder.EncoderId,
            execution.Encoder.FfmpegCodec,
            RateControlMode(execution.TargetMb, quality.Intent, execution.Encoder),
            QualityIntentText(quality.Intent),
            quality.EffectiveQuality,
            execution.EncoderPreset,
            context.TenBit ? 10 : 8,
            context.ScaleMode.ToString(),
            execution.Geometry?.Height,
            "preserve-source-timing",
            JsonSerializer.Serialize(context.Restoration, JsonOptions),
            AudioMode(execution.AudioChannels),
            execution.AudioChannels,
            null,
            execution.CopySubtitles ? "copy-compatible" : "omit",
            execution.CopyDataStreams,
            execution.CopyAttachments,
            context.ContainerConfigured.ToString(),
            context.CompatibilityPolicy.ToString(),
            execution.UseGpu ? "hardware" : "software",
            context.ConcurrentEncoderSessions,
            execution.MapMode.ToString(),
            execution.TargetMb)
        {
            OutputWidth = execution.Geometry?.Width,
            StorageSavingsPolicy = JsonSerializer.Serialize(context.StorageSavingsPolicy.CloneNormalized(), JsonOptions),
            TargetVideoBitrateKbps = execution.AutomaticTargetPlan?.TargetVideoBitrateKbps,
            MaximumVideoBitrateKbps = execution.AutomaticTargetPlan?.MaximumVideoBitrateKbps,
            VideoBufferSizeKbps = execution.AutomaticTargetPlan?.VideoBufferSizeKbps,
            PlannedAudioBitrateKbps = execution.AutomaticTargetPlan?.PlannedAudioBitrateKbps,
            PlannedMappedAncillaryBitrateKbps = execution.AutomaticTargetPlan?.PlannedMappedAncillaryBitrateKbps,
            TargetOutputBudgetKbps = execution.AutomaticTargetPlan?.TargetOutputBudgetKbps,
            SelectedVideoStreams = string.Join(",", context.Input.VideoStreamIndexes),
            SelectedAudioStreams = string.Join(",", context.Input.AudioStreamIndexes),
            SelectedSubtitleStreams = string.Join(",", context.Input.SubtitleStreamIndexes),
            AutomaticTargetCalculationVersion = execution.AutomaticTargetPlan?.CalculationVersion ?? "",
            ProjectedOutputBytes = execution.AutomaticTargetPlan?.ProjectedOutputBytes
        };
        return Create(inputs, SourcePaths(context.Input, context.Input.SourcePath));
    }

    public static ProductionPlanIdentity WithQuality(ProductionPlanIdentity identity, double? quality)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ProductionPlanFingerprintInput inputs = Canonicalize(identity.Inputs with { QualityValue = quality });
        string json = JsonSerializer.Serialize(inputs, JsonOptions);
        return identity with
        {
            Inputs = inputs,
            PlanFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
        };
    }

    public static bool AllowsQualityRebinding(ProductionPlanIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.Inputs.QualityIntent.StartsWith("qualitytarget:", StringComparison.Ordinal);
    }

    public static ProductionPlanIdentity CreateV2(
        string sourcePath,
        string encoder,
        string codec,
        string qualityMode,
        double quality,
        string preset,
        string pixelFormat,
        string geometryMode,
        int? outputHeight,
        string audioMode,
        int? audioBitrateKbps,
        string subtitleMode,
        string container,
        int? outputWidth = null)
    {
        int bitDepth = pixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) ? 10 : 8;
        var inputs = new ProductionPlanFingerprintInput(
            encoder,
            codec,
            qualityMode,
            "fixed",
            quality,
            preset,
            bitDepth,
            geometryMode,
            outputHeight,
            "preserve-source-timing",
            pixelFormat,
            audioMode,
            null,
            audioBitrateKbps,
            subtitleMode,
            false,
            false,
            container,
            "intelligent",
            encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ? "hardware" : "software",
            false,
            "keep-all",
            null);
        return Create(inputs with { OutputWidth = outputWidth }, [sourcePath]);
    }

    public static string CaptureSourceIdentity(IEnumerable<string> sourcePaths)
    {
        var identities = new List<string>();
        foreach (string path in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(path).ToUpperInvariant(); }
            catch { fullPath = path.Trim().ToUpperInvariant(); }

            try
            {
                var file = new FileInfo(path);
                identities.Add($"{fullPath}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                identities.Add($"{fullPath}|unavailable");
            }
        }

        string joined = string.Join("\n", identities);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static ProductionPlanFingerprintInput Canonicalize(ProductionPlanFingerprintInput input) => input with
    {
        EncoderId = Normalize(input.EncoderId),
        Codec = Normalize(input.Codec),
        RateControlMode = Normalize(input.RateControlMode),
        QualityIntent = Normalize(input.QualityIntent),
        Preset = Normalize(input.Preset),
        GeometryMode = Normalize(input.GeometryMode),
        FrameRateBehavior = Normalize(input.FrameRateBehavior),
        CodecOptions = input.CodecOptions ?? "",
        AudioMode = Normalize(input.AudioMode),
        SubtitleMode = Normalize(input.SubtitleMode),
        Container = Normalize(input.Container),
        ContainerCompatibility = Normalize(input.ContainerCompatibility),
        HardwareImplementation = Normalize(input.HardwareImplementation),
        StreamMapMode = Normalize(input.StreamMapMode),
        StorageSavingsPolicy = input.StorageSavingsPolicy ?? "",
        TargetMegabytes = input.TargetMegabytes is double target && double.IsFinite(target)
            ? Math.Round(target, 6, MidpointRounding.AwayFromZero)
            : null,
        TargetVideoBitrateKbps = FiniteRounded(input.TargetVideoBitrateKbps),
        MaximumVideoBitrateKbps = FiniteRounded(input.MaximumVideoBitrateKbps),
        VideoBufferSizeKbps = FiniteRounded(input.VideoBufferSizeKbps),
        PlannedAudioBitrateKbps = FiniteRounded(input.PlannedAudioBitrateKbps),
        PlannedMappedAncillaryBitrateKbps = FiniteRounded(input.PlannedMappedAncillaryBitrateKbps),
        TargetOutputBudgetKbps = FiniteRounded(input.TargetOutputBudgetKbps),
        SelectedVideoStreams = input.SelectedVideoStreams ?? "",
        SelectedAudioStreams = input.SelectedAudioStreams ?? "",
        SelectedSubtitleStreams = input.SelectedSubtitleStreams ?? ""
    };

    private static double? FiniteRounded(double? value) =>
        value is double finite && double.IsFinite(finite)
            ? Math.Round(finite, 3, MidpointRounding.AwayFromZero)
            : null;

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static IEnumerable<string> SourcePaths(EncodingInputSource input, string fallback)
    {
        if (input.Kind == EncodingInputKind.File && !string.IsNullOrWhiteSpace(input.SourcePath))
            return [input.SourcePath];
        if (input.SourceFiles.Count > 0)
            return input.SourceFiles;
        return [fallback];
    }

    private static string RateControlMode(double? targetMb, EncodingQualityIntent? intent, VideoEncoderSelection encoder)
    {
        if (targetMb is > 0)
            return "target-size";
        if (intent?.Kind == EncodingQualityIntentKind.AutomaticTargetBitrate)
            return "target-bitrate-vbr";

        EncoderQualityRange? range = EncoderRegistry.Default.Resolve(
            encoder.EncoderId, encoder.CodecFamily).Provider.Capabilities.QualityRange;
        if (string.Equals(range?.Name, "CQ", StringComparison.OrdinalIgnoreCase))
            return "cq";
        if (string.Equals(range?.Name, "CRF", StringComparison.OrdinalIgnoreCase))
            return "crf";
        if (range?.Name.Contains("global", StringComparison.OrdinalIgnoreCase) == true)
            return "icq";
        return intent?.Kind == EncodingQualityIntentKind.QualityTarget ? "constant-quality" : "fixed-quality";
    }

    private static string QualityIntentText(EncodingQualityIntent? intent) =>
        intent is null or { Kind: EncodingQualityIntentKind.LegacyNumeric }
            ? "manual-numeric"
            : $"{intent.Kind}:{intent.Target}";

    private static string AudioMode(int? channels) => channels is > 0 ? "transcode-layout" : "copy-compatible";
}
