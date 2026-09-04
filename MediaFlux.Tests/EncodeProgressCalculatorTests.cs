using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeProgressCalculatorTests
{
    [Fact]
    public void KnownDurationUsesEncodedMediaPosition()
    {
        Assert.Equal(15.8, EncodeProgressCalculator.CalculatePercentValue(450, 2841), precision: 1);
        Assert.Equal(16, EncodeProgressCalculator.CalculatePercent(450, 2841));
    }

    [Fact]
    public void UnknownDurationDoesNotInventProgress()
    {
        Assert.Equal(0, EncodeProgressCalculator.CalculatePercent(450, null));
        Assert.Equal(12, EncodeProgressCalculator.CalculatePercent(450, null, 12));
    }

    [Fact]
    public void MalformedOrTransientReportsCannotDecreaseProgress()
    {
        Assert.Equal(20, EncodeProgressCalculator.CalculatePercent(double.NaN, 1000, 20));
        Assert.Equal(20, EncodeProgressCalculator.CalculatePercent(100, 1000, 20));
        Assert.Equal(100, EncodeProgressCalculator.CalculatePercent(1000, 1000));
    }
}
