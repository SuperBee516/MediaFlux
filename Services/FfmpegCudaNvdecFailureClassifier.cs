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
}
