namespace MediaFlux.Models;

public enum TemporalStability { Unknown, Stable, MildInstability, ModerateInstability, SevereInstability }

/// <summary>Conservative, explainable comparison of original and restored motion samples.</summary>
public sealed record TemporalQualityResult(
    TemporalStability Classification,
    int Confidence,
    double OriginalMotion,
    double RestoredMotion,
    double OriginalEdgeVariation,
    double RestoredEdgeVariation,
    double OriginalBrightnessVariation,
    double RestoredBrightnessVariation,
    string Reason)
{
    public bool IsAdvisory => Classification is TemporalStability.ModerateInstability or TemporalStability.SevereInstability;
    public string Summary => Classification switch
    {
        TemporalStability.Stable => "Temporal Stability: Stable",
        TemporalStability.MildInstability => "Temporal Stability: Mild instability detected",
        TemporalStability.ModerateInstability => "Temporal Stability: Moderate shimmer detected",
        TemporalStability.SevereInstability => "Temporal Stability: Severe temporal instability detected",
        _ => "Temporal Stability: Unknown"
    };
}
