using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly ContextMenuStrip _healthMenu = new();
    private readonly ContextMenuStrip _historyMenu = new();
    private readonly ContextMenuStrip _recommendationsMenu = new();
    private readonly ContextMenuStrip _policiesMenu = new();
    private readonly ContextMenuStrip _reclamationMenu = new();
    private readonly ContextMenuStrip _integrityMenu = new();
    private readonly ContextMenuStrip _maintenanceMenu = new();

    private void ConfigurePhase3ContextMenus()
    {
        ConfigureHealthContextMenus();
        ConfigureRecommendationsContextMenu();
        ConfigurePoliciesContextMenu();
        ConfigureReclamationContextMenu();
        ConfigureIntegrityContextMenu();
        ConfigureMaintenanceContextMenu();
        AddGeneralRemovalMenu(_filesMenu, () => SelectedFiles().Select(file => (file.FileId, file.FullPath)), RefreshFilesAsync);
        AddCopySubmenu(_filesMenu, () => SelectedFiles().Select(file => (file.FileId, file.FullPath)));
        _filesMenu.Opening += (_, _) => UpdateFileBackedMenu(_filesMenu, SelectedFiles().Select(file => (file.FileId, file.FullPath)).ToArray());
    }

    private void ConfigureHealthContextMenus()
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(_healthMenu, "Execute recommended action", "Execute", () =>
        {
            LibraryHealthIssue? issue = LibraryAnalyzerGridInteraction.SelectedItems<LibraryHealthIssue>(_healthGrid).SingleOrDefault();
            if (issue?.Kind == LibraryHealthIssueKind.RestorableQuarantine) RestoreQuarantine_Click(null, EventArgs.Empty);
            else QueueHealthReanalysis_Click(null, EventArgs.Empty);
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(_healthMenu, "Re-analyze", "Reanalyze", () => { QueueHealthReanalysis_Click(null, EventArgs.Empty); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_healthMenu, "Run appropriate integrity check", "Integrity", () =>
        {
            LibraryHealthIssue[] issues = LibraryAnalyzerGridInteraction.SelectedItems<LibraryHealthIssue>(_healthGrid);
            foreach (IGrouping<LibraryIntegrityScrubType, LibraryHealthIssue> group in issues.Where(item => item.FileId.HasValue && item.SuggestedIntegrityScrub.HasValue).GroupBy(item => item.SuggestedIntegrityScrub!.Value))
                _runtime.Integrity.QueueFiles(group.Select(item => item.FileId!.Value), group.Key);
            return Task.CompletedTask;
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(_healthMenu, "Restore quarantine", "Restore", () => { RestoreQuarantine_Click(null, EventArgs.Empty); return Task.CompletedTask; });
        _healthMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_healthMenu, "Play Video", "Play", () => { if (SelectedHealthFiles().SingleOrDefault() is { } item) PlayLibraryVideo(item.FullPath); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_healthMenu, "Open Containing Folder", "Folder", () => { OpenContainingFolders(SelectedHealthFiles().Select(item => item.FullPath)); return Task.CompletedTask; });
        AddCopySubmenu(_healthMenu, () => SelectedHealthFiles().Select(item => (item.FileId, item.FullPath)));
        LibraryAnalyzerGridInteraction.AddMenuItem(_healthMenu, "Locate in Files", "Locate", async () =>
        {
            LibraryHealthIssue? issue = LibraryAnalyzerGridInteraction.SelectedItems<LibraryHealthIssue>(_healthGrid).SingleOrDefault();
            if (issue?.FileId is not long id) return;
            LibraryGeneralFileSnapshot? file = ResolveGeneralFileSnapshot(id, "");
            if (file != null) await LocateFileInFilesAsync(id, file.FullPath);
        });
        _healthMenu.Opening += (_, _) =>
        {
            LibraryHealthIssue[] issues = LibraryAnalyzerGridInteraction.SelectedItems<LibraryHealthIssue>(_healthGrid);
            LibraryGeneralFileSnapshot[] files = SelectedHealthFiles();
            bool suggested = issues.Any(item => item.FileId.HasValue && (item.SuggestedReanalysis != LibraryReanalysisWork.None || item.SuggestedIntegrityScrub.HasValue));
            LibraryAnalyzerGridInteraction.SetMenuState(_healthMenu, "Execute", suggested || issues.Any(item => item.Kind == LibraryHealthIssueKind.RestorableQuarantine));
            LibraryAnalyzerGridInteraction.SetMenuState(_healthMenu, "Reanalyze", suggested);
            LibraryAnalyzerGridInteraction.SetMenuState(_healthMenu, "Integrity", issues.Any(item => item.FileId.HasValue && item.SuggestedIntegrityScrub.HasValue));
            LibraryAnalyzerGridInteraction.SetMenuState(_healthMenu, "Restore", issues.Length == 1 && issues[0].Kind == LibraryHealthIssueKind.RestorableQuarantine);
            UpdateFileBackedMenu(_healthMenu, files.Select(item => (item.FileId, item.FullPath)).ToArray());
            LibraryAnalyzerGridInteraction.SetMenuState(_healthMenu, "Locate", files.Length == 1);
        };
        LibraryAnalyzerGridInteraction.AttachContextMenu(_healthGrid, _healthMenu);

        LibraryAnalyzerGridInteraction.AddMenuItem(_historyMenu, "Undo selected decision", "Undo", () => { UndoDecision_Click(null, EventArgs.Empty); return Task.CompletedTask; });
        _historyMenu.Opening += (_, _) =>
        {
            LibraryDecisionEvent? item = LibraryAnalyzerGridInteraction.SelectedItems<LibraryDecisionEvent>(_historyGrid).SingleOrDefault();
            LibraryAnalyzerGridInteraction.SetMenuState(_historyMenu, "Undo", item?.CanUndo == true && item.Source != "restored-history");
        };
        LibraryAnalyzerGridInteraction.AttachContextMenu(_historyGrid, _historyMenu);
    }

    private void ConfigureRecommendationsContextMenu()
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(_recommendationsMenu, "Open relevant results", "OpenResults", async () =>
        {
            LibraryCleanupRecommendationCategory? category = LibraryAnalyzerGridInteraction.SelectedItems<LibraryCleanupRecommendationCategory>(_recommendationsGrid).SingleOrDefault();
            if (category == null) return;
            string tab = category.Name.Contains("Exact", StringComparison.OrdinalIgnoreCase) ? "Duplicates — Exact" :
                category.Name.Contains("famil", StringComparison.OrdinalIgnoreCase) ? "Duplicates — Families" : "Duplicates — Visual";
            _tabs.SelectedTab = _tabs.TabPages.Cast<TabPage>().First(page => page.Text == tab);
            if (tab == "Duplicates — Exact") await RefreshDuplicateGroupsAsync();
            else if (tab == "Duplicates — Families") await RefreshVisualFamiliesAsync();
            else await RefreshVisualGroupsAsync();
        });
        LibraryAnalyzerGridInteraction.AddMenuItem(_recommendationsMenu, "Open Storage Optimization", "Reclamation", () =>
        {
            _tabs.SelectedTab = _tabs.TabPages.Cast<TabPage>().First(page => page.Text == "Storage Optimization");
            return Task.CompletedTask;
        });
        _recommendationsMenu.Opening += (_, _) =>
        {
            bool one = LibraryAnalyzerGridInteraction.SelectedItems<LibraryCleanupRecommendationCategory>(_recommendationsGrid).Length == 1;
            LibraryAnalyzerGridInteraction.SetMenuState(_recommendationsMenu, "OpenResults", one);
            LibraryAnalyzerGridInteraction.SetMenuState(_recommendationsMenu, "Reclamation", one);
        };
        LibraryAnalyzerGridInteraction.AttachContextMenu(_recommendationsGrid, _recommendationsMenu);
    }

    private void ConfigurePoliciesContextMenu()
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(_policiesMenu, "Play Video", "Play", () => { if (SelectedPolicies().SingleOrDefault() is { } item) PlayLibraryVideo(item.FullPath); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_policiesMenu, "Open Containing Folder", "Folder", () => { OpenContainingFolders(SelectedPolicies().Select(item => item.FullPath)); return Task.CompletedTask; });
        AddCopySubmenu(_policiesMenu, () => SelectedPolicies().Select(item => (item.FileId, item.FullPath)));
        _policiesMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_policiesMenu, "Add selected eligible candidates to Encode queue", "Encode", () => { AddOptimizationSelectionToQueue_Click(null, EventArgs.Empty); return Task.CompletedTask; });
        AddReanalysisMenuItems(_policiesMenu, () => SelectedPolicies().Select(item => item.FileId));
        LibraryAnalyzerGridInteraction.AddMenuItem(_policiesMenu, "Locate in Files", "Locate", async () => { if (SelectedPolicies().SingleOrDefault() is { } item) await LocateFileInFilesAsync(item.FileId, item.FullPath); });
        _policiesMenu.Opening += (_, _) => UpdateFileBackedMenu(_policiesMenu, SelectedPolicies().Select(item => (item.FileId, item.FullPath)).ToArray(),
            canEncode: SelectedPolicies().Any(item => item.State == LibraryPolicyComplianceState.OptimizationCandidate && item.SuggestedAction == LibraryPolicySuggestedAction.Reencode));
        LibraryAnalyzerGridInteraction.AttachContextMenu(_optimizationGrid, _policiesMenu);
    }

    private void ConfigureReclamationContextMenu()
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Play Video", "Play", () => { if (SelectedReclamation().SingleOrDefault() is { } item) PlayLibraryVideo(item.SourcePath); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Open Containing Folder", "Folder", () => { OpenContainingFolders(SelectedReclamation().Select(item => item.SourcePath)); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "View Media Information", "MediaDetails", () => { if(SelectedReclamation().SingleOrDefault() is { } item)ShowMediaDetails(item.FileId,item.SourcePath);return Task.CompletedTask; });
        AddCopySubmenu(_reclamationMenu, () => SelectedReclamation().Select(item => (item.FileId, item.SourcePath)));
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Protect", "Protect", ToggleSelectedReclamationProtectionAsync);
        _reclamationMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Add to Encode Queue", "Encode", () => { QueueLibraryFiles(SelectedReclamation().Select(item=>(item.FileId,item.SourcePath)).ToArray());return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Add to Encode Queue With Recommended Preset…", "PresetEncode", QueueSelectedReclamationEncodesAsync);
        _reclamationMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Locate in Files", "LocateFiles", async () => { if (SelectedReclamation().SingleOrDefault() is { } item) await LocateFileInFilesAsync(item.FileId, item.SourcePath); });
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Locate in Exact Duplicates", "LocateExact", () => LocateReclamationDuplicateAsync("Duplicates — Exact"));
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Locate in Visual Duplicates", "LocateVisual", () => LocateReclamationDuplicateAsync("Duplicates — Visual"));
        LibraryAnalyzerGridInteraction.AddMenuItem(_reclamationMenu, "Locate in Visual Family", "LocateFamily", () => LocateReclamationDuplicateAsync("Duplicates — Families"));
        _reclamationMenu.Opening += (_, _) =>
        {
            StorageReclamationPlanItem[] items = SelectedReclamation();
            UpdateFileBackedMenu(_reclamationMenu, items.Select(item => (item.FileId, item.SourcePath)).ToArray());
            bool one = items.Length == 1;
            LibraryCrossNavigationState? navigation = one ? LibraryCrossNavigationState.For(items[0]) : null;
            LibraryGeneralFileSnapshot? state = one ? ResolveGeneralFileSnapshot(items[0].FileId, items[0].SourcePath) : null;
            LibraryAnalyzerGridInteraction.SetMenuState(_reclamationMenu, "Protect", items.Length > 0, state?.IsProtected == true ? "Unprotect" : "Protect");
            LibraryAnalyzerGridInteraction.SetMenuState(_reclamationMenu,"MediaDetails",items.Length==1);
            LibraryAnalyzerGridInteraction.SetMenuState(_reclamationMenu,"Encode",items.Any(item=>File.Exists(item.SourcePath)),items.Length>1?"Add Selected to Encode Queue":"Add to Encode Queue");
            LibraryAnalyzerGridInteraction.SetMenuState(_reclamationMenu,"PresetEncode",items.Any(item=>item.ActionCategory==StorageReclamationActionCategory.PolicyReencode&&item.PolicyQueueIntent!=null));
            LibraryAnalyzerGridInteraction.SetMenuState(_reclamationMenu, "LocateExact", navigation?.ExactDuplicates == true);
            LibraryAnalyzerGridInteraction.SetMenuState(_reclamationMenu, "LocateVisual", navigation?.VisualDuplicates == true);
            LibraryAnalyzerGridInteraction.SetMenuState(_reclamationMenu, "LocateFamily", navigation?.VisualFamily == true);
        };
        LibraryAnalyzerGridInteraction.AttachContextMenu(_reclamationGrid, _reclamationMenu);
    }

    private void ConfigureIntegrityContextMenu()
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(_integrityMenu, "Play Video", "Play", () => { if (SelectedIntegrity().SingleOrDefault() is { } item) PlayLibraryVideo(item.FullPath); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_integrityMenu, "Open Containing Folder", "Folder", () => { OpenContainingFolders(SelectedIntegrity().Select(item => item.FullPath)); return Task.CompletedTask; });
        AddCopySubmenu(_integrityMenu, () => SelectedIntegrity().Select(item => (item.FileId, item.FullPath)));
        _integrityMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_integrityMenu, "Quick Scrub selected", "Quick", () => { QueueSelectedIntegrity(LibraryIntegrityScrubType.Quick); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_integrityMenu, "Full Scrub selected…", "Full", () => { QueueSelectedIntegrity(LibraryIntegrityScrubType.Full); return Task.CompletedTask; });
        LibraryAnalyzerGridInteraction.AddMenuItem(_integrityMenu, "Retry selected remediation", "Retry", () => { RetrySelectedIntegrity(); return Task.CompletedTask; });
        AddReanalysisMenuItems(_integrityMenu, () => SelectedIntegrity().Select(item => item.FileId));
        LibraryAnalyzerGridInteraction.AddMenuItem(_integrityMenu, "Locate in Files", "Locate", async () => { if (SelectedIntegrity().SingleOrDefault() is { } item) await LocateFileInFilesAsync(item.FileId, item.FullPath); });
        AddGeneralRemovalMenu(_integrityMenu, () => SelectedIntegrity().Select(item => (item.FileId, item.FullPath)), RefreshIntegrityAsync);
        _integrityMenu.Opening += (_, _) =>
        {
            LibraryIntegrityResult[] items = SelectedIntegrity();
            UpdateFileBackedMenu(_integrityMenu, items.Select(item => (item.FileId, item.FullPath)).ToArray());
            LibraryAnalyzerGridInteraction.SetMenuState(_integrityMenu, "Quick", items.Length > 0);
            LibraryAnalyzerGridInteraction.SetMenuState(_integrityMenu, "Full", items.Length > 0);
            LibraryAnalyzerGridInteraction.SetMenuState(_integrityMenu, "Retry", items.Any(item => item.State is LibraryIntegrityResultState.Failed or LibraryIntegrityResultState.Cancelled or LibraryIntegrityResultState.Stale or LibraryIntegrityResultState.Unavailable));
        };
        LibraryAnalyzerGridInteraction.AttachContextMenu(_integrityGrid, _integrityMenu);
    }

    private void ConfigureMaintenanceContextMenu()
    {
        LibraryAnalyzerGridInteraction.AddMenuItem(_maintenanceMenu, "Run selected task now", "Run", RunSelectedMaintenanceAsync);
        LibraryAnalyzerGridInteraction.AddMenuItem(_maintenanceMenu, "Enable", "Toggle", ToggleSelectedMaintenanceAsync);
        LibraryAnalyzerGridInteraction.AddMenuItem(_maintenanceMenu, "Edit schedule…", "Edit", () => { EditSelectedMaintenance(); return Task.CompletedTask; });
        _maintenanceMenu.Items.Add(new ToolStripSeparator());
        LibraryAnalyzerGridInteraction.AddMenuItem(_maintenanceMenu, "Refresh", "Refresh", RefreshMaintenanceAsync);
        _maintenanceMenu.Opening += (_, _) =>
        {
            LibraryMaintenanceProfileView? item = SelectedMaintenance();
            LibraryAnalyzerGridInteraction.SetMenuState(_maintenanceMenu, "Run", item != null);
            LibraryAnalyzerGridInteraction.SetMenuState(_maintenanceMenu, "Toggle", item != null, item?.Profile.Enabled == true ? "Disable" : "Enable");
            LibraryAnalyzerGridInteraction.SetMenuState(_maintenanceMenu, "Edit", item != null);
        };
        LibraryAnalyzerGridInteraction.AttachContextMenu(_maintenanceGrid, _maintenanceMenu);
    }

    private LibraryPolicyEvaluationResult[] SelectedPolicies() => LibraryAnalyzerGridInteraction.SelectedItems<LibraryPolicyEvaluationResult>(_optimizationGrid);
    private StorageReclamationPlanItem[] SelectedReclamation() => LibraryAnalyzerGridInteraction.SelectedItems<StorageReclamationPlanItem>(_reclamationGrid);
    private LibraryIntegrityResult[] SelectedIntegrity() => LibraryAnalyzerGridInteraction.SelectedItems<LibraryIntegrityResult>(_integrityGrid);
    private LibraryGeneralFileSnapshot[] SelectedHealthFiles() => LibraryAnalyzerGridInteraction.SelectedItems<LibraryHealthIssue>(_healthGrid)
        .Where(item => item.FileId.HasValue).Select(item => ResolveGeneralFileSnapshot(item.FileId!.Value, "")).OfType<LibraryGeneralFileSnapshot>().ToArray();

    private void AddCopySubmenu(ContextMenuStrip menu, Func<IEnumerable<(long FileId, string FullPath)>> selection)
    {
        var copy = new ToolStripMenuItem("Copy") { Name = "Copy" };
        copy.DropDownItems.Add(CreateCopyItem("Filename", "CopyFilename", () => CopyText(selection().Select(item => Path.GetFileName(item.FullPath)))));
        copy.DropDownItems.Add(CreateCopyItem("Full Path", "CopyFullPath", () => CopyText(selection().Select(item => item.FullPath))));
        copy.DropDownItems.Add(CreateCopyItem("Folder Path", "CopyFolderPath", () => CopyText(selection().Select(item => Path.GetDirectoryName(item.FullPath) ?? ""))));
        copy.DropDownItems.Add(CreateCopyItem("Media Details", "CopyDetails", () =>
        {
            (long FileId, string FullPath)[] selected = selection().ToArray();
            if (selected.Length == 1) CopyText(new[] { BuildMediaDetailsText(selected[0].FileId, selected[0].FullPath) });
        }));
        menu.Items.Add(copy);
        LibraryAnalyzerGridInteraction.AddMenuItem(menu, "Media Details…", "MediaDetails", () =>
        {
            (long FileId, string FullPath)[] selected = selection().ToArray();
            if (selected.Length == 1) ShowMediaDetails(selected[0].FileId, selected[0].FullPath);
            return Task.CompletedTask;
        });
    }

    private static ToolStripMenuItem CreateCopyItem(string text, string name, Action action)
    {
        var item = new ToolStripMenuItem(text) { Name = name };
        item.Click += (_, _) => action();
        return item;
    }

    private void UpdateFileBackedMenu(ContextMenuStrip menu, (long FileId, string FullPath)[] selected, bool canEncode = false)
    {
        bool any = selected.Length > 0;
        bool singleAvailable = selected.Length == 1 && File.Exists(selected[0].FullPath);
        bool folders = any && selected.All(item => Directory.Exists(Path.GetDirectoryName(item.FullPath)));
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Play", singleAvailable);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Folder", folders);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Copy", any);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "MediaDetails", selected.Length == 1);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "CopyDetails", selected.Length == 1);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Locate", selected.Length == 1);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "LocateFiles", selected.Length == 1);
        LibraryAnalyzerGridInteraction.SetMenuState(menu, "Encode", canEncode);
        foreach (string name in new[] { "Metadata", "Exact", "Visual", "All" }) LibraryAnalyzerGridInteraction.SetMenuState(menu, name, any);
        foreach (string name in new[] { "RemoveRecycle", "RemoveQuarantine", "RemovePermanent" }) LibraryAnalyzerGridInteraction.SetMenuState(menu, name, any);
    }

    private void AddGeneralRemovalMenu(ContextMenuStrip menu, Func<IEnumerable<(long FileId, string FullPath)>> selection, Func<Task> refresh)
    {
        var remove = new ToolStripMenuItem("Remove file(s)") { Name = "GeneralRemove" };
        if (_cleanupOptions.AllowRecycleBin) remove.DropDownItems.Add(RemovalItem("Move to Recycle Bin…", "RemoveRecycle", DuplicateCleanupAction.RecycleBin));
        if (_cleanupOptions.AllowQuarantine) remove.DropDownItems.Add(RemovalItem("Move to Quarantine…", "RemoveQuarantine", DuplicateCleanupAction.Quarantine));
        if (_cleanupOptions.AllowPermanentDelete) remove.DropDownItems.Add(RemovalItem("Permanently Delete…", "RemovePermanent", DuplicateCleanupAction.PermanentDelete));
        if (remove.DropDownItems.Count > 0) { menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(remove); }
        ToolStripMenuItem RemovalItem(string text, string name, DuplicateCleanupAction action)
        {
            var item = new ToolStripMenuItem(text) { Name = name };
            item.Click += async (_, _) => await PreviewAndRemoveGeneralFilesAsync(selection().ToArray(), action, refresh);
            return item;
        }
    }

    private async Task PreviewAndRemoveGeneralFilesAsync((long FileId, string FullPath)[] selected, DuplicateCleanupAction action, Func<Task> refresh)
    {
        LibraryGeneralFileRemovalPreview preview = await Task.Run(() => _generalFileRemoval.Preview(selected, action));
        string locations = preview.AffectedLocations.Count == 0 ? "None" : string.Join(Environment.NewLine, preview.AffectedLocations.Take(10));
        string message = $"General file removal preview\r\n\r\nSelected files: {selected.Length:N0}\r\nEligible files: {preview.Eligible.Count:N0}\r\n" +
            $"Total selected size: {FormatBytes(preview.SelectedBytes)}\r\nAffected locations: {preview.AffectedLocations.Count:N0}\r\n{locations}\r\n\r\n" +
            $"Protected files excluded: {preview.ProtectedExcluded:N0}\r\nUnavailable/missing files excluded: {preview.UnavailableExcluded:N0}\r\n" +
            $"Expected reclaimable space: {FormatBytes(preview.ExpectedReclaimableBytes)}\r\nRequested action: {CleanupActionLabel(action)}\r\n\r\n" +
            "Eligible files will be revalidated immediately before action. The affected locations must be rescanned afterward.";
        if (preview.Eligible.Count == 0) { MessageBox.Show(this, message, "Library Analyzer File Removal", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (action == DuplicateCleanupAction.PermanentDelete)
            message = "WARNING: PERMANENT DELETE CANNOT BE UNDONE.\r\n\r\n" + message;
        if (MessageBox.Show(this, message + "\r\n\r\nContinue?", "Library Analyzer File Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        LibraryGeneralFileRemovalResult result = await _generalFileRemoval.ExecuteAsync(preview, _cleanupOptions.QuarantineFolder);
        MessageBox.Show(this, $"File removal finished.\r\n\r\nSucceeded: {result.Succeeded:N0}\r\nExcluded by revalidation: {result.Excluded:N0}\r\nFailed: {result.Failed:N0}\r\nReclaimed: {FormatBytes(result.ReclaimedBytes)}\r\n\r\nRescan affected locations to reconcile the catalog.",
            "Library Analyzer File Removal", MessageBoxButtons.OK, result.Failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        await refresh();
    }

    private LibraryGeneralFileSnapshot? ResolveGeneralFileSnapshot(long fileId, string path)
    {
        IndexedFileRecord? indexed = string.IsNullOrWhiteSpace(path) ? FindIndexedFile(fileId) : _runtime.Catalog.GetFileByPath(path);
        if (indexed == null || indexed.Id != fileId) return null;
        LibraryFileViewRecord? view = _runtime.Catalog.QueryFiles(new LibraryFileQuery(Search: indexed.FullPath, Limit: 20)).Files.FirstOrDefault(item => item.FileId == fileId);
        return new(indexed.Id, indexed.FullPath, view?.LocationPath ?? "", indexed.SizeBytes, indexed.LastWriteTimeUtc,
            indexed.VolumeId, indexed.FileIdentity, indexed.Availability, view?.IsProtected ?? false, indexed.CreationTimeUtc);
    }

    private IndexedFileRecord? FindIndexedFile(long fileId)
    {
        foreach (LibraryFileMembershipRecord membership in _runtime.Catalog.GetMembershipsForFile(fileId))
        {
            long after = 0;
            while (true)
            {
                IReadOnlyList<IndexedFileRecord> page = _runtime.Catalog.GetLocationFilesPage(membership.LocationId, after, 1000);
                IndexedFileRecord? found = page.FirstOrDefault(item => item.Id == fileId);
                if (found != null) return found;
                if (page.Count == 0 || page[^1].Id >= fileId) break;
                after = page[^1].Id;
            }
        }
        return null;
    }

    private string BuildMediaDetailsText(long fileId, string path)
    {
        LibraryGeneralFileSnapshot? snapshot = ResolveGeneralFileSnapshot(fileId, path);
        if (snapshot == null) return $"Path: {path}\r\nCatalog record is unavailable.";
        LibraryFileViewRecord? view = _runtime.Catalog.QueryFiles(new LibraryFileQuery(Search: snapshot.FullPath, Limit: 20)).Files.FirstOrDefault(item => item.FileId == fileId);
        LibraryFileHashFact? hash = _runtime.AnalysisCatalog.GetFileHashFact(fileId);
        VisualFingerprintFact? fingerprint = _runtime.VisualCatalog.GetVisualFingerprint(fileId);
        var text = new StringBuilder();
        text.AppendLine($"Filename: {Path.GetFileName(snapshot.FullPath)}").AppendLine($"Full path: {snapshot.FullPath}")
            .AppendLine($"Folder: {Path.GetDirectoryName(snapshot.FullPath)}").AppendLine($"Location: {snapshot.LocationPath}")
            .AppendLine($"Size: {FormatBytes(snapshot.SizeBytes)} ({snapshot.SizeBytes:N0} bytes)")
            .AppendLine($"Created: {(snapshot.CreationUtc.HasValue ? snapshot.CreationUtc.Value.ToLocalTime().ToString("G") : "Unknown")}")
            .AppendLine($"Modified: {snapshot.LastWriteUtc.ToLocalTime():G}")
            .AppendLine($"Availability: {snapshot.Availability}").AppendLine($"Protected: {(snapshot.IsProtected ? "Yes" : "No")}");
        if (view != null) text.AppendLine($"Container: {view.FormatName}").AppendLine($"Video codec: {view.VideoCodec}")
            .AppendLine($"Resolution: {(view.Width.HasValue ? $"{view.Width}×{view.Height}" : "Unknown")}")
            .AppendLine($"Bitrate: {(view.TotalBitRate.HasValue ? FormatBytes(view.TotalBitRate.Value) + "/s" : "Unknown")}")
            .AppendLine($"Duration: {(view.DurationSeconds.HasValue ? TimeSpan.FromSeconds(view.DurationSeconds.Value).ToString("g") : "Unknown")}")
            .AppendLine($"Probe state: {view.ProbeStatus}{(string.IsNullOrWhiteSpace(view.ProbeError) ? "" : " — " + view.ProbeError)}");
        text.AppendLine($"Exact hash: {(hash?.FullHash is { Length: > 0 } ? "Available" : hash?.QuickHash is { Length: > 0 } ? "Quick hash only" : "Not analyzed")}")
            .AppendLine($"Visual fingerprint: {(fingerprint is { Status: VisualFingerprintStatus.Succeeded, FrameHashes.Count: > 0 } ? "Available" : fingerprint?.Status.ToString() ?? "Not analyzed")}");
        return text.ToString().TrimEnd();
    }

    private void ShowMediaDetails(long fileId, string path)
    {
        using var dialog = new MediaFluxForm { Text = "Media Details", Size = new Size(720, 620), MinimumSize = new Size(540, 420), StartPosition = FormStartPosition.CenterParent };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), Text = BuildMediaDetailsText(fileId, path) };
        var close = new Button { Text = "Close", Dock = DockStyle.Bottom, Height = 36 };
        close.Click += (_, _) => dialog.Close(); dialog.Controls.Add(box); dialog.Controls.Add(close); dialog.AcceptButton = close; dialog.ShowDialog(this);
    }

    private void CopyText(IEnumerable<string> values)
    {
        string text = string.Join(Environment.NewLine, values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { ShowError("The selected information could not be copied.", ex); }
    }

    private async Task ToggleSelectedReclamationProtectionAsync()
    {
        StorageReclamationPlanItem[] items = SelectedReclamation();
        bool protect = items.Any(item => ResolveGeneralFileSnapshot(item.FileId, item.SourcePath)?.IsProtected != true);
        await Task.Run(() => { foreach (StorageReclamationPlanItem item in items) _runtime.AnalysisCatalog.SetFileProtection(item.FileId, protect, protect ? "Protected in Storage Optimization" : ""); });
        RenderStorageReclamationPage();
    }

    private async Task LocateReclamationDuplicateAsync(string tab)
    {
        StorageReclamationPlanItem? item = SelectedReclamation().SingleOrDefault();
        if (item == null) return;
        _tabs.SelectedTab = _tabs.TabPages.Cast<TabPage>().First(page => page.Text == tab);
        if (tab == "Duplicates — Exact") { _duplicateSearch.Text = item.SourcePath; await RefreshDuplicateGroupsAsync(); }
        else if (tab == "Duplicates — Visual") { _visualSearch.Text = item.SourcePath; await RefreshVisualGroupsAsync(item.VisualGroupId); }
        else
        {
            _familyShowIgnored.Checked = true; await RefreshVisualFamiliesAsync();
            DataGridViewRow? row = _familyGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(value => (value.Tag as VisualFamilyRecord)?.FamilyId == item.VisualFamilyId);
            if (row != null) { _familyGrid.ClearSelection(); row.Selected = true; _familyGrid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible); }
        }
    }
}
