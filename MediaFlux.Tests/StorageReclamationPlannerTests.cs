using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class StorageReclamationPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-ReclamationTests", Guid.NewGuid().ToString("N"));
    private readonly StorageReclamationPlannerService _planner = new();
    public StorageReclamationPlannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void GoalSmallerThanExactSupplySelectsOnlyEnoughLargestExactItems()
    {
        StorageReclamationPlan plan = Plan(100 * GiB,
            Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 80 * GiB),
            Opportunity(2, StorageReclamationActionCategory.ExactDuplicateCleanup, 60 * GiB),
            Opportunity(3, StorageReclamationActionCategory.ExactDuplicateCleanup, 40 * GiB));
        Assert.Equal(2, plan.Items.Count(item => item.Included));
        Assert.Equal(140 * GiB, plan.ReadyReclaimBytes);
        Assert.Equal(0, plan.ShortfallBytes);
    }

    [Fact]
    public void GoalAddsVisualThenPolicyOnlyWhenRequired()
    {
        StorageReclamationPlan visual = Plan(120 * GiB,
            Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 80 * GiB),
            Opportunity(2, StorageReclamationActionCategory.ReviewedVisualFamilyCleanup, 50 * GiB),
            Opportunity(3, StorageReclamationActionCategory.PolicyReencode, 100 * GiB, LibraryPolicyConfidence.High));
        Assert.True(visual.Items.Single(item => item.FileId == 2).Included);
        Assert.False(visual.Items.Single(item => item.FileId == 3).Included);

        StorageReclamationPlan policy = Plan(200 * GiB,
            Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 80 * GiB),
            Opportunity(2, StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup, 50 * GiB),
            Opportunity(3, StorageReclamationActionCategory.PolicyReencode, 100 * GiB, LibraryPolicyConfidence.High));
        Assert.True(policy.Items.Single(item => item.FileId == 3).Included);
        Assert.Equal(230 * GiB, policy.ReadyReclaimBytes);
    }

    [Fact]
    public void ImpossibleZeroHugeAndEmptyGoalsAreAccountedConservatively()
    {
        StorageReclamationPlan shortfall = Plan(500 * GiB, Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 100 * GiB));
        Assert.Equal(400 * GiB, shortfall.ShortfallBytes);
        Assert.Contains(shortfall.Warnings, warning => warning.Contains("fall short", StringComparison.OrdinalIgnoreCase));

        StorageReclamationPlan zero = Plan(0, Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 100 * GiB));
        Assert.Equal(0, zero.ReadyReclaimBytes);
        Assert.False(zero.Items[0].Included);
        Assert.Contains(zero.Warnings, warning => warning.Contains("zero", StringComparison.OrdinalIgnoreCase));

        StorageReclamationPlan empty = Plan(long.MaxValue);
        Assert.Empty(empty.Items);
        Assert.Equal(long.MaxValue, empty.ShortfallBytes);
    }

    [Fact]
    public void PrecedencePreventsFileAndPhysicalDoubleCounting()
    {
        StorageReclamationPlan plan = Plan(500 * GiB,
            Opportunity(10, StorageReclamationActionCategory.ExactDuplicateCleanup, 100 * GiB, physical: "disk|1"),
            Opportunity(10, StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup, 90 * GiB, physical: "disk|1"),
            Opportunity(11, StorageReclamationActionCategory.ReviewedVisualFamilyCleanup, 80 * GiB, physical: "disk|2"),
            Opportunity(11, StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup, 80 * GiB, physical: "disk|2"),
            Opportunity(12, StorageReclamationActionCategory.PolicyReencode, 70 * GiB, LibraryPolicyConfidence.High, "disk|1"));
        Assert.Equal(2, plan.Items.Count);
        Assert.Contains(plan.Items, item => item.ActionCategory == StorageReclamationActionCategory.ExactDuplicateCleanup);
        Assert.Contains(plan.Items, item => item.ActionCategory == StorageReclamationActionCategory.ReviewedVisualFamilyCleanup);
        Assert.Equal(180 * GiB, plan.ReadyReclaimBytes);
    }

    [Fact]
    public void DisposableDuplicateIsNotEncodedButKeeperCanRemainPolicyCandidate()
    {
        StorageReclamationPlan plan = Plan(500 * GiB,
            Opportunity(2, StorageReclamationActionCategory.ExactDuplicateCleanup, 100 * GiB, physical: "copy"),
            Opportunity(2, StorageReclamationActionCategory.PolicyReencode, 30 * GiB, LibraryPolicyConfidence.High, "copy"),
            Opportunity(1, StorageReclamationActionCategory.PolicyReencode, 40 * GiB, LibraryPolicyConfidence.High, "keeper"));
        Assert.Equal(2, plan.Items.Count);
        Assert.Contains(plan.Items, item => item.FileId == 1 && item.ActionCategory == StorageReclamationActionCategory.PolicyReencode);
        Assert.DoesNotContain(plan.Items, item => item.FileId == 2 && item.ActionCategory == StorageReclamationActionCategory.PolicyReencode);
    }

    [Fact]
    public void ReviewAndBlockedPotentialNeverSatisfyReadyGoal()
    {
        StorageReclamationOpportunity ready = Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 50 * GiB);
        StorageReclamationOpportunity review = Opportunity(2, StorageReclamationActionCategory.ReviewRequired, 100 * GiB) with
        { SafetyState = StorageReclamationSafetyState.ReviewRequired };
        StorageReclamationOpportunity blocked = Opportunity(3, StorageReclamationActionCategory.ReviewRequired, 200 * GiB) with
        { SafetyState = StorageReclamationSafetyState.Blocked, BlockingReason = "Protected or unavailable" };
        StorageReclamationPlan plan = Plan(150 * GiB, ready, review, blocked);
        Assert.Equal(50 * GiB, plan.ReadyReclaimBytes);
        Assert.Equal(100 * GiB, plan.ReviewDependentBytes);
        Assert.Equal(100 * GiB, plan.ShortfallBytes);
        Assert.False(plan.Items.Single(item => item.FileId == 2).Included);
        Assert.False(plan.Items.Single(item => item.FileId == 3).Included);
    }

    [Fact]
    public void CategoryAndLocationTotalsExactlyMatchNonOverlappingPlan()
    {
        StorageReclamationPlan plan = Plan(200 * GiB,
            Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 80 * GiB) with { LocationPath = @"D:\Movies" },
            Opportunity(2, StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup, 70 * GiB) with { LocationPath = @"E:\TV" },
            Opportunity(3, StorageReclamationActionCategory.PolicyReencode, 60 * GiB, LibraryPolicyConfidence.High) with { LocationPath = @"D:\Movies" });
        Assert.Equal(plan.ReadyReclaimBytes, plan.CategoryTotals.Sum(item => item.ReadyBytes));
        Assert.Equal(plan.ReadyReclaimBytes, plan.LocationTotals.Sum(item => item.ReadyBytes));
        Assert.Equal(140 * GiB, plan.LocationTotals.Single(item => item.LocationPath == @"D:\Movies").ReadyBytes);
    }

    [Fact]
    public void StrategiesExcludeMediumPolicyRemuxOrAllReencodingAsSpecified()
    {
        StorageReclamationOpportunity medium = Opportunity(1, StorageReclamationActionCategory.PolicyReencode, 80 * GiB, LibraryPolicyConfidence.Medium);
        StorageReclamationOpportunity remux = Opportunity(2, StorageReclamationActionCategory.Remux, 20 * GiB, LibraryPolicyConfidence.High);
        Assert.Empty(_planner.BuildPlan(100 * GiB, StorageReclamationStrategy.SafestFirst, new[] { medium, remux }, "r", "policy").Items);
        Assert.Empty(_planner.BuildPlan(100 * GiB, StorageReclamationStrategy.AvoidReencoding, new[] { medium, remux }, "r", "policy").Items);
        Assert.Single(_planner.BuildPlan(100 * GiB, StorageReclamationStrategy.IncludeReencoding, new[] { medium, remux }, "r", "policy").Items);
    }

    [Fact]
    public void MaximumPotentialIncludesAllReadyButSeparatesReviewBytes()
    {
        StorageReclamationOpportunity review = Opportunity(3, StorageReclamationActionCategory.ReviewRequired, 30 * GiB) with { SafetyState = StorageReclamationSafetyState.ReviewRequired };
        StorageReclamationPlan plan = _planner.BuildPlan(10 * GiB, StorageReclamationStrategy.MaximumPotential,
            new[] { Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 20 * GiB), Opportunity(2, StorageReclamationActionCategory.PolicyReencode, 40 * GiB), review }, "r", "policy");
        Assert.Equal(60 * GiB, plan.ReadyReclaimBytes);
        Assert.Equal(30 * GiB, plan.ReviewDependentBytes);
        Assert.Equal(90 * GiB, plan.ProjectedReclaimBytes);
    }

    [Fact]
    public void UnitConversionUsesBinaryGbAndTbAndClampsOverflow()
    {
        Assert.Equal(GiB, StorageReclamationUnits.ToBytes(1, "GB"));
        Assert.Equal(1024 * GiB, StorageReclamationUnits.ToBytes(1, "TB"));
        Assert.Equal(long.MaxValue, StorageReclamationUnits.ToBytes(decimal.MaxValue, "TB"));
    }

    [Fact]
    public void AdvisoryPlanPersistenceRoundTripsWithoutExecutionState()
    {
        string path = Path.Combine(_root, "nested", "plan.json");
        var store = new StorageReclamationPlanStore(path);
        StorageReclamationPlan plan = Plan(100 * GiB, Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 120 * GiB));
        store.Save(plan);
        StorageReclamationPlan loaded = Assert.IsType<StorageReclamationPlan>(store.Load());
        Assert.Equal(plan.PlanId, loaded.PlanId);
        Assert.Equal(plan.ReadyReclaimBytes, loaded.ReadyReclaimBytes);
        Assert.Equal(0, loaded.ActuallyReclaimedBytes);
    }

    [Fact]
    public void ActuallyReclaimedChangesOnlyFromConfirmedExecutionAccounting()
    {
        StorageReclamationPlan projected = Plan(
            100 * GiB,
            Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 120 * GiB));
        Assert.Equal(0, projected.ActuallyReclaimedBytes);

        StorageReclamationPlan first =
            StorageReclamationPlannerService.RecordActuallyReclaimed(projected, 25 * GiB);
        StorageReclamationPlan unchanged =
            StorageReclamationPlannerService.RecordActuallyReclaimed(first, -1);
        StorageReclamationPlan saturated =
            StorageReclamationPlannerService.RecordActuallyReclaimed(
                first with { ActuallyReclaimedBytes = long.MaxValue - 5 },
                10);

        Assert.Equal(25 * GiB, first.ActuallyReclaimedBytes);
        Assert.Same(first, unchanged);
        Assert.Equal(long.MaxValue, saturated.ActuallyReclaimedBytes);
    }

    [Fact]
    public void ThousandsOfOpportunitiesRemainDeterministicAndLinear()
    {
        StorageReclamationOpportunity[] opportunities = Enumerable.Range(1, 10_000)
            .Select(index => Opportunity(index, index % 3 == 0 ? StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup : StorageReclamationActionCategory.ExactDuplicateCleanup,
                (index % 100 + 1) * GiB, physical: $"disk|{index}" )).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        StorageReclamationPlan first = Plan(750 * GiB, opportunities);
        StorageReclamationPlan second = Plan(750 * GiB, opportunities.Reverse().ToArray());
        watch.Stop();
        Assert.Equal(first.Items.Where(item => item.Included).Select(item => item.FileId), second.Items.Where(item => item.Included).Select(item => item.FileId));
        Assert.Equal(first.ReadyReclaimBytes, first.CategoryTotals.Sum(item => item.ReadyBytes));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Planning took {watch.Elapsed}.");
    }

    [Fact]
    public void RepresentativeSevenHundredFiftyGbPlanIsNonOverlappingAndExplainable()
    {
        StorageReclamationOpportunity review = Opportunity(5, StorageReclamationActionCategory.ReviewRequired, 143 * GiB) with
        { SafetyState = StorageReclamationSafetyState.ReviewRequired, Reason = "Marginal HDR policy estimate requires review" };
        StorageReclamationPlan plan = Plan(750 * GiB,
            Opportunity(1, StorageReclamationActionCategory.ExactDuplicateCleanup, 218 * GiB),
            Opportunity(2, StorageReclamationActionCategory.ReviewedVisualFamilyCleanup, 94 * GiB),
            Opportunity(3, StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup, 61 * GiB),
            Opportunity(4, StorageReclamationActionCategory.PolicyReencode, 439 * GiB, LibraryPolicyConfidence.High),
            review);
        Assert.Equal(812 * GiB, plan.ReadyReclaimBytes);
        Assert.Equal(143 * GiB, plan.ReviewDependentBytes);
        Assert.Equal(0, plan.ShortfallBytes);
        Assert.Equal(plan.ReadyReclaimBytes, plan.CategoryTotals.Sum(item => item.ReadyBytes));
        Assert.Equal(4, plan.Items.Count(item => item.Included));
        Assert.Equal(5, plan.Items.Select(item => item.FileId).Distinct().Count());
    }

    [Fact]
    public void BestSavingsEfficiencyRanksZeroCostThenAThenBThenUnknownC()
    {
        StorageReclamationOpportunity cleanup = Opportunity(9, StorageReclamationActionCategory.ExactDuplicateCleanup, 5 * GiB);
        StorageReclamationOpportunity a = Opportunity(1, StorageReclamationActionCategory.PolicyReencode, 12 * GiB) with
        { EstimatedProcessingHours = .25, SavingsPerComputeHourGb = 48, RuntimeConfidence = RuntimeEstimateConfidence.High, PolicyQueueIntent = Intent("A") };
        StorageReclamationOpportunity b = Opportunity(2, StorageReclamationActionCategory.PolicyReencode, 20 * GiB) with
        { EstimatedProcessingHours = 2, SavingsPerComputeHourGb = 10, RuntimeConfidence = RuntimeEstimateConfidence.High, PolicyQueueIntent = Intent("B") };
        StorageReclamationOpportunity c = Opportunity(3, StorageReclamationActionCategory.PolicyReencode, 30 * GiB) with
        { RuntimeConfidence = RuntimeEstimateConfidence.Unknown, PolicyQueueIntent = Intent("C") };
        StorageReclamationPlan plan = _planner.BuildPlan(100 * GiB, StorageReclamationStrategy.BestSavingsEfficiency,
            new[] { c, b, cleanup, a }, "catalog-v1", "policy-v1");
        Assert.Equal(new long[] { 9, 1, 2, 3 }, plan.Items.Select(item => item.FileId));
        Assert.Equal(2.25, plan.ProjectedReencodeHours);
        Assert.Equal(1, plan.UnknownRuntimeCandidateCount);
        Assert.Equal(32d / 2.25, plan.SavingsPerComputeHourGb!.Value, precision: 6);
        Assert.Equal(new[] { "A", "B", "C" }, StorageReclamationQueueOrdering.GetIncludedPolicyItems(plan)
            .Select(item => item.PolicyQueueIntent!.FullPath));
    }

    private static LibraryPolicyQueueItem Intent(string path) => new(path, "policy-v1", "Policy", VideoCodecFamily.Hevc,
        "nvenc", "p5", "", 24, 10, true, null, true, OutputContainerSelection.Auto, null, LibraryPolicyConfidence.High);

    private StorageReclamationPlan Plan(long requested, params StorageReclamationOpportunity[] opportunities) =>
        _planner.BuildPlan(requested, StorageReclamationStrategy.IncludeReencoding, opportunities, "catalog-v1", "policy-v1");

    private static StorageReclamationOpportunity Opportunity(long fileId, StorageReclamationActionCategory category, long bytes,
        LibraryPolicyConfidence confidence = LibraryPolicyConfidence.High, string? physical = null) => new()
    {
        FileId = fileId, SourcePath = $@"D:\Library\file-{fileId}.mkv", LocationPath = @"D:\Library",
        PhysicalIdentityKey = physical ?? $"disk|{fileId}", ActionCategory = category,
        SourceSubsystem = category switch
        {
            StorageReclamationActionCategory.ExactDuplicateCleanup => StorageReclamationSourceSubsystem.ExactDuplicates,
            StorageReclamationActionCategory.ReviewedVisualFamilyCleanup => StorageReclamationSourceSubsystem.VisualFamilies,
            StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup => StorageReclamationSourceSubsystem.VisualPairs,
            _ => StorageReclamationSourceSubsystem.LibraryPolicy
        },
        ExpectedReclaimBytes = bytes, Confidence = confidence, SafetyState = StorageReclamationSafetyState.Ready,
        Reason = "Synthetic planner evidence"
    };

    private const long GiB = 1024L * 1024 * 1024;
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
