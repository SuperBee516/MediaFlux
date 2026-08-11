using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerPhase7Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-LibraryPhase7Tests", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerPhase7Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task MassReviewHonorsThresholdsUsesBatchHistoryAndRevalidatesChanges()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "mass"); Directory.CreateDirectory(library);
        string a = Write(library, "a.mkv", 40_000), b = Write(library, "b.mp4", 30_000);
        AddInventoryAndMetadata(catalog, library, new[] { a, b }, path => path == a ? ("hevc", 1920, 1080, 8_000_000L) : ("h264", 1280, 720, 2_000_000L));
        await AnalyzeVisualAsync(catalog, new[] { a, b });
        var eligibility = new LibraryMatchEligibilityService(catalog, catalog);
        var service = new LibraryMassReviewService(catalog, eligibility, new DuplicateKeeperPreferences
        {
            Profile = DuplicateKeeperPreferences.Custom, ResolutionWeight = 60, QualityWeight = 20, StorageWeight = 10, CodecWeight = 10, MinimumScoreMargin = 0
        });
        LibraryMassReviewPreview preview = service.CreatePreview(new LibraryVisualReviewAutomationOptions(false, 1, 0, 70));
        LibraryMassReviewPreviewItem item = Assert.Single(preview.EligibleItems);
        Assert.Empty(preview.ExcludedItems);
        LibraryMassReviewApplyResult applied = service.Apply(preview);
        Assert.Equal(1, applied.Applied);
        VisualSimilarityGroupRecord updated = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
        Assert.True(updated.Reviewed);
        Assert.Equal(item.KeeperFileId, updated.ManualKeeperFileId);
        Assert.Contains(catalog.GetDecisionHistory(), x => x.BatchId == preview.BatchId && x.Source == "mass-review");

        catalog.SaveVisualDecision(new VisualGroupDecision(updated.GroupId, null, false, false));
        LibraryMassReviewPreview stalePreview = service.CreatePreview(new LibraryVisualReviewAutomationOptions(false, 1, 0, 70));
        using (FileStream changed = new(b, FileMode.Append, FileAccess.Write, FileShare.None))
            changed.WriteByte(1);
        LibraryMassReviewApplyResult skipped = service.Apply(stalePreview);
        Assert.Equal(0, skipped.Applied);
        Assert.Equal(1, skipped.Excluded);
    }

    [Fact]
    public async Task KeeperExplanationAndCleanupDashboardUseCurrentNonOverlappingCandidates()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "dashboard"); Directory.CreateDirectory(library);
        string keeper = Write(library, "keeper.mkv", 60_000);
        string duplicate = Path.Combine(library, "duplicate.mkv"); File.Copy(keeper, duplicate);
        AddInventoryAndMetadata(catalog, library, new[] { keeper, duplicate }, _ => ("hevc", 1920, 1080, 8_000_000L));
        using (var exact = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 16 * 1024)))
            await exact.AnalyzeAsync();
        await AnalyzeVisualAsync(catalog, new[] { keeper, duplicate });
        VisualSimilarityGroupRecord visual = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
        IReadOnlyList<VisualSimilarityMemberRecord> members = catalog.GetVisualGroupMembers(visual.GroupId);
        LibraryKeeperExplanation explanation = new LibraryKeeperExplanationService().Explain(members, new DuplicateKeeperPreferences());
        Assert.NotNull(explanation.RecommendedKeeperFileId);
        Assert.Contains(explanation.Factors, factor => factor.Contains("resolution", StringComparison.OrdinalIgnoreCase));
        var dashboard = new LibraryRecommendationService(catalog, new LibraryDuplicateCleanupService(catalog, catalog),
            new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog), catalog).GetCleanupDashboard();
        LibraryCleanupRecommendationCategory exactCategory = dashboard.Categories.Single(x => x.Name == "Exact duplicates");
        LibraryCleanupRecommendationCategory reviewedCategory = dashboard.Categories.Single(x => x.Name == "Reviewed visual duplicates");
        Assert.Equal(1, exactCategory.MatchCount);
        Assert.Equal(0, reviewedCategory.MatchCount);
    }

    [Fact]
    public void StorageOptimizationExcludesDuplicateMembersAndRanksCatalogCandidates()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "optimization"); Directory.CreateDirectory(library);
        string candidate = Write(library, "candidate.mkv", 200_000);
        AddInventoryAndMetadata(catalog, library, new[] { candidate }, _ => ("h264", 1920, 1080, 16_000_000L));
        LibraryStorageOptimizationCandidate listed = Assert.Single(catalog.QueryStorageOptimizationCandidates());
        Assert.Equal(candidate, listed.FullPath, ignoreCase: true);
        Assert.True(listed.OpportunityScore > 0);
        Assert.Contains("H264", listed.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemiAutomaticReviewUsesReviewedNextToPersistThePendingKeeperAndAdvance()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using SqliteLibraryCatalog catalog = CreateCatalog();
                string library = Path.Combine(_root, "semi-auto"); Directory.CreateDirectory(library);
                string recommended = Write(library, "recommended.mkv", 50_000);
                string manual = Write(library, "manual.mp4", 30_000);
                string nextRecommended = Write(library, "next-recommended.mkv", 55_000);
                string nextManual = Write(library, "next-manual.mp4", 35_000);
                AddInventoryAndMetadata(catalog, library, new[] { recommended, manual, nextRecommended, nextManual },
                    path => path == recommended ? ("hevc", 1920, 1080, 8_000_000L) : ("h264", 1280, 720, 2_000_000L));
                ulong[] firstMatch = { 0x1111111111111111, 0x2111111111111111, 0x3111111111111111, 0x4111111111111111, 0x5111111111111111, 0x6111111111111111 };
                ulong[] nextMatch = { 0xAAAAAAAAAAAAAAAA, 0xBAAAAAAAAAAAAAAA, 0xCAAAAAAAAAAAAAAA, 0xDAAAAAAAAAAAAAAA, 0xEAAAAAAAAAAAAAAA, 0xFAAAAAAAAAAAAAAA };
                using (var visual = new LibraryVisualAnalysisCoordinator(catalog,
                    new FakeVisualExtractor(path => path.StartsWith(Path.Combine(library, "next-"), StringComparison.OrdinalIgnoreCase) ? nextMatch : firstMatch),
                    new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70)))
                {
                    visual.AnalyzeAsync().GetAwaiter().GetResult();
                }
                VisualSimilarityGroupRecord group = catalog.QueryVisualGroups(new VisualGroupQuery()).Groups.First();
                long suggested = group.SuggestedKeeperFileId ?? throw new Xunit.Sdk.XunitException("Expected a suggested keeper.");
                long manualId = catalog.GetVisualGroupMembers(group.GroupId).Single(member => member.FileId != suggested).FileId;
                Assert.NotEqual(suggested, manualId);
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv", ".mp4" }, new EmptyMetadataProbe(),
                    new FakeVisualExtractor(_ => Array.Empty<ulong>()));
                bool semiAutomaticEnabled = false;
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                    AutomationOptionsProvider: () => new LibraryVisualReviewAutomationOptions(SemiAutomaticKeeperApproval: semiAutomaticEnabled)));
                form.Show();
                TabControl tabs = GetPrivateField<TabControl>(form, "_tabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Duplicates — Visual");
                PumpTask(InvokePrivateTask(form, "RefreshVisualGroupsAsync", new object?[] { null }));
                DataGridView groups = GetPrivateField<DataGridView>(form, "_visualGroupsGrid");
                PumpUntil(() => groups.Rows.Count > 1);
                DataGridViewRow row = groups.Rows.Cast<DataGridViewRow>().Single(row => ((VisualSimilarityGroupRecord)row.Tag!).GroupId == group.GroupId);
                groups.ClearSelection();
                row.Selected = true;
                groups.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                Application.DoEvents();
                semiAutomaticEnabled = true;
                bool sawSelectedKeeper = false;
                bool overrideClicked = false;
                bool reviewedNextClicked = false;
                bool advancedToNextGroup = false;
                using var timer = new System.Windows.Forms.Timer { Interval = 40 };
                timer.Tick += (_, _) =>
                {
                    Form? review = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open != form && open.Text.StartsWith("Review Visual Match", StringComparison.Ordinal));
                    if (review == null) return;
                    sawSelectedKeeper = Descendants<Button>(review).Any(button => button.Text == "Keeper selected" && button.BackColor == Color.FromArgb(46, 125, 50));
                    Assert.DoesNotContain(Descendants<Button>(review), button => button.Text == "Accept + Next");
                    if (!overrideClicked && Descendants<Button>(review).FirstOrDefault(button => button.Text == "Set as keeper") is { } setKeeper)
                    {
                        overrideClicked = true;
                        setKeeper.PerformClick();
                        return;
                    }
                    if (overrideClicked && !reviewedNextClicked)
                    {
                        Assert.Null(catalog.GetVisualGroup(group.GroupId)?.ManualKeeperFileId);
                        reviewedNextClicked = true;
                        Descendants<Button>(review).Single(button => button.Text == "Reviewed + Next").PerformClick();
                        return;
                    }
                    advancedToNextGroup = groups.SelectedRows.Count == 1 &&
                        ((VisualSimilarityGroupRecord)groups.SelectedRows[0].Tag!).GroupId != group.GroupId &&
                        Descendants<Button>(review).Any(button => button.Text == "Keeper selected" && button.BackColor == Color.FromArgb(46, 125, 50));
                    if (reviewedNextClicked && advancedToNextGroup && catalog.GetVisualGroup(group.GroupId)?.ManualKeeperFileId == manualId)
                        review.Close();
                };
                timer.Start();
                PumpTask(InvokePrivateTask(form, "OpenVisualReviewAsync"), TimeSpan.FromSeconds(10));
                timer.Stop();
                VisualSimilarityGroupRecord completed = catalog.GetVisualGroup(group.GroupId)!;
                Assert.True(sawSelectedKeeper);
                Assert.True(overrideClicked);
                Assert.True(reviewedNextClicked);
                Assert.True(advancedToNextGroup);
                Assert.True(completed.Reviewed);
                Assert.Equal(manualId, completed.ManualKeeperFileId);
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        if (thread.IsAlive) throw new TimeoutException("Semi-automatic visual review did not complete.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void VisualGridDoubleClickOpensReviewWithoutChangingCatalogState()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using SqliteLibraryCatalog catalog = CreateCatalog();
                string library = Path.Combine(_root, "double-click"); Directory.CreateDirectory(library);
                string first = Write(library, "first.mkv", 50_000);
                string second = Write(library, "second.mp4", 30_000);
                AddInventoryAndMetadata(catalog, library, new[] { first, second },
                    path => path == first ? ("hevc", 1920, 1080, 8_000_000L) : ("h264", 1280, 720, 2_000_000L));
                AnalyzeVisualAsync(catalog, new[] { first, second }).GetAwaiter().GetResult();
                VisualSimilarityGroupRecord before = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv", ".mp4" }, new EmptyMetadataProbe(),
                    new FakeVisualExtractor(_ => Array.Empty<ulong>()));
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                    AutomationOptions: new LibraryVisualReviewAutomationOptions(SemiAutomaticKeeperApproval: true)));
                form.Show();
                TabControl tabs = GetPrivateField<TabControl>(form, "_tabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Duplicates — Visual");
                PumpTask(InvokePrivateTask(form, "RefreshVisualGroupsAsync", new object?[] { null }));
                DataGridView groups = GetPrivateField<DataGridView>(form, "_visualGroupsGrid");
                PumpUntil(() => groups.Rows.Count == 1);

                // Reproduce the regression window: the row was loaded while active, then
                // a path became absent before review was opened. Opening must remain a
                // non-authoritative observation and must not suspend or hide the row.
                File.Delete(second);
                bool sawReview = false;
                bool sawSemiAutomaticSelection = false;
                using var timer = new System.Windows.Forms.Timer { Interval = 40 };
                timer.Tick += (_, _) =>
                {
                    Form? review = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open != form && open.Text.StartsWith("Review Visual Match", StringComparison.Ordinal));
                    if (review == null) return;
                    sawReview = true;
                    sawSemiAutomaticSelection = Descendants<Button>(review).Any(button => button.Text == "Keeper selected" && button.BackColor == Color.FromArgb(46, 125, 50));
                    review.Close();
                };
                timer.Start();
                var raiseDoubleClick = typeof(DataGridView).GetMethod("OnCellDoubleClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(DataGridView).FullName, "OnCellDoubleClick");
                raiseDoubleClick.Invoke(groups, new object[] { new DataGridViewCellEventArgs(1, 0) });
                timer.Stop();
                Application.DoEvents();

                VisualSimilarityGroupRecord after = catalog.GetVisualGroup(before.GroupId)!;
                Assert.True(sawReview);
                Assert.True(sawSemiAutomaticSelection);
                Assert.Equal(before.Reviewed, after.Reviewed);
                Assert.Equal(before.Ignored, after.Ignored);
                Assert.Equal(before.NotMatch, after.NotMatch);
                Assert.Equal(before.ManualKeeperFileId, after.ManualKeeperFileId);
                Assert.Equal(before.Eligibility, after.Eligibility);
                Assert.Single(groups.Rows.Cast<DataGridViewRow>());
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Visual double-click regression test did not complete.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void LibraryAnalyzerPrimaryLayoutsAndSettingsGroupsDoNotClipOrOverlap()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using SqliteLibraryCatalog catalog = CreateCatalog();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv", ".mp4" }, new EmptyMetadataProbe(),
                    new FakeVisualExtractor(_ => Array.Empty<ulong>()));
                using var form = new LibraryAnalyzerForm(runtime);
                form.Show();
                TabControl tabs = GetPrivateField<TabControl>(form, "_tabs");
                TableLayoutPanel exactControls = GetPrivateField<TableLayoutPanel>(form, "_duplicateControlArea");
                Button exactApply = GetPrivateField<Button>(form, "_duplicateApplyButton");
                TableLayoutPanel visualControls = GetPrivateField<TableLayoutPanel>(form, "_visualControlArea");
                Control visualActions = Descendants<Control>(form).Single(control => control.Name == "VisualActionArea");

                void AssertCurrentTabContains(Control control)
                {
                    Rectangle tabBounds = tabs.SelectedTab!.RectangleToScreen(tabs.SelectedTab.ClientRectangle);
                    Assert.True(control.Visible, $"{control.Name} should be visible.");
                    Assert.True(tabBounds.Contains(control.RectangleToScreen(control.ClientRectangle)), $"{control.Name} should remain inside the selected tab.");
                }

                void AssertLayoutsAtCurrentSize()
                {
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Duplicates — Exact");
                    Application.DoEvents();
                    AssertCurrentTabContains(exactControls);
                    AssertCurrentTabContains(exactApply);
                    Assert.False(exactControls.AutoScroll);

                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Duplicates — Visual");
                    Application.DoEvents();
                    AssertCurrentTabContains(visualControls);
                    AssertCurrentTabContains(visualActions);
                    Assert.False(visualControls.AutoScroll);
                    Assert.All(Descendants<Button>(visualActions), AssertCurrentTabContains);
                }

                form.Size = form.MinimumSize;
                Application.DoEvents();
                AssertLayoutsAtCurrentSize();
                form.Size = new Size(1280, 780);
                Application.DoEvents();
                AssertLayoutsAtCurrentSize();
                form.WindowState = FormWindowState.Maximized;
                Application.DoEvents();
                AssertLayoutsAtCurrentSize();

                Label visualNotice = Descendants<Label>(form).Single(label => label.Name == "VisualSafetyNotice");
                Label recommendationsIntro = Descendants<Label>(form).Single(label => label.Name == "CleanupRecommendationsIntro");
                Label optimizationIntro = Descendants<Label>(form).Single(label => label.Name == "StorageOptimizationIntro");
                Label exactStatus = GetPrivateField<Label>(form, "_duplicateStatus");
                Color accent = visualNotice.ForeColor;
                Assert.Equal(accent, recommendationsIntro.ForeColor);
                Assert.Equal(accent, optimizationIntro.ForeColor);
                Assert.Equal(accent, exactStatus.ForeColor);
                Assert.True(accent.B > accent.R && accent.B > accent.G, "The Library Analyzer accent should be visibly blue.");
                form.Close();

                string extensions = Path.Combine(_root, "extensions.txt");
                File.WriteAllText(extensions, ".mp4");
                using var settings = new SettingsForm(new Config(), extensions, new[] { ".mp4" }, _root);
                settings.Show();
                Application.DoEvents();
                Control analyzerPanel = settings.Controls.Find("LibraryAnalyzerSettingsPanel", true).Single();
                Control cleanup = settings.Controls.Find("grpLibraryAnalyzerCleanup", true).Single();
                Control productivity = settings.Controls.Find("grpLibraryAnalyzerReviewProductivity", true).Single();
                Control explorer = GetPrivateField<GroupBox>(settings, "grpExplorerIntegration");
                Control smart = GetPrivateField<GroupBox>(settings, "grpSmartRecommendations");
                Assert.False(analyzerPanel.Bounds.IntersectsWith(explorer.Bounds));
                Assert.False(analyzerPanel.Bounds.IntersectsWith(smart.Bounds));
                Assert.False(cleanup.Bounds.IntersectsWith(productivity.Bounds));
                Assert.True(settings.AutoScroll);
                settings.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Library Analyzer layout regression test did not complete.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private SqliteLibraryCatalog CreateCatalog()
    {
        var catalog = new SqliteLibraryCatalog(Path.Combine(_root, $"{Guid.NewGuid():N}.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        catalog.Initialize();
        return catalog;
    }

    private static async Task AnalyzeVisualAsync(SqliteLibraryCatalog catalog, IReadOnlyCollection<string> paths)
    {
        ulong[] hashes = { 0x1111111111111111, 0x2111111111111111, 0x3111111111111111, 0x4111111111111111, 0x5111111111111111, 0x6111111111111111 };
        using var visual = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes),
            new LibraryVisualAnalysisOptions(1, 2, 8, 128, 3, 70));
        await visual.AnalyzeAsync();
    }

    private static void AddInventoryAndMetadata(SqliteLibraryCatalog catalog, string root, IReadOnlyList<string> paths,
        Func<string, (string Codec, int Width, int Height, long BitRate)> metadata)
    {
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        foreach (string path in paths)
        {
            FileInfo file = new(path);
            LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(scan,
                new[] { new LibraryInventoryEntry(path, Path.GetRelativePath(root, path), file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) }, 1).Mutations);
            var values = metadata(path);
            var probe = new MediaProbeResult
            {
                Success = true, FormatName = Path.GetExtension(path), DurationSeconds = 60, BitRate = values.BitRate,
                Streams = new[] { new MediaProbeStreamInfo { CodecType = "video", CodecName = values.Codec, Width = values.Width, Height = values.Height } }
            };
            catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(new LibraryEnrichmentRequest(mutation.FileId, mutation.FullPath, "", file.Length, file.LastWriteTimeUtc),
                probe, 1, "probe", DateTime.UtcNow, null));
        }
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, paths.Count, 0, paths.Count, 0, 0, 0));
    }

    private static string Write(string root, string name, int bytes)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    private sealed class FakeVisualExtractor : ILibraryVisualFingerprintExtractor
    {
        private readonly Func<string, IReadOnlyList<ulong>> _factory;
        public FakeVisualExtractor(Func<string, IReadOnlyList<ulong>> factory) => _factory = factory;
        public string ToolVersion => "phase7-test";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(_factory(candidate.FullPath));
    }

    private sealed class EmptyMetadataProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "phase7-empty-probe";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeResult { Success = false, ErrorMessage = "Not used by this test." });
    }

    private static T GetPrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().FullName, name));

    private static Task InvokePrivateTask(object instance, string name, params object?[] arguments) =>
        (Task)(instance.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(instance, arguments)
            ?? throw new MissingMethodException(instance.GetType().FullName, name));

    private static void PumpTask(Task task, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The WinForms operation did not complete.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The WinForms condition did not complete.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static IEnumerable<T> Descendants<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T result) yield return result;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
