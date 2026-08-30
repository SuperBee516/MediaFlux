using MediaFlux.Models;

namespace MediaFlux.Services
{
    internal sealed class FfmpegCommandRequest
    {
        public required EncodingInputSource Input { get; init; }
        public required string OutputPath { get; init; }
        public required VideoEncoderSelection Encoder { get; init; }
        public required bool UseGpu { get; init; }
        public required double? TargetMb { get; init; }
        public required EncodingService.ScaleMode ScaleMode { get; init; }
        public required string? EncoderPreset { get; init; }
        public required int? QualityValue { get; init; }
        public required bool TenBit { get; init; }
        public required int? AudioChannels { get; init; }
        public required bool ConcurrentEncoderSessions { get; init; }
        public required EncodingService.StreamMapMode MapMode { get; init; }
        public required bool CopySubtitles { get; init; }
        public required bool CopyDataStreams { get; init; }
        public bool CopyAttachments { get; init; }
        public OutputContainerDecision ContainerDecision { get; init; } = new()
        {
            Requested = OutputContainerSelection.Mp4,
            Resolved = OutputContainer.Mp4,
            Reason = "Legacy MP4 output."
        };
        public required bool ForceMp4CompatibleAudio { get; init; }
        public required TimeSpan KnownDuration { get; init; }
        public bool NvencHighBitDepthOutputSupported { get; init; }
        public bool NvencCudaFormatConversionSupported { get; init; }
        public TimeSpan? SampleStart { get; init; }
        public TimeSpan? SampleDuration { get; init; }
    }
}
