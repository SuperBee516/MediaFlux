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
    public static VideoRestorationPipelinePlan BuildPlan(VideoRestorationSettings? settings, EncodingService.ScaleMode encodeScale, string? aiIntermediateFinalScaleFilter = null)
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
        if (encodeScale == EncodingService.ScaleMode.None)
        {
            AddResize(post, s);
            if (s.Resize == VideoRestorationResize.Original && !string.IsNullOrWhiteSpace(aiIntermediateFinalScaleFilter))
                post.Add(aiIntermediateFinalScaleFilter);
        }
        AddStrength(post, s.Sharpen, "unsharp", new[] { "unsharp=5:5:0.3:5:5:0", "unsharp=5:5:0.6:5:5:0", "unsharp=5:5:0.9:5:5:0" });
        return new VideoRestorationPipelinePlan("", string.Join(',', pre), string.Join(',', post), true);
    }

    public static void Validate(VideoRestorationSettings settings, EncodingService.ScaleMode encodeScale)
    {
        if (settings.Brightness is < -1 or > 1 || settings.Contrast is < 0.5m or > 2 || settings.Saturation is < 0 or > 2) throw new ArgumentException("Restoration color values are outside safe ranges.");
        if (settings.Resize == VideoRestorationResize.Custom && (settings.CustomWidth is < 64 or > 7680 || settings.CustomHeight is < 64 or > 4320)) throw new ArgumentException("Custom restoration resolution must be between 64x64 and 7680x4320.");
        if (settings.Resize != VideoRestorationResize.Original && encodeScale != EncodingService.ScaleMode.None) throw new ArgumentException("Choose either restoration resize or the normal encode resolution, not both.");
    }

    /// <summary>Central final-output intent. AI scale is deliberately excluded from this plan.</summary>
    public static VideoOutputResolutionPlan ResolveFinalOutputResolution(int sourceWidth, int sourceHeight, VideoRestorationSettings? settings, EncodingService.ScaleMode encodeScale)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth), "A known source resolution is required.");
        VideoRestorationSettings s = Effective(settings);
        Validate(s, encodeScale);
        return encodeScale switch
        {
            EncodingService.ScaleMode.To720p => FromHeight(sourceWidth, sourceHeight, 720, "normal encode 720p"),
            EncodingService.ScaleMode.To1080p => FromHeight(sourceWidth, sourceHeight, 1080, "normal encode 1080p"),
            EncodingService.ScaleMode.To1440p => FromHeight(sourceWidth, sourceHeight, 1440, "normal encode 1440p"),
            EncodingService.ScaleMode.To4K => FromHeight(sourceWidth, sourceHeight, 2160, "normal encode 4K"),
            _ => FromRestorationResize(sourceWidth, sourceHeight, s)
        };
    }

    private static VideoOutputResolutionPlan FromRestorationResize(int sourceWidth, int sourceHeight, VideoRestorationSettings settings) => settings.Resize switch
    {
        VideoRestorationResize.To720p => FromHeight(sourceWidth, sourceHeight, 720, "restoration resize 720p"),
        VideoRestorationResize.To1080p => FromHeight(sourceWidth, sourceHeight, 1080, "restoration resize 1080p"),
        VideoRestorationResize.Custom when settings.PreserveAspectRatio => Fit(sourceWidth, sourceHeight, settings.CustomWidth, settings.CustomHeight, "restoration custom aspect-preserving resize"),
        VideoRestorationResize.Custom => new(Even(settings.CustomWidth), Even(settings.CustomHeight), $"scale={Even(settings.CustomWidth)}:{Even(settings.CustomHeight)}:flags=lanczos", "restoration custom resize"),
        _ => new(sourceWidth, sourceHeight, $"scale={sourceWidth}:{sourceHeight}:flags=lanczos", "original source resolution")
    };

    private static VideoOutputResolutionPlan FromHeight(int sourceWidth, int sourceHeight, int height, string reason) =>
        new(Even((int)Math.Round(sourceWidth * (height / (double)sourceHeight), MidpointRounding.AwayFromZero)), height, $"scale=-2:{height}:flags=lanczos", reason);
    private static VideoOutputResolutionPlan Fit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight, string reason)
    {
        int evenMaxWidth = Even(maxWidth), evenMaxHeight = Even(maxHeight);
        double factor = Math.Min(evenMaxWidth / (double)sourceWidth, evenMaxHeight / (double)sourceHeight);
        return new(Even((int)Math.Floor(sourceWidth * factor)), Even((int)Math.Floor(sourceHeight * factor)), $"scale={evenMaxWidth}:{evenMaxHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos", reason);
    }
    private static int Even(int value) => Math.Max(2, value - value % 2);

    private static void AddStrength(List<string> filters, VideoRestorationStrength strength, string filter, string[] values) { if (strength != VideoRestorationStrength.Off) Add(filters, filter, values[(int)strength - 1]); }
    private static void Add(List<string> filters, string filter, string expression) { if (_availableFilters == null || _availableFilters.Contains(filter)) filters.Add(expression); }
    private static void AddResize(List<string> filters, VideoRestorationSettings s)
    {
        string scale = s.Resize switch { VideoRestorationResize.To720p => "-2:720", VideoRestorationResize.To1080p => "-2:1080", VideoRestorationResize.Custom when s.PreserveAspectRatio => $"{Even(s.CustomWidth)}:{Even(s.CustomHeight)}:force_original_aspect_ratio=decrease:force_divisible_by=2", VideoRestorationResize.Custom => $"{Even(s.CustomWidth)}:{Even(s.CustomHeight)}", _ => "" };
        if (!string.IsNullOrEmpty(scale)) filters.Add($"scale={scale}:flags=lanczos");
    }
    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static VideoRestorationSettings WithAi(VideoRestorationSettings source, VideoRestorationSettings preset)
    {
        preset.AiMode = source.AiMode; preset.AiModelId = source.AiModelId; preset.AiScale = source.AiScale; preset.AiDevice = source.AiDevice; preset.AiBackendPath = source.AiBackendPath; preset.AiModelsDirectory = source.AiModelsDirectory; preset.AiBackendSelection = source.AiBackendSelection;
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

/// <summary>The final encoded dimensions, independent of any AI intermediate upscale.</summary>
public sealed record VideoOutputResolutionPlan(int Width, int Height, string ScaleFilter, string Reason)
{
    public string Describe() => $"{Width}x{Height} ({Reason})";
}
