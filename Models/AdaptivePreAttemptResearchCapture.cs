namespace MediaFlux.Models;

/// <summary>Explicit research opt-in. Never enables Policy C or changes encoding settings.</summary>
public sealed record AdaptivePreAttemptResearchCaptureOptions
{
    public bool Enabled { get; init; }
    public string CaptureRevision { get; init; } = "adaptive-pre-attempt-v1";
    public string? ImplementationId { get; init; }
    public string? ExecutableSha256 { get; init; }
    public string? FfmpegSha256 { get; init; }
    public string? FfprobeSha256 { get; init; }
    public string? ResearchConfigSha256 { get; init; }
    // Optional preregistered context for the approved one-source saved-job route.
    public AdaptivePreAttemptSourceContext? Source { get; init; }
}

public sealed record AdaptivePreAttemptSourceContext
{
    public required string CanonicalPath { get; init; }
    public string? CampaignCaseId { get; init; }
    public string? StableSourceId { get; init; }
    public string? Sha256 { get; init; }
    public long? ExpectedByteLength { get; init; }
    public DateTime? ExpectedLastWriteUtc { get; init; }
}
