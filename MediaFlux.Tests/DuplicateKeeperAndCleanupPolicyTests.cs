using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DuplicateKeeperAndCleanupPolicyTests
{
    [Fact]
    public void QualityFirstPrefersResolutionThenBitrateThenSize()
    {
        DuplicateItem lowResolution = Item("low.mp4", size: 500, width: 1280, height: 720, bitrate: 8_000);
        DuplicateItem highResolution = Item("high.mp4", size: 300, width: 1920, height: 1080, bitrate: 2_000);

        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { lowResolution, highResolution },
            new DuplicateKeeperPreferences());

        Assert.False(result.RequiresReview);
        Assert.Equal(highResolution.Path, result.Keeper?.Path);
        Assert.Contains("highest resolution", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedReferenceRestrictsKeeperSelection()
    {
        DuplicateItem unprotected = Item("higher-quality.mkv", 1_000, 3840, 2160, 20_000);
        DuplicateItem protectedReference = Item("reference.mkv", 500, 1920, 1080, 5_000, isProtected: true);

        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { unprotected, protectedReference },
            new DuplicateKeeperPreferences());

        Assert.False(result.RequiresReview);
        Assert.Equal(protectedReference.Path, result.Keeper?.Path);
    }

    [Fact]
    public void VisualDefaultPrefersResolutionThenCodecThenBitrate()
    {
        DuplicateItem highResolutionH264 = Item("4k-h264.mkv", width: 3840, height: 2160, bitrate: 4_000, codec: "h264");
        DuplicateItem lowerResolutionHevc = Item("1080p-hevc.mkv", width: 1920, height: 1080, bitrate: 20_000, codec: "hevc");
        DuplicateKeeperEvaluation resolution = DuplicateKeeperScoringService.Evaluate(
            new[] { highResolutionH264, lowerResolutionHevc }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);
        Assert.Equal(highResolutionH264.Path, resolution.Keeper?.Path);

        DuplicateItem equalResolutionH264 = Item("1080p-h264.mkv", bitrate: 20_000, codec: "h264");
        DuplicateItem equalResolutionHevc = Item("1080p-hevc.mkv", bitrate: 4_000, codec: "hevc");
        DuplicateKeeperEvaluation codec = DuplicateKeeperScoringService.Evaluate(
            new[] { equalResolutionH264, equalResolutionHevc }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);
        Assert.Equal(equalResolutionHevc.Path, codec.Keeper?.Path);
        Assert.Contains("codec", codec.Explanation, StringComparison.OrdinalIgnoreCase);

        DuplicateItem lowerBitrateHevc = Item("hevc-low.mkv", bitrate: 4_000, codec: "hevc");
        DuplicateItem higherBitrateHevc = Item("hevc-high.mkv", bitrate: 8_000, codec: "hevc");
        DuplicateKeeperEvaluation bitrate = DuplicateKeeperScoringService.Evaluate(
            new[] { lowerBitrateHevc, higherBitrateHevc }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);
        Assert.Equal(higherBitrateHevc.Path, bitrate.Keeper?.Path);
        Assert.Contains("bitrate", bitrate.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualScoringDoesNotChangeDuplicateFinderQualityFirstOrder()
    {
        DuplicateItem h264 = Item("h264.mkv", bitrate: 12_000, codec: "h264");
        DuplicateItem hevc = Item("hevc.mkv", bitrate: 5_000, codec: "hevc");

        DuplicateKeeperEvaluation standard = DuplicateKeeperScoringService.Evaluate(new[] { h264, hevc }, new DuplicateKeeperPreferences());
        DuplicateKeeperEvaluation visual = DuplicateKeeperScoringService.Evaluate(
            new[] { h264, hevc }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);

        Assert.Equal(h264.Path, standard.Keeper?.Path);
        Assert.Equal(hevc.Path, visual.Keeper?.Path);
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("h264")]
    public void VisualRulePrefersSmallerSameCodecResolutionCopyAtComparableBitrate(string codec)
    {
        DuplicateItem larger = Item("larger.mkv", 443_690_000, bitrate: 2_870, codec: codec);
        DuplicateItem smaller = Item("smaller.mkv", 393_370_000, bitrate: 2_540, codec: codec);

        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { larger, smaller }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);

        Assert.False(result.RequiresReview);
        Assert.Equal(smaller.Path, result.Keeper?.Path);
        Assert.Contains("88.5%", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualRuleFallsBackToQualityLogicBelowThreshold()
    {
        DuplicateItem larger = Item("larger.mkv", 500, bitrate: 5_000, codec: "hevc");
        DuplicateItem smaller = Item("smaller.mkv", 300, bitrate: 4_000, codec: "hevc");

        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { larger, smaller }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);

        Assert.Equal(larger.Path, result.Keeper?.Path);
        Assert.Contains("bitrate", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualRuleDoesNotOverrideResolutionOrCodecPreference()
    {
        DuplicateItem highResolution = Item("4k-h264.mkv", 300, 3840, 2160, 2_000, codec: "h264");
        DuplicateItem lowResolution = Item("1080p-h264.mkv", 500, 1920, 1080, 2_100, codec: "h264");
        DuplicateKeeperEvaluation resolution = DuplicateKeeperScoringService.Evaluate(
            new[] { highResolution, lowResolution }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);
        Assert.Equal(highResolution.Path, resolution.Keeper?.Path);

        DuplicateItem h264 = Item("h264.mkv", 300, bitrate: 5_000, codec: "h264");
        DuplicateItem hevc = Item("hevc.mkv", 500, bitrate: 5_100, codec: "hevc");
        DuplicateKeeperEvaluation codec = DuplicateKeeperScoringService.Evaluate(
            new[] { h264, hevc }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);
        Assert.Equal(hevc.Path, codec.Keeper?.Path);
    }

    [Fact]
    public void WeightedScoresInsideMinimumMarginRequireManualReview()
    {
        DuplicateItem smaller = Item("smaller.mkv", 100, 1920, 1080, 5_000);
        DuplicateItem slightlyLarger = Item("larger.mkv", 101, 1920, 1080, 5_000);
        var preferences = new DuplicateKeeperPreferences
        {
            Profile = DuplicateKeeperPreferences.Custom,
            ResolutionWeight = 0,
            QualityWeight = 0,
            StorageWeight = 100,
            CodecWeight = 0,
            ModifiedDateWeight = 0,
            NeverSacrificeResolution = false,
            MinimumScoreMargin = 8
        };

        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { smaller, slightlyLarger },
            preferences);

        Assert.True(result.RequiresReview);
        Assert.Null(result.Keeper);
        Assert.InRange(result.Margin, 0, 8);
    }

    [Fact]
    public void ExactGroupRetainsQualityFirstPolicyRegardlessOfWeightedProfile()
    {
        DuplicateItem smallerLowResolution = Item("small.mkv", 100, 1280, 720, 3_000);
        DuplicateItem largerHighResolution = Item("large.mkv", 1_000, 1920, 1080, 5_000);
        DuplicateGroup group = Group("Exact", smallerLowResolution, largerHighResolution);
        var storagePreferences = new DuplicateKeeperPreferences
        {
            Profile = DuplicateKeeperPreferences.SaveStorage,
            NeverSacrificeResolution = false
        };

        DuplicateGroup result = DuplicateKeeperScoringService.Apply(group, storagePreferences);

        Assert.Equal(largerHighResolution.Path, result.Items[0].Path);
        Assert.Equal("Suggested keeper", result.Items[0].Recommendation);
    }

    [Fact]
    public void ExistingManualKeeperIsPreservedWhenRecommendationsAreReapplied()
    {
        DuplicateItem selected = Item("selected.mkv", 100, 1280, 720, 2_000) with
        {
            Recommendation = "Selected keeper",
            KeeperReason = "User selected in review"
        };
        DuplicateItem other = Item("other.mkv", 1_000, 3840, 2160, 20_000) with
        {
            Recommendation = "Trash candidate"
        };

        DuplicateGroup result = DuplicateKeeperScoringService.Apply(
            Group("Strong visual match", selected, other),
            new DuplicateKeeperPreferences());

        Assert.Equal(selected.Path, result.Items[0].Path);
        Assert.Equal("Selected keeper", result.Items[0].Recommendation);
    }

    [Theory]
    [InlineData("Exact", true)]
    [InlineData("Strong visual match", true)]
    [InlineData("Review only", false)]
    [InlineData("Unknown", false)]
    public void CleanupPolicyLimitsActionableEvidence(string confidence, bool expected)
    {
        Assert.Equal(expected, DuplicateCleanupPolicy.IsActionableGroup(Group(confidence, Item("a.mkv"))));
    }

    [Fact]
    public void CleanupPolicyAllowsOnlyUnprotectedTrashCandidates()
    {
        DuplicateItem trash = Item("trash.mkv") with { Recommendation = "Trash candidate" };
        DuplicateItem keeper = Item("keeper.mkv") with { Recommendation = "Suggested keeper" };
        DuplicateItem protectedTrash = Item("protected.mkv", isProtected: true) with { Recommendation = "Trash candidate" };
        DuplicateGroup actionable = Group("Exact", trash, keeper, protectedTrash);
        DuplicateGroup reviewOnly = Group("Review only", trash, keeper);

        Assert.True(DuplicateCleanupPolicy.CanCleanupItem(actionable, trash));
        Assert.False(DuplicateCleanupPolicy.CanCleanupItem(actionable, keeper));
        Assert.False(DuplicateCleanupPolicy.CanCleanupItem(actionable, protectedTrash));
        Assert.False(DuplicateCleanupPolicy.CanCleanupItem(reviewOnly, trash));
    }

    private static DuplicateGroup Group(string confidence, params DuplicateItem[] items)
    {
        return new DuplicateGroup(
            1,
            confidence,
            100,
            "characterization",
            confidence == "Exact" ? "Exact hash" : "Visual",
            0,
            0,
            0,
            0,
            items);
    }

    private static DuplicateItem Item(
        string path,
        long size = 100,
        int width = 1920,
        int height = 1080,
        int bitrate = 5_000,
        bool isProtected = false,
        string codec = "h264")
    {
        return new DuplicateItem(
            path,
            size,
            codec,
            width,
            height,
            60,
            bitrate,
            new DateTime(2025, 1, 1),
            new DateTime(2025, 1, 2),
            isProtected,
            "",
            "Review duplicate");
    }
}
