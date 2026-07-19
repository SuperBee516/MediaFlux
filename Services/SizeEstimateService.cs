using System;
using System.IO;

namespace MediaFlux.Services
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
        /// Auto target size in MB for a file and the current encoding settings.
        /// Probes the metadata needed by the authoritative estimator.
        /// </summary>
        public double EstimateAutoTargetMbSmart(
            string path,
            string compressionProfile,
            string targetCodec = "libx265",
            int quality = 23,
            int? targetHeight = null)
        {
            double srcMb = GetMbOnDisk(path);
            if (srcMb <= 0) return 0;

            var info = _mediaInfoService.GetInfo(path);
            double durSec = info.DurationSeconds is > 0
                ? info.DurationSeconds.Value
                : _mediaInfoService.GetDurationSeconds(path);
            if (durSec <= 0) return 0;

            return EstimateAutoTargetMbSmart(
                srcMb,
                durSec,
                info.Width ?? 0,
                info.Height ?? 0,
                info.Fps ?? 0,
                info.BitrateKbps ?? 0,
                info.VideoCodec,
                compressionProfile,
                targetCodec,
                quality,
                targetHeight);
        }

        /// <summary>
        /// Metadata-only auto estimator. The result is driven by the source's measured
        /// bits-per-pixel, codec efficiency, frame rate and the selected output codec,
        /// quality/profile and scale setting. Missing essential metadata returns zero.
        /// </summary>
        public static double EstimateAutoTargetMbSmart(
            double srcMb,
            double durationSec,
            int width,
            int height,
            double fps,
            int sourceVideoBitrateKbps,
            string? sourceCodec,
            string compressionProfile,
            string targetCodec,
            int quality,
            int? targetHeight)
        {
            if (srcMb <= 0 || durationSec <= 0 || width <= 0 || height <= 0 || fps <= 0)
                return 0;

            if (compressionProfile.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
                return srcMb;

            int outputHeight = targetHeight.GetValueOrDefault(height);
            if (outputHeight <= 0)
                outputHeight = height;
            int outputWidth = Math.Max(2, (int)Math.Round(width * (outputHeight / (double)height)));
            if ((outputWidth & 1) != 0)
                outputWidth++;

            double sourceTotalKbps = srcMb * 8192.0 / durationSec;
            double sourceVideoKbps = sourceVideoBitrateKbps > 0
                ? Math.Min(sourceVideoBitrateKbps, sourceTotalKbps)
                : sourceTotalKbps * 0.90;

            double sourcePixelsPerSecond = (double)width * height * fps;
            double measuredSourceBpp = sourceVideoKbps * 1000.0 / sourcePixelsPerSecond;
            double expectedSourceBpp = GetCodecBpp(sourceCodec);
            double complexity = Math.Clamp(
                Math.Sqrt(measuredSourceBpp / expectedSourceBpp),
                0.55,
                1.65);

            double profileScale = GetCompressionMultiplier(compressionProfile);
            double qualityScale = Math.Pow(2.0, (23 - Math.Clamp(quality, 0, 51)) / 12.0);
            double targetBpp = GetCodecBpp(targetCodec) * profileScale * qualityScale * complexity;
            double targetVideoKbps = ((double)outputWidth * outputHeight * fps * targetBpp) / 1000.0;

            // Preserve the measured non-video portion where possible. This avoids
            // pretending audio/container bytes disappear and keeps short/low-bitrate
            // files from receiving implausibly tiny targets.
            double nonVideoKbps = Math.Clamp(sourceTotalKbps - sourceVideoKbps, 96, 384);
            double targetTotalKbps = (targetVideoKbps + nonVideoKbps) * 1.02;
            double estimateMb = targetTotalKbps * durationSec / 8192.0;

            return Math.Max(0.1, Math.Min(srcMb * 0.98, estimateMb));
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

        // Same mapping you currently have in MainForm
        private static double GetCompressionMultiplier(string profile)
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

        private static double GetCodecBpp(string? codec)
        {
            string value = codec ?? string.Empty;
            if (value.Contains("av1", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("av01", StringComparison.OrdinalIgnoreCase))
                return 0.045;
            if (value.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("hevc", StringComparison.OrdinalIgnoreCase))
                return 0.055;
            if (value.Contains("264", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("avc", StringComparison.OrdinalIgnoreCase))
                return 0.085;
            if (value.Contains("mpeg2", StringComparison.OrdinalIgnoreCase))
                return 0.14;
            return 0.10;
        }
    }
}
