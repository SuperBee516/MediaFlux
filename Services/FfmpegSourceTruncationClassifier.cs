namespace MediaFlux.Services;

/// <summary>Classifies explicit FFmpeg end-of-media diagnostics after a nonzero encode exit.</summary>
internal sealed record FfmpegSourceTruncation(bool IsReliable, IReadOnlyList<string> MatchedEvidence);

internal static class FfmpegSourceTruncationClassifier
{
    private static readonly string[] Signatures =
    [
        "unexpected end of file",
        "partial file",
        "truncated"
    ];

    public static FfmpegSourceTruncation Classify(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
            return new(false, Array.Empty<string>());
        string[] matches = Signatures.Where(signature => standardError.Contains(
            signature, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(matches.Length > 0, matches);
    }
}
