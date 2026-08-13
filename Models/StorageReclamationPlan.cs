namespace MediaFlux.Models;

public enum StorageReclamationStrategy
{
    SafestFirst = 0,
    AvoidReencoding = 1,
    IncludeReencoding = 2,
    MaximumPotential = 3,
    BestSavingsEfficiency = 4
}

public enum StorageReclamationActionCategory
{
    ExactDuplicateCleanup = 0,
    ReviewedVisualFamilyCleanup = 1,
    ReviewedVisualDuplicateCleanup = 2,
    PolicyReencode = 3,
    Remux = 4,
    ReviewRequired = 5
}

public enum StorageReclamationSourceSubsystem
{
    ExactDuplicates = 0,
    VisualFamilies = 1,
    VisualPairs = 2,
    LibraryPolicy = 3
}

public enum StorageReclamationSafetyState
{
    Ready = 0,
    ReviewRequired = 1,
    Blocked = 2
}

public sealed record StorageReclamationPlanItem
{
    public string ItemId { get; init; } = Guid.NewGuid().ToString("N");
    public long FileId { get; init; }
    public string SourcePath { get; init; } = "";
    public string LocationPath { get; init; } = "";
    public string PhysicalIdentityKey { get; init; } = "";
    public StorageReclamationActionCategory ActionCategory { get; init; }
    public StorageReclamationSourceSubsystem SourceSubsystem { get; init; }
    public long ExpectedReclaimBytes { get; init; }
    public LibraryPolicyConfidence Confidence { get; init; }
    public StorageReclamationSafetyState SafetyState { get; init; }
    public string Reason { get; init; } = "";
    public long? KeeperFileId { get; init; }
    public string KeeperPath { get; init; } = "";
    public long? ExactGroupId { get; init; }
    public long? VisualGroupId { get; init; }
    public long? VisualFamilyId { get; init; }
    public string PolicyId { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public LibraryPolicyQueueItem? PolicyQueueIntent { get; init; }
    public bool RequiresUserReview { get; init; }
    public bool IsCurrentlyExecutable { get; init; }
    public string BlockingReason { get; init; } = "";
    public bool Included { get; init; }
    public double? EstimatedProcessingHours { get; init; }
    public double? HistoricalThroughputGbPerHour { get; init; }
    public double? SavingsPerComputeHourGb { get; init; }
    public double? EstimatedSpeedX { get; init; }
    public double? EstimatedFastProcessingHours { get; init; }
    public double? EstimatedSlowProcessingHours { get; init; }
    public RuntimeEstimateConfidence RuntimeConfidence { get; init; }
    public int RuntimeSampleCount { get; init; }
    public string RuntimeExplanation { get; init; } = "";
}

public sealed record StorageReclamationCategoryTotal(
    StorageReclamationActionCategory Category,
    int ItemCount,
    long ReadyBytes,
    long ReviewDependentBytes);

public sealed record StorageReclamationLocationTotal(
    string LocationPath,
    int ItemCount,
    long ReadyBytes,
    long ReviewDependentBytes);

public sealed record StorageReclamationPlan
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public long RequestedReclaimBytes { get; init; }
    public long ProjectedReclaimBytes { get; init; }
    public long ReadyReclaimBytes { get; init; }
    public long ReviewDependentBytes { get; init; }
    public long ShortfallBytes { get; init; }
    public long ActuallyReclaimedBytes { get; init; }
    public double? ProjectedReencodeHours { get; init; }
    public double? SavingsPerComputeHourGb { get; init; }
    public int UnknownRuntimeCandidateCount { get; init; }
    public StorageReclamationStrategy Strategy { get; init; }
    public string CatalogRevision { get; init; } = "";
    public string PolicyId { get; init; } = "";
    public IReadOnlyList<StorageReclamationPlanItem> Items { get; init; } = Array.Empty<StorageReclamationPlanItem>();
    public IReadOnlyList<StorageReclamationCategoryTotal> CategoryTotals { get; init; } = Array.Empty<StorageReclamationCategoryTotal>();
    public IReadOnlyList<StorageReclamationLocationTotal> LocationTotals { get; init; } = Array.Empty<StorageReclamationLocationTotal>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record StorageReclamationOpportunity
{
    public long FileId { get; init; }
    public string SourcePath { get; init; } = "";
    public string LocationPath { get; init; } = "";
    public string PhysicalIdentityKey { get; init; } = "";
    public StorageReclamationActionCategory ActionCategory { get; init; }
    public StorageReclamationSourceSubsystem SourceSubsystem { get; init; }
    public long ExpectedReclaimBytes { get; init; }
    public LibraryPolicyConfidence Confidence { get; init; }
    public StorageReclamationSafetyState SafetyState { get; init; }
    public string Reason { get; init; } = "";
    public long? KeeperFileId { get; init; }
    public string KeeperPath { get; init; } = "";
    public long? ExactGroupId { get; init; }
    public long? VisualGroupId { get; init; }
    public long? VisualFamilyId { get; init; }
    public string PolicyId { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public LibraryPolicyQueueItem? PolicyQueueIntent { get; init; }
    public string BlockingReason { get; init; } = "";
    public double? EstimatedProcessingHours { get; init; }
    public double? EstimatedSpeedX { get; init; }
    public double? EstimatedFastProcessingHours { get; init; }
    public double? EstimatedSlowProcessingHours { get; init; }
    public double? SavingsPerComputeHourGb { get; init; }
    public RuntimeEstimateConfidence RuntimeConfidence { get; init; }
    public int RuntimeSampleCount { get; init; }
    public string RuntimeExplanation { get; init; } = "";
}
