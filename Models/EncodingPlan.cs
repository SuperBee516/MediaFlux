using MediaFlux.Services;

namespace MediaFlux.Models;

// These values are deliberately broad. The code is the stable diagnostic and
// test contract; descriptions are presentation only.
public enum EncodingDecisionReasonCode { UserRequestedVideoReencode, ContainerAutoResolved, ContainerCompatibility, CompatibleAudioPassthrough, AudioConversionRequired, SubtitleConversionRequired, GeometryNormalized, StrictPolicyRejected, IntelligentRecoveryAvailable, HardwareEncoderSelected, TargetSizeBudget }
public enum EncodingRiskSeverity { Information, Warning }
public enum EncodingRiskCategory { SourceDecode, ContainerCompatibility, AudioCompatibility, SubtitleCompatibility, Geometry, Hardware, Sampling, OutputValidation, Storage }

public sealed record EncodingDecisionReason(EncodingDecisionReasonCode Code, string Description);
public sealed record EncodingRisk(EncodingRiskSeverity Severity, EncodingRiskCategory Category, string Code, string Description);

/// <summary>UI-independent, frozen inputs supplied to the planner.</summary>
public sealed record EncodingDecisionContext(
    MediaProbeResult Source, EncodingInputSource Input, VideoEncoderSelection Encoder,
    bool UseGpu, double? TargetMb, EncodingService.ScaleMode ScaleMode,
    VideoRestorationSettings Restoration, string EncoderPreset, int? QualityValue,
    bool TenBit, int? AudioChannels, EncodingService.StreamMapMode MapMode,
    bool CopySubtitles, bool CopyDataStreams, bool CopyAttachments,
    OutputContainerSelection ContainerConfigured, ContainerCompatibilityPolicy CompatibilityPolicy,
    TimeSpan KnownDuration,
    EncodeOutputValidationProfile ValidationProfile = EncodeOutputValidationProfile.Production);

public sealed record EncodingPlanSource(string Codec, int? Width, int? Height, double? FrameRate, double? DurationSeconds);
public sealed record EncodingPlanVideo(string Action, string Codec, string Encoder, int? ConfiguredWidth, int? ConfiguredHeight, int? EffectiveWidth, int? EffectiveHeight, string? PixelFormat);
public sealed record EncodingPlanStream(
    int StreamIndex, string StreamType, string Codec, StreamCompatibilityAction Action,
    string? TargetCodec, string Reason, int? Channels = null);
public sealed record EncodingPlanContainer(OutputContainerSelection Configured, OutputContainer Effective, string Reason);
public sealed record EncodingPlanHardware(bool UseGpu, string EncoderId, bool HardwareEncoder);
public sealed record EncodingPlanRecovery(string InitialDecodeMode, bool TolerantRecoveryPermitted, int MaximumRetryCount, IReadOnlyList<string> PermittedFailureClasses, IReadOnlyList<string> RejectedFailureClasses);
public sealed record EncodingPlanValidation(string Profile, bool OutputValidation, bool SampleComparison);
public sealed record EncodingPlanEstimates(double? TargetTotalBitrateKbps, double? EstimatedOutputSizeMb, double? EstimatedCompressionRatio);

public sealed record EncodingPlanItem(string Label, string Value, string? Reason = null);
public sealed record EncodingPlanSection(string Title, IReadOnlyList<EncodingPlanItem> Items);

/// <summary>Immutable description of an intended encode. It is diagnostic-only.</summary>
public sealed class EncodingPlan
{
    public bool IsAvailable { get; init; }
    public string UnavailableReason { get; init; } = "";
    public Guid PlanId { get; init; } = Guid.NewGuid();
    public EncodingPlanSource? Source { get; init; }
    public EncodingPlanVideo? Video { get; init; }
    public IReadOnlyList<EncodingPlanStream> Audio { get; init; } = Array.Empty<EncodingPlanStream>();
    public IReadOnlyList<EncodingPlanStream> Subtitles { get; init; } = Array.Empty<EncodingPlanStream>();
    public EncodingPlanContainer? Container { get; init; }
    public EncodingPlanHardware? Hardware { get; init; }
    public EncodingPlanRecovery? Recovery { get; init; }
    public EncodingPlanValidation? Validation { get; init; }
    public EncodingPlanEstimates Estimates { get; init; } = new(null, null, null);
    public IReadOnlyList<EncodingRisk> Risks { get; init; } = Array.Empty<EncodingRisk>();
    public IReadOnlyList<EncodingDecisionReason> DecisionReasons { get; init; } = Array.Empty<EncodingDecisionReason>();
    // Execution-only values retain the exact outputs of existing policy services.
    // They are internal so the UI/domain summary remains a projection, not an API
    // for teaching lower-level FFmpeg construction about EncodingPlan.
    internal EncodingPlanService.EncodingPlanExecutionValues? ExecutionValues { get; init; }
    // Retained for the existing read-only UI preview.
    public IReadOnlyList<EncodingPlanSection> Sections { get; init; } = Array.Empty<EncodingPlanSection>();

    public static EncodingPlan Unavailable(string reason) => new() { IsAvailable = false, UnavailableReason = reason };
}

public sealed record EncodingPlanSnapshot(Guid PlanId, EncodingPlan Plan);
public sealed record EncodingPlanDivergence(string Decision, string Planned, string Actual)
{
    public override string ToString() => $"{Decision}: planned={Planned}; actual={Actual}";
}
