using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Full-stream packet-duration diagnosis used only after strict output validation
/// exposes a material frame deficit. Normal encodes continue using the bounded
/// five-window timing analysis.
/// </summary>
internal sealed class LocalizedSourceTimelineAnalysisService
{
    private const double ExistingDurationBoundarySeconds = .75;
    private const double StableCadenceRelativeTolerance = .015;
    private const double ShortDurationRelativeThreshold = .25;
    private const double MinimumShortRegionPackets = 8;
    private const double MinimumStablePacketFraction = .97;

    private readonly string _ffprobePath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly Action<string>? _log;

    public LocalizedSourceTimelineAnalysisService(
        string ffprobePath,
        IMediaToolProcessRunner? runner = null,
        Action<string>? log = null)
    {
        _ffprobePath = ffprobePath;
        _runner = runner ?? new MediaToolProcessRunner();
        _log = log;
    }

    public async Task<SourceTimelineRecoveryAnalysis> AnalyzeAsync(
        string sourcePath,
        MediaProbeStreamInfo sourceVideo,
        double? authoritativeDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceVideo);

        if (!RationalFrameRate.TryParse(sourceVideo.NominalFrameRateRational, out RationalFrameRate rate))
            return NotEligible("The source has no exact positive nominal rational frame rate.");
        if (sourceVideo.FrameCount is not > 0)
            return NotEligible("The source video frame count is not authoritative/measured.");
        if (authoritativeDurationSeconds is not > 0 || !double.IsFinite(authoritativeDurationSeconds.Value))
            return NotEligible("The authoritative source duration is unavailable.");

        var packets = new List<SourceTimelinePacket>();
        MediaToolProcessResult result = await _runner.RunAsync(
            new MediaToolProcessRequest
            {
                FileName = _ffprobePath,
                Timeout = TimeSpan.FromMinutes(2),
                Arguments =
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "packet=pts_time,dts_time,duration_time",
                    "-of", "csv=p=0",
                    sourcePath
                ],
                StandardOutputLineCallback = line =>
                {
                    if (TryParsePacket(line, out SourceTimelinePacket? packet) && packet is not null)
                        packets.Add(packet);
                }
            }, cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
            return NotEligible("Full-stream packet timing diagnosis timed out.");
        if (result.ExitCode != 0)
            return NotEligible("Full-stream packet timing diagnosis failed.");

