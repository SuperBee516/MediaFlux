using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal sealed record ValidatedEncoderSettings(
        ResolvedVideoEncoder Resolved,
        bool UseGpu,
        string Preset,
        int QualityValue,
        bool TenBit,
        bool ConcurrentEncoderSessions);

    internal static class EncodingRequestValidator
    {
        public static ValidatedEncoderSettings ValidateAndNormalize(
            EncoderRegistry registry,
            VideoEncoderSelection selection,
            bool useGpu,
            double? targetMb,
            string? preset,
            int? qualityValue,
            bool tenBit,
            int? audioChannels,
            bool concurrentEncoderSessions)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(selection);

            ResolvedVideoEncoder resolved =
                selection.EncoderId.Equals(
                    VideoEncoderIds.LegacySoftware,
                    StringComparison.OrdinalIgnoreCase)
                    ? registry.ResolveLegacyCodec(selection.FfmpegCodec)
                    : registry.Resolve(
                        selection.EncoderId,
                        selection.CodecFamily);
            if (!resolved.Selection.FfmpegCodec.Equals(
                    selection.FfmpegCodec,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Encoder '{selection.EncoderId}' with " +
                    $"{selection.CodecFamily} must use FFmpeg codec " +
                    $"'{resolved.Selection.FfmpegCodec}', not " +
                    $"'{selection.FfmpegCodec}'.");
            }

            if (targetMb.HasValue && targetMb.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetMb),
                    "Target size must be greater than zero.");
            }

            if (audioChannels.HasValue &&
                (audioChannels.Value < 1 || audioChannels.Value > 8))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(audioChannels),
                    "Audio channel count must be between 1 and 8.");
            }

            IVideoEncoderProvider provider = resolved.Provider;
            EncoderCapabilities capabilities = provider.Capabilities;
            bool normalizedTenBit =
                tenBit &&
                capabilities.SupportsTenBit &&
                selection.CodecFamily is
                    VideoCodecFamily.Hevc or VideoCodecFamily.Av1;

            return new ValidatedEncoderSettings(
                resolved,
                UseGpu: useGpu && capabilities.IsHardware,
                Preset: provider.NormalizePreset(preset),
                QualityValue: provider.NormalizeQuality(
                    selection.CodecFamily,
                    qualityValue),
                TenBit: normalizedTenBit,
                ConcurrentEncoderSessions:
                    concurrentEncoderSessions &&
                    capabilities.IsHardware &&
                    capabilities.SupportsConcurrentJobs);
        }

        public static void EnsureEncoderAvailable(
            VideoEncoderSelection selection,
            FfmpegEncoderCapabilities capabilities)
        {
            ArgumentNullException.ThrowIfNull(selection);
            ArgumentNullException.ThrowIfNull(capabilities);

            if (!capabilities.InspectionSucceeded)
                return;
            if (capabilities.Contains(selection.FfmpegCodec))
                return;

            throw new NotSupportedException(
                $"The selected FFmpeg executable does not provide the " +
                $"'{selection.FfmpegCodec}' encoder required by " +
                $"'{selection.EncoderId}'. Choose another encoder or " +
                "configure a different FFmpeg build in Settings.");
        }
    }
}
