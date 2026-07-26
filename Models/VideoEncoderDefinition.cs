namespace MediaFlux.Models
{
    public enum VideoCodecFamily
    {
        H264,
        Hevc,
        Av1
    }

    public static class VideoEncoderIds
    {
        public const string Nvenc = "nvenc";
        public const string Qsv = "qsv";
        public const string Libx264 = "libx264";
        public const string Libx265 = "libx265";
        public const string SvtAv1 = "svt-av1";
        internal const string LegacySoftware = "legacy-software";
    }

    public sealed record EncoderPresetOption(string Value, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public sealed record VideoCodecDisplayOption(
        VideoCodecFamily Value,
        string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public sealed record EncoderQualityRange(
        string Name,
        int Minimum,
        int Maximum);

    public sealed class EncoderCapabilities
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required bool IsHardware { get; init; }
        public required bool SupportsTenBit { get; init; }
        public required bool SupportsConcurrentJobs { get; init; }
        public required IReadOnlyList<VideoCodecFamily> SupportedCodecs { get; init; }
        public required IReadOnlyList<EncoderPresetOption> Presets { get; init; }
        public required string DefaultPreset { get; init; }
        public EncoderQualityRange? QualityRange { get; init; }

        public bool Supports(VideoCodecFamily codecFamily) =>
            SupportedCodecs.Contains(codecFamily);

        public override string ToString() => DisplayName;
    }

    public sealed record VideoEncoderSelection(
        string EncoderId,
        VideoCodecFamily CodecFamily,
        string FfmpegCodec);

    public static class VideoEncoderCompatibility
    {
        public static VideoCodecFamily ParseCodecFamily(
            string? value,
            VideoCodecFamily fallback = VideoCodecFamily.Hevc)
        {
            if (Enum.TryParse(value, true, out VideoCodecFamily parsed))
                return parsed;

            if (value?.Contains("AV1", StringComparison.OrdinalIgnoreCase) == true)
                return VideoCodecFamily.Av1;
            if (value?.Contains("264", StringComparison.OrdinalIgnoreCase) == true)
                return VideoCodecFamily.H264;
            if (value?.Contains("265", StringComparison.OrdinalIgnoreCase) == true ||
                value?.Contains("HEVC", StringComparison.OrdinalIgnoreCase) == true)
            {
                return VideoCodecFamily.Hevc;
            }

            return fallback;
        }

        public static string ResolveEncoderId(
            string? value,
            VideoCodecFamily codecFamily)
        {
            string encoder = value?.Trim() ?? string.Empty;
            if (encoder.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase) ||
                encoder.Contains("NVENC", StringComparison.OrdinalIgnoreCase))
            {
                return VideoEncoderIds.Nvenc;
            }

            if (encoder.Equals(VideoEncoderIds.Qsv, StringComparison.OrdinalIgnoreCase) ||
                encoder.Contains("QSV", StringComparison.OrdinalIgnoreCase) ||
                encoder.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                return VideoEncoderIds.Qsv;
            }

            if (encoder.Equals(VideoEncoderIds.Libx265, StringComparison.OrdinalIgnoreCase) ||
                encoder.Contains("libx265", StringComparison.OrdinalIgnoreCase))
            {
                return VideoEncoderIds.Libx265;
            }

            if (encoder.Equals(VideoEncoderIds.SvtAv1, StringComparison.OrdinalIgnoreCase) ||
                encoder.Contains("SVT", StringComparison.OrdinalIgnoreCase))
            {
                return VideoEncoderIds.SvtAv1;
            }

            if (encoder.Equals(VideoEncoderIds.Libx264, StringComparison.OrdinalIgnoreCase))
                return VideoEncoderIds.Libx264;

            // Older UI files stored one generic CPU choice and relied on the
            // selected format to choose the actual software encoder.
            if (encoder.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                encoder.Contains("libx264", StringComparison.OrdinalIgnoreCase))
            {
                return codecFamily switch
                {
                    VideoCodecFamily.H264 => VideoEncoderIds.Libx264,
                    VideoCodecFamily.Hevc => VideoEncoderIds.Libx265,
                    VideoCodecFamily.Av1 => VideoEncoderIds.SvtAv1,
                    _ => VideoEncoderIds.Libx264
                };
            }

            return VideoEncoderIds.Nvenc;
        }

        public static string NormalizeLegacyNvencPreset(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "p5";

            string preset = value.Trim();
            if (preset.StartsWith("p", StringComparison.OrdinalIgnoreCase) &&
                preset.Length >= 2 &&
                preset[1] is >= '1' and <= '7')
            {
                return preset[..2].ToLowerInvariant();
            }

            if (preset.StartsWith("Fastest", StringComparison.OrdinalIgnoreCase))
                return "p1";
            if (preset.StartsWith("Fast", StringComparison.OrdinalIgnoreCase))
                return "p2";
            if (preset.StartsWith("High Quality", StringComparison.OrdinalIgnoreCase))
                return "p6";
            if (preset.StartsWith("Max Quality", StringComparison.OrdinalIgnoreCase))
                return "p7";

            return "p5";
        }
    }
}
