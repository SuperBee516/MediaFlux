namespace MediaFlux.Models
{
    public enum EncodingInputKind
    {
        File,
        DvdPhysicalConcat
    }

    /// <summary>
    /// Describes the physical FFmpeg input separately from the logical source shown
    /// to the user. Normal files use the same path for both values; DVD titles use
    /// FFmpeg's physical concat protocol while retaining the VIDEO_TS folder as the
    /// logical source.
    /// </summary>
    public sealed class EncodingInputSource
    {
        public EncodingInputKind Kind { get; init; } = EncodingInputKind.File;
        public string InputPath { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public IReadOnlyList<string> SourceFiles { get; init; } = Array.Empty<string>();
        public string OutputBaseName { get; init; } = "";
        public double? KnownDurationSeconds { get; init; }
        public double? KnownAudioBitrateKbps { get; init; }
        public int KnownAudioStreamCount { get; init; }
        public IReadOnlyList<int> VideoStreamIndexes { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> AudioStreamIndexes { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> SubtitleStreamIndexes { get; init; } = Array.Empty<int>();
        public bool AllowSourceDeletion { get; init; } = true;

        public bool HasExplicitStreamSelection =>
            VideoStreamIndexes.Count > 0 ||
            AudioStreamIndexes.Count > 0 ||
            SubtitleStreamIndexes.Count > 0;

        public bool ShouldDeleteSource(bool deleteRequested) =>
            deleteRequested && AllowSourceDeletion;

        public static EncodingInputSource FromFile(
            string path,
            double? knownAudioBitrateKbps = null,
            int knownAudioStreamCount = 0) => new()
        {
            Kind = EncodingInputKind.File,
            InputPath = path,
            SourcePath = path,
            OutputBaseName = string.IsNullOrWhiteSpace(path)
                ? ""
                : Path.GetFileNameWithoutExtension(path),
            KnownAudioBitrateKbps = knownAudioBitrateKbps,
            KnownAudioStreamCount = Math.Max(0, knownAudioStreamCount),
            AllowSourceDeletion = true
        };
    }
}
