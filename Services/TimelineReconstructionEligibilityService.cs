using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Proves whether one existing ordinary encode may regenerate video presentation
/// timestamps after stream-copy normalization preserved a classified defect.
/// It deliberately does not identify individual malformed-PTS signatures.
/// </summary>
internal sealed class TimelineReconstructionEligibilityService
{
    private const double DurationBoundarySeconds = .75;
    private const double StartOffsetBoundarySeconds = .125;
    private const double CadenceRelativeTolerance = .001;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly Action<string>? _log;

    public TimelineReconstructionEligibilityService(string ffmpegPath, string ffprobePath,
        IMediaToolProcessRunner? runner = null, Action<string>? log = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _runner = runner ?? new MediaToolProcessRunner();
        _log = log;
    }

    public async Task<TimelineReconstructionEligibility> EvaluateAsync(
        string sourcePath,
        MediaProbeStreamInfo video,
        double? authoritativeDurationSeconds,
        IReadOnlyList<MediaProbeStreamInfo> selectedAudio,
        CancellationToken cancellationToken = default)
    {
        if (!RationalFrameRate.TryParse(video.NominalFrameRateRational, out RationalFrameRate rate))
            return Rejected("The source has no exact positive nominal rational frame rate.");

        var packets = new List<SourceTimelinePacket>();
        MediaToolProcessResult packetProbe = await _runner.RunAsync(new MediaToolProcessRequest
        {
            FileName = _ffprobePath,
            Timeout = TimeSpan.FromMinutes(2),
            Arguments = ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,dts_time,duration_time", "-of", "csv=p=0", sourcePath],
            StandardOutputLineCallback = line =>
            {
                if (TryParsePacket(line, out SourceTimelinePacket? packet) && packet is not null)
                    packets.Add(packet);
            }
        }, cancellationToken).ConfigureAwait(false);
        if (packetProbe.TimedOut || packetProbe.ExitCode != 0)
            return Rejected(packetProbe.TimedOut ? "Full-stream packet evidence timed out." : "Full-stream packet evidence failed.");

        var progress = new DecodeProgress();
        MediaToolProcessResult decode = await _runner.RunAsync(new MediaToolProcessRequest
        {
            FileName = _ffmpegPath,
            Timeout = TimeSpan.FromMinutes(30),
            SendQuitOnCancellation = true,
            Arguments = ["-hide_banner", "-nostats", "-loglevel", "error", "-xerror", "-err_detect", "explode", "-progress", "pipe:1", "-i", sourcePath, "-map", "0:v:0", "-an", "-sn", "-dn", "-fps_mode", "passthrough", "-f", "null", "-"],
            StandardOutputLineCallback = progress.Consume
        }, cancellationToken).ConfigureAwait(false);
        if (decode.ExitCode != 0 || decode.TimedOut)
            return Rejected($"Full strict video decode did not complete: exit={decode.ExitCode}; timed-out={decode.TimedOut}.");

        TimelineReconstructionEligibility eligibility = Evaluate(new(
            rate, video.FrameCount, authoritativeDurationSeconds, video.StartTimeSeconds,
            packets, progress.ReachedEnd, progress.DecodedFrames,
            selectedAudio.Select(audio => new TimelineReconstructionAudioEvidence(audio.StartTimeSeconds, audio.DurationSeconds)).ToArray()));
        _log?.Invoke("[TimingRecovery] " + eligibility.DescribeEvidence());
        return eligibility;
    }

