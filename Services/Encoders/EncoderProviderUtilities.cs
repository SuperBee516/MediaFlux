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

            // NVENC receives CUDA frames as the `cuda` hardware format.  Asking
            // FFmpeg to force nv12/p010le at this boundary inserts auto_scale
            // and breaks otherwise compatible zero-copy jobs.  The matching
            // source format and explicit Main/Main10 profile remain the output
            // contract; conversion paths always emit the software pix_fmt.
            if (!context.UseGpuResidentFrames)
                builder.Append($"-pix_fmt {context.OutputPixelFormat} ");
            if (context.WantsTenBit && context.UseGpuResidentHighBitDepthOutput)
                builder.Append("-highbitdepth 1 ");
        }

    }
}
