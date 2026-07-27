namespace MediaFlux.Models
{
    public sealed class MediaRemuxRequest
    {
        public string SourcePath { get; init; } = "";
        public string OutputPath { get; init; } = "";
        public bool OverwriteExistingOutput { get; init; }
    }

    public sealed class MediaRemuxProgress
    {
        public string Status { get; init; } = "";
        public double? Percent { get; init; }
        public TimeSpan? CurrentTime { get; init; }
        public TimeSpan? TotalDuration { get; init; }
    }

    public sealed class MediaRemuxResult
    {
        public bool Success { get; init; }
        public bool WasCanceled { get; init; }
        public string OutputPath { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
        public string DiagnosticCommand { get; init; } = "";
        public string DiagnosticOutput { get; init; } = "";
        public bool CleanupSucceeded { get; internal set; } = true;
        public string CleanupMessage { get; internal set; } = "";
    }
}
