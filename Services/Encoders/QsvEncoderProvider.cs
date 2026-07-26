using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal sealed class QsvEncoderProvider : IVideoEncoderProvider
    {
        public EncoderCapabilities Capabilities { get; } = new()
        {
            Id = VideoEncoderIds.Qsv,
            DisplayName = "GPU (QSV) Experimental",
            IsHardware = true,
            SupportsTenBit = true,
            SupportsConcurrentJobs = false,
            SupportedCodecs =
                [VideoCodecFamily.H264, VideoCodecFamily.Hevc, VideoCodecFamily.Av1],
            Presets = [new EncoderPresetOption("slow", "slow")],
            DefaultPreset = "slow",
            QualityRange = new EncoderQualityRange("Global quality", 1, 51)
        };

        public string GetFfmpegCodec(VideoCodecFamily codecFamily) =>
            codecFamily switch
            {
                VideoCodecFamily.H264 => "h264_qsv",
                VideoCodecFamily.Hevc => "hevc_qsv",
                VideoCodecFamily.Av1 => "av1_qsv",
                _ => throw new ArgumentOutOfRangeException(nameof(codecFamily))
            };

        public string NormalizePreset(string? requestedPreset) =>
            Capabilities.DefaultPreset;

        public int NormalizeQuality(
            VideoCodecFamily codecFamily,
            int? requestedQuality)
        {
            int defaultQuality = codecFamily switch
            {
                VideoCodecFamily.Hevc => 19,
                VideoCodecFamily.Av1 => 28,
                _ => 20
            };

            return Math.Clamp(
                requestedQuality ?? defaultQuality,
                Capabilities.QualityRange!.Minimum,
                Capabilities.QualityRange.Maximum);
        }

        public void AppendInputAcceleration(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            bool qsvHardwareDecodeIsSafe =
                context.UseGpu &&
                !context.IsAsfFamilyInput &&
                string.IsNullOrEmpty(context.ScaleExpression) &&
                !context.WantsTenBit;

            if (qsvHardwareDecodeIsSafe)
                builder.Append("-hwaccel qsv ");
        }

        public void AppendVideoFilters(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendSoftwareVideoFilters(builder, context);
        }

        public void AppendTargetSizeArguments(
            StringBuilder builder,
            EncoderArgumentContext context,
            double videoKbps,
            double maxRateKbps,
            double bufferSizeKbps)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);
            builder.Append(
                $"-b:v {videoKbps:F0}k " +
                $"-maxrate {maxRateKbps:F0}k " +
                $"-bufsize {bufferSizeKbps:F0}k " +
                "-rc_mode vbr -preset slow ");

            if (context.Selection.CodecFamily == VideoCodecFamily.Hevc)
                builder.Append("-mbbrc 1 ");
        }

        public void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);

            builder.Append(
                $"-rc_mode icq -global_quality {context.QualityValue} " +
                "-preset slow ");
            if (context.Selection.CodecFamily == VideoCodecFamily.Hevc)
                builder.Append("-mbbrc 1 ");
        }
    }
}
