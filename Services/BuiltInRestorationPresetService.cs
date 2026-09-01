using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Creates Custom settings from the built-in restoration starting points.</summary>
public static class BuiltInRestorationPresetService
{
    public static VideoRestorationSettings Apply(BuiltInRestorationPreset preset, VideoRestorationSettings? current = null)
    {
        VideoRestorationSettings settings = new()
        {
            Mode = VideoRestorationMode.Custom,
            Preset = VideoRestorationPreset.Custom,
            AiDevice = string.IsNullOrWhiteSpace(current?.AiDevice) ? "Auto" : current.AiDevice,
            AiBackendPath = current?.AiBackendPath ?? "",
            AiModelsDirectory = current?.AiModelsDirectory ?? ""
        };

        switch (preset)
        {
            case BuiltInRestorationPreset.ClassicCartoon:
                settings.AiMode = AiRestorationMode.Animation; settings.AiModelId = "realesr-animevideov3"; settings.AiScale = AiRestorationScale.X2;
                settings.Denoise = VideoRestorationStrength.Light; settings.Sharpen = VideoRestorationStrength.Light;
                break;
            case BuiltInRestorationPreset.Anime:
                settings.AiMode = AiRestorationMode.Animation; settings.AiModelId = "realesr-animevideov3"; settings.AiScale = AiRestorationScale.X2;
                settings.Sharpen = VideoRestorationStrength.Light;
                break;
            case BuiltInRestorationPreset.DvdUpscale:
                settings.AiMode = AiRestorationMode.General; settings.AiScale = AiRestorationScale.X2;
                settings.Denoise = VideoRestorationStrength.Light; settings.Deblock = VideoRestorationStrength.Light;
                break;
            case BuiltInRestorationPreset.VhsCleanup:
                settings.Denoise = VideoRestorationStrength.Strong; settings.Deblock = VideoRestorationStrength.Medium;
                settings.Deinterlace = VideoRestorationDeinterlace.AutoSafe;
                break;
            case BuiltInRestorationPreset.FilmPreservation:
                // Preserve grain: this is intentionally a no-filter Custom baseline.
                break;
            case BuiltInRestorationPreset.LiveActionHdCleanup:
                settings.Denoise = VideoRestorationStrength.Light; settings.Deblock = VideoRestorationStrength.Light;
                break;
            case BuiltInRestorationPreset.LightCleanup:
                settings.Denoise = VideoRestorationStrength.Light;
                break;
            case BuiltInRestorationPreset.HeavyRestoration:
                settings.Denoise = VideoRestorationStrength.Strong; settings.Deblock = VideoRestorationStrength.Strong;
                settings.Deband = VideoRestorationStrength.Strong; settings.Sharpen = VideoRestorationStrength.Strong;
                break;
            case BuiltInRestorationPreset.AiGeneralEnhancement:
                settings.AiMode = AiRestorationMode.General;
                settings.AiModelId = current?.AiMode == AiRestorationMode.General ? current.AiModelId : "";
                settings.AiScale = current?.AiScale ?? AiRestorationScale.X2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preset));
        }

        return settings;
    }
}
