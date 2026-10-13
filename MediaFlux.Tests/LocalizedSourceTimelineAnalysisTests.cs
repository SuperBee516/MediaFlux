using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LocalizedSourceTimelineAnalysisTests
{
    [Fact]
    public void StableCfrWithLocalizedCompensatedDurationIsEligible()
    {
        SourceTimelineRecoveryAnalysis result = LocalizedSourceTimelineAnalysisService.Diagnose(
            Evidence(BuildPackets(30, shortCount: 10)));

        Assert.True(result.IsEligible);
        Assert.Equal(SourceTimelineRecoveryClassification.LocalizedSourceTimelineCorruption, result.Classification);
        Assert.Equal("30/1", result.NominalFrameRate!.Value.Text);
        Assert.Equal(10, result.ShortDurationPacketCount);
        Assert.Equal(500, result.SourceFrameCount);
    }

    [Fact]
    public void HealthyCfrDoesNotTriggerRecovery()
    {
        SourceTimelineRecoveryAnalysis result = LocalizedSourceTimelineAnalysisService.Diagnose(
            Evidence(BuildPackets(30, shortCount: 0)));

        Assert.False(result.IsEligible);
        Assert.Contains("No localized short-duration region", result.Reason);
    }

    [Fact]
    public void VfrLikeTimingWithoutCompensatedCorruptionDoesNotTriggerRecovery()
    {
        List<SourceTimelinePacket> packets = BuildPackets(30, shortCount: 0).ToList();
        packets[250] = packets[250] with { DurationSeconds = 0.025 };
        packets[251] = packets[251] with { DurationSeconds = 0.0417333333333333 };

        SourceTimelineRecoveryAnalysis result = LocalizedSourceTimelineAnalysisService.Diagnose(
            Evidence(packets, 16.6333333333333));

        Assert.False(result.IsEligible);
    }

    [Fact]
    public void NonMonotonicDecodeOrderingRemainsRejected()
    {
        List<SourceTimelinePacket> packets = BuildPackets(30, shortCount: 10).ToList();
        (packets[200], packets[201]) = (packets[201], packets[200] with { DecodeTimeSeconds = packets[200].DecodeTimeSeconds });

        SourceTimelineRecoveryAnalysis result = LocalizedSourceTimelineAnalysisService.Diagnose(
            Evidence(packets));

        Assert.False(result.IsEligible);
        Assert.Contains("non-monotonic", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("24000/1001", 24000, 1001)]
    public void ExactRationalRatesAreReducedAndPreserved(string text, long numerator, long denominator)
    {
        Assert.True(RationalFrameRate.TryParse(text, out RationalFrameRate rate));
        Assert.Equal(numerator, rate.Numerator);
        Assert.Equal(denominator, rate.Denominator);
        Assert.Equal(text, rate.Text);
    }

    private static SourceTimelineEvidence Evidence(
        IReadOnlyList<SourceTimelinePacket> packets,
        double? duration = null) =>
        new(new RationalFrameRate(30, 1), packets.Count, duration ?? (packets.Count - 1) / 30d, 0, packets);

    private static IReadOnlyList<SourceTimelinePacket> BuildPackets(int rate, int shortCount)
    {
        const int total = 500;
        double cadence = 1d / rate;
        var packets = new List<SourceTimelinePacket>(total);
        for (int index = 0; index < total; index++)
        {
            double duration = cadence;
            if (shortCount > 0 && index >= 200 && index < 200 + shortCount)
                duration = .001;
            else if (shortCount > 0 && index == 200 + shortCount)
                duration = shortCount * cadence - shortCount * .001;

            packets.Add(new(index * cadence, index * cadence, duration));
        }

        return packets;
    }
}
