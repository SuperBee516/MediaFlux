using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>
/// Resolves Automatic quality targets to one deterministic provider-normalized
/// constant-quality value. Source measurements and output geometry do not alter
/// the selected target; FFmpeg providers remain the authority for legal ranges
/// and command syntax.
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

        if (request.Intent.Kind == EncodingQualityIntentKind.AutomaticTargetBitrate)
        {
            if (request.Intent.Target is not { } bitrateTarget)
                throw new InvalidOperationException("Automatic target-bitrate intent requires a selected quality target.");
            reasons.Add(new(EncodingQualityReasonCode.AutomaticTargetBitrate,
                $"The {bitrateTarget} segment controls a metadata-derived VBR target; constant-quality parameters are not used."));
            return new(request.Intent, null, EncoderQualityMechanism.Unknown,
                EncodingQualityAssessment.Unknown, true, reasons);
        }

        if (request.Intent.Target is not { } target)
            throw new InvalidOperationException("Quality-target intent requires a selected target.");

        int requested = ResolveAutomaticQuality(request.Encoder, target);
        reasons.Add(new(EncodingQualityReasonCode.QualityTargetSelected,
            $"Quality target {target} was selected."));
        reasons.Add(new(EncodingQualityReasonCode.ProviderDefaultBaseline,
            "The selected encoder's existing default quality is the target-mapping baseline."));

        int effective = provider.NormalizeQuality(request.Encoder.CodecFamily, requested);
        if (effective != requested)
            reasons.Add(new(EncodingQualityReasonCode.ProviderNormalized,
                "The encoder provider normalized the selected target to its supported range."));
        return new(request.Intent, effective, mechanism, EncodingQualityAssessment.Unknown, false, reasons);
    }

    public static int ResolveAutomaticQuality(VideoEncoderSelection encoder, QualityTarget target)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(encoder.EncoderId, encoder.CodecFamily);
        int baseline = resolved.Provider.NormalizeQuality(encoder.CodecFamily, null);
        return resolved.Provider.NormalizeQuality(encoder.CodecFamily, baseline + TargetOffset(target));
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
