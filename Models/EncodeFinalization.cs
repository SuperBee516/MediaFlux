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

    public enum EncodeOutputValidationProfile
    {
        Production = 0,
        BenchmarkSample = 1,
        SampleComparison = 2
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
        /// <summary>Source-timeline interval intentionally represented by a validated partial-media output.</summary>
        public EncodeOutputTemporalWindow? ExpectedTemporalWindow { get; init; }
        public long? ExpectedVideoFrameCount { get; init; }
        public FrameCountProvenance ExpectedVideoFrameCountProvenance { get; init; } = FrameCountProvenance.Unavailable;
        /// <summary>
        /// A full, tolerant decode measurement used only after specific source-video
        /// corruption was proven and the bounded recovery encode completed.
        /// The original advertised source expectation remains available above.
        /// </summary>
        public RecoverableSourceBaseline? RecoverableSourceBaseline { get; init; }
        public EncodingSourceFailureClassification? SourceFailureClassification { get; init; }
        /// <summary>Bounded FFprobe timestamp evidence captured before the encode.</summary>
        public SourceTimingAnalysis? SourceTiming { get; init; }
        /// <summary>Requires bounded FFprobe evidence that the encoded output has a safe presentation timeline.</summary>
        public bool RequireMonotonicOutputTimeline { get; init; }
        /// <summary>Requires strict EOF video decoding before a reconstructed-timeline output can be promoted.</summary>
        public bool RequireFullVideoDecodeCoverage { get; init; }
        /// <summary>Requires strict EOF audio decoding for a degraded salvage output.</summary>
        public bool RequireFullAudioDecodeCoverage { get; init; }
        public int? ExpectedVideoWidth { get; init; }
        public int? ExpectedVideoHeight { get; init; }
        public PerformanceTimingService? PerformanceTiming { get; init; }
        public EncodeOutputValidationProfile Profile { get; init; } = EncodeOutputValidationProfile.Production;
    }

    public sealed record EncodeOutputTemporalWindow(double StartSeconds, double DurationSeconds)
    {
        public static EncodeOutputTemporalWindow FromSample(
            double startSeconds,
            double requestedDurationSeconds,
            double? sourceProgramDurationSeconds)
        {
            if (!double.IsFinite(startSeconds))
                throw new ArgumentOutOfRangeException(nameof(startSeconds));
            if (!double.IsFinite(requestedDurationSeconds) || requestedDurationSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(requestedDurationSeconds));

            double start = Math.Max(0, startSeconds);
            double end = start + requestedDurationSeconds;
            if (sourceProgramDurationSeconds is double sourceDuration && sourceDuration > 0 && double.IsFinite(sourceDuration))
                end = Math.Min(end, sourceDuration);
            return new EncodeOutputTemporalWindow(start, Math.Max(0, end - start));
        }

        public double GetOverlapDuration(double streamStartSeconds, double? streamDurationSeconds)
        {
            if (!double.IsFinite(StartSeconds) || StartSeconds < 0 ||
                !double.IsFinite(DurationSeconds) || DurationSeconds <= 0 ||
                !double.IsFinite(streamStartSeconds))
                return 0;

            double windowEnd = StartSeconds + DurationSeconds;
            double overlapStart = Math.Max(StartSeconds, streamStartSeconds);
            double overlapEnd = windowEnd;
            if (streamDurationSeconds is double streamDuration && streamDuration > 0 && double.IsFinite(streamDuration))
                overlapEnd = Math.Min(windowEnd, streamStartSeconds + streamDuration);
            return Math.Max(0, overlapEnd - overlapStart);
        }
    }

    /// <summary>Conservative evidence of the video frames and presentation tail that remained decodable from a proven-corrupt source.</summary>
    public sealed record RecoverableSourceBaseline(
        long DecodedVideoFrameCount,
        double TailPresentationSeconds,
        string Evidence);

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
        public EncodeOutputValidationFailureEvidence? FailureEvidence { get; init; }
    }

    public sealed class EncodeOutputValidationFailureEvidence
    {
        public required MediaProbeResult SourceProbe { get; init; }
        public required MediaProbeResult OutputProbe { get; init; }
        public long ExpectedFrameCount { get; init; }
        public long ActualFrameCount { get; init; }
        public long FrameDelta { get; init; }
        public double FrameRate { get; init; }
        public double DeficitSeconds { get; init; }
        public double AllowedSeconds { get; init; }
        public double SourceDurationSeconds { get; init; }
        public double OutputDurationSeconds { get; init; }
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
        public EncodeOutputValidationResult? StagedValidationResult { get; init; }
        public EncodeOutputValidationResult? PromotedValidationResult { get; init; }
    }
}
