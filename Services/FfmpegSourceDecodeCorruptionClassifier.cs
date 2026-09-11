namespace MediaFlux.Services;

/// <summary>
/// Identifies a narrowly defined set of FFmpeg decoder failures for diagnostics.
/// It never changes encode success or output validation decisions.
/// </summary>
internal sealed record FfmpegSourceDecodeCorruption(
    bool IsReliable,
    IReadOnlyList<string> MatchedEvidence)
{
    public string DescribeEvidence() => MatchedEvidence.Count == 0
        ? "none"
        : string.Join(" | ", MatchedEvidence);
}

internal static class FfmpegSourceDecodeCorruptionClassifier
{
    // Require both a bitstream/access-unit parse failure and decoder rejection.
    // This prevents a lone, version-specific FFmpeg diagnostic from changing the
    // user-visible error while retaining the concrete evidence in the log.
    private static readonly string[] BitstreamFailureSignatures =
    [
        "Invalid NAL unit size",
        "missing picture in access unit",
        "Error splitting the input into NAL units"
    ];

    private const string DecoderRejectionSignature =
        "Error submitting packet to decoder: Invalid data found when processing input";

    public static FfmpegSourceDecodeCorruption Classify(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
            return new(false, Array.Empty<string>());

        string[] bitstreamMatches = BitstreamFailureSignatures
            .Where(signature => standardError.Contains(signature, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        bool decoderRejected = standardError.Contains(
            DecoderRejectionSignature,
            StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> evidence = bitstreamMatches
            .Concat(decoderRejected ? [DecoderRejectionSignature] : Array.Empty<string>())
            .ToArray();
        return new(bitstreamMatches.Length > 0 && decoderRejected, evidence);
    }

    /// <summary>
    /// Benchmark-only classification for FFmpeg's causal decoder/teardown sequence.
    /// The production classifier intentionally remains unchanged.
    /// </summary>
    public static FfmpegSourceDecodeCorruption ClassifyForBenchmark(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
            return new(false, Array.Empty<string>());

        string[] lines = standardError.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        int decoderIndex = Array.FindIndex(lines, line =>
            line.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error processing packet in decoder", StringComparison.OrdinalIgnoreCase));
        if (decoderIndex < 0)
            return new(false, Array.Empty<string>());

        int bitstreamIndex = Array.FindIndex(lines, line =>
            line.Contains("Invalid NAL unit size", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("missing picture in access unit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error splitting the input into NAL units", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("mmco: unref short failure", StringComparison.OrdinalIgnoreCase));
        if (bitstreamIndex < 0 || bitstreamIndex > decoderIndex)
            return new(false, Array.Empty<string>());

        string[] evidence = lines
            .Take(decoderIndex + 1)
            .Where(line => line.Contains("mmco: unref short failure", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Invalid NAL unit size", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("missing picture in access unit", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Error splitting the input into NAL units", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Error processing packet in decoder", StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();
        return new(true, evidence);
    }
}
