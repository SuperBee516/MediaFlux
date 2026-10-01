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
/// lower/upper relationship, without invented uncertainty percentages. Phase 1
/// alone accepts actual full-file bytes.
/// </summary>
public sealed class StorageSavingsSampleSelector
{
    private readonly IAdaptiveVideoSampleRunner _runner;
    public StorageSavingsSampleSelector(IAdaptiveVideoSampleRunner runner) => _runner = runner;

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
                try { measured = await _runner.MeasureAsync(request, quality, position, cancellationToken).ConfigureAwait(false); }
                catch (InvalidDataException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, evidence,
                        "Sample evidence is unavailable; preserve preferred quality and use actual-byte validation. " + ex.Message);
                }
                if (measured.VideoBytes <= 0 || !double.IsFinite(measured.MeasuredSeconds) || measured.MeasuredSeconds <= 0)
                    return Result(request, AdaptiveSelectionDisposition.NotSuitable, request.Envelope.PreferredQuality, evidence,
                        "Sample evidence has no usable bytes/duration; preserve preferred quality and use actual-byte validation.");
                samples.Add(measured);
            }
            cancellationToken.ThrowIfCancellationRequested();
            double duration = request.SourceDuration.TotalSeconds;
            double lower = samples.Min(s => s.VideoBytes / s.MeasuredSeconds) * duration + request.Ancillary.MinimumStreamBytes;
            double upper = samples.Max(s => s.VideoBytes / s.MeasuredSeconds) * duration + request.Ancillary.StreamBytes;
            // Reuse the existing advisory container budget for the upper projection.
            // It is not a proven minimum; no assumed mux overhead creates a miss.
            double withContainer = SizeEstimateService.CalculateTargetTotalBitrateKbps(upper * 8 / duration / 1000, 0) * duration * 1000 / 8;
            double upperOverhead = withContainer - upper;
            upper = withContainer;
            if (!double.IsFinite(lower) || !double.IsFinite(upper))
                throw new InvalidDataException("Adaptive projection is not finite.");
            long limit = request.Contract.MaximumAcceptedOutputBytes.Value;
            AdaptiveSampleClassification classification = upper <= limit ? AdaptiveSampleClassification.ClearlyMeets
                : lower > limit ? AdaptiveSampleClassification.ClearlyMisses : AdaptiveSampleClassification.Borderline;
            evidence.Add(new(quality, Array.AsReadOnly(samples.ToArray()), lower, upper, upperOverhead, classification));
            if (classification == AdaptiveSampleClassification.ClearlyMeets)
                return Result(request, AdaptiveSelectionDisposition.Selected, quality, evidence,
                    "Selected the highest-quality tested candidate clearly meeting the projected storage objective.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Uncertain evidence is not a demonstrated miss. Prefer the highest
        // quality borderline candidate and let the actual-byte contract decide.
        AdaptiveCandidateEvidence? borderline = evidence.FirstOrDefault(e => e.Classification == AdaptiveSampleClassification.Borderline);
        return borderline is not null
            ? Result(request, AdaptiveSelectionDisposition.Selected, borderline.Quality, evidence,
                "Sample evidence is borderline; attempt one full encode at the highest-quality borderline candidate, protected by actual-byte validation.")
            : Result(request, AdaptiveSelectionDisposition.Skipped, null, evidence,
                "Every tested acceptable candidate clearly misses the projected storage objective.");
    }

    private static AdaptiveQualitySelectionEvidence Result(AdaptiveQualitySelectionRequest request,
        AdaptiveSelectionDisposition disposition, int? selected, List<AdaptiveCandidateEvidence> candidates, string reason) =>
        new(disposition, request.Envelope.Encoder.EncoderId, request.Envelope.Encoder.FfmpegCodec, request.Envelope.Target,
            request.Envelope.Mechanism, request.Envelope.PreferredQuality, selected, request.Envelope.MaximumCompressionQuality,
            request.Envelope.MaximumIncrease, request.Envelope.AbsoluteCap, request.Envelope.PreferredAlreadyAboveCap,
            request.Ancillary, Array.AsReadOnly(candidates.ToArray()), reason);

    public static IReadOnlyList<RepresentativeSample> Positions(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (duration <= TimeSpan.FromSeconds(25))
            return Array.AsReadOnly(new[] { new RepresentativeSample("Full short video", TimeSpan.Zero, duration) });
        // Tick arithmetic ensures adjacent short clips never overlap through rounding.
        TimeSpan clip = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(25).Ticks, duration.Ticks / 3));
        return Array.AsReadOnly(new[]
        {
            new RepresentativeSample("Beginning", TimeSpan.Zero, clip),
            new RepresentativeSample("Middle", TimeSpan.FromTicks((duration.Ticks - clip.Ticks) / 2), clip),
            new RepresentativeSample("End", duration - clip, clip)
        });
    }
}
