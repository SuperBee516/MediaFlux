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
    public void BalancedVisualStrategyPrefersEfficientGoodQualityHevcCopy()
    {
        DuplicateItem larger = Item("larger.mkv", 852_000_000, bitrate: 3_280, codec: "hevc");
        DuplicateItem smaller = Item("smaller.mkv", 462_000_000, bitrate: 1_780, codec: "hevc");
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { larger, smaller }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 98);

        Assert.False(result.RequiresReview);
        Assert.Equal(smaller.Path, result.Keeper?.Path);
        Assert.Equal(DuplicateKeeperOutcome.PreferSmallerMoreEfficientCopy, result.Outcome);
        Assert.Contains("good", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("45.8%", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualScoringDoesNotChangeDuplicateFinderQualityFirstOrder()
    {
        DuplicateItem h264 = Item("h264.mkv", bitrate: 12_000, codec: "h264");
        DuplicateItem hevc = Item("hevc.mkv", bitrate: 5_000, codec: "hevc");

        DuplicateKeeperEvaluation standard = DuplicateKeeperScoringService.Evaluate(new[] { h264, hevc }, new DuplicateKeeperPreferences());
        var visualPreferences = new DuplicateKeeperPreferences { MinimumScoreMargin = 0 };
        DuplicateKeeperEvaluation visual = DuplicateKeeperScoringService.Evaluate(
            new[] { h264, hevc }, visualPreferences, DuplicateKeeperScoringContext.Visual);

        Assert.Equal(h264.Path, standard.Keeper?.Path);
        Assert.Equal(hevc.Path, visual.Keeper?.Path);
    }

    [Fact]
    public void CandidateBelowQualityFloorRequiresManualReview()
    {
        DuplicateItem larger = Item("larger.mkv", 800, bitrate: 2_600, codec: "hevc");
        DuplicateItem smaller = Item("smaller.mkv", 300, bitrate: 900, codec: "hevc");
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { larger, smaller }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual);

        Assert.True(result.RequiresReview);
        Assert.Null(result.Keeper);
        Assert.Equal(DuplicateKeeperOutcome.ManualReviewRequired, result.Outcome);
        Assert.Contains("quality floor", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodecEfficiencyChangesEstimatedQualitySufficiency()
    {
        double av1 = VisualDuplicateQualityModel.Assess(Item("av1.mkv", bitrate: 1_600, codec: "av1")).SufficiencyScore;
        double hevc = VisualDuplicateQualityModel.Assess(Item("hevc.mkv", bitrate: 1_600, codec: "hevc")).SufficiencyScore;
        double h264 = VisualDuplicateQualityModel.Assess(Item("h264.mkv", bitrate: 1_600, codec: "h264")).SufficiencyScore;
        Assert.True(av1 > hevc);
        Assert.True(hevc > h264);
    }

    [Fact]
    public void SixtyFpsRequiresMoreBitrateThanThirtyFps()
    {
        double fps24 = VisualDuplicateQualityModel.Assess(Item("24.mkv", bitrate: 2_000, codec: "hevc", frameRate: 24)).SufficiencyScore;
        double fps30 = VisualDuplicateQualityModel.Assess(Item("30.mkv", bitrate: 2_000, codec: "hevc", frameRate: 30)).SufficiencyScore;
        double fps60 = VisualDuplicateQualityModel.Assess(Item("60.mkv", bitrate: 2_000, codec: "hevc", frameRate: 60)).SufficiencyScore;
        Assert.True(fps24 >= fps30);
        Assert.True(fps30 > fps60);
    }

    [Fact]
    public void IncompleteVisualMetadataRequiresManualReview()
    {
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { Item("known.mkv", bitrate: 2_000, codec: "hevc"), Item("unknown.mkv", bitrate: 2_000, codec: "hevc", frameRate: 0) },
            new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 98);
        Assert.Equal(DuplicateKeeperOutcome.ManualReviewRequired, result.Outcome);
        Assert.Contains("metadata is incomplete", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealWorldHevcResolutionValueCaseFavors1080pUnderBalanced()
    {
        DuplicateItem high = Item("1080p.mkv", 449_190_000, 1920, 1080, 3_970, codec: "hevc") with { DurationSeconds = 950 };
        DuplicateItem low = Item("720p.mkv", 280_210_000, 1280, 720, 2_470, codec: "hevc") with { DurationSeconds = 950 };
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 99.5);
        Assert.False(result.RequiresReview);
        Assert.Equal(high.Path, result.Keeper?.Path);
        Assert.Equal(DuplicateKeeperOutcome.PreferHigherQualityCopy, result.Outcome);
        Assert.Contains("2.25x pixels at 1.6x storage", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HigherResolutionOnlySlightlyLargerIsPreferred()
    {
        DuplicateItem high = Item("1080p.mkv", 320, 1920, 1080, 2_800, codec: "hevc");
        DuplicateItem low = Item("720p.mkv", 300, 1280, 720, 2_100, codec: "hevc");
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 99);
        Assert.False(result.RequiresReview);
        Assert.Equal(high.Path, result.Keeper?.Path);
    }

    [Fact]
    public void DramaticallyLarger1080pCanLoseOnStorageTradeoff()
    {
        DuplicateItem high = Item("1080p.mkv", 1_200, 1920, 1080, 4_000, codec: "hevc");
        DuplicateItem low = Item("720p.mkv", 300, 1280, 720, 2_100, codec: "hevc");
        var preferences = new DuplicateKeeperPreferences { MinimumScoreMargin = 0 };
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, preferences, DuplicateKeeperScoringContext.Visual, 99);
        Assert.False(result.RequiresReview);
        Assert.Equal(low.Path, result.Keeper?.Path);
        Assert.Equal(DuplicateKeeperOutcome.PreferSmallerMoreEfficientCopy, result.Outcome);
    }

    [Fact]
    public void InadequateHigherResolutionBitrateDoesNotWinAutomatically()
    {
        DuplicateItem high = Item("thin-1080p.mkv", 320, 1920, 1080, 700, codec: "hevc");
        DuplicateItem low = Item("healthy-720p.mkv", 300, 1280, 720, 2_100, codec: "hevc");
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 99);
        Assert.True(result.RequiresReview);
        Assert.Null(result.Keeper);
        Assert.Contains("quality floor", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataOnlyUpscaleRiskFlagsUnsupportedResolutionGain()
    {
        DuplicateItem high = Item("1080p.mkv", 340, 1920, 1080, 2_100, codec: "hevc");
        DuplicateItem low = Item("720p.mkv", 300, 1280, 720, 2_100, codec: "hevc");
        VisualQualityAssessment highQuality = VisualDuplicateQualityModel.Assess(high);
        VisualQualityAssessment lowQuality = VisualDuplicateQualityModel.Assess(low);

        VisualResolutionValueAssessment value = VisualDuplicateQualityModel.AssessResolutionValue(
            high, low, highQuality, lowQuality, 99, 90, 1);

        Assert.True(value.PixelRatio > 2);
        Assert.True(value.UpscaleRisk >= 0.25);
    }

    [Fact]
    public void MarginalVisualConfidenceRequiresManualReview()
    {
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { Item("a.mkv", 800, bitrate: 3_000, codec: "hevc"), Item("b.mkv", 500, bitrate: 2_000, codec: "hevc") },
            new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 85);
        Assert.True(result.RequiresReview);
        Assert.Contains("visual confidence", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HighConfidenceSupportsResolutionGainWhileNearFloorRemainsReviewable()
    {
        DuplicateItem high = Item("1080p.mkv", 450, 1920, 1080, 3_000, codec: "hevc");
        DuplicateItem low = Item("720p.mkv", 300, 1280, 720, 2_100, codec: "hevc");
        DuplicateKeeperEvaluation highConfidence = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 99.5);
        DuplicateKeeperEvaluation nearFloor = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 90.1);
        Assert.Equal(high.Path, highConfidence.Keeper?.Path);
        Assert.True(nearFloor.RequiresReview);
    }

    [Fact]
    public void SameResolutionBalancedBehaviorRemainsEfficientCopyPreference()
    {
        DuplicateItem larger = Item("larger.mkv", 852_000_000, bitrate: 3_280, codec: "hevc");
        DuplicateItem smaller = Item("smaller.mkv", 462_000_000, bitrate: 1_780, codec: "hevc");
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { larger, smaller }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 98);
        Assert.Equal(smaller.Path, result.Keeper?.Path);
        Assert.Equal(DuplicateKeeperOutcome.PreferSmallerMoreEfficientCopy, result.Outcome);
    }

    [Fact]
    public void ResolutionStrategiesExpressDifferentStorageTolerance()
    {
        DuplicateItem high = Item("1080p.mkv", 650, 1920, 1080, 3_200, codec: "hevc");
        DuplicateItem low = Item("720p.mkv", 300, 1280, 720, 2_100, codec: "hevc");
        var quality = new DuplicateKeeperPreferences
        {
            VisualKeeperStrategy = DuplicateKeeperPreferences.PreserveMaximumQuality,
            MinimumScoreMargin = 0
        };
        var balanced = new DuplicateKeeperPreferences { MinimumScoreMargin = 0 };
        var storage = new DuplicateKeeperPreferences
        {
            VisualKeeperStrategy = DuplicateKeeperPreferences.StorageOptimized,
            MinimumScoreMargin = 0
        };

        DuplicateKeeperEvaluation qualityResult = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, quality, DuplicateKeeperScoringContext.Visual, 99);
        DuplicateKeeperEvaluation balancedResult = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, balanced, DuplicateKeeperScoringContext.Visual, 99);
        DuplicateKeeperEvaluation storageResult = DuplicateKeeperScoringService.Evaluate(
            new[] { high, low }, storage, DuplicateKeeperScoringContext.Visual, 99);

        Assert.Equal(high.Path, qualityResult.Keeper?.Path);
        Assert.True(qualityResult.Scores[high.Path] - qualityResult.Scores[low.Path] >
                    balancedResult.Scores[high.Path] - balancedResult.Scores[low.Path]);
        Assert.Equal(low.Path, storageResult.Keeper?.Path);
    }

    [Fact]
    public void ProtectedVisualCandidateStillOverridesResolutionValueScore()
    {
        DuplicateItem high = Item("1080p.mkv", 450, 1920, 1080, 3_500, codec: "hevc");
        DuplicateItem protectedLow = Item("protected-720p.mkv", 300, 1280, 720, 2_100, isProtected: true, codec: "hevc");
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { high, protectedLow }, new DuplicateKeeperPreferences(), DuplicateKeeperScoringContext.Visual, 99);
        Assert.False(result.RequiresReview);
        Assert.Equal(protectedLow.Path, result.Keeper?.Path);
    }

    [Fact]
    public void BitrateModelHasDiminishingReturns()
    {
        double low = VisualDuplicateQualityModel.Assess(Item("low.mkv", bitrate: 1_000, codec: "hevc")).SufficiencyScore;
        double mid = VisualDuplicateQualityModel.Assess(Item("mid.mkv", bitrate: 1_800, codec: "hevc")).SufficiencyScore;
        double high = VisualDuplicateQualityModel.Assess(Item("high.mkv", bitrate: 3_300, codec: "hevc")).SufficiencyScore;
        Assert.True(mid - low > high - mid);
    }

    [Fact]
    public void VisualStrategyPresetsChangeKeeperRecommendation()
    {
        DuplicateItem larger = Item("quality.mkv", 800, bitrate: 3_800, codec: "hevc");
        DuplicateItem smaller = Item("efficient.mkv", 400, bitrate: 1_600, codec: "hevc");
        var quality = new DuplicateKeeperPreferences { VisualKeeperStrategy = DuplicateKeeperPreferences.PreserveMaximumQuality };
        var storage = new DuplicateKeeperPreferences { VisualKeeperStrategy = DuplicateKeeperPreferences.StorageOptimized };
        Assert.Equal(larger.Path, DuplicateKeeperScoringService.Evaluate(new[] { larger, smaller }, quality,
            DuplicateKeeperScoringContext.Visual, 98).Keeper?.Path);
        Assert.Equal(smaller.Path, DuplicateKeeperScoringService.Evaluate(new[] { larger, smaller }, storage,
            DuplicateKeeperScoringContext.Visual, 98).Keeper?.Path);
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
        string codec = "h264",
        double frameRate = 30)
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
            "Review duplicate") { FrameRate = frameRate };
    }
}
