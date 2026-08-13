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
    public void LibraryAnalyzerGridAndSplitterLayoutsRoundTripWithLegacyDefaults()
    {
        string path = Path.Combine(_root, "library-layout.json");
        var config = new Config();
        config.LibraryAnalyzerUiState.GridLayouts["Files.FilesGrid"] = new LibraryAnalyzerGridLayout
        {
            Columns = new Dictionary<string, LibraryAnalyzerColumnLayout>
            {
                ["Path"] = new() { Width = 515, DisplayIndex = 1, Visible = false }
            }
        };
        config.LibraryAnalyzerUiState.SplitterDistances["Exact duplicates.Split0"] = 312;

        config.Save(path);
        Config loaded = Config.Load(path);

        LibraryAnalyzerColumnLayout column = loaded.LibraryAnalyzerUiState.GridLayouts["files.filesgrid"].Columns["path"];
        Assert.Equal(515, column.Width);
        Assert.Equal(1, column.DisplayIndex);
        Assert.False(column.Visible);
        Assert.Equal(312, loaded.LibraryAnalyzerUiState.SplitterDistances["exact duplicates.split0"]);

        string legacyPath = Path.Combine(_root, "legacy-library-layout.json");
        File.WriteAllText(legacyPath, "{}");
        Config legacy = Config.Load(legacyPath);
        Assert.Empty(legacy.LibraryAnalyzerUiState.GridLayouts);
        Assert.Empty(legacy.LibraryAnalyzerUiState.SplitterDistances);
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
    public void VisualKeeperStrategyAndSafetyFloorsPersistAndNormalize()
    {
        string legacyPath = Path.Combine(_root, "legacy-keeper-rules.json");
        File.WriteAllText(legacyPath, "{}");
        Config legacy = Config.Load(legacyPath);
        Assert.Equal(DuplicateKeeperPreferences.VisualBalanced, legacy.DuplicateKeeperPreferences.VisualKeeperStrategy);
        Assert.False(legacy.DuplicateKeeperPreferences.ForceAutomaticKeeperOnHighConfidenceNearTies);
        Assert.Equal(99, legacy.DuplicateKeeperPreferences.HighConfidenceNearTieThreshold);

        string path = Path.Combine(_root, "keeper-rules.json");
        legacy.DuplicateKeeperPreferences.VisualKeeperStrategy = DuplicateKeeperPreferences.StorageOptimized;
        legacy.DuplicateKeeperPreferences.VisualQualityFloor = 50;
        legacy.DuplicateKeeperPreferences.VisualConfidenceFloor = 96;
        legacy.DuplicateKeeperPreferences.ForceAutomaticKeeperOnHighConfidenceNearTies = true;
        legacy.DuplicateKeeperPreferences.HighConfidenceNearTieThreshold = 99.5;
        legacy.Save(path);
        Config loaded = Config.Load(path);
        Assert.Equal(DuplicateKeeperPreferences.StorageOptimized, loaded.DuplicateKeeperPreferences.VisualKeeperStrategy);
        Assert.Equal(50, loaded.DuplicateKeeperPreferences.VisualQualityFloor);
        Assert.Equal(96, loaded.DuplicateKeeperPreferences.VisualConfidenceFloor);
        Assert.True(loaded.DuplicateKeeperPreferences.ForceAutomaticKeeperOnHighConfidenceNearTies);
        Assert.Equal(99.5, loaded.DuplicateKeeperPreferences.HighConfidenceNearTieThreshold);

        loaded.DuplicateKeeperPreferences.VisualQualityFloor = 1;
        loaded.DuplicateKeeperPreferences.VisualConfidenceFloor = 1;
        loaded.DuplicateKeeperPreferences.HighConfidenceNearTieThreshold = 101;
        loaded.DuplicateKeeperPreferences.Normalize();
        Assert.Equal(25, loaded.DuplicateKeeperPreferences.VisualQualityFloor);
        Assert.Equal(76, loaded.DuplicateKeeperPreferences.VisualConfidenceFloor);
        Assert.Equal(100, loaded.DuplicateKeeperPreferences.HighConfidenceNearTieThreshold);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
