using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Conservative, explainable recommendation layer. It never changes user settings.</summary>
public static class VideoRestorationRecommendationService
{
    public static VideoRestorationRecommendation Recommend(VideoRestorationAnalysisResult analysis, bool encodeHintAnimation, VideoRestorationSettings? explicitSettings = null, TemporalQualityResult? temporalQuality = null, AiRecommendationContext? aiContext = null)
    {
        TemporalQualityResult? measured = aiContext?.CurrentTemporalQuality ?? temporalQuality;
        if (aiContext?.Timing is { AiEligibility: not AiTimingEligibility.EligibleCurrentCfrPipeline })
            return new(new VideoRestorationSettings(), 0, "AI restoration not recommended for this source timing: " + aiContext.Timing.Reason, false, measured, AiRecommendationOutcome.AiNotRecommended);
        if (measured?.Classification == TemporalStability.SevereInstability)
            return new(new VideoRestorationSettings(), 0, "Current AI configuration discouraged — severe temporal instability detected in the preview.", true, measured, AiRecommendationOutcome.CurrentAiDiscouraged, true);
        AiConfigurationComparisonItem? tested = aiContext?.ComparisonResults?.FirstOrDefault(item => item.Rank == AiConfigurationRelativeRank.BestTemporalStability && item.TemporalQuality?.Classification is TemporalStability.Stable or TemporalStability.MildInstability);
        if (tested != null)
            return new(tested.Settings.Clone(), 75, "A tested AI configuration showed the best temporal stability for this source window; preview it before applying.", false, tested.TemporalQuality, AiRecommendationOutcome.CurrentAiSuitable, true);
        if (explicitSettings is { Preset: not VideoRestorationPreset.Off })
            return new(explicitSettings.Clone(), 100, "Your explicitly selected restoration settings are retained.", TemporalQuality: measured);
        bool animation = encodeHintAnimation || analysis.AnimationHint == true;
        bool poorLowResolution = analysis.Height is > 0 and <= 576 || analysis.Width is > 0 and <= 960;
        bool artifactEvidence = analysis.Noise >= RestorationEvidenceLevel.Moderate || analysis.Banding >= RestorationEvidenceLevel.Moderate || analysis.Blocking >= RestorationEvidenceLevel.Moderate;
        AiRestorationModel? candidate = aiContext?.AvailableModels.FirstOrDefault(model => model.Category == (animation ? AiRestorationMode.Animation : AiRestorationMode.General));
        if (candidate != null && poorLowResolution && artifactEvidence && analysis.ScanType is not RestorationScanType.InterlacedSuspected and not RestorationScanType.TelecineSuspected)
        {
            AiRestorationScale scale = candidate.SupportedScales.Contains(AiRestorationScale.X2) ? AiRestorationScale.X2 : candidate.SupportedScales[0];
            return new(new VideoRestorationSettings { AiMode = candidate.Category, AiModelId = candidate.Id, AiScale = scale }, 45, "AI restoration worth previewing — low-resolution " + (animation ? "animation" : "source") + " with measured artifacting. This is a heuristic, not a proven improvement.", false, measured, AiRecommendationOutcome.AiWorthPreviewing, false);
        }
        bool dvd = analysis.Codec.Contains("mpeg2", StringComparison.OrdinalIgnoreCase) && analysis.Height is > 0 and <= 576;
        bool uncertainScan = analysis.ScanType is RestorationScanType.InterlacedSuspected or RestorationScanType.TelecineSuspected;
        if (dvd && animation && analysis.Blocking >= RestorationEvidenceLevel.Moderate)
            return WithTemporal(new(new VideoRestorationSettings { Preset = VideoRestorationPreset.DvdAnimationRestore, Deinterlace = uncertainScan ? VideoRestorationDeinterlace.AutoSafe : VideoRestorationDeinterlace.Off }, 75, "SD MPEG-2 animation with compression-artifact evidence" + (uncertainScan ? " and possible telecine/interlacing." : "."), uncertainScan), measured);
        if (analysis.Noise == RestorationEvidenceLevel.High && uncertainScan)
            return WithTemporal(new(new VideoRestorationSettings { Preset = VideoRestorationPreset.VhsTvCaptureRestore, Deinterlace = VideoRestorationDeinterlace.AutoSafe }, 65, "High noise with suspected interlacing; confirm the scan type before applying.", true), measured);
        if (animation && (analysis.Banding >= RestorationEvidenceLevel.Moderate || analysis.Noise >= RestorationEvidenceLevel.Moderate || analysis.Blocking >= RestorationEvidenceLevel.Moderate))
        {
            bool restore = analysis.Blocking >= RestorationEvidenceLevel.High || analysis.Noise >= RestorationEvidenceLevel.High;
            return WithTemporal(new(new VideoRestorationSettings { Preset = restore ? VideoRestorationPreset.VintageAnimationRestore : VideoRestorationPreset.VintageAnimationLight }, restore ? 70 : 60, $"Animation context with noise {analysis.Noise}, blocking {analysis.Blocking}, and banding {analysis.Banding}."), measured) with { AiOutcome = AiRecommendationOutcome.ConventionalRecommended };
        }
        return WithTemporal(new(new VideoRestorationSettings(), 0, candidate == null ? "No conservative restoration recommendation; source evidence is limited or no compatible local AI model is available." : "Insufficient evidence — preview recommended before deciding on restoration."), measured) with { AiOutcome = candidate == null ? AiRecommendationOutcome.AiNotRecommended : AiRecommendationOutcome.InsufficientEvidencePreviewRecommended };
    }
    private static VideoRestorationRecommendation WithTemporal(VideoRestorationRecommendation recommendation, TemporalQualityResult? temporal) => temporal?.Classification == TemporalStability.SevereInstability ? recommendation with { Confidence = Math.Max(0, recommendation.Confidence - 25), Reason = recommendation.Reason + " Severe temporal instability was detected in the preview.", RequiresManualConfirmation = true, TemporalQuality = temporal } : recommendation with { TemporalQuality = temporal };
}
