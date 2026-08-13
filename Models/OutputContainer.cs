namespace MediaFlux.Models
{
    public enum OutputContainerSelection
    {
        Auto = 0,
        Matroska = 1,
        Mp4 = 2
    }

    public enum OutputContainer
    {
        Matroska = 1,
        Mp4 = 2
    }

    public sealed class OutputContainerDecision
    {
        public OutputContainerSelection Requested { get; init; }
        public OutputContainer Resolved { get; init; }
        public string Reason { get; init; } = "";
        public IReadOnlyList<string> CompatibilityWarnings { get; init; } =
            Array.Empty<string>();
        public bool CopySubtitles { get; init; }
        public bool CopyDataStreams { get; init; }
        public bool CopyAttachments { get; init; }

        public string Extension => Resolved == OutputContainer.Matroska ? ".mkv" : ".mp4";
        public string MuxerName => Resolved == OutputContainer.Matroska ? "matroska" : "mp4";
        public bool RequiresConfirmation =>
            Requested == OutputContainerSelection.Mp4 && CompatibilityWarnings.Count > 0;
    }
}
