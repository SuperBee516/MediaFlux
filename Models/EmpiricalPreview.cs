namespace MediaFlux.Models;

/// <summary>Advisory queue projection; deliberately not AdaptiveQualitySelectionEvidence.</summary>
public sealed record EmpiricalPreviewCandidate(int Quality, double MeanVideoBytesPerSecond,
    double ProjectedVideoBytes, double ProjectedLowerBytes, double ProjectedTypicalBytes,
    double ProjectedUpperBytes, double ContainerBytes, AdaptiveSampleClassification Classification,
    bool Interpolated);

public sealed record EmpiricalPreviewSampleEvidence(int Quality, string Label, double StartSeconds,
    double RequestedSeconds, double MeasuredSeconds, long VideoBytes, double VideoBytesPerSecond);

public sealed record EmpiricalPreviewResult(EncoderQualityMechanism Mechanism,
    IReadOnlyList<int> Anchors, IReadOnlyList<EmpiricalPreviewCandidate> Candidates,
    int? PredictedSelectedQuality, int DisplayQuality, double ProjectedTotalBytes,
    double AncillaryBytes, long SourceBytes, long? ContractCeilingBytes,
    AdaptiveSelectionDisposition? LikelyDisposition, int RegionsPerAnchor, double SecondsPerAnchor,
    int CacheHits, int CacheMisses)
{
    public const string ModelId = "EmpiricalPreview";
    public long SourceWriteUtcTicks { get; init; }
    public string Confidence { get; init; } = "Measured sample projection";
    public string RangeMethod { get; init; } = "Observed sample minimum and maximum";
    public IReadOnlyList<EmpiricalPreviewSampleEvidence> SampleEvidence { get; init; } = Array.Empty<EmpiricalPreviewSampleEvidence>();
    public double ExpectedSavingsPercent => (SourceBytes - ProjectedTotalBytes) * 100d / SourceBytes;
    public string Description => $"Empirical projection at {Mechanism.ToString().ToUpperInvariant()}{DisplayQuality}: {ProjectedTotalBytes / (1024d * 1024):0.##} MiB; " +
        $"anchors {string.Join(", ", Anchors)}; {RegionsPerAnchor} temporal regions, {SecondsPerAnchor / RegionsPerAnchor:0.###} seconds per region; " +
        $"lower / typical / upper {Candidates.Single(c => c.Quality == DisplayQuality).ProjectedLowerBytes / (1024d * 1024):0.##} / {ProjectedTotalBytes / (1024d * 1024):0.##} / {Candidates.Single(c => c.Quality == DisplayQuality).ProjectedUpperBytes / (1024d * 1024):0.##} MiB; " +
        (LikelyDisposition is null ? "" : $"likely {(LikelyDisposition == AdaptiveSelectionDisposition.Skipped ? "skip" : "encode")}; storage ceiling {ContractCeilingBytes / (1024d * 1024):0.##} MiB; ") +
        "advisory only. Production revalidates quality and acceptance.";
    public string Diagnostic => FormattableString.Invariant(
        $"EstimateSource=EmpiricalPreview; confidence={Confidence}; advisory only; Empirical projection; mechanism={Mechanism}; anchors={string.Join(",", Anchors)}; candidates=[{string.Join("; ", Candidates.Select(c => FormattableString.Invariant($"quality={c.Quality},meanVideoRate={c.MeanVideoBytesPerSecond:F3} B/s,interpolated={c.Interpolated},lower={c.ProjectedLowerBytes:F0},typical={c.ProjectedTypicalBytes:F0},upper={c.ProjectedUpperBytes:F0}")))}]; predictedQuality={PredictedSelectedQuality}; displayQuality={DisplayQuality}; projectedVideoBytes={Candidates.Single(c => c.Quality == DisplayQuality).ProjectedVideoBytes:F0}; ancillaryBytes={AncillaryBytes:F0}; containerBytes={Candidates.Single(c => c.Quality == DisplayQuality).ContainerBytes:F0}; projectedTotalBytes={ProjectedTotalBytes:F0}; sourceBytes={SourceBytes}; expectedSavings={ExpectedSavingsPercent:F3}%; contractCeiling={ContractCeilingBytes}; likelyDisposition={LikelyDisposition}; temporalRegions={RegionsPerAnchor}; secondsPerAnchor={SecondsPerAnchor:F3}; cacheHits={CacheHits}; cacheMisses={CacheMisses}. Production revalidates with its full sampling profile.");
}
