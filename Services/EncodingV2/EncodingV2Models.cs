using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using MediaFlux.Services;

namespace MediaFlux.Services.EncodingV2;

public sealed class EncodingV2Request
{
    public int SchemaVersion { get; init; } = 1;
    public string? JobId { get; init; }
    public string SourcePath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string Encoder { get; init; } = "libx265";
    public string Codec { get; init; } = "hevc";
    public string Container { get; init; } = "mp4";
    public string QualityMode { get; init; } = "crf";
    public double QualityValue { get; init; } = 28;
    public string Preset { get; init; } = "";
    public string PixelFormat { get; init; } = "yuv420p";
    public string GeometryMode { get; init; } = "source";
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public string AudioMode { get; init; } = "copy";
    public int? AudioBitrateKbps { get; init; }
    public string SubtitleMode { get; init; } = "none";
    public bool DeleteSourceOnSuccess { get; init; }
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public string? EventLogPath { get; init; }
}

public sealed class V2MediaFacts
{
    public required string SourcePath { get; init; }
    public required MediaFlux.Models.MediaProbeResult Probe { get; init; }
    public required long SourceLength { get; init; }
    public required long SourceLastWriteUtcTicks { get; init; }
    public required string PrimaryVideoCodec { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public IReadOnlyList<string> AudioCodecs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SubtitleCodecs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A plan whose invocation and execution-affecting values are fixed by the planner.
/// It owns a copied, read-only argument collection and checks its fingerprint before launch.
/// </summary>
public sealed class FrozenEncodePlan
{
    private readonly byte[] _invocationFingerprint;
    private readonly ReadOnlyCollection<string> _arguments;
    private readonly ReadOnlyCollection<string> _sourceAudioCodecs;
    private readonly ReadOnlyCollection<string> _sourceSubtitleCodecs;

    internal FrozenEncodePlan(
        string jobId,
        string sourcePath,
        string outputPath,
        string stagingPath,
        string ffmpegPath,
        string encoder,
        string codec,
        string container,
        string qualityMode,
        double qualityValue,
        string preset,
        string pixelFormat,
        string audioMode,
        int? audioBitrateKbps,
        string subtitleMode,
        string geometryMode,
        int width,
        int height,
        bool deleteSourceOnSuccess,
        long sourceLength,
        long sourceLastWriteUtcTicks,
        double? sourceDurationSeconds,
        string sourceFormat,
        string[] sourceAudioCodecs,
        string[] sourceSubtitleCodecs,
        string[] arguments,
        DateTime planFrozenUtc)
    {
        JobId = jobId;
        SourcePath = sourcePath;
        OutputPath = outputPath;
        StagingPath = stagingPath;
        FfmpegPath = ffmpegPath;
        Encoder = encoder;
        Codec = codec;
        Container = container;
        QualityMode = qualityMode;
        QualityValue = qualityValue;
        Preset = preset;
        PixelFormat = pixelFormat;
        AudioMode = audioMode;
        AudioBitrateKbps = audioBitrateKbps;
        SubtitleMode = subtitleMode;
        GeometryMode = geometryMode;
        Width = width;
        Height = height;
        DeleteSourceOnSuccess = deleteSourceOnSuccess;
        SourceLength = sourceLength;
        SourceLastWriteUtcTicks = sourceLastWriteUtcTicks;
        SourceDurationSeconds = sourceDurationSeconds;
        SourceFormat = sourceFormat;
        _sourceAudioCodecs = Array.AsReadOnly(sourceAudioCodecs.ToArray());
        _sourceSubtitleCodecs = Array.AsReadOnly(sourceSubtitleCodecs.ToArray());
        _arguments = Array.AsReadOnly(arguments.ToArray());
        PlanFrozenUtc = planFrozenUtc;
        _invocationFingerprint = ComputeFingerprint();
        ProductionPlan = ProductionPlanFingerprintService.CreateV2(
            sourcePath, encoder, codec, qualityMode, qualityValue, preset, pixelFormat,
            geometryMode, height, audioMode, audioBitrateKbps, subtitleMode, container, outputWidth: width);
    }

    public string JobId { get; }
    public string SourcePath { get; }
    public string OutputPath { get; }
    public string StagingPath { get; }
    public string FfmpegPath { get; }
    public string Encoder { get; }
    public string Codec { get; }
    public string Container { get; }
    public string QualityMode { get; }
    public double QualityValue { get; }
    public string Preset { get; }
    public string PixelFormat { get; }
    public string AudioMode { get; }
    public int? AudioBitrateKbps { get; }
    public string SubtitleMode { get; }
    public string GeometryMode { get; }
    public int Width { get; }
    public int Height { get; }
    public bool DeleteSourceOnSuccess { get; }
    public long SourceLength { get; }
    public long SourceLastWriteUtcTicks { get; }
    public double? SourceDurationSeconds { get; }
    public string SourceFormat { get; }
    public IReadOnlyList<string> SourceAudioCodecs => _sourceAudioCodecs;
    public IReadOnlyList<string> SourceSubtitleCodecs => _sourceSubtitleCodecs;
    public IReadOnlyList<string> Arguments => _arguments;
    public DateTime PlanFrozenUtc { get; }
    public ProductionPlanIdentity ProductionPlan { get; }

    public void EnsureExecutable()
    {
        if (!CryptographicOperations.FixedTimeEquals(_invocationFingerprint, ComputeFingerprint()))
            throw new InvalidOperationException("The frozen V2 plan changed after planning.");
        if (_arguments.Count == 0 || string.IsNullOrWhiteSpace(FfmpegPath) ||
            string.IsNullOrWhiteSpace(StagingPath) ||
            !string.Equals(_arguments[^1], StagingPath, StringComparison.Ordinal))
            throw new InvalidOperationException("The frozen V2 plan does not contain a complete FFmpeg invocation.");
    }

    private byte[] ComputeFingerprint()
    {
        var values = new List<string>
        {
            JobId, SourcePath, OutputPath, StagingPath, FfmpegPath, Encoder, Codec, Container,
            QualityMode, QualityValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            Preset, PixelFormat, AudioMode, AudioBitrateKbps?.ToString() ?? "", SubtitleMode,
            Width.ToString(), Height.ToString(), DeleteSourceOnSuccess.ToString(),
            SourceLength.ToString(), SourceLastWriteUtcTicks.ToString(),
            SourceDurationSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            SourceFormat
        };
        values.AddRange(_sourceAudioCodecs);
        values.Add("--subtitles--");
        values.AddRange(_sourceSubtitleCodecs);
        values.Add("--arguments--");
        values.AddRange(_arguments);
        return SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', values)));
    }
}

public enum V2LifecycleState
{
    Queued,
    Probing,
    Planned,
    Encoding,
    Validating,
    Promoting,
    Completed,
    Failed,
    Canceled,
    Skipped
}

public sealed record V2LifecycleEvent(
    int SchemaVersion,
    string JobId,
    int Sequence,
    DateTime TimestampUtc,
    V2LifecycleState State,
    string Stage,
    string Message,
    double? ElapsedMilliseconds = null,
    int? FfmpegExitCode = null,
    string? FailureCode = null,
    string? SourcePath = null,
    string? OutputPath = null,
    string? StagingPath = null,
    string? Encoder = null,
    string? QualityMode = null,
    double? QualityValue = null,
    V2FfmpegProgress? FfmpegProgress = null);

/// <summary>Parsed FFmpeg statistics; it is advisory progress and never changes the frozen plan or acceptance.</summary>
public sealed record V2FfmpegProgress(
    long? Frame,
    double? FramesPerSecond,
    double? Quantizer,
    string? OutputSize,
    double? OutputTimeSeconds,
    double? BitrateKbps,
    double? SpeedMultiplier,
    double? PercentComplete);

public sealed record V2Failure(string Code, string Message, int? ProcessExitCode = null);

public sealed record V2StageDurations(
    double ProbeMilliseconds = 0,
    double PlanMilliseconds = 0,
    double ExecuteRequestToProcessStartMilliseconds = 0,
    double EncodeMilliseconds = 0,
    double ValidationMilliseconds = 0,
    double PromotionMilliseconds = 0,
    double CleanupMilliseconds = 0,
    double TotalMilliseconds = 0);

public sealed class V2ExecutionResult
{
    public int SchemaVersion { get; init; } = 1;
    public required string JobId { get; init; }
    public required V2LifecycleState State { get; init; }
    public bool Success => State == V2LifecycleState.Completed;
    public string SourcePath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string StagingPath { get; init; } = "";
    public bool OutputValidated { get; init; }
    public bool OutputPromoted { get; init; }
    public long? OutputSizeBytes { get; init; }
    public bool SourceDeletionRequested { get; init; }
    public bool? SourceDeleted { get; init; }
    public V2Failure? Failure { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<V2LifecycleEvent> Events { get; init; } = Array.Empty<V2LifecycleEvent>();
    public V2StageDurations Timings { get; init; } = new();
    public int? FfmpegExitCode { get; init; }
    public int ProductionAttemptCount { get; init; }
}
