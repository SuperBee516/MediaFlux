using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Builds the software FFmpeg restoration stages before the normal encode conversion.</summary>
public static class VideoRestorationPipeline
{
    private static ISet<string>? _availableFilters;
    public static void SetAvailableFilters(IEnumerable<string> filters) => _availableFilters = new HashSet<string>(filters, StringComparer.OrdinalIgnoreCase);
    public static void ClearAvailableFilters() => _availableFilters = null;
    public static void ValidateAvailable(VideoRestorationSettings? settings)
    {
        if (_availableFilters == null || settings is null || settings.Preset == VideoRestorationPreset.Off) return;
        string[] required = RequiredFilters(settings).Where(filter => !_availableFilters.Contains(filter)).ToArray();
        if (required.Length > 0) throw new NotSupportedException($"The configured FFmpeg build does not provide required restoration filter(s): {string.Join(", ", required)}.");
    }
    public static IReadOnlyList<string> RequiredFilters(VideoRestorationSettings settings)
    {
        var s = Effective(settings); var filters = new List<string>();
        if (s.Deinterlace != VideoRestorationDeinterlace.Off) filters.Add("yadif"); if (s.Denoise != VideoRestorationStrength.Off) filters.Add("hqdn3d"); if (s.Deblock != VideoRestorationStrength.Off) filters.Add("deblock"); if (s.Deband != VideoRestorationStrength.Off) filters.Add("deband"); if (s.Sharpen != VideoRestorationStrength.Off) filters.Add("unsharp"); if (s.Brightness != 0 || s.Contrast != 1 || s.Saturation != 1) filters.Add("eq"); return filters;
    }
    public static VideoRestorationSettings Effective(VideoRestorationSettings? settings)
    {
        var effective = settings?.Clone() ?? new VideoRestorationSettings();
        if (effective.Preset == VideoRestorationPreset.Off || effective.Preset == VideoRestorationPreset.Custom) return effective;
        return effective.Preset switch
        {
            VideoRestorationPreset.VintageAnimationLight => WithAi(effective, new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Light, Sharpen = VideoRestorationStrength.Light }),
            VideoRestorationPreset.VintageAnimationRestore => WithAi(effective, new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Medium, Deblock = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Medium, Sharpen = VideoRestorationStrength.Medium }),
            VideoRestorationPreset.DvdAnimationRestore => WithAi(effective, new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Light, Deblock = VideoRestorationStrength.Medium, Deband = VideoRestorationStrength.Medium, Sharpen = VideoRestorationStrength.Medium, Deinterlace = effective.Deinterlace }),
            VideoRestorationPreset.VhsTvCaptureRestore => WithAi(effective, new() { Preset = effective.Preset, Denoise = VideoRestorationStrength.Strong, Deblock = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Light, Sharpen = VideoRestorationStrength.Light, Deinterlace = effective.Deinterlace }),
            _ => effective
        };
    }

    public static string BuildFilterChain(VideoRestorationSettings? settings, EncodingService.ScaleMode encodeScale)
    {
        var s = Effective(settings);
        if (s.Preset == VideoRestorationPreset.Off) return "";
        Validate(s, encodeScale);
        var filters = new List<string>();
        if (s.Deinterlace != VideoRestorationDeinterlace.Off) Add(filters, "yadif", "yadif=mode=send_frame:parity=auto:deint=interlaced");
        AddStrength(filters, s.Denoise, "hqdn3d", new[] { "hqdn3d=1:1:2:2", "hqdn3d=2:2:4:4", "hqdn3d=3:3:6:6" });
        AddStrength(filters, s.Deblock, "deblock", new[] { "deblock=filter=weak:block=4", "deblock=filter=medium:block=4", "deblock=filter=strong:block=4" });
        if (s.Brightness != 0 || s.Contrast != 1 || s.Saturation != 1) Add(filters, "eq", $"eq=brightness={Number(s.Brightness)}:contrast={Number(s.Contrast)}:saturation={Number(s.Saturation)}");
        AddStrength(filters, s.Deband, "deband", new[] { "deband=1thr=0.02:2thr=0.02:range=8", "deband=1thr=0.04:2thr=0.04:range=12", "deband=1thr=0.06:2thr=0.06:range=16" });
        if (encodeScale == EncodingService.ScaleMode.None) AddResize(filters, s);
        AddStrength(filters, s.Sharpen, "unsharp", new[] { "unsharp=5:5:0.3:5:5:0", "unsharp=5:5:0.6:5:5:0", "unsharp=5:5:0.9:5:5:0" });
        return string.Join(',', filters);
    }

    /// <summary>
    /// Produces ordered FFmpeg stages around an optional frame-based AI operation.  Keeping
    /// this plan here prevents preview and encode code from independently reordering filters.
    /// Destructive sharpening is always held for the finishing stage.
    /// </summary>
    public static VideoRestorationPipelinePlan BuildPlan(VideoRestorationSettings? settings, EncodingService.ScaleMode encodeScale)
    {
        var s = Effective(settings);
        Validate(s, encodeScale);
        bool aiEnabled = s.AiMode != AiRestorationMode.Off;
        if (!aiEnabled)
            return new VideoRestorationPipelinePlan(BuildFilterChain(s, encodeScale), "", "", false);

        var pre = new List<string>();
        if (s.Deinterlace != VideoRestorationDeinterlace.Off) Add(pre, "yadif", "yadif=mode=send_frame:parity=auto:deint=interlaced");
        AddStrength(pre, s.Denoise, "hqdn3d", new[] { "hqdn3d=1:1:2:2", "hqdn3d=2:2:4:4", "hqdn3d=3:3:6:6" });
        AddStrength(pre, s.Deblock, "deblock", new[] { "deblock=filter=weak:block=4", "deblock=filter=medium:block=4", "deblock=filter=strong:block=4" });
        if (s.Brightness != 0 || s.Contrast != 1 || s.Saturation != 1) Add(pre, "eq", $"eq=brightness={Number(s.Brightness)}:contrast={Number(s.Contrast)}:saturation={Number(s.Saturation)}");
        var post = new List<string>();
        AddStrength(post, s.Deband, "deband", new[] { "deband=1thr=0.02:2thr=0.02:range=8", "deband=1thr=0.04:2thr=0.04:range=12", "deband=1thr=0.06:2thr=0.06:range=16" });
        if (encodeScale == EncodingService.ScaleMode.None) AddResize(post, s);
        AddStrength(post, s.Sharpen, "unsharp", new[] { "unsharp=5:5:0.3:5:5:0", "unsharp=5:5:0.6:5:5:0", "unsharp=5:5:0.9:5:5:0" });
        return new VideoRestorationPipelinePlan("", string.Join(',', pre), string.Join(',', post), true);
    }

    public static void Validate(VideoRestorationSettings settings, EncodingService.ScaleMode encodeScale)
    {
        if (settings.Brightness is < -1 or > 1 || settings.Contrast is < 0.5m or > 2 || settings.Saturation is < 0 or > 2) throw new ArgumentException("Restoration color values are outside safe ranges.");
        if (settings.Resize == VideoRestorationResize.Custom && (settings.CustomWidth is < 64 or > 7680 || settings.CustomHeight is < 64 or > 4320)) throw new ArgumentException("Custom restoration resolution must be between 64x64 and 7680x4320.");
        if (settings.Resize != VideoRestorationResize.Original && encodeScale != EncodingService.ScaleMode.None) throw new ArgumentException("Choose either restoration resize or the normal encode resolution, not both.");
    }

    private static void AddStrength(List<string> filters, VideoRestorationStrength strength, string filter, string[] values) { if (strength != VideoRestorationStrength.Off) Add(filters, filter, values[(int)strength - 1]); }
    private static void Add(List<string> filters, string filter, string expression) { if (_availableFilters == null || _availableFilters.Contains(filter)) filters.Add(expression); }
    private static void AddResize(List<string> filters, VideoRestorationSettings s)
    {
        string scale = s.Resize switch { VideoRestorationResize.To720p => "-2:720", VideoRestorationResize.To1080p => "-2:1080", VideoRestorationResize.Custom when s.PreserveAspectRatio => $"{s.CustomWidth}:{s.CustomHeight}:force_original_aspect_ratio=decrease", VideoRestorationResize.Custom => $"{s.CustomWidth}:{s.CustomHeight}", _ => "" };
        if (!string.IsNullOrEmpty(scale)) filters.Add($"scale={scale}:flags=lanczos");
    }
    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static VideoRestorationSettings WithAi(VideoRestorationSettings source, VideoRestorationSettings preset)
    {
        preset.AiMode = source.AiMode; preset.AiModelId = source.AiModelId; preset.AiScale = source.AiScale; preset.AiDevice = source.AiDevice; preset.AiBackendPath = source.AiBackendPath; preset.AiModelsDirectory = source.AiModelsDirectory;
        return preset;
    }
}

public sealed record VideoRestorationPipelinePlan(string ConventionalFilterChain, string PreAiFilterChain, string PostAiFilterChain, bool UsesAi)
{
    public string DescribeStages() => UsesAi
        ? $"pre-cleanup={Display(PreAiFilterChain)} -> AI -> finishing={Display(PostAiFilterChain)}"
        : $"FFmpeg={Display(ConventionalFilterChain)}";
    private static string Display(string filters) => string.IsNullOrWhiteSpace(filters) ? "none" : filters;
}
