using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Builds the software FFmpeg restoration stages before the normal encode conversion.</summary>
public static class VideoRestorationPipeline
{
    public static VideoRestorationSettings Effective(VideoRestorationSettings? settings)
    {
        var effective = settings?.Clone() ?? new VideoRestorationSettings();
        if (effective.Preset == VideoRestorationPreset.Off || effective.Preset == VideoRestorationPreset.Custom) return effective;
        return effective.Preset switch
        {
            VideoRestorationPreset.VintageAnimationLight => new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Light, Sharpen = VideoRestorationStrength.Light },
            VideoRestorationPreset.VintageAnimationRestore => new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Medium, Deblock = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Medium, Sharpen = VideoRestorationStrength.Medium },
            VideoRestorationPreset.DvdAnimationRestore => new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Light, Deblock = VideoRestorationStrength.Medium, Deband = VideoRestorationStrength.Medium, Sharpen = VideoRestorationStrength.Medium, Deinterlace = effective.Deinterlace },
            VideoRestorationPreset.VhsTvCaptureRestore => new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Strong, Deblock = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Light, Sharpen = VideoRestorationStrength.Light, Deinterlace = effective.Deinterlace },
            _ => effective
        };
    }

    public static string BuildFilterChain(VideoRestorationSettings? settings, EncodingService.ScaleMode encodeScale)
    {
        var s = Effective(settings);
        if (s.Preset == VideoRestorationPreset.Off) return "";
        Validate(s, encodeScale);
        var filters = new List<string>();
        if (s.Deinterlace != VideoRestorationDeinterlace.Off) filters.Add("yadif=mode=send_frame:parity=auto:deint=interlaced");
        AddStrength(filters, s.Denoise, new[] { "hqdn3d=1:1:2:2", "hqdn3d=2:2:4:4", "hqdn3d=3:3:6:6" });
        AddStrength(filters, s.Deblock, new[] { "deblock=filter=weak:block=4", "deblock=filter=medium:block=4", "deblock=filter=strong:block=4" });
        if (s.Brightness != 0 || s.Contrast != 1 || s.Saturation != 1) filters.Add($"eq=brightness={Number(s.Brightness)}:contrast={Number(s.Contrast)}:saturation={Number(s.Saturation)}");
        AddStrength(filters, s.Deband, new[] { "deband=1thr=0.02:2thr=0.02:range=8", "deband=1thr=0.04:2thr=0.04:range=12", "deband=1thr=0.06:2thr=0.06:range=16" });
        if (encodeScale == EncodingService.ScaleMode.None) AddResize(filters, s);
        AddStrength(filters, s.Sharpen, new[] { "unsharp=5:5:0.3:5:5:0", "unsharp=5:5:0.6:5:5:0", "unsharp=5:5:0.9:5:5:0" });
        return string.Join(',', filters);
    }

    public static void Validate(VideoRestorationSettings settings, EncodingService.ScaleMode encodeScale)
    {
        if (settings.Brightness is < -1 or > 1 || settings.Contrast is < 0.5m or > 2 || settings.Saturation is < 0 or > 2) throw new ArgumentException("Restoration color values are outside safe ranges.");
        if (settings.Resize == VideoRestorationResize.Custom && (settings.CustomWidth is < 64 or > 7680 || settings.CustomHeight is < 64 or > 4320)) throw new ArgumentException("Custom restoration resolution must be between 64x64 and 7680x4320.");
        if (settings.Resize != VideoRestorationResize.Original && encodeScale != EncodingService.ScaleMode.None) throw new ArgumentException("Choose either restoration resize or the normal encode resolution, not both.");
    }

    private static void AddStrength(List<string> filters, VideoRestorationStrength strength, string[] values) { if (strength != VideoRestorationStrength.Off) filters.Add(values[(int)strength - 1]); }
    private static void AddResize(List<string> filters, VideoRestorationSettings s)
    {
        string scale = s.Resize switch { VideoRestorationResize.To720p => "-2:720", VideoRestorationResize.To1080p => "-2:1080", VideoRestorationResize.Custom when s.PreserveAspectRatio => $"{s.CustomWidth}:{s.CustomHeight}:force_original_aspect_ratio=decrease", VideoRestorationResize.Custom => $"{s.CustomWidth}:{s.CustomHeight}", _ => "" };
        if (!string.IsNullOrEmpty(scale)) filters.Add($"scale={scale}:flags=lanczos");
    }
    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
