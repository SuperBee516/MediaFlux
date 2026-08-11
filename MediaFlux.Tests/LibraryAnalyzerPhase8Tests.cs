using Microsoft.Data.Sqlite;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerPhase8Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-LibraryPhase8Tests", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerPhase8Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SchemaNineAddsFamilyPersistenceAndMigratesDecisionHistory()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, catalog.GetDiagnostics().SchemaVersion);
        using var connection = new SqliteConnection($"Data Source={catalog.DatabasePath}"); connection.Open();
        foreach (string table in new[] { "visual_families", "visual_family_members", "visual_family_edges", "visual_family_decisions" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
        }
        using SqliteCommand decisionCheck = connection.CreateCommand();
        decisionCheck.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='library_decision_events';";
        Assert.Contains("BETWEEN 0 AND 5", Convert.ToString(decisionCheck.ExecuteScalar()), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionEightCatalogMigratesToNineAndPreservesDecisionEvents()
    {
        string database = Path.Combine(_root, "v8-migration.db");
        var old = new LibraryCatalogDatabase(database, Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        Assert.Equal(8, old.InitializeForTesting(8).SchemaVersion);
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = "INSERT INTO library_decision_events(target_kind,target_key,event_kind,before_state,after_state,batch_id,source,occurred_utc_ticks) VALUES(4,'batch:old',5,'{}','{\"applied\":true}','old','phase2',1);";
            seed.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using SqliteLibraryCatalog catalog = CreateCatalog(database);
        Assert.Equal(LibraryCatalogMigrations.CurrentVersion, catalog.GetDiagnostics().SchemaVersion);
        LibraryDecisionEvent preserved = Assert.Single(catalog.GetDecisionHistory());
        Assert.Equal("batch:old", preserved.TargetKey);
    }

    [Fact]
    public void CompleteLinkTriangleCreatesFamilyAndSuppressesOnlyInternalPairPresentation()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "triangle", 3);
        SeedPairs(catalog, files, (0, 1, 98), (0, 2, 96), (1, 2, 97));
        VisualFamilyConstructionResult result = catalog.RebuildVisualFamilies();
        Assert.Equal(1, result.FamiliesCreated);
        VisualFamilyRecord family = Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        Assert.Equal(3, family.MemberCount);
        Assert.Equal(96, family.MinimumConfidence);
        Assert.Equal(3, catalog.GetVisualFamilyEdges(family.FamilyId).Count);
        Assert.Empty(catalog.QueryVisualGroups(new VisualGroupQuery()).Groups);
        Assert.Equal(3, catalog.QueryVisualGroups(new VisualGroupQuery(IncludeFamilyPairs: true)).Groups.Count);
    }

    [Fact]
    public void MissingDirectEdgeLeavesAmbiguousChainAsPairs()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "chain", 3);
        SeedPairs(catalog, files, (0, 1, 98), (1, 2, 97));
        VisualFamilyConstructionResult result = catalog.RebuildVisualFamilies();
        Assert.Equal(0, result.FamiliesCreated);
        Assert.Empty(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        Assert.Equal(2, catalog.QueryVisualGroups(new VisualGroupQuery()).Groups.Count);
    }

    [Fact]
    public void OverlappingMaximalCliquesAreRejectedAsConflicting()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "overlap", 4);
        SeedPairs(catalog, files, (0, 1, 98), (0, 2, 97), (1, 2, 96), (1, 3, 97), (2, 3, 98));
        VisualFamilyConstructionResult result = catalog.RebuildVisualFamilies();
        Assert.Equal(0, result.FamiliesCreated);
        Assert.Equal(1, result.AmbiguousComponents);
        Assert.Equal(5, catalog.QueryVisualGroups(new VisualGroupQuery()).Groups.Count);
    }

    [Fact]
    public void FamilyKeeperScoringHonorsMarginProtectionAndManualOverride()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "keeper", 3,
            (0, "hevc", 1920, 1080, 16_000_000), (1, "hevc", 1920, 1080, 8_000_000), (2, "h264", 1920, 1080, 3_000_000));
        SeedPairs(catalog, files, (0, 1, 98), (0, 2, 97), (1, 2, 96));
        catalog.RebuildVisualFamilies();
        VisualFamilyRecord family = Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        var service = new LibraryVisualFamilyService(catalog,
            new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog),
            new DuplicateKeeperPreferences { MinimumScoreMargin = 0 });
        Assert.Null(service.RefreshSuggestedKeeper(family.FamilyId, 101));
        long expected = catalog.GetFileByPath(files[0])!.Id;
        Assert.Equal(expected, service.RefreshSuggestedKeeper(family.FamilyId, 0));

        long manual = catalog.GetFileByPath(files[2])!.Id;
        catalog.SaveVisualFamilyDecision(new VisualFamilyDecision(family.FamilyId, manual, true, false));
        VisualFamilyRecord decided = catalog.GetVisualFamily(family.FamilyId)!;
        Assert.Equal(manual, decided.ManualKeeperFileId);
        Assert.Equal(manual, service.RefreshSuggestedKeeper(family.FamilyId, 0));
        catalog.SetFileProtection(manual, true, "family test");
        Assert.True(catalog.GetVisualFamilyMembers(family.FamilyId).Single(x => x.FileId == manual).IsProtected);
    }

    [Fact]
    public void UnavailableFamilyMembersSuspendFamilyWithoutDiscardingPairEvidence()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "unavailable", 3);
        SeedPairs(catalog, files, (0, 1, 98), (0, 2, 97), (1, 2, 96));
        catalog.RebuildVisualFamilies();
        Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        LibraryLocationRecord location = Assert.Single(catalog.GetLocations());
        catalog.SetLocationAvailability(location.Id, LibraryLocationAvailability.Unavailable, "offline", markMembershipsUnavailable: true);
        Assert.Empty(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery(IncludeInactive: true)).Families);
        Assert.Equal(3, catalog.QueryVisualGroups(new VisualGroupQuery(IncludeInactive: true, IncludeFamilyPairs: true)).Groups.Count);
    }

    [Fact]
    public void FamilyDecisionHistoryUndoAndBackupRestoreAreDurable()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "decision", 3);
        SeedPairs(catalog, files, (0, 1, 98), (0, 2, 97), (1, 2, 96));
        catalog.RebuildVisualFamilies();
        VisualFamilyRecord family = Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        long keeper = catalog.GetFileByPath(files[0])!.Id;
        catalog.SaveVisualFamilyDecision(new VisualFamilyDecision(family.FamilyId, keeper, true, false, "family-batch", "family-review"));
        LibraryDecisionEvent history = Assert.Single(catalog.GetDecisionHistory(), x => x.TargetKind == LibraryDecisionTargetKind.VisualFamily);
        Assert.Equal("family-batch", history.BatchId);
        Assert.True(catalog.UndoDecision(history.Id).Succeeded);
        Assert.False(catalog.GetVisualFamily(family.FamilyId)!.Reviewed);

        catalog.SaveVisualFamilyDecision(new VisualFamilyDecision(family.FamilyId, keeper, true, false));
        LibraryDecisionEvent superseded = catalog.GetDecisionHistory().First(x =>
            x.TargetKind == LibraryDecisionTargetKind.VisualFamily && x.ReversedByEventId == null);
        catalog.SaveVisualFamilyDecision(new VisualFamilyDecision(family.FamilyId, keeper, true, true));
        Assert.False(catalog.UndoDecision(superseded.Id).Succeeded);
        catalog.SaveVisualFamilyDecision(new VisualFamilyDecision(family.FamilyId, keeper, true, false));
        string backup = catalog.CreateUserDataBackup(Path.Combine(_root, "family-decisions.db"));
        using SqliteLibraryCatalog restored = CreateCatalog(Path.Combine(_root, "restored.db"));
        LibraryUserDataRestoreResult result = restored.RestoreUserDataBackup(backup);
        Assert.Equal(1, result.FamilyDecisions);
    }

    [Fact]
    public async Task FamilyCleanupUsesDirectEdgesDeduplicatesCandidatesAndRevalidatesChanges()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "cleanup", 4,
            (0, "hevc", 1920, 1080, 8_000_000), (1, "h264", 1280, 720, 4_000_000),
            (2, "h264", 960, 540, 3_000_000), (3, "h264", 854, 480, 2_000_000));
        await AnalyzeVisualAsync(catalog, files);
        VisualFamilyRecord family = Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        long keeper = catalog.GetFileByPath(files[0])!.Id;
        catalog.SaveVisualFamilyDecision(new VisualFamilyDecision(family.FamilyId, keeper, true, false));
        catalog.SetFileProtection(catalog.GetFileByPath(files[1])!.Id, true, "protected family candidate");
        var cleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog);
        var service = new LibraryVisualFamilyService(catalog, cleanup);
        VisualFamilyCleanupProposal proposal = service.BuildCleanupProposal(family.FamilyId);
        Assert.Equal(2, proposal.Items.Count);
        Assert.Equal(2, proposal.Items.Select(x => x.Candidate.FileId).Distinct().Count());
        Assert.Equal(1, proposal.ExcludedMembers);
        VisualCleanupPlanRecord plan = cleanup.CreatePlan(proposal.Items, DuplicateCleanupAction.Quarantine,
            Path.Combine(_root, "quarantine"), allowUnreviewed: true, minimumConfidence: family.MinimumConfidence);
        VisualFamilyCleanupProposal duplicatePreview = service.BuildCleanupProposal(family.FamilyId);
        Assert.Empty(duplicatePreview.Items);
        using (FileStream changed = new(files[3], FileMode.Append, FileAccess.Write, FileShare.None)) changed.WriteByte(7);
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Excluded);
        Assert.True(File.Exists(files[0]));
    }

    [Fact]
    public async Task DashboardDoesNotDoubleCountExactCandidatesInsideFamily()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string[] files = CreateFiles(catalog, "dashboard", 3);
        File.Copy(files[0], files[1], overwrite: true);
        File.Copy(files[0], files[2], overwrite: true);
        AddInventoryAndMetadata(catalog, Path.GetDirectoryName(files[0])!, files, _ => ("h264", 1920, 1080, 6_000_000L));
        using (var exact = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 16 * 1024)))
            await exact.AnalyzeAsync();
        await AnalyzeVisualAsync(catalog, files);
        VisualFamilyRecord family = Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
        catalog.SaveVisualFamilyDecision(new VisualFamilyDecision(family.FamilyId, catalog.GetFileByPath(files[0])!.Id, true, false));
        var visualCleanup = new LibraryVisualDuplicateCleanupService(catalog, catalog, catalog);
        var familyService = new LibraryVisualFamilyService(catalog, visualCleanup);
        var recommendations = new LibraryRecommendationService(catalog, new LibraryDuplicateCleanupService(catalog, catalog), visualCleanup, catalog);
        recommendations.AttachFamilies(catalog, familyService);
        LibraryCleanupRecommendationDashboard dashboard = recommendations.GetCleanupDashboard();
        Assert.Equal(2, dashboard.Categories.Single(x => x.Name == "Exact duplicates").MatchCount);
        Assert.Equal(0, dashboard.Categories.Single(x => x.Name == "Reviewed visual families").MatchCount);
        Assert.Equal(0, dashboard.Categories.Single(x => x.Name == "Reviewed visual duplicates").MatchCount);
    }

    [Fact]
    public void ConstructionScalesAcrossDenseAndManyDisjointComponents()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        const int denseCount = 24;
        const int triangleCount = 40;
        string[] dense = CreateFiles(catalog, "performance-dense", denseCount + triangleCount * 3);
        var pairs = new List<(int Left, int Right, double Confidence)>();
        for (int left = 0; left < denseCount; left++)
        for (int right = left + 1; right < denseCount; right++)
            pairs.Add((left, right, 96));
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            int first = denseCount + triangle * 3;
            pairs.Add((first, first + 1, 97));
            pairs.Add((first, first + 2, 96));
            pairs.Add((first + 1, first + 2, 98));
        }
        SeedPairs(catalog, dense, pairs.ToArray());
        VisualFamilyConstructionResult result = catalog.RebuildVisualFamilies();
        Assert.Equal(1 + triangleCount, result.FamiliesCreated);
        Assert.Equal(denseCount, result.LargestComponent);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(5), $"Construction took {result.Elapsed}.");
    }

    [Fact]
    public void FamilyReviewUiShowsAllMembersAndPersistsManualKeeper()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        string stage = "starting";
        var thread = new Thread(() =>
        {
            try
            {
                stage = "creating catalog";
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using SqliteLibraryCatalog catalog = CreateCatalog();
                string[] files = CreateFiles(catalog, "ui-family", 3);
                SeedPairs(catalog, files, (0, 1, 98), (0, 2, 97), (1, 2, 96));
                catalog.RebuildVisualFamilies();
                VisualFamilyRecord family = Assert.Single(catalog.QueryVisualFamilies(new VisualFamilyQuery()).Families);
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyMetadataProbe(), new FakeVisualExtractor(_ => Array.Empty<ulong>()));
                using var form = new LibraryAnalyzerForm(runtime);
                stage = "showing analyzer";
                form.Show();
                TabControl tabs = GetPrivateField<TabControl>(form, "_tabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Duplicates — Families");
                PumpTask(InvokePrivateTask(form, "RefreshVisualFamiliesAsync"));
                DataGridView families = GetPrivateField<DataGridView>(form, "_familyGrid");
                PumpUntil(() => families.Rows.Count == 1);
                families.Rows[0].Selected = true;
                families.CurrentCell = families.Rows[0].Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                bool sawMembers = false;
                bool keeperClicked = false;
                using var timer = new System.Windows.Forms.Timer { Interval = 40 };
                timer.Tick += (_, _) =>
                {
                    Form? review = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open != form && open.Text.StartsWith("Review Visual Family", StringComparison.Ordinal));
                    if (review == null) return;
                    DataGridView? grid = Descendants<DataGridView>(review).FirstOrDefault();
                    if (grid?.Rows.Count != 3) return;
                    sawMembers = true;
                    if (!keeperClicked)
                    {
                        keeperClicked = true;
                        grid.ClearSelection(); grid.Rows[2].Selected = true;
                        grid.CurrentCell = grid.Rows[2].Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                        Descendants<Button>(review).Single(button => button.Text == "Set selected keeper").PerformClick();
                    }
                    if (catalog.GetVisualFamily(family.FamilyId)?.ManualKeeperFileId != null)
                        review.Close();
                };
                timer.Start();
                stage = "reviewing family";
                PumpTask(InvokePrivateTask(form, "OpenVisualFamilyReviewAsync"), TimeSpan.FromSeconds(10));
                timer.Stop();
                stage = "verifying review";
                Assert.True(sawMembers);
                Assert.True(catalog.GetVisualFamily(family.FamilyId)!.Reviewed);
                form.Close();
                stage = "disposing analyzer";
            }
            catch (Exception ex) { failure = ex; }
            finally { stage = failure == null ? "completed" : $"failed: {failure.GetType().Name}"; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // The review itself has a 10-second pumped wait; allow setup, catalog work,
        // and teardown headroom when the full UI suite is contending for resources.
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException($"Family review UI smoke test did not complete (stage: {stage}).");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private SqliteLibraryCatalog CreateCatalog(string? path = null)
    {
        var catalog = new SqliteLibraryCatalog(path ?? Path.Combine(_root, $"{Guid.NewGuid():N}.db"),
            Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        catalog.Initialize();
        return catalog;
    }

    private string[] CreateFiles(SqliteLibraryCatalog catalog, string folderName, int count,
        params (int Index, string Codec, int Width, int Height, long Bitrate)[] overrides)
    {
        string folder = Path.Combine(_root, folderName); Directory.CreateDirectory(folder);
        string[] files = Enumerable.Range(0, count).Select(index =>
        {
            string path = Path.Combine(folder, $"video-{index}.mkv");
            File.WriteAllBytes(path, Enumerable.Range(0, 20_000 + index * 137).Select(x => (byte)((x + index) % 251)).ToArray());
            return path;
        }).ToArray();
        Dictionary<int, (int Index, string Codec, int Width, int Height, long Bitrate)> values = overrides.ToDictionary(x => x.Index);
        AddInventoryAndMetadata(catalog, folder, files, index => values.TryGetValue(index, out var item)
            ? (item.Codec, item.Width, item.Height, item.Bitrate)
            : ("h264", 1920, 1080, 6_000_000L));
        return files;
    }

    private static void SeedPairs(SqliteLibraryCatalog catalog, IReadOnlyList<string> files,
        params (int Left, int Right, double Confidence)[] pairs)
    {
        VisualAnalysisHandle run = catalog.BeginVisualAnalysis("phase3-seed", 1);
        catalog.PrepareVisualSimilarityGroups(run);
        catalog.AppendVisualSimilarityGroups(run, pairs.Select(pair => new VisualMatchWrite(
            catalog.GetFileByPath(files[pair.Left])!.Id, catalog.GetFileByPath(files[pair.Right])!.Id,
            pair.Confidence, 6, 6, 1, 0, "synthetic direct evidence")).ToArray());
        catalog.PublishVisualSimilarityGroups(run);
        catalog.CompleteVisualAnalysis(run, new VisualAnalysisCompletion(DuplicateAnalysisStatus.Completed,
            files.Count, files.Count, pairs.Length, pairs.Length, 0));
    }

    private static async Task AnalyzeVisualAsync(SqliteLibraryCatalog catalog, IReadOnlyList<string> files)
    {
        ulong[] hashes = { 0x1111111111111111, 0x2111111111111111, 0x3111111111111111, 0x4111111111111111, 0x5111111111111111, 0x6111111111111111 };
        using var analysis = new LibraryVisualAnalysisCoordinator(catalog, new FakeVisualExtractor(_ => hashes),
            new LibraryVisualAnalysisOptions(1, Math.Max(3, files.Count), 16, 128, 3, 70));
        LibraryVisualAnalysisResult result = await analysis.AnalyzeAsync();
        Assert.Equal(files.Count * (files.Count - 1) / 2, result.MatchPairs);
    }

    private static void AddInventoryAndMetadata(SqliteLibraryCatalog catalog, string root, IReadOnlyList<string> paths,
        Func<int, (string Codec, int Width, int Height, long Bitrate)> metadata)
    {
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        for (int index = 0; index < paths.Count; index++)
        {
            string path = paths[index]; FileInfo file = new(path);
            LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(scan,
                new[] { new LibraryInventoryEntry(path, Path.GetRelativePath(root, path), file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) }, 1).Mutations);
            var values = metadata(index);
            var probe = new MediaProbeResult
            {
                Success = true, FormatName = ".mkv", DurationSeconds = 60, BitRate = values.Bitrate,
                Streams = new[] { new MediaProbeStreamInfo { CodecType = "video", CodecName = values.Codec, Width = values.Width, Height = values.Height, FrameRate = 30 } }
            };
            catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(new LibraryEnrichmentRequest(mutation.FileId, mutation.FullPath, "", file.Length, file.LastWriteTimeUtc),
                probe, 1, "phase3-probe", DateTime.UtcNow, null));
        }
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, paths.Count, 0, paths.Count, 0, 0, 0));
    }

    private sealed class FakeVisualExtractor : ILibraryVisualFingerprintExtractor
    {
        private readonly Func<string, IReadOnlyList<ulong>> _factory;
        public FakeVisualExtractor(Func<string, IReadOnlyList<ulong>> factory) => _factory = factory;
        public string ToolVersion => "phase3-visual";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(_factory(candidate.FullPath));
    }

    private sealed class EmptyMetadataProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "phase3-empty-probe";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeResult { Success = false, ErrorMessage = "Not used." });
    }

    private static T GetPrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().FullName, name));

    private static Task InvokePrivateTask(object instance, string name, params object?[] arguments) =>
        (Task)(instance.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(instance, arguments)
            ?? throw new MissingMethodException(instance.GetType().FullName, name));

    private static void PumpTask(Task task, TimeSpan? timeout = null)
    {
        PumpUntil(() => task.IsCompleted, timeout);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The WinForms condition did not complete.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static IEnumerable<T> Descendants<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
