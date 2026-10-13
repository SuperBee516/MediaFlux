using MediaFlux;
using System.Net;
using System.Net.Http;
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

    [Fact]
    public void LocalFileLockDoesNotSuggestRepositoryPrivacy()
    {
        string message = UpdateManager.BuildFailureMessage(new IOException("ai-benchmarks.db is being used by another process."));
        Assert.Equal(UpdateManager.UpdateFailureKind.LocalData, UpdateManager.ClassifyFailure(new IOException("locked")));
        Assert.DoesNotContain("private", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryAuthenticationFailureRetainsAccessGuidance()
    {
        var failure = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);
        string message = UpdateManager.BuildFailureMessage(failure);
        Assert.Equal(UpdateManager.UpdateFailureKind.RepositoryAccess, UpdateManager.ClassifyFailure(failure));
        Assert.Contains("Private repositories", message, StringComparison.OrdinalIgnoreCase);
    }
}
