namespace MediaFlux.Models;

internal sealed record TimelineReconstructionAudioEvidence(
    double? StartTimeSeconds,
    double? DurationSeconds);

internal sealed record TimelineReconstructionEvidence(
    RationalFrameRate? FrameRate,
    long? ProbedFrameCount,
    double? AuthoritativeDurationSeconds,
    double? VideoStartTimeSeconds,
    IReadOnlyList<SourceTimelinePacket> Packets,
    bool DecodeReachedEnd,
    long DecodedFrameCount,
    IReadOnlyList<TimelineReconstructionAudioEvidence> AudioStreams);

internal sealed record TimelineReconstructionEligibility(
    bool IsEligible,
    string Reason,
    RationalFrameRate? FrameRate = null,
    int PacketCount = 0,
    long DecodedFrameCount = 0,
    bool DecodeReachedEnd = false,
    double ReconstructedDurationSeconds = 0,
    double AuthoritativeDurationSeconds = 0,
    double DurationDeltaSeconds = 0,
    bool DecodeOrderingMonotonic = false,
    bool ExactPacketCadence = false,
    bool AudioCoverageCompatible = false)
{
    public string DescribeEvidence() =>
        $"TimelineReconstructionEligibility: eligible={IsEligible}; rate={FrameRate?.Text ?? "unknown"}; " +
        $"packets={PacketCount}; decoded-eof={DecodeReachedEnd}; decoded-frames={DecodedFrameCount}; " +
        $"exact-packet-cadence={ExactPacketCadence}; dts-monotonic={DecodeOrderingMonotonic}; " +
        $"reconstructed-duration={ReconstructedDurationSeconds:0.######}s; authoritative-duration={AuthoritativeDurationSeconds:0.######}s; " +
        $"duration-delta={DurationDeltaSeconds:0.######}s; audio-coverage-compatible={AudioCoverageCompatible}; reason={Reason}";
}
