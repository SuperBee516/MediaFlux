using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Manifest contract for future timestamp-preserving VFR extraction/reassembly. It never normalizes cadence.</summary>
public static class AiTimestampManifestService
{
    public static AiTimestampValidationResult Validate(AiTimestampManifest manifest, IReadOnlyList<string>? actualOutputFiles = null)
    {
        if (manifest.Frames.Count == 0) return new(false, "Timing manifest contains no frames.", 0, false, false);
        bool indexes = manifest.Frames.Select((frame, index) => frame.FrameIndex == index).All(x => x); bool monotonic = manifest.Frames.Zip(manifest.Frames.Skip(1), (a, b) => b.PresentationSeconds > a.PresentationSeconds && a.DurationSeconds > 0).All(x => x); bool identity = actualOutputFiles == null || actualOutputFiles.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(manifest.Frames.Select(frame => frame.OutputFileName).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);
        return indexes && monotonic && identity ? new(true, "Manifest frame identities and monotonic presentation timing are valid.", 0, true, true) : new(false, !indexes ? "Manifest frame indexes are not deterministic." : !monotonic ? "Manifest contains non-monotonic or ambiguous presentation timing." : "AI output frame identities do not match the timing manifest.", 0, monotonic, identity);
    }
    /// <summary>
    /// Compares every decoded presentation timestamp. Callers supply a tolerance derived from
    /// the output container time base; a matching range alone is not evidence that VFR cadence
    /// survived reassembly.
    /// </summary>
    public static AiTimestampValidationResult Compare(AiTimestampManifest expected, IReadOnlyList<double> actualPts, double toleranceSeconds)
    {
        if (actualPts.Count != expected.Frames.Count) return new(false, $"Frame-count mismatch: expected {expected.Frames.Count}, actual {actualPts.Count}.", 0, false, false);
        if (actualPts.Zip(actualPts.Skip(1), (a, b) => b > a).Any(x => !x)) return new(false, "Reassembled timestamps are non-monotonic.", 0, false, true);
        double[] deltas = actualPts.Select((value, index) => Math.Abs(value - expected.Frames[index].PresentationSeconds)).ToArray();
        double delta = deltas.Max();
        if (delta > toleranceSeconds) return new(false, $"Reassembled timestamp {Array.IndexOf(deltas, delta)} differs from the manifest by {delta:0.########} seconds (tolerance {toleranceSeconds:0.########}).", delta, true, true);

        double[] expectedIntervals = expected.Frames.Zip(expected.Frames.Skip(1), (a, b) => b.PresentationSeconds - a.PresentationSeconds).ToArray();
        double[] actualIntervals = actualPts.Zip(actualPts.Skip(1), (a, b) => b - a).ToArray();
        double cadenceDelta = expectedIntervals.Zip(actualIntervals, (wanted, observed) => Math.Abs(wanted - observed)).DefaultIfEmpty(0).Max();
        return cadenceDelta <= toleranceSeconds * 2
            ? new(true, "Reassembled timestamps and per-frame cadence match the manifest within container quantization tolerance.", Math.Max(delta, cadenceDelta), true, true)
            : new(false, $"Reassembled per-frame cadence differs by {cadenceDelta:0.########} seconds (tolerance {toleranceSeconds * 2:0.########}).", Math.Max(delta, cadenceDelta), true, true);
    }
}
