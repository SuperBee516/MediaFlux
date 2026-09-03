using System;
using System.IO;
using MediaFlux.Models;

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
        /// Determines whether the profile-based estimator should be used. A positive
        /// manual target overrides it only while Auto is disabled; an empty target
        /// continues to use the selected Quality / File Size profile.
        /// </summary>
        public static bool ShouldUseProfileEstimate(bool autoRequested, double manualTargetMb)
        {
            return autoRequested || manualTargetMb <= 0;
        }

        /// <summary>
        /// Auto target size in MB for a file and the current encoding settings.
        /// Probes the metadata needed by the authoritative estimator.
        /// </summary>
        public double EstimateAutoTargetMbSmart(
            string path,
            string compressionProfile,
            string targetCodec = "libx265",
            int quality = 23,
            int? targetHeight = null,
            int? targetAudioChannels = null,
            StorageSavingsOptions? storageSavings = null)
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
                targetHeight,
                info.AudioBitrateKbps ?? 0,
                info.AudioStreamCount,
                targetAudioChannels,
                info.TotalBitrateKbps ?? 0,
                info.SubtitleBitrateKbps ?? 0,
                info.SubtitleStreamCount,
                info.DataBitrateKbps ?? 0,
                info.DataStreamCount,
                info.AttachmentStreamCount,
                info.AttachmentSizeBytes,
                storageSavings);
        }

        public double EstimateAutoTargetMbSmart(
            string path,
            string compressionProfile,
            VideoEncoderSelection encoder,
            int quality = 23,
            int? targetHeight = null,
            int? targetAudioChannels = null,
            StorageSavingsOptions? storageSavings = null)
        {
            ArgumentNullException.ThrowIfNull(encoder);
            return EstimateAutoTargetMbSmart(
                path,
                compressionProfile,
                encoder.FfmpegCodec,
                quality,
                targetHeight,
                targetAudioChannels,
                storageSavings);
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
            int? targetHeight,
            int sourceAudioBitrateKbps = 0,
            int sourceAudioStreamCount = 0,
            int? targetAudioChannels = null,
            int sourceTotalBitrateKbps = 0,
            int sourceSubtitleBitrateKbps = 0,
            int sourceSubtitleStreamCount = 0,
            int sourceDataBitrateKbps = 0,
            int sourceDataStreamCount = 0,
            int sourceAttachmentStreamCount = 0,
            long sourceAttachmentSizeBytes = 0,
            StorageSavingsOptions? storageSavings = null)
        {
            return EstimateAutoTargetMbSmartDetailed(
                srcMb,
                durationSec,
                width,
                height,
                fps,
                sourceVideoBitrateKbps,
                sourceCodec,
                compressionProfile,
                targetCodec,
                quality,
                targetHeight,
                sourceAudioBitrateKbps,
                sourceAudioStreamCount,
                targetAudioChannels,
                sourceTotalBitrateKbps,
                sourceSubtitleBitrateKbps,
                sourceSubtitleStreamCount,
                sourceDataBitrateKbps,
                sourceDataStreamCount,
                sourceAttachmentStreamCount,
                sourceAttachmentSizeBytes,
                storageSavings).EstimatedOutputMb;
        }

        public static double EstimateAutoTargetMbSmart(
            double srcMb,
            double durationSec,
            int width,
            int height,
            double fps,
            int sourceVideoBitrateKbps,
            string? sourceCodec,
            string compressionProfile,
            VideoEncoderSelection encoder,
            int quality,
            int? targetHeight,
            int sourceAudioBitrateKbps = 0,
            int sourceAudioStreamCount = 0,
            int? targetAudioChannels = null,
            int sourceTotalBitrateKbps = 0,
            int sourceSubtitleBitrateKbps = 0,
            int sourceSubtitleStreamCount = 0,
            int sourceDataBitrateKbps = 0,
            int sourceDataStreamCount = 0,
            int sourceAttachmentStreamCount = 0,
            long sourceAttachmentSizeBytes = 0,
            StorageSavingsOptions? storageSavings = null)
        {
            ArgumentNullException.ThrowIfNull(encoder);
            return EstimateAutoTargetMbSmart(
                srcMb,
                durationSec,
                width,
                height,
                fps,
                sourceVideoBitrateKbps,
                sourceCodec,
                compressionProfile,
                encoder.FfmpegCodec,
                quality,
                targetHeight,
                sourceAudioBitrateKbps,
                sourceAudioStreamCount,
                targetAudioChannels,
                sourceTotalBitrateKbps,
                sourceSubtitleBitrateKbps,
                sourceSubtitleStreamCount,
                sourceDataBitrateKbps,
                sourceDataStreamCount,
                sourceAttachmentStreamCount,
                sourceAttachmentSizeBytes,
                storageSavings);
        }

        internal static SizeEstimateBreakdown EstimateAutoTargetMbSmartDetailed(
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
            int? targetHeight,
            int sourceAudioBitrateKbps = 0,
            int sourceAudioStreamCount = 0,
            int? targetAudioChannels = null,
            int sourceTotalBitrateKbps = 0,
            int sourceSubtitleBitrateKbps = 0,
            int sourceSubtitleStreamCount = 0,
            int sourceDataBitrateKbps = 0,
            int sourceDataStreamCount = 0,
            int sourceAttachmentStreamCount = 0,
            long sourceAttachmentSizeBytes = 0,
            StorageSavingsOptions? storageSavings = null)
        {
            if (srcMb <= 0 || durationSec <= 0 || width <= 0 || height <= 0 || fps <= 0)
                return SizeEstimateBreakdown.Unavailable;

            if (compressionProfile.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
            {
                return new SizeEstimateBreakdown
                {
                    EstimatedOutputMb = srcMb,
                    Diagnostic = "No Compression profile selected; output estimate equals source size."
                };
            }

            StorageSavingsOptions savings =
                storageSavings?.CloneNormalized() ?? new StorageSavingsOptions();
            bool storageSavingsApplies = savings.Enabled && IsHevcCodec(targetCodec);

            int outputHeight = targetHeight.GetValueOrDefault(height);
            if (outputHeight <= 0)
                outputHeight = height;
            int outputWidth = Math.Max(
                2,
                (int)Math.Round(width * (outputHeight / (double)height)));
            if ((outputWidth & 1) != 0)
                outputWidth++;

            // File size divided by duration is the most reliable total bitrate
            // because it includes every stream and the container. The ffprobe format
            // bitrate is retained in diagnostics as a useful cross-check.
            double sourceTotalKbps = srcMb * 8192.0 / durationSec;
            double measuredAudioKbps = sourceAudioBitrateKbps > 0
                ? Math.Min(sourceAudioBitrateKbps, sourceTotalKbps)
                : 0;
            double measuredSubtitleKbps = sourceSubtitleBitrateKbps > 0
                ? Math.Min(sourceSubtitleBitrateKbps, sourceTotalKbps)
                : 0;
            double measuredDataKbps = sourceDataBitrateKbps > 0
                ? Math.Min(sourceDataBitrateKbps, sourceTotalKbps)
                : 0;
            double mappedAncillaryKbps =
                measuredSubtitleKbps +
                (measuredSubtitleKbps <= 0 ? sourceSubtitleStreamCount * 8d : 0);
            double sourceDataKbps =
                measuredDataKbps > 0
                    ? measuredDataKbps
                    : sourceDataStreamCount * 8d;
            double sourceAttachmentKbps =
                sourceAttachmentSizeBytes > 0
                    ? sourceAttachmentSizeBytes * 8d / 1000d / durationSec
                    : 0;
            double sourceAncillaryKbps =
                mappedAncillaryKbps +
                sourceDataKbps +
                sourceAttachmentKbps;
            double sourceContainerKbps = Math.Max(16, sourceTotalKbps * 0.01);

            bool usedMeasuredVideoBitrate = sourceVideoBitrateKbps > 0;
            double sourceVideoKbps;
            if (usedMeasuredVideoBitrate)
            {
                sourceVideoKbps = Math.Min(
                    sourceVideoBitrateKbps,
                    Math.Max(1, sourceTotalKbps - sourceAncillaryKbps));
            }
            else if (measuredAudioKbps > 0)
            {
                // Regression fix: do not assume video is 90% of the complete file
                // and then add measured audio a second time. Derive the missing video
                // bitrate from the actual total after known copied streams/overhead.
                sourceVideoKbps = Math.Max(
                    1,
                    sourceTotalKbps -
                    measuredAudioKbps -
                    sourceAncillaryKbps -
                    sourceContainerKbps);
            }
            else
            {
                sourceVideoKbps = Math.Max(
                    1,
                    sourceTotalKbps * 0.90 - sourceAncillaryKbps);
            }

            double inferredAudioKbps =
                sourceAudioStreamCount > 0 && measuredAudioKbps <= 0
                    ? Math.Max(
                        0,
                        sourceTotalKbps -
                        sourceVideoKbps -
                        sourceAncillaryKbps -
                        sourceContainerKbps)
                    : 0;
            double plannedAudioKbps = targetAudioChannels is > 0
                ? (targetAudioChannels.Value >= 6 ? 384 : 192) *
                  Math.Max(1, sourceAudioStreamCount)
                : measuredAudioKbps > 0
                    ? measuredAudioKbps
                    : inferredAudioKbps;

            double sourcePixelsPerSecond = (double)width * height * fps;
            double measuredSourceBpp =
                sourceVideoKbps * 1000.0 / sourcePixelsPerSecond;
            double expectedSourceBpp = GetCodecBpp(sourceCodec);
            double complexity = Math.Clamp(
                Math.Sqrt(measuredSourceBpp / expectedSourceBpp),
                0.55,
                1.65);

            int effectiveQuality = storageSavingsApplies && savings.UsesQualityTarget
                ? savings.QualityValue
                : Math.Clamp(quality, 0, 51);
            double targetVideoKbps;
            string mode;
            if (storageSavingsApplies && !savings.UsesQualityTarget)
            {
                targetVideoKbps =
                    sourceVideoKbps * savings.SourceVideoBitratePercent / 100d;
                mode =
                    $"storage bitrate target {savings.SourceVideoBitratePercent:0.#}% of source video";
            }
            else
            {
                double profileScale =
                    storageSavingsApplies && savings.UsesQualityTarget
                        ? 1d
                        : GetCompressionMultiplier(compressionProfile);
                double qualityScale =
                    Math.Pow(2.0, (23 - effectiveQuality) / 12.0);
                double targetBpp =
                    GetCodecBpp(targetCodec) *
                    profileScale *
                    qualityScale *
                    complexity;
                targetVideoKbps =
                    ((double)outputWidth * outputHeight * fps * targetBpp) / 1000.0;
                mode = storageSavingsApplies && savings.UsesQualityTarget
                    ? $"storage quality target {effectiveQuality} (CQ/CRF/ICQ)"
                    : $"conservative profile {compressionProfile}, quality {effectiveQuality}";
            }

            double plannedMappedKbps = plannedAudioKbps + mappedAncillaryKbps;
            double targetTotalKbps = CalculateTargetTotalBitrateKbps(
                targetVideoKbps,
                plannedMappedKbps);
            double estimateMb = targetTotalKbps * durationSec / 8192.0;
            // Keep the projection honest when the selected settings are not
            // expected to save space. Smart Encode can then recommend Skip or
            // Review instead of turning a no-benefit encode into an artificial
            // fixed-size reduction.
            double projectedEstimate = Math.Max(0.1, estimateMb);

            string videoSource = usedMeasuredVideoBitrate
                ? "ffprobe video stream"
                : measuredAudioKbps > 0
                    ? "derived total minus mapped streams"
                    : "90% total fallback";
            string diagnostic =
                $"Estimate: source={srcMb:0.##} MB/{sourceTotalKbps:0} kbps " +
                $"(ffprobe total={sourceTotalBitrateKbps} kbps), " +
                $"video={sourceVideoKbps:0} kbps [{videoSource}], " +
                $"audio={plannedAudioKbps:0} kbps/{sourceAudioStreamCount} stream(s), " +
                $"subtitles present={sourceSubtitleStreamCount} (mapping is decided at encode time), " +
                $"data excluded={sourceDataStreamCount}/{measuredDataKbps:0} kbps, " +
                $"attachments excluded={sourceAttachmentStreamCount}/" +
                $"{sourceAttachmentSizeBytes} bytes; " +
                $"mode={mode}; target video={targetVideoKbps:0} kbps, " +
                $"target total={targetTotalKbps:0} kbps, output={projectedEstimate:0.##} MB.";

            return new SizeEstimateBreakdown
            {
                EstimatedOutputMb = projectedEstimate,
                SourceTotalBitrateKbps = sourceTotalKbps,
                SourceVideoBitrateKbps = sourceVideoKbps,
                PlannedAudioBitrateKbps = plannedAudioKbps,
                PlannedMappedAncillaryBitrateKbps = mappedAncillaryKbps,
                TargetVideoBitrateKbps = targetVideoKbps,
                TargetTotalBitrateKbps = targetTotalKbps,
                UsedMeasuredVideoBitrate = usedMeasuredVideoBitrate,
                UsesStorageQualityTarget =
                    storageSavingsApplies && savings.UsesQualityTarget,
                Diagnostic = diagnostic
            };
        }

        internal static double CalculateTargetTotalBitrateKbps(
            double targetVideoKbps,
            double plannedMappedStreamKbps)
        {
            double subtotal = Math.Max(0, targetVideoKbps) +
                              Math.Max(0, plannedMappedStreamKbps);
            double percentBasedTotal = subtotal / 0.99;
            return percentBasedTotal * 0.01 >= 16
                ? percentBasedTotal
                : subtotal + 16;
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
        internal static double GetCompressionMultiplier(string profile)
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

        internal static bool IsHevcCodec(string? codec)
        {
            string value = codec ?? string.Empty;
            return value.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("hevc", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class SizeEstimateBreakdown
    {
        public static SizeEstimateBreakdown Unavailable { get; } = new();

        public double EstimatedOutputMb { get; init; }
        public double SourceTotalBitrateKbps { get; init; }
        public double SourceVideoBitrateKbps { get; init; }
        public double PlannedAudioBitrateKbps { get; init; }
        public double PlannedMappedAncillaryBitrateKbps { get; init; }
        public double TargetVideoBitrateKbps { get; init; }
        public double TargetTotalBitrateKbps { get; init; }
        public bool UsedMeasuredVideoBitrate { get; init; }
        public bool UsesStorageQualityTarget { get; init; }
        public string Diagnostic { get; init; } = "Required metadata is unavailable.";
    }
}
