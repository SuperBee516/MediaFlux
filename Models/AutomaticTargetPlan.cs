namespace MediaFlux.Models;

public enum AutomaticTargetDisposition { Ready, Review, Unavailable }

/// <summary>
/// Metadata-derived VBR settings and matching output projection for one Automatic
/// production plan. Values are frozen with the plan and consumed by FFmpeg.
/// </summary>
public sealed record AutomaticTargetPlan
{
    public const string CurrentCalculationVersion = "source-bpp-v1";

    public AutomaticTargetDisposition Disposition { get; init; }
    public QualityTarget QualityTarget { get; init; }
    public string CalculationVersion { get; init; } = CurrentCalculationVersion;
    public double? SourceVideoBitrateKbps { get; init; }
    public double? TargetVideoBitrateKbps { get; init; }
    public double? MaximumVideoBitrateKbps { get; init; }
    public double? VideoBufferSizeKbps { get; init; }
    public double? PlannedAudioBitrateKbps { get; init; }
    public double? PlannedMappedAncillaryBitrateKbps { get; init; }
    public double? ContainerOverheadBitrateKbps { get; init; }
    public double? TargetOutputBudgetKbps { get; init; }
    public double? TargetTotalBitrateKbps { get; init; }
    public long? ProjectedOutputBytes { get; init; }
    public string Reason { get; init; } = "";

    public bool IsAvailable =>
        TargetVideoBitrateKbps is > 0 &&
        MaximumVideoBitrateKbps is > 0 &&
        VideoBufferSizeKbps is > 0 &&
        ProjectedOutputBytes is > 0;
}
