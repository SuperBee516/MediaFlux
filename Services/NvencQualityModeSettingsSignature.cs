using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Effective NVENC quality-mode settings shared by finalized statistics and estimate admission.</summary>
public static class NvencQualityModeSettingsSignature
{
    public const string Version = "nvenc-hevc-quality-v1";

    public static string Create(string encoderId, string outputCodec, string preset,
        int? bitDepth, bool? concurrent)
    {
        if (!string.Equals(encoderId, VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase) ||
            !outputCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(preset) || bitDepth is not (8 or 10) || concurrent is null)
            return "";

        string concurrency = concurrent.Value
            ? "lookahead=12;aq=1:8;temporal=1;surfaces=24;bf=3;refs=3;multipass=default"
            : "lookahead=32;aq=1:12;temporal=1;surfaces=48;bf=4;refs=4;multipass=fullres";
        return $"{Version};codec=hevc_nvenc;mode=vbr-cq;preset={preset.Trim().ToLowerInvariant()};" +
            $"tune=hq;depth={bitDepth};{concurrency};bref=middle";
    }
}
