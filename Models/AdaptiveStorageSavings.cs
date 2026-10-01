namespace MediaFlux.Models;

public enum AdaptiveSampleClassification { ClearlyMeets, Borderline, ClearlyMisses }
public enum AdaptiveSelectionDisposition { Selected, Skipped, NotSuitable }

public sealed record RepresentativeSample(string Label, TimeSpan Start, TimeSpan Duration);
public sealed record RepresentativeSampleEvidence(RepresentativeSample Sample, long VideoBytes, double MeasuredSeconds);
public sealed record AdaptiveAncillaryAllowance(double AudioBytes, double SubtitleBytes, double DataBytes, long AttachmentBytes)
{
    public double StreamBytes => AudioBytes + SubtitleBytes + DataBytes + AttachmentBytes;
    // Nominal audio/subtitle/data bitrate budgets are not proven minimum bytes.
    // Copied attachment extradata is a measured fixed-size contribution.
    public double MinimumStreamBytes => AttachmentBytes;
}
public sealed record AdaptiveCandidateEvidence(int Quality, IReadOnlyList<RepresentativeSampleEvidence> Samples,
    double ProjectedLowerBytes, double ProjectedUpperBytes, double ContainerAllowanceBytes, AdaptiveSampleClassification Classification);

/// <summary>Sample evidence, never accepted final-output bytes or a completed sample job.</summary>
public sealed record AdaptiveQualitySelectionEvidence(
    AdaptiveSelectionDisposition Disposition, string EncoderId, string Codec, QualityTarget Target,
    EncoderQualityMechanism Mechanism, int PreferredQuality, int? SelectedQuality,
    int WorstAcceptableQuality, int? MaximumIncrease, int? AbsoluteCap, bool PreferredAlreadyAboveCap,
    AdaptiveAncillaryAllowance? Ancillary, IReadOnlyList<AdaptiveCandidateEvidence> Candidates, string Reason)
{
    public double SamplingSeconds { get; init; }
}

public sealed class AdaptiveStorageSavingsSkippedException : InvalidOperationException
{
    public AdaptiveStorageSavingsSkippedException(AdaptiveQualitySelectionEvidence evidence)
        : base("Skipped — insufficient savings at acceptable quality") => Evidence = evidence;
    public AdaptiveQualitySelectionEvidence Evidence { get; }
}
