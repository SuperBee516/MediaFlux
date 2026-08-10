using System.Security.Cryptography;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private const int DuplicatePageSize = 100;
        private readonly DataGridView _duplicateGroupsGrid = CreateGrid();
        private readonly DataGridView _duplicateMembersGrid = CreateGrid();
        private readonly TextBox _duplicateSearch = new() { Width = 230, PlaceholderText = "Path or filename" };
        private readonly ComboBox _duplicateLocation = DropDown();
        private readonly TextBox _duplicateCodec = new() { Width = 90, PlaceholderText = "e.g. hevc" };
        private readonly ComboBox _duplicateResolution = DropDown();
        private readonly ComboBox _duplicateReviewFilter = DropDown();
        private readonly ComboBox _duplicateProtectionFilter = DropDown();
        private readonly ComboBox _duplicateSort = DropDown();
        private readonly Label _duplicateStatus = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0), Text = "Exact analysis has not run." };
        private readonly Label _duplicatePageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
        private readonly ProgressBar _duplicateProgress = new() { Width = 180, Style = ProgressBarStyle.Marquee, Visible = false };
        private readonly Button _duplicateApplyButton = new() { Name = "DuplicateApplyButton", Text = "Apply", Dock = DockStyle.Top, Height = 30 };
        private readonly TableLayoutPanel _duplicateControlArea = new() { Name = "DuplicateControlArea" };
        private int _duplicatePage;
        private long _duplicateTotal;
        private bool _loadingDuplicateGroups;

        private void BuildDuplicatesTab()
        {
            var tab = new TabPage("Duplicates — Exact") { Padding = new Padding(8) };
            _duplicateStatus.ForeColor = LibraryAnalyzerAccentColor;
            _duplicateControlArea.Dock = DockStyle.Top;
            _duplicateControlArea.Height = 142;
            _duplicateControlArea.ColumnCount = 1;
            _duplicateControlArea.RowCount = 2;
            _duplicateControlArea.Margin = Padding.Empty;
            _duplicateControlArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            _duplicateControlArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var analysis = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, Padding = new Padding(0, 4, 0, 2) };
            AddButton(analysis, "Analyze exact duplicates", AnalyzeDuplicates_Click);
            AddButton(analysis, "Pause", (_, _) => _runtime.Duplicates.Pause());
            AddButton(analysis, "Resume", (_, _) => _runtime.Duplicates.Resume());
            AddButton(analysis, "Cancel", (_, _) => _runtime.Duplicates.Cancel());
            _duplicateControlArea.Controls.Add(analysis, 0, 0);

            var filtersBox = new GroupBox { Text = "Filters", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 7) };
            var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, Margin = Padding.Empty };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            AddVisualFilter(filters, "Search", _duplicateSearch, 0, 0, 2);
            AddVisualFilter(filters, "Location", _duplicateLocation, 2, 0);
            AddVisualFilter(filters, "Review", _duplicateReviewFilter, 3, 0);
            AddVisualFilter(filters, "Codec", _duplicateCodec, 0, 1);
            AddVisualFilter(filters, "Resolution", _duplicateResolution, 1, 1);
            AddVisualFilter(filters, "Protection", _duplicateProtectionFilter, 2, 1);
            AddVisualFilter(filters, "Sort", _duplicateSort, 3, 1);
            var filterActions = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(5, 0, 0, 0) };
            filterActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            filterActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _duplicateApplyButton.Click += async (_, _) => { _duplicatePage = 0; await RefreshDuplicateGroupsAsync(); };
            var reset = new Button { Text = "Reset", Dock = DockStyle.Top, Height = 30 };
            reset.Click += ResetDuplicateFilters_Click;
            filterActions.Controls.Add(_duplicateApplyButton, 0, 0);
            filterActions.Controls.Add(reset, 0, 1);
            filters.Controls.Add(filterActions, 4, 0);
            filters.SetRowSpan(filterActions, 2);
            filtersBox.Controls.Add(filters);
            _duplicateControlArea.Controls.Add(filtersBox, 0, 1);

            _duplicateReviewFilter.Items.AddRange(new object[] { "All", "Unreviewed", "Reviewed", "Ignored" });
            _duplicateReviewFilter.SelectedIndex = 0;
            _duplicateProtectionFilter.Items.AddRange(new object[] { "All", "Protected", "Unprotected" });
            _duplicateProtectionFilter.SelectedIndex = 0;
            _duplicateLocation.Items.Add(new LocationChoice(0, "All locations"));
            _duplicateLocation.SelectedIndex = 0;
            _duplicateResolution.Items.AddRange(new object[] { "All", "8K+", "4K", "1440p", "1080p", "720p", "SD", "Unknown" });
            _duplicateResolution.SelectedIndex = 0;
            _duplicateSort.Items.AddRange(new object[] { "Reclaimable", "Copies", "Size", "Codec", "Resolution", "Reviewed" });
            _duplicateSort.SelectedIndex = 0;

            AddDuplicateGroupColumn("Id", "Id", 60, visible: false);
            AddDuplicateGroupColumn("Reclaimable", "Reclaimable", 105);
            AddDuplicateGroupColumn("Copies", "Copies", 65);
            AddDuplicateGroupColumn("Physical", "Physical copies", 90);
            AddDuplicateGroupColumn("FileSize", "Each", 90);
            AddDuplicateGroupColumn("Codec", "Codec", 80);
            AddDuplicateGroupColumn("Resolution", "Resolution", 85);
            AddDuplicateGroupColumn("State", "Review state", 95);
            AddDuplicateGroupColumn("Protected", "Protected", 75);
            AddDuplicateGroupColumn("Hash", "SHA-256 evidence", 190);
            _duplicateGroupsGrid.MultiSelect = true;
            _duplicateGroupsGrid.SelectionChanged += async (_, _) => await RefreshDuplicateMembersAsync();

            AddDuplicateMemberColumn("Keeper", "Keeper", 85);
            AddDuplicateMemberColumn("Protected", "Protected", 70);
            AddDuplicateMemberColumn("Path", "File location", 420);
            AddDuplicateMemberColumn("Root", "Root", 180);
            AddDuplicateMemberColumn("Size", "Size", 85);
            AddDuplicateMemberColumn("Codec", "Codec", 75);
            AddDuplicateMemberColumn("Resolution", "Resolution", 85);
            AddDuplicateMemberColumn("Bitrate", "Bitrate", 85);
            AddDuplicateMemberColumn("Identity", "Physical identity", 170);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, AutoScroll = true, WrapContents = false };
            AddButton(actions, "Set selected keeper", SetManualKeeper_Click);
            AddButton(actions, "Protect / unprotect file", ToggleProtection_Click);
            AddButton(actions, "Mark reviewed", MarkReviewed_Click);
            AddButton(actions, "Ignore / restore group", ToggleIgnored_Click);
            AddButton(actions, "Re-analyze selected group", QueueSelectedExactGroup_Click);
            AddButton(actions, $"Preview {CleanupActionLabel(_cleanupOptions.PreferredAction)} cleanup…", PreviewPreferredCleanup_Click);

            var pager = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var next = new Button { Text = "Next", AutoSize = true };
            var previous = new Button { Text = "Previous", AutoSize = true };
            next.Click += async (_, _) => { _duplicatePage++; await RefreshDuplicateGroupsAsync(); };
            previous.Click += async (_, _) => { _duplicatePage = Math.Max(0, _duplicatePage - 1); await RefreshDuplicateGroupsAsync(); };
            pager.Controls.Add(next); pager.Controls.Add(previous); pager.Controls.Add(_duplicatePageLabel);

            var status = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false };
            status.Controls.Add(_duplicateProgress); status.Controls.Add(_duplicateStatus);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240, Panel1MinSize = 140, Panel2MinSize = 140 };
            split.Panel1.Controls.Add(_duplicateGroupsGrid);
            split.Panel2.Controls.Add(_duplicateMembersGrid);
            tab.Controls.Add(split);
            tab.Controls.Add(status);
            tab.Controls.Add(pager);
            tab.Controls.Add(actions);
            tab.Controls.Add(_duplicateControlArea);
            _tabs.TabPages.Add(tab);
        }

        private async void ResetDuplicateFilters_Click(object? sender, EventArgs e)
        {
            _duplicateSearch.Clear();
            _duplicateLocation.SelectedIndex = _duplicateLocation.Items.Count > 0 ? 0 : -1;
            _duplicateCodec.Clear();
            _duplicateResolution.SelectedIndex = 0;
            _duplicateReviewFilter.SelectedIndex = 0;
            _duplicateProtectionFilter.SelectedIndex = 0;
            _duplicateSort.SelectedIndex = 0;
            _duplicatePage = 0;
            await RefreshDuplicateGroupsAsync();
        }

        private async void AnalyzeDuplicates_Click(object? sender, EventArgs e)
        {
            if (_runtime.Duplicates.IsRunning) return;
            _duplicateProgress.Visible = true;
            try
            {
                LibraryDuplicateAnalysisResult result = await _runtime.Duplicates.AnalyzeAsync();
                _duplicateStatus.Text = result.Status == DuplicateAnalysisStatus.Completed
                    ? $"Completed: {result.ExactGroups:N0} exact groups; {result.QuickHashed:N0} quick and {result.FullHashed:N0} full hashes read."
                    : $"{result.Status}: {result.ErrorText}";
                _duplicatePage = 0;
                await RefreshDuplicateGroupsAsync();
                await RefreshOverviewAsync();
            }
            catch (Exception ex) { ShowError("Exact duplicate analysis failed.", ex); }
            finally { _duplicateProgress.Visible = false; }
        }

        private void QueueSelectedExactGroup_Click(object? sender, EventArgs e)
        {
            long[] fileIds = _duplicateGroupsGrid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Tag)
                .OfType<ExactDuplicateGroupRecord>()
                .SelectMany(group => _runtime.AnalysisCatalog.GetDuplicateGroupMembers(group.GroupId))
                .Select(member => member.FileId).Distinct().ToArray();
            if (fileIds.Length > 0) _runtime.Reanalysis.QueueFiles(fileIds, LibraryReanalysisWork.ExactHash);
        }

        private async Task RefreshDuplicateGroupsAsync()
        {
            if (_loadingDuplicateGroups || IsDisposed) return;
            _loadingDuplicateGroups = true;
            try
            {
                ExactDuplicateGroupPage page = await Task.Run(() => _runtime.AnalysisCatalog.QueryDuplicateGroups(BuildDuplicateQuery()));
                if (IsDisposed) return;
                _duplicateTotal = page.TotalCount;
                _duplicateGroupsGrid.Rows.Clear();
                foreach (ExactDuplicateGroupRecord group in page.Groups)
                {
                    int row = _duplicateGroupsGrid.Rows.Add(group.GroupId, FormatBytes(group.ReclaimableBytes), group.MemberCount,
                        group.PhysicalCopyCount, FormatBytes(group.SizeBytes), group.VideoCodec, group.ResolutionTier,
                        group.Ignored ? "Ignored" : group.Reviewed ? "Reviewed" : "Unreviewed",
                        group.ProtectedMemberCount, Convert.ToHexString(group.FullHash.AsSpan(0, Math.Min(8, group.FullHash.Length))) + "…");
                    _duplicateGroupsGrid.Rows[row].Tag = group;
                }
                long first = _duplicateTotal == 0 ? 0 : (long)_duplicatePage * DuplicatePageSize + 1;
                long last = Math.Min(_duplicateTotal, ((long)_duplicatePage + 1) * DuplicatePageSize);
                _duplicatePageLabel.Text = $"{first:N0}–{last:N0} of {_duplicateTotal:N0}";
                await RefreshDuplicateMembersAsync();
            }
            finally { _loadingDuplicateGroups = false; }
        }

        private async Task RefreshDuplicateMembersAsync()
        {
            if (_duplicateGroupsGrid.SelectedRows.Count == 0) { _duplicateMembersGrid.Rows.Clear(); return; }
            long groupId = Convert.ToInt64(_duplicateGroupsGrid.SelectedRows[0].Cells[0].Value);
            LibraryMatchEligibility eligibility = await Task.Run(() => _runtime.MatchEligibility.EvaluateExactGroup(groupId));
            if (!eligibility.IsActive)
            {
                _duplicateMembersGrid.Rows.Clear();
                _duplicateStatus.Text = $"Match suspended: {eligibility.Reason}";
                if (!_loadingDuplicateGroups) await RefreshDuplicateGroupsAsync();
                return;
            }
            IReadOnlyList<ExactDuplicateMemberRecord> members = await Task.Run(() => _runtime.AnalysisCatalog.GetDuplicateGroupMembers(groupId));
            if (IsDisposed) return;
            _duplicateMembersGrid.Rows.Clear();
            foreach (ExactDuplicateMemberRecord member in members)
            {
                string keeper = member.IsManualKeeper ? "Manual" : member.IsSuggestedKeeper ? "Suggested" : member.IsHardLinkAlias ? "Hard-link alias" : "Candidate";
                int row = _duplicateMembersGrid.Rows.Add(keeper, member.IsProtected ? "Yes" : "No", member.FullPath, member.LocationPath,
                    FormatBytes(member.SizeBytes), member.VideoCodec, member.Width.HasValue && member.Height.HasValue ? $"{member.Width}×{member.Height}" : "",
                    member.TotalBitRate.HasValue ? $"{member.TotalBitRate / 1_000_000d:0.##} Mbps" : "", member.PhysicalIdentityKey);
                _duplicateMembersGrid.Rows[row].Tag = member;
            }
        }

        private DuplicateGroupQuery BuildDuplicateQuery()
        {
            bool? reviewed = _duplicateReviewFilter.SelectedIndex switch { 1 => false, 2 => true, _ => null };
            bool? ignored = _duplicateReviewFilter.SelectedIndex == 3 ? true : null;
            bool? protection = _duplicateProtectionFilter.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            long? location = _duplicateLocation.SelectedItem is LocationChoice choice && choice.Id > 0 ? choice.Id : null;
            string resolution = _duplicateResolution.SelectedIndex > 0 ? _duplicateResolution.Text : "";
            return new DuplicateGroupQuery(_duplicateSearch.Text, LocationId: location, Codec: _duplicateCodec.Text.Trim(), ResolutionTier: resolution,
                Reviewed: reviewed, Ignored: ignored, Protected: protection,
                SortColumn: _duplicateSort.Text.ToLowerInvariant(), Descending: true, Offset: _duplicatePage * DuplicatePageSize, Limit: DuplicatePageSize);
        }

        private void RefreshDuplicateLocationFilter(IReadOnlyList<LibraryLocationRecord> locations)
        {
            long selected = _duplicateLocation.SelectedItem is LocationChoice choice ? choice.Id : 0;
            _duplicateLocation.Items.Clear();
            _duplicateLocation.Items.Add(new LocationChoice(0, "All locations"));
            foreach (LibraryLocationRecord location in locations) _duplicateLocation.Items.Add(new LocationChoice(location.Id, location.Path));
            _duplicateLocation.SelectedItem = _duplicateLocation.Items.Cast<LocationChoice>().FirstOrDefault(item => item.Id == selected) ?? _duplicateLocation.Items[0];
        }

        private async void SetManualKeeper_Click(object? sender, EventArgs e)
        {
            if (SelectedGroup() is not ExactDuplicateGroupRecord group || SelectedMember() is not ExactDuplicateMemberRecord member || member.GroupId != group.GroupId) return;
            _runtime.AnalysisCatalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, member.FileId, true, group.Ignored));
            await RefreshDuplicateGroupsAsync();
        }

        private async void ToggleProtection_Click(object? sender, EventArgs e)
        {
            if (SelectedMember() is not ExactDuplicateMemberRecord member) return;
            _runtime.AnalysisCatalog.SetFileProtection(member.FileId, !member.IsProtected, member.IsProtected ? "" : "Protected in Library Analyzer");
            await RefreshDuplicateGroupsAsync();
        }

        private async void MarkReviewed_Click(object? sender, EventArgs e)
        {
            if (SelectedGroup() is not ExactDuplicateGroupRecord group) return;
            _runtime.AnalysisCatalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, group.ManualKeeperFileId, true, group.Ignored));
            await RefreshDuplicateGroupsAsync();
        }

        private async void ToggleIgnored_Click(object? sender, EventArgs e)
        {
            if (SelectedGroup() is not ExactDuplicateGroupRecord group) return;
            _runtime.AnalysisCatalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, group.ManualKeeperFileId, true, !group.Ignored));
            await RefreshDuplicateGroupsAsync();
        }

        private async void PreviewRecycle_Click(object? sender, EventArgs e) => await PreviewCleanupAsync(DuplicateCleanupAction.RecycleBin);
        private async void PreviewQuarantine_Click(object? sender, EventArgs e) => await PreviewCleanupAsync(DuplicateCleanupAction.Quarantine);
        private async void PreviewPreferredCleanup_Click(object? sender, EventArgs e) => await PreviewCleanupAsync(_cleanupOptions.PreferredAction);

        private static string CleanupActionLabel(DuplicateCleanupAction action) => action switch
        {
            DuplicateCleanupAction.RecycleBin => "Recycle Bin",
            DuplicateCleanupAction.Quarantine => "Quarantine",
            _ => "permanent-delete"
        };

        private async Task PreviewCleanupAsync(DuplicateCleanupAction action)
        {
            long[] groupIds = _duplicateGroupsGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => Convert.ToInt64(row.Cells[0].Value)).Distinct().ToArray();
            if (groupIds.Length == 0) return;
            string quarantine = _cleanupOptions.QuarantineFolder;
            if (action == DuplicateCleanupAction.Quarantine && !Directory.Exists(quarantine))
            {
                using var folder = new FolderBrowserDialog { Description = "Choose a quarantine folder", UseDescriptionForTitle = true };
                if (folder.ShowDialog(this) != DialogResult.OK) return;
                quarantine = Path.Combine(folder.SelectedPath, $"MediaFlux Duplicate Quarantine {DateTime.Now:yyyy-MM-dd HHmmss}");
            }
            try
            {
                DuplicateCleanupPlanRecord plan = _runtime.DuplicateCleanup.CreatePlan(groupIds, action, quarantine);
                long bytes = plan.Items.Sum(item => item.SourceSizeBytes);
                string message = $"Cleanup preview\r\n\r\nAction: {action}\r\nFiles: {plan.Items.Count:N0}\r\nPotential storage: {FormatBytes(bytes)}\r\n" +
                                 $"Groups: {plan.Items.Select(item => item.GroupId).Distinct().Count():N0}\r\n\r\n" +
                                 "Every keeper and candidate will be revalidated by path, size, modified time, stable identity, and SHA-256 immediately before action. " +
                                 "Protected, changed, unavailable, and hard-link alias files are excluded.\r\n\r\nExecute this plan?";
                if (action == DuplicateCleanupAction.PermanentDelete)
                    message = "WARNING: This action permanently deletes files and cannot be undone.\r\n\r\n" + message;
                if (MessageBox.Show(this, message, "Confirm exact duplicate cleanup", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                DuplicateCleanupExecutionResult result = await _runtime.DuplicateCleanup.ExecutePlanAsync(plan.PlanId);
                MessageBox.Show(this, $"Cleanup plan {result.PlanId} finished.\r\n\r\nSucceeded: {result.Succeeded:N0}\r\nExcluded by revalidation: {result.Excluded:N0}\r\nFailed: {result.Failed:N0}\r\n\r\nRescan affected locations to reconcile the catalog.",
                    "Library Analyzer cleanup", MessageBoxButtons.OK, result.Failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                await RefreshDuplicateGroupsAsync();
            }
            catch (Exception ex) { ShowError("The cleanup plan could not be completed. No unvalidated files were changed.", ex); }
        }

        private void Duplicates_ProgressChanged(object? sender, LibraryDuplicateAnalysisProgress e)
        {
            _latestDuplicateProgress = e;
        }

        private void UpdateDuplicateActivity(string status, string detail, long completed, long total)
        {
            _duplicateStatus.Text = string.IsNullOrWhiteSpace(detail) ? status : $"{status} · {detail}";
            ConfigureProgress(_duplicateProgress, true, completed, total, completed > 0 && total > 0);
        }

        private ExactDuplicateGroupRecord? SelectedGroup() => _duplicateGroupsGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as ExactDuplicateGroupRecord;
        private ExactDuplicateMemberRecord? SelectedMember() => _duplicateMembersGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as ExactDuplicateMemberRecord;
        private void AddDuplicateGroupColumn(string name, string header, int width, bool visible = true) =>
            _duplicateGroupsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, Visible = visible });
        private void AddDuplicateMemberColumn(string name, string header, int width) =>
            _duplicateMembersGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
    }
}
