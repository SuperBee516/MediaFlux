using System.Drawing;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record TemporalFrame(double Luma, double EdgeDensity);

/// <summary>Bounded post-preview temporal analysis. It compares restored variation with the same original motion sample.</summary>
public sealed class TemporalQualityAnalysisService
{
    private const int MinimumFrames = 4;
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly Action<string>? _log;
    public TemporalQualityAnalysisService(string ffmpegPath, IMediaToolProcessRunner runner, Action<string>? log = null) { _ffmpegPath = ffmpegPath; _runner = runner; _log = log; }

    public TemporalQualityResult Analyze(IReadOnlyList<TemporalFrame> original, IReadOnlyList<TemporalFrame> restored)
    {
        if (original.Count < MinimumFrames || restored.Count != original.Count) return new(TemporalStability.Unknown, 0, 0, 0, 0, 0, 0, 0, "Insufficient matched temporal samples.");
        double om = Difference(original.Select(x => x.Luma)), rm = Difference(restored.Select(x => x.Luma));
        double oe = Difference(original.Select(x => x.EdgeDensity)), re = Difference(restored.Select(x => x.EdgeDensity));
        double ob = StandardDeviation(original.Select(x => x.Luma)), rb = StandardDeviation(restored.Select(x => x.Luma));
        // Ratios normalize ordinary source motion. Conservative thresholds require restored
        // change materially above the corresponding original signal before warning.
        double score = Math.Max(rm / Math.Max(.002, om), Math.Max(re / Math.Max(.002, oe), rb / Math.Max(.002, ob)));
        TemporalStability stability = score >= 2.4 ? TemporalStability.SevereInstability : score >= 1.7 ? TemporalStability.ModerateInstability : score >= 1.3 ? TemporalStability.MildInstability : TemporalStability.Stable;
        return new(stability, 70, om, rm, oe, re, ob, rb, stability == TemporalStability.Stable ? "Restored frame-to-frame variation is comparable to the original sample." : "Restored luma or edge variation is materially higher than the matching original sample.");
    }

    public async Task<TemporalQualityResult> AnalyzeMotionAsync(string originalPath, string restoredPath, CancellationToken token = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxTemporal", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            IReadOnlyList<TemporalFrame> original = await ExtractAsync(originalPath, Path.Combine(root, "original"), token).ConfigureAwait(false);
            IReadOnlyList<TemporalFrame> restored = await ExtractAsync(restoredPath, Path.Combine(root, "restored"), token).ConfigureAwait(false);
            TemporalQualityResult result = Analyze(original, restored); _log?.Invoke($"[TemporalQuality] originalMotion={result.OriginalMotion:0.####}; restoredMotion={result.RestoredMotion:0.####}; classification={result.Classification}; confidence={result.Confidence}."); return result;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private async Task<IReadOnlyList<TemporalFrame>> ExtractAsync(string video, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory); string pattern = Path.Combine(directory, "frame-%03d.png");
        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", video, "-vf", "fps=4,scale=160:-2", "-frames:v", "20", pattern }, Timeout = TimeSpan.FromSeconds(45) }, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut) return Array.Empty<TemporalFrame>();
        return Directory.EnumerateFiles(directory, "frame-*.png").OrderBy(x => x, StringComparer.Ordinal).Select(ReadFrame).ToArray();
    }
    private static TemporalFrame ReadFrame(string path)
    {
        using var image = new Bitmap(path); double luma = 0, edge = 0; int count = 0;
        for (int y = 1; y < image.Height; y += 2) for (int x = 1; x < image.Width; x += 2) { Color p = image.GetPixel(x, y), left = image.GetPixel(x - 1, y), up = image.GetPixel(x, y - 1); double value = (p.R * .2126 + p.G * .7152 + p.B * .0722) / 255d; luma += value; edge += Math.Abs(value - ((left.R * .2126 + left.G * .7152 + left.B * .0722) / 255d)) + Math.Abs(value - ((up.R * .2126 + up.G * .7152 + up.B * .0722) / 255d)); count++; }
        return new(luma / Math.Max(1, count), edge / Math.Max(1, count));
    }
    private static double Difference(IEnumerable<double> values) { double[] a = values.ToArray(); return a.Zip(a.Skip(1), (x, y) => Math.Abs(y - x)).DefaultIfEmpty().Average(); }
    private static double StandardDeviation(IEnumerable<double> values) { double[] a = values.ToArray(); double mean = a.Average(); return Math.Sqrt(a.Select(x => (x - mean) * (x - mean)).Average()); }
}
