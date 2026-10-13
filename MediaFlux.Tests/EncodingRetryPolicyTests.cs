using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingRetryPolicyTests
{
    [Fact]
    public void StoragePolicyRejectionIsTerminalWithoutAutomaticRetry()
    {
        Assert.False(EncodingRetryPolicy.AllowsAutomaticRetry(EncodingTerminalResult.StoragePolicyRejected));
        Assert.False(EncodingRetryPolicy.AllowsAutomaticRetry(EncodingTerminalResult.StoragePolicyRejected, false));
    }
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
        Assert.True(EncodingRetryPolicy.AllowsAutomaticRetry(terminalResult, hasResearchExperimentAssignment: false));
        Assert.False(EncodingRetryPolicy.AllowsAutomaticRetry(terminalResult, hasResearchExperimentAssignment: true));
    }
}
