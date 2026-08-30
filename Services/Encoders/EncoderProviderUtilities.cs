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
            if (!context.RequiresVideoFilter)
                return;

            if (!string.IsNullOrEmpty(context.ScaleExpression))
            {
                builder.Append(
                    $"-vf scale={context.ScaleExpression}:flags=lanczos," +
                    $"format={context.OutputPixelFormat} ");
            }
            else
            {
                builder.Append($"-vf format={context.OutputPixelFormat} ");
            }
        }

        public static string BuildCudaVideoFilter(
            string scaleExpression,
            string outputPixelFormat)
        {
            if (string.IsNullOrWhiteSpace(outputPixelFormat))
                throw new ArgumentException("An output pixel format is required.", nameof(outputPixelFormat));

            // FFmpeg's first filter option uses '=' (not ':'). Subsequent named
            // options are colon-separated, for example scale_cuda=-2:1080:format=nv12.
            return string.IsNullOrEmpty(scaleExpression)
                ? $"scale_cuda=format={outputPixelFormat}"
                : $"scale_cuda={scaleExpression}:interp_algo=lanczos:format={outputPixelFormat}";
        }

        public static void AppendCodecAndTenBitFlags(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            builder.Append($"-c:v {context.Selection.FfmpegCodec} ");
        }

        public static void AppendOutputFormatFlags(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            if (context.Selection.CodecFamily == VideoCodecFamily.Hevc)
            {
                builder.Append(context.WantsTenBit ? "-profile:v main10 " : "-profile:v main ");
            }
            else if (context.Selection.CodecFamily == VideoCodecFamily.H264)
            {
                builder.Append("-profile:v high ");
            }

            builder.Append($"-pix_fmt {context.OutputPixelFormat} ");
            if (context.WantsTenBit && context.UseGpuResidentHighBitDepthOutput)
                builder.Append("-highbitdepth 1 ");
        }

    }
}
