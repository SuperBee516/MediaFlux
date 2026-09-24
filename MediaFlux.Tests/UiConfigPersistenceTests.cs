using MediaFlux.Models;
using MediaFlux.Services;
using System.Reflection;
using System.Windows.Forms;
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
    public void ExpandedDetailsRatioRoundTripsWithoutChangingCollapseState()
    {
        string path = Path.Combine(_root, "encode-layout.json");
        var config = new Config { EncodeInfoHeaderCollapsed = false, EncodeInfoHeight = 420, EncodeInfoExpandedRatio = 0.42 };
        config.Save(path);

        Config loaded = Config.Load(path);

        Assert.False(loaded.EncodeInfoHeaderCollapsed);
        Assert.Equal(420, loaded.EncodeInfoHeight);
        Assert.Equal(0.42, loaded.EncodeInfoExpandedRatio, 3);
    }

    [Fact]
    public void MissingExpandedDetailsRatioRemainsBackwardCompatible()
    {
        string path = Path.Combine(_root, "legacy-encode-layout.json");
        File.WriteAllText(path, "{\"EncodeInfoHeaderCollapsed\":true,\"EncodeInfoHeight\":300}");

        Config loaded = Config.Load(path);

        Assert.True(loaded.EncodeInfoHeaderCollapsed);
        Assert.Equal(300, loaded.EncodeInfoHeight);
        Assert.Equal(0, loaded.EncodeInfoExpandedRatio);
    }

    [Fact]
    public void ExpandedLayoutUsesDeterministicFortyFivePercentDetailsAllocation()
    {
        Assert.Equal(450, MainForm.CalculateStandardEncodeDetailsHeight(1000));
        Assert.Equal(0, MainForm.CalculateStandardEncodeDetailsHeight(0));
    }

    [Fact]
    public void QueueControlsRefreshTheirWidthConstraintWhenWrapStateStaysTheSame()
    {
        using var card = new TableLayoutPanel();
        using var behavior = new FlowLayoutPanel { Dock = DockStyle.Fill };
        using var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };

        MainForm.ApplyQueueControlsCompactLayout(card, behavior, actions, wrapControls: true, contentWidth: 200);

        Assert.Equal(DockStyle.Top, behavior.Dock);
        Assert.Equal(DockStyle.Top, actions.Dock);
        Assert.True(behavior.WrapContents);
        Assert.True(actions.WrapContents);
        Assert.Equal(200, behavior.MaximumSize.Width);
        Assert.Equal(200, actions.MaximumSize.Width);

        // A restored/resized window can remain in wrapping mode while gaining width.
        // The width limit must follow the new bounds even though the wrap flag is unchanged.
        MainForm.ApplyQueueControlsCompactLayout(card, behavior, actions, wrapControls: true, contentWidth: 620);
        Assert.Equal(620, behavior.MaximumSize.Width);
        Assert.Equal(620, actions.MaximumSize.Width);

        MainForm.ApplyQueueControlsCompactLayout(card, behavior, actions, wrapControls: false, contentWidth: 800);
        Assert.False(behavior.WrapContents);
        Assert.False(actions.WrapContents);
        Assert.Equal(System.Drawing.Size.Empty, behavior.MaximumSize);
        Assert.Equal(System.Drawing.Size.Empty, actions.MaximumSize);
    }

    [Fact]
    public void AutomaticQualityModeRoundTripsAndMissingPreferenceDefaultsToManual()
    {
        string path = Path.Combine(_root, "quality-mode.json");
        new Config { LastQualityMode = "Automatic" }.Save(path);

        Assert.Equal("Automatic", Config.Load(path).LastQualityMode);

        File.WriteAllText(path, "{}");
        Assert.Equal("Manual", Config.Load(path).LastQualityMode);
    }

    [Fact]
    public void AutomaticQualityModeRestoresOnMainFormConstructionWithoutBeingOverwritten()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string path = Path.Combine(_root, "automatic-quality-startup.json");
        new Config { LastQualityMode = "Automatic" }.Save(path);

        Exception? failure = null;
        MainForm? form = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                form = new MainForm(path);
                Assert.Equal("Automatic", Config.Load(path).LastQualityMode);
                Assert.Equal("Automatic", Field<Config>(form, "_config").LastQualityMode);
                ComboBox mode = Field<ComboBox>(form, "comboQualityMode");

                Assert.Equal("Automatic • Source Adaptive", mode.Text);
            }
            catch (Exception ex) { failure = ex; }
            finally { WinFormsTestLifecycle.CloseAndDispose(form); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Quality preference startup test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void QualityTargetTrackPositionsUseThumbInsetAndRemainOrdered()
    {
        const int trackWidth = 500;
        const int thumbInset = 5;
        int[] positions = Enumerable.Range(0, 5)
            .Select(index => MainForm.GetQualityTrackPosition(trackWidth, thumbInset, index, 5))
            .ToArray();

        Assert.Equal(new[] { 5, 127, 250, 373, 495 }, positions);
        Assert.Equal(250, positions[2]);
        Assert.Equal(5, positions[0]);
        Assert.Equal(495, positions[4]);
        Assert.Equal(positions.OrderBy(value => value), positions);
    }

    [Fact]
    public void QualityModeVisibilityCollapsesAndPreservesBothModes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? form = null;
        var thread = new Thread(() =>
        {
            try
            {
                form = new MainForm();
                form.CreateControl();
                form.Show();
                Application.DoEvents();

                TabControl tabs = Field<TabControl>(form, "_encodeInfoTabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Plan & Analysis");
                Application.DoEvents();
                ComboBox mode = Field<ComboBox>(form, "comboQualityMode");
                TrackBar target = Field<TrackBar>(form, "trkQualityTarget");
                NumericUpDown manual = Field<NumericUpDown>(form, "nudAutoQuality");
                Label manualLabel = Field<Label>(form, "lblManualQuality");
                Panel labels = Field<Panel>(form, "pnlQualityTargetLabels");
                Panel qualityIntent = Field<Panel>(form, "pnlQualityIntent");
                Label fileSizeEstimateLabel = Field<Label>(form, "lblCompressionProfile");
                ComboBox fileSizeEstimate = Field<ComboBox>(form, "comboCompressionProfile");
                Label selectedPreference = Field<Label>(form, "lblQualityTargetValue");

                target.Value = 3;
                manual.Value = 27;
                fileSizeEstimate.SelectedItem = "Medium Quality (Default)";
                mode.SelectedIndex = 0;
                Application.DoEvents();
                Assert.True(target.Visible);
                Assert.True(labels.Visible);
                Assert.Equal("High Quality  (4 of 5)", selectedPreference.Text);
                Assert.False(manual.Visible);
                Assert.False(manualLabel.Visible);
                Assert.False(fileSizeEstimateLabel.Visible);
                Assert.False(fileSizeEstimate.Visible);
                int automaticHeight = qualityIntent.PreferredSize.Height;

                mode.SelectedIndex = 1;
                Application.DoEvents();
                Assert.False(target.Visible);
                Assert.False(labels.Visible);
                Assert.True(manual.Visible);
                Assert.True(manualLabel.Visible);
                Assert.True(fileSizeEstimateLabel.Visible);
                Assert.True(fileSizeEstimate.Visible);
                Assert.Equal(27, manual.Value);
                Assert.True(qualityIntent.PreferredSize.Height < automaticHeight);

                mode.SelectedIndex = 0;
                Application.DoEvents();
                Assert.Equal(3, target.Value);
            }
            catch (Exception ex) { failure = ex; }
            finally { WinFormsTestLifecycle.CloseAndDispose(form); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Quality mode UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void EncodingProfileUsesIndependentColumnsAndKeepsQualityRowTogether()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string configPath = Path.Combine(_root, "encoding-profile-layout.json");
        new Config { LastQualityMode = "Manual" }.Save(configPath);
        Exception? failure = null;
        MainForm? form = null;
        var thread = new Thread(() =>
        {
            try
            {
                form = new MainForm(configPath);
                form.CreateControl();
                form.Show();
                Application.DoEvents();
                TabControl tabs = Field<TabControl>(form, "_encodeInfoTabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Plan & Analysis");
                Application.DoEvents();

                Label qualityLabel = Field<Label>(form, "lblCompressionProfile");
                ComboBox quality = Field<ComboBox>(form, "comboCompressionProfile");
                Label speedLabel = Field<Label>(form, "lblEncodingSpeed");
                ComboBox speed = Field<ComboBox>(form, "comboEncoderPreset");
                TableLayoutPanel profile = Field<TableLayoutPanel>(form, "tlEncodingProfileFields");

                Assert.Same(qualityLabel.Parent, quality.Parent);
                Assert.Same(speedLabel.Parent, speed.Parent);
                Assert.Equal(2, profile.ColumnCount);
                Assert.Equal(1, profile.RowCount);
                Assert.True(Math.Abs(qualityLabel.Bounds.Top + qualityLabel.Height / 2 -
                    (quality.Bounds.Top + quality.Height / 2)) <= 8);
                Assert.True(speed.Bounds.Top >= quality.Bounds.Bottom);

                Control left = qualityLabel.Parent!;
                Control right = Field<Panel>(form, "pnlQualityIntent").Parent!.Parent!;
                Assert.NotSame(left, right);
                Assert.True(left.Bounds.Height > 0);
                Assert.True(right.Bounds.Height > 0);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(form);
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Independent profile layout test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static T Field<T>(MainForm form, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form)
            ?? throw new MissingFieldException(name));

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
