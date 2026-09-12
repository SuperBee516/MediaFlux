using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeProgressArbitratorTests
{
    [Fact]
    public void AdvancingTimestampRemainsPrimary()
    {
        var tracker = new EncodeProgressArbitrator(TimeSpan.FromSeconds(100), 1000, 10, true);
        EncodeProgressArbitrationResult result = tracker.Update(10, 100, 60, 2, 0, null);

        Assert.Equal(EncodeProgressBasis.Timestamp, result.Basis);
        Assert.Equal(10, result.Percent, 3);
    }

    [Fact]
    public void StalledTimestampSwitchesToMeasuredFramesAndRemainsMonotonic()
    {
        var tracker = new EncodeProgressArbitrator(TimeSpan.FromSeconds(100), 1000, 10, true);
        tracker.Update(10, 100, 60, 2, 0, null);
        tracker.Update(10, 200, 60, 2, 0, null);
        tracker.Update(10, 300, 60, 2, 0, null);
        EncodeProgressArbitrationResult result = tracker.Update(10, 400, 60, 2, 0, null);

        Assert.Equal(EncodeProgressBasis.MeasuredFrames, result.Basis);
        Assert.Equal(40, result.Percent, 3);
        Assert.True(result.TimestampStalled);
    }

    [Fact]
    public void MissingTimestampUsesDerivedCfrProgressWhenEligible()
    {
        var tracker = new EncodeProgressArbitrator(TimeSpan.FromSeconds(100), null, 10, true);
        EncodeProgressArbitrationResult result = tracker.Update(null, 250, 60, 2, 0, null);

        Assert.Equal(EncodeProgressBasis.DerivedCfrFrames, result.Basis);
        Assert.Equal(25, result.Percent, 3);
        Assert.Equal(1000, result.TotalFrames);
    }

    [Fact]
    public void UnknownTimingDoesNotInventPercentage()
    {
        var tracker = new EncodeProgressArbitrator(TimeSpan.FromSeconds(100), null, 10, false);
        EncodeProgressArbitrationResult result = tracker.Update(null, 250, 60, 2, 0, null);

        Assert.Equal(EncodeProgressBasis.Indeterminate, result.Basis);
        Assert.Equal(0, result.Percent);
    }
}
