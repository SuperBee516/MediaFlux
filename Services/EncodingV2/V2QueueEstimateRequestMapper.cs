using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services.EncodingV2;

/// <summary>
/// Maps validated fixed encoder settings to the V2 request contract. Adaptive,
/// target-size, custom, restoration, and ten-bit jobs remain on the existing path.
/// </summary>
public static class V2QueueEstimateRequestMapper
{
    public static bool TryCreate(
        string sourcePath,
        VideoEncoderSelection encoder,
        int qualityValue,
        string compressionProfile,
        string preset,
        OutputContainerSelection containerSelection,
        int? targetHeight,
        int? audioChannels,
        bool tenBit,
        bool adaptiveQuality,
        bool targetSize,
        bool customSettings,
        bool restorationEnabled,
        out V2FastEstimateCommandRequest? request,
        out string reason)
    {
        request = null;
        reason = "";
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            return Reject("A fully qualified source path is required.", out reason);
        ArgumentNullException.ThrowIfNull(encoder);
        if (adaptiveQuality)
            return Reject("Source Adaptive quality remains on the existing estimate path.", out reason);
        if (targetSize)
            return Reject("Target-size estimates remain on the existing estimate path.", out reason);
        if (customSettings)
            return Reject("Custom queue settings remain on the existing estimate path.", out reason);
        if (restorationEnabled)
            return Reject("Restoration estimates remain on the existing estimate path.", out reason);
        if (tenBit)
            return Reject("Ten-bit estimates remain on the existing estimate path.", out reason);
        if (qualityValue < 0 || qualityValue > 63)
            return Reject("The validated encoder quality is outside the supported range.", out reason);

        string codec = encoder.CodecFamily switch
        {
            VideoCodecFamily.H264 => "h264",
            VideoCodecFamily.Hevc => "hevc",
            VideoCodecFamily.Av1 => "av1",
            _ => ""
        };
        string encoderName = encoder.FfmpegCodec.ToLowerInvariant();
        bool nvenc = encoderName.EndsWith("_nvenc", StringComparison.Ordinal);
        bool supported = (encoderName, codec) is
            ("libx264", "h264") or
            ("libx265", "hevc") or
            ("libsvtav1", "av1") or
            ("h264_nvenc", "h264") or
            ("hevc_nvenc", "hevc") or
            ("av1_nvenc", "av1");
        if (!supported)
            return Reject("This encoder configuration is not supported by the V2 fast estimator.", out reason);
        if (audioChannels is < 1 or > 8)
            return Reject("Selected audio channels are outside the supported range.", out reason);
        if (targetHeight is <= 0 || (targetHeight.HasValue && targetHeight.Value % 2 != 0))
            return Reject("The output height must be a positive even number.", out reason);

        string container = containerSelection switch
        {
            OutputContainerSelection.Matroska => "mkv",
            OutputContainerSelection.Mp4 => "mp4",
            OutputContainerSelection.Auto when Path.GetExtension(sourcePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase) => "mkv",
            OutputContainerSelection.Auto => "mp4",
            _ => ""
        };
        if (container.Length == 0)
            return Reject("The selected output container is unsupported.", out reason);

        // Preserve the legacy File-size estimate choices as estimate-only offsets.
        // The existing profile multiplier expresses a relative bitrate scale against
        // Medium; V2's CQ/CRF model changes bitrate by 2x for every 6 quality steps.
        // This value is used only by the fast estimate request, never the encode plan.
        double profileScale = SizeEstimateService.GetCompressionMultiplier(compressionProfile);
        double defaultProfileScale = SizeEstimateService.GetCompressionMultiplier("Medium Quality (Default)");
        double estimateQualityValue = Math.Clamp(
            qualityValue - 6d * Math.Log2(profileScale / defaultProfileScale),
            0d,
            encoderName == "libsvtav1" ? 63d : 51d);

        request = new V2FastEstimateCommandRequest
        {
            SourcePath = Path.GetFullPath(sourcePath),
            Encoder = encoderName,
            Codec = codec,
            Container = container,
            QualityMode = nvenc ? "cq" : "crf",
            QualityValue = estimateQualityValue,
            Preset = preset ?? "",
            GeometryMode = targetHeight.HasValue ? "fitBox" : "source",
            MaxWidth = targetHeight.HasValue ? 8192 : null,
            MaxHeight = targetHeight,
            AudioMode = audioChannels.HasValue ? "aac" : "copy",
            AudioBitrateKbps = audioChannels.HasValue
                ? audioChannels.Value >= 6 ? 384 : 192
                : null
        };
        return true;
    }

    private static bool Reject(string value, out string reason)
    {
        reason = value;
        return false;
    }
}
