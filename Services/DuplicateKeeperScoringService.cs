using MediaFlux.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MediaFlux.Services
{
    public sealed record DuplicateKeeperEvaluation(
        DuplicateItem? Keeper,
        bool RequiresReview,
        double Margin,
        IReadOnlyDictionary<string, double> Scores,
        string Explanation);

    public static class DuplicateKeeperScoringService
    {
        public static DuplicateGroup Apply(
            DuplicateGroup group,
            DuplicateKeeperPreferences? preferences,
            bool preserveManualSelection = true)
        {
            if (group.Items.Count == 0)
                return group;

            if (preserveManualSelection)
            {
                var selected = group.Items.FirstOrDefault(item =>
                    string.Equals(item.KeeperReason, "User selected in review", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(item.Recommendation, "Selected keeper", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Recommendation, "Protected keeper", StringComparison.OrdinalIgnoreCase)));
                if (selected != null)
                    return ApplyManualKeeper(group, selected.Path);
            }

            var effectivePreferences = string.Equals(group.ConfidenceLabel, "Exact", StringComparison.OrdinalIgnoreCase)
                ? new DuplicateKeeperPreferences()
                : preferences;
            var evaluation = Evaluate(group.Items, effectivePreferences);
            var ordered = group.Items
                .Select(item => ApplyRecommendation(item, evaluation))
                .OrderByDescending(item => evaluation.Keeper != null &&
                    string.Equals(item.Path, evaluation.Keeper.Path, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.IsReferenceProtected)
                .ThenByDescending(item => evaluation.Scores.TryGetValue(item.Path, out var score) ? score : double.MinValue)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return group with { Items = ordered };
        }

        public static DuplicateKeeperEvaluation Evaluate(
            IReadOnlyCollection<DuplicateItem> sourceItems,
            DuplicateKeeperPreferences? sourcePreferences)
        {
            if (sourceItems.Count == 0)
                return new DuplicateKeeperEvaluation(null, true, 0, new Dictionary<string, double>(), "No files are available to score.");

            var preferences = sourcePreferences?.Clone() ?? new DuplicateKeeperPreferences();
            preferences.Normalize();
            var items = sourceItems.ToList();
            var protectedItems = items.Where(item => item.IsReferenceProtected).ToList();
            var candidates = protectedItems.Count > 0 ? protectedItems : items;

            if (string.Equals(preferences.Profile, DuplicateKeeperPreferences.QualityFirst, StringComparison.Ordinal))
                return EvaluateLegacy(items, candidates);

            if (preferences.NeverSacrificeResolution)
            {
                long bestPixels = candidates.Max(GetPixels);
                if (bestPixels > 0)
                    candidates = candidates.Where(item => GetPixels(item) == bestPixels).ToList();
            }

            var weights = ResolveWeights(preferences);
            bool useResolution = weights.Resolution > 0 && candidates.All(item => GetPixels(item) > 0);
            bool useQuality = weights.Quality > 0 && candidates.All(item => item.BitrateKbps > 0);
            bool useStorage = weights.Storage > 0 && candidates.All(item => item.LengthBytes > 0);
            bool useCodec = weights.Codec > 0 &&
                            !string.Equals(preferences.CodecPreference, DuplicateKeeperPreferences.CodecNoPreference, StringComparison.Ordinal) &&
                            candidates.All(item => !string.IsNullOrWhiteSpace(item.VideoCodec));
            bool useDate = weights.ModifiedDate > 0 && candidates.All(item => item.Modified != default);

            long bestPixelsForScore = useResolution ? candidates.Max(GetPixels) : 0;
            int bestBitrate = useQuality ? candidates.Max(item => item.BitrateKbps) : 0;
            long smallestSize = useStorage ? candidates.Min(item => item.LengthBytes) : 0;
            DateTime newest = useDate ? candidates.Max(item => item.Modified) : default;

            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in candidates)
            {
                double weighted = 0;
                double usedWeight = 0;
                AddComponent(useResolution, weights.Resolution, RatioScore(GetPixels(item), bestPixelsForScore), ref weighted, ref usedWeight);
                AddComponent(useQuality, weights.Quality, RatioScore(item.BitrateKbps, bestBitrate), ref weighted, ref usedWeight);
                AddComponent(useStorage, weights.Storage, RatioScore(smallestSize, item.LengthBytes), ref weighted, ref usedWeight);
                AddComponent(useCodec, weights.Codec, GetCodecScore(item.VideoCodec, preferences.CodecPreference), ref weighted, ref usedWeight);
                AddComponent(useDate, weights.ModifiedDate, GetRecencyScore(item.Modified, newest), ref weighted, ref usedWeight);
                scores[item.Path] = usedWeight > 0 ? weighted / usedWeight : 50;
            }

            var ranked = candidates
                .OrderByDescending(item => scores[item.Path])
                .ThenByDescending(item => GetPixels(item))
                .ThenByDescending(item => item.BitrateKbps)
                .ThenByDescending(item => item.LengthBytes)
                .ThenByDescending(item => item.Modified)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keeper = ranked[0];
            double margin = ranked.Count > 1 ? scores[keeper.Path] - scores[ranked[1].Path] : 100;
            bool requiresReview = protectedItems.Count == 0 && ranked.Count > 1 && margin < preferences.MinimumScoreMargin;
            string explanation = requiresReview
                ? $"{preferences.Profile}: scores are too close ({scores[keeper.Path]:0.0} vs {scores[ranked[1].Path]:0.0}; margin {margin:0.0} is below {preferences.MinimumScoreMargin}). Select a keeper in review."
                : BuildWeightedExplanation(preferences, keeper, items, scores[keeper.Path], ranked.Count > 1 ? scores[ranked[1].Path] : (double?)null, margin);

            return new DuplicateKeeperEvaluation(
                requiresReview ? null : keeper,
                requiresReview,
                margin,
                scores,
                explanation);
        }

        private static DuplicateKeeperEvaluation EvaluateLegacy(
            IReadOnlyCollection<DuplicateItem> allItems,
            IReadOnlyCollection<DuplicateItem> candidates)
        {
            var keeper = candidates
                .OrderByDescending(item => GetPixels(item))
                .ThenByDescending(item => item.BitrateKbps)
                .ThenByDescending(item => item.LengthBytes)
                .ThenByDescending(item => item.Modified)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .First();
            var scores = allItems.ToDictionary(
                item => item.Path,
                item => string.Equals(item.Path, keeper.Path, StringComparison.OrdinalIgnoreCase) ? 100d : 0d,
                StringComparer.OrdinalIgnoreCase);
            return new DuplicateKeeperEvaluation(keeper, false, 100, scores, GetLegacyReason(keeper, allItems));
        }

        private static DuplicateItem ApplyRecommendation(DuplicateItem item, DuplicateKeeperEvaluation evaluation)
        {
            if (evaluation.RequiresReview)
            {
                return item with
                {
                    Recommendation = item.IsReferenceProtected ? "Protected reference" : "Review required",
                    KeeperReason = evaluation.Explanation
                };
            }

            if (evaluation.Keeper != null && string.Equals(item.Path, evaluation.Keeper.Path, StringComparison.OrdinalIgnoreCase))
            {
                return item with
                {
                    Recommendation = item.IsReferenceProtected ? "Protected keeper" : "Suggested keeper",
                    KeeperReason = evaluation.Explanation
                };
            }

            if (item.IsReferenceProtected)
            {
                return item with
                {
                    Recommendation = "Protected reference",
                    KeeperReason = "Reference folder protection"
                };
            }

            string scoreReason = evaluation.Scores.TryGetValue(item.Path, out var score) && evaluation.Keeper != null &&
                                 evaluation.Scores.TryGetValue(evaluation.Keeper.Path, out var keeperScore)
                ? $"Keeper score {score:0.0} vs selected {keeperScore:0.0}"
                : "Not selected by keeper preferences";
            return item with { Recommendation = "Trash candidate", KeeperReason = scoreReason };
        }

        public static DuplicateGroup ApplyManualKeeper(DuplicateGroup group, string keeperPath)
        {
            var items = group.Items.Select(item =>
            {
                if (string.Equals(item.Path, keeperPath, StringComparison.OrdinalIgnoreCase))
                {
                    return item with
                    {
                        Recommendation = item.IsReferenceProtected ? "Protected keeper" : "Selected keeper",
                        KeeperReason = "User selected in review"
                    };
                }

                return item.IsReferenceProtected
                    ? item with { Recommendation = "Protected reference", KeeperReason = "Reference folder protection" }
                    : item with { Recommendation = "Trash candidate", KeeperReason = "Not selected to keep" };
            })
                .OrderByDescending(item => string.Equals(item.Path, keeperPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.IsReferenceProtected)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return group with { Items = items };
        }

        private static string GetLegacyReason(DuplicateItem keeper, IReadOnlyCollection<DuplicateItem> items)
        {
            if (keeper.IsReferenceProtected)
                return "Quality first: protected reference folder";

            long pixels = GetPixels(keeper);
            long maxPixels = items.Max(GetPixels);
            if (pixels > 0 && pixels == maxPixels && items.Any(item => GetPixels(item) < maxPixels))
                return "Quality first: highest resolution";

            int maxBitrate = items.Max(item => item.BitrateKbps);
            if (keeper.BitrateKbps > 0 && keeper.BitrateKbps == maxBitrate && items.Any(item => item.BitrateKbps < maxBitrate))
                return "Quality first: highest reported bitrate";

            long maxSize = items.Max(item => item.LengthBytes);
            if (keeper.LengthBytes == maxSize && items.Any(item => item.LengthBytes < maxSize))
                return "Quality first: largest file";

            return "Quality first: best available quality; modified date used as the final tie-breaker";
        }

        private static string BuildWeightedExplanation(
            DuplicateKeeperPreferences preferences,
            DuplicateItem keeper,
            IReadOnlyCollection<DuplicateItem> items,
            double score,
            double? nextScore,
            double margin)
        {
            var details = new List<string>();
            long keeperPixels = GetPixels(keeper);
            if (keeperPixels > 0 && items.All(item => GetPixels(item) == keeperPixels))
                details.Add("same resolution");
            else if (keeperPixels == items.Max(GetPixels))
                details.Add("highest resolution");

            long largest = items.Max(item => item.LengthBytes);
            if (largest > 0 && keeper.LengthBytes < largest)
            {
                int saved = (int)Math.Round((largest - keeper.LengthBytes) * 100d / largest);
                if (saved > 0)
                    details.Add($"{saved}% smaller than the largest copy");
            }

            if (!string.Equals(preferences.CodecPreference, DuplicateKeeperPreferences.CodecNoPreference, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(keeper.VideoCodec))
            {
                details.Add($"{keeper.VideoCodec.ToUpperInvariant()} codec preference considered");
            }

            string comparison = nextScore.HasValue
                ? $"{score:0.0} points vs {nextScore.Value:0.0} (margin {margin:0.0})"
                : $"{score:0.0} points";
            string suffix = details.Count > 0 ? $"; {string.Join("; ", details)}" : string.Empty;
            return $"{preferences.Profile}: {comparison}{suffix}";
        }

        private static (int Resolution, int Quality, int Storage, int Codec, int ModifiedDate) ResolveWeights(DuplicateKeeperPreferences preferences)
        {
            return preferences.Profile switch
            {
                DuplicateKeeperPreferences.Balanced => (40, 15, 30, 15, 0),
                DuplicateKeeperPreferences.SaveStorage => (30, 10, 45, 15, 0),
                DuplicateKeeperPreferences.PreferModernCodecs => (35, 15, 20, 30, 0),
                _ => (preferences.ResolutionWeight, preferences.QualityWeight, preferences.StorageWeight,
                    preferences.CodecWeight, preferences.ModifiedDateWeight)
            };
        }

        private static void AddComponent(bool enabled, int weight, double score, ref double weighted, ref double usedWeight)
        {
            if (!enabled || weight <= 0)
                return;
            weighted += score * weight;
            usedWeight += weight;
        }

        private static double RatioScore(long value, long best)
        {
            return value > 0 && best > 0 ? Math.Clamp(value * 100d / best, 0, 100) : 0;
        }

        private static double GetRecencyScore(DateTime value, DateTime newest)
        {
            double days = Math.Max(0, (newest - value).TotalDays);
            return 100 * Math.Exp(-days / 365d);
        }

        private static double GetCodecScore(string codec, string preference)
        {
            string value = codec.Trim().ToLowerInvariant();
            bool av1 = value.Contains("av1");
            bool hevc = value.Contains("hevc") || value.Contains("h265") || value.Contains("x265");
            bool h264 = value.Contains("h264") || value.Contains("avc") || value.Contains("x264");
            bool vp9 = value.Contains("vp9");

            if (string.Equals(preference, DuplicateKeeperPreferences.CodecH264First, StringComparison.Ordinal))
            {
                if (h264) return 100;
                if (hevc) return 80;
                if (av1 || vp9) return 65;
                return 50;
            }

            if (av1) return 100;
            if (hevc) return 88;
            if (vp9) return 82;
            if (h264) return 65;
            return 45;
        }

        private static long GetPixels(DuplicateItem item) => (long)item.Width * item.Height;
    }
}
