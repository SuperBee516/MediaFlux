using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal sealed class NvencEncoderProvider : IVideoEncoderProvider
    {
        private static readonly string[] ValidPresets =
            ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];

        public EncoderCapabilities Capabilities { get; } = new()
        {
            Id = VideoEncoderIds.Nvenc,
            DisplayName = "GPU (NVENC)",
            IsHardware = true,
            SupportsTenBit = true,
            SupportsConcurrentJobs = true,
            SupportedCodecs =
                [VideoCodecFamily.H264, VideoCodecFamily.Hevc, VideoCodecFamily.Av1],
            Presets = ValidPresets
                .Select(value => new EncoderPresetOption(value, value))
                .ToArray(),
            DefaultPreset = "p5",
            QualityRange = new EncoderQualityRange("CQ", 0, 51)
        };

        public string GetFfmpegCodec(VideoCodecFamily codecFamily) =>
            codecFamily switch
            {
                VideoCodecFamily.H264 => "h264_nvenc",
                VideoCodecFamily.Hevc => "hevc_nvenc",
                VideoCodecFamily.Av1 => "av1_nvenc",
                _ => throw new ArgumentOutOfRangeException(nameof(codecFamily))
            };

        public string NormalizePreset(string? requestedPreset)
        {
            if (string.IsNullOrWhiteSpace(requestedPreset))
                return Capabilities.DefaultPreset;

            string token = requestedPreset.Trim();
            if (!token.StartsWith("p", StringComparison.OrdinalIgnoreCase))
                return Capabilities.DefaultPreset;

            string first = token.Split(' ')[0];
            return ValidPresets.Contains(first, StringComparer.Ordinal)
                ? first
                : Capabilities.DefaultPreset;
        }

        public int NormalizeQuality(
            VideoCodecFamily codecFamily,
            int? requestedQuality)
        {
            int defaultQuality = codecFamily switch
            {
                VideoCodecFamily.H264 => 22,
                VideoCodecFamily.Hevc => 24,
                _ => 28
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
            if (!context.UseGpu || context.IsAsfFamilyInput)
                return;

            if (context.WantsTenBit)
                builder.Append("-hwaccel cuda ");
            else
                builder.Append("-hwaccel cuda -hwaccel_output_format cuda ");
        }

        public void AppendVideoFilters(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            if (!string.IsNullOrEmpty(context.ScaleExpression) &&
                context.UseGpu &&
                !context.WantsTenBit)
            {
                builder.Append(
                    $"-vf scale_cuda={context.ScaleExpression}:interp_algo=lanczos ");
                return;
            }

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
                $"-rc vbr " +
                $"-preset {context.Preset} ");

            if (context.Selection.CodecFamily == VideoCodecFamily.Av1)
                builder.Append($"-cq {context.QualityValue} ");

            AppendTuningOptions(builder, context);
        }

        public void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);

            builder.Append(
                $"-rc vbr -cq {context.QualityValue} " +
                $"-preset {context.Preset} ");
            AppendTuningOptions(builder, context);
        }

        private static void AppendTuningOptions(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            builder.Append("-tune hq ");

            if (context.ConcurrentEncoderSessions)
            {
                builder.Append(
                    "-rc-lookahead 12 -spatial_aq 1 -temporal_aq 1 " +
                    "-aq-strength 8 -surfaces 24 ");
            }
            else
            {
                builder.Append(
                    "-rc-lookahead 32 -spatial_aq 1 -temporal_aq 1 " +
                    "-aq-strength 12 -surfaces 48 ");

                if (context.Selection.CodecFamily != VideoCodecFamily.Av1)
                    builder.Append("-multipass fullres ");
            }

            if (context.Selection.CodecFamily is
                VideoCodecFamily.H264 or VideoCodecFamily.Hevc)
            {
                if (context.ConcurrentEncoderSessions)
                    builder.Append("-bf 3 -b_ref_mode middle -refs 3 ");
                else
                    builder.Append("-bf 4 -b_ref_mode middle -refs 4 ");
            }
        }
    }
}
