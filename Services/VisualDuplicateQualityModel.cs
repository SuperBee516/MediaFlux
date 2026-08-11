using MediaFlux.Models;
using System;
using System.Collections.Generic;

namespace MediaFlux.Services
{
    public enum VisualQualityBand
    {
        BelowTarget,
        Acceptable,
        Good,
        VeryGood,
        DiminishingReturns
    }

    public sealed record VisualQualityAssessment(
        VisualQualityBand Band,
        double SufficiencyScore,
        double AcceptableBitrateKbps,
        double GoodBitrateKbps,
        double VeryGoodBitrateKbps,
        double DiminishingReturnsBitrateKbps,
        string CodecIdentity,
        double FrameRate);

    public sealed record VisualResolutionValueAssessment(
        double Utility,
        double PixelRatio,
        double SizeRatio,
        double StorageEfficiencyRatio,
        double ConfidenceSupport,
        double QualitySupport,
        double UpscaleRisk);

    /// <summary>
    /// Estimated bitrate sufficiency for keeper comparison. This is deliberately
    /// a tunable signal, not a claim that bitrate measures perceptual quality.
    /// </summary>
    public static class VisualDuplicateQualityModel
    {
        private sealed record CodecScale(double BitrateScale, double PreferenceScore);

        private static readonly IReadOnlyDictionary<string, CodecScale> CodecScales =
            new Dictionary<string, CodecScale>(StringComparer.OrdinalIgnoreCase)
            {
                ["av1"] = new(0.78, 100),
                ["hevc"] = new(1.00, 92),
                ["vp9"] = new(1.10, 86),
                ["h264"] = new(1.55, 72),
                ["mpeg4"] = new(1.85, 58),
                ["mpeg2"] = new(2.40, 45)
            };

        public static VisualQualityAssessment Assess(DuplicateItem item)
        {
            string codec = GetCodecIdentity(item.VideoCodec);
            CodecScale scale = CodecScales.TryGetValue(codec, out CodecScale? known)
                ? known
                : new CodecScale(1.75, 50);
            double fps = item.FrameRate > 0 ? item.FrameRate : 30;
            double pixels = Math.Max(1, (double)item.Width * item.Height);
            double resolutionScale = Math.Sqrt(pixels / (1920d * 1080d));
            double frameRateScale = Math.Pow(Math.Max(0.5, fps / 30d), 0.65);
            double contextScale = scale.BitrateScale * resolutionScale * frameRateScale;

            // Calibrated so 1080p30 HEVC maps to 1.2 / 1.5 / 2.5 / 4 Mbps.
            double acceptable = 1_200 * contextScale;
            double good = 1_500 * contextScale;
            double veryGood = 2_500 * contextScale;
            double diminishing = 4_000 * contextScale;
            double bitrate = Math.Max(0, item.BitrateKbps);

            VisualQualityBand band;
            double score;
            if (bitrate < acceptable)
            {
                band = VisualQualityBand.BelowTarget;
                score = 45 * Math.Pow(bitrate / Math.Max(1, acceptable), 0.72);
            }
            else if (bitrate < good)
            {
                band = VisualQualityBand.Acceptable;
                score = Interpolate(bitrate, acceptable, good, 45, 65);
            }
            else if (bitrate < veryGood)
            {
                band = VisualQualityBand.Good;
                score = InterpolateLog(bitrate, good, veryGood, 65, 83);
            }
            else if (bitrate < diminishing)
            {
                band = VisualQualityBand.VeryGood;
                score = InterpolateLog(bitrate, veryGood, diminishing, 83, 94);
            }
            else
            {
                band = VisualQualityBand.DiminishingReturns;
                score = 94 + 6 * (1 - Math.Exp(-(bitrate / diminishing - 1) * 0.8));
            }

            return new VisualQualityAssessment(band, Math.Clamp(score, 0, 100), acceptable, good,
                veryGood, diminishing, codec, fps);
        }

        public static double GetCodecPreferenceScore(string codec) =>
            CodecScales.TryGetValue(GetCodecIdentity(codec), out CodecScale? scale) ? scale.PreferenceScore : 50;

