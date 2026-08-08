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
        private int _visualPage;
        private long _visualTotal;
        private bool _loadingVisualGroups;

        private void BuildVisualSimilarityTab()
        {
            var tab = new TabPage("Duplicates — Visual") { Padding = new Padding(8) };
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, AutoScroll = true, WrapContents = true };
            AddButton(toolbar, "Analyze visual similarity", AnalyzeVisualSimilarity_Click);
            AddButton(toolbar, "Pause", (_, _) => _runtime.VisualSimilarity.Pause());
            AddButton(toolbar, "Resume", (_, _) => _runtime.VisualSimilarity.Resume());
            AddButton(toolbar, "Cancel", (_, _) => _runtime.VisualSimilarity.Cancel());
            toolbar.Controls.Add(Labeled("Search", _visualSearch));
            toolbar.Controls.Add(Labeled("Location", _visualLocation));
            toolbar.Controls.Add(Labeled("Minimum confidence", _visualConfidence));
            toolbar.Controls.Add(Labeled("Review", _visualReview));
            toolbar.Controls.Add(Labeled("Codec", _visualCodecDifference));
            toolbar.Controls.Add(Labeled("Resolution", _visualResolutionDifference));
            toolbar.Controls.Add(Labeled("Sort", _visualSort));
            var apply = new Button { Text = "Apply", AutoSize = true, Margin = new Padding(4, 19, 3, 3) };
            apply.Click += async (_, _) => { _visualPage = 0; await RefreshVisualGroupsAsync(); };
            toolbar.Controls.Add(apply);

            _visualLocation.Items.Add(new LocationChoice(0, "All locations"));
            _visualLocation.SelectedIndex = 0;
            _visualReview.Items.AddRange(new object[] { "All", "Unreviewed", "Reviewed", "Ignored" });
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
            _visualGroupsGrid.MultiSelect = false;
            _visualGroupsGrid.SelectionChanged += async (_, _) => await RefreshVisualMembersAsync();

            AddVisualMemberColumn("Keeper", "Keeper", 85);
            AddVisualMemberColumn("Protected", "Protected", 70);
            AddVisualMemberColumn("Path", "File location", 400);
            AddVisualMemberColumn("Root", "Root", 180);
            AddVisualMemberColumn("Size", "Size", 85);
            AddVisualMemberColumn("Codec", "Codec", 75);
            AddVisualMemberColumn("Resolution", "Resolution", 85);
            AddVisualMemberColumn("Bitrate", "Bitrate", 90);
            AddVisualMemberColumn("Duration", "Duration", 85);
            AddVisualMemberColumn("Availability", "Availability", 90);

            var notice = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Text = "Visual matches are probabilistic and review-only. MediaFlux never offers bulk deletion from this view.",
                ForeColor = Color.DarkOrange,
                Padding = new Padding(8, 9, 0, 0)
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, AutoScroll = true, WrapContents = false };
            AddButton(actions, "Set selected keeper", SetVisualKeeper_Click);
            AddButton(actions, "Protect / unprotect file", ToggleVisualProtection_Click);
            AddButton(actions, "Mark reviewed", MarkVisualReviewed_Click);
            AddButton(actions, "Ignore / restore match", ToggleVisualIgnored_Click);

            var pager = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var next = new Button { Text = "Next", AutoSize = true };
            var previous = new Button { Text = "Previous", AutoSize = true };
            next.Click += async (_, _) => { _visualPage++; await RefreshVisualGroupsAsync(); };
            previous.Click += async (_, _) => { _visualPage = Math.Max(0, _visualPage - 1); await RefreshVisualGroupsAsync(); };
            pager.Controls.Add(next); pager.Controls.Add(previous); pager.Controls.Add(_visualPageLabel);
            var status = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false };
            status.Controls.Add(_visualProgress); status.Controls.Add(_visualStatus);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240, Panel1MinSize = 140, Panel2MinSize = 140 };
            split.Panel1.Controls.Add(_visualGroupsGrid);
            split.Panel2.Controls.Add(_visualMembersGrid);
            tab.Controls.Add(split);
            tab.Controls.Add(status);
            tab.Controls.Add(pager);
            tab.Controls.Add(actions);
            tab.Controls.Add(notice);
            tab.Controls.Add(toolbar);
            _tabs.TabPages.Add(tab);
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

        private async Task RefreshVisualGroupsAsync()
        {
            if (_loadingVisualGroups || IsDisposed) return;
            _loadingVisualGroups = true;
            try
            {
                VisualSimilarityGroupPage page = await Task.Run(() => _runtime.VisualCatalog.QueryVisualGroups(BuildVisualQuery()));
                if (IsDisposed) return;
                _visualTotal = page.TotalCount;
                _visualGroupsGrid.Rows.Clear();
                foreach (VisualSimilarityGroupRecord group in page.Groups)
                {
                    int row = _visualGroupsGrid.Rows.Add(group.GroupId, "Visual / similar", $"{group.ConfidenceScore:0.0}%", FormatBytes(group.ReclaimableBytes),
                        $"{group.DurationDeltaSeconds:0.###} s", group.CodecDiffers ? "Different" : "Same", group.ResolutionDiffers ? "Different" : "Same",
                        group.Ignored ? "Ignored" : group.Reviewed ? "Reviewed" : "Unreviewed", group.EvidenceText);
                    _visualGroupsGrid.Rows[row].Tag = group;
                }
                long first = _visualTotal == 0 ? 0 : (long)_visualPage * VisualPageSize + 1;
                long last = Math.Min(_visualTotal, ((long)_visualPage + 1) * VisualPageSize);
                _visualPageLabel.Text = $"{first:N0}–{last:N0} of {_visualTotal:N0}";
                await RefreshVisualMembersAsync();
            }
            finally { _loadingVisualGroups = false; }
        }

        private async Task RefreshVisualMembersAsync()
        {
            if (_visualGroupsGrid.SelectedRows.Count == 0) { _visualMembersGrid.Rows.Clear(); return; }
            long groupId = Convert.ToInt64(_visualGroupsGrid.SelectedRows[0].Cells[0].Value);
            IReadOnlyList<VisualSimilarityMemberRecord> members = await Task.Run(() => _runtime.VisualCatalog.GetVisualGroupMembers(groupId));
            if (IsDisposed) return;
            _visualMembersGrid.Rows.Clear();
            foreach (VisualSimilarityMemberRecord member in members)
            {
                string keeper = member.IsManualKeeper ? "Manual" : member.IsSuggestedKeeper ? "Suggested" : "Candidate";
                int row = _visualMembersGrid.Rows.Add(keeper, member.IsProtected ? "Yes" : "No", member.FullPath, member.LocationPath,
                    FormatBytes(member.SizeBytes), member.VideoCodec, member.Width.HasValue && member.Height.HasValue ? $"{member.Width}×{member.Height}" : "",
                    member.TotalBitRate.HasValue ? $"{member.TotalBitRate / 1_000_000d:0.##} Mbps" : "",
                    member.DurationSeconds.HasValue ? FormatDuration(member.DurationSeconds.Value) : "", member.Availability);
                _visualMembersGrid.Rows[row].Tag = member;
            }
        }

        private VisualGroupQuery BuildVisualQuery()
        {
            bool? reviewed = _visualReview.SelectedIndex switch { 1 => false, 2 => true, _ => null };
            bool? ignored = _visualReview.SelectedIndex == 3 ? true : null;
            bool? codec = _visualCodecDifference.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            bool? resolution = _visualResolutionDifference.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            long? location = _visualLocation.SelectedItem is LocationChoice choice && choice.Id > 0 ? choice.Id : null;
            return new VisualGroupQuery(Search: _visualSearch.Text, LocationId: location, Reviewed: reviewed, Ignored: ignored,
                CodecDiffers: codec, ResolutionDiffers: resolution, MinimumConfidence: (double)_visualConfidence.Value,
                SortColumn: _visualSort.Text.ToLowerInvariant(), Descending: true, Offset: _visualPage * VisualPageSize, Limit: VisualPageSize);
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
            if (SelectedVisualGroup() is not { } group || SelectedVisualMember() is not { } member) return;
            _runtime.VisualCatalog.SaveVisualDecision(new VisualGroupDecision(group.GroupId, member.FileId, true, group.Ignored));
            await RefreshVisualGroupsAsync();
        }

        private async void ToggleVisualProtection_Click(object? sender, EventArgs e)
        {
            if (SelectedVisualMember() is not { } member) return;
            _runtime.AnalysisCatalog.SetFileProtection(member.FileId, !member.IsProtected, member.IsProtected ? "" : "Protected in Library Analyzer visual review");
            await RefreshVisualGroupsAsync();
        }

        private async void MarkVisualReviewed_Click(object? sender, EventArgs e)
        {
            if (SelectedVisualGroup() is not { } group) return;
            _runtime.VisualCatalog.SaveVisualDecision(new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, true, group.Ignored));
            await RefreshVisualGroupsAsync();
        }

        private async void ToggleVisualIgnored_Click(object? sender, EventArgs e)
        {
            if (SelectedVisualGroup() is not { } group) return;
            _runtime.VisualCatalog.SaveVisualDecision(new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, true, !group.Ignored));
            await RefreshVisualGroupsAsync();
        }

        private void VisualSimilarity_ProgressChanged(object? sender, LibraryVisualAnalysisProgress e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                _visualProgress.Visible = _runtime.VisualSimilarity.IsRunning;
                _visualStatus.Text = $"{e.Stage}: {e.FingerprintedFiles:N0}/{e.EligibleFiles:N0} fingerprinted, {e.MatchPairs:N0}/{e.CandidatePairs:N0} matches" +
                                     (string.IsNullOrWhiteSpace(e.CurrentPath) ? "" : $" · {Path.GetFileName(e.CurrentPath)}");
            });
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
                MessageBox.Show(this, $"Decision restore completed.\r\n\r\nExact decisions: {result.DuplicateDecisions:N0}\r\nProtected paths: {result.FileProtections:N0}\r\nVisual decisions: {result.VisualDecisions:N0}", "Library Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshSelectedTabAsync();
            }
            catch (Exception ex) { ShowError("User decisions could not be restored. No media files were changed.", ex); }
        }

        private VisualSimilarityGroupRecord? SelectedVisualGroup() => _visualGroupsGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as VisualSimilarityGroupRecord;
        private VisualSimilarityMemberRecord? SelectedVisualMember() => _visualMembersGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as VisualSimilarityMemberRecord;
        private void AddVisualGroupColumn(string name, string header, int width, bool visible = true) => _visualGroupsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, Visible = visible });
        private void AddVisualMemberColumn(string name, string header, int width) => _visualMembersGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
    }
}
