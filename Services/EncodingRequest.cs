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
        public VideoRestorationSettings Restoration { get; init; } = new();
        public string? EncoderPreset { get; init; }
        public int? QualityValue { get; init; }
        public bool TenBit { get; init; }
        public int? AudioChannels { get; init; }
        public Action<string>? ProgressCallback { get; init; }
        /// <summary>Structured progress while a frame-based AI intermediate is being prepared.</summary>
        public Action<AiIntermediateProgress>? AiProgressCallback { get; init; }
        public bool ConcurrentEncoderSessions { get; init; }
        public EncodingService.StreamMapMode MapMode { get; init; } =
            EncodingService.StreamMapMode.KeepAll;
        public bool CopySubtitles { get; init; } = true;
        public bool CopyDataStreams { get; init; } = true;
        public bool CopyAttachments { get; init; } = true;
        // MP4 preserves the historical default for callers and old persisted settings.
        public OutputContainerSelection OutputContainer { get; init; } =
            OutputContainerSelection.Mp4;
        public bool ContainerCompatibilityConfirmed { get; init; }
        public ContainerCompatibilityPolicy CompatibilityPolicy { get; init; } = ContainerCompatibilityPolicy.Intelligent;
        public Action<OutputContainerDecision>? ContainerDecisionCallback { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public Action<string>? OutputPathCallback { get; init; }
        public Action<string>? StagingPathCallback { get; init; }
        public Action<string>? FinalizationStatusCallback { get; init; }
        public TimeSpan? SampleStart { get; init; }
        public TimeSpan? SampleDuration { get; init; }
    }
}
