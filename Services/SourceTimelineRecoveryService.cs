using MediaFlux.Models;
using System.Globalization;

namespace MediaFlux.Services;

internal sealed record SourceTimelineRecoveryResult(
    bool Success, string RepairedPath, MediaProbeResult? RepairedProbe,
    SourceTimingAnalysis? RepairedTiming, string Reason)
{
    public static SourceTimelineRecoveryResult Failed(string reason) => new(false, "", null, null, reason);
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
                return SourceTimelineRecoveryResult.Failed($"The normalized temporary source did not prove a safe monotonic timeline: {timing.Reason}");
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
        if (originalRequired.Length != repairedRequired.Length || originalRequired.Select(Key).OrderBy(x => x).SequenceEqual(repairedRequired.Select(Key).OrderBy(x => x)) == false)
        { reason = "The normalized source changed required video/audio/subtitle/attachment stream topology."; return false; }
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
    private static string Key(MediaProbeStreamInfo stream) => $"{stream.CodecType}|{stream.CodecName}|{stream.Language}";
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
