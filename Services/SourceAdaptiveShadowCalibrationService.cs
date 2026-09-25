using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Builds observational AVC-to-HEVC calibration evidence; never supplies execution values.</summary>
public sealed class SourceAdaptiveShadowCalibrationService
{
    public SourceAdaptiveShadowCalibration CreateSafely(
        EncodingDecisionContext context,
        EncodingQualityResolution quality,
        VideoOutputGeometryPlan? geometry)
    {
        try
        {
            return Create(context, quality, geometry);
        }
        catch (Exception exception)
        {
            int? cq = quality?.EffectiveQuality;
            return new SourceAdaptiveShadowCalibration
            {
                Status = SourceAdaptiveShadowStatus.PredictionUnavailable,
                EligibilityReason = $"Shadow calculation failed safely ({exception.GetType().Name}); encoding policy is unchanged.",
                InitialCq = cq,
                FinalExecutionCq = cq,
                ShadowReason = "Shadow mode only; execution adjustment: none."
            };
        }
    }

    public SourceAdaptiveShadowCalibration Create(
        EncodingDecisionContext context,
        EncodingQualityResolution quality,
        VideoOutputGeometryPlan? geometry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(quality);
        MediaProbeStreamInfo? video = context.Source.Streams.FirstOrDefault(stream =>
            stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        double? sourceFps = video?.FrameRate is > 0 ? video.FrameRate : null;
        double? sourceVideoKbps = video?.BitRate is > 0 ? video.BitRate.Value / 1000d : null;
        bool transformed = HasMaterialTransform(context, geometry);
        SourceAdaptiveShadowStatus status = SourceAdaptiveShadowStatus.NotApplicable;
        string reason = "The job is outside the AVC-to-HEVC Source Adaptive calibration scope.";

        if (context.TargetMb is > 0 || quality.IsSupersededByTargetSize)
        {
            status = SourceAdaptiveShadowStatus.ExplicitUserPolicy;
            reason = "An authoritative target-size policy supersedes constant quality.";
        }
        else if (quality.Intent.Kind != EncodingQualityIntentKind.QualityTarget)
        {
            status = SourceAdaptiveShadowStatus.ExplicitUserPolicy;
            reason = "Quality is explicit numeric intent rather than Source Adaptive.";
        }
        else if (context.Input.Kind != EncodingInputKind.File)
        {
            status = SourceAdaptiveShadowStatus.NotApplicable;
            reason = "Calibration is limited to ordinary file inputs.";
        }
        else if (!IsCodec(video?.CodecName, "h264") || context.Encoder.CodecFamily != VideoCodecFamily.Hevc ||
                 !context.Encoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase))
        {
            status = SourceAdaptiveShadowStatus.NotApplicable;
            reason = "Only Source Adaptive AVC-to-HEVC NVENC jobs are in the primary cohort.";
        }
        else if (transformed)
        {
            status = SourceAdaptiveShadowStatus.TransformationExcluded;
            reason = "Restoration, filtering, scaling, timeline repair, or changed geometry excludes this sample from the primary cohort.";
        }
        else if (sourceVideoKbps is not > 0 || context.KnownDuration <= TimeSpan.Zero ||
                 geometry?.Width is not > 0 || geometry.Height is not > 0 || sourceFps is not > 0)
        {
            status = SourceAdaptiveShadowStatus.InsufficientEvidence;
            reason = "Measured source video bitrate, duration, final geometry, and source cadence are required.";
        }
        else if (geometry.Width != video!.Width || geometry.Height != video.Height)
        {
            status = SourceAdaptiveShadowStatus.TransformationExcluded;
            reason = "Output geometry differs from source geometry.";
        }
        else
        {
            status = SourceAdaptiveShadowStatus.CalibrationCandidate;
            reason = "Measured AVC video bitrate and unchanged NVENC HEVC plan are available; no enforcement threshold is defined.";
        }

        SizeEstimateBreakdown? estimate = null;
        if (status == SourceAdaptiveShadowStatus.CalibrationCandidate &&
            quality.EffectiveQuality is int cq && video?.Width is > 0 && video.Height is > 0 && sourceFps is > 0 &&
            context.Source.SizeBytes is > 0 && context.KnownDuration > TimeSpan.Zero && geometry is not null)
        {
            int audioCount = context.Source.Streams.Count(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase));
            int sourceAudioKbps = (int)Math.Round(context.Source.Streams
                .Where(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) && stream.BitRate is > 0)
                .Sum(stream => stream.BitRate!.Value / 1000d));
            int subtitleCount = context.Source.Streams.Count(stream => stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase));
            int dataCount = context.Source.Streams.Count(stream => stream.CodecType.Equals("data", StringComparison.OrdinalIgnoreCase));
            estimate = SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                context.Source.SizeBytes.Value / 1048576d, context.KnownDuration.TotalSeconds,
                video.Width.Value, video.Height.Value, sourceFps.Value,
                sourceVideoKbps is > 0 ? (int)Math.Round(sourceVideoKbps.Value) : 0,
                video.CodecName, "Medium Quality (Default)", context.Encoder.FfmpegCodec, cq,
                geometry.Height, sourceAudioKbps, audioCount, context.AudioChannels,
                context.Source.BitRate is > 0 ? (int)Math.Round(context.Source.BitRate.Value / 1000d) : 0,
                sourceSubtitleBitrateKbps: 0, sourceSubtitleStreamCount: subtitleCount,
                sourceDataStreamCount: dataCount);
            if (status == SourceAdaptiveShadowStatus.CalibrationCandidate &&
                estimate.TargetVideoBitrateKbps > 0 && sourceVideoKbps is > 0 && estimate.TargetVideoBitrateKbps > sourceVideoKbps)
            {
                status = SourceAdaptiveShadowStatus.PredictedExpansion;
                reason = "The existing heuristic predicts output video bitrate above measured source video bitrate; this is diagnostic only.";
            }
            else if (status == SourceAdaptiveShadowStatus.CalibrationCandidate && estimate.EstimatedOutputMb <= 0)
            {
                status = SourceAdaptiveShadowStatus.PredictionUnavailable;
                reason = "The existing estimator could not produce a complete output-size prediction.";
            }
        }
        else if (status == SourceAdaptiveShadowStatus.CalibrationCandidate)
        {
            status = SourceAdaptiveShadowStatus.PredictionUnavailable;
            reason = "Source size, duration, or geometry needed by the existing predictor is unavailable.";
        }

        double? predictedTotalBytes = estimate?.EstimatedOutputMb is > 0
            ? estimate.EstimatedOutputMb * 1048576d
            : null;
        double? sourceTotalBytes = context.Source.SizeBytes is > 0 ? context.Source.SizeBytes.Value : null;
        return new SourceAdaptiveShadowCalibration
        {
            Status = status,
            EligibilityReason = reason,
            SourceCodec = video?.CodecName ?? "",
            OutputCodec = context.Encoder.FfmpegCodec,
            EncoderId = context.Encoder.EncoderId,
            Preset = context.EncoderPreset,
            QualityTarget = quality.Intent.Target,
            InitialCq = quality.EffectiveQuality,
            FinalExecutionCq = quality.EffectiveQuality,
            SourceVideoBitrateKbps = sourceVideoKbps,
            SourceBitrateProvenance = sourceVideoKbps is > 0 ? "Measured FFprobe video stream bit_rate" : "Unavailable; total bitrate is not substituted",
            SourceTotalBitrateKbps = context.Source.BitRate is > 0 ? context.Source.BitRate.Value / 1000d : null,
            PredictedOutputVideoBitrateKbps = estimate?.TargetVideoBitrateKbps > 0 ? estimate.TargetVideoBitrateKbps : null,
            PredictedTotalOutputBytes = predictedTotalBytes is > 0 && double.IsFinite(predictedTotalBytes.Value) ? (long)Math.Round(predictedTotalBytes.Value) : null,
            PredictedVideoRatio = SourceAdaptiveShadowOutcome.SafeRatio(estimate?.TargetVideoBitrateKbps, sourceVideoKbps),
            PredictedTotalRatio = SourceAdaptiveShadowOutcome.SafeRatio(predictedTotalBytes, sourceTotalBytes),
            SourceTotalBytes = context.Source.SizeBytes,
            PlannedWidth = geometry?.Width,
            PlannedHeight = geometry?.Height,
            PlannedFps = sourceFps,
            MaterialTransformationActive = transformed,
            ShadowReason = status == SourceAdaptiveShadowStatus.PredictedExpansion
                ? "Predicted expansion is a calibration label only; execution adjustment: none."
                : "Shadow mode only; execution adjustment: none."
        };
    }

    private static bool HasMaterialTransform(EncodingDecisionContext context, VideoOutputGeometryPlan? geometry)
    {
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(context.Restoration, context.ScaleMode);
        return context.SourceHealth?.Type == EncodingSourceFailureType.TimelineCorruption ||
               plan.UsesAi || !string.IsNullOrWhiteSpace(plan.ConventionalFilterChain) ||
               !string.IsNullOrWhiteSpace(plan.PreAiFilterChain) || !string.IsNullOrWhiteSpace(plan.PostAiFilterChain) ||
               context.ScaleMode != EncodingService.ScaleMode.None || geometry?.WasNormalized == true;
    }

    private static bool IsCodec(string? codec, string expected) =>
        string.Equals(codec, expected, StringComparison.OrdinalIgnoreCase) ||
        (expected == "h264" && (codec?.Contains("avc", StringComparison.OrdinalIgnoreCase) ?? false));
}
