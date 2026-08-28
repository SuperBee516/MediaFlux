using System.Globalization;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>UI-independent FFmpeg analysis that proposes, but never exports, commercial boundaries.</summary>
public sealed class CommercialDetectionService
{
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly IMediaProbeService _probe;
    private readonly Action<string>? _log;

    public CommercialDetectionService(string applicationDirectory, string? configuredFfmpegPath = null, string? configuredFfprobePath = null, Action<string>? log = null)
        : this(FfmpegToolResolver.Resolve(applicationDirectory, configuredFfmpegPath, configuredFfprobePath).FfmpegPath,
            new MediaToolProcessRunner(), new FfprobeService(applicationDirectory, configuredFfprobePath), log) { }

    internal CommercialDetectionService(string ffmpegPath, IMediaToolProcessRunner runner, IMediaProbeService probe, Action<string>? log = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
        _probe = probe;
        _log = log;
    }

    public async Task<CommercialDetectionResult> AnalyzeAsync(string sourcePath, CommercialDetectionSettings? settings = null,
        IProgress<CommercialDetectionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        settings ??= CommercialDetectionSettings.Standard;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return Failed(sourcePath, "The source media file does not exist.");
        if (!File.Exists(_ffmpegPath)) return Failed(sourcePath, $"FFmpeg was not found at '{_ffmpegPath}'.");

        progress?.Report(new(CommercialDetectionStage.ProbingSource, "Probing source", 0));
        MediaProbeResult probe;
        try { probe = await _probe.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failed(sourcePath, $"FFprobe could not inspect the source: {ex.Message}"); }
        if (!probe.Success || probe.DurationSeconds is not > 0) return Failed(sourcePath, $"The source could not be probed: {probe.ErrorMessage}");
        if (!probe.Streams.Any(IsPlayableVideo)) return Failed(sourcePath, "The source does not contain a playable video stream.");

        var warnings = new List<string>();
        IReadOnlyList<DetectionSignal> black = Array.Empty<DetectionSignal>();
        IReadOnlyList<DetectionSignal> silence = Array.Empty<DetectionSignal>();
        IReadOnlyList<DetectionSignal> scenes = Array.Empty<DetectionSignal>();
        if (settings.BlackDetectionEnabled)
            black = await RunDetectorAsync(CommercialDetectionStage.DetectingBlack, "Detecting black/fades", 15, sourcePath,
                BlackDetectionAnalyzer.BuildArguments(sourcePath, settings), BlackDetectionAnalyzer.Parse, warnings, progress, cancellationToken).ConfigureAwait(false);
        if (settings.SilenceDetectionEnabled && probe.Streams.Any(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase)))
            silence = await RunDetectorAsync(CommercialDetectionStage.DetectingSilence, "Detecting silence", 40, sourcePath,
                SilenceDetectionAnalyzer.BuildArguments(sourcePath, settings), SilenceDetectionAnalyzer.Parse, warnings, progress, cancellationToken).ConfigureAwait(false);
        else if (settings.SilenceDetectionEnabled) warnings.Add("The source has no audio stream; silence detection was skipped.");
        if (settings.SceneDetectionEnabled)
            scenes = await RunDetectorAsync(CommercialDetectionStage.DetectingScenes, "Detecting scene changes", 65, sourcePath,
                SceneDetectionAnalyzer.BuildArguments(sourcePath, settings), SceneDetectionAnalyzer.Parse, warnings, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new(CommercialDetectionStage.CorrelatingCandidates, "Correlating candidate boundaries", 82));
        IReadOnlyList<CommercialBoundary> candidates = BoundaryCorrelationEngine.Correlate(black.Concat(silence).Concat(scenes), settings);
        (IReadOnlyList<CommercialBoundary> boundaries, int rejected) = BoundaryCorrelationEngine.FilterAndOrder(candidates, probe.DurationSeconds.Value, settings);
        progress?.Report(new(CommercialDetectionStage.GeneratingSegments, "Generating segments", 92));
        IReadOnlyList<CommercialSegment> segments = BoundaryCorrelationEngine.GenerateSegments(boundaries, probe.DurationSeconds.Value);
        _log?.Invoke($"[CommercialDetector] Duration {probe.DurationSeconds.Value:0.###}s; black {black.Count}; silence {silence.Count}; scenes {scenes.Count}; correlated {candidates.Count}; rejected {rejected}; boundaries {boundaries.Count}; segments {segments.Count}.");
        progress?.Report(new(CommercialDetectionStage.Completed, "Analysis complete", 100));
        return new CommercialDetectionResult { SourcePath = sourcePath, SourceDurationSeconds = probe.DurationSeconds.Value, BlackSignals = black, SilenceSignals = silence, SceneSignals = scenes, Boundaries = boundaries, Segments = segments, Warnings = warnings, RejectedCandidateCount = rejected, Success = true };
    }

    private async Task<IReadOnlyList<DetectionSignal>> RunDetectorAsync(CommercialDetectionStage stage, string status, double percent, string sourcePath,
        IReadOnlyList<string> arguments, Func<string, IReadOnlyList<DetectionSignal>> parser, List<string> warnings, IProgress<CommercialDetectionProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(stage, status, percent));
        try
        {
            MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = arguments, Timeout = Timeout.InfiniteTimeSpan, SendQuitOnCancellation = true }, token).ConfigureAwait(false);
            if (process.ExitCode != 0) { warnings.Add($"{status} failed: {LastUsefulLine(process.StandardError)}"); return Array.Empty<DetectionSignal>(); }
            // Most FFmpeg filters report through stderr, while metadata=print can use stdout
            // with some builds. Parsing both keeps detector behavior independent of that detail.
            return parser(process.StandardError + Environment.NewLine + process.StandardOutput);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"{status} could not run: {ex.Message}"); return Array.Empty<DetectionSignal>(); }
    }

    private static bool IsPlayableVideo(MediaProbeStreamInfo stream) => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) && !(stream.Dispositions.TryGetValue("attached_pic", out bool attached) && attached);
    private static CommercialDetectionResult Failed(string source, string warning) => new() { SourcePath = source, Warnings = new[] { warning } };
    private static string LastUsefulLine(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "FFmpeg returned no diagnostic output.";
}

