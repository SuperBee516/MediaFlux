using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal static class EncoderAvailability
    {
        public static IReadOnlyList<EncoderCapabilities> GetAvailableEncoders(
            EncoderRegistry registry,
            FfmpegEncoderCapabilities ffmpegCapabilities)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(ffmpegCapabilities);

            IReadOnlyList<EncoderCapabilities> encoders =
                registry.GetCapabilities();
            if (!ffmpegCapabilities.InspectionSucceeded)
                return encoders;

            return encoders
                .Where(encoder => GetAvailableCodecs(
                    registry,
                    encoder,
                    ffmpegCapabilities).Count > 0)
                .ToArray();
        }

        public static IReadOnlyList<VideoCodecFamily> GetAvailableCodecs(
            EncoderRegistry registry,
            EncoderCapabilities encoder,
            FfmpegEncoderCapabilities ffmpegCapabilities)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(encoder);
            ArgumentNullException.ThrowIfNull(ffmpegCapabilities);

            if (!ffmpegCapabilities.InspectionSucceeded)
                return encoder.SupportedCodecs;

            return encoder.SupportedCodecs
                .Where(codec =>
                {
                    VideoEncoderSelection selection =
                        registry.Resolve(encoder.Id, codec).Selection;
                    return ffmpegCapabilities.Contains(
                        selection.FfmpegCodec);
                })
                .ToArray();
        }
    }
}
