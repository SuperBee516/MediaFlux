using MediaFlux;
using Xunit;

namespace MediaFlux.Tests;

public sealed class UpdateEligibilityTests
{
    [Fact]
    public void IdleEmptyQueuePermitsUpdate() => Assert.False(UpdateManager.IsBusyForUpdate(false, 0, false, false));

    [Fact]
    public void IdleNonRunningQueuePermitsUpdate() => Assert.False(UpdateManager.IsBusyForUpdate(false, 0, false, false));

    [Theory]
    [InlineData(true, 0, false, false)]
    [InlineData(false, 1, false, false)]
    [InlineData(false, 0, true, false)]
    [InlineData(false, 0, false, true)]
    public void ActiveUnsafeWorkBlocksUpdate(bool encoding, int pendingImports, bool importing, bool duplicateScan) => Assert.True(UpdateManager.IsBusyForUpdate(encoding, pendingImports, importing, duplicateScan));

    [Fact]
    public void CompletionCancellationAndFailureRestoreEligibility()
    {
        using var import = new CancellationTokenSource();
        Assert.True(UpdateManager.IsBusyForUpdate(false, 0, import.IsCancellationRequested == false, false));
        import.Cancel();
        Assert.False(UpdateManager.IsBusyForUpdate(false, 0, import.IsCancellationRequested == false, false));
        Assert.False(UpdateManager.IsBusyForUpdate(false, 0, false, false));
    }
}
