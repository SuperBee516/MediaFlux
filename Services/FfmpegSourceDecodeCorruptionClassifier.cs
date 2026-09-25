namespace MediaFlux.Services;

/// <summary>
/// Identifies a narrowly defined set of FFmpeg decoder failures for diagnostics.
/// It never changes encode success or output validation decisions.
/// </summary>
internal sealed record FfmpegSourceDecodeCorruption(
    bool IsReliable,
    IReadOnlyList<string> MatchedEvidence)
{
    public bool HasStructuralEvidence => MatchedEvidence.Any(IsStructuralSignature);
    public bool HasDecoderRejection => MatchedEvidence.Any(IsDecoderSignature);

    public bool IsStrongSourceIntegrityEvidence =>
        HasStructuralEvidence &&
        MatchedEvidence.Count(IsStructuralSignature) >= 2 &&
        (HasDecoderRejection || MatchedEvidence.Any(IsContainerSignature) ||
         MatchedEvidence.Any(value => value.Equals("corrupt input packet in stream 0", StringComparison.OrdinalIgnoreCase)));

    public string DescribeEvidence() => MatchedEvidence.Count == 0
        ? "none"
        : string.Join(" | ", MatchedEvidence);

    private static bool IsStructuralSignature(string value) =>
        value.Contains("Invalid NAL unit size", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("missing picture in access unit", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Error splitting the input into NAL units", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("missing mandatory atoms", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("broken header", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid as first byte of an EBML number", StringComparison.OrdinalIgnoreCase);

    private static bool IsContainerSignature(string value) =>
        value.Contains("missing mandatory atoms", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("broken header", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid as first byte of an EBML number", StringComparison.OrdinalIgnoreCase);

    private static bool IsDecoderSignature(string value) =>
        value.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Error processing packet in decoder", StringComparison.OrdinalIgnoreCase) ||
        (value.Contains("Error while decoding stream", StringComparison.OrdinalIgnoreCase) &&
         value.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase));
}

internal static class FfmpegSourceDecodeCorruptionClassifier
{
    internal static bool ShouldAttemptAutomaticRecovery(
        bool automaticRecoveryDisabled,
        bool isFileInput,
        bool cancellationRequested,
        bool strongSourceIntegrityEvidence) =>
        !automaticRecoveryDisabled && isFileInput && !cancellationRequested && strongSourceIntegrityEvidence;

    private static readonly string[] BitstreamFailureSignatures =
    [
        "Invalid NAL unit size",
        "missing picture in access unit",
        "Error splitting the input into NAL units"
    ];

    private static readonly string[] DecoderRejectionSignatures =
    [
        "Error submitting packet to decoder: Invalid data found when processing input",
        "Error processing packet in decoder: Invalid data found when processing input",
        "Error while decoding stream #0:0: Invalid data found when processing input"
    ];

    private static readonly string[] ContainerCorruptionSignatures =
    [
        "invalid as first byte of an EBML number",
        "missing mandatory atoms",
        "broken header"
    ];

    private static readonly string[] VideoPacketCorruptionSignatures =
    [
        "corrupt input packet in stream 0"
    ];

    public static FfmpegSourceDecodeCorruption Classify(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
            return new(false, Array.Empty<string>());

        string[] bitstreamMatches = BitstreamFailureSignatures
            .Where(signature => standardError.Contains(signature, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] decoderMatches = DecoderRejectionSignatures
            .Where(signature => standardError.Contains(signature, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] containerMatches = ContainerCorruptionSignatures
            .Where(signature => standardError.Contains(signature, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] packetMatches = VideoPacketCorruptionSignatures
            .Where(signature => standardError.Contains(signature, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new(decoderMatches.Length > 0 || containerMatches.Length > 0,
            bitstreamMatches.Concat(decoderMatches).Concat(containerMatches).Concat(packetMatches).ToArray());
    }

    public static bool HasStrongSourceIntegrityEvidence(
        FfmpegSourceDecodeCorruption rawCorruption,
        FfmpegDiagnosticSummary? diagnosticSummary)
    {
        if (rawCorruption.IsStrongSourceIntegrityEvidence)
            return true;

        if (diagnosticSummary?.Classification is not
            {
                PrimaryCategory: FfmpegDiagnosticCategory.SourceIntegrity,
                Confidence: FfmpegDiagnosticConfidence.High
            })
            return false;

        IReadOnlyList<string> families = diagnosticSummary.Classification.SupportingFamilies;
        bool hasNalStructure = families.Contains("Invalid NAL unit size", StringComparer.OrdinalIgnoreCase) &&
            families.Contains("Error splitting input into NAL units", StringComparer.OrdinalIgnoreCase);
        bool hasDecoderRejection = families.Any(family =>
            family.Equals("Invalid input data", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("Decoder packet submission failure", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("Decoder packet processing failure", StringComparison.OrdinalIgnoreCase));
        return hasNalStructure && hasDecoderRejection;
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
