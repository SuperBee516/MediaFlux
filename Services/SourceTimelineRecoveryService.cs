using MediaFlux.Models;
using System.Globalization;

namespace MediaFlux.Services;

internal enum SourceTimelineRecoveryFailureKind { None, StreamCopyPreservedUnsafeTiming, Other }

internal sealed record SourceTimelineRecoveryResult(
    bool Success, string RepairedPath, MediaProbeResult? RepairedProbe,
    SourceTimingAnalysis? RepairedTiming, string Reason,
    SourceTimelineRecoveryFailureKind FailureKind = SourceTimelineRecoveryFailureKind.None)
{
    public static SourceTimelineRecoveryResult Failed(string reason, SourceTimelineRecoveryFailureKind failureKind = SourceTimelineRecoveryFailureKind.Other) => new(false, "", null, null, reason, failureKind);
}

/// <summary>One-shot, non-destructive timestamp normalization for a classified timeline defect.</summary>
internal sealed class SourceTimelineRecoveryService
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly Action<string>? _log;

    public SourceTimelineRecoveryService(string ffmpegPath, string ffprobePath, IMediaToolProcessRunner? runner = null, Action<string>? log = null)
    { _ffmpegPath = ffmpegPath; _ffprobePath = ffprobePath; _runner = runner ?? new MediaToolProcessRunner(); _log = log; }

    public async Task<SourceTimelineRecoveryResult> TryNormalizeAsync(string sourcePath, string outputPath, MediaProbeResult originalProbe, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(outputPath) ||
            sourcePath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            return SourceTimelineRecoveryResult.Failed("The temporary timeline-repair path was invalid or replaced the source.");
        string stagingPath = outputPath + ".partial";
        try
        {
            TryDelete(stagingPath);
            MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffmpegPath,
                Arguments = new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-fflags", "+genpts", "-i", sourcePath, "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-dn", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", "matroska", stagingPath },
                Timeout = TimeSpan.FromMinutes(2)
            }, token).ConfigureAwait(false);
            if (process.ExitCode != 0 || process.TimedOut || !File.Exists(stagingPath) || new FileInfo(stagingPath).Length == 0)
                return SourceTimelineRecoveryResult.Failed($"Timestamp normalization did not complete: exit={process.ExitCode}; timed-out={process.TimedOut}; diagnostics={process.StandardError.Trim()}");

            var probeService = new FfprobeService(_ffprobePath, _runner);
            MediaProbeResult repairedProbe = await probeService.ProbeAsync(stagingPath, token).ConfigureAwait(false);
            if (!repairedProbe.Success)
                return SourceTimelineRecoveryResult.Failed("The normalized temporary source could not be re-probed: " + repairedProbe.ErrorMessage);
            if (!IsEquivalent(originalProbe, repairedProbe, out string equivalenceError))
                return SourceTimelineRecoveryResult.Failed(equivalenceError);

            SourceTimingAnalysis timing = await new SourceTimingAnalysisService(_ffprobePath, _runner, _log).AnalyzeAsync(stagingPath, token).ConfigureAwait(false);
            if (timing.Classification == SourceTimingClassification.IrregularUnsafe || timing.Classification == SourceTimingClassification.Unknown)
                return SourceTimelineRecoveryResult.Failed($"The normalized temporary source did not prove a safe monotonic timeline: {timing.Reason}", SourceTimelineRecoveryFailureKind.StreamCopyPreservedUnsafeTiming);
            File.Move(stagingPath, outputPath, overwrite: false);
            return new(true, outputPath, repairedProbe, timing, $"Temporary stream-copy normalization produced an equivalent {timing.Classification} timeline.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return SourceTimelineRecoveryResult.Failed($"Timestamp normalization failed: {ex.Message}"); }
        finally { TryDelete(stagingPath); }
    }

    internal static bool IsEquivalent(MediaProbeResult original, MediaProbeResult repaired, out string reason)
    {
        if (!original.Success || !repaired.Success) { reason = "Source equivalence requires successful original and repaired probes."; return false; }
        MediaProbeStreamInfo[] originalRequired = original.Streams.Where(IsRequired).ToArray();
        MediaProbeStreamInfo[] repairedRequired = repaired.Streams.Where(IsRequired).ToArray();
        if (originalRequired.Length != repairedRequired.Length ||
            !HaveEquivalentRequiredTopology(originalRequired, repairedRequired))
        {
            reason = DescribeTopologyMismatch(originalRequired, repairedRequired);
            return false;
        }
        double originalDuration = ProgramDurationResolver.Resolve(original).DurationSeconds ?? original.DurationSeconds ?? 0;
        double repairedDuration = ProgramDurationResolver.Resolve(repaired).DurationSeconds ?? repaired.DurationSeconds ?? 0;
        if (originalDuration > 0 && repairedDuration > 0 && Math.Abs(originalDuration - repairedDuration) > .75)
        { reason = $"The normalized source changed authoritative duration from {originalDuration:0.###}s to {repairedDuration:0.###}s."; return false; }
        MediaProbeStreamInfo? originalVideo = original.Streams.FirstOrDefault(x => x.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        MediaProbeStreamInfo? repairedVideo = repaired.Streams.FirstOrDefault(x => x.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        if (originalVideo?.FrameCount is > 0 && repairedVideo?.FrameCount is > 0 && originalVideo.FrameCount != repairedVideo.FrameCount)
        { reason = "The normalized source changed the measured video frame count."; return false; }
        reason = "Original and normalized source probes preserve required topology and authoritative duration."; return true;
    }

    private static bool IsRequired(MediaProbeStreamInfo stream) => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) || stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) || stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) || stream.CodecType.Equals("attachment", StringComparison.OrdinalIgnoreCase);
    private static bool HaveEquivalentRequiredTopology(
        IReadOnlyList<MediaProbeStreamInfo> original,
        IReadOnlyList<MediaProbeStreamInfo> repaired)
    {
        var remaining = repaired.Select((stream, index) => (stream, index)).ToList();
        foreach (MediaProbeStreamInfo source in original)
        {
            int match = remaining.FindIndex(candidate => SemanticKey(candidate.stream) == SemanticKey(source));
            if (match < 0) return false;
            remaining.RemoveAt(match);
        }
        return remaining.Count == 0;
    }

    private static string DescribeTopologyMismatch(
        IReadOnlyList<MediaProbeStreamInfo> original,
        IReadOnlyList<MediaProbeStreamInfo> repaired)
    {
        var remaining = repaired.Select((stream, index) => (stream, index)).ToList();
        var missing = new List<string>();
        foreach (MediaProbeStreamInfo source in original)
        {
            int match = remaining.FindIndex(candidate => SemanticKey(candidate.stream) == SemanticKey(source));
            if (match >= 0) remaining.RemoveAt(match);
            else missing.Add(DescribeStream(source));
        }
        string missingText = missing.Count == 0 ? "none" : string.Join(", ", missing);
        string extraText = remaining.Count == 0 ? "none" : string.Join(", ", remaining.Select(candidate => DescribeStream(candidate.stream)));
        return $"The normalized source changed required video/audio/subtitle/attachment stream topology. " +
            $"Counts(original→normalized): video={Count(original, "video")}→{Count(repaired, "video")}, " +
            $"audio={Count(original, "audio")}→{Count(repaired, "audio")}, " +
            $"subtitle={Count(original, "subtitle")}→{Count(repaired, "subtitle")}, " +
            $"attachment={Count(original, "attachment")}→{Count(repaired, "attachment")}. " +
            $"Missing-after={missingText}; extra-after={extraText}.";
    }

    private static string SemanticKey(MediaProbeStreamInfo stream) =>
        $"{stream.CodecType.Trim().ToLowerInvariant()}|{stream.CodecName.Trim().ToLowerInvariant()}|{LanguageMetadataNormalizer.Normalize(stream.Language)}";

    private static string DescribeStream(MediaProbeStreamInfo stream) =>
        $"{stream.CodecType}/{stream.CodecName}/language={LanguageMetadataNormalizer.Normalize(stream.Language)}";

    private static int Count(IEnumerable<MediaProbeStreamInfo> streams, string type) =>
        streams.Count(stream => stream.CodecType.Equals(type, StringComparison.OrdinalIgnoreCase));
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
