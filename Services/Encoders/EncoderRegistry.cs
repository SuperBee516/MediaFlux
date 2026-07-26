using MediaFlux.Models;

namespace MediaFlux.Services.Encoders
{
    internal sealed record ResolvedVideoEncoder(
        IVideoEncoderProvider Provider,
        VideoEncoderSelection Selection);

    internal sealed class EncoderRegistry
    {
        private readonly IReadOnlyDictionary<string, IVideoEncoderProvider> _providers;

        public static EncoderRegistry Default { get; } = new(
            [
                new NvencEncoderProvider(),
                new QsvEncoderProvider(),
                new Libx264EncoderProvider(),
                new Libx265EncoderProvider(),
                new SvtAv1EncoderProvider()
            ]);

        public EncoderRegistry(IEnumerable<IVideoEncoderProvider> providers)
        {
            _providers = providers.ToDictionary(
                provider => provider.Capabilities.Id,
                StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<EncoderCapabilities> GetCapabilities() =>
            _providers.Values
                .Select(provider => provider.Capabilities)
                .OrderBy(capabilities => capabilities.DisplayName)
                .ToArray();

        public ResolvedVideoEncoder Resolve(
            string encoderId,
            VideoCodecFamily codecFamily)
        {
            if (!_providers.TryGetValue(encoderId, out var provider))
            {
                throw new InvalidOperationException(
                    $"Unknown video encoder '{encoderId}'.");
            }

            if (!provider.Capabilities.Supports(codecFamily))
            {
                throw new InvalidOperationException(
                    $"Encoder '{encoderId}' does not support {codecFamily}.");
            }

            return new ResolvedVideoEncoder(
                provider,
                new VideoEncoderSelection(
                    provider.Capabilities.Id,
                    codecFamily,
                    provider.GetFfmpegCodec(codecFamily)));
        }

        public ResolvedVideoEncoder ResolveLegacyCodec(string ffmpegCodec)
        {
            if (string.IsNullOrWhiteSpace(ffmpegCodec))
                throw new ArgumentException(
                    "Video codec must be provided.",
                    nameof(ffmpegCodec));

            string codec = ffmpegCodec.Trim();
            VideoCodecFamily family = InferCodecFamily(codec);

            IVideoEncoderProvider provider;
            if (codec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
                provider = _providers[VideoEncoderIds.Nvenc];
            else if (codec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase))
                provider = _providers[VideoEncoderIds.Qsv];
            else if (codec.Equals("libx264", StringComparison.OrdinalIgnoreCase))
                provider = _providers[VideoEncoderIds.Libx264];
            else if (codec.Equals("libx265", StringComparison.OrdinalIgnoreCase))
                provider = _providers[VideoEncoderIds.Libx265];
            else if (codec.Equals("libsvtav1", StringComparison.OrdinalIgnoreCase))
                provider = _providers[VideoEncoderIds.SvtAv1];
            else
                provider = new LegacySoftwareEncoderProvider(codec, family);

            return new ResolvedVideoEncoder(
                provider,
                new VideoEncoderSelection(
                    provider.Capabilities.Id,
                    family,
                    codec));
        }

        internal static VideoCodecFamily InferCodecFamily(string codec)
        {
            if (codec.Contains("av1", StringComparison.OrdinalIgnoreCase))
                return VideoCodecFamily.Av1;
            if (codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                codec.Contains("265", StringComparison.OrdinalIgnoreCase))
            {
                return VideoCodecFamily.Hevc;
            }

            return VideoCodecFamily.H264;
        }
    }
}
