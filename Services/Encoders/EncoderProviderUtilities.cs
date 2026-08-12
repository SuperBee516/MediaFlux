using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal static class EncoderProviderUtilities
    {
        public static void AppendSoftwareVideoFilters(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            if (!string.IsNullOrEmpty(context.ScaleExpression))
            {
                if (context.WantsTenBit &&
                    !string.IsNullOrEmpty(context.TenBitPixelFormat))
                {
                    builder.Append(
                        $"-vf scale={context.ScaleExpression}:flags=lanczos," +
                        $"format={context.TenBitPixelFormat} ");
                }
                else
                {
                    builder.Append(
                        $"-vf scale={context.ScaleExpression}:flags=lanczos ");
                }
            }
            else if (context.WantsTenBit &&
                     !string.IsNullOrEmpty(context.TenBitPixelFormat))
            {
                builder.Append($"-vf format={context.TenBitPixelFormat} ");
            }
        }

        public static void AppendCodecAndTenBitFlags(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            builder.Append($"-c:v {context.Selection.FfmpegCodec} ");

            if (!context.WantsTenBit ||
                string.IsNullOrEmpty(context.TenBitPixelFormat))
            {
                return;
            }

            if (context.UseGpuResidentHighBitDepthOutput)
            {
                if (context.Selection.CodecFamily == VideoCodecFamily.Hevc)
                    builder.Append("-profile:v main10 ");

                builder.Append("-highbitdepth 1 ");
                return;
            }

            if (context.Selection.CodecFamily == VideoCodecFamily.Hevc)
            {
                builder.Append(
                    $"-profile:v main10 -pix_fmt {context.TenBitPixelFormat} ");
            }
            else if (context.Selection.CodecFamily == VideoCodecFamily.Av1)
            {
                builder.Append($"-pix_fmt {context.TenBitPixelFormat} ");
            }
        }

    }
}
