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
    }
}
