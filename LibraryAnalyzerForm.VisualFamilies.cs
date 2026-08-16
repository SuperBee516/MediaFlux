using MediaFlux.Services.LibraryCatalog;
using MediaFlux.Services;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly DataGridView _familyGrid = CreateGrid("FamilyGrid");
    private readonly DataGridView _familyMembersGrid = CreateGrid("FamilyMembersGrid");
    private readonly ContextMenuStrip _familyMenu = new();
    private readonly ContextMenuStrip _familyMembersMenu = new();
    private readonly Label _familyStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 6, 0, 0) };
    private readonly CheckBox _familyShowIgnored = new() { Text = "Show ignored", AutoSize = true, Margin = new Padding(10, 10, 3, 3) };
    private long _familyTotal;
    private CancellationTokenSource? _familyCleanupCancellation;
    private int _familyMemberLoadVersion;
    private readonly SemaphoreSlim _familyMemberRefreshLock = new(1, 1);

    private void BuildVisualFamiliesTab()
    {
        var tab = new TabPage("Duplicates — Families") { Padding = new Padding(8) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false };
        AddButton(actions, "Refresh", async (_, _) => await RefreshVisualFamiliesAsync());
        AddButton(actions, "Rebuild from current pair evidence", async (_, _) => await RebuildVisualFamiliesAsync());
        AddButton(actions, "Review family…", async (_, _) => await OpenVisualFamilyReviewAsync());
        AddButton(actions, "Mark selected reviewed", async (_, _) => await SaveSelectedFamiliesStateAsync(reviewed: true));
        AddButton(actions, "Ignore / restore", async (_, _) => await ToggleSelectedFamilyIgnoredAsync());
        AddButton(actions, "Clean selected…", async (_, _) => await PreviewFamilyCleanupAsync(allReviewedFamilies: false));
        AddButton(actions, "Clean all reviewed…", async (_, _) => await PreviewFamilyCleanupAsync(allReviewedFamilies: true));
        AddButton(actions, "Cancel cleanup", (_, _) => _familyCleanupCancellation?.Cancel());
        _familyShowIgnored.CheckedChanged += async (_, _) => await RefreshVisualFamiliesAsync();
        actions.Controls.Add(_familyShowIgnored);

        _familyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", Visible = false });
        _familyGrid.Columns.Add("Members", "Members");
        _familyGrid.Columns.Add("Confidence", "Minimum confidence");
        _familyGrid.Columns.Add("Space", "Potential space");
        _familyGrid.Columns.Add("Keeper", "Keeper");
        _familyGrid.Columns.Add("State", "Review state");
        _familyGrid.Columns.Add("Evidence", "Construction");
        _familyGrid.Columns[6].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _familyGrid.MultiSelect = true;
        _familyGrid.SelectionChanged += async (_, _) => await RefreshVisualFamilyMembersAsync();
        _familyGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0) return;
            _familyGrid.ClearSelection();
            _familyGrid.Rows[e.RowIndex].Selected = true;
            _familyGrid.CurrentCell = _familyGrid.Rows[e.RowIndex].Cells.Cast<DataGridViewCell>()
                .First(cell => cell.Visible);
            await OpenVisualFamilyReviewAsync();
        };

        foreach ((string name, string header, int width) in new[]
        {
            ("Keeper","Keeper",80),("Protected","Protected",75),("Path","Path",380),("Size","Size",85),
            ("Codec","Codec",75),("Resolution","Resolution",90),("Bitrate","Bitrate",90),("HDR","HDR",50),
            ("Audio","Audio",120),("Availability","Availability",90),("Evidence","Direct confidence",110)
        })
            _familyMembersGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
        _familyMembersGrid.Columns["Path"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _familyMembersGrid.MultiSelect = true;
        _familyMembersGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _familyMembersGrid.Rows[e.RowIndex].Tag is VisualFamilyMemberRecord member)
                PlayFamilyMember(member);
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 235, Panel1MinSize = 120, Panel2MinSize = 150 };
        split.Panel1.Controls.Add(_familyGrid);
        split.Panel2.Controls.Add(_familyMembersGrid);
        var notice = new Label
        {
            Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(8, 7, 0, 0), ForeColor = Color.DarkOrange,
            Text = "Families require direct visual evidence between every pair. Ambiguous overlaps remain in pair review. Cleanup still revalidates each candidate independently."
        };
        tab.Controls.Add(split);
        tab.Controls.Add(_familyStatus);
        tab.Controls.Add(notice);
        tab.Controls.Add(actions);
        _tabs.TabPages.Add(tab);
        ConfigureFamilyContextMenus();
    }

    private void ConfigureFamilyContextMenus()
    {
        AddVisualMenuItem(_familyMenu, "Review Family", "Review", OpenVisualFamilyReviewAsync);
        AddVisualMenuItem(_familyMenu, "Mark Selected Families Reviewed", "Reviewed",
            () => SaveSelectedFamiliesStateAsync(reviewed: true));
        AddVisualMenuItem(_familyMenu, "Mark Selected Families Unreviewed", "MarkUnreviewed",
            () => SaveSelectedFamiliesStateAsync(reviewed: false));
        AddVisualMenuItem(_familyMenu, "Ignore", "Ignore", ToggleSelectedFamilyIgnoredAsync);
        _familyMenu.Items.Add(new ToolStripSeparator());
        AddVisualMenuItem(_familyMenu, "Re-analyze Family Evidence", "Reanalyze", ReanalyzeSelectedFamilyAsync);
        AddVisualMenuItem(_familyMenu, "Rebuild Families From Current Evidence", "Rebuild", RebuildVisualFamiliesAsync);
        _familyMenu.Items.Add(new ToolStripSeparator());
        AddVisualMenuItem(_familyMenu, "Clean Selected Family", "Cleanup",
            () => PreviewFamilyCleanupAsync(allReviewedFamilies: false));
        AddVisualMenuItem(_familyMenu, "Clean All Reviewed Families", "CleanupAllReviewed",
            () => PreviewFamilyCleanupAsync(allReviewedFamilies: true));
        _familyMenu.Opening += (_, e) =>
        {
            VisualFamilyRecord[] families = SelectedVisualFamilies();
            VisualFamilyRecord? family = families.Length == 1 ? families[0] : null;
            SetMenuState(_familyMenu, "Review", family != null);
            SetMenuState(_familyMenu, "Reviewed", families.Any(value => !value.Reviewed));
            SetMenuState(_familyMenu, "MarkUnreviewed", families.Any(value => value.Reviewed));
            SetMenuState(_familyMenu, "Ignore", family != null,
                family?.Ignored == true ? "Restore" : "Ignore");
            SetMenuState(_familyMenu, "Reanalyze", families.Length > 0);
            SetMenuState(_familyMenu, "Cleanup", families.Length > 0,
                families.Length > 1 ? "Clean Selected Families" : "Clean Selected Family");
            SetMenuState(_familyMenu, "CleanupAllReviewed", true);
            e.Cancel = false;
        };
        AttachVisualContextMenu(_familyGrid, _familyMenu);

        AddVisualMenuItem(_familyMembersMenu, "Play Video", "Play", () => { if (SelectedFamilyMembers().SingleOrDefault() is { } member) PlayFamilyMember(member); return Task.CompletedTask; });
        AddVisualMenuItem(_familyMembersMenu, "Open Containing Folder", "Folder", () => { OpenContainingFolders(SelectedFamilyMembers().Select(member => member.FullPath)); return Task.CompletedTask; });
        AddVisualMenuItem(_familyMembersMenu, "Copy File Path", "CopyPath", () => { CopyPaths(SelectedFamilyMembers().Select(member => member.FullPath)); return Task.CompletedTask; });
        AddVisualMenuItem(_familyMembersMenu, "Compare With Keeper", "CompareKeeper", CompareFamilyMemberWithKeeperAsync);
        AddVisualMenuItem(_familyMembersMenu, "Compare Selected Pair", "ComparePair", CompareSelectedFamilyPairAsync);
        _familyMembersMenu.Items.Add(new ToolStripSeparator());
        AddVisualMenuItem(_familyMembersMenu, "Set as Keeper", "Keeper", SetSelectedFamilyKeeperAsync);
        AddVisualMenuItem(_familyMembersMenu, "Protect", "Protect", ToggleSelectedFamilyProtectionAsync);
        AddVisualMenuItem(_familyMembersMenu, "Re-analyze Selected Member(s)", "Reanalyze", ReanalyzeSelectedFamilyMembersAsync);
        _familyMembersMenu.Items.Add(new ToolStripSeparator());
        AddVisualMenuItem(_familyMembersMenu, "Select All Except Keeper", "SelectOthers", () =>
        {
            long? keeperId = SelectedVisualFamily() is { } family ? family.ManualKeeperFileId ?? family.SuggestedKeeperFileId : null;
            SelectFamilyMembers(member => member.FileId != keeperId);
            return Task.CompletedTask;
        });
        AddVisualMenuItem(_familyMembersMenu, "Select All", "SelectAll", () => { SelectFamilyMembers(_ => true); return Task.CompletedTask; });
        AddVisualMenuItem(_familyMembersMenu, "Select None", "SelectNone", () => { LibraryAnalyzerGridInteraction.ClearSelection(_familyMembersGrid); return Task.CompletedTask; });
        AddVisualMenuItem(_familyMembersMenu, "Invert Selection", "Invert", () => { LibraryAnalyzerGridInteraction.SelectRows<VisualFamilyMemberRecord>(_familyMembersGrid, _ => true, invert: true); return Task.CompletedTask; });
        AddVisualMenuItem(_familyMembersMenu, "Select Available", "SelectAvailable", () => { SelectFamilyMembers(IsAvailableFamilyMember); return Task.CompletedTask; });
        AddVisualMenuItem(_familyMembersMenu, "Select Unprotected", "SelectUnprotected", () => { SelectFamilyMembers(member => !member.IsProtected); return Task.CompletedTask; });
        _familyMembersMenu.Opening += (_, e) => UpdateFamilyMembersMenuState(e);
        AttachVisualContextMenu(_familyMembersGrid, _familyMembersMenu);
    }

    private void UpdateFamilyMembersMenuState(System.ComponentModel.CancelEventArgs e)
    {
        VisualFamilyMemberRecord[] members = SelectedFamilyMembers();
        VisualFamilyRecord? family = SelectedVisualFamily();
        bool valid = family != null && members.Length > 0 && members.All(member => member.FamilyId == family.FamilyId);
        bool single = valid && members.Length == 1;
        VisualFamilyMemberRecord? member = single ? members[0] : null;
        long? keeperId = family?.ManualKeeperFileId ?? family?.SuggestedKeeperFileId;
        VisualFamilyMemberRecord? keeper = keeperId.HasValue
            ? _familyMembersGrid.Rows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<VisualFamilyMemberRecord>().FirstOrDefault(value => value.FileId == keeperId)
            : null;
        SetMenuState(_familyMembersMenu, "Play", single && File.Exists(member!.FullPath));
        SetMenuState(_familyMembersMenu, "Folder", valid && members.All(value => Directory.Exists(Path.GetDirectoryName(value.FullPath))));
        SetMenuState(_familyMembersMenu, "CopyPath", valid, members.Length > 1 ? "Copy File Paths" : "Copy File Path");
        SetMenuState(_familyMembersMenu, "Keeper", single && IsAvailableFamilyMember(member!) && member!.FileId != family!.ManualKeeperFileId);
        SetMenuState(_familyMembersMenu, "Protect", valid, members.All(value => value.IsProtected) ? "Unprotect" : "Protect");
        SetMenuState(_familyMembersMenu, "CompareKeeper", single && keeper != null && keeper.FileId != member!.FileId && IsAvailableFamilyMember(keeper) && IsAvailableFamilyMember(member));
        SetMenuState(_familyMembersMenu, "ComparePair", members.Length == 2 && members.All(IsAvailableFamilyMember));
        SetMenuState(_familyMembersMenu, "Reanalyze", valid);
        SetMenuState(_familyMembersMenu, "SelectOthers", family != null && keeperId.HasValue && _familyMembersGrid.Rows.Count > 1);
        e.Cancel = !valid;
    }

    private async Task RebuildVisualFamiliesAsync()
    {
        try
        {
            UseWaitCursor = true;
            VisualFamilyConstructionResult result = await Task.Run(() => _runtime.FamilyCatalog.RebuildVisualFamilies());
            _familyStatus.Text = $"Created {result.FamiliesCreated:N0} families from {result.EligibleEdges:N0} eligible edges; {result.AmbiguousComponents:N0} ambiguous/oversized components stayed as pairs. {result.Elapsed.TotalMilliseconds:N0} ms.";
            await RefreshVisualFamiliesAsync();
            await RefreshVisualGroupsAsync();
        }
        catch (Exception ex) { ShowError("Visual families could not be rebuilt.", ex); }
        finally { UseWaitCursor = false; }
    }

    private async Task RefreshVisualFamiliesAsync()
    {
        VisualFamilyPage page = await Task.Run(() => _runtime.FamilyCatalog.QueryVisualFamilies(
            new VisualFamilyQuery(Ignored: _familyShowIgnored.Checked ? null : false, Limit: 500)));
        if (IsDisposed) return;
        _familyTotal = page.TotalCount;
        long[] selectedIds = SelectedVisualFamilies().Select(family => family.FamilyId).ToArray();
        long? currentId = SelectedVisualFamily()?.FamilyId;
        bool hadSelection = selectedIds.Length > 0;
        _familyGrid.Rows.Clear();
        foreach (VisualFamilyRecord family in page.Families)
        {
            string keeper = family.ManualKeeperFileId.HasValue ? "Manual" : family.SuggestedKeeperFileId.HasValue ? "Suggested" : "Unresolved";
            int row = _familyGrid.Rows.Add(family.FamilyId, family.MemberCount, $"{family.MinimumConfidence:0.0}%",
                FormatBytes(family.ReclaimableBytes), keeper, family.Ignored ? "Ignored" : family.Reviewed ? "Reviewed" : "Unreviewed",
                $"{family.MemberCount * (family.MemberCount - 1) / 2:N0} preserved pair edges");
            _familyGrid.Rows[row].Tag = family;
        }
        _familyGrid.ClearSelection();
        foreach (DataGridViewRow row in _familyGrid.Rows)
            row.Selected = row.Tag is VisualFamilyRecord family &&
                           selectedIds.Contains(family.FamilyId);
        if (!hadSelection && _familyGrid.Rows.Count > 0)
            _familyGrid.Rows[0].Selected = true;
        if (_familyGrid.SelectedRows.Count > 0)
        {
            DataGridViewRow row = _familyGrid.Rows.Cast<DataGridViewRow>()
                .FirstOrDefault(value =>
                    value.Selected &&
                    (value.Tag as VisualFamilyRecord)?.FamilyId == currentId)
                ?? _familyGrid.SelectedRows.Cast<DataGridViewRow>().OrderBy(value => value.Index).First();
            _familyGrid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
        }
        _familyStatus.Text = $"{page.TotalCount:N0} active non-ambiguous visual families. Internal pairs are preserved but suppressed from normal pair review.";
        await RefreshVisualFamilyMembersAsync();
    }

    private async Task RefreshVisualFamilyMembersAsync()
    {
        int version = Interlocked.Increment(ref _familyMemberLoadVersion);
        await _familyMemberRefreshLock.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _familyMemberLoadVersion) || IsDisposed) return;
            VisualFamilyRecord? family = SelectedVisualFamily();
            if (family == null)
            {
                _familyMembersGrid.Rows.Clear();
                return;
            }
            IReadOnlyList<VisualFamilyMemberRecord> members = await Task.Run(() =>
                _runtime.FamilyCatalog.GetVisualFamilyMembers(family.FamilyId));
            if (version != Volatile.Read(ref _familyMemberLoadVersion) || IsDisposed) return;
            _familyMembersGrid.Rows.Clear();
            foreach (VisualFamilyMemberRecord member in members)
            {
                string keeper = member.IsManualKeeper ? "Manual" : member.IsSuggestedKeeper ? "Suggested" : "";
                int row = _familyMembersGrid.Rows.Add(keeper, member.IsProtected ? "Yes" : "No", member.FullPath, FormatBytes(member.SizeBytes),
                    member.VideoCodec.ToUpperInvariant(), member.Width.HasValue && member.Height.HasValue ? $"{member.Width}×{member.Height}" : "",
                    member.TotalBitRate.HasValue ? $"{member.TotalBitRate.Value / 1_000_000d:0.##} Mbps" : "", member.IsHdr ? "Yes" : "No",
                    member.AudioSummary, member.Availability, $"{member.MinimumMemberConfidence:0.0}%");
                _familyMembersGrid.Rows[row].Tag = member;
            }
        }
        finally { _familyMemberRefreshLock.Release(); }
    }

    private async Task OpenVisualFamilyReviewAsync()
    {
        if (SelectedVisualFamily() == null) return;
        bool semiAutomatic = (_reviewOptions.AutomationOptions ?? new LibraryVisualReviewAutomationOptions()).Normalize().SemiAutomaticKeeperApproval;
        double minimumMargin = (_reviewOptions.AutomationOptions ?? new LibraryVisualReviewAutomationOptions()).Normalize().MinimumAutomationMargin;
        using var dialog = new MediaFluxForm { Text = "Review Visual Family", StartPosition = FormStartPosition.CenterParent, Size = new Size(1220, 720), MinimumSize = new Size(960, 520) };
        var header = new Label { Dock = DockStyle.Top, Height = 100, Padding = new Padding(10), BackColor = SystemColors.Window };
        var grid = CreateGrid(); grid.MultiSelect = false;
        foreach ((string name, string headerText, int width) in new[]
        {
            ("Keeper","Keeper",90),("Path","File",350),("Resolution","Resolution",90),("Codec","Codec",75),("Bitrate","Bitrate",90),
            ("HDR","HDR",50),("Audio","Audio",125),("Size","Size",80),("Confidence","Direct evidence",100),("State","State",120)
        })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = headerText, Width = width });
        grid.Columns["Path"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(7), WrapContents = false };
        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.OK };
        var next = new Button { Text = semiAutomatic ? "Accept + Next" : "Next >", Width = semiAutomatic ? 110 : 90 };
        var previous = new Button { Text = "< Previous", Width = 90 };
        var setKeeper = new Button { Text = "Set selected keeper", Width = 145 };
        var protect = new Button { Text = "Protect / unprotect", Width = 140 };
        var play = new Button { Text = "Play selected", Width = 105 };
        var reviewed = new Button { Text = "Mark reviewed", Width = 110 };
        var ignore = new Button { Text = "Ignore", Width = 90 };
        footer.Controls.AddRange(new Control[] { close, next, previous, ignore, reviewed, protect, setKeeper, play });
        dialog.Controls.Add(grid); dialog.Controls.Add(footer); dialog.Controls.Add(header);
        VisualFamilyRecord? current = null;
        long? automaticKeeper = null;

        async Task LoadAsync()
        {
            current = SelectedVisualFamily();
            if (current == null) { dialog.Close(); return; }
            if (!current.ManualKeeperFileId.HasValue)
                await Task.Run(() => _runtime.VisualFamilies.RefreshSuggestedKeeper(current.FamilyId, minimumMargin));
            current = await Task.Run(() => _runtime.FamilyCatalog.GetVisualFamily(current.FamilyId));
            if (current == null) { dialog.Close(); return; }
            IReadOnlyList<VisualFamilyMemberRecord> members = await Task.Run(() => _runtime.FamilyCatalog.GetVisualFamilyMembers(current.FamilyId));
            LibraryKeeperExplanation explanation = _runtime.VisualFamilies.Explain(current.FamilyId);
            automaticKeeper = current.ManualKeeperFileId ?? (semiAutomatic ? current.SuggestedKeeperFileId : null);
            header.Text = $"Visual family · {current.MemberCount:N0} members · minimum direct confidence {current.MinimumConfidence:0.0}% · {(current.Reviewed ? "Reviewed" : "Unreviewed")}\r\n" +
                          $"Keeper: {(current.ManualKeeperFileId.HasValue ? "manual" : current.SuggestedKeeperFileId.HasValue ? "recommended" : "unresolved")} · score {explanation.Score:0.0}, automation margin {explanation.Margin:0.0}\r\n" +
                          explanation.Summary;
            ignore.Text = current.Ignored ? "Restore" : "Ignore";
            grid.Rows.Clear();
            foreach (VisualFamilyMemberRecord member in members)
            {
                bool selected = member.FileId == current.ManualKeeperFileId || (!current.ManualKeeperFileId.HasValue && member.FileId == automaticKeeper);
                int row = grid.Rows.Add(selected ? current.ManualKeeperFileId.HasValue ? "Manual" : "Selected for Next" : member.IsSuggestedKeeper ? "Suggested" : "",
                    member.FullPath, member.Width.HasValue && member.Height.HasValue ? $"{member.Width}×{member.Height}" : "", member.VideoCodec.ToUpperInvariant(),
                    member.TotalBitRate.HasValue ? $"{member.TotalBitRate.Value / 1_000_000d:0.##} Mbps" : "", member.IsHdr ? "Yes" : "No", member.AudioSummary,
                    FormatBytes(member.SizeBytes), $"{member.MinimumMemberConfidence:0.0}%", $"{member.Availability}{(member.IsProtected ? " · Protected" : "")}");
                grid.Rows[row].Tag = member;
                if (selected) grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(220, 245, 220);
            }
            if (grid.Rows.Count > 0) grid.Rows[0].Selected = true;
            dialog.Text = $"Review Visual Family · {current.MemberCount:N0} members";
        }

        async Task MoveAsync(int delta)
        {
            if (_familyGrid.Rows.Count == 0 || _familyGrid.SelectedRows.Count == 0) return;
            int index = (_familyGrid.SelectedRows[0].Index + delta + _familyGrid.Rows.Count) % _familyGrid.Rows.Count;
            _familyGrid.ClearSelection(); _familyGrid.Rows[index].Selected = true;
            _familyGrid.CurrentCell = _familyGrid.Rows[index].Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
            await LoadAsync();
        }
        play.Click += (_, _) => { if (grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is VisualFamilyMemberRecord member) PlayFamilyMember(member); };
        setKeeper.Click += async (_, _) =>
        {
            if (current == null || grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is not VisualFamilyMemberRecord member ||
                member.Availability != IndexedFileAvailability.Present || !File.Exists(member.FullPath)) return;
            await Task.Run(() => _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(current.FamilyId, member.FileId, true, current.Ignored)));
            await LoadAsync();
        };
        protect.Click += async (_, _) =>
        {
            if (grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is not VisualFamilyMemberRecord member) return;
            await Task.Run(() => _runtime.AnalysisCatalog.SetFileProtection(member.FileId, !member.IsProtected, "Library Analyzer family review"));
            await LoadAsync();
        };
        reviewed.Click += async (_, _) => { if (current != null) { await Task.Run(() => _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(current.FamilyId, current.ManualKeeperFileId, true, current.Ignored))); await LoadAsync(); } };
        ignore.Click += async (_, _) =>
        {
            if (current == null) return;
            await Task.Run(() => _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(current.FamilyId, current.ManualKeeperFileId, true, !current.Ignored)));
            dialog.Close();
        };
        previous.Click += async (_, _) => await MoveAsync(-1);
        next.Click += async (_, _) =>
        {
            if (current != null && semiAutomatic && !current.ManualKeeperFileId.HasValue && automaticKeeper.HasValue)
                await Task.Run(() => _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(current.FamilyId, automaticKeeper, true, current.Ignored, Source: "semi-automatic-family-review")));
            await MoveAsync(1);
        };
        dialog.Shown += async (_, _) => await LoadAsync();
        dialog.ShowDialog(this);
        await RefreshVisualFamiliesAsync();
        await RefreshVisualGroupsAsync();
    }

    private async Task SaveSelectedFamiliesStateAsync(bool reviewed)
    {
        VisualFamilyRecord[] families = SelectedVisualFamilies();
        if (families.Length == 0) return;
        string batchId = Guid.NewGuid().ToString("N");
        await Task.Run(() =>
        {
            foreach (VisualFamilyRecord family in families)
                _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(
                    family.FamilyId, family.ManualKeeperFileId, reviewed, family.Ignored,
                    batchId, reviewed ? "batch-family-reviewed" : "batch-family-unreviewed"));
        });
        await RefreshVisualFamiliesAsync();
    }

    private async Task ToggleSelectedFamilyIgnoredAsync()
    {
        if (SelectedVisualFamily() is not { } family) return;
        await Task.Run(() => _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(
            family.FamilyId, family.ManualKeeperFileId, true, !family.Ignored)));
        await RefreshVisualFamiliesAsync();
        await RefreshVisualGroupsAsync();
    }

    private VisualFamilyMemberRecord[] SelectedFamilyMembers() =>
        LibraryAnalyzerGridInteraction.SelectedItems<VisualFamilyMemberRecord>(_familyMembersGrid);

    private static bool IsAvailableFamilyMember(VisualFamilyMemberRecord member) =>
        member.Availability == IndexedFileAvailability.Present && File.Exists(member.FullPath);

    private void SelectFamilyMembers(Func<VisualFamilyMemberRecord, bool> predicate) =>
        LibraryAnalyzerGridInteraction.SelectRows(_familyMembersGrid, predicate);

    private async Task SetSelectedFamilyKeeperAsync()
    {
        if (SelectedVisualFamily() is not { } family || SelectedFamilyMembers().SingleOrDefault() is not { } member ||
            !IsAvailableFamilyMember(member)) return;
        await Task.Run(() => _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(
            family.FamilyId, member.FileId, true, family.Ignored)));
        await RefreshVisualFamiliesAsync();
    }

    private async Task ToggleSelectedFamilyProtectionAsync()
    {
        VisualFamilyMemberRecord[] members = SelectedFamilyMembers();
        if (members.Length == 0) return;
        bool protect = members.Any(member => !member.IsProtected);
        await Task.Run(() =>
        {
            foreach (VisualFamilyMemberRecord member in members)
                _runtime.AnalysisCatalog.SetFileProtection(member.FileId, protect,
                    protect ? "Protected in Library Analyzer visual family" : "");
        });
        await RefreshVisualFamiliesAsync();
    }

    private Task ReanalyzeSelectedFamilyMembersAsync()
    {
        long[] ids = SelectedFamilyMembers().Select(member => member.FileId).Distinct().ToArray();
        if (ids.Length > 0) _runtime.Reanalysis.QueueFiles(ids, LibraryReanalysisWork.VisualFingerprint);
        return Task.CompletedTask;
    }

    private async Task ReanalyzeSelectedFamilyAsync()
    {
        long[] familyIds = SelectedVisualFamilies().Select(family => family.FamilyId).ToArray();
        if (familyIds.Length == 0) return;
        long[] ids = await Task.Run(() => familyIds
            .SelectMany(familyId => _runtime.FamilyCatalog.GetVisualFamilyMembers(familyId))
            .Select(member => member.FileId).Distinct().ToArray());
        if (ids.Length > 0) _runtime.Reanalysis.QueueFiles(ids, LibraryReanalysisWork.VisualFingerprint);
    }

    private async Task CompareFamilyMemberWithKeeperAsync()
    {
        if (SelectedVisualFamily() is not { } family || SelectedFamilyMembers().SingleOrDefault() is not { } member) return;
        long? keeperId = family.ManualKeeperFileId ?? family.SuggestedKeeperFileId;
        if (!keeperId.HasValue || keeperId == member.FileId) return;
        VisualFamilyMemberRecord? keeper = _familyMembersGrid.Rows.Cast<DataGridViewRow>()
            .Select(row => row.Tag).OfType<VisualFamilyMemberRecord>().FirstOrDefault(value => value.FileId == keeperId.Value);
        if (keeper == null) return;
        await OpenMemberComparisonAsync("Compare Family Member With Keeper",
            new[] { ToVisualMember(keeper), ToVisualMember(member) }, keeper.FileId);
    }

    private async Task CompareSelectedFamilyPairAsync()
    {
        VisualFamilyMemberRecord[] members = SelectedFamilyMembers();
        if (members.Length != 2 || members.Any(member => !IsAvailableFamilyMember(member))) return;
        long? keeperId = SelectedVisualFamily() is { } family ? family.ManualKeeperFileId ?? family.SuggestedKeeperFileId : null;
        await OpenMemberComparisonAsync("Compare Selected Family Members", members.Select(ToVisualMember).ToArray(), keeperId);
    }

    private async Task PreviewFamilyCleanupAsync(bool allReviewedFamilies)
    {
        long[] selectedFamilyIds = SelectedVisualFamilies()
            .Select(family => family.FamilyId)
            .Distinct()
            .ToArray();
        if (!allReviewedFamilies && selectedFamilyIds.Length == 0) return;
        string quarantine = _cleanupOptions.QuarantineFolder;
        if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.Quarantine &&
            !Directory.Exists(quarantine))
        {
            MessageBox.Show(
                this,
                "The configured quarantine folder is unavailable.",
                "Family cleanup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _familyCleanupCancellation?.Dispose();
        _familyCleanupCancellation = new CancellationTokenSource();
        UseWaitCursor = true;
        try
        {
            CancellationToken token = _familyCleanupCancellation.Token;
            VisualFamilyBatchCleanupPlanResult plan = await Task.Run(
                () => _runtime.VisualFamilies.CreateBatchCleanupPlan(
                    allReviewedFamilies ? null : selectedFamilyIds,
                    allReviewedFamilies,
                    _cleanupOptions.PreferredAction,
                    quarantine,
                    token),
                token);
            string preview = BuildFamilyCleanupPreview(plan);
            if (plan.Summary.Status != DuplicateCleanupStatus.Ready)
            {
                MessageBox.Show(
                    this,
                    preview + "\r\n\r\nNo files were changed.",
                    "Family cleanup preview",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete)
                preview = "WARNING: This action permanently deletes files and cannot be undone.\r\n\r\n" + preview;
            preview += "\r\n\r\nThe ready persisted plan shown above is the plan that will execute. " +
                       "Every keeper and candidate will be revalidated immediately before action.\r\n\r\nExecute this plan?";
            if (MessageBox.Show(
                    this,
                    preview,
                    "Confirm visual family cleanup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                _runtime.VisualDuplicateCleanup.FailPlan(
                    plan.PlanId,
                    "Canceled during consolidated preview; no files were changed.");
                return;
            }

            var progress = new Progress<DuplicateCleanupProgress>(value =>
                _familyStatus.Text =
                    $"Family cleanup {value.ProcessedItems:N0}/{value.TotalItems:N0} · " +
                    $"{FormatBytes(value.ReclaimedBytes)} reclaimed");
            DuplicateCleanupExecutionResult result = await Task.Run(
                async () => await _runtime.VisualDuplicateCleanup.ExecutePlanAsync(
                    plan.PlanId, token, progress).ConfigureAwait(false),
                token);
            MessageBox.Show(
                this,
                $"Family cleanup plan {result.PlanId} finished.\r\n\r\n" +
                $"Succeeded: {result.Succeeded:N0}\r\n" +
                $"Excluded by revalidation: {result.Excluded:N0}\r\n" +
                $"Failed: {result.Failed:N0}\r\n" +
                $"Actual reclaimed: {FormatBytes(result.ReclaimedBytes)}" +
                (string.IsNullOrWhiteSpace(result.ErrorText)
                    ? ""
                    : $"\r\nStatus: {result.ErrorText}"),
                "Family cleanup",
                MessageBoxButtons.OK,
                result.Failed == 0 && string.IsNullOrWhiteSpace(result.ErrorText)
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
            if (result.Succeeded > 0) await RefreshAfterSuccessfulRemovalAsync();
            else
            {
                await RefreshVisualFamiliesAsync();
                await RefreshVisualGroupsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(
                this,
                "Family cleanup planning was canceled. No unvalidated file was changed.",
                "Family cleanup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowError(
                "The family cleanup plan could not be completed. No unvalidated files were changed.",
                ex);
        }
        finally
        {
            UseWaitCursor = false;
            _familyCleanupCancellation?.Dispose();
            _familyCleanupCancellation = null;
        }
    }

    internal static string BuildFamilyCleanupPreview(VisualFamilyBatchCleanupPlanResult result)
    {
        string reasons = result.ExclusionReasons.Count == 0
            ? "None"
            : string.Join("\r\n", result.ExclusionReasons
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Select(pair => $"  {pair.Value:N0} — {pair.Key}"));
        string locations = result.Summary.Locations.Count == 0
            ? "None"
            : string.Join("\r\n", result.Summary.Locations.Select(location =>
                $"  {location.LocationPath}: {location.FileCount:N0} files · " +
                $"{FormatBytes(location.ReclaimableBytes)}"));
        return
            $"Consolidated family cleanup preview\r\n\r\n" +
            $"Families selected/reviewed: {result.RequestedFamilies:N0}\r\n" +
            $"Eligible families: {result.EligibleFamilies:N0}\r\n" +
            $"Excluded families: {result.ExcludedFamilies:N0}\r\n" +
            $"Keepers retained: {result.Summary.KeeperCount:N0}\r\n" +
            $"Files scheduled for cleanup: {result.Summary.PlannedItems:N0}\r\n" +
            $"Reclaimable storage: {FormatBytes(result.Summary.PlannedBytes)}\r\n\r\n" +
            $"Exclusion reasons:\r\n{reasons}\r\n\r\n" +
            $"Location/root totals:\r\n{locations}";
    }

    private VisualFamilyRecord[] SelectedVisualFamilies() =>
        LibraryAnalyzerGridInteraction.SelectedItems<VisualFamilyRecord>(_familyGrid);

    private VisualFamilyRecord? SelectedVisualFamily()
    {
        if (_familyGrid.CurrentRow is { Selected: true, Tag: VisualFamilyRecord current })
            return current;
        return SelectedVisualFamilies().FirstOrDefault();
    }

    private void PlayFamilyMember(VisualFamilyMemberRecord member) => PlayVisualMember(new VisualSimilarityMemberRecord(
        member.FamilyId, member.FileId, member.FullPath, member.LocationPath, member.SizeBytes, member.LastWriteUtc,
        member.Availability, member.VideoCodec, member.Width, member.Height, member.TotalBitRate, member.DurationSeconds,
        member.IsProtected, member.IsSuggestedKeeper, member.IsManualKeeper, member.IsHdr, member.AudioSummary));
}
