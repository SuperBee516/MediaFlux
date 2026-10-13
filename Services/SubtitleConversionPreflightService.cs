using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Validates planned text subtitles before the main staged encode.</summary>
internal sealed class SubtitleConversionPreflightService
{
    private static readonly HashSet<string> TextSubtitleCodecs = new(
        new[] { "ass", "ssa", "mov_text", "tx3g", "webvtt", "subrip", "srt" },
        StringComparer.OrdinalIgnoreCase);
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;

    public SubtitleConversionPreflightService(string ffmpegPath, IMediaToolProcessRunner? runner = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner ?? new MediaToolProcessRunner();
    }

    public async Task<SubtitleConversionPreflightResult> ValidateAsync(
        EncodingInputSource input, OutputContainerDecision decision, CancellationToken cancellationToken = default)
    {
        if (input.Kind != EncodingInputKind.File)
            return SubtitleConversionPreflightResult.Passed;
        foreach (StreamCompatibilityPlan plan in decision.StreamPlans.Where(plan =>
                     plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                     plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode &&
                     (decision.Resolved == OutputContainer.Mp4 ||
                      plan.Action == StreamCompatibilityAction.Transcode) &&
                     (TextSubtitleCodecs.Contains(plan.Codec) ||
                      string.Equals(plan.TargetCodec, "mov_text", StringComparison.OrdinalIgnoreCase))))
        {
            MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffmpegPath,
                Arguments = BuildArguments(input.InputPath, plan.StreamIndex),
                Timeout = TimeSpan.FromMinutes(2),
                SendQuitOnCancellation = true
            }, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut)
                return new(false, plan.StreamIndex, result.TimedOut,
                    result.TimedOut
                        ? $"Text subtitle stream #{plan.StreamIndex} conversion validation timed out."
                        : $"Text subtitle stream #{plan.StreamIndex} is malformed or noncompliant and could not be decoded safely.",
                    result.StandardError);
        }
        return SubtitleConversionPreflightResult.Passed;
    }

    internal static IReadOnlyList<string> BuildArguments(string inputPath, int streamIndex) =>
    ["-hide_banner", "-v", "error", "-xerror", "-i", inputPath, "-map", $"0:{streamIndex}", "-c:s", "mov_text", "-f", "null", "-"];
}

internal sealed record SubtitleConversionPreflightResult(bool Success, int? StreamIndex, bool TimedOut, string ErrorMessage, string Diagnostics)
{
    public static SubtitleConversionPreflightResult Passed { get; } = new(true, null, false, "", "");
}
