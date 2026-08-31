namespace MediaFlux.Models;

public enum VideoRestorationPreset { Off, VintageAnimationLight, VintageAnimationRestore, DvdAnimationRestore, VhsTvCaptureRestore, Custom }
public enum VideoRestorationStrength { Off, Light, Medium, Strong }
public enum VideoRestorationDeinterlace { Off, AutoSafe, Yadif }
public enum VideoRestorationResize { Original, To720p, To1080p, Custom }

/// <summary>Persisted, encoder-independent description of the optional restoration pass.</summary>
public sealed class VideoRestorationSettings
{
    public VideoRestorationPreset Preset { get; set; } = VideoRestorationPreset.Off;
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

    public VideoRestorationSettings Clone() => (VideoRestorationSettings)MemberwiseClone();
}
