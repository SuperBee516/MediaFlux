namespace MediaFlux.Models;

/// <summary>A resolved total-file-byte acceptance requirement, independent of encode predictions.</summary>
public sealed record StorageSavingsContract(
    bool Applies,
    long? SourceSizeBytes,
    int? MinimumSavingsPercent,
    long? MinimumSavingsBytes,
    long? MaximumAcceptedOutputBytes,
    string Reason)
{
    public static StorageSavingsContract Disabled { get; } =
        new(false, null, null, null, null, "Storage Savings Mode does not apply to this job.");
}

public enum StorageSavingsAcceptance { NotApplicable, Accepted, Rejected }

/// <summary>Actual validated candidate evidence. Percentages are diagnostic only.</summary>
public sealed record StorageSavingsEvaluation(
    StorageSavingsContract Contract,
    StorageSavingsAcceptance Acceptance,
    long CandidateOutputBytes,
    long? ActualSavingsBytes,
    double? ActualSavingsPercent,
    string Reason);
