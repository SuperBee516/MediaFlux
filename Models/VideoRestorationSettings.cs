namespace MediaFlux.Models;

public enum VideoRestorationPreset { Off, VintageAnimationLight, VintageAnimationRestore, DvdAnimationRestore, VhsTvCaptureRestore, Custom }
public enum VideoRestorationMode { Off, Auto, Custom }
public enum VideoRestorationStrength { Off, Light, Medium, Strong }
public enum VideoRestorationDeinterlace { Off, AutoSafe, Yadif }
public enum VideoRestorationResize { Original, To720p, To1080p, Custom }
/// <summary>Optional frame-based AI stage. Off preserves the Phase 1-3 FFmpeg-only path.</summary>
public enum AiRestorationMode { Off, Animation, General }
public enum AiRestorationScale { X1 = 1, X2 = 2, X3 = 3, X4 = 4 }

/// <summary>Persisted, encoder-independent description of the optional restoration pass.</summary>
public sealed class VideoRestorationSettings
{
    /// <summary>Authoritative encode/preview controller. Advanced values remain persisted independently.</summary>
    public VideoRestorationMode Mode { get; set; } = VideoRestorationMode.Off;
    public VideoRestorationPreset Preset { get; set; } = VideoRestorationPreset.Off;
    public VideoRestorationSettings? AutoRecommendation { get; set; }
    public VideoRestorationStrength Denoise { get; set; }
    public VideoRestorationStrength Deblock { get; set; }
    public VideoRestorationStrength Deband { get; set; }
    public VideoRestorationStrength Sharpen { get; set; }
    public VideoRestorationDeinterlace Deinterlace { get; set; }
    public decimal Brightness { get; set; }
    public decimal Contrast { get; set; } = 1;
    public decimal Saturation { get; set; } = 1;
    public VideoRestorationResize Resize { get; set; } = VideoRestorationResize.Original;
    public int CustomWidth { get; set; }
    public int CustomHeight { get; set; }
    public bool PreserveAspectRatio { get; set; } = true;

    // These values deliberately live beside the normal restoration settings so cloned
    // presets, saved jobs and scheduled jobs retain the user's explicit AI choice.
    // Older JSON simply uses the safe Off/default values below.
    public AiRestorationMode AiMode { get; set; } = AiRestorationMode.Off;
    public string AiModelId { get; set; } = "";
    public AiRestorationScale AiScale { get; set; } = AiRestorationScale.X2;
    public string AiDevice { get; set; } = "Auto";
    public string AiBackendPath { get; set; } = "";
    public string AiModelsDirectory { get; set; } = "";
    public Services.AiBackendSelection AiBackendSelection { get; set; } = Services.AiBackendSelection.Auto;

    public VideoRestorationSettings Clone()
    {
        var clone = (VideoRestorationSettings)MemberwiseClone();
        clone.AutoRecommendation = AutoRecommendation?.CloneWithoutRecommendation();
        return clone;
    }

    private VideoRestorationSettings CloneWithoutRecommendation()
    {
        var clone = (VideoRestorationSettings)MemberwiseClone();
        clone.AutoRecommendation = null;
        return clone;
    }
}