        SourceTimelineRecoveryAnalysis analysis = Diagnose(new(
            rate,
            sourceVideo.FrameCount,
            authoritativeDurationSeconds,
            sourceVideo.StartTimeSeconds,
            packets));
        _log?.Invoke($"[TimingRecovery] {analysis.DescribeEvidence()}; eligible={analysis.IsEligible}; reason={analysis.Reason}");
        return analysis;
    }

    internal static SourceTimelineRecoveryAnalysis Diagnose(SourceTimelineEvidence evidence)
    {
        if (evidence.NominalFrameRate is not { IsValid: true } rate)
            return NotEligible("The source has no exact positive nominal rational frame rate.");
        if (evidence.SourceFrameCount is not > 0)
            return NotEligible("The source video frame count is not authoritative/measured.");
        if (evidence.AuthoritativeDurationSeconds is not > 0 || !double.IsFinite(evidence.AuthoritativeDurationSeconds.Value))
            return NotEligible("The authoritative source duration is unavailable.");

        IReadOnlyList<SourceTimelinePacket> packets = evidence.Packets;
        if (packets.Count != evidence.SourceFrameCount.Value)
            return NotEligible($"Packet count {packets.Count} does not equal measured source frame count {evidence.SourceFrameCount.Value}.");
        if (packets.Any(packet => packet.DurationSeconds is not > 0 || !double.IsFinite(packet.DurationSeconds.Value)))
            return NotEligible("At least one video packet has no finite positive sample duration.");
        if (packets.Any(packet => packet.DecodeTimeSeconds is null || !double.IsFinite(packet.DecodeTimeSeconds.Value)))
            return NotEligible("The packet decode-time ordering cannot be verified.");

        for (int index = 1; index < packets.Count; index++)
        {
            if (packets[index].DecodeTimeSeconds!.Value < packets[index - 1].DecodeTimeSeconds!.Value)
                return NotEligible("Packet decode-time ordering is non-monotonic.");
        }

        double cadenceSeconds = (double)rate.Denominator / rate.Numerator;
        double reconstructedDuration = Math.Max(0, evidence.SourceFrameCount.Value - 1) * cadenceSeconds;
        double authoritativeDuration = evidence.AuthoritativeDurationSeconds.Value;
        if (Math.Abs(reconstructedDuration - authoritativeDuration) > ExistingDurationBoundarySeconds)
            return NotEligible($"Frame-index reconstruction changes the authoritative duration materially ({reconstructedDuration:0.######}s vs {authoritativeDuration:0.######}s).");

        double packetDurationTotal = packets.Sum(packet => packet.DurationSeconds!.Value);
        if (Math.Abs(packetDurationTotal - authoritativeDuration) > ExistingDurationBoundarySeconds)
            return NotEligible($"Packet sample durations do not agree with the authoritative duration ({packetDurationTotal:0.######}s vs {authoritativeDuration:0.######}s).");

        double stableTolerance = cadenceSeconds * StableCadenceRelativeTolerance;
        bool IsStable(SourceTimelinePacket packet) =>
            Math.Abs(packet.DurationSeconds!.Value - cadenceSeconds) <= stableTolerance;
        bool IsShort(SourceTimelinePacket packet) =>
            packet.DurationSeconds!.Value <= cadenceSeconds * ShortDurationRelativeThreshold;

        var candidates = new List<(int Start, int End, double ShortDuration, double CompensatingDuration)>();
        for (int start = 0; start < packets.Count; start++)
        {
            if (!IsShort(packets[start]))
                continue;

            int end = start;
            double shortDuration = packets[start].DurationSeconds!.Value;
            while (end + 1 < packets.Count && IsShort(packets[end + 1]))
            {
                end++;
                shortDuration += packets[end].DurationSeconds!.Value;
            }

            int count = end - start + 1;
            if (count < MinimumShortRegionPackets || end + 1 >= packets.Count)
            {
                start = end;
                continue;
            }

            double expectedCompensatingDuration = count * cadenceSeconds - shortDuration;
            double nextDuration = packets[end + 1].DurationSeconds!.Value;
            double compensationTolerance = Math.Max(cadenceSeconds * 4, expectedCompensatingDuration * .02);
            if (expectedCompensatingDuration > cadenceSeconds * 4 &&
                nextDuration > cadenceSeconds * 4 &&
                Math.Abs(nextDuration - expectedCompensatingDuration) <= compensationTolerance)
            {
                candidates.Add((start, end, shortDuration, nextDuration));
            }

            start = end;
        }

        if (candidates.Count != 1)
            return NotEligible(candidates.Count == 0
                ? "No localized short-duration region with a compensating long sample was found."
                : "Multiple candidate malformed timing regions were found; reconstruction is not unambiguous.");

        (int shortStart, int shortEnd, double shortTotal, double compensatingDuration) = candidates[0];
        int stablePacketCount = packets.Count(IsStable);
        double stableFraction = (double)stablePacketCount / packets.Count;
        if (stableFraction < MinimumStablePacketFraction)
            return NotEligible($"Only {stableFraction:P2} of samples conform to the nominal CFR cadence.");
        if (shortEnd - shortStart + 1 > packets.Count * .10)
            return NotEligible("The malformed timing region is not localized to a bounded portion of the stream.");

        return new(
            SourceTimelineRecoveryClassification.LocalizedSourceTimelineCorruption,
            "Stable measured CFR cadence surrounds one localized near-zero sample-duration run and its compensating long duration; deterministic frame-index reconstruction is safe under the existing duration boundary.",
            rate,
            evidence.SourceFrameCount,
            packets.Count,
            stablePacketCount,
            shortEnd - shortStart + 1,
            shortStart,
            shortEnd,
            shortTotal,
            compensatingDuration,
            reconstructedDuration,
            authoritativeDuration);
    }

    private static bool TryParsePacket(string line, out SourceTimelinePacket? packet)
    {
        packet = null;
        string[] fields = line.Split(',');
        if (fields.Length < 3)
            return false;

        static double? Parse(string value) =>
            double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed)
                ? parsed
                : null;

        packet = new SourceTimelinePacket(Parse(fields[0]), Parse(fields[1]), Parse(fields[2]));
        return true;
    }

    private static SourceTimelineRecoveryAnalysis NotEligible(string reason) =>
        new(SourceTimelineRecoveryClassification.None, reason);
}
