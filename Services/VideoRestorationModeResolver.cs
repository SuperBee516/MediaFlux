using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Resolves the sole restoration input used by encode and preview planning.</summary>
public static class VideoRestorationModeResolver
{
    public static VideoRestorationSettings Resolve(VideoRestorationSettings? settings) =>
        (settings?.Mode ?? VideoRestorationMode.Off) switch
        {
            VideoRestorationMode.Auto => ResolveAuto(settings!),
            VideoRestorationMode.Custom => ResolveCustom(settings!),
            _ => Disabled()
        };

    public static RestorationModeControlState ControlState(VideoRestorationMode mode) => mode switch
    {
        VideoRestorationMode.Off => new(false, false, false, false, "All restoration disabled.", "Video restoration is disabled."),
        VideoRestorationMode.Auto => new(true, true, true, false, "Analyze / Recommend controls restoration.", "Switch to Custom to edit advanced restoration settings."),
        _ => new(true, true, true, true, "Using Advanced restoration settings.", "")
    };

    private static VideoRestorationSettings ResolveAuto(VideoRestorationSettings settings)
    {
        VideoRestorationSettings resolved = settings.AutoRecommendation?.Clone() ?? Disabled();
        resolved.Mode = VideoRestorationMode.Auto;
        resolved.AutoRecommendation = null;
        return resolved;
    }

    private static VideoRestorationSettings ResolveCustom(VideoRestorationSettings settings)
    {
        VideoRestorationSettings resolved = settings.Clone();
        resolved.Mode = VideoRestorationMode.Custom;
        resolved.AutoRecommendation = null;
        return resolved;
    }

    private static VideoRestorationSettings Disabled() => new()
    {
        Mode = VideoRestorationMode.Off,
        Preset = VideoRestorationPreset.Off,
        AiMode = AiRestorationMode.Off
    };
}

public sealed record RestorationModeControlState(
    bool AnalyzeEnabled, bool PreviewEnabled, bool ApplyEnabled, bool AdvancedEnabled,
    string StatusText, string DisabledToolTip);
