using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class SmartEncodeDecisionService
    {
        private const double StrongSavingsPercent = 30;
        private const double AudioReviewSharePercent = 30;

        public SmartEncodeRecommendation Evaluate(
            SmartEncodeSourceInfo source,
            SmartEncodeIntent intent)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(intent);

            if (source.SourceMb <= 0 ||
                source.DurationSeconds <= 0 ||
                source.Width <= 0 ||
                source.Height <= 0 ||
                source.FramesPerSecond <= 0 ||
                intent.EstimatedOutputMb <= 0)
            {
                return Create(
                    SmartEncodeRecommendationKind.Unavailable,
                    SmartEncodeConfidence.Low,
                    null,
                    null,
                    "Required media metadata is unavailable.",
                    "MediaFlux will not invent resolution, duration, frame-rate, or size values.");
            }

            double savingsMb = source.SourceMb - intent.EstimatedOutputMb;
            double savingsPercent = (savingsMb / source.SourceMb) * 100d;
            var reasons = new List<string>();
            var reviewReasons = new List<string>();

            if (IsInterlaced(source.FieldOrder))
            {
                reviewReasons.Add(
                    $"The source is flagged as interlaced ({source.FieldOrder}); " +
                    "the current encode path does not automatically deinterlace it.");
            }

            if (intent.TargetHeight.HasValue && intent.TargetHeight.Value > source.Height)
            {
                reviewReasons.Add(
                    $"The selected output height ({intent.TargetHeight.Value}p) is above " +
                    $"the source height ({source.Height}p).");
            }

            if (source.VideoStreamCount > 1)
            {
                reviewReasons.Add(
                    $"The file contains {source.VideoStreamCount} video streams and needs stream-selection review.");
            }

            if (source.IsLikelyAnimation)
            {
                reviewReasons.Add(
                    "The source appears to be animation; a content-specific profile may produce a better result.");
            }

            double audioShare = GetAudioSharePercent(source);
            if (source.AudioBitrateKbps > 256 && audioShare >= AudioReviewSharePercent)
            {
                reviewReasons.Add(
                    $"Audio accounts for about {audioShare:0}% of the source bitrate; " +
                    "video re-encoding alone may save less than expected.");
            }

            double sourceEfficiency = GetCodecEfficiency(source.VideoCodec);
            double targetEfficiency = GetCodecEfficiency(intent.TargetCodec);
            bool isDownscaling =
                intent.TargetHeight.HasValue && intent.TargetHeight.Value < source.Height;
            if (!isDownscaling &&
                sourceEfficiency > 0 &&
                targetEfficiency > 0 &&
                sourceEfficiency > targetEfficiency + 0.25)
            {
                reviewReasons.Add(
                    $"The source codec ({FriendlyCodec(source.VideoCodec)}) is more storage-efficient " +
                    $"than the selected output codec ({FriendlyCodec(intent.TargetCodec)}).");
            }

            AddEfficiencyReasons(source, intent, reasons);

            if (reviewReasons.Count > 0)
            {
                reasons.InsertRange(0, reviewReasons);
                return Create(
                    SmartEncodeRecommendationKind.Review,
                    ResolveConfidence(source),
                    savingsPercent,
                    savingsMb,
                    reviewReasons[0],
                    reasons.ToArray());
            }

            double minimumSavings = Math.Clamp(intent.MinimumSavingsPercent, 0, 90);
            if (IsContainerRemuxOpportunity(source) &&
                (savingsMb <= 0 || savingsPercent < minimumSavings))
            {
                string remuxReason =
                    "The video is already efficient, but its legacy container can be cleaned up without video encoding.";
                reasons.Insert(0, remuxReason);
                reasons.Add(
                    "A lossless MKV stream copy should preserve the video, audio, subtitles, metadata, and chapters with little size change.");
                return Create(
                    SmartEncodeRecommendationKind.RemuxOnly,
                    ResolveConfidence(source),
                    savingsPercent,
                    savingsMb,
                    remuxReason,
                    reasons.ToArray());
            }

            SmartEncodeRecommendationKind kind;
            string primaryReason;
            if (savingsMb <= 0)
            {
                kind = SmartEncodeRecommendationKind.Skip;
                primaryReason = "The selected settings are not expected to reduce the file size.";
            }
            else if (savingsPercent < minimumSavings)
            {
                kind = SmartEncodeRecommendationKind.Skip;
                primaryReason =
                    $"Expected savings are below the configured {minimumSavings:0.#}% minimum.";
            }
            else if (savingsPercent >= StrongSavingsPercent)
            {
                kind = SmartEncodeRecommendationKind.StrongCandidate;
                primaryReason = "The selected settings are expected to produce significant savings.";
            }
            else
            {
                kind = SmartEncodeRecommendationKind.ModerateCandidate;
                primaryReason = "The selected settings are expected to produce useful but smaller savings.";
            }

            reasons.Insert(0, primaryReason);
            return Create(
                kind,
                ResolveConfidence(source),
                savingsPercent,
                savingsMb,
                primaryReason,
                reasons.ToArray());
        }

        public SmartEncodeRecommendation RefineWithDeepAnalysis(
            SmartEncodeRecommendation baseline,
            DeepMediaAnalysisResult analysis,
            SmartEncodeContentHint contentHint,
            double intendedOutputMb)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(analysis);

            var reasons = baseline.Reasons
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .ToList();
            SmartEncodeRecommendationKind kind = baseline.Kind;
            SmartEncodeConfidence confidence = baseline.Confidence;
            string primaryReason = baseline.PrimaryReason;

            if (analysis.ProjectedOutputMb is > 0 && intendedOutputMb > 0)
            {
                double differencePercent =
                    Math.Abs(analysis.ProjectedOutputMb.Value - intendedOutputMb) /
                    intendedOutputMb * 100d;

                if (differencePercent <= 15)
                {
                    confidence = analysis.ProjectionSampleCount > 0
                        ? analysis.ProjectionConfidence
                        : SmartEncodeConfidence.High;
                    reasons.Add(
                        $"Beginning/middle/end samples project {analysis.ProjectedOutputMb.Value:0.#} MB, " +
                        "which agrees with the current estimate.");
                }
                else if (differencePercent <= 35)
                {
                    confidence = SmartEncodeConfidence.Medium;
                    reasons.Add(
                        $"Sample projection ({analysis.ProjectedOutputMb.Value:0.#} MB) differs from " +
                        $"the current estimate ({intendedOutputMb:0.#} MB) by about {differencePercent:0}%.");
                }
                else
                {
                    kind = SmartEncodeRecommendationKind.Review;
                    confidence = SmartEncodeConfidence.Medium;
                    primaryReason =
                        "Sample encoding disagrees substantially with the current size estimate.";
                    reasons.Insert(
                        0,
                        $"Beginning/middle/end samples project {analysis.ProjectedOutputMb.Value:0.#} MB " +
                        $"instead of {intendedOutputMb:0.#} MB ({differencePercent:0}% difference).");
                }

                if (analysis.ProjectedOutputLowerMb is > 0 &&
                    analysis.ProjectedOutputUpperMb is > 0)
                {
                    reasons.Add(
                        $"Calibrated range: {analysis.ProjectedOutputLowerMb.Value:0.#}–" +
                        $"{analysis.ProjectedOutputUpperMb.Value:0.#} MB " +
                        $"({analysis.ProjectionConfidence.ToString().ToLowerInvariant()} confidence).");
                }
            }
            else
            {
                confidence = confidence == SmartEncodeConfidence.High
                    ? SmartEncodeConfidence.Medium
                    : confidence;
            }

            if (analysis.InterlaceStatus is
                SampledInterlaceStatus.Interlaced or
                SampledInterlaceStatus.Mixed)
            {
                kind = SmartEncodeRecommendationKind.Review;
                confidence = SmartEncodeConfidence.High;
                primaryReason = analysis.InterlaceStatus == SampledInterlaceStatus.Interlaced
                    ? "Sampled frames indicate interlaced video."
                    : "Sampled frames contain a mixture of progressive and interlaced video.";
                reasons.Insert(
                    0,
                    $"{primaryReason} The current encode path does not automatically deinterlace it.");
            }
            else if (analysis.InterlaceStatus == SampledInterlaceStatus.Progressive)
            {
                reasons.Add("Beginning/middle/end frame samples appear progressive.");
            }

            bool treatAsSynthetic = contentHint switch
            {
                SmartEncodeContentHint.Animation => true,
                SmartEncodeContentHint.ScreenContent => true,
                SmartEncodeContentHint.LiveAction => false,
                _ => analysis.PossibleSyntheticContent
            };

            if (treatAsSynthetic)
            {
                kind = SmartEncodeRecommendationKind.Review;
                primaryReason = contentHint switch
                {
                    SmartEncodeContentHint.Animation =>
                        "This row is marked as animation and should use content-specific settings.",
                    SmartEncodeContentHint.ScreenContent =>
                        "This row is marked as screen content and should use content-specific settings.",
                    _ =>
                        "Visual samples suggest possible animation or screen content."
                };
                reasons.Insert(0, primaryReason);
            }
            else if (contentHint == SmartEncodeContentHint.LiveAction &&
                     analysis.PossibleSyntheticContent)
            {
                reasons.Add(
                    "The Live action content hint overrides the conservative visual-content heuristic.");
            }

            if (contentHint != SmartEncodeContentHint.Auto)
                reasons.Add($"Content hint: {FriendlyContentHint(contentHint)}.");

            foreach (string note in analysis.Notes)
                reasons.Add(note);

            return new SmartEncodeRecommendation
            {
                Kind = kind,
                Confidence = confidence,
                EstimatedSavingsPercent = baseline.EstimatedSavingsPercent,
                EstimatedSavingsMb = baseline.EstimatedSavingsMb,
                PrimaryReason = primaryReason,
                Reasons = reasons
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }

        private static void AddEfficiencyReasons(
            SmartEncodeSourceInfo source,
            SmartEncodeIntent intent,
            ICollection<string> reasons)
        {
            double bpp = GetBitsPerPixelPerFrame(source);
            double lowThreshold = GetLowBppThreshold(source.VideoCodec);
            if (bpp > 0 && lowThreshold > 0 && bpp < lowThreshold)
            {
                reasons.Add(
                    $"Source video bitrate is already low for its codec and resolution " +
                    $"({bpp:0.000} bits/pixel/frame).");
            }

            double sourceEfficiency = GetCodecEfficiency(source.VideoCodec);
            double targetEfficiency = GetCodecEfficiency(intent.TargetCodec);
            if (sourceEfficiency >= 3 &&
                targetEfficiency > 0 &&
                sourceEfficiency >= targetEfficiency)
            {
                reasons.Add(
                    $"The source already uses the efficient {FriendlyCodec(source.VideoCodec)} codec.");
            }
        }

        private static double GetBitsPerPixelPerFrame(SmartEncodeSourceInfo source)
        {
            if (source.VideoBitrateKbps <= 0 ||
                source.Width <= 0 ||
                source.Height <= 0 ||
                source.FramesPerSecond <= 0)
            {
                return 0;
            }

            return source.VideoBitrateKbps * 1000d /
                   (source.Width * (double)source.Height * source.FramesPerSecond);
        }

        private static double GetLowBppThreshold(string codec)
        {
            double efficiency = GetCodecEfficiency(codec);
            return efficiency switch
            {
                >= 4 => 0.035,
                >= 3 => 0.045,
                >= 2 => 0.065,
                > 0 => 0.09,
                _ => 0
            };
        }

        private static double GetAudioSharePercent(SmartEncodeSourceInfo source)
        {
            int total = source.TotalBitrateKbps;
            if (total <= 0)
                total = source.VideoBitrateKbps + source.AudioBitrateKbps;

            return total > 0 && source.AudioBitrateKbps > 0
                ? Math.Clamp(source.AudioBitrateKbps * 100d / total, 0, 100)
                : 0;
        }

        private static bool IsInterlaced(string fieldOrder)
        {
            string value = fieldOrder?.Trim().ToLowerInvariant() ?? "";
            return value.Length > 0 &&
                   value is not "progressive" and not "unknown" and not "unspecified";
        }

        private static SmartEncodeConfidence ResolveConfidence(
            SmartEncodeSourceInfo source)
        {
            if (source.VideoBitrateKbps > 0 &&
                source.TotalBitrateKbps > 0 &&
                (source.AudioStreamCount == 0 || source.AudioBitrateKbps > 0))
            {
                return SmartEncodeConfidence.High;
            }

            return source.VideoBitrateKbps > 0
                ? SmartEncodeConfidence.Medium
                : SmartEncodeConfidence.Low;
        }

        private static double GetCodecEfficiency(string codec)
        {
            string value = codec?.ToLowerInvariant() ?? "";
            if (value.Contains("av1") || value.Contains("av01") || value.Contains("svt"))
                return 4;
            if (value.Contains("265") || value.Contains("hevc") || value.Contains("vp9"))
                return 3;
            if (value.Contains("264") || value.Contains("avc"))
                return 2;
            if (value.Contains("mpeg2") || value.Contains("mpeg-2"))
                return 1;
            if (value.Contains("mpeg4") || value.Contains("vp8"))
                return 1.5;
            return 0;
        }

        internal static bool IsContainerRemuxOpportunity(
            SmartEncodeSourceInfo source)
        {
            if (source.VideoStreamCount != 1 ||
                GetCodecEfficiency(source.VideoCodec) < 2)
            {
                return false;
            }

            string extension = Path.GetExtension(source.Path)
                .ToLowerInvariant();
            if (extension is
                ".avi" or
                ".wmv" or
                ".asf" or
                ".flv" or
                ".ts" or
                ".m2ts" or
                ".mts" or
                ".mpeg" or
                ".mpg" or
                ".vob")
            {
                return true;
            }

            string[] formats = (source.FormatName ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return formats.Any(format =>
                format.Equals("avi", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("asf", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("flv", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("mpegts", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("mpeg", StringComparison.OrdinalIgnoreCase));
        }

        private static string FriendlyCodec(string codec)
        {
            string value = codec?.ToLowerInvariant() ?? "";
            if (value.Contains("av1") || value.Contains("av01") || value.Contains("svt"))
                return "AV1";
            if (value.Contains("265") || value.Contains("hevc"))
                return "HEVC";
            if (value.Contains("264") || value.Contains("avc"))
                return "H.264";
            if (value.Contains("vp9"))
                return "VP9";
            if (value.Contains("mpeg2") || value.Contains("mpeg-2"))
                return "MPEG-2";
            return string.IsNullOrWhiteSpace(codec) ? "unknown" : codec;
        }

        private static string FriendlyContentHint(SmartEncodeContentHint contentHint)
        {
            return contentHint switch
            {
                SmartEncodeContentHint.LiveAction => "Live action",
                SmartEncodeContentHint.Animation => "Animation",
                SmartEncodeContentHint.ScreenContent => "Screen content",
                _ => "Auto"
            };
        }

        private static SmartEncodeRecommendation Create(
            SmartEncodeRecommendationKind kind,
            SmartEncodeConfidence confidence,
            double? savingsPercent,
            double? savingsMb,
            string primaryReason,
            params string[] reasons)
        {
            return new SmartEncodeRecommendation
            {
                Kind = kind,
                Confidence = confidence,
                EstimatedSavingsPercent = savingsPercent,
                EstimatedSavingsMb = savingsMb,
                PrimaryReason = primaryReason,
                Reasons = reasons
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }
}
