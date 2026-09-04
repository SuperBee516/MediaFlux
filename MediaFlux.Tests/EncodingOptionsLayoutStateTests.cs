using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingOptionsLayoutStateTests
{
    [Fact]
    public void InitialStateAppliesOnce() => Assert.True(EncodingOptionsLayoutState.ShouldApply(null, false));

    [Fact]
    public void RepeatedSizeEventsWithSameDesiredStateDoNotReapply() => Assert.False(EncodingOptionsLayoutState.ShouldApply(false, false));

    [Fact]
    public void WidthThresholdChangeStillApplies() => Assert.True(EncodingOptionsLayoutState.ShouldApply(false, true));
}
