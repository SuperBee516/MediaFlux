using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal sealed class EncoderArgumentContext
    {
        public required VideoEncoderSelection Selection { get; init; }
        public required bool UseGpu { get; init; }
        public required bool WantsTenBit { get; init; }
        public required string? TenBitPixelFormat { get; init; }
        public required string ScaleExpression { get; init; }
        public required string Preset { get; init; }
        public required int QualityValue { get; init; }
        public required bool ConcurrentEncoderSessions { get; init; }
        public required bool IsAsfFamilyInput { get; init; }
        public required bool UseGpuResidentHighBitDepthOutput { get; init; }
        public required bool UseGpuResidentFrames { get; init; }
        public required bool UseGpuResidentFormatConversion { get; init; }
        public required bool RequiresVideoFilter { get; init; }
        public required string OutputPixelFormat { get; init; }
    }

    internal interface IVideoEncoderProvider
    {
        EncoderCapabilities Capabilities { get; }

        string GetFfmpegCodec(VideoCodecFamily codecFamily);

        string NormalizePreset(string? requestedPreset);

        int NormalizeQuality(
            VideoCodecFamily codecFamily,
            int? requestedQuality);

        void AppendInputAcceleration(
            StringBuilder builder,
            EncoderArgumentContext context);

        void AppendVideoFilters(
            StringBuilder builder,
            EncoderArgumentContext context);

        void AppendTargetSizeArguments(
            StringBuilder builder,
            EncoderArgumentContext context,
            double videoKbps,
            double maxRateKbps,
            double bufferSizeKbps);

        void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context);
    }
}
