using System.Security.Cryptography;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record PreviewSourceBinding(string Path, long Bytes, long WriteTicks)
{
    internal static PreviewSourceBinding Capture(string path) => Capture([path]);

    internal static PreviewSourceBinding Capture(IEnumerable<string> paths)
    {
        var identities = paths.Select(path =>
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0)
                throw new PreviewUnavailableException("Source binding is unavailable.");
            return (Path: file.FullName.ToUpperInvariant(), file.Length, Ticks: file.LastWriteTimeUtc.Ticks);
        }).ToArray();
        if (identities.Length == 0)
            throw new PreviewUnavailableException("Source binding is unavailable.");
        return new(
            string.Join("|", identities.Select(value => $"{value.Path}:{value.Length}:{value.Ticks}")),
            identities.Sum(value => value.Length),
            identities.Aggregate(17L, (current, value) => unchecked(current * 31 + value.Ticks)));
    }
}

/// <summary>Bounded video evidence only. Ancillary streams are projected anew on every request.</summary>
internal sealed class PreviewVideoEvidenceCache(int capacity = 128)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<RepresentativeSampleEvidence>> _entries = new();
    private readonly LinkedList<string> _lru = new();
    internal bool TryGet(string key, out IReadOnlyList<RepresentativeSampleEvidence> samples)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out samples!)) return false;
            _lru.Remove(key); _lru.AddLast(key); return true;
        }
    }
    internal void Add(string key, IReadOnlyList<RepresentativeSampleEvidence> samples)
    {
        lock (_sync)
        {
            _entries[key] = Array.AsReadOnly(samples.ToArray());
            _lru.Remove(key); _lru.AddLast(key);
            while (_entries.Count > Math.Max(1, capacity))
            { string oldest = _lru.First!.Value; _lru.RemoveFirst(); _entries.Remove(oldest); }
        }
    }
}