        /// <summary>
        /// Measures the practical value of a resolution increase relative to its storage cost.
        /// Logarithms make successive pixel increases progressively less valuable. The quality
        /// and confidence terms reduce the benefit when the extra resolution is weakly supported.
        /// UpscaleRisk is metadata-only evidence, not a definitive upscale detector.
        /// </summary>
        public static VisualResolutionValueAssessment AssessResolutionValue(
            DuplicateItem item,
            DuplicateItem lowerResolutionReference,
            VisualQualityAssessment quality,
            VisualQualityAssessment referenceQuality,
            double visualConfidence,
            double visualConfidenceFloor,
            double storageCostSensitivity)
        {
            double pixelRatio = GetPixels(item) / (double)Math.Max(1, GetPixels(lowerResolutionReference));
            double sizeRatio = item.LengthBytes / (double)Math.Max(1, lowerResolutionReference.LengthBytes);
            double storageEfficiencyRatio = pixelRatio / Math.Max(0.0001, sizeRatio);
            double confidenceRange = Math.Max(0.001, 100 - visualConfidenceFloor);
            double confidenceProgress = Math.Clamp((visualConfidence - visualConfidenceFloor) / confidenceRange, 0, 1);
            double confidenceSupport = 0.35 + 0.65 * confidenceProgress;
            double qualityDelta = quality.SufficiencyScore - referenceQuality.SufficiencyScore;
            double qualitySupport = Math.Clamp(1 + qualityDelta / 50d, 0.35, 1);

            double expectedBitrateRatio = quality.GoodBitrateKbps / Math.Max(1, referenceQuality.GoodBitrateKbps);
            double actualBitrateRatio = item.BitrateKbps / (double)Math.Max(1, lowerResolutionReference.BitrateKbps);
            double bitrateSupport = actualBitrateRatio / Math.Max(0.0001, expectedBitrateRatio);
            double upscaleRisk = pixelRatio >= 1.5
                ? Math.Clamp((0.90 - bitrateSupport) / 0.45, 0, 1) *
                  Math.Clamp((-qualityDelta - 2) / 15d, 0, 1)
                : 0;

            double pixelBenefit = Math.Log(pixelRatio, 2) * confidenceSupport * qualitySupport;
            double storageCost = Math.Log(Math.Max(0.0001, sizeRatio), 2) * storageCostSensitivity;
            double utility = pixelBenefit - storageCost - upscaleRisk * 0.75;
            return new VisualResolutionValueAssessment(utility, pixelRatio, sizeRatio,
                storageEfficiencyRatio, confidenceSupport, qualitySupport, upscaleRisk);
        }

        public static string FormatBand(VisualQualityBand band) => band switch
        {
            VisualQualityBand.BelowTarget => "below target",
            VisualQualityBand.Acceptable => "acceptable",
            VisualQualityBand.Good => "good",
            VisualQualityBand.VeryGood => "very good",
            _ => "diminishing returns"
        };

        private static double Interpolate(double value, double low, double high, double lowScore, double highScore) =>
            lowScore + (highScore - lowScore) * Math.Clamp((value - low) / Math.Max(1, high - low), 0, 1);

        private static double InterpolateLog(double value, double low, double high, double lowScore, double highScore) =>
            lowScore + (highScore - lowScore) * Math.Clamp(Math.Log(value / low) / Math.Log(high / low), 0, 1);

        private static string GetCodecIdentity(string codec)
        {
            string value = codec?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value.Contains("av1")) return "av1";
            if (value.Contains("hevc") || value.Contains("h265") || value.Contains("x265")) return "hevc";
            if (value.Contains("h264") || value.Contains("avc") || value.Contains("x264")) return "h264";
            if (value.Contains("vp9")) return "vp9";
            if (value.Contains("mpeg2")) return "mpeg2";
            if (value.Contains("mpeg4")) return "mpeg4";
            return value;
        }

        private static long GetPixels(DuplicateItem item) => (long)item.Width * item.Height;
    }
}
