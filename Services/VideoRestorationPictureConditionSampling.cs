using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Conservative classification of bounded frame-window metrics. Values without enough stable samples stay Unknown.</summary>
public sealed record VideoRestorationFrameMetrics(double TemporalDifference, double LumaVariance, double EdgeVariation, double BlockBoundaryRatio);
public static class VideoRestorationPictureConditionSampling
{
    public static (RestorationEvidenceLevel Noise, RestorationEvidenceLevel Banding, RestorationEvidenceLevel Blocking) Classify(IReadOnlyList<VideoRestorationFrameMetrics> samples)
    {
        if (samples.Count < 3) return (RestorationEvidenceLevel.Unknown, RestorationEvidenceLevel.Unknown, RestorationEvidenceLevel.Unknown);
        RestorationEvidenceLevel noise = samples.All(x => double.IsFinite(x.TemporalDifference)) ? Level(samples.Average(x => x.TemporalDifference), .025, .06) : RestorationEvidenceLevel.Unknown;
        RestorationEvidenceLevel banding = samples.All(x => double.IsFinite(x.EdgeVariation)) ? LevelInverse(samples.Average(x => x.EdgeVariation), .08, .035) : RestorationEvidenceLevel.Unknown;
        RestorationEvidenceLevel blocks = samples.All(x => double.IsFinite(x.BlockBoundaryRatio)) ? Level(samples.Average(x => x.BlockBoundaryRatio), .10, .22) : RestorationEvidenceLevel.Unknown;
        return (noise, banding, blocks);
    }
    private static RestorationEvidenceLevel Level(double value, double moderate, double high) => value >= high ? RestorationEvidenceLevel.High : value >= moderate ? RestorationEvidenceLevel.Moderate : RestorationEvidenceLevel.Low;
    private static RestorationEvidenceLevel LevelInverse(double value, double moderate, double high) => value <= high ? RestorationEvidenceLevel.High : value <= moderate ? RestorationEvidenceLevel.Moderate : RestorationEvidenceLevel.Low;
}
