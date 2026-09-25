using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingRetryPolicyTests
{
    [Fact]
    public void SourceUnrecoverableIsNotAutomaticallyRetried()
    {
        Assert.False(EncodingRetryPolicy.AllowsAutomaticRetry(EncodingTerminalResult.SourceUnrecoverable));
    }

    [Theory]
    [InlineData(EncodingTerminalResult.EncodeFailed)]
    [InlineData(EncodingTerminalResult.RecoveryFailed)]
    [InlineData(EncodingTerminalResult.ValidationFailed)]
    [InlineData(EncodingTerminalResult.FinalizationFailed)]
    public void OtherTerminalFailuresKeepExistingRetryEligibility(EncodingTerminalResult terminalResult)
    {
        Assert.True(EncodingRetryPolicy.AllowsAutomaticRetry(terminalResult));
    }
}
