using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class UiConfigPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MediaFlux-UiConfigTests",
        Guid.NewGuid().ToString("N"));

    public UiConfigPersistenceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SummaryPreviewHeightRoundTrips()
    {
        string path = Path.Combine(_root, "config.json");
        var config = new Config
        {
            EncodeInfoHeight = 412
        };

        config.Save(path);
        Config loaded = Config.Load(path);

        Assert.Equal(412, loaded.EncodeInfoHeight);
    }

    [Fact]
    public void OlderConfigUsesDefaultSummaryPreviewHeight()
    {
        string path = Path.Combine(_root, "legacy.json");
        File.WriteAllText(path, """{"EncodeInfoHeaderCollapsed":false}""");

        Config loaded = Config.Load(path);

        Assert.Equal(0, loaded.EncodeInfoHeight);
    }

    [Fact]
    public void LibraryAnalyzerCleanupDefaultsAreConservativeAndAdvancedChoiceRoundTrips()
    {
        string legacy = Path.Combine(_root, "legacy-cleanup.json");
        File.WriteAllText(legacy, "{}");
        Config defaults = Config.Load(legacy);
        Assert.Equal("PermanentDelete", defaults.LibraryAnalyzerCleanupMode);
        Assert.False(defaults.AllowUnreviewedVisualBulkCleanup);
        Assert.Equal(95, defaults.VisualBulkCleanupMinimumConfidence);
        Assert.False(defaults.SemiAutomaticVisualKeeperApproval);
        Assert.Equal(100, defaults.VisualMassReviewMaximumMatches);
        Assert.Equal(15, defaults.VisualMassReviewMinimumAutomationMargin);
        Assert.Equal(95, defaults.VisualMassReviewMinimumConfidence);

        string path = Path.Combine(_root, "cleanup.json");
        defaults.LibraryAnalyzerCleanupMode = "RecycleBin";
        defaults.AllowUnreviewedVisualBulkCleanup = true;
        defaults.VisualBulkCleanupMinimumConfidence = 97.5;
        defaults.Save(path);
        Config loaded = Config.Load(path);
        Assert.Equal("RecycleBin", loaded.LibraryAnalyzerCleanupMode);
        Assert.True(loaded.AllowUnreviewedVisualBulkCleanup);
        Assert.Equal(97.5, loaded.VisualBulkCleanupMinimumConfidence);
    }

    [Fact]
    public void LibraryAnalyzerReviewProductivitySettingsRoundTripAndNormalize()
    {
        string path = Path.Combine(_root, "review-productivity.json");
        var config = new Config
        {
            SemiAutomaticVisualKeeperApproval = true,
            VisualMassReviewMaximumMatches = 2_000,
            VisualMassReviewMinimumAutomationMargin = -1,
            VisualMassReviewMinimumConfidence = 10
        };
        config.Save(path);
        Config loaded = Config.Load(path);
        Assert.True(loaded.SemiAutomaticVisualKeeperApproval);
        Assert.Equal(1_000, loaded.VisualMassReviewMaximumMatches);
        Assert.Equal(0, loaded.VisualMassReviewMinimumAutomationMargin);
        Assert.Equal(76, loaded.VisualMassReviewMinimumConfidence);
    }

    [Fact]
    public void VisualComparableBitrateThresholdPersistsNormalizesAndIsHonored()
    {
        string legacyPath = Path.Combine(_root, "legacy-keeper-rules.json");
        File.WriteAllText(legacyPath, "{}");
        Config legacy = Config.Load(legacyPath);
        Assert.True(legacy.DuplicateKeeperPreferences.PreferSmallerComparableVisualCopy);
        Assert.Equal(85, legacy.DuplicateKeeperPreferences.ComparableVisualBitratePercent);

        string path = Path.Combine(_root, "keeper-rules.json");
        legacy.DuplicateKeeperPreferences.ComparableVisualBitratePercent = 90;
        legacy.Save(path);
        Config loaded = Config.Load(path);
        Assert.Equal(90, loaded.DuplicateKeeperPreferences.ComparableVisualBitratePercent);

        DuplicateItem larger = new("larger.mkv", 443_690_000, "hevc", 1920, 1080, 1237, 2_870,
            DateTime.Today, DateTime.Today, false, "", "");
        DuplicateItem smaller = new("smaller.mkv", 393_370_000, "hevc", 1920, 1080, 1237, 2_540,
            DateTime.Today, DateTime.Today, false, "", "");
        DuplicateKeeperEvaluation result = DuplicateKeeperScoringService.Evaluate(
            new[] { larger, smaller }, loaded.DuplicateKeeperPreferences, DuplicateKeeperScoringContext.Visual);
        Assert.Equal(larger.Path, result.Keeper?.Path);

        loaded.DuplicateKeeperPreferences.ComparableVisualBitratePercent = 1;
        loaded.DuplicateKeeperPreferences.Normalize();
        Assert.Equal(50, loaded.DuplicateKeeperPreferences.ComparableVisualBitratePercent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
