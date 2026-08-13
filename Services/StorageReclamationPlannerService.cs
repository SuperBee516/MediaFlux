using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed class StorageReclamationPlannerService
{
    public StorageReclamationPlan BuildPlan(
        long requestedBytes,
        StorageReclamationStrategy strategy,
        IEnumerable<StorageReclamationOpportunity> opportunities,
        string catalogRevision,
        string policyId = "")
    {
        requestedBytes = Math.Max(0, requestedBytes);
        ArgumentNullException.ThrowIfNull(opportunities);

        var seenFiles = new HashSet<long>();
        var seenPhysical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<StorageReclamationOpportunity>();
        IOrderedEnumerable<StorageReclamationOpportunity> ordered = opportunities
            .Where(item => item.ExpectedReclaimBytes > 0 && !string.IsNullOrWhiteSpace(item.SourcePath))
            .OrderBy(item => Priority(item.ActionCategory))
            .ThenBy(item => item.SafetyState);
        ordered = strategy == StorageReclamationStrategy.BestSavingsEfficiency
            ? ordered.ThenBy(EfficiencyBand)
                .ThenByDescending(item => item.SavingsPerComputeHourGb ?? double.MinValue)
                .ThenByDescending(item => item.Confidence)
                .ThenByDescending(item => item.ExpectedReclaimBytes)
                .ThenBy(item => item.FileId)
            : ordered.ThenByDescending(item => item.Confidence)
                .ThenByDescending(item => item.ExpectedReclaimBytes)
                .ThenBy(item => item.FileId);
        foreach (StorageReclamationOpportunity item in ordered)
        {
            if (strategy == StorageReclamationStrategy.AvoidReencoding && item.ActionCategory is StorageReclamationActionCategory.PolicyReencode or StorageReclamationActionCategory.Remux) continue;
            if (strategy == StorageReclamationStrategy.SafestFirst && item.ActionCategory == StorageReclamationActionCategory.PolicyReencode && item.Confidence != LibraryPolicyConfidence.High) continue;
            if (item.ActionCategory == StorageReclamationActionCategory.Remux) continue;
            if (!seenFiles.Add(item.FileId)) continue;
            if (!string.IsNullOrWhiteSpace(item.PhysicalIdentityKey) && !seenPhysical.Add(item.PhysicalIdentityKey)) continue;
            accepted.Add(item);
        }

        long readySelected = 0;
        bool maximum = strategy == StorageReclamationStrategy.MaximumPotential;
        var items = new List<StorageReclamationPlanItem>(accepted.Count);
        foreach (StorageReclamationOpportunity opportunity in accepted)
        {
            bool ready = opportunity.SafetyState == StorageReclamationSafetyState.Ready;
            bool include = ready && (maximum || readySelected < requestedBytes);
            if (include) readySelected += opportunity.ExpectedReclaimBytes;
            items.Add(ToPlanItem(opportunity, include));
        }

        long reviewBytes = items.Where(item => item.SafetyState == StorageReclamationSafetyState.ReviewRequired)
            .Sum(item => item.ExpectedReclaimBytes);
        long readyBytes = items.Where(item => item.Included && item.SafetyState == StorageReclamationSafetyState.Ready)
            .Sum(item => item.ExpectedReclaimBytes);
        StorageReclamationPlanItem[] selectedEncodes = items.Where(item => item.Included && item.ActionCategory == StorageReclamationActionCategory.PolicyReencode).ToArray();
        double encodeHours = selectedEncodes.Where(item => item.EstimatedProcessingHours is > 0).Sum(item => item.EstimatedProcessingHours!.Value);
        long timedEncodeBytes = selectedEncodes.Where(item => item.EstimatedProcessingHours is > 0).Sum(item => item.ExpectedReclaimBytes);
        string[] warnings = BuildWarnings(requestedBytes, readyBytes, reviewBytes, policyId, strategy);
        return new StorageReclamationPlan
        {
            RequestedReclaimBytes = requestedBytes,
            ReadyReclaimBytes = readyBytes,
            ReviewDependentBytes = reviewBytes,
            ProjectedReclaimBytes = readyBytes + reviewBytes,
            ShortfallBytes = Math.Max(0, requestedBytes - readyBytes),
            ProjectedReencodeHours = encodeHours > 0 ? encodeHours : null,
            SavingsPerComputeHourGb = encodeHours > 0 ? timedEncodeBytes / (1024d * 1024 * 1024) / encodeHours : null,
            UnknownRuntimeCandidateCount = selectedEncodes.Count(item => item.EstimatedProcessingHours is not > 0),
            Strategy = strategy,
            CatalogRevision = catalogRevision ?? "",
            PolicyId = policyId ?? "",
            Items = items,
            CategoryTotals = items.GroupBy(item => item.ActionCategory)
                .Select(group => new StorageReclamationCategoryTotal(group.Key, group.Count(),
                    group.Where(item => item.Included && item.SafetyState == StorageReclamationSafetyState.Ready).Sum(item => item.ExpectedReclaimBytes),
                    group.Where(item => item.SafetyState == StorageReclamationSafetyState.ReviewRequired).Sum(item => item.ExpectedReclaimBytes)))
                .OrderBy(item => Priority(item.Category)).ToArray(),
            LocationTotals = items.GroupBy(item => string.IsNullOrWhiteSpace(item.LocationPath) ? "Unassigned location" : item.LocationPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new StorageReclamationLocationTotal(group.Key, group.Count(),
                    group.Where(item => item.Included && item.SafetyState == StorageReclamationSafetyState.Ready).Sum(item => item.ExpectedReclaimBytes),
                    group.Where(item => item.SafetyState == StorageReclamationSafetyState.ReviewRequired).Sum(item => item.ExpectedReclaimBytes)))
                .OrderByDescending(item => item.ReadyBytes).ThenBy(item => item.LocationPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            Warnings = warnings
        };
    }

    public StorageReclamationPlan RecalculateSelections(StorageReclamationPlan plan, IReadOnlyCollection<string> includedItemIds)
    {
        var included = includedItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        StorageReclamationOpportunity[] opportunities = plan.Items.Select(item => new StorageReclamationOpportunity
        {
            FileId = item.FileId, SourcePath = item.SourcePath, LocationPath = item.LocationPath,
            PhysicalIdentityKey = item.PhysicalIdentityKey, ActionCategory = item.ActionCategory,
            SourceSubsystem = item.SourceSubsystem, ExpectedReclaimBytes = item.ExpectedReclaimBytes,
            Confidence = item.Confidence, SafetyState = item.SafetyState, Reason = item.Reason,
            KeeperFileId = item.KeeperFileId, KeeperPath = item.KeeperPath, ExactGroupId = item.ExactGroupId,
            VisualGroupId = item.VisualGroupId, VisualFamilyId = item.VisualFamilyId, PolicyId = item.PolicyId,
            PolicyName = item.PolicyName, PolicyQueueIntent = item.PolicyQueueIntent, BlockingReason = item.BlockingReason,
            EstimatedProcessingHours = item.EstimatedProcessingHours, EstimatedSpeedX = item.EstimatedSpeedX,
            EstimatedFastProcessingHours = item.EstimatedFastProcessingHours, EstimatedSlowProcessingHours = item.EstimatedSlowProcessingHours,
            SavingsPerComputeHourGb = item.SavingsPerComputeHourGb, RuntimeConfidence = item.RuntimeConfidence,
            RuntimeSampleCount = item.RuntimeSampleCount, RuntimeExplanation = item.RuntimeExplanation
        }).ToArray();
        StorageReclamationPlan rebuilt = BuildPlan(plan.RequestedReclaimBytes, StorageReclamationStrategy.MaximumPotential,
            opportunities, plan.CatalogRevision, plan.PolicyId);
        StorageReclamationPlanItem[] items = rebuilt.Items.Select(item => item with
        {
            Included = item.SafetyState == StorageReclamationSafetyState.Ready && included.Contains(
                plan.Items.First(original => original.FileId == item.FileId && original.ActionCategory == item.ActionCategory).ItemId),
            ItemId = plan.Items.First(original => original.FileId == item.FileId && original.ActionCategory == item.ActionCategory).ItemId
        }).ToArray();
        return Reaccount(plan with { Items = items });
    }

    public static StorageReclamationPlan Reaccount(StorageReclamationPlan plan)
    {
        long ready = plan.Items.Where(item => item.Included && item.SafetyState == StorageReclamationSafetyState.Ready).Sum(item => item.ExpectedReclaimBytes);
        long review = plan.Items.Where(item => item.SafetyState == StorageReclamationSafetyState.ReviewRequired).Sum(item => item.ExpectedReclaimBytes);
        StorageReclamationPlanItem[] encodes = plan.Items.Where(item => item.Included && item.ActionCategory == StorageReclamationActionCategory.PolicyReencode).ToArray();
        double encodeHours = encodes.Where(item => item.EstimatedProcessingHours is > 0).Sum(item => item.EstimatedProcessingHours!.Value);
        long timedEncodeBytes = encodes.Where(item => item.EstimatedProcessingHours is > 0).Sum(item => item.ExpectedReclaimBytes);
        return plan with
        {
            ReadyReclaimBytes = ready, ReviewDependentBytes = review, ProjectedReclaimBytes = ready + review,
            ShortfallBytes = Math.Max(0, plan.RequestedReclaimBytes - ready),
            ProjectedReencodeHours = encodeHours > 0 ? encodeHours : null,
            SavingsPerComputeHourGb = encodeHours > 0 ? timedEncodeBytes / (1024d * 1024 * 1024) / encodeHours : null,
            UnknownRuntimeCandidateCount = encodes.Count(item => item.EstimatedProcessingHours is not > 0),
            CategoryTotals = plan.Items.GroupBy(item => item.ActionCategory).Select(group => new StorageReclamationCategoryTotal(
                group.Key, group.Count(), group.Where(item => item.Included && item.SafetyState == StorageReclamationSafetyState.Ready).Sum(item => item.ExpectedReclaimBytes),
                group.Where(item => item.SafetyState == StorageReclamationSafetyState.ReviewRequired).Sum(item => item.ExpectedReclaimBytes))).ToArray(),
            LocationTotals = plan.Items.GroupBy(item => string.IsNullOrWhiteSpace(item.LocationPath) ? "Unassigned location" : item.LocationPath,
                    StringComparer.OrdinalIgnoreCase).Select(group => new StorageReclamationLocationTotal(group.Key, group.Count(),
                    group.Where(item => item.Included && item.SafetyState == StorageReclamationSafetyState.Ready).Sum(item => item.ExpectedReclaimBytes),
                    group.Where(item => item.SafetyState == StorageReclamationSafetyState.ReviewRequired).Sum(item => item.ExpectedReclaimBytes))).ToArray()
        };
    }

    public static StorageReclamationPlan RecordActuallyReclaimed(
        StorageReclamationPlan plan,
        long reclaimedBytes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (reclaimedBytes <= 0) return plan;
        long current = Math.Max(0, plan.ActuallyReclaimedBytes);
        long total = reclaimedBytes > long.MaxValue - current
            ? long.MaxValue
            : current + reclaimedBytes;
        return plan with { ActuallyReclaimedBytes = total };
    }

    private static StorageReclamationPlanItem ToPlanItem(StorageReclamationOpportunity item, bool included) => new()
    {
        FileId = item.FileId, SourcePath = item.SourcePath, LocationPath = item.LocationPath,
        PhysicalIdentityKey = item.PhysicalIdentityKey, ActionCategory = item.ActionCategory,
        SourceSubsystem = item.SourceSubsystem, ExpectedReclaimBytes = item.ExpectedReclaimBytes,
        Confidence = item.Confidence, SafetyState = item.SafetyState, Reason = item.Reason,
        KeeperFileId = item.KeeperFileId, KeeperPath = item.KeeperPath, ExactGroupId = item.ExactGroupId,
        VisualGroupId = item.VisualGroupId, VisualFamilyId = item.VisualFamilyId, PolicyId = item.PolicyId,
        PolicyName = item.PolicyName, PolicyQueueIntent = item.PolicyQueueIntent,
        RequiresUserReview = item.SafetyState == StorageReclamationSafetyState.ReviewRequired,
        IsCurrentlyExecutable = item.SafetyState == StorageReclamationSafetyState.Ready,
        BlockingReason = item.BlockingReason, Included = included,
        EstimatedProcessingHours = item.EstimatedProcessingHours,
        HistoricalThroughputGbPerHour = item.SavingsPerComputeHourGb,
        SavingsPerComputeHourGb = item.SavingsPerComputeHourGb,
        EstimatedSpeedX = item.EstimatedSpeedX,
        EstimatedFastProcessingHours = item.EstimatedFastProcessingHours,
        EstimatedSlowProcessingHours = item.EstimatedSlowProcessingHours,
        RuntimeConfidence = item.RuntimeConfidence,
        RuntimeSampleCount = item.RuntimeSampleCount,
        RuntimeExplanation = item.RuntimeExplanation
    };

    private static int EfficiencyBand(StorageReclamationOpportunity item)
    {
        if (item.ActionCategory != StorageReclamationActionCategory.PolicyReencode) return 0;
        if (item.SavingsPerComputeHourGb is not > 0) return 3;
        return item.RuntimeConfidence >= RuntimeEstimateConfidence.Medium ? 1 : 2;
    }

    private static int Priority(StorageReclamationActionCategory category) => category switch
    {
        StorageReclamationActionCategory.ExactDuplicateCleanup => 0,
        StorageReclamationActionCategory.ReviewedVisualFamilyCleanup => 1,
        StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup => 2,
        StorageReclamationActionCategory.PolicyReencode => 3,
        StorageReclamationActionCategory.Remux => 4,
        _ => 5
    };

    private static string[] BuildWarnings(long requested, long ready, long review, string policyId, StorageReclamationStrategy strategy)
    {
        var warnings = new List<string> { "This plan is advisory. No files have been deleted, encoded, remuxed, moved, or replaced." };
        if (requested == 0) warnings.Add("The requested target is zero; no items were selected automatically.");
        if (ready < requested) warnings.Add($"The ready-to-act opportunities fall short by {FormatBytes(requested - ready)}.");
        if (review > 0) warnings.Add($"An additional {FormatBytes(review)} requires review and is not counted as ready reclaimable storage.");
        if (strategy is StorageReclamationStrategy.SafestFirst or StorageReclamationStrategy.IncludeReencoding or StorageReclamationStrategy.BestSavingsEfficiency && string.IsNullOrWhiteSpace(policyId))
            warnings.Add("No Library Policy was selected, so re-encode opportunities were not considered.");
        return warnings.ToArray();
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024 * 1024):0.##} TiB"
        : $"{bytes / (1024d * 1024 * 1024):0.##} GiB";
}

public sealed class StorageReclamationPlanStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public StorageReclamationPlanStore(string path) => _path = path;

    public StorageReclamationPlan? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            StorageReclamationPlan? plan = JsonSerializer.Deserialize<StorageReclamationPlan>(File.ReadAllText(_path), _json);
            return plan?.SchemaVersion == StorageReclamationPlan.CurrentSchemaVersion ? plan : null;
        }
        catch { return null; }
    }

    public void Save(StorageReclamationPlan plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(plan, _json));
        File.Move(temporary, _path, true);
    }
}

public static class StorageReclamationUnits
{
    public static long ToBytes(decimal value, string unit)
    {
        value = Math.Max(0, value);
        decimal multiplier = unit.Equals("TB", StringComparison.OrdinalIgnoreCase)
            ? 1024m * 1024 * 1024 * 1024 : 1024m * 1024 * 1024;
        if (value >= long.MaxValue / multiplier) return long.MaxValue;
        return (long)(value * multiplier);
    }
}

public static class StorageReclamationQueueOrdering
{
    public static IReadOnlyList<StorageReclamationPlanItem> GetIncludedPolicyItems(StorageReclamationPlan plan) =>
        plan.Items.Where(item => item.Included && item.ActionCategory == StorageReclamationActionCategory.PolicyReencode &&
            item.PolicyQueueIntent != null).ToArray();
}
