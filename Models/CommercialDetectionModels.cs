namespace MediaFlux.Models;

/// <summary>Balanced and tunable options for heuristic commercial-boundary analysis.</summary>
public enum CommercialDetectionPreset { Standard, Conservative, Aggressive, Custom }
public enum CommercialDetectionConfidence { Low, Medium, High }
public enum DetectionSignalKind { Black, Silence, Scene }
public enum CommercialDetectionStage { ProbingSource, DetectingBlack, DetectingSilence, DetectingScenes, CorrelatingCandidates, GeneratingSegments, Completed }

public sealed record CommercialDetectionSettings
{
    public bool BlackDetectionEnabled { get; init; } = true;
    public double MinimumBlackDurationSeconds { get; init; } = .15;
    public double BlackPixelThreshold { get; init; } = .10;
    public bool SilenceDetectionEnabled { get; init; } = true;
    public double MinimumSilenceDurationSeconds { get; init; } = .20;
    public double SilenceThresholdDb { get; init; } = -35;
    public bool SceneDetectionEnabled { get; init; } = true;
    public double SceneThreshold { get; init; } = .45;
    public double CorrelationToleranceSeconds { get; init; } = .50;
    public double MinimumSegmentDurationSeconds { get; init; } = 15;
    public bool PreferCommonCommercialLengths { get; init; } = true;
    public int MinimumBoundaryConfidence { get; init; } = 45;
    public int MinimumSceneOnlyConfidence { get; init; } = 55;

    public static CommercialDetectionSettings Standard { get; } = new();
    public static CommercialDetectionSettings Conservative { get; } = Standard with
    {
        MinimumBlackDurationSeconds = .30, MinimumSilenceDurationSeconds = .40,
        SceneThreshold = .60, CorrelationToleranceSeconds = .35,
        MinimumBoundaryConfidence = 65, MinimumSceneOnlyConfidence = 70
    };
    public static CommercialDetectionSettings Aggressive { get; } = Standard with
    {
        MinimumBlackDurationSeconds = .10, MinimumSilenceDurationSeconds = .12,
        SceneThreshold = .35, CorrelationToleranceSeconds = .65,
        MinimumBoundaryConfidence = 28, MinimumSceneOnlyConfidence = 25
    };

    public static CommercialDetectionSettings FromPreset(CommercialDetectionPreset preset) => preset switch
    {
        CommercialDetectionPreset.Conservative => Conservative,
        CommercialDetectionPreset.Aggressive => Aggressive,
        _ => Standard
    };

    /// <summary>Lets a future settings UI show Custom after an individual value differs from a preset.</summary>
    public CommercialDetectionPreset GetPreset() =>
        this == Standard ? CommercialDetectionPreset.Standard :
        this == Conservative ? CommercialDetectionPreset.Conservative :
        this == Aggressive ? CommercialDetectionPreset.Aggressive : CommercialDetectionPreset.Custom;
}

/// <summary>Per-tool preferences stored as part of MediaFlux's normal Config file.</summary>
public sealed class CommercialDetectorPreferences
{
    public string DetectionPreset { get; set; } = nameof(CommercialDetectionPreset.Standard);
    public CommercialDetectionSettings Settings { get; set; } = CommercialDetectionSettings.Standard;
    public int ExportModeIndex { get; set; }
    public string FilenameTemplate { get; set; } = CommercialSegmentExportDefaults.FilenameTemplate;
}

public static class CommercialSegmentExportDefaults
{
    public const string FilenameTemplate = "{source}_Commercial_{index:00}";
}

public sealed record DetectionSignal(
    DetectionSignalKind Kind,
    double TimestampSeconds,
    double? StartSeconds = null,
    double? EndSeconds = null,
    double? DurationSeconds = null,
    double? Strength = null);

public sealed record DetectionEvidence(
    DetectionSignalKind Kind,
    double TimestampSeconds,
    string Description,
    double? StartSeconds = null,
    double? EndSeconds = null,
    double? DurationSeconds = null);

public sealed record CommercialBoundary(
    double TimestampSeconds,
    int Confidence,
    CommercialDetectionConfidence ConfidenceCategory,
    IReadOnlyList<DetectionEvidence> Evidence);

public sealed record CommercialSegment(int Number, double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

public sealed record CommercialDetectionProgress(CommercialDetectionStage Stage, string Status, double? Percent = null);

public sealed class CommercialDetectionResult
{
    public string SourcePath { get; init; } = "";
    public double SourceDurationSeconds { get; init; }
    public IReadOnlyList<DetectionSignal> BlackSignals { get; init; } = Array.Empty<DetectionSignal>();
    public IReadOnlyList<DetectionSignal> SilenceSignals { get; init; } = Array.Empty<DetectionSignal>();
    public IReadOnlyList<DetectionSignal> SceneSignals { get; init; } = Array.Empty<DetectionSignal>();
    public IReadOnlyList<CommercialBoundary> Boundaries { get; init; } = Array.Empty<CommercialBoundary>();
    public IReadOnlyList<CommercialSegment> Segments { get; init; } = Array.Empty<CommercialSegment>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public int RejectedCandidateCount { get; init; }
    public bool Success { get; init; }
}
