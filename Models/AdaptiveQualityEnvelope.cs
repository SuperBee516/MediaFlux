namespace MediaFlux.Models;

/// <summary>
/// Immutable bounds for a future sample-based quality search. The upper bound
/// must be supplied by an explicit product policy; an encoder's legal maximum
/// is not evidence of acceptable visual quality. This model does not activate
/// adaptive encoding or authorize output acceptance.
/// </summary>
public sealed class AdaptiveQualityEnvelope
{
    internal AdaptiveQualityEnvelope(
        VideoEncoderSelection encoder,
        QualityTarget target,
        EncoderQualityMechanism mechanism,
        int preferredQuality,
        int maximumCompressionQuality,
        int? maximumIncrease = null,
        int? absoluteCap = null)
    {
        Encoder = encoder;
        Target = target;
        Mechanism = mechanism;
        PreferredQuality = preferredQuality;
        MaximumCompressionQuality = maximumCompressionQuality;
        MaximumIncrease = maximumIncrease;
        AbsoluteCap = absoluteCap;
    }

    public VideoEncoderSelection Encoder { get; }
    public QualityTarget Target { get; }
    public EncoderQualityMechanism Mechanism { get; }
    public int PreferredQuality { get; }
    public int MaximumCompressionQuality { get; }
    public int? MaximumIncrease { get; }
    public int? AbsoluteCap { get; }
    // The cap bounds additional degradation; it never changes Source Adaptive intent.
    public bool PreferredAlreadyAboveCap => AbsoluteCap.HasValue && PreferredQuality > AbsoluteCap.Value;

    /// <summary>
    /// CQ/CRF candidates may increase compression from the preferred value up
    /// to and including the explicit worst acceptable value.
    /// </summary>
    public bool ContainsQuality(int quality) =>
        quality >= PreferredQuality && quality <= MaximumCompressionQuality;
}
