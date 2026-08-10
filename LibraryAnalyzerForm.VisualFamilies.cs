using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly DataGridView _familyGrid = CreateGrid();
    private readonly DataGridView _familyMembersGrid = CreateGrid();
    private readonly Label _familyStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 6, 0, 0) };
    private readonly CheckBox _familyShowIgnored = new() { Text = "Show ignored", AutoSize = true, Margin = new Padding(10, 10, 3, 3) };
    private long _familyTotal;

    private void BuildVisualFamiliesTab()
    {
        var tab = new TabPage("Duplicates — Families") { Padding = new Padding(8) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false };
        AddButton(actions, "Refresh", async (_, _) => await RefreshVisualFamiliesAsync());
        AddButton(actions, "Rebuild from current pair evidence", async (_, _) => await RebuildVisualFamiliesAsync());
        AddButton(actions, "Review family…", async (_, _) => await OpenVisualFamilyReviewAsync());
        AddButton(actions, "Mark reviewed", async (_, _) => await SaveSelectedFamilyStateAsync(reviewed: true));
        AddButton(actions, "Ignore / restore", async (_, _) => await ToggleSelectedFamilyIgnoredAsync());
        AddButton(actions, "Review family cleanup…", async (_, _) => await PreviewFamilyCleanupAsync());
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
        _familyGrid.MultiSelect = false;
        _familyGrid.SelectionChanged += async (_, _) => await RefreshVisualFamilyMembersAsync();
        _familyGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await OpenVisualFamilyReviewAsync(); };

        foreach ((string name, string header, int width) in new[]
        {
            ("Keeper","Keeper",80),("Protected","Protected",75),("Path","Path",380),("Size","Size",85),
            ("Codec","Codec",75),("Resolution","Resolution",90),("Bitrate","Bitrate",90),("HDR","HDR",50),
            ("Audio","Audio",120),("Availability","Availability",90),("Evidence","Direct confidence",110)
        })
            _familyMembersGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
        _familyMembersGrid.Columns["Path"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _familyMembersGrid.MultiSelect = false;
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
        long? selected = SelectedVisualFamily()?.FamilyId;
        _familyGrid.Rows.Clear();
        foreach (VisualFamilyRecord family in page.Families)
        {
            string keeper = family.ManualKeeperFileId.HasValue ? "Manual" : family.SuggestedKeeperFileId.HasValue ? "Suggested" : "Unresolved";
            int row = _familyGrid.Rows.Add(family.FamilyId, family.MemberCount, $"{family.MinimumConfidence:0.0}%",
                FormatBytes(family.ReclaimableBytes), keeper, family.Ignored ? "Ignored" : family.Reviewed ? "Reviewed" : "Unreviewed",
                $"{family.MemberCount * (family.MemberCount - 1) / 2:N0} preserved pair edges");
            _familyGrid.Rows[row].Tag = family;
        }
        if (_familyGrid.Rows.Count > 0)
        {
            DataGridViewRow row = _familyGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(x => (x.Tag as VisualFamilyRecord)?.FamilyId == selected) ?? _familyGrid.Rows[0];
            row.Selected = true;
            _familyGrid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
        }
        _familyStatus.Text = $"{page.TotalCount:N0} active non-ambiguous visual families. Internal pairs are preserved but suppressed from normal pair review.";
        await RefreshVisualFamilyMembersAsync();
    }

    private async Task RefreshVisualFamilyMembersAsync()
    {
        VisualFamilyRecord? family = SelectedVisualFamily();
        _familyMembersGrid.Rows.Clear();
        if (family == null) return;
        IReadOnlyList<VisualFamilyMemberRecord> members = await Task.Run(() => _runtime.FamilyCatalog.GetVisualFamilyMembers(family.FamilyId));
        if (IsDisposed) return;
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

    private async Task SaveSelectedFamilyStateAsync(bool reviewed)
    {
        if (SelectedVisualFamily() is not { } family) return;
        await Task.Run(() => _runtime.FamilyCatalog.SaveVisualFamilyDecision(new VisualFamilyDecision(
            family.FamilyId, family.ManualKeeperFileId, reviewed, family.Ignored)));
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

    private async Task PreviewFamilyCleanupAsync()
    {
        if (SelectedVisualFamily() is not { } family) return;
        try
        {
            VisualFamilyCleanupProposal proposal = await Task.Run(() => _runtime.VisualFamilies.BuildCleanupProposal(family.FamilyId));
            if (proposal.Items.Count == 0)
            {
                MessageBox.Show(this, "No safe family cleanup candidates remain. Select a keeper, mark the family reviewed, and ensure candidates are present, unchanged, and unprotected.",
                    "Family cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dialog = new MediaFluxForm { Text = "Review Visual Family Cleanup Plan", StartPosition = FormStartPosition.CenterParent, Size = new Size(1160, 620), MinimumSize = new Size(900, 480) };
            var warning = new Label
            {
                Dock = DockStyle.Top, Height = 58, Padding = new Padding(10),
                ForeColor = _cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete ? Color.DarkRed : SystemColors.ControlText,
                Text = (_cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete ? "PERMANENT DELETE — selected files cannot be recovered.\r\n" : "") +
                       "Each candidate has direct visual evidence to the selected keeper and will be independently revalidated before cleanup."
            };
            var grid = CreateGrid(); grid.Name = "VisualFamilyCleanupPreviewGrid"; grid.ReadOnly = false;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Delete", Width = 55 });
            foreach ((string name, string header, int width) in new[] { ("Confidence","Confidence",85),("Keeper","Keep",330),("Candidate","Delete",430),("Size","Reclaim",90),("Evidence","Evidence",280) })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true });
            foreach (VisualCleanupProposalItem item in proposal.Items)
            {
                int row = grid.Rows.Add(true, $"{item.Group.ConfidenceScore:0.0}%", item.Keeper.FullPath, item.Candidate.FullPath,
                    FormatBytes(item.Candidate.SizeBytes), item.HasExactEvidence ? "Current SHA-256 evidence" : "Direct family pair evidence");
                grid.Rows[row].Tag = item;
            }
            var summary = new Label { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 5, 0, 0) };
            void UpdateSummary()
            {
                VisualCleanupProposalItem[] selected = grid.Rows.Cast<DataGridViewRow>().Where(row => Convert.ToBoolean(row.Cells["Include"].Value ?? false)).Select(row => (VisualCleanupProposalItem)row.Tag!).ToArray();
                summary.Text = $"Selected: {selected.Length:N0} files · {FormatBytes(selected.Sum(x => x.Candidate.SizeBytes))} · {proposal.ExcludedMembers:N0} members excluded";
            }
            grid.CellValueChanged += (_, _) => UpdateSummary();
            grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(7) };
            footer.Controls.Add(new Button { Text = "Approve selected plan…", Width = 165, DialogResult = DialogResult.OK });
            footer.Controls.Add(new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel });
            dialog.Controls.Add(grid); dialog.Controls.Add(summary); dialog.Controls.Add(footer); dialog.Controls.Add(warning); UpdateSummary();
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            VisualCleanupProposalItem[] approved = grid.Rows.Cast<DataGridViewRow>().Where(row => Convert.ToBoolean(row.Cells["Include"].Value ?? false)).Select(row => (VisualCleanupProposalItem)row.Tag!).ToArray();
            if (approved.Length == 0) return;
            if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.Quarantine && !Directory.Exists(_cleanupOptions.QuarantineFolder))
                throw new DirectoryNotFoundException("The configured quarantine folder is unavailable.");
            if (MessageBox.Show(this, $"Execute the approved family cleanup?\r\n\r\nFiles: {approved.Length:N0}\r\nEstimated space: {FormatBytes(approved.Sum(x => x.Candidate.SizeBytes))}\r\nAction: {CleanupActionLabel(_cleanupOptions.PreferredAction)}\r\n\r\nEvery file will be revalidated.",
                "Confirm family cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            VisualCleanupPlanRecord plan = await Task.Run(() => _runtime.VisualDuplicateCleanup.CreatePlan(approved, _cleanupOptions.PreferredAction,
                _cleanupOptions.QuarantineFolder, allowUnreviewed: true, minimumConfidence: family.MinimumConfidence));
            DuplicateCleanupExecutionResult result = await _runtime.VisualDuplicateCleanup.ExecutePlanAsync(plan.PlanId);
            MessageBox.Show(this, $"Family cleanup completed.\r\n\r\nSucceeded: {result.Succeeded:N0}\r\nExcluded: {result.Excluded:N0}\r\nFailed: {result.Failed:N0}",
                "Family cleanup", MessageBoxButtons.OK, result.Failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            await RefreshVisualFamiliesAsync();
        }
        catch (Exception ex) { ShowError("The family cleanup plan could not be completed. No unvalidated files were changed.", ex); }
    }

    private VisualFamilyRecord? SelectedVisualFamily() => _familyGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as VisualFamilyRecord;

    private void PlayFamilyMember(VisualFamilyMemberRecord member) => PlayVisualMember(new VisualSimilarityMemberRecord(
        member.FamilyId, member.FileId, member.FullPath, member.LocationPath, member.SizeBytes, member.LastWriteUtc,
        member.Availability, member.VideoCodec, member.Width, member.Height, member.TotalBitRate, member.DurationSeconds,
        member.IsProtected, member.IsSuggestedKeeper, member.IsManualKeeper, member.IsHdr, member.AudioSummary));
}
