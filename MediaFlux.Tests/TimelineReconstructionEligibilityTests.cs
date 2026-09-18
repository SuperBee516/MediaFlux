using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class TimelineReconstructionEligibilityTests
{
    [Fact]
    public void ExactCfrCompleteSourceQualifiesWithoutCompensatingPattern()
    {
        TimelineReconstructionEligibility result = TimelineReconstructionEligibilityService.Evaluate(Evidence());

        Assert.True(result.IsEligible, result.Reason);
        Assert.Equal("30000/1001", result.FrameRate!.Value.Text);
        Assert.True(result.ExactPacketCadence);
        Assert.True(result.DecodeOrderingMonotonic);
        Assert.True(result.AudioCoverageCompatible);
    }

    [Fact]
    public void VariablePacketCadenceCannotQualify()
    {
        List<SourceTimelinePacket> packets = Packets().ToList();
        packets[12] = packets[12] with { DurationSeconds = .05 };

        Assert.False(TimelineReconstructionEligibilityService.Evaluate(Evidence(packets: packets)).IsEligible);
    }

    [Fact]
    public void IncompleteDecodeCannotQualify() =>
        Assert.False(TimelineReconstructionEligibilityService.Evaluate(Evidence(decodedFrames: 299)).IsEligible);

    [Fact]
    public void MaterialReconstructedDurationMismatchCannotQualify() =>
        Assert.False(TimelineReconstructionEligibilityService.Evaluate(Evidence(authoritativeDuration: 8)).IsEligible);

    [Fact]
    public void NonMonotonicDtsCannotQualify()
    {
        List<SourceTimelinePacket> packets = Packets().ToList();
        packets[20] = packets[20] with { DecodeTimeSeconds = packets[19].DecodeTimeSeconds };

        TimelineReconstructionEligibility result = TimelineReconstructionEligibilityService.Evaluate(Evidence(packets: packets));
        Assert.False(result.IsEligible);
        Assert.Contains("DTS", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaterialAudioVideoDurationMismatchCannotQualify() =>
        Assert.False(TimelineReconstructionEligibilityService.Evaluate(Evidence(audioDuration: 8)).IsEligible);

    [Fact]
    public void RationalNtscDurationUsesExactRateWithoutIntegerRounding()
    {
        TimelineReconstructionEligibility result = TimelineReconstructionEligibilityService.Evaluate(Evidence());
        Assert.Equal(300d * 1001d / 30000d, result.ReconstructedDurationSeconds, 9);
    }

    private static TimelineReconstructionEvidence Evidence(
        IReadOnlyList<SourceTimelinePacket>? packets = null,
        long decodedFrames = 300,
        double? authoritativeDuration = null,
        double? audioDuration = null) => new(
            new RationalFrameRate(30000, 1001), 300,
            authoritativeDuration ?? 300d * 1001d / 30000d,
            0, packets ?? Packets(), true, decodedFrames,
            new[] { new TimelineReconstructionAudioEvidence(0, audioDuration ?? 300d * 1001d / 30000d) });

    private static IReadOnlyList<SourceTimelinePacket> Packets()
    {
        double cadence = 1001d / 30000d;
        return Enumerable.Range(0, 300)
            .Select(index => new SourceTimelinePacket(
                index == 120 ? (index - 4) * cadence : index * cadence,
                index * cadence,
                cadence))
            .ToArray();
    }
}
