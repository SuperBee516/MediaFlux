using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Decodes audio streams that an encode would otherwise stream-copy. This keeps
/// a corrupt compressed audio payload from being carried into a validated output.
/// </summary>
internal sealed class SourceAudioDecodePreflightService
{
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;

    public SourceAudioDecodePreflightService(string ffmpegPath, IMediaToolProcessRunner? runner = null)
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _runner = runner ?? new MediaToolProcessRunner();
    }

    public async Task<SourceAudioDecodePreflightResult> ValidateCopiedStreamsAsync(
        EncodingInputSource input,
        OutputContainerDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(decision);
        if (input.Kind != EncodingInputKind.File)
            return SourceAudioDecodePreflightResult.Passed;

        StreamCompatibilityPlan[] copiedAudio = decision.StreamPlans
            .Where(plan => plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                           plan.Action == StreamCompatibilityAction.Copy)
            .ToArray();
        for (int position = 0; position < copiedAudio.Length; position++)
        {
            StreamCompatibilityPlan stream = copiedAudio[position];
            MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffmpegPath,
                Arguments = BuildArguments(input.InputPath, stream.StreamIndex),
                Timeout = Timeout.InfiniteTimeSpan,
                SendQuitOnCancellation = true
            }, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || process.TimedOut)
            {
                bool reliableCorruption = !process.TimedOut && IsReliableAudioDecodeFailure(process.StandardError);
                string role = position == 0 ? "primary selected" : "secondary selected";
                string message = reliableCorruption
                    ? $"Source {role} audio stream #{stream.StreamIndex} contains undecodable or corrupt audio data. MediaFlux did not start the encode."
                    : $"MediaFlux could not fully decode source {role} audio stream #{stream.StreamIndex}; the encode was not started."
                        + (process.TimedOut ? " Audio preflight timed out." : " FFmpeg reported an ambiguous audio read/decode failure.");
                return new(false, stream.StreamIndex, reliableCorruption, message, process.StandardError);
            }
        }

        return SourceAudioDecodePreflightResult.Passed;
    }

    internal static IReadOnlyList<string> BuildArguments(string inputPath, int streamIndex) =>
    [
        "-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode",
        "-i", inputPath, "-map", $"0:{streamIndex}", "-vn", "-sn", "-dn", "-f", "null", "-"
    ];

    // The preflight maps only one known audio stream. Require a decoder-specific
    // signal, not merely arbitrary stderr, before describing it as corruption.
    internal static bool IsReliableAudioDecodeFailure(string? standardError) =>
        !string.IsNullOrWhiteSpace(standardError) &&
        (standardError.Contains("Error while decoding", StringComparison.OrdinalIgnoreCase) ||
         standardError.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) ||
         standardError.Contains("Invalid audio", StringComparison.OrdinalIgnoreCase) ||
         standardError.Contains("Header missing", StringComparison.OrdinalIgnoreCase));
}

internal sealed record SourceAudioDecodePreflightResult(
    bool Success,
    int? StreamIndex,
    bool IsReliableCorruption,
    string ErrorMessage,
    string Diagnostics)
{
    public static SourceAudioDecodePreflightResult Passed { get; } = new(true, null, false, "", "");
}