internal sealed class EmpiricalSizePreviewService(PreviewVideoEvidenceCache cache)
{
    internal async Task<EmpiricalPreviewResult> EstimateAsync(AdaptiveQualitySelectionRequest request,
        bool adaptive, IAdaptiveVideoSampleRunner runner, Func<int, string> videoSignature, CancellationToken token,
        ProductionPlanIdentity? productionPlan = null)
    {
        PreviewSourceBinding binding = PreviewSourceBinding.Capture(request.Input.SourcePath);
        IReadOnlyList<RepresentativeSample> initialPositions = VideoSamplingProfile.Preview.Positions(request.SourceDuration);
        IReadOnlyList<RepresentativeSample> expandedPositions = VideoSamplingProfile.Preview.Positions(request.SourceDuration, 5);
        RepresentativeSample[] additionalPositions = expandedPositions.Where(position => !initialPositions.Contains(position)).ToArray();
        int[] qualities = adaptive ? AdaptiveQualityEnvelopeService.Candidates(request.Envelope).ToArray()
            : [request.Envelope.PreferredQuality];
        int[] anchors = qualities.Length > 1 ? [qualities[0], qualities[^1]] : [qualities[0]];
        var measured = new Dictionary<int, double[]>();
        var samplesByQuality = new Dictionary<int, List<RepresentativeSampleEvidence>>();
        int hits = 0, misses = 0;

        string CacheKey(int quality)
        {
            string planFingerprint = productionPlan is null
                ? "unbound"
                : ProductionPlanFingerprintService.WithQuality(productionPlan, quality).PlanFingerprint;
            string identity = $"Preview5sV2|plan={planFingerprint}|{binding}|duration={request.SourceDuration.Ticks}|quality={quality}|{videoSignature(quality)}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        static void Validate(RepresentativeSampleEvidence sample, RepresentativeSample requested)
        {
            if (sample.Purpose != VideoSamplePurpose.PreviewEstimation || sample.Sample != requested ||
                sample.VideoBytes <= 0 || !double.IsFinite(sample.MeasuredSeconds) || sample.MeasuredSeconds <= 0)
                throw new InvalidDataException("Preview video evidence is invalid or has the wrong purpose.");
        }

        async Task<double[]> Measure(int quality)
        {
            if (measured.TryGetValue(quality, out double[]? existingRates))
                return existingRates;
            token.ThrowIfCancellationRequested();
            string key = CacheKey(quality);
            IReadOnlyList<RepresentativeSampleEvidence> samples;
            if (cache.TryGet(key, out samples)) hits++;
            else
            {
                misses++;
                var collected = new List<RepresentativeSampleEvidence>();
                foreach (RepresentativeSample position in initialPositions)
                {
                    var sample = await runner.MeasureAsync(request, quality, position, token).ConfigureAwait(false);
                    Validate(sample, position);
                    collected.Add(sample);
                }
                token.ThrowIfCancellationRequested();
                if (binding != PreviewSourceBinding.Capture(request.Input.SourcePath))
                    throw new PreviewUnavailableException("Source changed during preview sampling.");
                samples = collected.AsReadOnly();
                cache.Add(key, samples);
            }
            samplesByQuality[quality] = samples.ToList();
            return measured[quality] = samples.Select(s => s.VideoBytes / s.MeasuredSeconds).ToArray();
        }

        async Task Extend(int quality)
        {
            await Measure(quality).ConfigureAwait(false);
            List<RepresentativeSampleEvidence> samples = samplesByQuality[quality];
            foreach (RepresentativeSample position in additionalPositions)
            {
                if (samples.Any(sample => sample.Sample == position))
                    continue;
                token.ThrowIfCancellationRequested();
                RepresentativeSampleEvidence sample = await runner.MeasureAsync(request, quality, position, token).ConfigureAwait(false);
                Validate(sample, position);
                samples.Add(sample);
            }
            token.ThrowIfCancellationRequested();
            if (binding != PreviewSourceBinding.Capture(request.Input.SourcePath))
                throw new PreviewUnavailableException("Source changed during preview sampling.");
            cache.Add(CacheKey(quality), samples);
            measured[quality] = samples.Select(sample => sample.VideoBytes / sample.MeasuredSeconds).ToArray();
        }

        async Task<List<EmpiricalPreviewCandidate>> ProjectCandidates()
        {
            static (double Minimum, double Mean, double Maximum) ProjectionRates(double[] rates)
            {
                double[] ordered = rates.OrderBy(rate => rate).ToArray();
                int trimEachTail = ordered.Length >= 5 ? 1 : 0;
                double mean = rates.Average();
                double minimum = Math.Min(ordered[trimEachTail], mean);
                double maximum = Math.Max(ordered[ordered.Length - 1 - trimEachTail], mean);
                return (minimum, mean, maximum);
            }

            var projected = new List<EmpiricalPreviewCandidate>();
            var first = ProjectionRates(measured[anchors[0]]);
            var last = ProjectionRates(measured[anchors[^1]]);
            // A tiny relative tolerance allows floating-point rounding, never a compression multiplier.
            bool monotonic = first.Mean >= last.Mean * (1 - 1e-9) &&
                first.Minimum >= last.Minimum * (1 - 1e-9) && first.Maximum >= last.Maximum * (1 - 1e-9);
            foreach (int quality in qualities)
            {
                bool interpolated = !measured.ContainsKey(quality) && monotonic;
                double min, mean, max;
                if (interpolated)
                {
                    double fraction = (quality - anchors[0]) / (double)(anchors[^1] - anchors[0]);
                    static double Interpolate(double a, double b, double t) => Math.Exp(Math.Log(a) + t * (Math.Log(b) - Math.Log(a)));
                    min = Interpolate(first.Minimum, last.Minimum, fraction);
                    mean = Interpolate(first.Mean, last.Mean, fraction);
                    max = Interpolate(first.Maximum, last.Maximum, fraction);
                }
                else
                {
                    double[] rates = measured.TryGetValue(quality, out double[]? existing)
                        ? existing
                        : await Measure(quality).ConfigureAwait(false);
                    (min, mean, max) = ProjectionRates(rates);
                }
                var projection = StorageSavingsSampleSelector.ProjectRates(request.SourceDuration.TotalSeconds,
                    request.Ancillary, min, mean, max, adaptive ? request.Contract.MaximumAcceptedOutputBytes : null);
                double video = mean * request.SourceDuration.TotalSeconds;
                projected.Add(new(quality, mean, video, projection.Lower, projection.Typical, projection.Upper,
                    projection.Typical - video - request.Ancillary.StreamBytes, projection.Classification, interpolated));
            }
            return projected;
        }

        foreach (int anchor in anchors) await Measure(anchor).ConfigureAwait(false);
        List<EmpiricalPreviewCandidate> candidates = await ProjectCandidates().ConfigureAwait(false);
        bool highDispersion = measured.Values.Any(rates =>
            rates.Length >= 3 && rates.Min() > 0 && rates.Max() / rates.Min() >= 1.75);
        bool nearStorageBoundary = adaptive && request.Contract.MaximumAcceptedOutputBytes is long ceiling &&
            candidates.Any(candidate => candidate.Classification == AdaptiveSampleClassification.Borderline &&
                Math.Abs(candidate.ProjectedTypicalBytes - ceiling) <= ceiling * 0.15);
        bool expanded = measured.Values.Any(rates => rates.Length >= expandedPositions.Count);
        if (!expanded && additionalPositions.Length > 0 && (highDispersion || nearStorageBoundary))
        {
            foreach (int quality in measured.Keys.ToArray())
                await Extend(quality).ConfigureAwait(false);
            candidates = await ProjectCandidates().ConfigureAwait(false);
            expanded = true;
        }
        EmpiricalPreviewCandidate? selected = adaptive
            ? candidates.FirstOrDefault(c => c.Classification == AdaptiveSampleClassification.ClearlyMeets) ??
              candidates.FirstOrDefault(c => StorageSavingsSampleSelector.IsEligibleBorderline(c.Classification,
                  c.ProjectedTypicalBytes, request.Contract.MaximumAcceptedOutputBytes!.Value))
            : candidates[0];
        var display = selected ?? candidates[^1];
        token.ThrowIfCancellationRequested();
        if (binding != PreviewSourceBinding.Capture(request.Input.SourcePath))
            throw new PreviewUnavailableException("Source changed before preview publication.");
        RepresentativeSampleEvidence[] reportingSamples = samplesByQuality[anchors[0]].ToArray();
        int reportingCount = reportingSamples.Length;
        double reportingSeconds = reportingSamples.Sum(sample => sample.MeasuredSeconds);
        bool rawWideRange = measured.Values.Any(rates =>
            rates.Length >= 3 && rates.Average() > 0 &&
            (rates.Min() / rates.Average() < 0.5 || rates.Max() / rates.Average() > 1.5));
        bool projectedWideRange = candidates.Any(candidate => candidate.ProjectedTypicalBytes > 0 &&
            (candidate.ProjectedUpperBytes / candidate.ProjectedTypicalBytes > 1.5 ||
             candidate.ProjectedLowerBytes / candidate.ProjectedTypicalBytes < 0.5));
        bool lowConfidence = rawWideRange || projectedWideRange;
        bool trimmedRange = measured.Values.Any(rates => rates.Length >= 5);
        string rangeMethod = trimmedRange
            ? "middle 3 of 5 sample rates, expanded to include the five-sample mean"
            : "observed sample minimum and maximum";
        string confidence = lowConfidence
            ? $"Low confidence; {reportingCount}-sample temporal projection; {rangeMethod}; raw sample dispersion is wide"
            : $"{reportingCount}-sample temporal projection";
        return new(request.Envelope.Mechanism, Array.AsReadOnly(anchors), candidates.AsReadOnly(), selected?.Quality,
            display.Quality, display.ProjectedTypicalBytes, request.Ancillary.StreamBytes, binding.Bytes,
            adaptive ? request.Contract.MaximumAcceptedOutputBytes : null,
            adaptive ? selected is null ? AdaptiveSelectionDisposition.Skipped : AdaptiveSelectionDisposition.Selected : null,
            reportingCount, reportingSeconds, hits, misses)
        {
            SourceWriteUtcTicks = binding.WriteTicks,
            Confidence = confidence,
            RangeMethod = rangeMethod,
            SampleEvidence = Array.AsReadOnly(samplesByQuality
                .OrderBy(entry => entry.Key)
                .SelectMany(entry => entry.Value.Select(sample => new EmpiricalPreviewSampleEvidence(
                    entry.Key,
                    sample.Sample.Label,
                    sample.Sample.Start.TotalSeconds,
                    sample.Sample.Duration.TotalSeconds,
                    sample.MeasuredSeconds,
                    sample.VideoBytes,
                    sample.VideoBytes / sample.MeasuredSeconds)))
                .ToArray())
        };
    }
}
