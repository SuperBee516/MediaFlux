using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryAnalyzerExactManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-ExactManagementTests", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerExactManagementTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExactKeeperPolicyHonorsManualProtectionPreferredRootsAndDeterministicPathRules()
    {
        ExactDuplicateMemberRecord incoming = Member(1, @"H:\Incoming\Movie copy.mkv");
        ExactDuplicateMemberRecord preferred = Member(2, @"Y:\Media\Movies\Movie.mkv");
        var preferences = new DuplicateKeeperPreferences { ExactPreferredLocations = new() { @"Y:\Media\Movies", @"H:\Incoming" } };
        Assert.Equal(2, ExactDuplicateKeeperPolicy.Select(new[] { incoming, preferred }, preferences).Keeper.FileId);

        ExactDuplicateMemberRecord protectedIncoming = incoming with { IsProtected = true };
        Assert.Equal(1, ExactDuplicateKeeperPolicy.Select(new[] { protectedIncoming, preferred }, preferences).Keeper.FileId);

        ExactDuplicateMemberRecord manualPreferred = preferred with { IsManualKeeper = true };
        Assert.Equal(2, ExactDuplicateKeeperPolicy.Select(new[] { protectedIncoming, manualPreferred }, preferences).Keeper.FileId);

        var noLocationRules = new DuplicateKeeperPreferences();
        Assert.Equal(2, ExactDuplicateKeeperPolicy.Select(new[] { incoming, preferred }, noLocationRules).Keeper.FileId);
        Assert.Equal(2, ExactDuplicateKeeperPolicy.Select(new[] { preferred, incoming }, noLocationRules).Keeper.FileId);
    }

    [Fact]
    public void SelectAllExceptKeeperReturnsEveryOtherMember()
    {
        ExactDuplicateMemberRecord[] members = { Member(1, "a.mkv"), Member(2, "b.mkv"), Member(3, "c.mkv") };
        Assert.Equal(new long[] { 1, 3 }, ExactDuplicateSelectionPolicy.SelectAllExceptKeeper(members, 2));
    }

    [Fact]
    public void LocationSelectionSurvivesPollingAndUpdatesAndHandlesAddRemove()
    {
        Assert.Equal(new long[] { 2 }, LibraryLocationSelectionPolicy.Resolve(new long[] { 2 }, new long[] { 1, 2, 3 }));
        Assert.Equal(new long[] { 2 }, LibraryLocationSelectionPolicy.Resolve(new long[] { 2 }, new long[] { 1, 2, 3 }));
        Assert.Equal(new long[] { 4 }, LibraryLocationSelectionPolicy.Resolve(new long[] { 2 }, new long[] { 1, 2, 4 }, preferredLocationId: 4));
        Assert.Equal(new long[] { 1 }, LibraryLocationSelectionPolicy.Resolve(new long[] { 2 }, new long[] { 1, 3 }));
        Assert.Empty(LibraryLocationSelectionPolicy.Resolve(new long[] { 2 }, Array.Empty<long>()));
    }

    [Fact]
    public async Task BulkPlansAndLocationReclaimRespectKeeperProtectionAndIgnoredGroups()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string preferredRoot = Path.Combine(_root, "Media");
        string incomingRoot = Path.Combine(_root, "Incoming");
        Directory.CreateDirectory(preferredRoot);
        Directory.CreateDirectory(incomingRoot);
        byte[] first = Enumerable.Repeat((byte)11, 128_000).ToArray();
        byte[] second = Enumerable.Repeat((byte)22, 96_000).ToArray();
        string keeperA = Write(preferredRoot, "Movie A.mkv", first);
        string keeperB = Write(preferredRoot, "Movie B.mkv", second);
        string protectedA = Write(incomingRoot, "Movie A backup.mkv", first);
        string deleteA = Write(incomingRoot, "Movie A copy.mkv", first);
        string deleteB = Write(incomingRoot, "Movie B copy.mkv", second);
        AddInventory(catalog, preferredRoot, keeperA, keeperB);
        AddInventory(catalog, incomingRoot, protectedA, deleteA, deleteB);
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024),
            keeperPreferences: new DuplicateKeeperPreferences { ExactPreferredLocations = new() { preferredRoot, incomingRoot } });
        Assert.Equal(DuplicateAnalysisStatus.Completed, (await coordinator.AnalyzeAsync()).Status);

        ExactDuplicateGroupRecord groupA = GroupContaining(catalog, keeperA);
        ExactDuplicateGroupRecord groupB = GroupContaining(catalog, keeperB);
        long keeperAId = catalog.GetFileByPath(keeperA)!.Id;
        long keeperBId = catalog.GetFileByPath(keeperB)!.Id;
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(groupA.GroupId, keeperAId, true, false));
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(groupB.GroupId, keeperBId, true, true));
        catalog.SetFileProtection(catalog.GetFileByPath(protectedA)!.Id, true, "test");

        var cleanup = new LibraryDuplicateCleanupService(catalog, catalog,
            new DuplicateKeeperPreferences { ExactPreferredLocations = new() { preferredRoot, incomingRoot } });
        DuplicateCleanupPlanSummary all = cleanup.CreatePlanForAllEligible(DuplicateCleanupAction.Quarantine, Path.Combine(_root, "all-q"));
        DuplicateCleanupPlanItemRecord only = Assert.Single(catalog.GetCleanupPlanItemsBatch(all.PlanId, 0, 0, 10));
        Assert.Equal(deleteA, only.SourcePath);
        Assert.Equal(keeperAId, only.KeeperFileId);
        ExactDuplicateReclaimLocation reclaim = Assert.Single(catalog.GetExactDuplicateReclaimByLocation());
        Assert.Equal(incomingRoot, reclaim.LocationPath);
        Assert.Equal(first.Length, reclaim.ReclaimableBytes);
        Assert.Equal(1, reclaim.FileCount);

        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(groupB.GroupId, keeperBId, true, false));
        DuplicateCleanupPlanSummary selected = cleanup.CreatePlan(new[] { groupA.GroupId, groupB.GroupId }, DuplicateCleanupAction.Quarantine, Path.Combine(_root, "selected-q"));
        IReadOnlyList<DuplicateCleanupPlanItemRecord> selectedItems = catalog.GetCleanupPlanItemsBatch(selected.PlanId, 0, 0, 10);
        Assert.Equal(2, selectedItems.Count);
        Assert.Contains(selectedItems, item => item.SourcePath == deleteA);
        Assert.Contains(selectedItems, item => item.SourcePath == deleteB);
        Assert.DoesNotContain(selectedItems, item => item.SourcePath == protectedA);
        Assert.Equal(2, selectedItems.Select(item => item.GroupId).Distinct().Count());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        DuplicateCleanupExecutionResult canceled = await cleanup.ExecutePlanAsync(selected.PlanId, cancellation.Token);
        Assert.Equal(0, canceled.Excluded);
        Assert.Contains("Canceled", canceled.ErrorText);
        Assert.Equal(DuplicateCleanupStatus.Failed, catalog.GetCleanupPlan(selected.PlanId)!.Status);
        Assert.Equal(2, catalog.GetCleanupPlanSummary(selected.PlanId)!.PlannedItems);
        Assert.True(File.Exists(deleteA));
        Assert.True(File.Exists(deleteB));
    }

    [Fact]
    public async Task ReclamationHandoffCanPlanOneSelectedCandidateWithoutExpandingItsGroup()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog(Path.Combine(_root, "selected-reclamation.db"));
        string root = Path.Combine(_root, "selected-reclamation"); Directory.CreateDirectory(root);
        byte[] content = Enumerable.Repeat((byte)73, 128_000).ToArray();
        string keeper = Write(root, "keeper.mkv", content);
        string firstCopy = Write(root, "copy-a.mkv", content);
        string secondCopy = Write(root, "copy-b.mkv", content);
        AddInventory(catalog, root, keeper, firstCopy, secondCopy);
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 32 * 1024));
        Assert.Equal(DuplicateAnalysisStatus.Completed, (await coordinator.AnalyzeAsync()).Status);
        ExactDuplicateGroupRecord group = GroupContaining(catalog, keeper);
        long keeperId = catalog.GetFileByPath(keeper)!.Id;
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, keeperId, true, false));
        var cleanup = new LibraryDuplicateCleanupService(catalog, catalog);
        ExactCleanupCandidate chosen = cleanup.GetEligibleCandidates().Single(item => item.FileId == catalog.GetFileByPath(firstCopy)!.Id);
        DuplicateCleanupPlanSummary plan = cleanup.CreatePlanForCandidates(new[] { chosen }, DuplicateCleanupAction.Quarantine, Path.Combine(_root, "selected-q"));
        DuplicateCleanupPlanItemRecord item = Assert.Single(catalog.GetCleanupPlanItemsBatch(plan.PlanId, 0, 0, 10));
        Assert.Equal(firstCopy, item.SourcePath);
        Assert.NotEqual(secondCopy, item.SourcePath);
        Assert.True(File.Exists(firstCopy));
        Assert.True(File.Exists(secondCopy));
    }

    [Fact]
    public void PreferredLocationConfigurationClonesAndNormalizesDurably()
    {
        var preferences = new DuplicateKeeperPreferences { ExactPreferredLocations = new() { @"Y:\Media\", @"y:\media", " " } };
        preferences.Normalize();
        DuplicateKeeperPreferences clone = preferences.Clone();
        Assert.Equal(new[] { @"Y:\Media" }, clone.ExactPreferredLocations);
        clone.ExactPreferredLocations.Add(@"H:\Incoming");
        Assert.Single(preferences.ExactPreferredLocations);
    }

    [Fact]
    public async Task LargeCleanupIsPagedAndPersistsPartialResultsAcrossCancellation()
    {
        using SqliteLibraryCatalog catalog = CreateCatalog();
        string library = Path.Combine(_root, "large-cleanup");
        Directory.CreateDirectory(library);
        const int groupCount = 503;
        var paths = new List<string>(groupCount * 2);
        for (int index = 0; index < groupCount; index++)
        {
            byte[] content = new byte[2048];
            BitConverter.GetBytes(index).CopyTo(content, 0);
            string keeper = Write(library, $"keeper-{index:D4}.mkv", content);
            string copy = Write(library, $"copy-{index:D4}.mkv", content);
            paths.Add(keeper);
            paths.Add(copy);
        }
        AddInventory(catalog, library, paths.ToArray());
        using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(4, 128, 1024));
        Assert.Equal(DuplicateAnalysisStatus.Completed, (await coordinator.AnalyzeAsync()).Status);
        ExactDuplicateGroupRecord[] groups = catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Limit: 500)).Groups
            .Concat(catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Offset: 500, Limit: 500)).Groups).OrderBy(group => group.GroupId).ToArray();
        Assert.Equal(groupCount, groups.Length);

        ExactDuplicateGroupRecord ignored = groups[0];
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(ignored.GroupId, ignored.SuggestedKeeperFileId, true, true));
        ExactDuplicateGroupRecord protectedGroup = groups[1];
        ExactDuplicateMemberRecord protectedCandidate = catalog.GetDuplicateGroupMembers(protectedGroup.GroupId).Single(member => !member.IsSuggestedKeeper);
        catalog.SetFileProtection(protectedCandidate.FileId, true, "large cleanup test");
        ExactDuplicateGroupRecord manualGroup = groups[250];
        ExactDuplicateMemberRecord manualKeeper = catalog.GetDuplicateGroupMembers(manualGroup.GroupId).Single(member => Path.GetFileName(member.FullPath).StartsWith("copy-", StringComparison.Ordinal));
        catalog.SaveDuplicateDecision(new DuplicateGroupDecision(manualGroup.GroupId, manualKeeper.FileId, true, false));

        var planner = new LibraryDuplicateCleanupService(catalog, catalog);
        DuplicateCleanupPlanSummary plan = planner.CreatePlanForAllEligible(DuplicateCleanupAction.PermanentDelete);
        Assert.Equal(501, plan.TotalItems);
        Assert.Equal(501, plan.TotalGroups);
        IReadOnlyList<DuplicateCleanupPlanItemRecord> firstBatch = catalog.GetCleanupPlanItemsBatch(plan.PlanId, 0, 0, 500);
        Assert.Equal(500, firstBatch.Count);
        DuplicateCleanupPlanItemRecord cursor = firstBatch[^1];
        DuplicateCleanupPlanItemRecord finalItem = Assert.Single(catalog.GetCleanupPlanItemsBatch(plan.PlanId, cursor.GroupId, cursor.FileId, 500));
        Assert.DoesNotContain(firstBatch, item => item.GroupId == ignored.GroupId || item.FileId == protectedCandidate.FileId);
        DuplicateCleanupPlanItemRecord manualCandidate = Assert.Single(firstBatch, item => item.GroupId == manualGroup.GroupId);
        Assert.Equal(manualKeeper.FileId, manualCandidate.KeeperFileId);

        DuplicateCleanupPlanItemRecord stale = firstBatch[10];
        using (var stream = new FileStream(stale.SourcePath, FileMode.Append, FileAccess.Write, FileShare.None)) stream.WriteByte(99);
        DuplicateCleanupPlanItemRecord failing = firstBatch[20];
        var actions = new TrackingCleanupActions { FailPath = failing.SourcePath };
        var cleanup = new LibraryDuplicateCleanupService(catalog, catalog, null, actions, new EmptyIdentityProvider());
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.ProcessedItems >= 500) cancellation.Cancel();
        });
        DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(plan.PlanId, cancellation.Token, progress);

        Assert.Contains("Canceled", result.ErrorText);
        Assert.Equal(498, result.Succeeded);
        Assert.Equal(1, result.Excluded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(498L * 2048, result.ReclaimedBytes);
        DuplicateCleanupPlanSummary persisted = catalog.GetCleanupPlanSummary(plan.PlanId)!;
        Assert.Equal(DuplicateCleanupStatus.Failed, persisted.Status);
        Assert.Equal(1, persisted.PlannedItems);
        Assert.Equal(498, persisted.SucceededItems);
        Assert.Equal(1, persisted.ExcludedItems);
        Assert.Equal(1, persisted.FailedItems);
        Assert.True(File.Exists(finalItem.SourcePath));
        Assert.True(File.Exists(stale.SourcePath));
        Assert.True(File.Exists(failing.SourcePath));
        Assert.True(File.Exists(manualKeeper.FullPath));
        Assert.False(File.Exists(manualCandidate.SourcePath));
        Assert.True(File.Exists(catalog.GetDuplicateGroupMembers(ignored.GroupId).First().FullPath));
        Assert.True(File.Exists(protectedCandidate.FullPath));
        Assert.Equal(500, CountAuditRows(catalog, plan.PlanId));
    }

    [Fact]
    public async Task InterruptedRunningPlanRecoversWithoutRepeatingCompletedDeletion()
    {
        string database = Path.Combine(_root, "recovery.db");
        string library = Path.Combine(_root, "recovery-library");
        Directory.CreateDirectory(library);
        byte[] content = Enumerable.Repeat((byte)44, 4096).ToArray();
        string keeper = Write(library, "keeper.mkv", content);
        string first = Write(library, "copy-1.mkv", content);
        string second = Write(library, "copy-2.mkv", content);
        long planId;
        long bytes;
        using (SqliteLibraryCatalog catalog = CreateCatalog(database))
        {
            AddInventory(catalog, library, keeper, first, second);
            using var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 1024));
            await coordinator.AnalyzeAsync();
            ExactDuplicateGroupRecord group = Assert.Single(catalog.QueryDuplicateGroups(new DuplicateGroupQuery()).Groups);
            catalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, catalog.GetFileByPath(keeper)!.Id, true, false));
            DuplicateCleanupPlanSummary plan = new LibraryDuplicateCleanupService(catalog, catalog).CreatePlan(
                new[] { group.GroupId }, DuplicateCleanupAction.PermanentDelete);
            planId = plan.PlanId;
            bytes = plan.PlannedBytes;
            catalog.MarkCleanupPlanRunning(planId);
            DuplicateCleanupPlanItemRecord completed = catalog.GetCleanupPlanItemsBatch(planId, 0, 0, 1, DuplicateCleanupItemStatus.Planned).Single();
            File.Delete(completed.SourcePath);
            catalog.UpdateCleanupPlanItem(planId, completed.FileId, DuplicateCleanupItemStatus.Succeeded, "Permanently deleted", "");
            catalog.AppendCleanupAudit(planId, completed.FileId, completed.SourcePath, "Permanently deleted", DuplicateCleanupAction.PermanentDelete,
                DuplicateCleanupItemStatus.Succeeded, "Simulated committed batch before process interruption.");
            catalog.MarkFileRemovedByCleanup(completed.FileId, completed.SourcePath, "Simulated interrupted cleanup.");
        }

        using (SqliteLibraryCatalog reopened = CreateCatalog(database))
        {
            Assert.Equal(1, reopened.RecoverInterruptedCleanupPlans());
            DuplicateCleanupPlanSummary recovered = reopened.GetCleanupPlanSummary(planId)!;
            Assert.Equal(DuplicateCleanupStatus.Ready, recovered.Status);
            Assert.Equal(1, recovered.SucceededItems);
            Assert.Equal(1, recovered.PlannedItems);
            var actions = new TrackingCleanupActions();
            var cleanup = new LibraryDuplicateCleanupService(reopened, reopened, null, actions, new EmptyIdentityProvider());
            DuplicateCleanupExecutionResult result = await cleanup.ExecutePlanAsync(planId);
            Assert.Equal(2, result.Succeeded);
            Assert.Equal(bytes, result.ReclaimedBytes);
            Assert.Single(actions.AttemptedPaths);
            Assert.Equal(2, CountAuditRows(reopened, planId));
            Assert.Equal(DuplicateCleanupStatus.Completed, reopened.GetCleanupPlanSummary(planId)!.Status);
        }
    }

    [Fact]
    public void ExactManagementUiCommandsAndLabelsAreWired()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunSta(() =>
        {
            using SqliteLibraryCatalog catalog = CreateCatalog(Path.Combine(_root, "exact-ui.db"));
            string library = Path.Combine(_root, "exact-ui");
            Directory.CreateDirectory(library);
            byte[] first = Enumerable.Repeat((byte)61, 4096).ToArray();
            byte[] second = Enumerable.Repeat((byte)62, 4096).ToArray();
            string[] files =
            {
                Write(library, "first.mkv", first), Write(library, "first copy.mkv", first),
                Write(library, "second.mkv", second), Write(library, "second copy.mkv", second)
            };
            AddInventory(catalog, library, files);
            using (var coordinator = new LibraryDuplicateAnalysisCoordinator(catalog, new LibraryDuplicateAnalysisOptions(1, 8, 1024)))
                coordinator.AnalyzeAsync().GetAwaiter().GetResult();
            var played = new ConcurrentQueue<string>();
            var comparisons = new ConcurrentQueue<IReadOnlyList<string>>();
            using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyMetadataProbe(), new EmptyVisualExtractor());
            using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                VideoLauncher: played.Enqueue,
                ComparisonLauncher: (_, paths) => { comparisons.Enqueue(paths); return Task.CompletedTask; }));
            form.Show();
            TabControl tabs = GetPrivateField<TabControl>(form, "_tabs");
            TabPage exactTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Duplicates — Exact");
            TabPage visualTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Duplicates — Visual");
            Assert.Contains(Descendants<Button>(exactTab), button => button.Text == "Run Analysis");
            Assert.Contains(Descendants<Button>(visualTab), button => button.Text == "Run Analysis");
            tabs.SelectedTab = exactTab;
            DataGridView groups = GetPrivateField<DataGridView>(form, "_duplicateGroupsGrid");
            DataGridView members = GetPrivateField<DataGridView>(form, "_duplicateMembersGrid");
            PumpUntil(() => groups.Rows.Count == 2);
            groups.ClearSelection();
            groups.Rows[0].Selected = true;
            groups.Rows[1].Selected = true;
            long[] selectedIds = (long[])InvokePrivate(form, "SelectedGroupIds")!;
            Assert.Equal(2, selectedIds.Length);
            ContextMenuStrip groupMenu = GetPrivateField<ContextMenuStrip>(form, "_duplicateGroupsMenu");
            InvokePrivate(form, "DuplicateGroupsMenu_Opening", groupMenu, new CancelEventArgs());
            Assert.True(groupMenu.Items.Find("Delete", false).Single().Enabled);
            Assert.Equal("Delete Selected Groups…", groupMenu.Items.Find("Delete", false).Single().Text);

            groups.ClearSelection();
            groups.Rows[0].Selected = true;
            groups.CurrentCell = groups.Rows[0].Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
            PumpTask(InvokePrivateTask(form, "RefreshDuplicateMembersAsync"));
            Assert.Equal(2, members.Rows.Count);
            Assert.Contains("Created", members.Columns.Cast<DataGridViewColumn>().Select(column => column.Name));
            Assert.Contains("Modified", members.Columns.Cast<DataGridViewColumn>().Select(column => column.Name));
            Assert.All(members.Rows.Cast<DataGridViewRow>(), row =>
            {
                Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Created"].Value)));
                Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Modified"].Value)));
            });
            ContextMenuStrip memberMenu = GetPrivateField<ContextMenuStrip>(form, "_duplicateMembersMenu");
            InvokePrivate(form, "DuplicateMembersMenu_Opening", memberMenu, new CancelEventArgs());
            foreach (string name in new[] { "Keeper", "Protect", "Play", "Folder", "CopyPath", "Compare", "Reanalyze", "DeleteCandidate", "SelectOthers", "SelectAll", "SelectNone", "Invert", "SelectAvailable", "SelectUnprotected" })
                Assert.NotEmpty(memberMenu.Items.Find(name, false));
            memberMenu.Items.Find("Play", false).Single().PerformClick();
            Application.DoEvents();
            Assert.Single(played);

            InvokePrivate(form, "SelectAllExceptKeeper");
            Assert.Single(members.SelectedRows.Cast<DataGridViewRow>());
            ExactDuplicateMemberRecord selected = (ExactDuplicateMemberRecord)members.SelectedRows[0].Tag!;
            ExactDuplicateMemberRecord keeper = members.Rows.Cast<DataGridViewRow>().Select(row => (ExactDuplicateMemberRecord)row.Tag!).Single(member => member.IsSuggestedKeeper);
            Assert.NotEqual(keeper.FileId, selected.FileId);
            InvokePrivate(form, "DuplicateMembersMenu_Opening", memberMenu, new CancelEventArgs());
            Assert.True(memberMenu.Items.Find("Compare", false).Single().Enabled);
            Assert.True(memberMenu.Items.Find("DeleteCandidate", false).Single().Enabled);
            PumpTask(InvokePrivateTask(form, "CompareSelectedExactWithKeeperAsync"));
            Assert.Single(comparisons);
            Assert.Equal(2, comparisons.Single().Count);

            members.ClearSelection();
            DataGridViewRow keeperRow = members.Rows.Cast<DataGridViewRow>().Single(row => ((ExactDuplicateMemberRecord)row.Tag!).FileId == keeper.FileId);
            keeperRow.Selected = true;
            members.CurrentCell = keeperRow.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
            InvokePrivate(form, "DuplicateMembersMenu_Opening", memberMenu, new CancelEventArgs());
            Assert.False(memberMenu.Items.Find("DeleteCandidate", false).Single().Enabled);

            members.ClearSelection();
            DataGridViewRow selectedRow = members.Rows.Cast<DataGridViewRow>().Single(row => ((ExactDuplicateMemberRecord)row.Tag!).FileId == selected.FileId);
            selectedRow.Selected = true;
            members.CurrentCell = selectedRow.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
            InvokePrivate(form, "SetManualKeeper_Click", null, EventArgs.Empty);
            long groupId = ((ExactDuplicateGroupRecord)groups.Rows[0].Tag!).GroupId;
            PumpUntil(() => catalog.GetDuplicateGroup(groupId)?.ManualKeeperFileId == selected.FileId);
            PumpUntil(() => members.Rows.Cast<DataGridViewRow>().Any(row => ((ExactDuplicateMemberRecord)row.Tag!).IsManualKeeper));
            Assert.Equal(selected.FileId, catalog.GetDuplicateGroup(groupId)!.ManualKeeperFileId);
            PumpFor(TimeSpan.FromMilliseconds(100));
            form.Close();
        });
    }

    [Fact]
    public void LocationSelectionSurvivesRealTimerPollingAndStatusChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunSta(() =>
        {
            using SqliteLibraryCatalog catalog = CreateCatalog(Path.Combine(_root, "locations-ui.db"));
            LibraryLocationRecord first = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "A")));
            LibraryLocationRecord second = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "B")));
            using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyMetadataProbe(), new EmptyVisualExtractor());
            using var form = new LibraryAnalyzerForm(runtime);
            form.Show();
            TabControl tabs = GetPrivateField<TabControl>(form, "_tabs");
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Locations");
            System.Windows.Forms.Timer timer = GetPrivateField<System.Windows.Forms.Timer>(form, "_refreshTimer");
            timer.Interval = 50;
            PumpTask(InvokePrivateTask(form, "RefreshLocationsAsync"));
            DataGridView grid = GetPrivateField<DataGridView>(form, "_locationsGrid");
            DataGridViewRow secondRow = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToInt64(row.Cells[0].Value) == second.Id);
            grid.ClearSelection();
            secondRow.Selected = true;
            grid.CurrentCell = secondRow.Cells[1];
            PumpFor(TimeSpan.FromMilliseconds(250));
            Assert.Equal(new[] { second.Id }, SelectedLocationIds(grid));

            catalog.SetLocationAvailability(second.Id, LibraryLocationAvailability.Unavailable, "offline test");
            PumpUntil(() => Convert.ToString(grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToInt64(row.Cells[0].Value) == second.Id).Cells[3].Value) == "Unavailable");
            Assert.Equal(new[] { second.Id }, SelectedLocationIds(grid));
            LibraryLocationRecord third = catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "C")));
            PumpUntil(() => grid.Rows.Cast<DataGridViewRow>().Any(row => Convert.ToInt64(row.Cells[0].Value) == third.Id));
            Assert.Equal(new[] { second.Id }, SelectedLocationIds(grid));
            catalog.UpsertLocation(new LibraryLocationUpsert(first.Path, IsEnabled: false));
            PumpUntil(() => Convert.ToString(grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToInt64(row.Cells[0].Value) == first.Id).Cells[2].Value) == "No");
            Assert.Equal(new[] { second.Id }, SelectedLocationIds(grid));
            catalog.RemoveLocation(second.Id, removeOrphanedFiles: true);
            PumpUntil(() => grid.Rows.Count == 2);
            Assert.DoesNotContain(second.Id, SelectedLocationIds(grid));
            Assert.Single(SelectedLocationIds(grid));
            form.Close();
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private SqliteLibraryCatalog CreateCatalog(string? databasePath = null)
    {
        var catalog = new SqliteLibraryCatalog(databasePath ?? Path.Combine(_root, "library.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        catalog.Initialize();
        return catalog;
    }

    private static void AddInventory(SqliteLibraryCatalog catalog, string root, params string[] paths)
    {
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(root));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        LibraryInventoryEntry[] entries = paths.Select(path =>
        {
            var file = new FileInfo(path);
            return new LibraryInventoryEntry(path, Path.GetRelativePath(root, path), file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        }).ToArray();
        catalog.UpsertInventoryBatchDetailed(scan, entries, 1);
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, entries.Length, 0, entries.Length, 0, 0, 0));
    }

    private static ExactDuplicateGroupRecord GroupContaining(SqliteLibraryCatalog catalog, string path) =>
        catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Limit: 100)).Groups.Single(group =>
            catalog.GetDuplicateGroupMembers(group.GroupId).Any(member => member.FullPath == path));

    private static string Write(string root, string name, byte[] content)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static ExactDuplicateMemberRecord Member(long fileId, string path) => new(
        1, fileId, path, path.ToUpperInvariant(), Path.GetPathRoot(path) ?? "", 100,
        new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        "", "", fileId.ToString(), false, IndexedFileAvailability.Present, "", null, null, null, null, false, false, false);

    private static int CountAuditRows(SqliteLibraryCatalog catalog, long planId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalog.DatabasePath}");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM duplicate_cleanup_audit WHERE plan_id=$plan;";
        command.Parameters.AddWithValue("$plan", planId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed class EmptyIdentityProvider : ILibraryFileIdentityProvider
    {
        public LibraryFileIdentity GetIdentity(string path) => LibraryFileIdentity.Empty;
    }

    private sealed class TrackingCleanupActions : ILibraryDuplicateFileActions
    {
        public string FailPath { get; init; } = "";
        public HashSet<string> AttemptedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Recycle(string path) => DeletePermanent(path);
        public void DeletePermanent(string path)
        {
            AttemptedPaths.Add(path);
            if (string.Equals(path, FailPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("simulated deletion failure");
            File.Delete(path);
        }
        public string Quarantine(string path, string quarantineRoot, long groupId, long fileId)
        {
            DeletePermanent(path);
            return "test quarantine";
        }
    }

    private sealed class InlineProgress : IProgress<DuplicateCleanupProgress>
    {
        private readonly Action<DuplicateCleanupProgress> _report;
        public InlineProgress(Action<DuplicateCleanupProgress> report) => _report = report;
        public void Report(DuplicateCleanupProgress value) => _report(value);
    }

    private sealed class EmptyMetadataProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "exact-management-empty";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeResult { Success = false, ErrorMessage = "Not used." });
    }

    private sealed class EmptyVisualExtractor : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "exact-management-empty";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Exact management UI smoke test did not complete.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static T GetPrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().FullName, name));

    private static object? InvokePrivate(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, arguments)
        ?? (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.ReturnType == typeof(void)
            ? null : throw new MissingMethodException(instance.GetType().FullName, name));

    private static Task InvokePrivateTask(object instance, string name, params object?[] arguments) =>
        (Task)(instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, arguments)
            ?? throw new MissingMethodException(instance.GetType().FullName, name));

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(control => (control is T match ? new[] { match } : Array.Empty<T>()).Concat(Descendants<T>(control)));

    private static void PumpTask(Task task, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new TimeoutException("UI task did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(condition(), "UI condition did not become true.");
    }

    private static void PumpFor(TimeSpan duration)
    {
        DateTime deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static long[] SelectedLocationIds(DataGridView grid) => grid.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => Convert.ToInt64(row.Cells[0].Value)).OrderBy(id => id).ToArray();
}
