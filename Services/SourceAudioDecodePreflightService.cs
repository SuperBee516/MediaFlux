using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Classifies reliable audio decoder evidence from the actual encode.</summary>
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
        await Task.CompletedTask;
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
        (standardError.Contains("Error while decoding stream", StringComparison.OrdinalIgnoreCase) ||
         standardError.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) ||
         standardError.Contains("Invalid audio", StringComparison.OrdinalIgnoreCase) ||
         standardError.Contains("Header missing", StringComparison.OrdinalIgnoreCase)) &&
        (standardError.Contains("audio", StringComparison.OrdinalIgnoreCase) || standardError.Contains("stream #0:", StringComparison.OrdinalIgnoreCase));

    internal static int? FindCorruptAudioStreamIndex(string? standardError) =>
        IsReliableAudioDecodeFailure(standardError)
            ? System.Text.RegularExpressions.Regex.Matches(standardError!, @"stream\s+#\d+:(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(match => int.TryParse(match.Groups[1].Value, out int index) ? index : (int?)null)
                .FirstOrDefault(index => index.HasValue)
            : null;
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