internal static class BlackDetectionAnalyzer
{
    private static readonly Regex Interval = new(@"black_start:(?<start>[-+\d.,]+)\s+black_end:(?<end>[-+\d.,]+)\s+black_duration:(?<duration>[-+\d.,]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static IReadOnlyList<string> BuildArguments(string source, CommercialDetectionSettings settings) => new[] { "-hide_banner", "-nostats", "-i", source, "-vf", $"blackdetect=d={F(settings.MinimumBlackDurationSeconds)}:pix_th={F(settings.BlackPixelThreshold)}", "-an", "-f", "null", "-" };
    internal static IReadOnlyList<DetectionSignal> Parse(string output) => Interval.Matches(output).Select(match => TryInterval(match, DetectionSignalKind.Black)).Where(signal => signal != null).Cast<DetectionSignal>().ToArray();
    internal static DetectionSignal? TryInterval(Match match, DetectionSignalKind kind) => TryNumber(match.Groups["start"].Value, out double start) && TryNumber(match.Groups["end"].Value, out double end) && TryNumber(match.Groups["duration"].Value, out double duration) && end >= start ? new(kind, (start + end) / 2d, start, end, duration) : null;
    internal static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    internal static bool TryNumber(string text, out double value) => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

internal static class SilenceDetectionAnalyzer
{
    private static readonly Regex Start = new(@"silence_start:\s*(?<value>[-+\d.,]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex End = new(@"silence_end:\s*(?<end>[-+\d.,]+)\s*\|\s*silence_duration:\s*(?<duration>[-+\d.,]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static IReadOnlyList<string> BuildArguments(string source, CommercialDetectionSettings settings) => new[] { "-hide_banner", "-nostats", "-i", source, "-af", $"silencedetect=n={BlackDetectionAnalyzer.F(settings.SilenceThresholdDb)}dB:d={BlackDetectionAnalyzer.F(settings.MinimumSilenceDurationSeconds)}", "-vn", "-f", "null", "-" };
    internal static IReadOnlyList<DetectionSignal> Parse(string output)
    {
        var results = new List<DetectionSignal>(); double? start = null;
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match begin = Start.Match(line); if (begin.Success && BlackDetectionAnalyzer.TryNumber(begin.Groups["value"].Value, out double value)) start = value;
            Match end = End.Match(line); if (!end.Success || !BlackDetectionAnalyzer.TryNumber(end.Groups["end"].Value, out double finish) || !BlackDetectionAnalyzer.TryNumber(end.Groups["duration"].Value, out double duration)) continue;
            double intervalStart = start ?? Math.Max(0, finish - duration); if (finish >= intervalStart) results.Add(new(DetectionSignalKind.Silence, (intervalStart + finish) / 2d, intervalStart, finish, duration)); start = null;
        }
        return results;
    }
}

internal static class SceneDetectionAnalyzer
{
    private static readonly Regex Timestamp = new(@"pts_time:(?<value>[-+\d.,]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static IReadOnlyList<string> BuildArguments(string source, CommercialDetectionSettings settings) => new[] { "-hide_banner", "-nostats", "-i", source, "-vf", $"select='gt(scene,{BlackDetectionAnalyzer.F(settings.SceneThreshold)})',metadata=print", "-an", "-f", "null", "-" };
    internal static IReadOnlyList<DetectionSignal> Parse(string output)
    {
        var results = new List<DetectionSignal>();
        foreach (Match match in Timestamp.Matches(output))
            if (BlackDetectionAnalyzer.TryNumber(match.Groups["value"].Value, out double timestamp))
                results.Add(new DetectionSignal(DetectionSignalKind.Scene, timestamp));
        return results;
    }
}

internal static class BoundaryCorrelationEngine
{
    internal static IReadOnlyList<CommercialBoundary> Correlate(IEnumerable<DetectionSignal> signals, CommercialDetectionSettings settings)
    {
        var groups = new List<List<DetectionSignal>>();
        foreach (DetectionSignal signal in signals.Where(signal => signal.TimestampSeconds >= 0 && double.IsFinite(signal.TimestampSeconds)).OrderBy(signal => signal.TimestampSeconds))
        {
            if (groups.Count == 0 || signal.TimestampSeconds - groups[^1].Max(item => item.TimestampSeconds) > settings.CorrelationToleranceSeconds) groups.Add(new List<DetectionSignal> { signal }); else groups[^1].Add(signal);
        }
        return groups.Select(group => CreateBoundary(group)).ToArray();
    }

    internal static (IReadOnlyList<CommercialBoundary> Boundaries, int Rejected) FilterAndOrder(IReadOnlyList<CommercialBoundary> candidates, double duration, CommercialDetectionSettings settings)
    {
        var accepted = new List<CommercialBoundary>(); int rejected = 0;
        foreach (CommercialBoundary rawCandidate in candidates.OrderBy(item => item.TimestampSeconds))
        {
            CommercialBoundary candidate = ApplyCommonLengthBoost(rawCandidate, accepted.Count == 0 ? 0 : accepted[^1].TimestampSeconds, settings);
            bool sceneOnly = candidate.Evidence.All(evidence => evidence.Kind == DetectionSignalKind.Scene);
            if (candidate.Confidence < settings.MinimumBoundaryConfidence || (sceneOnly && candidate.Confidence < settings.MinimumSceneOnlyConfidence) || candidate.TimestampSeconds < settings.MinimumSegmentDurationSeconds || duration - candidate.TimestampSeconds < settings.MinimumSegmentDurationSeconds || (accepted.Count > 0 && candidate.TimestampSeconds - accepted[^1].TimestampSeconds < settings.MinimumSegmentDurationSeconds)) { rejected++; continue; }
            accepted.Add(candidate);
        }
        return (accepted, rejected);
    }

    private static CommercialBoundary ApplyCommonLengthBoost(CommercialBoundary candidate, double previousBoundary, CommercialDetectionSettings settings)
    {
        if (!settings.PreferCommonCommercialLengths) return candidate;
        double length = candidate.TimestampSeconds - previousBoundary;
        if (!new[] { 15d, 30d, 60d, 90d }.Any(common => Math.Abs(length - common) <= Math.Max(1.5, common * .08))) return candidate;
        int confidence = Math.Min(100, candidate.Confidence + 4);
        return candidate with { Confidence = confidence, ConfidenceCategory = confidence >= 75 ? CommercialDetectionConfidence.High : confidence >= 50 ? CommercialDetectionConfidence.Medium : CommercialDetectionConfidence.Low };
    }

    internal static IReadOnlyList<CommercialSegment> GenerateSegments(IReadOnlyList<CommercialBoundary> boundaries, double duration)
    {
        var points = boundaries.Select(boundary => boundary.TimestampSeconds).Where(point => point > 0 && point < duration).Distinct().OrderBy(point => point).ToList(); points.Add(duration);
        var segments = new List<CommercialSegment>(); double start = 0;
        foreach (double end in points) { if (end > start) segments.Add(new(segments.Count + 1, start, end)); start = end; }
        return segments;
    }

    private static CommercialBoundary CreateBoundary(IReadOnlyList<DetectionSignal> group)
    {
        double weightTotal = group.Sum(Weight); double timestamp = group.Sum(signal => signal.TimestampSeconds * Weight(signal)) / weightTotal;
        int score = group.Sum(signal => BaseScore(signal) + DurationBonus(signal)); int types = group.Select(signal => signal.Kind).Distinct().Count();
        if (types > 1) score += 18 + (types - 2) * 5; if (group.Count > types) score += Math.Min(8, (group.Count - types) * 2);
        score = Math.Clamp(score, 0, 100);
        DetectionEvidence[] evidence = group.Select(signal => new DetectionEvidence(signal.Kind, signal.TimestampSeconds, Describe(signal), signal.StartSeconds, signal.EndSeconds, signal.DurationSeconds)).ToArray();
        return new(timestamp, score, score >= 75 ? CommercialDetectionConfidence.High : score >= 50 ? CommercialDetectionConfidence.Medium : CommercialDetectionConfidence.Low, evidence);
    }
    private static int BaseScore(DetectionSignal signal) => signal.Kind switch { DetectionSignalKind.Black => 55, DetectionSignalKind.Silence => 30, _ => 16 };
    private static int DurationBonus(DetectionSignal signal) => signal.DurationSeconds is not > 0 ? 0 : signal.Kind switch { DetectionSignalKind.Black => Math.Min(8, (int)Math.Round(signal.DurationSeconds.Value * 4)), DetectionSignalKind.Silence => Math.Min(5, (int)Math.Round(signal.DurationSeconds.Value * 2)), _ => 0 };
    private static double Weight(DetectionSignal signal) => signal.Kind switch { DetectionSignalKind.Black => 4, DetectionSignalKind.Silence => 2.5, _ => 1 };
    private static string Describe(DetectionSignal signal) => signal.Kind switch { DetectionSignalKind.Black => $"Black/fade interval {signal.StartSeconds:0.###}–{signal.EndSeconds:0.###}s", DetectionSignalKind.Silence => $"Silence interval {signal.StartSeconds:0.###}–{signal.EndSeconds:0.###}s", _ => $"Scene change at {signal.TimestampSeconds:0.###}s" };
}
