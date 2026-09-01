using MediaFlux.Services;

namespace MediaFlux.Models;

public enum AiConfigurationRelativeRank { InsufficientEvidence, BestTemporalStability, SimilarStability, MoreTemporalInstability, Discouraged }
public sealed record AiConfigurationComparisonItem(VideoRestorationSettings Settings, VideoRestorationMotionPreview Clip, TemporalQualityResult? TemporalQuality, AiConfigurationRelativeRank Rank, string Summary);
public sealed record AiConfigurationComparisonResult(TimeSpan Start, TimeSpan Duration, IReadOnlyList<AiConfigurationComparisonItem> Items);
public sealed record AiComparisonSessionEntry(string SourcePath, TimeSpan Start, TimeSpan Duration, AiConfigurationComparisonItem Item, bool UserSelected, bool Recommended, DateTimeOffset RecordedAt);
