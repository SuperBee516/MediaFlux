using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>
/// Phase 2 product policy: additional degradation budgets and absolute caps
/// are provider-specific. Existing Source Adaptive preferred quality is retained.
/// </summary>
public static class AdaptiveQualityEnvelopeService
{
    public static AdaptiveQualityEnvelope Resolve(EncodingQualityResolution preferred, VideoEncoderSelection encoder)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(encoder);
        if (encoder.CodecFamily != VideoCodecFamily.Hevc ||
            (encoder.EncoderId != VideoEncoderIds.Nvenc && encoder.EncoderId != VideoEncoderIds.Libx265))
            throw new InvalidOperationException("Phase 2 supports only NVENC HEVC CQ and libx265 CRF.");
        var (increase, nvencCap, softwareCap) = preferred.Intent.Target switch
        {
            QualityTarget.SmallerFile => (4, 34, 32),
            QualityTarget.Efficient => (3, 31, 30),
            QualityTarget.Balanced => (3, 29, 28),
            QualityTarget.HighQuality => (2, 25, 24),
            QualityTarget.MaximumQuality => (1, 22, 21),
            _ => throw new InvalidOperationException("A defined automatic quality target is required.")
        };
        int quality = preferred.EffectiveQuality ?? throw new InvalidOperationException("Preferred quality is unresolved.");
        int cap = encoder.EncoderId == VideoEncoderIds.Nvenc ? nvencCap : softwareCap;
        // Preserve a preferred value already beyond the cap, without redefining
        // that cap. Such jobs have zero additional Phase 2 degradation budget.
        int worst = quality > cap ? quality : Math.Min(checked(quality + increase), cap);
        AdaptiveQualityEnvelope validated = Create(preferred, encoder, worst);
        return new(validated.Encoder, validated.Target, validated.Mechanism, quality, worst, increase, cap);
    }

    public static IReadOnlyList<int> Candidates(AdaptiveQualityEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        int first = envelope.PreferredQuality;
        int last = envelope.MaximumCompressionQuality;
        // These approved envelopes are at most four steps wide. At width four,
        // use the midpoint; otherwise evaluate the narrow integer ladder. This
        // avoids a fifth three-scene encode while preserving ascending quality.
        int[] values = last - first >= 4 ? [first, first + 1, first + (last - first) / 2, last]
            : Enumerable.Range(first, last - first + 1).ToArray();
        return Array.AsReadOnly(values);
    }
    public static AdaptiveQualityEnvelope Create(
        EncodingQualityResolution preferred,
        VideoEncoderSelection encoder,
        int maximumCompressionQuality)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(encoder);
        if (preferred.Intent.Kind != EncodingQualityIntentKind.QualityTarget ||
            preferred.Intent.Target is not { } target || !Enum.IsDefined(target) ||
            preferred.IsSupersededByTargetSize || preferred.EffectiveQuality is not { } quality)
            throw new InvalidOperationException(
                "An adaptive envelope requires resolved automatic constant-quality intent.");

        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(
            encoder.EncoderId, encoder.CodecFamily);
        if (!encoder.FfmpegCodec.Equals(resolved.Selection.FfmpegCodec, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encoder codec does not match its provider.");
        EncoderQualityRange range = resolved.Provider.Capabilities.QualityRange
            ?? throw new InvalidOperationException("The encoder has no constant-quality range.");
        EncoderQualityMechanism mechanism = range.Name.Equals("CQ", StringComparison.OrdinalIgnoreCase)
            ? EncoderQualityMechanism.Cq
            : range.Name.Equals("CRF", StringComparison.OrdinalIgnoreCase)
                ? EncoderQualityMechanism.Crf
                : EncoderQualityMechanism.Unknown;
        if (mechanism == EncoderQualityMechanism.Unknown || preferred.Mechanism != mechanism)
            throw new InvalidOperationException("The envelope requires matching CQ or CRF semantics.");
        if (quality < range.Minimum || quality > range.Maximum)
            throw new InvalidOperationException("Preferred quality is outside the encoder's legal range.");
        if (maximumCompressionQuality < quality || maximumCompressionQuality > range.Maximum)
            throw new ArgumentOutOfRangeException(nameof(maximumCompressionQuality),
                "The explicit worst acceptable value must be legal and no lower than preferred quality.");

        return new AdaptiveQualityEnvelope(resolved.Selection, target, mechanism,
            quality, maximumCompressionQuality);
    }
}
