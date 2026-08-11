using MediaFlux.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MediaFlux.Services
{
    public enum DuplicateKeeperOutcome
    {
        PreferHigherQualityCopy,
        PreferSmallerMoreEfficientCopy,
        ManualReviewRequired
    }

    public sealed record DuplicateKeeperEvaluation(
        DuplicateItem? Keeper,
        bool RequiresReview,
        double Margin,
        IReadOnlyDictionary<string, double> Scores,
        string Explanation,
        DuplicateKeeperOutcome Outcome = DuplicateKeeperOutcome.PreferHigherQualityCopy);

    public enum DuplicateKeeperScoringContext
    {
        Standard,
        Visual
    }

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

            bool exact = string.Equals(group.ConfidenceLabel, "Exact", StringComparison.OrdinalIgnoreCase);
            var effectivePreferences = exact
                ? new DuplicateKeeperPreferences()
                : preferences;
            var evaluation = Evaluate(group.Items, effectivePreferences,
                exact ? DuplicateKeeperScoringContext.Standard : DuplicateKeeperScoringContext.Visual,
                group.ConfidenceScore);
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
            DuplicateKeeperPreferences? sourcePreferences,
            DuplicateKeeperScoringContext context = DuplicateKeeperScoringContext.Standard,
            double visualConfidence = 100)
        {
            if (sourceItems.Count == 0)
                return new DuplicateKeeperEvaluation(null, true, 0, new Dictionary<string, double>(), "No files are available to score.",
                    DuplicateKeeperOutcome.ManualReviewRequired);

            var preferences = sourcePreferences?.Clone() ?? new DuplicateKeeperPreferences();
            preferences.Normalize();
            var items = sourceItems.ToList();
            var protectedItems = items.Where(item => item.IsReferenceProtected).ToList();
            var candidates = protectedItems.Count > 0 ? protectedItems : items;

            if (context == DuplicateKeeperScoringContext.Visual)
                return EvaluateVisualQualityAware(items, candidates, protectedItems.Count > 0, preferences, visualConfidence);

            if (string.Equals(preferences.Profile, DuplicateKeeperPreferences.QualityFirst, StringComparison.Ordinal))
            {
                return EvaluateLegacy(items, candidates);
            }

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
                explanation,
                requiresReview ? DuplicateKeeperOutcome.ManualReviewRequired : DuplicateKeeperOutcome.PreferHigherQualityCopy);
        }

        // Automation deliberately uses a calibrated weighted score even when the shared
        // Quality First profile is selected. This leaves the legacy/manual review path
        // untouched while preventing its deterministic 100/0 tie-breaker from looking
        // like a meaningful automation margin.
        public static DuplicateKeeperEvaluation EvaluateAutomation(
            IReadOnlyCollection<DuplicateItem> sourceItems,
            DuplicateKeeperPreferences? sourcePreferences,
            DuplicateKeeperScoringContext context = DuplicateKeeperScoringContext.Visual,
            double visualConfidence = 100)
        {
            var preferences = sourcePreferences?.Clone() ?? new DuplicateKeeperPreferences();
            preferences.Normalize();
            if (string.Equals(preferences.Profile, DuplicateKeeperPreferences.QualityFirst, StringComparison.Ordinal))
            {
                preferences.Profile = DuplicateKeeperPreferences.Custom;
                preferences.ResolutionWeight = 50;
                preferences.QualityWeight = 20;
                preferences.StorageWeight = 10;
                preferences.CodecWeight = 20;
                preferences.ModifiedDateWeight = 0;
            }
            return Evaluate(sourceItems, preferences, context, visualConfidence);
        }

        private static DuplicateKeeperEvaluation EvaluateVisualQualityAware(
            IReadOnlyCollection<DuplicateItem> allItems,
            IReadOnlyCollection<DuplicateItem> sourceCandidates,
            bool hasProtectedCandidates,
            DuplicateKeeperPreferences preferences,
            double visualConfidence)
        {
            var candidates = sourceCandidates.ToList();
            var emptyScores = allItems.ToDictionary(x => x.Path, _ => 0d, StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 1 && hasProtectedCandidates)
            {
                emptyScores[candidates[0].Path] = 100;
                return new DuplicateKeeperEvaluation(candidates[0], false, 100, emptyScores,
                    "Prefer higher-quality copy: the protected file must remain the keeper.");
            }

            if (candidates.Any(item => item.Width <= 0 || item.Height <= 0 || item.BitrateKbps <= 0 ||
                                       item.LengthBytes <= 0 || item.FrameRate <= 0 || string.IsNullOrWhiteSpace(item.VideoCodec)))
            {
                return new DuplicateKeeperEvaluation(null, true, 0, emptyScores,
                    "Manual review required: codec, resolution, frame rate, bitrate, or file-size metadata is incomplete.",
                    DuplicateKeeperOutcome.ManualReviewRequired);
            }

            if (visualConfidence + 0.001 < preferences.VisualConfidenceFloor)
            {
                return new DuplicateKeeperEvaluation(null, true, 0, emptyScores,
                    $"Manual review required: visual confidence {visualConfidence:0.0}% is below the {preferences.VisualConfidenceFloor:0.0}% floor.",
                    DuplicateKeeperOutcome.ManualReviewRequired);
            }

            long largestPixels = candidates.Max(GetPixels);
            long smallestPixels = candidates.Min(GetPixels);
            var quality = candidates.ToDictionary(item => item.Path, VisualDuplicateQualityModel.Assess,
                StringComparer.OrdinalIgnoreCase);
            if (quality.Values.Any(value => value.SufficiencyScore + 0.001 < preferences.VisualQualityFloor))
            {
                VisualQualityAssessment weakest = quality.Values.OrderBy(x => x.SufficiencyScore).First();
                return new DuplicateKeeperEvaluation(null, true, 0, emptyScores,
                    $"Manual review required: a candidate is {VisualDuplicateQualityModel.FormatBand(weakest.Band)} " +
                    $"({weakest.SufficiencyScore:0.0}/100), below the quality floor of {preferences.VisualQualityFloor}.",
                    DuplicateKeeperOutcome.ManualReviewRequired);
            }

            (double resolution, double qualityWeight, double storage, double codec, double confidence) =
                ResolveVisualWeights(preferences);
            bool bothGood = quality.Values.All(x => x.SufficiencyScore >= 65);
            if (bothGood)
                storage *= 1.25;
            double totalWeight = resolution + qualityWeight + storage + codec + confidence;
            long smallestSize = candidates.Min(x => x.LengthBytes);
            bool differentResolutions = smallestPixels != largestPixels;
            var resolutionValue = new Dictionary<string, VisualResolutionValueAssessment>(StringComparer.OrdinalIgnoreCase);
            var resolutionValueScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (differentResolutions)
            {
                DuplicateItem lowerResolutionReference = candidates
                    .OrderBy(GetPixels)
                    .ThenBy(x => x.LengthBytes)
                    .First();
                double storageCostSensitivity = ResolveResolutionStorageCostSensitivity(preferences);
                foreach (DuplicateItem item in candidates)
                {
                    resolutionValue[item.Path] = VisualDuplicateQualityModel.AssessResolutionValue(
                        item, lowerResolutionReference, quality[item.Path], quality[lowerResolutionReference.Path],
                        visualConfidence, preferences.VisualConfidenceFloor, storageCostSensitivity);
                }

                double lowestUtility = resolutionValue.Values.Min(x => x.Utility);
                double highestUtility = resolutionValue.Values.Max(x => x.Utility);
                double midpoint = (lowestUtility + highestUtility) / 2;
                foreach (DuplicateItem item in candidates)
                {
                    // Centering makes the tradeoff fair to both ends of the comparison;
                    // tanh bounds extreme storage or resolution ratios without hard cutoffs.
                    resolutionValueScores[item.Path] = 50 + 50 * Math.Tanh(
                        (resolutionValue[item.Path].Utility - midpoint) / 0.55);
                }
            }
            var scores = allItems.ToDictionary(x => x.Path, _ => 0d, StringComparer.OrdinalIgnoreCase);
            foreach (DuplicateItem item in candidates)
            {
                double resolutionScore = 100 * Math.Sqrt(GetPixels(item) / (double)largestPixels);
                double storageScore = differentResolutions
                    ? resolutionValueScores[item.Path]
                    : 100 * smallestSize / item.LengthBytes;
                double codecScore = string.Equals(preferences.CodecPreference, DuplicateKeeperPreferences.CodecNoPreference, StringComparison.Ordinal)
                    ? 50
                    : string.Equals(preferences.CodecPreference, DuplicateKeeperPreferences.CodecH264First, StringComparison.Ordinal)
                        ? GetCodecScore(item.VideoCodec, preferences.CodecPreference)
                        : VisualDuplicateQualityModel.GetCodecPreferenceScore(item.VideoCodec);
                double qualityScore = quality[item.Path].SufficiencyScore;
                double riskPenalty = qualityScore < 65 ? (65 - qualityScore) * 0.35 : 0;
                if (differentResolutions)
                    riskPenalty += resolutionValue[item.Path].UpscaleRisk * 8;
                scores[item.Path] = Math.Clamp((resolutionScore * resolution + qualityScore * qualityWeight +
                    storageScore * storage + codecScore * codec + visualConfidence * confidence) / totalWeight - riskPenalty, 0, 100);
            }

            List<DuplicateItem> ranked = candidates.OrderByDescending(x => scores[x.Path])
                .ThenByDescending(x => quality[x.Path].SufficiencyScore)
                .ThenBy(x => x.LengthBytes)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            DuplicateItem winner = ranked[0];
            double margin = ranked.Count > 1 ? scores[winner.Path] - scores[ranked[1].Path] : 100;
            if (!hasProtectedCandidates && ranked.Count > 1 && margin + 0.001 < preferences.MinimumScoreMargin)
            {
                return new DuplicateKeeperEvaluation(null, true, margin, scores,
                    $"Manual review required: quality and storage signals are too close or conflicting " +
                    $"({scores[winner.Path]:0.0} vs {scores[ranked[1].Path]:0.0}; margin {margin:0.0}, required {preferences.MinimumScoreMargin}).",
                    DuplicateKeeperOutcome.ManualReviewRequired);
            }

            VisualQualityAssessment winnerQuality = quality[winner.Path];
            long largestSize = candidates.Max(x => x.LengthBytes);
            double saved = largestSize > 0 ? (largestSize - winner.LengthBytes) * 100d / largestSize : 0;
            bool efficientWinner = winner.LengthBytes == smallestSize && saved >= 5;
            string outcome = efficientWinner ? "Prefer smaller/more-efficient copy" : "Prefer higher-quality copy";
            string storageText = saved > 0.05 ? $"; saves {saved:0.#}% storage" : "; no material storage saving";
            string resolutionValueText = string.Empty;
            if (differentResolutions)
            {
                VisualResolutionValueAssessment value = resolutionValue[winner.Path];
                resolutionValueText = $"; resolution value {value.PixelRatio:0.##}x pixels at {value.SizeRatio:0.##}x storage";
                if (value.UpscaleRisk >= 0.15)
                    resolutionValueText += $"; metadata-only upscale risk {value.UpscaleRisk * 100:0.#}%";
            }
            string explanation = $"{outcome}: {preferences.VisualKeeperStrategy}; {winner.VideoCodec.ToUpperInvariant()} " +
                $"{winner.Width}x{winner.Height} at {winner.FrameRate:0.##} fps and {winner.BitrateKbps / 1000d:0.##} Mbps is " +
                $"estimated {VisualDuplicateQualityModel.FormatBand(winnerQuality.Band)} ({winnerQuality.SufficiencyScore:0.0}/100){storageText}{resolutionValueText}; " +
                $"visual confidence contributes {visualConfidence:0.0}%; final score {scores[winner.Path]:0.0}, margin {margin:0.0}.";
            return new DuplicateKeeperEvaluation(winner, false, margin, scores, explanation,
                efficientWinner ? DuplicateKeeperOutcome.PreferSmallerMoreEfficientCopy : DuplicateKeeperOutcome.PreferHigherQualityCopy);
        }

        private static (double Resolution, double Quality, double Storage, double Codec, double Confidence)
            ResolveVisualWeights(DuplicateKeeperPreferences preferences) => preferences.VisualKeeperStrategy switch
            {
                DuplicateKeeperPreferences.PreserveMaximumQuality => (20, 60, 5, 10, 5),
                DuplicateKeeperPreferences.StorageOptimized => (10, 25, 45, 10, 10),
                DuplicateKeeperPreferences.Custom => (preferences.ResolutionWeight, preferences.QualityWeight,
                    preferences.StorageWeight, preferences.CodecWeight, 10),
                _ => (15, 35, 30, 10, 10)
            };

        private static double ResolveResolutionStorageCostSensitivity(DuplicateKeeperPreferences preferences) =>
            preferences.VisualKeeperStrategy switch
            {
                DuplicateKeeperPreferences.PreserveMaximumQuality => 0.55,
                DuplicateKeeperPreferences.StorageOptimized => 1.35,
                DuplicateKeeperPreferences.Custom => Math.Clamp(
                    0.55 + preferences.StorageWeight / (double)Math.Max(1, preferences.ResolutionWeight + preferences.StorageWeight),
                    0.55, 1.55),
                _ => 1.0
            };

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
