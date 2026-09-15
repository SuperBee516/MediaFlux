using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>
/// The sole source-adaptive constant-quality policy. It resolves user intent
/// from immutable probe and planned-geometry facts; FFmpeg providers remain the
/// final authority for legal backend-specific normalization and syntax.
/// </summary>
public sealed class EncodingQualityPolicyService
{
    public EncodingQualityResolution Resolve(EncodingQualityPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Encoder);
        ArgumentNullException.ThrowIfNull(request.Intent);

        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(
            request.Encoder.EncoderId, request.Encoder.CodecFamily);
        IVideoEncoderProvider provider = resolved.Provider;
        EncoderQualityMechanism mechanism = GetMechanism(provider.Capabilities);
        var reasons = new List<EncodingQualityReason>();

        if (request.TargetMb is > 0)
        {
            reasons.Add(new(EncodingQualityReasonCode.TargetSizeSupersedesQuality,
                "A target-size bitrate plan is configured, so constant quality is not emitted."));
            return new(request.Intent, null, mechanism, EncodingQualityAssessment.Unknown,
                true, reasons);
        }

        if (request.Intent.Kind == EncodingQualityIntentKind.LegacyNumeric)
        {
            int normalized = provider.NormalizeQuality(
                request.Encoder.CodecFamily, request.Intent.LegacyQualityValue);
            reasons.Add(new(EncodingQualityReasonCode.LegacyNumericIntent,
                "Existing numeric quality intent is preserved without source-adaptive adjustment."));
            if (request.Intent.LegacyQualityValue != normalized)
                reasons.Add(new(EncodingQualityReasonCode.ProviderNormalized,
                    "The encoder provider normalized the requested quality to its supported range."));
            return new(request.Intent, normalized, mechanism,
                EncodingQualityAssessment.Unknown, false, reasons);
        }

        if (request.Intent.Target is not { } target)
            throw new InvalidOperationException("Quality-target intent requires a selected target.");

        int baseline = provider.NormalizeQuality(request.Encoder.CodecFamily, null);
        int requested = baseline + TargetOffset(target);
        reasons.Add(new(EncodingQualityReasonCode.QualityTargetSelected,
            $"Quality target {target} was selected."));
        reasons.Add(new(EncodingQualityReasonCode.ProviderDefaultBaseline,
            "The selected encoder's existing default quality is the adaptive policy baseline."));

        MediaProbeStreamInfo? sourceVideo = request.Source.Streams.FirstOrDefault(stream =>
            stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        EncodingQualityAssessment assessment = Assess(sourceVideo);
        switch (assessment)
        {
            case EncodingQualityAssessment.CompressedSource:
                requested += 1;
                reasons.Add(new(EncodingQualityReasonCode.CompressedSource,
                    "Low measured bits-per-pixel indicates an already compressed source."));
                break;
            case EncodingQualityAssessment.HighQualitySource:
                requested -= 2;
                reasons.Add(new(EncodingQualityReasonCode.HighQualitySource,
                    "High measured bits-per-pixel indicates a source worth preserving more closely."));
                break;
        }

        if (sourceVideo?.Height is >= 2160)
        {
            requested--;
            reasons.Add(new(EncodingQualityReasonCode.HighResolutionSource,
                "4K-or-higher source resolution receives a conservative preservation adjustment."));
        }

        if (sourceVideo?.Height is > 0 && request.OutputGeometry?.Height is > 0 &&
            request.OutputGeometry.Height < sourceVideo.Height)
        {
            requested += 2;
            reasons.Add(new(EncodingQualityReasonCode.Downscale,
                "Downscaling reduces the required constant-quality preservation setting."));
        }

        int effective = provider.NormalizeQuality(request.Encoder.CodecFamily, requested);
        if (effective != requested)
            reasons.Add(new(EncodingQualityReasonCode.ProviderNormalized,
                "The encoder provider normalized the adaptive result to its supported range."));
        return new(request.Intent, effective, mechanism, assessment, false, reasons);
    }

    private static int TargetOffset(QualityTarget target) => target switch
    {
        QualityTarget.SmallerFile => 6,
        QualityTarget.Efficient => 3,
        QualityTarget.Balanced => 0,
        QualityTarget.HighQuality => -3,
        QualityTarget.MaximumQuality => -5,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private static EncodingQualityAssessment Assess(MediaProbeStreamInfo? source)
    {
        if (source?.Width is not > 0 || source.Height is not > 0 ||
            source.FrameRate is not > 0 || source.BitRate is not > 0)
        {
            return EncodingQualityAssessment.Unknown;
        }

        double bitsPerPixel = source.BitRate.Value /
            ((double)source.Width.Value * source.Height.Value * source.FrameRate.Value);
        return bitsPerPixel switch
        {
            < 0.035 => EncodingQualityAssessment.CompressedSource,
            >= 0.12 => EncodingQualityAssessment.HighQualitySource,
            _ => EncodingQualityAssessment.TypicalSource
        };
    }

    private static EncoderQualityMechanism GetMechanism(EncoderCapabilities capabilities)
    {
        string name = capabilities.QualityRange?.Name ?? string.Empty;
        if (name.Equals("CQ", StringComparison.OrdinalIgnoreCase))
            return EncoderQualityMechanism.Cq;
        if (name.Contains("global", StringComparison.OrdinalIgnoreCase))
            return EncoderQualityMechanism.Icq;
        if (name.Equals("CRF", StringComparison.OrdinalIgnoreCase))
            return EncoderQualityMechanism.Crf;
        return EncoderQualityMechanism.Unknown;
    }
}

public sealed record EncodingQualityPolicyRequest(
    EncodingQualityIntent Intent,
    MediaProbeResult Source,
    VideoEncoderSelection Encoder,
    VideoOutputGeometryPlan? OutputGeometry,
    EncodingService.ScaleMode ScaleMode,
    double? TargetMb);
