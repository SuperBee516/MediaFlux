using MediaFlux.Services;

namespace MediaFlux.Models
{
    public enum EncodeFinalizationFailureKind
    {
        None = 0,
        Validation = 1,
        Promotion = 2,
        FinalVerification = 3
    }

    public sealed class EncodeOutputValidationRequest
    {
        public required EncodingInputSource Input { get; init; }
        public string OutputPath { get; init; } = "";
        public string FinalOutputPath { get; init; } = "";
        public required VideoEncoderSelection Encoder { get; init; }
        public EncodingService.ScaleMode ScaleMode { get; init; }
        public bool TenBit { get; init; }
        public int? AudioChannels { get; init; }
        public EncodingService.StreamMapMode MapMode { get; init; } =
            EncodingService.StreamMapMode.KeepAll;
        public bool CopySubtitles { get; init; }
        public bool CopyDataStreams { get; init; }
        public bool CopyAttachments { get; init; }
        public OutputContainerDecision ContainerDecision { get; init; } = new()
        {
            Requested = OutputContainerSelection.Mp4,
            Resolved = OutputContainer.Mp4,
            Reason = "Legacy MP4 output."
        };
        public MediaProbeResult? SourceProbe { get; init; }
        public double? ExpectedDurationSeconds { get; init; }
        public long? ExpectedVideoFrameCount { get; init; }
        public FrameCountProvenance ExpectedVideoFrameCountProvenance { get; init; } = FrameCountProvenance.Unavailable;
        public int? ExpectedVideoWidth { get; init; }
        public int? ExpectedVideoHeight { get; init; }
        public PerformanceTimingService? PerformanceTiming { get; init; }
    }

    public sealed class EncodeOutputValidationEvidence
    {
        public required MediaProbeResult SourceProbe { get; init; }
        public required MediaProbeResult OutputProbe { get; init; }
        public long OutputSizeBytes { get; init; }
        public long OutputLastWriteUtcTicks { get; init; }
        public IReadOnlyList<double> DecodePositionsSeconds { get; init; } =
            Array.Empty<double>();
    }

    public sealed class EncodeOutputValidationResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
        public string Summary { get; init; } = "";
        public EncodeOutputValidationEvidence? Evidence { get; init; }
    }

    public sealed class DecodeIntegritySpotCheckResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
        public IReadOnlyList<double> PositionsSeconds { get; init; } =
            Array.Empty<double>();
    }

    public sealed class EncodeFinalizationResult
    {
        public bool Success { get; init; }
        public EncodeFinalizationFailureKind FailureKind { get; init; }
        public string ErrorMessage { get; init; } = "";
        public string FinalOutputPath { get; init; } = "";
        public string StagingPath { get; init; } = "";
        public string RecoverableOutputPath { get; init; } = "";
        public string ValidationSummary { get; init; } = "";
        public long? FinalOutputSizeBytes { get; init; }
        public long? FinalOutputLastWriteUtcTicks { get; init; }
    }
}
