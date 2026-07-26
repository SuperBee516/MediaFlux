using MediaFlux.Models;

namespace MediaFlux.Services
{
    /// <summary>
    /// Describes one video encode independently from UI display strings and
    /// encoder-specific positional parameters.
    /// </summary>
    public sealed class EncodingRequest
    {
        public required EncodingInputSource Input { get; init; }
        public string OutputFolder { get; init; } = "";
        public string Suffix { get; init; } = "";
        public required VideoEncoderSelection Encoder { get; init; }
        public bool UseGpu { get; init; }
        public double? TargetMb { get; init; }
        public EncodingService.ScaleMode ScaleMode { get; init; } =
            EncodingService.ScaleMode.None;
        public string? EncoderPreset { get; init; }
        public int? QualityValue { get; init; }
        public bool TenBit { get; init; }
        public int? AudioChannels { get; init; }
        public Action<string>? ProgressCallback { get; init; }
        public bool ConcurrentEncoderSessions { get; init; }
        public EncodingService.StreamMapMode MapMode { get; init; } =
            EncodingService.StreamMapMode.KeepAll;
        public bool CopySubtitles { get; init; } = true;
        public CancellationToken CancellationToken { get; init; }
        public Action<string>? OutputPathCallback { get; init; }
    }
}