    internal static TimelineReconstructionEligibility Evaluate(TimelineReconstructionEvidence evidence)
    {
        if (evidence.FrameRate is not { IsValid: true } rate)
            return Rejected("The source has no exact positive nominal rational frame rate.");
        if (evidence.ProbedFrameCount is not > 0)
            return Rejected("The source video has no measured frame count to corroborate the full decode.");
        if (evidence.AuthoritativeDurationSeconds is not > 0 || !double.IsFinite(evidence.AuthoritativeDurationSeconds.Value))
            return Rejected("The authoritative source presentation duration is unavailable.");
        if (!evidence.DecodeReachedEnd || evidence.DecodedFrameCount <= 0)
            return Rejected("Full strict video decode did not prove EOF coverage.");
        if (evidence.DecodedFrameCount != evidence.ProbedFrameCount.Value)
            return Rejected($"Full strict decode produced {evidence.DecodedFrameCount} frames, but FFprobe reported {evidence.ProbedFrameCount.Value}.");
        if (evidence.Packets.Count != evidence.DecodedFrameCount)
            return Rejected($"Packet count {evidence.Packets.Count} does not equal the full decoded frame count {evidence.DecodedFrameCount}.");
        if (evidence.Packets.Any(packet => packet.DurationSeconds is not > 0 || !double.IsFinite(packet.DurationSeconds.Value)))
            return Rejected("At least one video packet has no finite positive duration.");
        if (evidence.Packets.Any(packet => packet.DecodeTimeSeconds is null || !double.IsFinite(packet.DecodeTimeSeconds.Value)))
            return Rejected("Packet DTS ordering cannot be verified.");
        if (evidence.Packets.Zip(evidence.Packets.Skip(1), (left, right) => right.DecodeTimeSeconds!.Value > left.DecodeTimeSeconds!.Value).Any(ordered => !ordered))
            return Rejected("Packet DTS ordering is non-monotonic.");

        double cadence = (double)rate.Denominator / rate.Numerator;
        double cadenceTolerance = Math.Max(0.000001, cadence * CadenceRelativeTolerance);
        if (evidence.Packets.Any(packet => Math.Abs(packet.DurationSeconds!.Value - cadence) > cadenceTolerance))
            return Rejected("Full-stream packet durations do not prove exact CFR cadence.");

        double reconstructedDuration = evidence.DecodedFrameCount * cadence;
        double authoritativeDuration = evidence.AuthoritativeDurationSeconds.Value;
        double durationDelta = Math.Abs(reconstructedDuration - authoritativeDuration);
        if (durationDelta > DurationBoundarySeconds)
            return Rejected($"Frame-index reconstruction duration {reconstructedDuration:0.######}s materially differs from authoritative duration {authoritativeDuration:0.######}s.");

        foreach (TimelineReconstructionAudioEvidence audio in evidence.AudioStreams)
        {
            if (audio.DurationSeconds is not > 0 || !double.IsFinite(audio.DurationSeconds.Value))
                return Rejected("A selected audio stream has no reliable duration for A/V coverage validation.");
            if (Math.Abs(audio.DurationSeconds.Value - reconstructedDuration) > DurationBoundarySeconds)
                return Rejected($"A selected audio stream duration {audio.DurationSeconds.Value:0.######}s is materially incompatible with reconstructed video duration {reconstructedDuration:0.######}s.");
            if (audio.StartTimeSeconds is { } audioStart && evidence.VideoStartTimeSeconds is { } videoStart &&
                Math.Abs(audioStart - videoStart) > StartOffsetBoundarySeconds)
                return Rejected($"Selected audio/video start offsets differ materially ({audioStart:0.######}s vs {videoStart:0.######}s).");
        }

        return new(true, "Full strict decode, exact packet cadence, DTS ordering, duration, and selected audio coverage prove deterministic frame-index reconstruction safe.",
            rate, evidence.Packets.Count, evidence.DecodedFrameCount, evidence.DecodeReachedEnd, reconstructedDuration, authoritativeDuration,
            durationDelta, true, true, true);
    }

    private static TimelineReconstructionEligibility Rejected(string reason) => new(false, reason);

    private static bool TryParsePacket(string line, out SourceTimelinePacket? packet)
    {
        packet = null;
        string[] fields = line.Split(',');
        if (fields.Length < 3) return false;
        static double? Parse(string value) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) ? parsed : null;
        packet = new(Parse(fields[0]), Parse(fields[1]), Parse(fields[2]));
        return true;
    }

    private sealed class DecodeProgress
    {
        public long DecodedFrames { get; private set; }
        public bool ReachedEnd { get; private set; }
        public void Consume(string line)
        {
            int separator = line.IndexOf('=');
            if (separator <= 0) return;
            string key = line[..separator];
            string value = line[(separator + 1)..].Trim();
            if (key.Equals("frame", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frames)) DecodedFrames = Math.Max(DecodedFrames, frames);
            else if (key.Equals("progress", StringComparison.OrdinalIgnoreCase) && value.Equals("end", StringComparison.OrdinalIgnoreCase)) ReachedEnd = true;
        }
    }
}
