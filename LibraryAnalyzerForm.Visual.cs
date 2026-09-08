using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private const int VisualPageSize = 100;
        private readonly DataGridView _visualGroupsGrid = CreateGrid();
        private readonly DataGridView _visualMembersGrid = CreateGrid();
        private readonly TextBox _visualSearch = new() { Width = 220, PlaceholderText = "Path or filename" };
        private readonly ComboBox _visualLocation = DropDown();
        private readonly ComboBox _visualReview = DropDown();
        private readonly ComboBox _visualCodecDifference = DropDown();
        private readonly ComboBox _visualResolutionDifference = DropDown();
        private readonly ComboBox _visualSort = DropDown();
        private readonly NumericUpDown _visualConfidence = new() { Width = 75, Minimum = 0, Maximum = 100, Value = 76 };
        private readonly Label _visualStatus = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0), Text = "Visual similarity analysis has not run." };
        private readonly Label _visualPageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
        private readonly ProgressBar _visualProgress = new() { Width = 180, Style = ProgressBarStyle.Marquee, Visible = false };
        private readonly CheckBox _visualComparisonPreviewEnabled = new() { Name = "VisualComparisonPreviewEnabled", Text = "Show Side-by-Side Preview", AutoSize = true };
        private readonly ToolTip _visualActionToolTip = new();
        private readonly Label _visualReviewGuidance = new() { Name = "VisualReviewGuidance", AutoSize = true, Text = "Select one of the two files above, then choose what MediaFlux should do with it." };
        private Button? _visualKeepButton;
        private Button? _visualReviewCompareButton;
        private Button? _visualProtectionButton;
        private Button? _visualReviewedButton;
        private Button? _visualIgnoredButton;
        private Button? _visualRecheckButton;
        private Button? _visualPreviewCleanupButton;
        private Button? _visualBulkCleanupButton;
        private Button? _visualRulesReviewButton;
        private Button? _visualDeleteBothButton;
        // The top half of the tab keeps results and the optional preview side-by-side.
        private readonly SplitContainer _visualDetailSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        private readonly SplitContainer _visualResultsMembersSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240, Panel1MinSize = 140, Panel2MinSize = 140 };
        private readonly Panel _visualComparisonPreview = new() { Dock = DockStyle.Fill, Visible = false };
        private readonly ContextMenuStrip _visualGroupsMenu = new();
        private readonly ContextMenuStrip _visualMembersMenu = new();
        private readonly Button _visualApplyButton = new() { Name = "VisualApplyButton", Text = "Apply", Dock = DockStyle.Top, Height = 30 };
        private readonly TableLayoutPanel _visualControlArea = new() { Name = "VisualControlArea" };
        private int _visualPage;
        private long _visualTotal;
        private bool _loadingVisualGroups;
        private DuplicateReviewSelectionAnchor? _visualAdvanceAfterRefresh;
        private int _visualMemberLoadVersion;
        private readonly SemaphoreSlim _visualMemberRefreshLock = new(1, 1);

        private void BuildVisualSimilarityTab()
        {
            var tab = new TabPage("Duplicates — Visual") { Padding = new Padding(8) };
            _visualStatus.ForeColor = LibraryAnalyzerAccentColor;
            _visualControlArea.Dock = DockStyle.Top;
            _visualControlArea.Height = 142;
            _visualControlArea.ColumnCount = 1;
            _visualControlArea.RowCount = 2;
            _visualControlArea.Margin = Padding.Empty;
            _visualControlArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            _visualControlArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var analysis = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, Padding = new Padding(0, 4, 0, 2) };
            AddButton(analysis, "Analyze Library", AnalyzeVisualSimilarity_Click);
            AddButton(analysis, "Pause", (_, _) => _runtime.VisualSimilarity.Pause());
            AddButton(analysis, "Resume", (_, _) => _runtime.VisualSimilarity.Resume());
            AddButton(analysis, "Cancel", (_, _) => _runtime.VisualSimilarity.Cancel());
            AddButton(analysis, "Configure File Selection Rules…", VisualKeeperRules_Click);
            _visualComparisonPreviewEnabled.Checked = _reviewOptions.UiState?.ShowVisualComparisonPreview == true;
            _visualComparisonPreviewEnabled.CheckedChanged += async (_, _) => await ToggleVisualComparisonPreviewAsync();
            analysis.Controls.Add(_visualComparisonPreviewEnabled);
            _visualControlArea.Controls.Add(analysis, 0, 0);

            var filtersBox = new GroupBox { Text = "Filters", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 7) };
            var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, Margin = Padding.Empty };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            AddVisualFilter(filters, "Search", _visualSearch, 0, 0, 2);
            AddVisualFilter(filters, "Location", _visualLocation, 2, 0);
            AddVisualFilter(filters, "Review state", _visualReview, 3, 0);
            AddVisualFilter(filters, "Minimum confidence", _visualConfidence, 0, 1);
            AddVisualFilter(filters, "Codec", _visualCodecDifference, 1, 1);
            AddVisualFilter(filters, "Resolution", _visualResolutionDifference, 2, 1);
            AddVisualFilter(filters, "Sort", _visualSort, 3, 1);
            var filterActions = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(5, 0, 0, 0) };
            filterActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            filterActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _visualApplyButton.Click += async (_, _) => { _visualPage = 0; await RefreshVisualGroupsAsync(); };
            var reset = new Button { Text = "Reset", Dock = DockStyle.Top, Height = 30 };
            reset.Click += ResetVisualFilters_Click;
            filterActions.Controls.Add(_visualApplyButton, 0, 0);
            filterActions.Controls.Add(reset, 0, 1);
            filters.Controls.Add(filterActions, 4, 0);
            filters.SetRowSpan(filterActions, 2);
            filtersBox.Controls.Add(filters);
            _visualControlArea.Controls.Add(filtersBox, 0, 1);

            _visualLocation.Items.Add(new LocationChoice(0, "All locations"));
            _visualLocation.SelectedIndex = 0;
            _visualReview.Items.AddRange(new object[] { "Active", "Unreviewed", "Reviewed", "Ignored", "Not a match", "All including failed matches" });
            _visualReview.SelectedIndex = 0;
            _visualCodecDifference.Items.AddRange(new object[] { "All", "Different", "Same" });
            _visualCodecDifference.SelectedIndex = 0;
            _visualResolutionDifference.Items.AddRange(new object[] { "All", "Different", "Same" });
            _visualResolutionDifference.SelectedIndex = 0;
            _visualSort.Items.AddRange(new object[] { "Confidence", "Reclaimable", "Duration", "Reviewed" });
            _visualSort.SelectedIndex = 0;

            AddVisualGroupColumn("Id", "Id", 50, false);
            AddVisualGroupColumn("Type", "Match type", 105);
            AddVisualGroupColumn("Confidence", "Confidence", 85);
            AddVisualGroupColumn("Reclaimable", "Potential space", 100);
            AddVisualGroupColumn("Duration", "Duration delta", 95);
            AddVisualGroupColumn("Codec", "Codec", 75);
            AddVisualGroupColumn("Resolution", "Resolution", 85);
            AddVisualGroupColumn("Review", "Review state", 95);
            AddVisualGroupColumn("Evidence", "Evidence", 430);
            _visualGroupsGrid.MultiSelect = true;
            _visualGroupsGrid.SelectionChanged += async (_, _) =>
            {
                await RefreshVisualMembersAsync();
            };
            _visualGroupsGrid.CellDoubleClick += VisualGroupsGrid_CellDoubleClick;
            _visualGroupsGrid.KeyDown += async (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A)
                {
                    foreach (DataGridViewRow row in _visualGroupsGrid.Rows)
                        row.Selected = true;
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode != Keys.Enter)
                    return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                await OpenVisualReviewAsync();
            };

            AddVisualMemberColumn("Keeper", "Cleanup Role", 135);
            AddVisualMemberColumn("Protected", "Protected", 70);
            AddVisualMemberColumn("Path", "File location", 400);
            AddVisualMemberColumn("Root", "Root", 180);
            AddVisualMemberColumn("Size", "Size", 85);
            AddVisualMemberColumn("Codec", "Codec", 75);
            AddVisualMemberColumn("Resolution", "Resolution", 85);
            AddVisualMemberColumn("Bitrate", "Bitrate", 90);
            AddVisualMemberColumn("Duration", "Duration", 85);
            AddVisualMemberColumn("Availability", "Availability", 90);
            _visualMembersGrid.MultiSelect = false;
            _visualMembersGrid.SelectionChanged += (_, _) => UpdateVisualActionState();
            _visualMembersGrid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex >= 0)
                    PlaySelectedVisualMember();
            };
            _visualMembersGrid.KeyDown += VisualMembersGrid_KeyDown;
            ConfigureVisualContextMenus();

            var notice = new Label
            {
                Name = "VisualSafetyNotice",
                Dock = DockStyle.Bottom,
                Height = 36,
                Text = "Visual matches are probabilistic. Cleanup always requires a preview, explicit confirmation, and keeper/candidate revalidation.",
                ForeColor = LibraryAnalyzerAccentColor,
                Padding = new Padding(8, 9, 0, 0)
            };
            var actions = new TableLayoutPanel { Name = "VisualActionArea", Dock = DockStyle.Bottom, Height = 220, RowCount = 2, ColumnCount = 2, Padding = new Padding(0, 2, 0, 2) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 162));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

            GroupBox reviewBox = CreateVisualActionGroup("REVIEW SELECTED MATCH", out TableLayoutPanel reviewLayout);
            reviewLayout.RowCount = 2;
            reviewLayout.RowStyles.Clear();
            reviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            reviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _visualReviewGuidance.ForeColor = SystemColors.GrayText;
            reviewLayout.Controls.Add(_visualReviewGuidance, 0, 0);
            var reviewActions = CreateVisualActionFlow();
            _visualReviewCompareButton = AddVisualActionButton(
                reviewActions,
                "Review & Compare…",
                ReviewAndCompareSelectedVisualMatch_Click,
                "Open the Review & Compare window to inspect the matched files side by side before deciding which file to keep.");
            _visualReviewCompareButton.Name = "VisualReviewCompareButton";
            _visualReviewCompareButton.Font = new Font(_visualReviewCompareButton.Font, FontStyle.Bold);
            _visualKeepButton = AddVisualActionButton(reviewActions, "Keep Selected File", SetVisualKeeper_Click,
                "Choose the selected file as the keeper for this match. This changes the review decision; it does not delete anything.");
            _visualProtectionButton = AddVisualActionButton(reviewActions, "Protect Selected File", ToggleVisualProtection_Click,
                "Protect or remove protection for the selected file. Protected files are respected by existing cleanup eligibility rules.");
            _visualProtectionButton.Name = "VisualProtectionButton";
            _visualReviewedButton = AddVisualActionButton(reviewActions, "Mark Match as Reviewed", MarkVisualReviewed_Click,
                "Mark this match as reviewed without changing its files or running cleanup.");
            _visualIgnoredButton = AddVisualActionButton(reviewActions, "Ignore This Match", ToggleVisualIgnored_Click,
                "Ignore this match in the analyzer. Restoring it makes the match active again; neither action deletes files.");
            _visualIgnoredButton.Name = "VisualIgnoredButton";
            _visualRecheckButton = AddVisualActionButton(reviewActions, "Recheck This Match", QueueSelectedVisualGroup_Click,
                "Queue the files in this match for visual re-analysis. Existing decisions and files are not deleted by this action.");
            reviewLayout.Controls.Add(reviewActions, 0, 1);

            GroupBox cleanupBox = CreateVisualActionGroup("CLEANUP", out TableLayoutPanel cleanupLayout);
            var cleanupActions = CreateVisualActionFlow();
            _visualPreviewCleanupButton = AddVisualActionButton(cleanupActions, "Preview Files to Delete…", ReviewSelectedVisualCleanup_Click,
                "Open the existing cleanup preview for the selected match. The current preview, confirmation, and revalidation safeguards still apply.");
            _visualBulkCleanupButton = AddVisualActionButton(cleanupActions, "Remove Recommended Duplicates…", ReviewBulkVisualCleanup_Click,
                "Review removal of eligible recommended duplicate candidates. Existing cleanup preview, confirmation, and validation safeguards remain in effect.");
            cleanupLayout.Controls.Add(cleanupActions, 0, 0);

            GroupBox automationBox = CreateVisualActionGroup("AUTOMATION", out TableLayoutPanel automationLayout);
            var automationActions = CreateVisualActionFlow();
            _visualRulesReviewButton = AddVisualActionButton(automationActions, "Review Matches Using File Selection Rules…", async (_, _) => await PreviewMassReviewAsync(),
                "Preview matches selected by the configured file selection rules. Review decisions are shown before they are applied.");
            automationLayout.Controls.Add(automationActions, 0, 0);

            GroupBox advancedBox = CreateVisualActionGroup("ADVANCED / DESTRUCTIVE", out TableLayoutPanel advancedLayout);
            var advancedActions = CreateVisualActionFlow();
            _visualDeleteBothButton = AddVisualActionButton(advancedActions, "Delete Both Files…", DeleteBothVisual_Click,
                "Open the existing destructive delete-both workflow. The cleanup preview, confirmation, and revalidation safeguards still apply.");
            advancedLayout.Controls.Add(advancedActions, 0, 0);
            actions.Controls.Add(reviewBox, 0, 0);
            actions.Controls.Add(cleanupBox, 1, 0);
            actions.Controls.Add(automationBox, 0, 1);
            actions.Controls.Add(advancedBox, 1, 1);
            UpdateVisualActionState();

            var pager = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var next = new Button { Text = "Next", AutoSize = true };
            var previous = new Button { Text = "Previous", AutoSize = true };
            next.Click += async (_, _) => { _visualPage++; await RefreshVisualGroupsAsync(); };
            previous.Click += async (_, _) => { _visualPage = Math.Max(0, _visualPage - 1); await RefreshVisualGroupsAsync(); };
            pager.Controls.Add(next); pager.Controls.Add(previous); pager.Controls.Add(_visualPageLabel);
            var status = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false };
            status.Controls.Add(_visualProgress); status.Controls.Add(_visualStatus);
            _visualDetailSplit.Panel1.Controls.Add(_visualGroupsGrid);
            BuildVisualComparisonPreview();
            _visualDetailSplit.Panel2.Controls.Add(_visualComparisonPreview);
            _visualResultsMembersSplit.Panel1.Controls.Add(_visualDetailSplit);
            _visualResultsMembersSplit.Panel2.Controls.Add(_visualMembersGrid);
            tab.Controls.Add(_visualResultsMembersSplit);
            tab.Controls.Add(status);
            tab.Controls.Add(pager);
            tab.Controls.Add(actions);
            tab.Controls.Add(notice);
            tab.Controls.Add(_visualControlArea);
            _tabs.TabPages.Add(tab);
        }

        private static void AddVisualFilter(TableLayoutPanel layout, string label, Control control, int column, int row, int columnSpan = 1)
        {
            var cell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(3, 0, 6, 0) };
            cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cell.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 1) }, 0, 0);
            control.Dock = DockStyle.Fill;
            control.Margin = Padding.Empty;
            cell.Controls.Add(control, 0, 1);
            layout.Controls.Add(cell, column, row);
            if (columnSpan > 1) layout.SetColumnSpan(cell, columnSpan);
        }

        private static GroupBox CreateVisualActionGroup(string title, out TableLayoutPanel layout)
        {
            var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(7, 4, 7, 2), Margin = new Padding(0, 1, 0, 1) };
            layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            group.Controls.Add(layout);
            return group;
        }

        private static FlowLayoutPanel CreateVisualActionFlow() => new()
        {
            Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false, FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 1)
        };

        private Button AddVisualActionButton(FlowLayoutPanel parent, string text, EventHandler handler, string toolTip)
        {
            Button button = AddButton(parent, text, handler);
            _visualActionToolTip.SetToolTip(button, toolTip);
            return button;
        }

        private async void ResetVisualFilters_Click(object? sender, EventArgs e)
        {
            _visualSearch.Clear();
            _visualLocation.SelectedIndex = _visualLocation.Items.Count > 0 ? 0 : -1;
            _visualConfidence.Value = 76;
            _visualReview.SelectedIndex = 0;
            _visualCodecDifference.SelectedIndex = 0;
            _visualResolutionDifference.SelectedIndex = 0;
            _visualSort.SelectedIndex = 0;
            _visualPage = 0;
            await RefreshVisualGroupsAsync();
        }

        private async void VisualKeeperRules_Click(object? sender, EventArgs e)
        {
            using var dialog = new DuplicateKeeperPreferencesForm(_visualKeeperPreferences, DuplicateKeeperScoringContext.Visual);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _visualKeeperPreferences = dialog.Preferences.Clone();
            _reviewOptions.KeeperPreferencesChanged?.Invoke(_visualKeeperPreferences.Clone());
            await Task.Run(() =>
            {
                _runtime.UpdateVisualKeeperPreferences(_visualKeeperPreferences);
                _runtime.UpdateExactKeeperPreferences(_visualKeeperPreferences);
            });
            await RefreshVisualGroupsAsync(SelectedVisualGroup()?.GroupId);
        }

        private async void AnalyzeVisualSimilarity_Click(object? sender, EventArgs e)
        {
            if (_runtime.VisualSimilarity.IsRunning) return;
            _visualProgress.Visible = true;
            try
            {
                LibraryVisualAnalysisResult result = await _runtime.VisualSimilarity.AnalyzeAsync();
                _visualStatus.Text = result.Status == DuplicateAnalysisStatus.Completed
                    ? $"Completed: {result.MatchPairs:N0} review pairs from {result.CandidatePairs:N0} indexed candidates; {result.FingerprintedFiles:N0} files fingerprinted."
                    : $"{result.Status}: {result.ErrorText}";
                _visualPage = 0;
                await RefreshVisualGroupsAsync();
            }
            catch (Exception ex) { ShowError("Visual similarity analysis failed. No media files were changed.", ex); }
            finally { _visualProgress.Visible = false; }
        }

        private void QueueSelectedVisualGroup_Click(object? sender, EventArgs e)
        {
            long[] fileIds = SelectedVisualGroups()
                .SelectMany(group => _runtime.VisualCatalog.GetVisualGroupMembers(group.GroupId))
                .Select(member => member.FileId)
                .Distinct()
                .ToArray();
            if (fileIds.Length > 0) _runtime.Reanalysis.QueueFiles(fileIds, LibraryReanalysisWork.VisualFingerprint);
        }

        private async Task RefreshVisualGroupsAsync(long? preferredGroupId = null)
        {
            if (_loadingVisualGroups || IsDisposed) return;
            _loadingVisualGroups = true;
            try
            {
                DuplicateReviewSelectionAnchor? advanceAfter = _visualAdvanceAfterRefresh;
                _visualAdvanceAfterRefresh = null;
                preferredGroupId ??= SelectedVisualGroup()?.GroupId;
                long[] selectedGroupIds = SelectedVisualGroups().Select(group => group.GroupId).ToArray();
                VisualGroupQuery query = BuildVisualQuery();
                VisualSimilarityGroupPage page = await Task.Run(() => _runtime.VisualCatalog.QueryVisualGroups(query));
                if (IsDisposed || Disposing || _visualGroupsGrid.IsDisposed) return;
                if (page.TotalCount > 0 && page.Groups.Count == 0 && _visualPage > 0)
                {
                    _visualPage = (int)((page.TotalCount - 1) / VisualPageSize);
                    query = BuildVisualQuery();
                    page = await Task.Run(() => _runtime.VisualCatalog.QueryVisualGroups(query));
                    if (IsDisposed || Disposing || _visualGroupsGrid.IsDisposed) return;
                }
                _visualTotal = page.TotalCount;
                _visualGroupsGrid.Rows.Clear();
                foreach (VisualSimilarityGroupRecord group in page.Groups)
                {
                    int row = _visualGroupsGrid.Rows.Add(group.GroupId, "Visual / similar", $"{group.ConfidenceScore:0.0}%", FormatBytes(group.ReclaimableBytes),
                        $"{group.DurationDeltaSeconds:0.###} s", group.CodecDiffers ? "Different" : "Same", group.ResolutionDiffers ? "Different" : "Same",
                        group.NotMatch ? "Not a match" : group.Ignored ? "Ignored" : group.Reviewed ? "Reviewed" : "Unreviewed", group.EvidenceText);
                    _visualGroupsGrid.Rows[row].Tag = group;
                }
                if (advanceAfter is { } anchor)
                {
                    int targetIndex = LibraryDuplicateReviewSelectionPolicy.ResolveNextVisibleIndex(
                        _visualGroupsGrid.Rows.Cast<DataGridViewRow>()
                            .Select(row => ((VisualSimilarityGroupRecord)row.Tag!).GroupId)
                            .ToArray(),
                        anchor.GroupId,
                        anchor.RowIndex);
                    SelectVisualGroupRow(targetIndex);
                }
                else
                {
                    DataGridViewRow[] selectedRows = _visualGroupsGrid.Rows.Cast<DataGridViewRow>()
                        .Where(row => row.Tag is VisualSimilarityGroupRecord group && selectedGroupIds.Contains(group.GroupId))
                        .ToArray();
                    DataGridViewRow? preferredRow = preferredGroupId.HasValue
                        ? _visualGroupsGrid.Rows.Cast<DataGridViewRow>()
                            .FirstOrDefault(row => row.Tag is VisualSimilarityGroupRecord group && group.GroupId == preferredGroupId.Value)
                        : null;
                    if (selectedRows.Length > 0)
                    {
                        DataGridViewRow currentRow = preferredRow ?? selectedRows[0];
                        _visualGroupsGrid.CurrentCell = currentRow.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                        foreach (DataGridViewRow row in selectedRows)
                            row.Selected = true;
                    }
                    else if (preferredRow != null)
                        SelectVisualGroupRow(preferredRow.Index);
                }
                long first = _visualTotal == 0 ? 0 : (long)_visualPage * VisualPageSize + 1;
                long last = Math.Min(_visualTotal, ((long)_visualPage + 1) * VisualPageSize);
                _visualPageLabel.Text = $"{first:N0}–{last:N0} of {_visualTotal:N0}";
                await RefreshVisualMembersAsync();
            }
            finally
            {
                _loadingVisualGroups = false;
            }
        }

        private async Task RefreshVisualMembersAsync()
        {
            int loadVersion = Interlocked.Increment(ref _visualMemberLoadVersion);
            await _visualMemberRefreshLock.WaitAsync();
            try
            {
                if (loadVersion != Volatile.Read(ref _visualMemberLoadVersion) || IsDisposed || Disposing || _visualMembersGrid.IsDisposed)
                    return;
                if (SelectedVisualGroup() is not { } selectedGroup) { _visualMembersGrid.Rows.Clear(); UpdateVisualActionState(); return; }
                long groupId = selectedGroup.GroupId;
                IReadOnlyList<VisualSimilarityMemberRecord> members = await Task.Run(() => _runtime.VisualCatalog.GetVisualGroupMembers(groupId));
                if (loadVersion != Volatile.Read(ref _visualMemberLoadVersion) || IsDisposed || Disposing || _visualMembersGrid.IsDisposed)
                    return;
                _visualMembersGrid.Rows.Clear();
                foreach (VisualSimilarityMemberRecord member in members)
                {
                    string keeper = member.IsManualKeeper ? "Recommended to Keep" : member.IsSuggestedKeeper ? "Recommended to Keep" : "Duplicate Candidate";
                    int row = _visualMembersGrid.Rows.Add(keeper, member.IsProtected ? "Yes" : "No", member.FullPath, member.LocationPath,
                        FormatBytes(member.SizeBytes), member.VideoCodec, member.Width.HasValue && member.Height.HasValue ? $"{member.Width}×{member.Height}" : "",
                        member.TotalBitRate.HasValue ? $"{member.TotalBitRate / 1_000_000d:0.##} Mbps" : "",
                        member.DurationSeconds.HasValue ? FormatDuration(member.DurationSeconds.Value) : "", member.Availability);
                    _visualMembersGrid.Rows[row].Tag = member;
                }
                if (_visualMembersGrid.Rows.Count > 0 && _visualMembersGrid.SelectedRows.Count == 0)
                {
                    _visualMembersGrid.Rows[0].Selected = true;
                    _visualMembersGrid.CurrentCell = _visualMembersGrid.Rows[0].Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                }
                await UpdateVisualComparisonPreviewAsync(members);
                UpdateVisualActionState();
            }
            finally { _visualMemberRefreshLock.Release(); }
        }

        private DuplicateReviewSelectionAnchor? CaptureVisualReviewSelection(long groupId)
        {
            DataGridViewRow? row = _visualGroupsGrid.SelectedRows.Cast<DataGridViewRow>()
                .FirstOrDefault(value => value.Tag is VisualSimilarityGroupRecord group && group.GroupId == groupId);
            return row == null ? null : new DuplicateReviewSelectionAnchor(groupId, row.Index);
        }

        private VisualGroupQuery BuildVisualQuery()
        {
            bool? reviewed = _visualReview.SelectedIndex switch { 1 => false, 2 => true, _ => null };
            bool? ignored = _visualReview.SelectedIndex == 3 ? true : null;
            bool? notMatch = _visualReview.SelectedIndex switch { 4 => true, 5 => null, _ => false };
            bool? codec = _visualCodecDifference.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            bool? resolution = _visualResolutionDifference.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            long? location = _visualLocation.SelectedItem is LocationChoice choice && choice.Id > 0 ? choice.Id : null;
            return new VisualGroupQuery(Search: _visualSearch.Text, LocationId: location, Reviewed: reviewed, Ignored: ignored, NotMatch: notMatch,
                CodecDiffers: codec, ResolutionDiffers: resolution, MinimumConfidence: (double)_visualConfidence.Value,
                SortColumn: _visualSort.Text.ToLowerInvariant(), Descending: true, Offset: _visualPage * VisualPageSize, Limit: VisualPageSize,
                IncludeInactive: _visualReview.SelectedIndex == 5);
        }

        private void RefreshVisualLocationFilter(IReadOnlyList<LibraryLocationRecord> locations)
        {
            long selected = _visualLocation.SelectedItem is LocationChoice choice ? choice.Id : 0;
            _visualLocation.Items.Clear();
            _visualLocation.Items.Add(new LocationChoice(0, "All locations"));
            foreach (LibraryLocationRecord location in locations) _visualLocation.Items.Add(new LocationChoice(location.Id, location.Path));
            _visualLocation.SelectedItem = _visualLocation.Items.Cast<LocationChoice>().FirstOrDefault(item => item.Id == selected) ?? _visualLocation.Items[0];
        }

        private async void SetVisualKeeper_Click(object? sender, EventArgs e)
        {
            await SetSelectedVisualKeeperAsync();
        }

        private async void ToggleVisualProtection_Click(object? sender, EventArgs e)
        {
            await ToggleSelectedVisualProtectionAsync();
        }

        private async void MarkVisualReviewed_Click(object? sender, EventArgs e)
        {
            await MarkSelectedVisualReviewedAsync();
        }

        private async void ToggleVisualIgnored_Click(object? sender, EventArgs e)
        {
            await ToggleSelectedVisualIgnoredAsync();
        }

        private async void ReviewSelectedVisualCleanup_Click(object? sender, EventArgs e)
        {
            long[] groupIds = SelectedVisualGroups().Select(group => group.GroupId).ToArray();
            if (groupIds.Length == 0) return;
            await PreviewVisualCleanupAsync(groupIds);
        }

        private async void ReviewBulkVisualCleanup_Click(object? sender, EventArgs e) => await PreviewVisualCleanupAsync(null);

        private async void DeleteBothVisual_Click(object? sender, EventArgs e)
        {
            if (SelectedVisualGroup() is not { } group) return;
            await PreviewDeleteBothAsync(group.GroupId);
        }

        private void VisualSimilarity_ProgressChanged(object? sender, LibraryVisualAnalysisProgress e)
        {
            _latestVisualProgress = e;
        }

        private void UpdateVisualActivity(string status, string detail, long completed, long total, bool determinate)
        {
            _visualStatus.Text = string.IsNullOrWhiteSpace(detail) ? status : $"{status} · {detail}";
            ConfigureProgress(_visualProgress, true, completed, total, determinate);
        }

        private async void BackupUserDecisions_Click(object? sender, EventArgs e)
        {
            using var dialog = new SaveFileDialog { Filter = "MediaFlux decision backup (*.db)|*.db", FileName = $"mediaflux-library-decisions-{DateTime.Now:yyyyMMdd-HHmmss}.db", AddExtension = true, DefaultExt = "db" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string path = await Task.Run(() => _runtime.AnalysisCatalog.CreateUserDataBackup(dialog.FileName));
                MessageBox.Show(this, $"User decisions were backed up to:\r\n{path}", "Library Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError("User decisions could not be backed up.", ex); }
        }

        private async void RestoreUserDecisions_Click(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog { Filter = "MediaFlux decision backup (*.db)|*.db|SQLite database (*.db)|*.db", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (MessageBox.Show(this, "Merge protected paths, keeper choices, and review/ignore decisions from this backup?\r\n\r\nNo media files will be changed and cleanup history will not be executed.", "Restore Library Analyzer decisions", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                LibraryUserDataRestoreResult result = await Task.Run(() => _runtime.AnalysisCatalog.RestoreUserDataBackup(dialog.FileName));
                MessageBox.Show(this, $"Decision restore completed.\r\n\r\nExact decisions: {result.DuplicateDecisions:N0}\r\nProtected paths: {result.FileProtections:N0}\r\nVisual pair decisions: {result.VisualDecisions:N0}\r\nVisual family decisions: {result.FamilyDecisions:N0}", "Library Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshSelectedTabAsync();
            }
            catch (Exception ex) { ShowError("User decisions could not be restored. No media files were changed.", ex); }
        }

        private VisualSimilarityGroupRecord[] SelectedVisualGroups() => _visualGroupsGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Tag).OfType<VisualSimilarityGroupRecord>().ToArray();
        private VisualSimilarityGroupRecord? SelectedVisualGroup() => (_visualGroupsGrid.CurrentRow is { Selected: true } current ? current.Tag as VisualSimilarityGroupRecord : null)
            ?? SelectedVisualGroups().FirstOrDefault();
        private VisualSimilarityMemberRecord? SelectedVisualMember() => _visualMembersGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as VisualSimilarityMemberRecord;

        private void UpdateVisualActionState()
        {
            VisualSimilarityGroupRecord? group = SelectedVisualGroup();
            VisualSimilarityMemberRecord? member = SelectedVisualMember();
            bool hasGroup = group != null;
            bool hasMember = member != null;
            if (_visualKeepButton == null || _visualReviewCompareButton == null) return;
            Button reviewCompareButton = _visualReviewCompareButton;
            Button protectionButton = _visualProtectionButton!;
            Button reviewedButton = _visualReviewedButton!;
            Button ignoredButton = _visualIgnoredButton!;
            Button recheckButton = _visualRecheckButton!;
            Button previewCleanupButton = _visualPreviewCleanupButton!;
            Button bulkCleanupButton = _visualBulkCleanupButton!;
            Button rulesReviewButton = _visualRulesReviewButton!;
            Button deleteBothButton = _visualDeleteBothButton!;
            reviewCompareButton.Enabled = hasGroup;
            _visualKeepButton.Enabled = hasGroup && hasMember && CanSelectVisualKeeper(member!);
            protectionButton.Enabled = hasMember;
            protectionButton.Text = member?.IsProtected == true ? "Remove Protection" : "Protect Selected File";
            reviewedButton.Enabled = hasGroup && group?.Reviewed == false;
            ignoredButton.Enabled = hasGroup;
            ignoredButton.Text = group?.Ignored == true ? "Restore Ignored Match" : "Ignore This Match";
            recheckButton.Enabled = hasGroup;
            previewCleanupButton.Enabled = hasGroup && group?.Ignored != true && group?.NotMatch != true;
            bulkCleanupButton.Enabled = _visualTotal > 0;
            rulesReviewButton.Enabled = _visualTotal > 0;
            deleteBothButton.Enabled = hasGroup && group?.Ignored != true && group?.NotMatch != true;
            _visualReviewGuidance.Text = !hasGroup
                ? "Select a match above to review its files."
                : hasMember
                    ? $"Selected: {Path.GetFileName(member!.FullPath)} — {VisualMemberRole(member)}. Choose an action below."
                    : "Select one of the two files above, then choose what MediaFlux should do with it.";
        }

        private static string VisualMemberRole(VisualSimilarityMemberRecord member) =>
            member.IsManualKeeper || member.IsSuggestedKeeper ? "Recommended to Keep" : "Duplicate Candidate";
        private void AddVisualGroupColumn(string name, string header, int width, bool visible = true) => _visualGroupsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, Visible = visible });
        private void AddVisualMemberColumn(string name, string header, int width) => _visualMembersGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
    }
}
