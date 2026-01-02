using System;
using System.IO;

namespace Encode.Services
{
    public sealed class SizeEstimateService
    {
        private readonly MediaInfoService _mediaInfoService;

        public SizeEstimateService(MediaInfoService mediaInfoService)
        {
            _mediaInfoService = mediaInfoService ?? throw new ArgumentNullException(nameof(mediaInfoService));
        }

        // ─────────────────────────────────────────────
        // PUBLIC API
        // ─────────────────────────────────────────────

        /// <summary>
        /// Estimate a size range based on codec, quality, resolution and fps.
        /// Returns min/max/mid in KiB.
        /// </summary>
        public (int minKiB, int maxKiB, int midKiB) EstimateSizeRangeKiB(
            string path,
            string codec,
            int quality)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must not be empty.", nameof(path));

            // Duration
            double durationSec = Math.Max(1.0, _mediaInfoService.GetDurationSeconds(path));

            // Resolution and fps
            var (w, h) = _mediaInfoService.GetResolutionPixels(path);
            int fps = Math.Max(24, (int)Math.Round(_mediaInfoService.GetFps(path)));

            long pix = (long)w * h;
            string bucket = (pix >= 3840L * 2160) ? "UHD" :
                            (pix >= 2560L * 1440) ? "QHD" :
                            (pix >= 1920L * 1080) ? "FHD" :
                            (pix >= 1280L * 720) ? "HD" : "SD";

            // Bits-per-pixel bands per codec & resolution bucket
            (double minBpp, double maxBpp) = codec.ToLowerInvariant() switch
            {
                var c when c.Contains("av1") =>
                    bucket switch
                    {
                        "UHD" => (0.03, 0.08),
                        "QHD" => (0.035, 0.09),
                        "FHD" => (0.04, 0.10),
                        "HD" => (0.05, 0.12),
                        _ => (0.06, 0.14)
                    },

                var c when c.Contains("265") || c.Contains("hevc") =>
                    bucket switch
                    {
                        "UHD" => (0.04, 0.10),
                        "QHD" => (0.05, 0.12),
                        "FHD" => (0.06, 0.14),
                        "HD" => (0.08, 0.18),
                        _ => (0.10, 0.22)
                    },

                _ => // h264-ish
                    bucket switch
                    {
                        "UHD" => (0.05, 0.12),
                        "QHD" => (0.06, 0.14),
                        "FHD" => (0.08, 0.18),
                        "HD" => (0.10, 0.22),
                        _ => (0.12, 0.26)
                    }
            };

            // Adjust for CRF/CQ: lower number => larger output
            // 23 ~ neutral, one "step" ~= 5 units
            double qNorm = Math.Clamp((quality - 23) / 5.0, -1.0, 1.0);
            double scale = 1.0 + (qNorm * 0.25); // -25% … +25%
            minBpp *= scale;
            maxBpp *= scale;

            double minbps = minBpp * w * h * fps;
            double maxbps = maxBpp * w * h * fps;

            double minBytes = minbps * durationSec / 8.0;
            double maxBytes = maxbps * durationSec / 8.0;

            // ~5% container/audio overhead
            minBytes *= 1.05;
            maxBytes *= 1.05;

            int minKiB = (int)Math.Max(1, minBytes / 1024.0);
            int maxKiB = (int)Math.Max(minKiB + 1, maxBytes / 1024.0);
            int midKiB = (int)((minKiB + maxKiB) / 2.0);

            return (minKiB, maxKiB, midKiB);
        }

        /// <summary>
        /// Auto target size in MB based only on file path + compression profile.
        /// Probes duration and resolution as needed.
        /// </summary>
        public double EstimateAutoTargetMbSmart(string path, string compressionProfile)
        {
            double srcMb = GetMbOnDisk(path);
            if (srcMb <= 0) return 1.0;

            double durSec = _mediaInfoService.GetDurationSeconds(path);
            if (durSec <= 0) return Math.Max(1.0, srcMb * 0.6);

            var (w, h) = _mediaInfoService.GetResolutionPixels(path);
            string res = GetResolutionBucket(w, h);

            return EstimateAutoTargetMbSmart(srcMb, durSec, res, compressionProfile);
        }

        /// <summary>
        /// Core estimator that works purely on metadata (for when MainForm already has RowMeta).
        /// </summary>
        public double EstimateAutoTargetMbSmart(
            double srcMb,
            double durationSec,
            string? resolutionBucket,
            string compressionProfile)
        {
            if (srcMb <= 0) return 1.0;

            if (durationSec <= 0)
                return Math.Max(1.0, srcMb * 0.6);

            double avgKbps = (srcMb * 8192.0) / durationSec;

            // Higher source bitrate => more room to compress
            double mult = avgKbps switch
            {
                >= 12000 => 0.35, // very high bitrate
                >= 8000 => 0.45, // high
                >= 4000 => 0.55, // medium
                >= 2000 => 0.65, // low
                _ => 0.80  // already quite small/efficient
            };

            string res = resolutionBucket ?? "Unknown";
            double resAdj = res switch
            {
                "4K" => 0.90,
                "1080p" => 1.00,
                "720p" => 1.05,
                "480p" => 1.10,
                _ => 1.00
            };

            double est = srcMb * mult * resAdj;

            // Guardrails: don't grow files; don't crush below ~30%
            double minPct = 0.30;
            double maxPct = 0.98;
            est = Math.Max(srcMb * minPct, Math.Min(srcMb * maxPct, est));

            est *= GetCompressionMultiplier(compressionProfile);

            // Never return tiny or zero
            return Math.Max(1.0, est);
        }

        // ─────────────────────────────────────────────
        // INTERNAL HELPERS
        // ─────────────────────────────────────────────

        private static double GetMbOnDisk(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) return fi.Length / (1024.0 * 1024.0);
            }
            catch
            {
                // ignore IO issues, just treat as 0
            }
            return 0;
        }

        private static string GetResolutionBucket(int w, int h)
        {
            long pix = (long)w * h;
            if (pix >= 3840L * 2160) return "4K";
            if (pix >= 1920L * 1080) return "1080p";
            if (pix >= 1280L * 720) return "720p";
            if (pix >= 720L * 480) return "480p";
            return "Unknown";
        }

        // Same mapping you currently have in MainForm
        private double GetCompressionMultiplier(string profile)
        {
            switch (profile)
            {
                // New verbose UI labels
                case "Very High Quality (Largest File)":
                    return 0.95; // almost source size

                case "High Quality":
                    return 0.85;

                case "Medium Quality (Default)":
                    return 0.75;

                case "Low Quality (Smaller File)":
                    return 0.65;

                case "Very Low Quality (Smallest File)":
                    return 0.55; // strongest compression, smallest files

                // Old short labels (for old queue exports / configs)
                case "Very High":
                    return 0.95;
                case "High":
                    return 0.85;
                case "Medium":
                    return 0.75;
                case "Low":
                    return 0.65;
                case "Very Low":
                    return 0.55;

                case "No Compression":
                    return 1.00;

                default:
                    // Sensible default if we see an unknown string
                    return 0.75; // treat as Medium
            }
        }
    }
}
