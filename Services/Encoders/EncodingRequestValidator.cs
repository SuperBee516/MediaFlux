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
            if (tenBit &&
                (!capabilities.SupportsTenBit ||
                 selection.CodecFamily == VideoCodecFamily.H264))
            {
                throw new NotSupportedException(
                    $"Requested: {selection.CodecFamily} 10-bit. " +
                    $"Encoder '{selection.FfmpegCodec}' does not support that output format. " +
                    "Choose an HEVC/AV1 10-bit-capable encoder or request 8-bit output.");
            }

            return new ValidatedEncoderSettings(
                resolved,
                UseGpu: useGpu && capabilities.IsHardware,
                Preset: provider.NormalizePreset(preset),
                QualityValue: provider.NormalizeQuality(
                    selection.CodecFamily,
                    qualityValue),
                TenBit: tenBit,
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
