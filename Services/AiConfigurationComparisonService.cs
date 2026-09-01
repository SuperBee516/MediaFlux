using System.Collections.Concurrent;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Sequential, bounded comparison of existing AI motion previews. Results are session-only.</summary>
public sealed class AiConfigurationComparisonService
{
    private const int MaximumHistory = 30;
    private static readonly ConcurrentDictionary<string, TemporalQualityResult> History = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> HistoryOrder = new();
    private static readonly ConcurrentQueue<AiComparisonSessionEntry> SessionEntries = new();
    private readonly VideoRestorationPreviewService _previews;
    public AiConfigurationComparisonService(VideoRestorationPreviewService previews) => _previews = previews;

    public async Task<AiConfigurationComparisonResult> CompareAsync(string source, TimeSpan duration, TimeSpan position, EncodingService.ScaleMode scale, IReadOnlyList<VideoRestorationSettings> candidates, IProgress<string>? progress = null, CancellationToken token = default)
    {
        VideoRestorationSettings[] bounded = candidates.Where(s => s.AiMode != AiRestorationMode.Off).Select(s => s.Clone()).DistinctBy(Key).Take(3).ToArray();
        if (bounded.Length < 2) throw new ArgumentException("Choose at least two distinct AI configurations to compare.", nameof(candidates));
        var results = new List<(VideoRestorationSettings Settings, VideoRestorationMotionPreview Clip, TemporalQualityResult? Temporal)>();
        for (int index = 0; index < bounded.Length; index++)
        {
            token.ThrowIfCancellationRequested(); progress?.Report($"Comparing {index + 1} of {bounded.Length}...");
            VideoRestorationMotionPreview clip = await _previews.GenerateMotionAsync(new VideoRestorationPreviewRequest(source, duration, position, bounded[index], scale), TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            TemporalQualityResult? temporal = clip.TemporalQuality; if (temporal != null) Remember(HistoryKey(source, clip, bounded[index]), temporal);
            results.Add((bounded[index], clip, temporal));
        }
        AiConfigurationComparisonResult comparison = new(results[0].Clip.Start, results[0].Clip.Duration, Rank(results));
        foreach (AiConfigurationComparisonItem item in comparison.Items) { SessionEntries.Enqueue(new AiComparisonSessionEntry(source, comparison.Start, comparison.Duration, item, false, item.Rank == AiConfigurationRelativeRank.BestTemporalStability, DateTimeOffset.UtcNow)); while (SessionEntries.Count > MaximumHistory) SessionEntries.TryDequeue(out _); }
        return comparison;
    }

    public static IReadOnlyCollection<TemporalQualityResult> SessionHistory => History.Values.ToArray();
    public static IReadOnlyList<AiComparisonSessionEntry> SessionReport => SessionEntries.ToArray();
    public static IReadOnlyList<VideoRestorationSettings> ValidateCuratedCandidates(IEnumerable<VideoRestorationSettings> settings, IReadOnlyList<AiRestorationModel> models)
    {
        return settings.Where(s => s.AiMode != AiRestorationMode.Off).Where(s => models.Any(model => model.Id.Equals(s.AiModelId, StringComparison.OrdinalIgnoreCase) && model.Category == s.AiMode && model.SupportedScales.Contains(s.AiScale))).Select(s => s.Clone()).DistinctBy(Key).Take(3).ToArray();
    }
    private static IReadOnlyList<AiConfigurationComparisonItem> Rank(IReadOnlyList<(VideoRestorationSettings Settings, VideoRestorationMotionPreview Clip, TemporalQualityResult? Temporal)> values)
    {
        double[] scores = values.Select(x => Score(x.Temporal)).ToArray(); double best = scores.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min();
        return values.Select((value, index) =>
        {
            AiConfigurationRelativeRank rank = !double.IsFinite(scores[index]) ? AiConfigurationRelativeRank.InsufficientEvidence : value.Temporal?.Classification == TemporalStability.SevereInstability ? AiConfigurationRelativeRank.Discouraged : scores[index] <= best * 1.15 ? (scores.Count(score => double.IsFinite(score) && score <= best * 1.15) == 1 ? AiConfigurationRelativeRank.BestTemporalStability : AiConfigurationRelativeRank.SimilarStability) : AiConfigurationRelativeRank.MoreTemporalInstability;
            string summary = $"{value.Settings.AiModelId} · {(int)value.Settings.AiScale}x · {value.Temporal?.Summary ?? "Temporal Stability: Unknown"} · {rank}";
            return new AiConfigurationComparisonItem(value.Settings, value.Clip, value.Temporal, rank, summary);
        }).ToArray();
    }
    private static double Score(TemporalQualityResult? result) => result is null || result.Classification == TemporalStability.Unknown ? double.NaN : (result.RestoredMotion / Math.Max(.002, result.OriginalMotion)) + (result.RestoredEdgeVariation / Math.Max(.002, result.OriginalEdgeVariation));
    private static string Key(VideoRestorationSettings s) => $"{s.AiMode}|{s.AiModelId}|{s.AiScale}|{s.AiDevice}|{VideoRestorationPipeline.BuildPlan(s, EncodingService.ScaleMode.None).DescribeStages()}";
    private static string HistoryKey(string source, VideoRestorationMotionPreview clip, VideoRestorationSettings settings) => $"{Path.GetFullPath(source)}|{clip.Start.Ticks}|{clip.Duration.Ticks}|{Key(settings)}";
    private static void Remember(string key, TemporalQualityResult result) { if (History.TryAdd(key, result)) { HistoryOrder.Enqueue(key); while (HistoryOrder.Count > MaximumHistory && HistoryOrder.TryDequeue(out string? old)) History.TryRemove(old, out _); } }
}
