using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record FfmpegVideoDecodeRecoveryDecision(
    bool Eligible,
    string Evidence,
    EncodeOutputValidationFailureEvidence? FrameDeficit = null)
{
    public static FfmpegVideoDecodeRecoveryDecision NotEligible(string reason) =>
        new(false, reason);
}

/// <summary>Pure, classifier-backed gate for the single Intelligent video recovery retry.</summary>
internal static class FfmpegVideoDecodeRecoveryPolicy
{
    public static FfmpegVideoDecodeRecoveryDecision EvaluateFrameDeficit(
        EncodeOutputValidationFailureEvidence? evidence,
        ContainerCompatibilityPolicy policy,
        SourceTimingAnalysis? sourceTiming,
        bool cancellationRequested,
        bool recoveryAlreadyAttempted,
        bool requestedHardwareEncoder)
    {
        if (policy != ContainerCompatibilityPolicy.Intelligent)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("compatibility policy is not Intelligent");
        if (cancellationRequested)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("cancellation was requested");
        if (recoveryAlreadyAttempted)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("video recovery was already attempted");
        if (!requestedHardwareEncoder)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("the requested encoder is not hardware NVENC");
        if (sourceTiming?.Classification != SourceTimingClassification.Cfr ||
            sourceTiming.AiEligibility != AiTimingEligibility.EligibleCurrentCfrPipeline)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("source is not eligible for the current CFR pipeline");
        if (evidence is null)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("authoritative frame-deficit validation evidence is absent");

        return new(true,
            $"source={evidence.ExpectedFrameCount}; output={evidence.ActualFrameCount}; " +
            $"delta={evidence.FrameDelta}; deficit={evidence.DeficitSeconds:0.###}s; " +
            $"source-duration={evidence.SourceDurationSeconds:0.###}s; output-duration={evidence.OutputDurationSeconds:0.###}s",
            evidence);
    }

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

        return EvaluateEvidence(standardError, requestedHardwareEncoder, outputPath, benchmark: false);
    }

    /// <summary>Benchmark-only classifier; retry ownership remains outside production encoding.</summary>
    public static FfmpegVideoDecodeRecoveryDecision EvaluateForBenchmark(
        string? standardError,
        bool cancellationRequested,
        bool requestedHardwareEncoder,
        string outputPath)
    {
        if (cancellationRequested)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("cancellation was requested");
        return EvaluateEvidence(standardError, requestedHardwareEncoder, outputPath, benchmark: true);
    }

    private static FfmpegVideoDecodeRecoveryDecision EvaluateEvidence(
        string? standardError,
        bool requestedHardwareEncoder,
        string outputPath,
        bool benchmark)
    {
        FfmpegSourceTruncation truncation = FfmpegSourceTruncationClassifier.Classify(standardError);
        if (truncation.IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("source truncation evidence is present");

        FfmpegSourceDecodeCorruption corruption = benchmark
            ? FfmpegSourceDecodeCorruptionClassifier.ClassifyForBenchmark(standardError)
            : FfmpegSourceDecodeCorruptionClassifier.Classify(standardError);
        if (!corruption.IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("reliable source video corruption evidence is absent");

        FfmpegDiagnosticSummary? diagnosticSummary = benchmark
            ? null
            : FfmpegSourceDecodeCorruptionClassifier.SummarizeDiagnostics(standardError);
        bool strongSourceEvidence = benchmark ||
            FfmpegSourceDecodeCorruptionClassifier.HasStrongSourceIntegrityEvidence(corruption, diagnosticSummary, standardError);
        bool knownStandaloneContainerCorruption = !benchmark &&
            FfmpegSourceDecodeCorruptionClassifier.HasKnownStandaloneContainerCorruption(corruption, diagnosticSummary);
        if (!strongSourceEvidence && !knownStandaloneContainerCorruption)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("corroborated source-integrity evidence is absent or follows an output failure");

        FfmpegStorageFailure storageFailure = FfmpegStorageFailureClassifier.Classify(standardError, outputPath);
        if (storageFailure.IsReliable &&
            !(strongSourceEvidence && FfmpegSourceDecodeCorruptionClassifier.IsMuxTeardownFailure(storageFailure)))
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("output/storage failure evidence is present");

        // A benchmark decoder failure that causally precedes teardown is primary;
        // ignore only consequential encoder/empty-output messages in that case.
        if (!benchmark && requestedHardwareEncoder && FfmpegNvencFailureClassifier.Classify(standardError).IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("video encoder failure evidence is present");

        if (SourceAudioDecodePreflightService.IsReliableAudioDecodeFailure(standardError) &&
            !corruption.IsReliable)
            return FfmpegVideoDecodeRecoveryDecision.NotEligible("audio-only failure evidence is present");

        return new(true, corruption.DescribeEvidence());
    }
}
