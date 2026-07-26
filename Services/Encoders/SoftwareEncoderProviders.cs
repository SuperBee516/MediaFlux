using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal abstract class SoftwareEncoderProviderBase : IVideoEncoderProvider
    {
        public abstract EncoderCapabilities Capabilities { get; }

        public abstract string GetFfmpegCodec(VideoCodecFamily codecFamily);

        public virtual string NormalizePreset(string? requestedPreset) =>
            Capabilities.DefaultPreset;

        public int NormalizeQuality(
            VideoCodecFamily codecFamily,
            int? requestedQuality)
        {
            EncoderQualityRange range = Capabilities.QualityRange ??
                throw new InvalidOperationException(
                    $"Encoder '{Capabilities.Id}' has no quality range.");
            return Math.Clamp(
                requestedQuality ?? GetDefaultQuality(codecFamily),
                range.Minimum,
                range.Maximum);
        }

        public virtual void AppendInputAcceleration(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            // Software encoders never receive CUDA or hardware-backend input
            // arguments. Hardware decode rules belong to hardware providers.
        }

        public void AppendVideoFilters(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendSoftwareVideoFilters(builder, context);
        }

        public virtual void AppendTargetSizeArguments(
            StringBuilder builder,
            EncoderArgumentContext context,
            double videoKbps,
            double maxRateKbps,
            double bufferSizeKbps)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);
            builder.Append($"-b:v {videoKbps:F0}k -preset slow ");
        }

        public abstract void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context);

        protected abstract int GetDefaultQuality(
            VideoCodecFamily codecFamily);

        protected static void EnsureCodec(
            VideoCodecFamily actual,
            VideoCodecFamily expected,
            string encoderId)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"Encoder '{encoderId}' does not support {actual}.");
            }
        }
    }

    internal sealed class Libx264EncoderProvider : SoftwareEncoderProviderBase
    {
        public override EncoderCapabilities Capabilities { get; } = new()
        {
            Id = VideoEncoderIds.Libx264,
            DisplayName = "CPU (libx264)",
            IsHardware = false,
            SupportsTenBit = false,
            SupportsConcurrentJobs = false,
            SupportedCodecs = [VideoCodecFamily.H264],
            Presets = [new EncoderPresetOption("slow", "slow")],
            DefaultPreset = "slow",
            QualityRange = new EncoderQualityRange("CRF", 0, 51)
        };

        public override string GetFfmpegCodec(VideoCodecFamily codecFamily)
        {
            EnsureCodec(codecFamily, VideoCodecFamily.H264, Capabilities.Id);
            return "libx264";
        }

        public override void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);
            builder.Append(
                $"-crf {context.QualityValue} -preset slow ");
        }

        protected override int GetDefaultQuality(
            VideoCodecFamily codecFamily) => 23;
    }

    internal sealed class Libx265EncoderProvider : SoftwareEncoderProviderBase
    {
        private static readonly string[] ValidPresets =
        [
            "ultrafast",
            "superfast",
            "veryfast",
            "faster",
            "fast",
            "medium",
            "slow",
            "slower",
            "veryslow",
            "placebo"
        ];

        public override EncoderCapabilities Capabilities { get; } = new()
        {
            Id = VideoEncoderIds.Libx265,
            DisplayName = "CPU (libx265)",
            IsHardware = false,
            SupportsTenBit = true,
            SupportsConcurrentJobs = false,
            SupportedCodecs = [VideoCodecFamily.Hevc],
            Presets = ValidPresets
                .Select(value => new EncoderPresetOption(value, value))
                .ToArray(),
            DefaultPreset = "slow",
            QualityRange = new EncoderQualityRange("CRF", 0, 51)
        };

        public override string GetFfmpegCodec(VideoCodecFamily codecFamily)
        {
            EnsureCodec(codecFamily, VideoCodecFamily.Hevc, Capabilities.Id);
            return "libx265";
        }

        public override string NormalizePreset(string? requestedPreset)
        {
            if (string.IsNullOrWhiteSpace(requestedPreset))
                return Capabilities.DefaultPreset;

            string preset = requestedPreset.Trim();
            return ValidPresets.FirstOrDefault(
                       item => item.Equals(
                           preset,
                           StringComparison.OrdinalIgnoreCase)) ??
                   Capabilities.DefaultPreset;
        }

        public override void AppendTargetSizeArguments(
            StringBuilder builder,
            EncoderArgumentContext context,
            double videoKbps,
            double maxRateKbps,
            double bufferSizeKbps)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(
                builder,
                context);
            builder.Append(
                $"-b:v {videoKbps:F0}k " +
                $"-maxrate {maxRateKbps:F0}k " +
                $"-bufsize {bufferSizeKbps:F0}k " +
                $"-preset {context.Preset} ");
        }

        public override void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);
            builder.Append(
                $"-crf {context.QualityValue} " +
                $"-preset {context.Preset} ");
        }

        protected override int GetDefaultQuality(
            VideoCodecFamily codecFamily) => 24;
    }

    internal sealed class SvtAv1EncoderProvider : SoftwareEncoderProviderBase
    {
        public override EncoderCapabilities Capabilities { get; } = new()
        {
            Id = VideoEncoderIds.SvtAv1,
            DisplayName = "CPU (SVT-AV1)",
            IsHardware = false,
            SupportsTenBit = true,
            SupportsConcurrentJobs = false,
            SupportedCodecs = [VideoCodecFamily.Av1],
            Presets = [new EncoderPresetOption("6", "6")],
            DefaultPreset = "6",
            QualityRange = new EncoderQualityRange("CRF", 0, 63)
        };

        public override string GetFfmpegCodec(VideoCodecFamily codecFamily)
        {
            EnsureCodec(codecFamily, VideoCodecFamily.Av1, Capabilities.Id);
            return "libsvtav1";
        }

        public override void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);
            builder.Append(
                $"-crf {context.QualityValue} -preset 6 ");
        }

        protected override int GetDefaultQuality(
            VideoCodecFamily codecFamily) => 30;
    }

    internal sealed class LegacySoftwareEncoderProvider :
        SoftwareEncoderProviderBase
    {
        private readonly string _ffmpegCodec;
        private readonly VideoCodecFamily _codecFamily;

        public LegacySoftwareEncoderProvider(
            string ffmpegCodec,
            VideoCodecFamily codecFamily)
        {
            _ffmpegCodec = ffmpegCodec;
            _codecFamily = codecFamily;
            Capabilities = new EncoderCapabilities
            {
                Id = VideoEncoderIds.LegacySoftware,
                DisplayName = ffmpegCodec,
                IsHardware = false,
                SupportsTenBit = codecFamily != VideoCodecFamily.H264,
                SupportsConcurrentJobs = false,
                SupportedCodecs = [codecFamily],
                Presets = [new EncoderPresetOption("6", "6")],
                DefaultPreset = "6",
                QualityRange = new EncoderQualityRange("CRF", 0, 63)
            };
        }

        public override EncoderCapabilities Capabilities { get; }

        public override string GetFfmpegCodec(VideoCodecFamily codecFamily)
        {
            EnsureCodec(codecFamily, _codecFamily, Capabilities.Id);
            return _ffmpegCodec;
        }

        public override void AppendQualityArguments(
            StringBuilder builder,
            EncoderArgumentContext context)
        {
            EncoderProviderUtilities.AppendCodecAndTenBitFlags(builder, context);
            builder.Append(
                $"-crf {context.QualityValue} -preset 6 ");
        }

        protected override int GetDefaultQuality(
            VideoCodecFamily codecFamily) => 30;
    }
}
