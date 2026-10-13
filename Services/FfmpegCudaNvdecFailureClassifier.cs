namespace MediaFlux.Services;

internal sealed record FfmpegCudaNvdecFailure(
    bool IsCudaNvdecDeviceFailure,
    IReadOnlyList<string> MatchedEvidence)
{
    public string DescribeEvidence() => MatchedEvidence.Count == 0
        ? "none"
        : string.Join(" | ", MatchedEvidence);
}

internal static class FfmpegCudaNvdecFailureClassifier
{
    private static readonly string[] DeviceFailureSignatures =
    [
        "CUDA_ERROR_LAUNCH_FAILED",
        "CUDA_ERROR_MAP_FAILED",
        "cuMemcpy2DAsync",
        "cuvidMapVideoFrame",
        "Failed unlocking input buffer"
    ];

    public static FfmpegCudaNvdecFailure Classify(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
            return new(false, Array.Empty<string>());

        string[] matches = DeviceFailureSignatures
            .Where(signature => standardError.Contains(signature, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new(matches.Length > 0, matches);
    }

    public static bool ShouldRetryOnce(
        bool nvencActive,
        bool hardwareDecodeWasUsed,
        bool cancellationRequested,
        bool recoveryAlreadyAttempted,
        FfmpegCudaNvdecFailure failure) =>
        nvencActive && hardwareDecodeWasUsed && !cancellationRequested &&
        !recoveryAlreadyAttempted && failure.IsCudaNvdecDeviceFailure;

    public static bool ShouldRetryFilterGraphOnce(
        bool nvencActive,
        bool cudaFramesWereUsed,
        bool cancellationRequested,
        bool recoveryAlreadyAttempted,
        string? arguments,
        string? standardError)
    {
        if (!nvencActive || !cudaFramesWereUsed || cancellationRequested || recoveryAlreadyAttempted ||
            string.IsNullOrWhiteSpace(arguments) || string.IsNullOrWhiteSpace(standardError))
            return false;

        // Restrict this recovery to the observed scale_cuda -> auto_scale
        // negotiation failure. Generic filter errors and generic reinitializing
        // messages do not justify another production attempt.
        return arguments.Contains("-hwaccel_output_format cuda", StringComparison.OrdinalIgnoreCase) &&
            arguments.Contains("scale_cuda", StringComparison.OrdinalIgnoreCase) &&
            standardError.Contains("Impossible to convert between the formats supported by", StringComparison.OrdinalIgnoreCase) &&
            standardError.Contains("scale_cuda", StringComparison.OrdinalIgnoreCase) &&
            standardError.Contains("auto_scale", StringComparison.OrdinalIgnoreCase) &&
            standardError.Contains("Error reinitializing filters!", StringComparison.OrdinalIgnoreCase);
    }
}
