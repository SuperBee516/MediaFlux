using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
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
    public void SemiAutomaticReviewPreselectsRecommendationAndManualChoiceWins()
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
                AddInventoryAndMetadata(catalog, library, new[] { recommended, manual },
                    path => path == recommended ? ("hevc", 1920, 1080, 8_000_000L) : ("h264", 1280, 720, 2_000_000L));
                AnalyzeVisualAsync(catalog, new[] { recommended, manual }).GetAwaiter().GetResult();
                VisualSimilarityGroupRecord group = Assert.Single(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
                long suggested = group.SuggestedKeeperFileId ?? throw new Xunit.Sdk.XunitException("Expected a suggested keeper.");
                long manualId = catalog.GetFileByPath(manual)!.Id;
                Assert.NotEqual(suggested, manualId);
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
                DataGridViewRow row = Assert.Single(groups.Rows.Cast<DataGridViewRow>());
                groups.ClearSelection();
                row.Selected = true;
                groups.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                Application.DoEvents();
                bool overrideClicked = false;
                bool accepted = false;
                using var timer = new System.Windows.Forms.Timer { Interval = 40 };
                timer.Tick += (_, _) =>
                {
                    Form? review = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open != form && open.Text.StartsWith("Review Visual Match", StringComparison.Ordinal));
                    if (review == null) return;
                    if (!overrideClicked && Descendants<Button>(review).FirstOrDefault(button => button.Text == "Set as keeper") is { } setKeeper)
                    {
                        overrideClicked = true;
                        setKeeper.PerformClick();
                        return;
                    }
                    if (overrideClicked && !accepted && Descendants<Button>(review).FirstOrDefault(button => button.Text == "Accept + Next") is { } next)
                    {
                        accepted = true;
                        next.PerformClick();
                        return;
                    }
                    if (accepted && catalog.GetVisualGroup(group.GroupId)?.ManualKeeperFileId == manualId)
                        review.Close();
                };
                timer.Start();
                PumpTask(InvokePrivateTask(form, "OpenVisualReviewAsync"), TimeSpan.FromSeconds(10));
                timer.Stop();
                VisualSimilarityGroupRecord completed = catalog.GetVisualGroup(group.GroupId)!;
                Assert.True(overrideClicked);
                Assert.True(accepted);
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
