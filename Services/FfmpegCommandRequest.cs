using MediaFlux.Models;

namespace MediaFlux.Services
{
    /// <summary>Maps replacement video separately from the original ancillary streams.</summary>
    internal sealed record SplitSourceInput(string VideoPath, EncodingInputSource AncillarySource);

    internal sealed class FfmpegCommandRequest
    {
        public required EncodingInputSource Input { get; init; }
        public required string OutputPath { get; init; }
        public required VideoEncoderSelection Encoder { get; init; }
        public required bool UseGpu { get; init; }
        public required double? TargetMb { get; init; }
        public required EncodingService.ScaleMode ScaleMode { get; init; }
        public VideoRestorationSettings Restoration { get; init; } = new();
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
        // CUDA frame output is an optional fast path.  It is deliberately kept
        // separate from NVENC availability because some FFmpeg builds expose
        // both features but cannot negotiate CUDA frames with a given format.
        public bool PreferNvencGpuResidentFrames { get; init; } = true;
        // Keeps NVENC active while deliberately removing NVDEC/CUDA input
        // acceleration for a single device-recovery retry.
        public bool DisableHardwareDecode { get; init; }
        public string SourcePixelFormat { get; init; } = "";
        public TimeSpan? SampleStart { get; init; }
        public TimeSpan? SampleDuration { get; init; }
        public SplitSourceInput? SplitSource { get; init; }
        public string? RestorationFilterOverride { get; init; }
    }
}
