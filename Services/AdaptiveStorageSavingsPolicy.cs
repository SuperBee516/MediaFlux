using MediaFlux.Models;

namespace MediaFlux.Services;

public static class AdaptiveStorageSavingsPolicy
{
    public const string PendingEstimate = "Estimate pending adaptive quality selection";

    public static bool IsPotentiallyApplicable(bool phase1Applies, EncodingInputSource input,
        VideoEncoderSelection encoder, EncodingQualityIntent? intent, double? targetMb,
        VideoRestorationSettings restoration, bool isSample = false) =>
        phase1Applies && input.Kind == EncodingInputKind.File && !isSample && targetMb is not > 0 &&
        intent is { Kind: EncodingQualityIntentKind.QualityTarget } && encoder.CodecFamily == VideoCodecFamily.Hevc &&
        (encoder.EncoderId == VideoEncoderIds.Nvenc || encoder.EncoderId == VideoEncoderIds.Libx265) &&
        VideoRestorationModeResolver.Resolve(restoration) is { Mode: VideoRestorationMode.Off, AiMode: AiRestorationMode.Off };

    internal static bool TryCreateRequest(bool enabled, bool concurrentEncoderSessions, StorageSavingsContract contract,
        EncodingDecisionContext context, EncodingPlan plan, out AdaptiveQualitySelectionRequest? request, out string reason)
    {
        request = null;
        reason = "This operation is outside Phase 2 applicability.";
        if (!enabled || !IsPotentiallyApplicable(contract.Applies, context.Input, context.Encoder,
            context.QualityIntent, context.TargetMb, context.Restoration,
            context.ValidationProfile != EncodeOutputValidationProfile.Production)) return false;
        MediaProbeStreamInfo? video = context.Source.Streams.FirstOrDefault(s => s.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        if (video is null || !(video.CodecName.Equals("h264", StringComparison.OrdinalIgnoreCase) || video.CodecName.Equals("avc", StringComparison.OrdinalIgnoreCase))) return false;
        var execution = EncodingPlanService.GetExecutionValues(plan);
        VideoOutputGeometryPlan? geometry = execution.Geometry;
        if (geometry is null || geometry.RequestedWidth > geometry.SourceWidth || geometry.RequestedHeight > geometry.SourceHeight)
        { reason = "Upscaling or unknown geometry is unsuitable for Phase 2."; return false; }
        if (context.KnownDuration <= TimeSpan.Zero || contract.MaximumAcceptedOutputBytes is not > 0 || string.IsNullOrWhiteSpace(video.PixelFormat))
        { reason = "Duration, pixel format, or the storage byte limit is unresolved."; return false; }
        if (!TryAncillaryAllowance(context.Source, execution.ContainerDecision, execution.AudioChannels,
            context.KnownDuration.TotalSeconds, out AdaptiveAncillaryAllowance? allowance, out reason)) return false;
        AdaptiveQualityEnvelope envelope = AdaptiveQualityEnvelopeService.Resolve(execution.QualityResolution, execution.Encoder);
        request = new(contract, envelope, context.Input, context.KnownDuration,
            new(execution.Encoder, execution.UseGpu, context.EncoderPreset, context.TenBit,
                concurrentEncoderSessions, geometry, video.PixelFormat, context.ScaleMode, execution.ContainerDecision.Resolved,
                video.ColorRange, video.ColorSpace, video.ColorTransfer, video.ColorPrimaries), allowance!);
        reason = "Applicable AVC to HEVC automatic storage-savings job.";
        return true;
    }

    public static bool TryAncillaryAllowance(MediaProbeResult source, OutputContainerDecision container,
        int? audioChannels, double durationSeconds, out AdaptiveAncillaryAllowance? allowance, out string reason)
    {
        allowance = null;
        reason = "Mapped stream sizes are not sufficiently known for adaptive selection.";
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return false;
        double audio = 0, subtitles = 0, data = 0;
        long attachments = 0;
        foreach (StreamCompatibilityPlan action in container.StreamPlans.Where(p => p.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode))
        {
            MediaProbeStreamInfo? stream = source.Streams.FirstOrDefault(s => s.Index == action.StreamIndex);
            if (stream is null) return false;
            switch (action.StreamType.ToLowerInvariant())
            {
                case "audio":
                    if (audioChannels is > 0 || action.Action == StreamCompatibilityAction.Transcode && action.TargetCodec == "aac")
                        audio += (audioChannels is >= 6 ? 384_000d : 192_000d) * durationSeconds / 8;
                    else if (action.Action == StreamCompatibilityAction.Copy && stream.BitRate is > 0)
                        audio += stream.BitRate.Value * durationSeconds / 8d;
                    else return false;
                    break;
                case "subtitle":
                    // Existing advisory convention, not a claim of measured subtitle bytes.
                    subtitles += (stream.BitRate is > 0 ? stream.BitRate.Value : 8_000d) * durationSeconds / 8;
                    break;
                case "data":
                    if (!container.CopyDataStreams) break;
                    if (stream.BitRate is not > 0) return false;
                    data += stream.BitRate.Value * durationSeconds / 8d;
                    break;
                case "attachment":
                    if (!container.CopyAttachments) break;
                    if (stream.ExtraDataSizeBytes is not > 0) return false;
                    attachments = checked(attachments + stream.ExtraDataSizeBytes.Value);
                    break;
            }
        }
        allowance = new(audio, subtitles, data, attachments);
        if (!double.IsFinite(allowance.StreamBytes)) return false;
        reason = "Planned audio, subtitle, data, and attachment allowances; container allowance is added per candidate.";
        return true;
    }
}
