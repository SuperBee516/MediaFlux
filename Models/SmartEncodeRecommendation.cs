namespace MediaFlux.Models
{
    public enum SmartEncodeRecommendationKind
    {
        Unavailable,
        StrongCandidate,
        ModerateCandidate,
        Skip,
        Review,
        RemuxOnly
    }

    public enum SmartEncodeConfidence
    {
        Low,
        Medium,
        High
    }

    public sealed class SmartEncodeRecommendation
    {
        public SmartEncodeRecommendationKind Kind { get; init; }
        public SmartEncodeConfidence Confidence { get; init; }
        public double? EstimatedSavingsPercent { get; init; }
        public double? EstimatedSavingsMb { get; init; }
        public string PrimaryReason { get; init; } = "";
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

        public string DisplayName => Kind switch
        {
            SmartEncodeRecommendationKind.StrongCandidate => "Strong candidate",
            SmartEncodeRecommendationKind.ModerateCandidate => "Moderate candidate",
            SmartEncodeRecommendationKind.Skip => "Skip",
            SmartEncodeRecommendationKind.Review => "Review",
            SmartEncodeRecommendationKind.RemuxOnly => "Remux only",
            _ => "Unavailable"
        };

        public bool IsCandidate =>
            Kind is SmartEncodeRecommendationKind.StrongCandidate or
                SmartEncodeRecommendationKind.ModerateCandidate;

        public string BuildTooltip()
        {
            return QueueAnalysisPresentation.Create(this).BuildTooltip();
        }
    }

    public sealed class SmartEncodeSourceInfo
    {
        public string Path { get; init; } = "";
        public double SourceMb { get; init; }
        public double DurationSeconds { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double FramesPerSecond { get; init; }
        public int VideoBitrateKbps { get; init; }
        public int TotalBitrateKbps { get; init; }
        public int AudioBitrateKbps { get; init; }
        public int VideoStreamCount { get; init; }
        public int AudioStreamCount { get; init; }
        public int SubtitleStreamCount { get; init; }
        public string VideoCodec { get; init; } = "";
        public string FormatName { get; init; } = "";
        public string FieldOrder { get; init; } = "";
        public bool IsLikelyAnimation { get; init; }
    }

    public sealed class SmartEncodeIntent
    {
        public string TargetCodec { get; init; } = "";
        public int? TargetHeight { get; init; }
        public double EstimatedOutputMb { get; init; }
        public double MinimumSavingsPercent { get; init; } = 15;
    }
}
