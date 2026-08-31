namespace MediaFlux.Models;

/// <summary>Selection state used by the restoration preview. It is deliberately separate from persisted encode settings.</summary>
public sealed class VideoRestorationPreviewSelection
{
    public VideoRestorationPreviewSelection(VideoRestorationSettings encodeSettings, VideoRestorationRecommendation? recommendation = null)
    {
        EncodeSettings = encodeSettings.Clone();
        PreviewSettings = encodeSettings.Clone();
        Recommendation = recommendation;
    }

    public VideoRestorationSettings EncodeSettings { get; private set; }
    public VideoRestorationSettings PreviewSettings { get; private set; }
    public VideoRestorationRecommendation? Recommendation { get; private set; }
    public bool DiffersFromEncode => !Equivalent(EncodeSettings, PreviewSettings);

    public void PreviewOff() => PreviewSettings = new VideoRestorationSettings();
    public void PreviewCurrent() => PreviewSettings = EncodeSettings.Clone();
    public bool PreviewRecommendation()
    {
        if (Recommendation == null) return false;
        PreviewSettings = Recommendation.Settings.Clone();
        return true;
    }

    public void SetRecommendation(VideoRestorationRecommendation? recommendation) => Recommendation = recommendation;

    /// <summary>Returns a clone for the caller to persist only after an explicit user action.</summary>
    public VideoRestorationSettings ApplyToEncodeSettings()
    {
        EncodeSettings = PreviewSettings.Clone();
        return EncodeSettings.Clone();
    }

    public static bool Equivalent(VideoRestorationSettings left, VideoRestorationSettings right) =>
        left.Preset == right.Preset && left.Denoise == right.Denoise && left.Deblock == right.Deblock &&
        left.Deband == right.Deband && left.Sharpen == right.Sharpen && left.Deinterlace == right.Deinterlace &&
        left.Brightness == right.Brightness && left.Contrast == right.Contrast && left.Saturation == right.Saturation &&
        left.Resize == right.Resize && left.CustomWidth == right.CustomWidth && left.CustomHeight == right.CustomHeight &&
        left.PreserveAspectRatio == right.PreserveAspectRatio;
}
