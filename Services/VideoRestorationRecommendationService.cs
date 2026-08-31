using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Conservative, explainable recommendation layer. It never changes user settings.</summary>
public static class VideoRestorationRecommendationService
{
    public static VideoRestorationRecommendation Recommend(VideoRestorationAnalysisResult analysis, bool encodeHintAnimation, VideoRestorationSettings? explicitSettings = null)
    {
        if (explicitSettings is { Preset: not VideoRestorationPreset.Off })
            return new(explicitSettings.Clone(), 100, "Your explicitly selected restoration settings are retained.");
        bool animation = encodeHintAnimation || analysis.AnimationHint == true;
        bool dvd = analysis.Codec.Contains("mpeg2", StringComparison.OrdinalIgnoreCase) && analysis.Height is > 0 and <= 576;
        bool uncertainScan = analysis.ScanType is RestorationScanType.InterlacedSuspected or RestorationScanType.TelecineSuspected;
        if (dvd && animation && analysis.Blocking >= RestorationEvidenceLevel.Moderate)
            return new(new VideoRestorationSettings { Preset = VideoRestorationPreset.DvdAnimationRestore, Deinterlace = uncertainScan ? VideoRestorationDeinterlace.AutoSafe : VideoRestorationDeinterlace.Off }, 75, "SD MPEG-2 animation with compression-artifact evidence" + (uncertainScan ? " and possible telecine/interlacing." : "."), uncertainScan);
        if (analysis.Noise == RestorationEvidenceLevel.High && uncertainScan)
            return new(new VideoRestorationSettings { Preset = VideoRestorationPreset.VhsTvCaptureRestore, Deinterlace = VideoRestorationDeinterlace.AutoSafe }, 65, "High noise with suspected interlacing; confirm the scan type before applying.", true);
        if (animation && (analysis.Banding >= RestorationEvidenceLevel.Moderate || analysis.Noise >= RestorationEvidenceLevel.Moderate || analysis.Blocking >= RestorationEvidenceLevel.Moderate))
        {
            bool restore = analysis.Blocking >= RestorationEvidenceLevel.High || analysis.Noise >= RestorationEvidenceLevel.High;
            return new(new VideoRestorationSettings { Preset = restore ? VideoRestorationPreset.VintageAnimationRestore : VideoRestorationPreset.VintageAnimationLight }, restore ? 70 : 60, $"Animation context with noise {analysis.Noise}, blocking {analysis.Blocking}, and banding {analysis.Banding}.");
        }
        return new(new VideoRestorationSettings(), 0, "No conservative restoration recommendation; source evidence is limited or uncertain.");
    }
}
