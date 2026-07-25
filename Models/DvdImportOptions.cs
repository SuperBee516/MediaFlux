namespace MediaFlux.Models
{
    public enum DvdOutputMode
    {
        LosslessRemuxToMkv,
        EncodeUsingCurrentSettings
    }

    public sealed class DvdImportOptions
    {
        public DvdTitleCandidate Candidate { get; init; } = null!;
        public DvdOutputMode OutputMode { get; init; } = DvdOutputMode.LosslessRemuxToMkv;
        public string OutputPath { get; init; } = "";
        public IReadOnlyList<int> SelectedAudioStreamIndexes { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> SelectedSubtitleStreamIndexes { get; init; } = Array.Empty<int>();
        public bool OverwriteExistingOutput { get; init; }
    }

    public sealed class DvdOperationProgress
    {
        public string Status { get; init; } = "";
        public double? Percent { get; init; }
        public TimeSpan? CurrentTime { get; init; }
        public TimeSpan? TotalDuration { get; init; }
    }

    public sealed class DvdRemuxResult
    {
        public bool Success { get; init; }
        public bool WasCanceled { get; init; }
        public string OutputPath { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
        public int? FailedSourceStreamIndex { get; init; }
        public string FailedStreamDescription { get; init; } = "";
        public string DiagnosticCommand { get; init; } = "";
        public string DiagnosticOutput { get; init; } = "";
        public bool CleanupSucceeded { get; internal set; } = true;
        public string CleanupMessage { get; internal set; } = "";
    }

    public sealed class DvdOutputValidationResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
        public MediaProbeResult? ProbeResult { get; init; }
    }
}
