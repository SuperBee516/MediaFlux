using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeEtaCalculatorTests
{
    [Fact]
    public void UsesAuthoritativeMediaTimeAndFfmpegSpeed()
    {
        double? eta = EncodeEtaCalculator.CalculateSeconds(1000, 890, 3.5);

        Assert.NotNull(eta);
        Assert.Equal(110d / 3.5d, eta.Value, 6);
    }

    [Theory]
    [InlineData(0, 10, 3.5)]
    [InlineData(1000, 890, 0)]
    [InlineData(1000, 890, -1)]
    public void InvalidInputsProduceUnknownEta(double duration, double encoded, double speed)
        => Assert.Null(EncodeEtaCalculator.CalculateSeconds(duration, encoded, speed));

    [Fact]
    public void CompletedMediaHasZeroEta()
        => Assert.Equal(0d, EncodeEtaCalculator.CalculateSeconds(1000, 1100, 3.5));
}
