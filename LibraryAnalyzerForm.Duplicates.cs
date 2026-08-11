using System.Security.Cryptography;
using System.Diagnostics;
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
        private readonly Label _duplicateReclaimByLocation = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0), ForeColor = SystemColors.GrayText };
        private readonly ContextMenuStrip _duplicateGroupsMenu = new();
        private readonly ContextMenuStrip _duplicateMembersMenu = new();
        private readonly Button _duplicateApplyButton = new() { Name = "DuplicateApplyButton", Text = "Apply", Dock = DockStyle.Top, Height = 30 };
        private readonly TableLayoutPanel _duplicateControlArea = new() { Name = "DuplicateControlArea" };
        private int _duplicatePage;
        private long _duplicateTotal;
        private bool _loadingDuplicateGroups;
        private CancellationTokenSource? _exactCleanupCancellation;
        private int _duplicateMemberLoadVersion;
        private readonly SemaphoreSlim _duplicateMemberRefreshLock = new(1, 1);

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
            AddButton(analysis, "Run Analysis", AnalyzeDuplicates_Click);
            AddButton(analysis, "Pause", (_, _) => _runtime.Duplicates.Pause());
            AddButton(analysis, "Resume", (_, _) => _runtime.Duplicates.Resume());
            AddButton(analysis, "Cancel", (_, _) => _runtime.Duplicates.Cancel());
            AddButton(analysis, "Keeper rules…", ExactKeeperRules_Click);
            AddButton(analysis, "Cancel Cleanup", (_, _) => _exactCleanupCancellation?.Cancel());
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
            AddDuplicateMemberColumn("Created", "Created", 135);
            AddDuplicateMemberColumn("Modified", "Modified", 135);
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
            AddButton(actions, "Select all except keeper", SelectAllExceptKeeper_Click);
            AddButton(actions, "Delete Selected Groups…", DeleteSelectedGroups_Click);
            AddButton(actions, "Delete All Eligible…", DeleteAllEligible_Click);
            AddButton(actions, $"Preview {CleanupActionLabel(_cleanupOptions.PreferredAction)} cleanup…", PreviewPreferredCleanup_Click);

            var pager = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var next = new Button { Text = "Next", AutoSize = true };
            var previous = new Button { Text = "Previous", AutoSize = true };
            next.Click += async (_, _) => { _duplicatePage++; await RefreshDuplicateGroupsAsync(); };
            previous.Click += async (_, _) => { _duplicatePage = Math.Max(0, _duplicatePage - 1); await RefreshDuplicateGroupsAsync(); };
            pager.Controls.Add(next); pager.Controls.Add(previous); pager.Controls.Add(_duplicatePageLabel);

            var status = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false };
            status.Controls.Add(_duplicateProgress); status.Controls.Add(_duplicateStatus); status.Controls.Add(_duplicateReclaimByLocation);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240, Panel1MinSize = 140, Panel2MinSize = 140 };
            split.Panel1.Controls.Add(_duplicateGroupsGrid);
            split.Panel2.Controls.Add(_duplicateMembersGrid);
            tab.Controls.Add(split);
            tab.Controls.Add(status);
            tab.Controls.Add(pager);
            tab.Controls.Add(actions);
            tab.Controls.Add(_duplicateControlArea);
            _tabs.TabPages.Add(tab);
            ConfigureExactContextMenus();
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
                long[] selectedGroupIds = SelectedGroups().Select(group => group.GroupId).ToArray();
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
                foreach (DataGridViewRow row in _duplicateGroupsGrid.Rows)
                    row.Selected = row.Tag is ExactDuplicateGroupRecord group && selectedGroupIds.Contains(group.GroupId);
                if (_duplicateGroupsGrid.SelectedRows.Count == 0 && _duplicateGroupsGrid.Rows.Count > 0)
                    _duplicateGroupsGrid.Rows[0].Selected = true;
                long first = _duplicateTotal == 0 ? 0 : (long)_duplicatePage * DuplicatePageSize + 1;
                long last = Math.Min(_duplicateTotal, ((long)_duplicatePage + 1) * DuplicatePageSize);
                _duplicatePageLabel.Text = $"{first:N0}–{last:N0} of {_duplicateTotal:N0}";
                await RefreshExactReclaimByLocationAsync();
                await RefreshDuplicateMembersAsync();
            }
            finally { _loadingDuplicateGroups = false; }
        }

        private async Task RefreshExactReclaimByLocationAsync()
        {
            IReadOnlyList<ExactDuplicateReclaimLocation> locations = await Task.Run(() => _runtime.AnalysisCatalog.GetExactDuplicateReclaimByLocation());
            if (IsDisposed) return;
            _duplicateReclaimByLocation.Text = locations.Count == 0
                ? "No reclaimable exact copies"
                : string.Join("  ·  ", locations.Take(3).Select(item => $"{CompactLocation(item.LocationPath)}: {FormatBytes(item.ReclaimableBytes)}")) +
                  (locations.Count > 3 ? $"  ·  +{locations.Count - 3:N0} more" : "");
        }

        private static string CompactLocation(string path)
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrWhiteSpace(root) ? path : root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        private async Task RefreshDuplicateMembersAsync()
        {
            int version = Interlocked.Increment(ref _duplicateMemberLoadVersion);
            await _duplicateMemberRefreshLock.WaitAsync();
            try
            {
                if (version != Volatile.Read(ref _duplicateMemberLoadVersion) || IsDisposed) return;
                if (_duplicateGroupsGrid.SelectedRows.Count == 0) { _duplicateMembersGrid.Rows.Clear(); return; }
                long groupId = Convert.ToInt64(_duplicateGroupsGrid.SelectedRows[0].Cells[0].Value);
                LibraryMatchEligibility eligibility = await Task.Run(() => _runtime.MatchEligibility.EvaluateExactGroup(groupId));
                if (version != Volatile.Read(ref _duplicateMemberLoadVersion) || IsDisposed) return;
                if (!eligibility.IsActive)
                {
                    _duplicateMembersGrid.Rows.Clear();
                    _duplicateStatus.Text = $"Match suspended: {eligibility.Reason}";
                    if (!_loadingDuplicateGroups) await RefreshDuplicateGroupsAsync();
                    return;
                }
                IReadOnlyList<ExactDuplicateMemberRecord> members = await Task.Run(() => _runtime.AnalysisCatalog.GetDuplicateGroupMembers(groupId));
                if (version != Volatile.Read(ref _duplicateMemberLoadVersion) || IsDisposed) return;
                _duplicateMembersGrid.Rows.Clear();
                foreach (ExactDuplicateMemberRecord member in members)
                {
                    string keeper = member.IsManualKeeper ? "Manual" : member.IsSuggestedKeeper ? "Suggested" : member.IsHardLinkAlias ? "Hard-link alias" : "Candidate";
                    int row = _duplicateMembersGrid.Rows.Add(keeper, member.IsProtected ? "Yes" : "No", member.FullPath, member.LocationPath,
                        FormatBytes(member.SizeBytes), member.CreationUtc?.ToLocalTime().ToString("g") ?? "Unknown", member.LastWriteUtc.ToLocalTime().ToString("g"),
                        member.VideoCodec, member.Width.HasValue && member.Height.HasValue ? $"{member.Width}×{member.Height}" : "",
                        member.TotalBitRate.HasValue ? $"{member.TotalBitRate / 1_000_000d:0.##} Mbps" : "", member.PhysicalIdentityKey);
                    _duplicateMembersGrid.Rows[row].Tag = member;
                }
            }
            finally { _duplicateMemberRefreshLock.Release(); }
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

        private void ConfigureExactContextMenus()
        {
            AddVisualMenuItem(_duplicateMembersMenu, "Set as Keeper", "Keeper", () => { SetManualKeeper_Click(null, EventArgs.Empty); return Task.CompletedTask; });
            AddVisualMenuItem(_duplicateMembersMenu, "Protect", "Protect", () => { ToggleProtection_Click(null, EventArgs.Empty); return Task.CompletedTask; });
            AddVisualMenuItem(_duplicateMembersMenu, "Play Video", "Play", () => { if (SelectedMember() is { } m) PlayLibraryVideo(m.FullPath); return Task.CompletedTask; });
            AddVisualMenuItem(_duplicateMembersMenu, "Open File Location", "Folder", () => { if (SelectedMember() is { } m) OpenLibraryFileLocation(m.FullPath); return Task.CompletedTask; });
            _duplicateMembersMenu.Items.Add(new ToolStripSeparator());
            AddVisualMenuItem(_duplicateMembersMenu, "Select All Except Keeper", "SelectOthers", () => { SelectAllExceptKeeper(); return Task.CompletedTask; });
            _duplicateMembersMenu.Opening += DuplicateMembersMenu_Opening;
            AttachVisualContextMenu(_duplicateMembersGrid, _duplicateMembersMenu);

            AddVisualMenuItem(_duplicateGroupsMenu, "Mark Reviewed", "Reviewed", async () => await MarkSelectedExactGroupsReviewedAsync());
            AddVisualMenuItem(_duplicateGroupsMenu, "Ignore Group", "Ignore", async () => await ToggleSelectedExactGroupsIgnoredAsync());
            AddVisualMenuItem(_duplicateGroupsMenu, "Re-analyze Group", "Reanalyze", () => { QueueSelectedExactGroup_Click(null, EventArgs.Empty); return Task.CompletedTask; });
            _duplicateGroupsMenu.Items.Add(new ToolStripSeparator());
            AddVisualMenuItem(_duplicateGroupsMenu, "Delete Duplicates in Group…", "Delete", async () => await PreviewCleanupAsync(SelectedGroupIds(), DuplicateCleanupAction.PermanentDelete));
            AddVisualMenuItem(_duplicateGroupsMenu, "Protect Keeper", "ProtectKeeper", async () => await ProtectSelectedExactKeeperAsync());
            AddVisualMenuItem(_duplicateGroupsMenu, "Open Keeper Location", "OpenKeeper", () => { OpenSelectedExactKeeperLocation(); return Task.CompletedTask; });
            _duplicateGroupsMenu.Opening += DuplicateGroupsMenu_Opening;
            AttachVisualContextMenu(_duplicateGroupsGrid, _duplicateGroupsMenu);
        }

        private void DuplicateMembersMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            ExactDuplicateMemberRecord? member = SelectedMember();
            ExactDuplicateGroupRecord? group = SelectedGroup();
            bool valid = member != null && group != null && member.GroupId == group.GroupId;
            SetMenuState(_duplicateMembersMenu, "Keeper", valid && !member!.IsManualKeeper);
            SetMenuState(_duplicateMembersMenu, "Protect", valid, member?.IsProtected == true ? "Unprotect" : "Protect");
            SetMenuState(_duplicateMembersMenu, "Play", valid && File.Exists(member!.FullPath));
            SetMenuState(_duplicateMembersMenu, "Folder", valid && Directory.Exists(Path.GetDirectoryName(member!.FullPath)));
            SetMenuState(_duplicateMembersMenu, "SelectOthers", group != null && _duplicateMembersGrid.Rows.Count > 1);
            e.Cancel = !valid;
        }

        private void DuplicateGroupsMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            ExactDuplicateGroupRecord[] groups = SelectedGroups();
            ExactDuplicateGroupRecord? first = groups.FirstOrDefault();
            SetMenuState(_duplicateGroupsMenu, "Reviewed", groups.Any(group => !group.Reviewed));
            SetMenuState(_duplicateGroupsMenu, "Ignore", groups.Length > 0, first?.Ignored == true ? "Restore Group" : "Ignore Group");
            SetMenuState(_duplicateGroupsMenu, "Reanalyze", groups.Length > 0);
            SetMenuState(_duplicateGroupsMenu, "Delete", groups.Length > 0 && groups.Any(group => !group.Ignored), groups.Length > 1 ? "Delete Selected Groups…" : "Delete Duplicates in Group…");
            SetMenuState(_duplicateGroupsMenu, "ProtectKeeper", groups.Length == 1);
            SetMenuState(_duplicateGroupsMenu, "OpenKeeper", groups.Length == 1);
            e.Cancel = groups.Length == 0;
        }

        private async Task MarkSelectedExactGroupsReviewedAsync()
        {
            foreach (ExactDuplicateGroupRecord group in SelectedGroups())
                _runtime.AnalysisCatalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, group.ManualKeeperFileId, true, group.Ignored));
            await RefreshDuplicateGroupsAsync();
        }

        private async Task ToggleSelectedExactGroupsIgnoredAsync()
        {
            ExactDuplicateGroupRecord[] groups = SelectedGroups();
            if (groups.Length == 0) return;
            bool ignored = !groups[0].Ignored;
            foreach (ExactDuplicateGroupRecord group in groups)
                _runtime.AnalysisCatalog.SaveDuplicateDecision(new DuplicateGroupDecision(group.GroupId, group.ManualKeeperFileId, true, ignored));
            await RefreshDuplicateGroupsAsync();
        }

        private ExactDuplicateMemberRecord? KeeperForGroup(long groupId)
        {
            IReadOnlyList<ExactDuplicateMemberRecord> members = _runtime.AnalysisCatalog.GetDuplicateGroupMembers(groupId);
            return members.Count == 0 ? null : ExactDuplicateKeeperPolicy.Select(members, _visualKeeperPreferences).Keeper;
        }

        private async Task ProtectSelectedExactKeeperAsync()
        {
            if (SelectedGroup() is not { } group || KeeperForGroup(group.GroupId) is not { } keeper) return;
            _runtime.AnalysisCatalog.SetFileProtection(keeper.FileId, true, "Exact duplicate keeper protected in Library Analyzer");
            await RefreshDuplicateGroupsAsync();
        }

        private void OpenSelectedExactKeeperLocation()
        {
            if (SelectedGroup() is { } group && KeeperForGroup(group.GroupId) is { } keeper)
                OpenLibraryFileLocation(keeper.FullPath);
        }

        private async void ExactKeeperRules_Click(object? sender, EventArgs e)
        {
            using var dialog = new DuplicateKeeperPreferencesForm(_visualKeeperPreferences);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _visualKeeperPreferences = dialog.Preferences.Clone();
            _reviewOptions.KeeperPreferencesChanged?.Invoke(_visualKeeperPreferences.Clone());
            await Task.Run(() =>
            {
                _runtime.UpdateExactKeeperPreferences(_visualKeeperPreferences);
                _runtime.Duplicates.RefreshKeeperRecommendations();
            });
            await RefreshDuplicateGroupsAsync();
        }

        private void SelectAllExceptKeeper_Click(object? sender, EventArgs e) => SelectAllExceptKeeper();

        private void SelectAllExceptKeeper()
        {
            if (SelectedGroup() is not { } group || KeeperForGroup(group.GroupId) is not { } keeper) return;
            IReadOnlyList<long> selectedIds = ExactDuplicateSelectionPolicy.SelectAllExceptKeeper(
                _duplicateMembersGrid.Rows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<ExactDuplicateMemberRecord>().ToArray(), keeper.FileId);
            _duplicateMembersGrid.ClearSelection();
            foreach (DataGridViewRow row in _duplicateMembersGrid.Rows)
                row.Selected = row.Tag is ExactDuplicateMemberRecord member && selectedIds.Contains(member.FileId);
        }

        private async void DeleteSelectedGroups_Click(object? sender, EventArgs e) =>
            await PreviewCleanupAsync(SelectedGroupIds(), DuplicateCleanupAction.PermanentDelete);

        private async void DeleteAllEligible_Click(object? sender, EventArgs e) =>
            await PreviewCleanupAsync(null, DuplicateCleanupAction.PermanentDelete);

        private async void PreviewRecycle_Click(object? sender, EventArgs e) => await PreviewCleanupAsync(DuplicateCleanupAction.RecycleBin);
        private async void PreviewQuarantine_Click(object? sender, EventArgs e) => await PreviewCleanupAsync(DuplicateCleanupAction.Quarantine);
        private async void PreviewPreferredCleanup_Click(object? sender, EventArgs e) => await PreviewCleanupAsync(_cleanupOptions.PreferredAction);

        private static string CleanupActionLabel(DuplicateCleanupAction action) => action switch
        {
            DuplicateCleanupAction.RecycleBin => "Recycle Bin",
            DuplicateCleanupAction.Quarantine => "Quarantine",
            _ => "permanent-delete"
        };

        private async Task PreviewCleanupAsync(DuplicateCleanupAction action) =>
            await PreviewCleanupAsync(SelectedGroupIds(), action);

        private async Task PreviewCleanupAsync(IReadOnlyCollection<long>? groupIds, DuplicateCleanupAction action)
        {
            if (groupIds is { Count: 0 }) return;
            string quarantine = _cleanupOptions.QuarantineFolder;
            if (action == DuplicateCleanupAction.Quarantine && !Directory.Exists(quarantine))
            {
                using var folder = new FolderBrowserDialog { Description = "Choose a quarantine folder", UseDescriptionForTitle = true };
                if (folder.ShowDialog(this) != DialogResult.OK) return;
                quarantine = Path.Combine(folder.SelectedPath, $"MediaFlux Duplicate Quarantine {DateTime.Now:yyyy-MM-dd HHmmss}");
            }
            _exactCleanupCancellation?.Dispose();
            _exactCleanupCancellation = new CancellationTokenSource();
            _duplicateProgress.Visible = true;
            try
            {
                CancellationToken token = _exactCleanupCancellation.Token;
                DuplicateCleanupPlanSummary plan = await Task.Run(() => groupIds == null
                    ? _runtime.DuplicateCleanup.CreatePlanForAllEligible(action, quarantine, token)
                    : _runtime.DuplicateCleanup.CreatePlan(groupIds, action, quarantine, token), token);
                string locationSummary = BuildCleanupLocationSummary(plan);
                string message = $"Cleanup preview\r\n\r\nAction: {action}\r\n\r\n{plan.TotalGroups:N0} groups\r\n{plan.TotalItems:N0} files to remove\r\n" +
                                 $"{plan.TotalGroups:N0} keepers retained\r\n0 protected files affected\r\n{FormatBytes(plan.PlannedBytes)} reclaimable\r\n" + locationSummary + "\r\n" +
                                 "Every keeper and candidate will be revalidated by path, size, modified time, stable identity, and SHA-256 immediately before action. " +
                                 "Protected, changed, unavailable, and hard-link alias files are excluded.\r\n\r\nExecute this plan?";
                if (action == DuplicateCleanupAction.PermanentDelete)
                    message = "WARNING: This action permanently deletes files and cannot be undone.\r\n\r\n" + message;
                if (MessageBox.Show(this, message, "Confirm exact duplicate cleanup", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                var progress = new Progress<DuplicateCleanupProgress>(value =>
                    _duplicateStatus.Text = $"Cleanup {value.ProcessedItems:N0}/{value.TotalItems:N0} · {FormatBytes(value.ReclaimedBytes)} reclaimed");
                DuplicateCleanupExecutionResult result = await Task.Run(
                    async () => await _runtime.DuplicateCleanup.ExecutePlanAsync(plan.PlanId, token, progress).ConfigureAwait(false), token);
                MessageBox.Show(this, $"Cleanup plan {result.PlanId} finished.\r\n\r\nSucceeded: {result.Succeeded:N0}\r\nExcluded by revalidation: {result.Excluded:N0}\r\nFailed: {result.Failed:N0}\r\nActual reclaimed: {FormatBytes(result.ReclaimedBytes)}\r\n" +
                    (string.IsNullOrWhiteSpace(result.ErrorText) ? "" : $"Status: {result.ErrorText}\r\n") + "\r\nRescan affected locations to reconcile the catalog.",
                    "Library Analyzer cleanup", MessageBoxButtons.OK,
                    result.Failed == 0 && string.IsNullOrWhiteSpace(result.ErrorText) ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                await RefreshDuplicateGroupsAsync();
                await RefreshOverviewAsync();
                await RefreshLocationsAsync();
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show(this, "Cleanup planning was canceled. No unvalidated file was changed.", "Library Analyzer cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError("The cleanup plan could not be completed. No unvalidated files were changed.", ex); }
            finally
            {
                _duplicateProgress.Visible = false;
                _exactCleanupCancellation?.Dispose();
                _exactCleanupCancellation = null;
            }
        }

        private static string BuildCleanupLocationSummary(DuplicateCleanupPlanSummary plan)
        {
            if (plan.Locations.Count == 0) return "";
            return "\r\nBy location:\r\n" + string.Join("\r\n", plan.Locations.Select(item => $"  {item.LocationPath} — {FormatBytes(item.ReclaimableBytes)}")) + "\r\n";
        }

        private void PlayLibraryVideo(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                if (_reviewOptions.VideoLauncher != null) _reviewOptions.VideoLauncher(path);
                else if (!string.IsNullOrWhiteSpace(_reviewOptions.ExternalPlayerPath) && File.Exists(_reviewOptions.ExternalPlayerPath))
                    Process.Start(new ProcessStartInfo { FileName = _reviewOptions.ExternalPlayerPath, Arguments = $"\"{path}\"", UseShellExecute = true });
                else Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MediaFlux.Services.ErrorLogService.Append(Application.StartupPath, "Open Library Analyzer video failed", path, ex);
                MessageBox.Show(this, "The selected video could not be opened.\r\n\r\n" + ex.Message, "Library Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenLibraryFileLocation(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{directory}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MediaFlux.Services.ErrorLogService.Append(Application.StartupPath, "Open Library Analyzer file location failed", path, ex);
                MessageBox.Show(this, "The containing folder could not be opened.", "Library Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

        private ExactDuplicateGroupRecord[] SelectedGroups() => _duplicateGroupsGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<ExactDuplicateGroupRecord>().ToArray();
        private long[] SelectedGroupIds() => SelectedGroups().Select(group => group.GroupId).Distinct().ToArray();
        private ExactDuplicateGroupRecord? SelectedGroup() => SelectedGroups().FirstOrDefault();
        private ExactDuplicateMemberRecord? SelectedMember() => _duplicateMembersGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as ExactDuplicateMemberRecord;
        private void AddDuplicateGroupColumn(string name, string header, int width, bool visible = true) =>
            _duplicateGroupsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, Visible = visible });
        private void AddDuplicateMemberColumn(string name, string header, int width) =>
            _duplicateMembersGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
    }
}
