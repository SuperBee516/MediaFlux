using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record AdaptiveVideoSettings(VideoEncoderSelection Encoder, bool UseGpu, string Preset, bool TenBit,
    bool ConcurrentEncoderSessions, VideoOutputGeometryPlan Geometry, string SourcePixelFormat,
    EncodingService.ScaleMode ScaleMode, OutputContainer Container = OutputContainer.Matroska,
    string SourceColorRange = "", string SourceColorSpace = "", string SourceColorTransfer = "", string SourceColorPrimaries = "");
public sealed record AdaptiveQualitySelectionRequest(StorageSavingsContract Contract, AdaptiveQualityEnvelope Envelope,
    EncodingInputSource Input, TimeSpan SourceDuration, AdaptiveVideoSettings Video, AdaptiveAncillaryAllowance Ancillary)
{
    /// <summary>Source video start relative to the input/container start, when both probe facts are valid.</summary>
    public double? SourceVideoStartOffsetSeconds { get; init; }

    /// <summary>Source video end relative to the input/container start, when both probe facts are valid.</summary>
    public double? SourceVideoEndOffsetSeconds { get; init; }
}

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
    // For a nine-region source, gather the seven preregistered positions first.
    // Positions 4 and 6 remain required whenever those seven do not prove a positive.
    private static readonly int[] NineRegionMeasurementOrder = [0, 1, 2, 4, 6, 7, 8, 3, 5];

    private readonly IAdaptiveVideoSampleRunner _runner;
    public StorageSavingsSampleSelector(IAdaptiveVideoSampleRunner runner) => _runner = runner;

    internal static AdaptiveQualitySelectionEvidence PreservePreferredQuality(
        AdaptiveQualitySelectionRequest request,
        string reason) =>
        Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, [],
            "Adaptive optimization is unavailable; preserve preferred quality and use actual-byte validation. " + reason);

    internal static AdaptiveQualitySelectionEvidence PreserveConfiguredQualityAfterSkip(
        AdaptiveQualitySelectionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence.Disposition != AdaptiveSelectionDisposition.Skipped
            ? evidence
            : evidence with
            {
                Disposition = AdaptiveSelectionDisposition.NotSuitable,
                SelectedQuality = evidence.PreferredQuality,
                Reason = "Adaptive projection did not select a candidate; preserve configured quality and use actual-byte validation. " + evidence.Reason
            };
    }

    public async Task<AdaptiveQualitySelectionEvidence> SelectAsync(AdaptiveQualitySelectionRequest request,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Contract.Applies || request.Contract.MaximumAcceptedOutputBytes is not > 0)
            throw new InvalidOperationException("Selection requires a resolved applicable storage contract.");
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RepresentativeSample> positions = Positions(request.SourceDuration);
        int[] measurementOrder = positions.Count == NineRegionMeasurementOrder.Length
            ? NineRegionMeasurementOrder
            : Enumerable.Range(0, positions.Count).ToArray();
        IReadOnlyList<int> candidateQualities = AdaptiveQualityEnvelopeService.Candidates(request.Envelope);
        var evidence = new List<AdaptiveCandidateEvidence>();
        int samplesExecuted = 0;
        foreach (int quality in candidateQualities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"Testing compression… {(request.Envelope.Mechanism == EncoderQualityMechanism.Cq ? "CQ" : "CRF")} {quality}");
            var samplesByPosition = new RepresentativeSampleEvidence?[positions.Count];
            for (int measurementIndex = 0; measurementIndex < measurementOrder.Length; measurementIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int positionIndex = measurementOrder[measurementIndex];
                RepresentativeSample position = positions[positionIndex];
                RepresentativeSampleEvidence measured;
                try
                {
                    measured = await _runner.MeasureAsync(request, quality, position, cancellationToken).ConfigureAwait(false);
                    samplesExecuted++;
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
                        "Sample optimization is unavailable; preserve preferred quality and use actual-byte validation. " + ex.Message,
                        samplesExecuted);
                }
                if (measured.VideoBytes <= 0 || !double.IsFinite(measured.MeasuredSeconds) || measured.MeasuredSeconds <= 0)
                    return Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, evidence,
                        "Sample evidence has no usable bytes/duration; preserve preferred quality and use actual-byte validation.", samplesExecuted);
                samplesByPosition[positionIndex] = measured;

                if (positions.Count == 9 && measurementIndex == 6)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RepresentativeSampleEvidence[] sevenSamples = samplesByPosition
                        .Where(sample => sample is not null).Select(sample => sample!).ToArray();
                    AdaptiveCandidateEvidence sevenCandidate = ProjectCandidate(request, quality, sevenSamples);
                    if (sevenCandidate.Classification == AdaptiveSampleClassification.ClearlyMeets)
                    {
                        evidence.Add(sevenCandidate);
                        return Result(request, AdaptiveSelectionDisposition.Selected, quality, evidence,
                            "Seven-first production samples clearly meet the projected storage objective; positions 4 and 6 were omitted. Actual output-byte validation remains authoritative.",
                            samplesExecuted, sevenFirstEarlyPositive: true);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            RepresentativeSampleEvidence[] samples = samplesByPosition
                .Select(sample => sample ?? throw new InvalidDataException("A complete production sample set is required for full-nine selection."))
                .ToArray();
            AdaptiveCandidateEvidence candidate = ProjectCandidate(request, quality, samples);
            evidence.Add(candidate);
            if (candidate.Classification == AdaptiveSampleClassification.ClearlyMeets)
                return Result(request, AdaptiveSelectionDisposition.Selected, quality, evidence,
                    "Selected the highest-quality tested candidate clearly meeting the projected storage objective.", samplesExecuted);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Uncertain evidence may justify one guarded attempt when its typical
        // projection meets. The easiest scene alone is insufficient evidence.
        AdaptiveCandidateEvidence? borderline = request.Contract.AllowRiskyBorderlineProduction
            ? evidence.FirstOrDefault(e => IsEligibleBorderlineByTypical(e, request.Contract.MaximumAcceptedOutputBytes.Value))
            : evidence.FirstOrDefault(e => IsEligibleBorderline(e, request.Contract.MaximumAcceptedOutputBytes.Value));
        if (borderline is not null)
            return Result(request, AdaptiveSelectionDisposition.Selected, borderline.Quality, evidence,
                request.Contract.AllowRiskyBorderlineProduction && IsRiskyBorderline(borderline, request.Contract.MaximumAcceptedOutputBytes.Value)
                    ? "Explicit policy allows one full encode for a borderline candidate whose upper projection exceeds the safe range; actual-byte validation remains authoritative."
                    : "Sample evidence is borderline but within the safe upper-projection range; attempt one full encode at the highest-quality eligible candidate, protected by actual-byte validation.", samplesExecuted);

        bool allCandidatesRejected = evidence.All(e => IsReliablyRejectedCandidate(e,
            request.Contract.MaximumAcceptedOutputBytes.Value, request.Contract.AllowRiskyBorderlineProduction));
        string reason = evidence.All(e => e.Classification == AdaptiveSampleClassification.ClearlyMisses)
            ? "Every tested acceptable candidate clearly misses the projected storage objective."
            : allCandidatesRejected
                ? "Every tested acceptable candidate is materially risky or projects above the storage objective."
                : "No tested acceptable candidate has a typical temporal projection meeting the storage objective; an optimistic minimum scene rate does not justify a full encode.";
        AdaptiveQualitySelectionEvidence allMiss = Result(request, AdaptiveSelectionDisposition.Skipped, null, evidence,
            reason + " Reject before production encoding only when complete production samples confirm every candidate is inadmissible.", samplesExecuted);
        if (HasReliableAllCandidateStorageRisk(allMiss, request))
            return allMiss;

        return Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, evidence,
            "Adaptive evidence is incomplete or inconclusive; preserve configured quality and use actual-byte validation. " + reason, samplesExecuted);
    }

    internal static bool HasReliableAllCandidateStorageMiss(
        AdaptiveQualitySelectionEvidence selection,
        AdaptiveQualitySelectionRequest request)
        => HasReliableAllCandidateStorageRisk(selection, request, requireClearMiss: true);

    internal static bool HasReliableAllCandidateStorageRisk(
        AdaptiveQualitySelectionEvidence selection,
        AdaptiveQualitySelectionRequest request) =>
        HasReliableAllCandidateStorageRisk(selection, request, requireClearMiss: false);

    private static bool HasReliableAllCandidateStorageRisk(
        AdaptiveQualitySelectionEvidence selection,
        AdaptiveQualitySelectionRequest request,
        bool requireClearMiss)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);

        if (selection.Disposition != AdaptiveSelectionDisposition.Skipped ||
            selection.SelectedQuality is not null ||
            !request.Contract.Applies || request.Contract.MaximumAcceptedOutputBytes is not > 0 ||
            selection.EncoderId != request.Envelope.Encoder.EncoderId ||
            !string.Equals(selection.Codec, request.Envelope.Encoder.FfmpegCodec, StringComparison.OrdinalIgnoreCase) ||
            selection.Target != request.Envelope.Target || selection.Mechanism != request.Envelope.Mechanism ||
            selection.PreferredQuality != request.Envelope.PreferredQuality ||
            selection.WorstAcceptableQuality != request.Envelope.MaximumCompressionQuality)
            return false;

        IReadOnlyList<int> acceptableQualities = AdaptiveQualityEnvelopeService.Candidates(request.Envelope);
        IReadOnlyList<RepresentativeSample> positions = Positions(request.SourceDuration);
        if (acceptableQualities.Count == 0 || positions.Count == 0 ||
            selection.Candidates is null || selection.Candidates.Count != acceptableQualities.Count)
            return false;

        long maximumBytes = request.Contract.MaximumAcceptedOutputBytes.Value;
        for (int candidateIndex = 0; candidateIndex < acceptableQualities.Count; candidateIndex++)
        {
            AdaptiveCandidateEvidence candidate = selection.Candidates[candidateIndex];
            if (candidate is null || candidate.Quality != acceptableQualities[candidateIndex] ||
                !IsReliablyRejectedCandidate(candidate, maximumBytes, request.Contract.AllowRiskyBorderlineProduction) ||
                requireClearMiss && (candidate.Classification != AdaptiveSampleClassification.ClearlyMisses ||
                    !double.IsFinite(candidate.ProjectedLowerBytes) || candidate.ProjectedLowerBytes <= maximumBytes) ||
                candidate.Samples is null || candidate.Samples.Count != positions.Count)
                return false;

            for (int sampleIndex = 0; sampleIndex < positions.Count; sampleIndex++)
            {
                RepresentativeSampleEvidence sample = candidate.Samples[sampleIndex];
                if (sample is null || sample.Purpose != VideoSamplePurpose.ProductionAcceptance ||
                    sample.Sample is null || sample.Sample != positions[sampleIndex] || sample.VideoBytes <= 0 ||
                    !double.IsFinite(sample.MeasuredSeconds) || sample.MeasuredSeconds <= 0)
                    return false;
            }
        }

        return true;
    }

    internal static bool IsEligibleBorderline(AdaptiveCandidateEvidence candidate, long maximumBytes) =>
        IsEligibleBorderlineByTypical(candidate, maximumBytes) && !IsRiskyBorderline(candidate, maximumBytes);

    internal static bool IsEligibleBorderlineByTypical(AdaptiveCandidateEvidence candidate, long maximumBytes) =>
        IsEligibleBorderline(candidate.Classification, candidate.ProjectedTypicalBytes, maximumBytes);

    internal static bool IsRiskyBorderline(AdaptiveCandidateEvidence candidate, long maximumBytes) =>
        candidate.Classification == AdaptiveSampleClassification.Borderline &&
        double.IsFinite(candidate.ProjectedUpperBytes) &&
        candidate.ProjectedUpperBytes > maximumBytes * 1.10d;

    private static bool IsReliablyRejectedCandidate(AdaptiveCandidateEvidence candidate, long maximumBytes, bool allowRiskyBorderline) =>
        (candidate.Classification == AdaptiveSampleClassification.ClearlyMisses &&
         double.IsFinite(candidate.ProjectedLowerBytes) && candidate.ProjectedLowerBytes > maximumBytes) ||
        (!allowRiskyBorderline && candidate.Classification == AdaptiveSampleClassification.Borderline &&
         (IsRiskyBorderline(candidate, maximumBytes) ||
          candidate.ProjectedTypicalBytes is { } typical && double.IsFinite(typical) && typical > maximumBytes));

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
        AdaptiveSelectionDisposition disposition, int? selected, List<AdaptiveCandidateEvidence> candidates, string reason,
        int? samplesExecuted = null, bool sevenFirstEarlyPositive = false) =>
        new(disposition, request.Envelope.Encoder.EncoderId, request.Envelope.Encoder.FfmpegCodec, request.Envelope.Target,
            request.Envelope.Mechanism, request.Envelope.PreferredQuality, selected, request.Envelope.MaximumCompressionQuality,
            request.Envelope.MaximumIncrease, request.Envelope.AbsoluteCap, request.Envelope.PreferredAlreadyAboveCap,
            request.Ancillary, Array.AsReadOnly(candidates.ToArray()), reason)
            {
                SourceDurationSeconds = request.SourceDuration.TotalSeconds,
                SamplesExecuted = samplesExecuted ?? candidates.Sum(candidate => candidate.Samples.Count),
                SevenFirstEarlyPositive = sevenFirstEarlyPositive
            };

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
        yield return $"[AdaptiveStorageSavings] samplesExecuted={selection.SamplesExecuted}; sevenFirstEarlyPositive={selection.SevenFirstEarlyPositive}; selected={selection.SelectedQuality}; disposition={selection.Disposition}; {selection.Reason}";
    }
}
