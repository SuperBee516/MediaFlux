namespace MediaFlux.Models
{
    public sealed class DvdSegmentInfo
    {
        public string Path { get; init; } = "";
        public int SegmentNumber { get; init; }
        public long SizeBytes { get; internal set; }
        public bool IsReadable { get; internal set; }
        public string ReadError { get; internal set; } = "";
        public MediaProbeResult? ProbeResult { get; internal set; }
    }

    public sealed class DvdTitleCandidate
    {
        public string TitleSetId { get; init; } = "";
        public IReadOnlyList<DvdSegmentInfo> Segments { get; init; } =
            Array.Empty<DvdSegmentInfo>();
        public IReadOnlyList<int> MissingSegmentNumbers { get; internal set; } =
            Array.Empty<int>();
        public List<string> Warnings { get; } = new();

        public bool StartsAtSegmentOne { get; internal set; }
        public bool HasConsistentStreams { get; internal set; }
        public bool IsValidForConversion { get; internal set; }
        public bool IsLikelyMainFeature { get; internal set; }
        public long CombinedSizeBytes { get; internal set; }
        public double CombinedDurationSeconds { get; internal set; }
        public string VideoCodec { get; internal set; } = "";
        public int? VideoWidth { get; internal set; }
        public int? VideoHeight { get; internal set; }
        public string DisplayAspectRatio { get; internal set; } = "";
        public double? FrameRate { get; internal set; }
        public string FieldOrder { get; internal set; } = "";
        public int AudioStreamCount { get; internal set; }
        public int SubtitleStreamCount { get; internal set; }
        public int ChapterCount { get; internal set; }
        public IReadOnlyList<string> Languages { get; internal set; } =
            Array.Empty<string>();
        public string RecommendationReason { get; internal set; } = "";
    }

    public sealed class DvdFolderAnalysisResult
    {
        public string SelectedFolderPath { get; init; } = "";
        public string VideoTsFolderPath { get; init; } = "";
        public bool ResemblesDvdVideo { get; internal set; }
        public string ErrorMessage { get; internal set; } = "";
        public List<string> Warnings { get; } = new();
        public IReadOnlyList<DvdTitleCandidate> Candidates { get; internal set; } =
            Array.Empty<DvdTitleCandidate>();
        public DvdTitleCandidate? RecommendedCandidate { get; internal set; }
        public bool HasAmbiguousMainFeature { get; internal set; }
        public string AmbiguityWarning { get; internal set; } = "";
    }

    public sealed class DvdAnalysisProgress
    {
        public string Status { get; init; } = "";
        public string TitleSetId { get; init; } = "";
        public int CompletedSegments { get; init; }
        public int TotalSegments { get; init; }
    }
}
