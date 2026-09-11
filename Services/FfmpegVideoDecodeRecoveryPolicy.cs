using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record FfmpegVideoDecodeRecoveryDecision(
    bool Eligible,
    string Evidence)
{
    public static FfmpegVideoDecodeRecoveryDecision NotEligible(string reason) =>
        new(false, reason);
}

/// <summary>Pure, classifier-backed gate for the single Intelligent video recovery retry.</summary>
internal static class FfmpegVideoDecodeRecoveryPolicy
{
    public static FfmpegVideoDecodeRecoveryDecision Evaluate(
        string? standardError,
        ContainerCompatibilityPolicy policy,
        bool cancellationRequested,
        bool recoveryAlreadyAttempted,
        bool requestedHardwareEncoder,
        string outputPath)
    {
        if (policy != ContainerCompatibilityPolicy.Intelligent)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("compatibility policy is not Intelligent");
        if (cancellationRequested)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("cancellation was requested");
        if (recoveryAlreadyAttempted)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("video recovery was already attempted");

        FfmpegSourceTruncation truncation = FfmpegSourceTruncationClassifier.Classify(standardError);
        if (truncation.IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("source truncation evidence is present");

        FfmpegSourceDecodeCorruption corruption = FfmpegSourceDecodeCorruptionClassifier.Classify(standardError);
        if (!corruption.IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("reliable source video corruption evidence is absent");

        if (FfmpegStorageFailureClassifier.Classify(standardError, outputPath).IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("output/storage failure evidence is present");

        if (requestedHardwareEncoder && FfmpegNvencFailureClassifier.Classify(standardError).IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("video encoder failure evidence is present");

        if (SourceAudioDecodePreflightService.IsReliableAudioDecodeFailure(standardError) &&
            !corruption.IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("audio-only failure evidence is present");

        return new(true, corruption.DescribeEvidence());
    }
}
