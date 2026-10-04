using System.Security.Cryptography;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record PreviewSourceBinding(string Path, long Bytes, long WriteTicks)
{
    internal static PreviewSourceBinding Capture(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0) throw new PreviewUnavailableException("Source binding is unavailable.");
        return new(file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks);
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
        bool adaptive, IAdaptiveVideoSampleRunner runner, Func<int, string> videoSignature, CancellationToken token)
    {
        PreviewSourceBinding binding = PreviewSourceBinding.Capture(request.Input.SourcePath);
        var positions = VideoSamplingProfile.Preview.Positions(request.SourceDuration);
        int[] qualities = adaptive ? AdaptiveQualityEnvelopeService.Candidates(request.Envelope).ToArray()
            : [request.Envelope.PreferredQuality];
        int[] anchors = qualities.Length > 1 ? [qualities[0], qualities[^1]] : [qualities[0]];
        var measured = new Dictionary<int, double[]>();
        int hits = 0, misses = 0;
        async Task<double[]> Measure(int quality)
        {
            token.ThrowIfCancellationRequested();
            string identity = $"Preview5sV1|{binding}|duration={request.SourceDuration.Ticks}|quality={quality}|{videoSignature(quality)}";
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            if (cache.TryGet(key, out var samples)) hits++;
            else
            {
                misses++;
                var collected = new List<RepresentativeSampleEvidence>();
                foreach (var position in positions)
                {
                    var sample = await runner.MeasureAsync(request, quality, position, token).ConfigureAwait(false);
                    if (sample.Purpose != VideoSamplePurpose.PreviewEstimation || sample.Sample != position ||
                        sample.VideoBytes <= 0 || !double.IsFinite(sample.MeasuredSeconds) || sample.MeasuredSeconds <= 0)
                        throw new InvalidDataException("Preview video evidence is invalid or has the wrong purpose.");
                    collected.Add(sample);
                }
                token.ThrowIfCancellationRequested();
                if (binding != PreviewSourceBinding.Capture(request.Input.SourcePath))
                    throw new PreviewUnavailableException("Source changed during preview sampling.");
                samples = collected.AsReadOnly();
                cache.Add(key, samples);
            }
            return measured[quality] = samples.Select(s => s.VideoBytes / s.MeasuredSeconds).ToArray();
        }
        foreach (int anchor in anchors) await Measure(anchor).ConfigureAwait(false);
        var candidates = new List<EmpiricalPreviewCandidate>();
        double[] first = measured[anchors[0]], last = measured[anchors[^1]];
        // A tiny relative tolerance allows floating-point rounding, never a compression multiplier.
        bool monotonic = first.Average() >= last.Average() * (1 - 1e-9) &&
            first.Min() >= last.Min() * (1 - 1e-9) && first.Max() >= last.Max() * (1 - 1e-9);
        foreach (int quality in qualities)
        {
            bool interpolated = !measured.ContainsKey(quality) && monotonic;
            double min, mean, max;
            if (interpolated)
            {
                double fraction = (quality - anchors[0]) / (double)(anchors[^1] - anchors[0]);
                static double Interpolate(double a, double b, double t) => Math.Exp(Math.Log(a) + t * (Math.Log(b) - Math.Log(a)));
                min = Interpolate(first.Min(), last.Min(), fraction);
                mean = Interpolate(first.Average(), last.Average(), fraction);
                max = Interpolate(first.Max(), last.Max(), fraction);
            }
            else
            {
                double[] rates = measured.TryGetValue(quality, out var existing) ? existing : await Measure(quality).ConfigureAwait(false);
                min = rates.Min(); mean = rates.Average(); max = rates.Max();
            }
            var p = StorageSavingsSampleSelector.ProjectRates(request.SourceDuration.TotalSeconds,
                request.Ancillary, min, mean, max, adaptive ? request.Contract.MaximumAcceptedOutputBytes : null);
            double video = mean * request.SourceDuration.TotalSeconds;
            candidates.Add(new(quality, mean, video, p.Lower, p.Typical, p.Upper,
                p.Typical - video - request.Ancillary.StreamBytes, p.Classification, interpolated));
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
        return new(request.Envelope.Mechanism, Array.AsReadOnly(anchors), candidates.AsReadOnly(), selected?.Quality,
            display.Quality, display.ProjectedTypicalBytes, request.Ancillary.StreamBytes, binding.Bytes,
            adaptive ? request.Contract.MaximumAcceptedOutputBytes : null,
            adaptive ? selected is null ? AdaptiveSelectionDisposition.Skipped : AdaptiveSelectionDisposition.Selected : null,
            positions.Count, positions.Sum(p => p.Duration.TotalSeconds), hits, misses) { SourceWriteUtcTicks = binding.WriteTicks };
    }
}
