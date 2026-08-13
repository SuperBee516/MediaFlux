using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using System.Text.Json;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly NumericUpDown _reclamationTarget = new() { Minimum = 0, Maximum = 1_000_000, DecimalPlaces = 1, Increment = 10, Value = 100, Width = 120 };
    private readonly ComboBox _reclamationUnit = DropDown();
    private readonly ComboBox _reclamationStrategy = DropDown();
    private readonly ComboBox _reclamationPolicy = DropDown();
    private readonly DataGridView _reclamationGrid = CreateGrid();
    private readonly Label _reclamationSummary = new() { Dock = DockStyle.Bottom, Height = 72, AutoEllipsis = true, Padding = new Padding(8, 4, 0, 0) };
    private readonly Label _reclamationStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 7, 0, 0) };
    private readonly Label _reclamationPageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    private readonly StorageReclamationPlannerService _reclamationPlanner = new();
    private StorageReclamationPlanStore? _reclamationStore;
    private StorageReclamationPlan? _reclamationPlan;
    private CancellationTokenSource? _reclamationBuildCancellation;
    private int _reclamationPage;

    private void BuildStorageReclamationTab()
    {
        _reclamationStore = _reviewOptions.ReclamationPlanStore ?? new StorageReclamationPlanStore(AppPaths.StorageReclamationPlanFile);
        var tab = new TabPage("Storage Reclamation") { Padding = new Padding(10) };
        var intro = new Label
        {
            Name = "StorageReclamationIntro", Dock = DockStyle.Top, Height = 46, Padding = new Padding(4),
            ForeColor = LibraryAnalyzerAccentColor,
            Text = "Build an explainable, non-overlapping plan to reach a storage goal. Planning and selection are advisory; cleanup and encoding require separate explicit handoffs through existing safety workflows."
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, WrapContents = true };
        actions.Controls.Add(new Label { Text = "Reclaim:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        actions.Controls.Add(_reclamationTarget);
        _reclamationUnit.Width = 70; _reclamationUnit.Items.AddRange(new object[] { "GB", "TB" }); _reclamationUnit.SelectedIndex = 0;
        actions.Controls.Add(_reclamationUnit);
        actions.Controls.Add(new Label { Text = "Strategy:", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        _reclamationStrategy.Width = 170;
        _reclamationStrategy.Items.AddRange(Enum.GetValues<StorageReclamationStrategy>().Select(value => new ReclamationStrategyChoice(value)).Cast<object>().ToArray());
        _reclamationStrategy.SelectedItem = _reclamationStrategy.Items.Cast<ReclamationStrategyChoice>().Single(item => item.Value == StorageReclamationStrategy.SafestFirst);
        actions.Controls.Add(_reclamationStrategy);
        actions.Controls.Add(new Label { Text = "Policy:", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        _reclamationPolicy.Width = 220;
        actions.Controls.Add(_reclamationPolicy);
        AddButton(actions, "Build Plan", async (_, _) => await BuildStorageReclamationPlanAsync());
        AddButton(actions, "Cancel", (_, _) => _reclamationBuildCancellation?.Cancel());
        AddButton(actions, "Previous", (_, _) => { if (_reclamationPage > 0) { _reclamationPage--; RenderStorageReclamationPage(); } });
        AddButton(actions, "Next", (_, _) => { if (_reclamationPlan != null && (_reclamationPage + 1L) * PageSize < _reclamationPlan.Items.Count) { _reclamationPage++; RenderStorageReclamationPage(); } });
        actions.Controls.Add(_reclamationPageLabel);
        AddButton(actions, "Preview selected cleanup…", async (_, _) => await PreviewSelectedReclamationCleanupAsync());
        AddButton(actions, "Send selected encodes to queue", async (_, _) => await QueueSelectedReclamationEncodesAsync());
        AddButton(actions, "View breakdown…", (_, _) => ShowReclamationBreakdown());

        _reclamationGrid.Name = "StorageReclamationGrid";
        _reclamationGrid.ReadOnly = false;
        _reclamationGrid.MultiSelect = false;
        _reclamationGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Include", Width = 58 });
        AddReadOnlyColumn("Action", "Action", 160);
        AddReadOnlyColumn("Safety", "Safety", 105);
        AddReadOnlyColumn("File", "File", 220);
        AddReadOnlyColumn("CurrentSize", "Current size", 95);
        AddReadOnlyColumn("Reclaim", "Projected reclaim", 110);
        AddReadOnlyColumn("Confidence", "Confidence", 85);
        AddReadOnlyColumn("Runtime", "Encode time", 100);
        AddReadOnlyColumn("Efficiency", "Savings / hour", 110);
        AddReadOnlyColumn("RuntimeConfidence", "Runtime confidence", 115);
        AddReadOnlyColumn("Keeper", "Keeper / target", 260);
        AddReadOnlyColumn("Reason", "Reason", 360, true);
        AddReadOnlyColumn("Source", "Source", 110);
        AddReadOnlyColumn("Path", "Path", 320);
        _reclamationGrid.CurrentCellDirtyStateChanged += (_, _) => { if (_reclamationGrid.IsCurrentCellDirty) _reclamationGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _reclamationGrid.CellValueChanged += ReclamationGrid_CellValueChanged;

        tab.Controls.Add(_reclamationGrid);
        tab.Controls.Add(_reclamationStatus);
        tab.Controls.Add(_reclamationSummary);
        tab.Controls.Add(intro);
        tab.Controls.Add(actions);
        _tabs.TabPages.Add(tab);
        ReloadReclamationPolicies();
        _reclamationPlan = _reclamationStore.Load();
        RenderStorageReclamationPage();

        void AddReadOnlyColumn(string name, string header, int width, bool fill = false)
        {
            var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true };
            if (fill) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _reclamationGrid.Columns.Add(column);
        }
    }

    private void ReloadReclamationPolicies()
    {
        _reclamationPolicy.Items.Clear();
        _reclamationPolicy.Items.Add(new ReclamationPolicyChoice(null));
        LibraryPolicyStore store = _reviewOptions.PolicyStore ?? new LibraryPolicyStore(AppPaths.LibraryPolicyFile);
        foreach (LibraryPolicyDefinition policy in store.LoadAll()) _reclamationPolicy.Items.Add(new ReclamationPolicyChoice(policy));
        _reclamationPolicy.SelectedIndex = 0;
    }

    private async Task BuildStorageReclamationPlanAsync()
    {
        _reclamationBuildCancellation?.Cancel();
        _reclamationBuildCancellation?.Dispose();
        _reclamationBuildCancellation = new CancellationTokenSource();
        CancellationToken token = _reclamationBuildCancellation.Token;
        try
        {
            long requested = ReclamationTargetBytes();
            StorageReclamationStrategy strategy = (_reclamationStrategy.SelectedItem as ReclamationStrategyChoice)?.Value ?? StorageReclamationStrategy.SafestFirst;
            LibraryPolicyDefinition? policy = (_reclamationPolicy.SelectedItem as ReclamationPolicyChoice)?.Policy;
            LibraryPolicyCapabilitySnapshot capabilities = _reviewOptions.PolicyCapabilities ?? new LibraryPolicyCapabilitySnapshot();
            _reclamationStatus.Text = "Building a bounded, non-overlapping plan from current catalog evidence…";
            IReadOnlyList<StorageReclamationOpportunity> opportunities = await Task.Run(() =>
                _runtime.ReclamationOpportunities.Collect(strategy, policy, capabilities, token,
                    runtimeEstimator: _reviewOptions.RuntimeEstimator), token);
            StorageReclamationPlan plan = await Task.Run(() => _reclamationPlanner.BuildPlan(
                requested, strategy, opportunities, CurrentReclamationRevision(policy, capabilities), policy?.Id ?? ""), token);
            if (IsDisposed) return;
            _reclamationPlan = plan;
            _reclamationPage = 0;
            _reclamationStore?.Save(plan);
            RenderStorageReclamationPage();
            _reclamationStatus.Text = $"Plan {plan.PlanId[..8]} built from current catalog evidence. No file action was performed.";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) _reclamationStatus.Text = "Plan generation canceled. No files or decisions were changed.";
        }
        catch (Exception ex) { if (!IsDisposed) ShowError("The reclamation plan could not be built. No files or decisions were changed.", ex); }
        finally { _reclamationBuildCancellation?.Dispose(); _reclamationBuildCancellation = null; }
    }

    private void RenderStorageReclamationPage()
    {
        _reclamationGrid.Rows.Clear();
        if (_reclamationPlan == null)
        {
            _reclamationSummary.Text = "No saved plan. Enter a target and build a plan.";
            _reclamationPageLabel.Text = "";
            return;
        }
        StorageReclamationPlanItem[] page = _reclamationPlan.Items.Skip(_reclamationPage * PageSize).Take(PageSize).ToArray();
        foreach (StorageReclamationPlanItem item in page)
        {
            string keeper = item.ActionCategory == StorageReclamationActionCategory.PolicyReencode
                ? item.PolicyQueueIntent == null ? item.PolicyName : $"{item.PolicyQueueIntent.ProposedCodec} / {item.PolicyQueueIntent.TargetContainer}"
                : item.KeeperPath;
            int index = _reclamationGrid.Rows.Add(item.Included, ReclamationActionLabel(item.ActionCategory), ReclamationSafetyLabel(item.SafetyState),
                Path.GetFileName(item.SourcePath), File.Exists(item.SourcePath) ? FormatBytes(new FileInfo(item.SourcePath).Length) : "Unavailable",
                FormatBytes(item.ExpectedReclaimBytes), item.Confidence, FormatRuntimeHours(item.EstimatedProcessingHours),
                FormatEfficiency(item.SavingsPerComputeHourGb), item.RuntimeConfidence == RuntimeEstimateConfidence.Unknown ? "Unknown" : item.RuntimeConfidence,
                keeper, item.Reason, item.SourceSubsystem, item.SourcePath);
            _reclamationGrid.Rows[index].Tag = item;
            _reclamationGrid.Rows[index].Cells["Include"].ReadOnly = item.SafetyState != StorageReclamationSafetyState.Ready;
            if (item.SafetyState != StorageReclamationSafetyState.Ready) _reclamationGrid.Rows[index].DefaultCellStyle.ForeColor = Color.DarkOrange;
        }
        long first = _reclamationPlan.Items.Count == 0 ? 0 : (long)_reclamationPage * PageSize + 1;
        long last = Math.Min(_reclamationPlan.Items.Count, (long)(_reclamationPage + 1) * PageSize);
        _reclamationPageLabel.Text = _reclamationPlan.Items.Count == 0 ? "No items" : $"{first:N0}–{last:N0} of {_reclamationPlan.Items.Count:N0}";
        UpdateReclamationSummary();
        RefreshStorageReclamationStaleness();
    }

    private void ShowReclamationBreakdown()
    {
        if (_reclamationPlan == null) return;
        string categories = string.Join(Environment.NewLine, _reclamationPlan.CategoryTotals.Select(item =>
            $"{ReclamationActionLabel(item.Category)}: {item.ItemCount:N0} items · {FormatBytes(item.ReadyBytes)} ready · {FormatBytes(item.ReviewDependentBytes)} review"));
        string locations = string.Join(Environment.NewLine, _reclamationPlan.LocationTotals.Take(25).Select(item =>
            $"{item.LocationPath}: {item.ItemCount:N0} items · {FormatBytes(item.ReadyBytes)} ready · {FormatBytes(item.ReviewDependentBytes)} review"));
        if (_reclamationPlan.LocationTotals.Count > 25) locations += $"{Environment.NewLine}…and {_reclamationPlan.LocationTotals.Count - 25:N0} more locations.";
        MessageBox.Show(this, $"Category breakdown\r\n\r\n{categories}\r\n\r\nLocation breakdown\r\n\r\n{locations}",
            "Storage Reclamation Breakdown", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ReclamationGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_reclamationPlan == null || e.RowIndex < 0 || e.ColumnIndex != _reclamationGrid.Columns["Include"].Index) return;
        var changes = _reclamationPlan.Items.ToDictionary(item => item.ItemId, item => item.Included, StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _reclamationGrid.Rows)
            if (!row.IsNewRow && row.Tag is StorageReclamationPlanItem item)
                changes[item.ItemId] = Convert.ToBoolean(row.Cells["Include"].Value ?? false) && item.SafetyState == StorageReclamationSafetyState.Ready;
        _reclamationPlan = StorageReclamationPlannerService.Reaccount(_reclamationPlan with
        {
            Items = _reclamationPlan.Items.Select(item => item with { Included = changes[item.ItemId] }).ToArray()
        });
        _reclamationStore?.Save(_reclamationPlan);
        UpdateReclamationSummary();
    }

    private void UpdateReclamationSummary()
    {
        if (_reclamationPlan == null) return;
        _reclamationSummary.Text =
            $"Requested {FormatBytes(_reclamationPlan.RequestedReclaimBytes)} · " +
            $"Projected reclaim {FormatBytes(_reclamationPlan.ProjectedReclaimBytes)} · " +
            $"Ready reclaim {FormatBytes(_reclamationPlan.ReadyReclaimBytes)} · " +
            $"Actually reclaimed {FormatBytes(_reclamationPlan.ActuallyReclaimedBytes)} · " +
            $"Shortfall {FormatBytes(_reclamationPlan.ShortfallBytes)} · " +
            $"Encode time {FormatRuntimeHours(_reclamationPlan.ProjectedReencodeHours)} · " +
            $"Runtime confidence: {_reclamationPlan.UnknownRuntimeCandidateCount:N0} unknown · " +
            $"Efficiency {FormatEfficiency(_reclamationPlan.SavingsPerComputeHourGb)}";
    }

    private async Task PreviewSelectedReclamationCleanupAsync()
    {
        if (_reclamationPlan == null) return;
        StorageReclamationPlanItem[] selected = _reclamationPlan.Items.Where(item => item.Included && item.IsCurrentlyExecutable &&
            item.ActionCategory is StorageReclamationActionCategory.ExactDuplicateCleanup or StorageReclamationActionCategory.ReviewedVisualFamilyCleanup or StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup).ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "No ready cleanup items are included in this plan.", "Storage Reclamation", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string warning = $"Selected cleanup preview\r\n\r\n{selected.Length:N0} files · {FormatBytes(selected.Sum(item => item.ExpectedReclaimBytes))} projected\r\n\r\n" +
                         "Current evidence will be rebuilt and the existing exact/visual cleanup services will revalidate every item. Changed, protected, missing, stale, or keeper-mismatched items will be excluded.\r\n\r\nContinue to final cleanup confirmation?";
        if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete)
            warning = "WARNING: THE CONFIGURED CLEANUP ACTION IS PERMANENT DELETE.\r\n\r\n" + warning;
        warning = warning.Replace("Continue to final cleanup confirmation?", $"Execute the currently valid selected items using {CleanupActionLabel(_cleanupOptions.PreferredAction)}?");
        if (MessageBox.Show(this, warning, "Storage Reclamation Cleanup Preview", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await ExecuteSelectedReclamationCleanupAsync(selected);
    }

    private async Task ExecuteSelectedReclamationCleanupAsync(IReadOnlyList<StorageReclamationPlanItem> selected)
    {
        string quarantine = _cleanupOptions.QuarantineFolder;
        if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.Quarantine && !Directory.Exists(quarantine))
        {
            MessageBox.Show(this, "The configured quarantine folder is unavailable. No files were changed.", "Storage Reclamation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        try
        {
            int succeeded = 0, excluded = 0, failed = 0;
            long reclaimed = 0;
            ExactCleanupCandidate[] exact = selected.Where(item => item.ActionCategory == StorageReclamationActionCategory.ExactDuplicateCleanup)
                .Select(item => new ExactCleanupCandidate(item.ExactGroupId!.Value, item.FileId, item.KeeperFileId!.Value, item.ExpectedReclaimBytes)).ToArray();
            if (exact.Length > 0)
            {
                HashSet<(long GroupId, long FileId)> valid = _runtime.DuplicateCleanup.GetEligibleCandidates(50_000)
                    .Select(item => (item.GroupId, item.FileId)).ToHashSet();
                ExactCleanupCandidate[] currentExact = exact.Where(item => valid.Contains((item.GroupId, item.FileId))).ToArray();
                excluded += exact.Length - currentExact.Length;
                if (currentExact.Length > 0)
                {
                    DuplicateCleanupPlanSummary plan = await Task.Run(() => _runtime.DuplicateCleanup.CreatePlanForCandidates(currentExact, _cleanupOptions.PreferredAction, quarantine));
                    DuplicateCleanupExecutionResult result = await _runtime.DuplicateCleanup.ExecutePlanAsync(plan.PlanId);
                    succeeded += result.Succeeded; excluded += result.Excluded; failed += result.Failed; reclaimed += result.ReclaimedBytes;
                }
            }
            VisualCleanupProposalItem[] family = RebuildVisualSelections(selected, family: true);
            excluded += selected.Count(item => item.ActionCategory == StorageReclamationActionCategory.ReviewedVisualFamilyCleanup) - family.Length;
            if (family.Length > 0)
            {
                VisualCleanupPlanRecord plan = await Task.Run(() => _runtime.VisualDuplicateCleanup.CreatePlan(family, _cleanupOptions.PreferredAction, quarantine, allowUnreviewed: true, minimumConfidence: 0));
                DuplicateCleanupExecutionResult result = await _runtime.VisualDuplicateCleanup.ExecutePlanAsync(plan.PlanId);
                succeeded += result.Succeeded; excluded += result.Excluded; failed += result.Failed; reclaimed += result.ReclaimedBytes;
            }
            VisualCleanupProposalItem[] pairs = RebuildVisualSelections(selected, family: false);
            excluded += selected.Count(item => item.ActionCategory == StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup) - pairs.Length;
            if (pairs.Length > 0)
            {
                VisualCleanupPlanRecord plan = await Task.Run(() => _runtime.VisualDuplicateCleanup.CreatePlan(pairs, _cleanupOptions.PreferredAction, quarantine));
                DuplicateCleanupExecutionResult result = await _runtime.VisualDuplicateCleanup.ExecutePlanAsync(plan.PlanId);
                succeeded += result.Succeeded; excluded += result.Excluded; failed += result.Failed; reclaimed += result.ReclaimedBytes;
            }
            if (_reclamationPlan is { } planToUpdate)
            {
                _reclamationPlan = StorageReclamationPlannerService.RecordActuallyReclaimed(planToUpdate, reclaimed);
                _reclamationStore?.Save(_reclamationPlan);
                UpdateReclamationSummary();
            }
            MessageBox.Show(this, $"Cleanup handoff finished.\r\n\r\nSucceeded: {succeeded:N0}\r\nExcluded by revalidation: {excluded:N0}\r\nFailed: {failed:N0}\r\nActually reclaimed: {FormatBytes(reclaimed)}\r\n\r\nRescan affected locations before rebuilding the plan.",
                "Storage Reclamation", MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            _runtime.PolicyEvaluation.Invalidate();
            RefreshStorageReclamationStaleness();
        }
        catch (Exception ex) { ShowError("The selected cleanup handoff could not be completed. Existing cleanup safeguards remained authoritative.", ex); }
    }

    private VisualCleanupProposalItem[] RebuildVisualSelections(IReadOnlyList<StorageReclamationPlanItem> selected, bool family)
    {
        var result = new List<VisualCleanupProposalItem>();
        StorageReclamationPlanItem[] requested = selected.Where(item => family
            ? item.ActionCategory == StorageReclamationActionCategory.ReviewedVisualFamilyCleanup
            : item.ActionCategory == StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup).ToArray();
        if (family)
        {
            foreach (IGrouping<long, StorageReclamationPlanItem> group in requested.Where(item => item.VisualFamilyId.HasValue).GroupBy(item => item.VisualFamilyId!.Value))
            {
                VisualFamilyCleanupProposal current = _runtime.VisualFamilies.BuildCleanupProposal(group.Key);
                HashSet<long> fileIds = group.Select(item => item.FileId).ToHashSet();
                result.AddRange(current.Items.Where(item => fileIds.Contains(item.Candidate.FileId)));
            }
        }
        else
        {
            long[] groupIds = requested.Where(item => item.VisualGroupId.HasValue).Select(item => item.VisualGroupId!.Value).Distinct().ToArray();
            if (groupIds.Length > 0)
            {
                VisualCleanupProposal current = _runtime.VisualDuplicateCleanup.BuildProposal(groupIds: groupIds, maximumItems: Math.Min(10_000, groupIds.Length));
                HashSet<long> fileIds = requested.Select(item => item.FileId).ToHashSet();
                result.AddRange(current.Items.Where(item => fileIds.Contains(item.Candidate.FileId)));
            }
        }
        return result.GroupBy(item => item.Candidate.FileId).Select(group => group.First()).ToArray();
    }

    private async Task QueueSelectedReclamationEncodesAsync()
    {
        if (_reclamationPlan == null || _reviewOptions.AddPolicyCandidatesToEncodeQueue == null) return;
        IReadOnlyList<StorageReclamationPlanItem> selected = StorageReclamationQueueOrdering.GetIncludedPolicyItems(_reclamationPlan);
        if (selected.Count == 0) { MessageBox.Show(this, "No ready policy re-encode items are included in this plan.", "Storage Reclamation", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        LibraryPolicyStore store = _reviewOptions.PolicyStore ?? new LibraryPolicyStore(AppPaths.LibraryPolicyFile);
        LibraryPolicyDefinition? policy = store.LoadAll().FirstOrDefault(item => item.Id.Equals(_reclamationPlan.PolicyId, StringComparison.OrdinalIgnoreCase));
        if (policy == null) { MessageBox.Show(this, "The policy used by this plan no longer exists. Rebuild the plan.", "Storage Reclamation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        LibraryPolicyCapabilitySnapshot capabilities = _reviewOptions.PolicyCapabilities ?? new LibraryPolicyCapabilitySnapshot();
        IReadOnlyList<LibraryPolicyEvaluationResult> current = await Task.Run(() => _runtime.PolicyEvaluation.EvaluateForPlanning(policy, capabilities, false));
        Dictionary<long, LibraryPolicyEvaluationResult> ready = current.Where(item => item.State == LibraryPolicyComplianceState.OptimizationCandidate && item.SuggestedAction == LibraryPolicySuggestedAction.Reencode)
            .ToDictionary(item => item.FileId);
        LibraryPolicyQueueItem[] queue = selected.Where(item => File.Exists(item.SourcePath) && ready.TryGetValue(item.FileId, out LibraryPolicyEvaluationResult? result) &&
                PolicyIntentStillMatches(item.PolicyQueueIntent!, result, policy))
            .Select(item => item.PolicyQueueIntent!).ToArray();
        int excluded = selected.Count - queue.Length;
        if (queue.Length == 0) { MessageBox.Show(this, "All selected encode opportunities changed or became unavailable. Rebuild the plan.", "Storage Reclamation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (MessageBox.Show(this, $"Add {queue.Length:N0} revalidated policy item(s) to the normal Encode queue?\r\n\r\nEncoding will not start. Global settings will not change. {excluded:N0} stale item(s) will be excluded.",
                "Storage Reclamation Encode Handoff", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await _reviewOptions.AddPolicyCandidatesToEncodeQueue(queue);
        _reclamationStatus.Text = $"Added {queue.Length:N0} revalidated policy item(s) to the normal Encode queue; encoding was not started. {excluded:N0} stale item(s) excluded.";
    }

    private void RefreshStorageReclamationStaleness()
    {
        if (_reclamationPlan == null) return;
        LibraryPolicyDefinition? policy = string.IsNullOrWhiteSpace(_reclamationPlan.PolicyId) ? null :
            (_reviewOptions.PolicyStore ?? new LibraryPolicyStore(AppPaths.LibraryPolicyFile)).LoadAll()
            .FirstOrDefault(item => item.Id.Equals(_reclamationPlan.PolicyId, StringComparison.OrdinalIgnoreCase));
        LibraryPolicyCapabilitySnapshot capabilities = _reviewOptions.PolicyCapabilities ?? new LibraryPolicyCapabilitySnapshot();
        if (!_reclamationPlan.CatalogRevision.Equals(CurrentReclamationRevision(policy, capabilities), StringComparison.Ordinal))
            _reclamationStatus.Text = "This plan may be stale because catalog, protection, duplicate, family, or review evidence changed. Rebuild before action.";
    }

    private string CurrentReclamationRevision(LibraryPolicyDefinition? policy, LibraryPolicyCapabilitySnapshot capabilities) =>
        _runtime.ReclamationRevision + "|policy=" + JsonSerializer.Serialize(policy) + "|encoders=" +
        string.Join(';', capabilities.AvailableEncoderCodecs.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value}")) + "|10bit=" +
        string.Join(';', capabilities.TenBitEncoderIds.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)) + "|presets=" +
        JsonSerializer.Serialize(capabilities.EncodingPresets.OrderBy(item => item.Key).Select(item => item.Value)) + "|runtime=" +
        (_reviewOptions.RuntimeEstimator?.GetHistoryRevision() ?? "unavailable");

    private static bool PolicyIntentStillMatches(LibraryPolicyQueueItem intent, LibraryPolicyEvaluationResult result, LibraryPolicyDefinition policy) =>
        intent.PolicyId.Equals(result.PolicyId, StringComparison.OrdinalIgnoreCase) &&
        intent.ProposedCodec == result.ProposedCodec &&
        intent.EncoderId.Equals(result.EncoderId, StringComparison.OrdinalIgnoreCase) &&
        intent.EncoderPreset.Equals(result.EncoderPreset, StringComparison.OrdinalIgnoreCase) &&
        intent.EncodingPresetName.Equals(result.EncodingPresetName, StringComparison.OrdinalIgnoreCase) &&
        intent.QualityValue == result.QualityValue && intent.PreferredBitDepth == result.PreferredBitDepth &&
        intent.PreserveSourceResolution == result.PreserveSourceResolution && intent.MaximumOutputHeight == result.MaximumOutputHeight &&
        intent.PreserveHdr == result.PreserveHdr && intent.TargetContainer == result.TargetContainer && policy.Id == result.PolicyId;

    private long ReclamationTargetBytes()
        => StorageReclamationUnits.ToBytes(_reclamationTarget.Value, _reclamationUnit.SelectedItem?.ToString() ?? "GB");

    private static string ReclamationActionLabel(StorageReclamationActionCategory category) => category switch
    {
        StorageReclamationActionCategory.ExactDuplicateCleanup => "Exact duplicate cleanup",
        StorageReclamationActionCategory.ReviewedVisualFamilyCleanup => "Reviewed visual family",
        StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup => "Reviewed visual duplicate",
        StorageReclamationActionCategory.PolicyReencode => "Policy re-encode",
        StorageReclamationActionCategory.Remux => "Remux",
        _ => "Review required"
    };
    private static string ReclamationSafetyLabel(StorageReclamationSafetyState state) => state switch
    {
        StorageReclamationSafetyState.Ready => "Ready",
        StorageReclamationSafetyState.ReviewRequired => "Review required",
        _ => "Blocked"
    };
    private sealed record ReclamationStrategyChoice(StorageReclamationStrategy Value)
    {
        public override string ToString() => Value switch
        {
            StorageReclamationStrategy.SafestFirst => "Safest First",
            StorageReclamationStrategy.AvoidReencoding => "Avoid Re-Encoding",
            StorageReclamationStrategy.IncludeReencoding => "Include Re-Encoding",
            StorageReclamationStrategy.BestSavingsEfficiency => "Best Savings Efficiency",
            _ => "Maximum Potential"
        };
    }

    private static string FormatRuntimeHours(double? hours)
    {
        if (hours is not > 0 || !double.IsFinite(hours.Value)) return "Unknown";
        TimeSpan value = TimeSpan.FromHours(hours.Value);
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{Math.Max(1, (int)Math.Round(value.TotalMinutes))} min";
    }

    private static string FormatEfficiency(double? gbPerHour)
    {
        if (gbPerHour is not > 0 || !double.IsFinite(gbPerHour.Value)) return "Unknown";
        if (gbPerHour >= 1024) return $"{gbPerHour / 1024:0.##} TB/hour";
        if (gbPerHour >= 1) return $"{gbPerHour:0.##} GB/hour";
        return $"{gbPerHour * 1024:0.##} MB/hour";
    }
    private sealed record ReclamationPolicyChoice(LibraryPolicyDefinition? Policy)
    {
        public override string ToString() => Policy == null ? "No policy" : Policy.IsBuiltIn ? $"{Policy.Name} (built-in)" : Policy.Name;
    }
}
