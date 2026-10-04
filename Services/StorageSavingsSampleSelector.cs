using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record AdaptiveVideoSettings(VideoEncoderSelection Encoder, bool UseGpu, string Preset, bool TenBit,
    bool ConcurrentEncoderSessions, VideoOutputGeometryPlan Geometry, string SourcePixelFormat,
    EncodingService.ScaleMode ScaleMode, OutputContainer Container = OutputContainer.Matroska,
    string SourceColorRange = "", string SourceColorSpace = "", string SourceColorTransfer = "", string SourceColorPrimaries = "");
public sealed record AdaptiveQualitySelectionRequest(StorageSavingsContract Contract, AdaptiveQualityEnvelope Envelope,
    EncodingInputSource Input, TimeSpan SourceDuration, AdaptiveVideoSettings Video, AdaptiveAncillaryAllowance Ancillary);

public interface IAdaptiveVideoSampleRunner
{
    Task<RepresentativeSampleEvidence> MeasureAsync(AdaptiveQualitySelectionRequest request, int quality,
        RepresentativeSample sample, CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic initial-quality optimization. Sample-rate spread supplies the
/// lower/upper relationship. Equal temporal strata provide a typical projection;
/// the easiest scene alone cannot justify a borderline full encode. Phase 1
/// alone accepts actual full-file bytes.
/// </summary>
public sealed class StorageSavingsSampleSelector
{
    private readonly IAdaptiveVideoSampleRunner _runner;
    public StorageSavingsSampleSelector(IAdaptiveVideoSampleRunner runner) => _runner = runner;

    internal static AdaptiveQualitySelectionEvidence PreservePreferredQuality(
        AdaptiveQualitySelectionRequest request,
        string reason) =>
        Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, [],
            "Adaptive optimization is unavailable; preserve preferred quality and use actual-byte validation. " + reason);

    public async Task<AdaptiveQualitySelectionEvidence> SelectAsync(AdaptiveQualitySelectionRequest request,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Contract.Applies || request.Contract.MaximumAcceptedOutputBytes is not > 0)
            throw new InvalidOperationException("Selection requires a resolved applicable storage contract.");
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RepresentativeSample> positions = Positions(request.SourceDuration);
        var evidence = new List<AdaptiveCandidateEvidence>();
        foreach (int quality in AdaptiveQualityEnvelopeService.Candidates(request.Envelope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"Testing compression… {(request.Envelope.Mechanism == EncoderQualityMechanism.Cq ? "CQ" : "CRF")} {quality}");
            var samples = new List<RepresentativeSampleEvidence>();
            foreach (RepresentativeSample position in positions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RepresentativeSampleEvidence measured;
                try
                {
                    measured = await _runner.MeasureAsync(request, quality, position, cancellationToken).ConfigureAwait(false);
                    if (measured.Purpose != VideoSamplePurpose.ProductionAcceptance)
                        throw new InvalidDataException("Preview evidence cannot authorize production adaptive selection.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, evidence,
                        "Sample optimization is unavailable; preserve preferred quality and use actual-byte validation. " + ex.Message);
                }
                if (measured.VideoBytes <= 0 || !double.IsFinite(measured.MeasuredSeconds) || measured.MeasuredSeconds <= 0)
                    return Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, evidence,
                        "Sample evidence has no usable bytes/duration; preserve preferred quality and use actual-byte validation.");
                samples.Add(measured);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AdaptiveCandidateEvidence candidate = ProjectCandidate(request, quality, samples);
            evidence.Add(candidate);
            if (candidate.Classification == AdaptiveSampleClassification.ClearlyMeets)
                return Result(request, AdaptiveSelectionDisposition.Selected, quality, evidence,
                    "Selected the highest-quality tested candidate clearly meeting the projected storage objective.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Uncertain evidence may justify one guarded attempt when its typical
        // projection meets. The easiest scene alone is insufficient evidence.
        AdaptiveCandidateEvidence? borderline = evidence.FirstOrDefault(e =>
            IsEligibleBorderline(e, request.Contract.MaximumAcceptedOutputBytes.Value));
        return borderline is not null
            ? Result(request, AdaptiveSelectionDisposition.Selected, borderline.Quality, evidence,
                "Sample evidence is borderline and its typical temporal projection meets the objective; attempt one full encode at the highest-quality eligible candidate, protected by actual-byte validation.")
            : Result(request, AdaptiveSelectionDisposition.Skipped, null, evidence,
                evidence.All(e => e.Classification == AdaptiveSampleClassification.ClearlyMisses)
                    ? "Every tested acceptable candidate clearly misses the projected storage objective."
                    : "No tested acceptable candidate has a typical temporal projection meeting the storage objective; an optimistic minimum scene rate does not justify a full encode.");
    }

    internal static bool IsEligibleBorderline(AdaptiveCandidateEvidence candidate, long maximumBytes) =>
        IsEligibleBorderline(candidate.Classification, candidate.ProjectedTypicalBytes, maximumBytes);

    internal static bool IsEligibleBorderline(AdaptiveSampleClassification classification, double? typical, long maximumBytes) =>
        classification == AdaptiveSampleClassification.Borderline &&
        typical is { } value && double.IsFinite(value) && value <= maximumBytes;

    internal static AdaptiveCandidateEvidence ProjectCandidate(AdaptiveQualitySelectionRequest request, int quality,
        IReadOnlyList<RepresentativeSampleEvidence> samples)
    {
            if (samples.Any(s => s.Purpose != VideoSamplePurpose.ProductionAcceptance))
                throw new InvalidDataException("Preview evidence cannot become production candidate evidence.");
            double[] rates = samples.Select(s => s.VideoBytes / s.MeasuredSeconds).ToArray();
            var projection = ProjectRates(request.SourceDuration.TotalSeconds, request.Ancillary,
                rates.Min(), rates.Average(), rates.Max(), request.Contract.MaximumAcceptedOutputBytes);
            return new(quality, Array.AsReadOnly(samples.ToArray()), projection.Lower, projection.Upper,
                projection.Container, projection.Classification) { ProjectedTypicalBytes = projection.Typical };
    }

    internal static (double Lower, double Typical, double Upper, double Container, AdaptiveSampleClassification Classification)
        ProjectRates(double duration, AdaptiveAncillaryAllowance ancillary, double minimum, double mean, double maximum, long? limit)
    {
            double lower = minimum * duration + ancillary.MinimumStreamBytes;
            double upper = maximum * duration + ancillary.StreamBytes;
            // Reuse the existing advisory container budget for the upper projection.
            // It is not a proven minimum; no assumed mux overhead creates a miss.
            double withContainer = SizeEstimateService.CalculateTargetTotalBitrateKbps(upper * 8 / duration / 1000, 0) * duration * 1000 / 8;
            double upperOverhead = withContainer - upper;
            upper = withContainer;
            // Positions sample equal temporal strata. Each rate represents the same
            // share of the source, even when the bounded clip covers only part of it.
            double typical = mean * duration + ancillary.StreamBytes;
            typical = SizeEstimateService.CalculateTargetTotalBitrateKbps(typical * 8 / duration / 1000, 0) * duration * 1000 / 8;
            if (!double.IsFinite(lower) || !double.IsFinite(upper) || !double.IsFinite(typical))
                throw new InvalidDataException("Adaptive projection is not finite.");
            AdaptiveSampleClassification classification = upper <= limit ? AdaptiveSampleClassification.ClearlyMeets
                : lower > limit ? AdaptiveSampleClassification.ClearlyMisses : AdaptiveSampleClassification.Borderline;
            return (lower, typical, upper, upperOverhead, classification);
    }

    private static AdaptiveQualitySelectionEvidence Result(AdaptiveQualitySelectionRequest request,
        AdaptiveSelectionDisposition disposition, int? selected, List<AdaptiveCandidateEvidence> candidates, string reason) =>
        new(disposition, request.Envelope.Encoder.EncoderId, request.Envelope.Encoder.FfmpegCodec, request.Envelope.Target,
            request.Envelope.Mechanism, request.Envelope.PreferredQuality, selected, request.Envelope.MaximumCompressionQuality,
            request.Envelope.MaximumIncrease, request.Envelope.AbsoluteCap, request.Envelope.PreferredAlreadyAboveCap,
            request.Ancillary, Array.AsReadOnly(candidates.ToArray()), reason)
            { SourceDurationSeconds = request.SourceDuration.TotalSeconds };

    public static IReadOnlyList<RepresentativeSample> Positions(TimeSpan duration) =>
        VideoSamplingProfile.Production.Positions(duration);

    public static IEnumerable<string> DescribeDiagnostics(AdaptiveQualitySelectionEvidence selection, double sourceSeconds, long maximumBytes)
    {
        double projectionSeconds = selection.SourceDurationSeconds ?? sourceSeconds;
        yield return FormattableString.Invariant($"[AdaptiveStorageSavings] mechanism={selection.Mechanism}; sourceSeconds={projectionSeconds:F6}; maximumBytes={maximumBytes}; ancillaryMinimumBytes={selection.Ancillary?.MinimumStreamBytes:F0}; ancillaryExpectedBytes={selection.Ancillary?.StreamBytes:F0}.");
        foreach (AdaptiveCandidateEvidence candidate in selection.Candidates)
        {
            foreach (RepresentativeSampleEvidence sample in candidate.Samples)
                yield return FormattableString.Invariant($"[AdaptiveStorageSavings] quality={candidate.Quality}; startSeconds={sample.Sample.Start.TotalSeconds:F6}; requestedSeconds={sample.Sample.Duration.TotalSeconds:F6}; measuredSeconds={sample.MeasuredSeconds:F6}; videoPayloadBytes={sample.VideoBytes}; bytesPerSecond={sample.VideoBytes / sample.MeasuredSeconds:F3}.");
            yield return FormattableString.Invariant($"[AdaptiveStorageSavings] quality={candidate.Quality}; sampledSeconds={candidate.Samples.Sum(s => s.MeasuredSeconds):F6}; minRate={candidate.Samples.Min(s => s.VideoBytes / s.MeasuredSeconds):F3}; maxRate={candidate.Samples.Max(s => s.VideoBytes / s.MeasuredSeconds):F3}; lowerBytes={candidate.ProjectedLowerBytes:F0}; typicalBytes={candidate.ProjectedTypicalBytes:F0}; upperBytes={candidate.ProjectedUpperBytes:F0}; containerUpperBytes={candidate.ContainerAllowanceBytes:F0}; maximumBytes={maximumBytes}; classification={candidate.Classification}.");
        }
        yield return $"[AdaptiveStorageSavings] selected={selection.SelectedQuality}; disposition={selection.Disposition}; {selection.Reason}";
    }
}
