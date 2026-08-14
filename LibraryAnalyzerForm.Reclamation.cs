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
    private readonly ComboBox _reclamationSort = DropDown();
    private readonly DataGridView _reclamationGrid = CreateGrid();
    private readonly DataGridView _reclamationOpportunitySummary = CreateGrid();
    private readonly Label _reclamationSummary = new() { Dock = DockStyle.Bottom, Height = 72, AutoEllipsis = true, Padding = new Padding(8, 4, 0, 0) };
    private readonly Label _reclamationStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 7, 0, 0) };
    private readonly Label _reclamationPageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    private readonly StorageReclamationPlannerService _reclamationPlanner = new();
    private StorageReclamationPlanStore? _reclamationStore;
    private StorageReclamationPlan? _reclamationPlan;
    private CancellationTokenSource? _reclamationBuildCancellation;
    private int _reclamationPage;
    private StorageReclamationActionCategory? _reclamationCategoryFilter;

    private void BuildStorageReclamationTab()
    {
        _reclamationStore = _reviewOptions.ReclamationPlanStore ?? new StorageReclamationPlanStore(AppPaths.StorageReclamationPlanFile);
        var tab = new TabPage("Storage Optimization") { Padding = new Padding(10) };
        var intro = new Label
        {
            Name = "StorageReclamationIntro", Dock = DockStyle.Top, Height = 46, Padding = new Padding(4),
            ForeColor = LibraryAnalyzerAccentColor,
            Text = "Find defensible storage opportunities from current catalog evidence. Duplicate savings are exact; re-encode savings are estimates. This view never modifies source files or starts encoding."
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, WrapContents = true };
        actions.Controls.Add(new Label { Text = "Planning target:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
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
        actions.Controls.Add(new Label { Text = "Prioritize:", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        _reclamationSort.Width=165;_reclamationSort.Items.AddRange(new object[]{"Largest savings","Largest current size","File / group count","Location"});_reclamationSort.SelectedIndex=0;_reclamationSort.SelectedIndexChanged+=(_,_)=>{_reclamationPage=0;RenderStorageReclamationPage();};actions.Controls.Add(_reclamationSort);
        AddButton(actions, "Refresh Opportunities", async (_, _) => await BuildStorageReclamationPlanAsync());
        AddButton(actions, "Cancel", (_, _) => _reclamationBuildCancellation?.Cancel());
        AddButton(actions, "Previous", (_, _) => { if (_reclamationPage > 0) { _reclamationPage--; RenderStorageReclamationPage(); } });
        AddButton(actions, "Next", (_, _) => { if (_reclamationPlan != null && (_reclamationPage + 1L) * PageSize < VisibleReclamationItemCount()) { _reclamationPage++; RenderStorageReclamationPage(); } });
        actions.Controls.Add(_reclamationPageLabel);
        AddButton(actions, "Open selected duplicate workflow", async (_, _) => await OpenSelectedOptimizationWorkflowAsync());
        AddButton(actions, "Add selected encodes to queue", async (_, _) => await QueueSelectedReclamationEncodesAsync());
        AddButton(actions, "Clear category filter", (_, _) => { _reclamationCategoryFilter=null;_reclamationPage=0;RenderStorageReclamationPage(); });
        AddButton(actions, "View breakdown…", (_, _) => ShowReclamationBreakdown());

        _reclamationGrid.Name = "StorageReclamationGrid";
        _reclamationGrid.ReadOnly = false;
        _reclamationGrid.MultiSelect = true;
        _reclamationGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Include", Width = 58 });
        AddReadOnlyColumn("Action", "Action", 160);
        AddReadOnlyColumn("Safety", "Safety", 105);
        AddReadOnlyColumn("File", "File", 220);
        AddReadOnlyColumn("CurrentSize", "Current size", 95);
        AddReadOnlyColumn("PostSize", "Estimated post-size", 115);
        AddReadOnlyColumn("Reclaim", "Potential savings", 125);
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

        _reclamationOpportunitySummary.Name = "StorageOptimizationSummary";
        AddSummaryColumn("Opportunity", 220);AddSummaryColumn("Count", 75);AddSummaryColumn("Current", 105);
        AddSummaryColumn("Post", 115);AddSummaryColumn("Savings", 125);AddSummaryColumn("Locations", 280, true);
        _reclamationOpportunitySummary.CellDoubleClick += (_,e)=>{if(e.RowIndex>=0&&_reclamationOpportunitySummary.Rows[e.RowIndex].Tag is StorageReclamationCategoryTotal total){_reclamationCategoryFilter=total.Category;_reclamationPage=0;RenderStorageReclamationPage();}};

        var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=50};
        split.Resize+=(_,_)=>{if(split.Height>220&&split.SplitterDistance<100)split.SplitterDistance=Math.Min(145,split.Height-75);};
        split.Panel1.Controls.Add(_reclamationOpportunitySummary);split.Panel2.Controls.Add(_reclamationGrid);

        tab.Controls.Add(split);
        tab.Controls.Add(_reclamationStatus);
        tab.Controls.Add(_reclamationSummary);
        tab.Controls.Add(intro);
        tab.Controls.Add(actions);
        _tabs.TabPages.Add(tab);
        ReloadReclamationPolicies();
        _reclamationPolicy.SelectedItem=_reclamationPolicy.Items.Cast<ReclamationPolicyChoice>().FirstOrDefault(item=>item.Policy?.Id==LibraryPolicyBuiltIns.GeneralArchiveId)??_reclamationPolicy.Items[0];
        _reclamationPlan = _reclamationStore.Load();
        RenderStorageReclamationPage();

        void AddReadOnlyColumn(string name, string header, int width, bool fill = false)
        {
            var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true };
            if (fill) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _reclamationGrid.Columns.Add(column);
        }
        void AddSummaryColumn(string name,int width,bool fill=false){var column=new DataGridViewTextBoxColumn{Name=name,HeaderText=name,Width=width,ReadOnly=true};if(fill)column.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;_reclamationOpportunitySummary.Columns.Add(column);}
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
            _reclamationStatus.Text = "Building a bounded, non-overlapping opportunity snapshot from current catalog evidence…";
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
            _reclamationStatus.Text = $"Opportunity snapshot {plan.PlanId[..8]} refreshed from current catalog evidence. No file action was performed.";
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
            _reclamationSummary.Text = "No saved opportunity snapshot. Choose a target and refresh opportunities.";
            _reclamationPageLabel.Text = "";
            return;
        }
        IEnumerable<StorageReclamationPlanItem> visible=_reclamationPlan.Items;
        if(_reclamationCategoryFilter.HasValue)visible=visible.Where(item=>item.ActionCategory==_reclamationCategoryFilter.Value);
        IOrderedEnumerable<StorageReclamationPlanItem> ordered=_reclamationSort.SelectedIndex switch{1=>visible.OrderByDescending(item=>item.CurrentSizeBytes),2=>visible.OrderBy(item=>item.ActionCategory).ThenByDescending(item=>item.ExpectedReclaimBytes),3=>visible.OrderBy(item=>item.LocationPath,StringComparer.OrdinalIgnoreCase).ThenByDescending(item=>item.ExpectedReclaimBytes),_=>visible.OrderByDescending(item=>item.ExpectedReclaimBytes)};
        StorageReclamationPlanItem[] allVisible=ordered.ThenBy(item=>item.SourcePath,StringComparer.OrdinalIgnoreCase).ToArray();
        StorageReclamationPlanItem[] page = allVisible.Skip(_reclamationPage * PageSize).Take(PageSize).ToArray();
        foreach (StorageReclamationPlanItem item in page)
        {
            string keeper = item.ActionCategory == StorageReclamationActionCategory.PolicyReencode
                ? item.PolicyQueueIntent == null ? item.PolicyName : $"{item.PolicyQueueIntent.ProposedCodec} / {item.PolicyQueueIntent.TargetContainer}"
                : item.KeeperPath;
            int index = _reclamationGrid.Rows.Add(item.Included, ReclamationActionLabel(item.ActionCategory), ReclamationSafetyLabel(item.SafetyState),
                Path.GetFileName(item.SourcePath), FormatBytes(item.CurrentSizeBytes),
                item.EstimatedPostOptimizationBytes.HasValue?FormatBytes(item.EstimatedPostOptimizationBytes.Value):"Unknown",
                $"{(item.SavingsAreEstimated?"Estimated":"Exact")} · {FormatBytes(item.ExpectedReclaimBytes)}", item.Confidence, FormatRuntimeHours(item.EstimatedProcessingHours),
                FormatEfficiency(item.SavingsPerComputeHourGb), item.RuntimeConfidence == RuntimeEstimateConfidence.Unknown ? "Unknown" : item.RuntimeConfidence,
                keeper, item.Reason, item.SourceSubsystem, item.SourcePath);
            _reclamationGrid.Rows[index].Tag = item;
            _reclamationGrid.Rows[index].Cells["Include"].ReadOnly = item.SafetyState != StorageReclamationSafetyState.Ready;
            if (item.SafetyState != StorageReclamationSafetyState.Ready) _reclamationGrid.Rows[index].DefaultCellStyle.ForeColor = Color.DarkOrange;
        }
        long first = allVisible.Length == 0 ? 0 : (long)_reclamationPage * PageSize + 1;
        long last = Math.Min(allVisible.Length, (long)(_reclamationPage + 1) * PageSize);
        _reclamationPageLabel.Text = allVisible.Length == 0 ? "No items" : $"{first:N0}–{last:N0} of {allVisible.Length:N0}";
        RenderStorageOptimizationSummary();
        UpdateReclamationSummary();
        RefreshStorageReclamationStaleness();
    }

    private void RenderStorageOptimizationSummary()
    {
        _reclamationOpportunitySummary.Rows.Clear();if(_reclamationPlan==null)return;
        IEnumerable<StorageReclamationCategoryTotal> totals=_reclamationSort.SelectedIndex switch{1=>_reclamationPlan.CategoryTotals.OrderByDescending(item=>item.CurrentBytes),2=>_reclamationPlan.CategoryTotals.OrderByDescending(item=>item.ItemCount),3=>_reclamationPlan.CategoryTotals.OrderBy(item=>ReclamationActionLabel(item.Category),StringComparer.OrdinalIgnoreCase),_=>_reclamationPlan.CategoryTotals.OrderByDescending(item=>item.PotentialSavingsBytes)};
        foreach(StorageReclamationCategoryTotal total in totals)
        {
            StorageReclamationPlanItem[] categoryItems=_reclamationPlan.Items.Where(item=>item.ActionCategory==total.Category).ToArray();string[] allLocations=categoryItems.Select(item=>item.LocationPath).Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();string locations=string.Join(", ",allLocations.Take(4))+(allLocations.Length>4?$" +{allLocations.Length-4:N0} more":"");
            int groups=total.Category switch{StorageReclamationActionCategory.ExactDuplicateCleanup=>categoryItems.Where(item=>item.ExactGroupId.HasValue).Select(item=>item.ExactGroupId).Distinct().Count(),StorageReclamationActionCategory.ReviewedVisualFamilyCleanup=>categoryItems.Where(item=>item.VisualFamilyId.HasValue).Select(item=>item.VisualFamilyId).Distinct().Count(),StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup=>categoryItems.Where(item=>item.VisualGroupId.HasValue).Select(item=>item.VisualGroupId).Distinct().Count(),_=>0};string count=groups>0?$"{total.ItemCount:N0} files / {groups:N0} groups":$"{total.ItemCount:N0} files";
            int row=_reclamationOpportunitySummary.Rows.Add(ReclamationActionLabel(total.Category),count,FormatBytes(total.CurrentBytes),total.EstimatedPostOptimizationBytes.HasValue?FormatBytes(total.EstimatedPostOptimizationBytes.Value):"Unknown",$"{(total.IncludesEstimatedSavings?"Estimated":"Exact")} · {FormatBytes(total.PotentialSavingsBytes)}",locations);_reclamationOpportunitySummary.Rows[row].Tag=total;
        }
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

    private int VisibleReclamationItemCount()=>_reclamationPlan?.Items.Count(item=>!_reclamationCategoryFilter.HasValue||item.ActionCategory==_reclamationCategoryFilter.Value)??0;

    private async Task OpenSelectedOptimizationWorkflowAsync()
    {
        StorageReclamationPlanItem? item=SelectedReclamation().FirstOrDefault(value=>value.ActionCategory is StorageReclamationActionCategory.ExactDuplicateCleanup or StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup or StorageReclamationActionCategory.ReviewedVisualFamilyCleanup);
        if(item==null){MessageBox.Show(this,"Select a duplicate opportunity first.","Storage Optimization",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
        _reclamationGrid.ClearSelection();foreach(DataGridViewRow row in _reclamationGrid.Rows)if(row.Tag is StorageReclamationPlanItem candidate&&candidate.ItemId==item.ItemId)row.Selected=true;
        string tab=item.ExactGroupId.HasValue?"Duplicates — Exact":item.VisualFamilyId.HasValue?"Duplicates — Families":"Duplicates — Visual";
        await LocateReclamationDuplicateAsync(tab);
    }

    private async Task QueueSelectedReclamationEncodesAsync()
    {
        if (_reclamationPlan == null || _reviewOptions.AddPolicyCandidatesToEncodeQueue == null) return;
        StorageReclamationPlanItem[] rowSelection=SelectedReclamation().Where(item=>item.ActionCategory==StorageReclamationActionCategory.PolicyReencode&&item.PolicyQueueIntent!=null).ToArray();
        IReadOnlyList<StorageReclamationPlanItem> selected = rowSelection.Length>0?rowSelection:StorageReclamationQueueOrdering.GetIncludedPolicyItems(_reclamationPlan);
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more ready re-encode opportunities first.", "Storage Optimization", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        LibraryPolicyStore store = _reviewOptions.PolicyStore ?? new LibraryPolicyStore(AppPaths.LibraryPolicyFile);
        LibraryPolicyDefinition? policy = store.LoadAll().FirstOrDefault(item => item.Id.Equals(_reclamationPlan.PolicyId, StringComparison.OrdinalIgnoreCase));
        if (policy == null) { MessageBox.Show(this, "The policy used by this plan no longer exists. Rebuild the opportunities.", "Storage Optimization", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        LibraryPolicyCapabilitySnapshot capabilities = _reviewOptions.PolicyCapabilities ?? new LibraryPolicyCapabilitySnapshot();
        IReadOnlyList<LibraryPolicyEvaluationResult> current = await Task.Run(() => _runtime.PolicyEvaluation.EvaluateForPlanning(policy, capabilities, false));
        Dictionary<long, LibraryPolicyEvaluationResult> ready = current.Where(item => item.State == LibraryPolicyComplianceState.OptimizationCandidate && item.SuggestedAction == LibraryPolicySuggestedAction.Reencode)
            .ToDictionary(item => item.FileId);
        LibraryPolicyQueueItem[] queue = selected.Where(item => File.Exists(item.SourcePath) && ready.TryGetValue(item.FileId, out LibraryPolicyEvaluationResult? result) &&
                PolicyIntentStillMatches(item.PolicyQueueIntent!, result, policy))
            .Select(item => item.PolicyQueueIntent!).ToArray();
        int excluded = selected.Count - queue.Length;
        if (queue.Length == 0) { MessageBox.Show(this, "All selected encode opportunities changed or became unavailable. Refresh the opportunities.", "Storage Optimization", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (MessageBox.Show(this, $"Add {queue.Length:N0} revalidated policy item(s) to the normal Encode queue?\r\n\r\nEncoding will not start. Global settings will not change. {excluded:N0} stale item(s) will be excluded.",
                "Storage Optimization Encode Handoff", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
        StorageReclamationActionCategory.PolicyReencode => "Large / inefficient re-encode",
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
