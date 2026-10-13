using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingServiceFinalizationStateTests
{
    [Fact]
    public void SuccessfulRecoveryFinalizationCompletesAfterRecovery()
    {
        EncodingTerminalResult result = EncodingService.ResolveTerminalResult(
            SuccessfulFinalization(), completedAfterRecovery: true);

        Assert.Equal(EncodingTerminalResult.CompletedAfterRecovery, result);
    }

    [Fact]
    public void PrimaryValidationFailureDoesNotDetermineSuccessfulRecoveryTerminalState()
    {
        EncodeFinalizationResult primaryFailure = FailedFinalization(EncodeFinalizationFailureKind.Validation);
        EncodeFinalizationResult recoveredSuccess = SuccessfulFinalization();

        Assert.Equal(
            EncodingTerminalResult.ValidationFailed,
            EncodingService.ResolveTerminalResult(primaryFailure, completedAfterRecovery: false));
        Assert.Equal(
            EncodingTerminalResult.CompletedAfterRecovery,
            EncodingService.ResolveTerminalResult(recoveredSuccess, completedAfterRecovery: true));
    }

    [Fact]
    public void FailedRecoveryFinalizationRemainsFailure()
    {
        EncodingTerminalResult result = EncodingService.ResolveTerminalResult(
            FailedFinalization(EncodeFinalizationFailureKind.Validation), completedAfterRecovery: true);

        Assert.Equal(EncodingTerminalResult.ValidationFailed, result);
    }

    [Theory]
    [InlineData(EncodeFinalizationFailureKind.Promotion)]
    [InlineData(EncodeFinalizationFailureKind.FinalVerification)]
    public void GenuineFinalizationFailuresRemainTerminalFailures(
        EncodeFinalizationFailureKind failureKind)
    {
        EncodingTerminalResult result = EncodingService.ResolveTerminalResult(
            FailedFinalization(failureKind), completedAfterRecovery: true);

        Assert.Equal(EncodingTerminalResult.FinalizationFailed, result);
    }

    [Fact]
    public void SuccessfulNormalFinalizationCompletes()
    {
        EncodingTerminalResult result = EncodingService.ResolveTerminalResult(
            SuccessfulFinalization(), completedAfterRecovery: false);

        Assert.Equal(EncodingTerminalResult.Completed, result);
    }

    private static EncodeFinalizationResult SuccessfulFinalization() => new()
    {
        Success = true,
        FinalOutputPath = "final.mp4",
        StagingPath = "final.mp4.partial"
    };

    private static EncodeFinalizationResult FailedFinalization(
        EncodeFinalizationFailureKind failureKind) => new()
    {
        Success = false,
        FailureKind = failureKind,
        ErrorMessage = "attempt failure",
        FinalOutputPath = "final.mp4",
        StagingPath = "final.mp4.partial"
    };
}
