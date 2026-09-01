using MediaFlux.Services;

namespace MediaFlux.Models;

public enum RestorationEvidenceLevel { Unknown, Low, Moderate, High }
public enum RestorationScanType { Unknown, Progressive, InterlacedSuspected, TelecineSuspected }

public sealed record VideoRestorationAnalysisResult
{
    public required string SourcePath { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string Codec { get; init; } = "Unknown";
    public RestorationScanType ScanType { get; init; }
    public RestorationEvidenceLevel Noise { get; init; }
    public RestorationEvidenceLevel Blocking { get; init; }
    public RestorationEvidenceLevel Banding { get; init; }
    public bool? AnimationHint { get; init; }
    public int Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public SourceTimingAnalysis? Timing { get; init; }
}

public sealed record VideoRestorationRecommendation(
    VideoRestorationSettings Settings,
    int Confidence,
    string Reason,
    bool RequiresManualConfirmation = false,
    TemporalQualityResult? TemporalQuality = null,
    AiRecommendationOutcome AiOutcome = AiRecommendationOutcome.NotConsidered,
    bool IsPreviewTested = false);

public enum AiRecommendationOutcome { NotConsidered, ConventionalRecommended, AiWorthPreviewing, AiNotRecommended, CurrentAiSuitable, CurrentAiDiscouraged, InsufficientEvidencePreviewRecommended }
public sealed record AiRecommendationContext(IReadOnlyList<AiRestorationModel> AvailableModels, IReadOnlyList<AiConfigurationComparisonItem>? ComparisonResults = null, TemporalQualityResult? CurrentTemporalQuality = null, int? TargetHeight = null, SourceTimingAnalysis? Timing = null);
