using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private void ConfigureVisualContextMenus()
        {
            AddVisualMenuItem(_visualGroupsMenu, "Review / Compare", "Review", async () => await OpenVisualReviewAsync());
            AddVisualMenuItem(_visualGroupsMenu, "Review cleanup plan…", "Cleanup", async () => { if (SelectedVisualGroup() is { } g) await PreviewVisualCleanupAsync(new[] { g.GroupId }); });
            AddVisualMenuItem(_visualGroupsMenu, "Delete both files…", "DeleteBoth", async () => { if (SelectedVisualGroup() is { } g) await PreviewDeleteBothAsync(g.GroupId); });
            _visualGroupsMenu.Items.Add(new ToolStripSeparator());
            AddVisualMenuItem(_visualGroupsMenu, "Mark reviewed", "Reviewed", async () => await MarkSelectedVisualReviewedAsync());
            AddVisualMenuItem(_visualGroupsMenu, "Ignore match", "Ignore", async () => await ToggleSelectedVisualIgnoredAsync());
            AddVisualMenuItem(_visualGroupsMenu, "Not a match", "NotMatch", async () => await ToggleSelectedVisualNotMatchAsync());
            _visualGroupsMenu.Items.Add(new ToolStripSeparator());
            AddVisualMenuItem(_visualGroupsMenu, "Previous match", "Previous", async () => await NavigateVisualSelectionAsync(-1));
            AddVisualMenuItem(_visualGroupsMenu, "Next match", "Next", async () => await NavigateVisualSelectionAsync(1));
            _visualGroupsMenu.Opening += VisualGroupsMenu_Opening;
            AttachVisualContextMenu(_visualGroupsGrid, _visualGroupsMenu);

            AddVisualMenuItem(_visualMembersMenu, "Review / Compare", "Review", async () => await OpenVisualReviewAsync());
            AddVisualMenuItem(_visualMembersMenu, "Play / Preview", "Play", () => { PlaySelectedVisualMember(); return Task.CompletedTask; });
            AddVisualMenuItem(_visualMembersMenu, "Set as keeper", "Keeper", async () => await SetSelectedVisualKeeperAsync());
            AddVisualMenuItem(_visualMembersMenu, "Keep this file / delete the other…", "KeepDeleteOther", async () => await KeepSelectedAndDeleteOtherAsync());
            AddVisualMenuItem(_visualMembersMenu, "Delete selected candidate…", "DeleteCandidate", async () => await DeleteSelectedVisualCandidateAsync());
            AddVisualMenuItem(_visualMembersMenu, "Protect", "Protect", async () => await ToggleSelectedVisualProtectionAsync());
            _visualMembersMenu.Items.Add(new ToolStripSeparator());
            AddVisualMenuItem(_visualMembersMenu, "Open containing folder", "Folder", () => { OpenSelectedVisualMemberFolder(); return Task.CompletedTask; });
            AddVisualMenuItem(_visualMembersMenu, "Copy file path", "CopyPath", () => { CopySelectedVisualMemberPath(); return Task.CompletedTask; });
            _visualMembersMenu.Items.Add(new ToolStripSeparator());
            AddVisualMenuItem(_visualMembersMenu, "Mark reviewed", "Reviewed", async () => await MarkSelectedVisualReviewedAsync());
            AddVisualMenuItem(_visualMembersMenu, "Ignore match", "Ignore", async () => await ToggleSelectedVisualIgnoredAsync());
            AddVisualMenuItem(_visualMembersMenu, "Not a match", "NotMatch", async () => await ToggleSelectedVisualNotMatchAsync());
            _visualMembersMenu.Opening += VisualMembersMenu_Opening;
            AttachVisualContextMenu(_visualMembersGrid, _visualMembersMenu);
        }

        private static void AddVisualMenuItem(ContextMenuStrip menu, string text, string name, Func<Task> action)
        {
            var item = new ToolStripMenuItem(text) { Name = name };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }

        private static void AttachVisualContextMenu(DataGridView grid, ContextMenuStrip menu)
        {
            grid.ContextMenuStrip = menu;
            grid.CellMouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                    return;
                if (!grid.Rows[e.RowIndex].Selected)
                    grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
                if (e.ColumnIndex >= 0)
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            };
        }

        private void VisualGroupsMenu_Opening(object? sender, CancelEventArgs e)
        {
            VisualSimilarityGroupRecord? group = SelectedVisualGroup();
            SetMenuState(_visualGroupsMenu, "Review", group != null);
            SetMenuState(_visualGroupsMenu, "Cleanup", group != null && !group.Ignored);
            SetMenuState(_visualGroupsMenu, "DeleteBoth", group != null && !group.Ignored && !group.NotMatch);
            SetMenuState(_visualGroupsMenu, "Reviewed", group != null && !group.Reviewed);
            SetMenuState(_visualGroupsMenu, "Ignore", group != null, group?.Ignored == true ? "Restore match" : "Ignore match");
            SetMenuState(_visualGroupsMenu, "NotMatch", group != null, group?.NotMatch == true ? "Restore failed match" : "Not a match");
            SetMenuState(_visualGroupsMenu, "Previous", group != null && _visualTotal > 1);
            SetMenuState(_visualGroupsMenu, "Next", group != null && _visualTotal > 1);
            e.Cancel = group == null;
        }

        private void VisualMembersMenu_Opening(object? sender, CancelEventArgs e)
        {
            VisualSimilarityGroupRecord? group = SelectedVisualGroup();
            VisualSimilarityMemberRecord? member = SelectedVisualMember();
            bool fileExists = member != null && File.Exists(member.FullPath);
            bool folderExists = member != null && Directory.Exists(Path.GetDirectoryName(member.FullPath));
            SetMenuState(_visualMembersMenu, "Review", group != null);
            SetMenuState(_visualMembersMenu, "Play", fileExists);
            SetMenuState(_visualMembersMenu, "Keeper", member != null && CanSelectVisualKeeper(member) && !member.IsManualKeeper);
            bool hasOtherKeeper = group != null && member != null && (group.ManualKeeperFileId ?? group.SuggestedKeeperFileId) is long keeperId && keeperId != member.FileId;
            SetMenuState(_visualMembersMenu, "KeepDeleteOther", member != null && CanSelectVisualKeeper(member));
            SetMenuState(_visualMembersMenu, "DeleteCandidate", hasOtherKeeper && member!.Availability == IndexedFileAvailability.Present && !member.IsProtected);
            SetMenuState(_visualMembersMenu, "Protect", member != null, member?.IsProtected == true ? "Unprotect" : "Protect");
            SetMenuState(_visualMembersMenu, "Folder", folderExists);
            SetMenuState(_visualMembersMenu, "CopyPath", member != null && !string.IsNullOrWhiteSpace(member.FullPath));
            SetMenuState(_visualMembersMenu, "Reviewed", group != null && !group.Reviewed);
            SetMenuState(_visualMembersMenu, "Ignore", group != null, group?.Ignored == true ? "Restore match" : "Ignore match");
            SetMenuState(_visualMembersMenu, "NotMatch", group != null, group?.NotMatch == true ? "Restore failed match" : "Not a match");
            e.Cancel = member == null;
        }

        private static void SetMenuState(ContextMenuStrip menu, string name, bool enabled, string? text = null)
        {
            if (menu.Items.Find(name, false).FirstOrDefault() is not ToolStripItem item)
                return;
            item.Enabled = enabled;
            if (!string.IsNullOrWhiteSpace(text))
                item.Text = text;
        }

        private async Task SetSelectedVisualKeeperAsync()
        {
            if (SelectedVisualGroup() is not { } group || SelectedVisualMember() is not { } member)
                return;
            if (!CanSelectVisualKeeper(member))
                return;
            await SaveVisualKeeperAsync(group, member);
            await RefreshVisualGroupsAsync(group.GroupId);
        }

        private async Task ToggleSelectedVisualProtectionAsync()
        {
            if (SelectedVisualMember() is not { } member)
                return;
            await SaveVisualProtectionAsync(member);
            await RefreshVisualGroupsAsync(member.GroupId);
        }

        private async Task MarkSelectedVisualReviewedAsync()
        {
            if (SelectedVisualGroup() is not { } group)
                return;
            await Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, true, group.Ignored, group.NotMatch)));
            await RefreshVisualGroupsAsync(group.GroupId);
        }

        private async Task ToggleSelectedVisualIgnoredAsync()
        {
            if (SelectedVisualGroup() is not { } group)
                return;
            await Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, true, !group.Ignored, group.NotMatch)));
            await RefreshVisualGroupsAsync(group.GroupId);
        }

        private async Task ToggleSelectedVisualNotMatchAsync()
        {
            if (SelectedVisualGroup() is not { } group) return;
            await Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, group.Reviewed, group.Ignored, !group.NotMatch)));
            await RefreshVisualGroupsAsync();
        }

        private Task SaveVisualKeeperAsync(VisualSimilarityGroupRecord group, VisualSimilarityMemberRecord member) =>
            Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                new VisualGroupDecision(group.GroupId, member.FileId, true, group.Ignored, group.NotMatch)));

        private Task SaveVisualProtectionAsync(VisualSimilarityMemberRecord member) =>
            Task.Run(() => _runtime.AnalysisCatalog.SetFileProtection(
                member.FileId,
                !member.IsProtected,
                member.IsProtected ? "" : "Protected in Library Analyzer visual review"));

        private async Task KeepSelectedAndDeleteOtherAsync()
        {
            if (SelectedVisualGroup() is not { } group || SelectedVisualMember() is not { } member) return;
            await SaveVisualKeeperAsync(group, member);
            await PreviewVisualCleanupAsync(new[] { group.GroupId });
        }

        private async Task DeleteSelectedVisualCandidateAsync()
        {
            if (SelectedVisualGroup() is not { } group || SelectedVisualMember() is not { } candidate) return;
            IReadOnlyList<VisualSimilarityMemberRecord> members = await Task.Run(() => _runtime.VisualCatalog.GetVisualGroupMembers(group.GroupId));
            VisualSimilarityMemberRecord? keeper = members.FirstOrDefault(x => x.FileId == (group.ManualKeeperFileId ?? group.SuggestedKeeperFileId));
            if (keeper == null || keeper.FileId == candidate.FileId) return;
            await PreviewVisualCleanupAsync(new[] { group.GroupId }, candidate.FileId);
        }

        private async Task NavigateVisualSelectionAsync(int delta)
        {
            if (_visualTotal <= 1 || _visualGroupsGrid.SelectedRows.Count == 0)
                return;
            long current = ((long)_visualPage * VisualPageSize) + _visualGroupsGrid.SelectedRows[0].Index;
            long target = (current + delta + _visualTotal) % _visualTotal;
            int targetPage = (int)(target / VisualPageSize);
            int targetRow = (int)(target % VisualPageSize);
            if (targetPage != _visualPage)
            {
                _visualPage = targetPage;
                await RefreshVisualGroupsAsync();
            }
            SelectVisualGroupRow(targetRow);
            await RefreshVisualMembersAsync();
        }

        private void SelectVisualGroupRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _visualGroupsGrid.Rows.Count)
                return;
            _visualGroupsGrid.ClearSelection();
            DataGridViewRow row = _visualGroupsGrid.Rows[rowIndex];
            row.Selected = true;
            _visualGroupsGrid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
        }

        private async void VisualGroupsGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _visualGroupsGrid.Rows.Count)
                return;

            DataGridViewRow row = _visualGroupsGrid.Rows[e.RowIndex];
            _visualGroupsGrid.ClearSelection();
            row.Selected = true;
            _visualGroupsGrid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
            await OpenVisualReviewAsync();
        }

        private async Task OpenVisualReviewAsync()
        {
            if (SelectedVisualGroup() == null)
                return;

            using var dialog = new MediaFluxForm
            {
                Text = "Review Visual Match",
                StartPosition = FormStartPosition.CenterParent,
                KeyPreview = true,
                MinimumSize = new Size(980, 650),
                Size = new Size(1120, 780)
            };
            using var previewCancellation = new CancellationTokenSource();
            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 124,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = SystemColors.Window
            };
            var body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = false,
                Padding = new Padding(10),
                BackColor = SystemColors.Control
            };
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8),
                WrapContents = false
            };
            var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.OK };
            bool semiAutomaticApproval = (_reviewOptions.AutomationOptions ?? new LibraryVisualReviewAutomationOptions()).Normalize().SemiAutomaticKeeperApproval;
            var next = new Button { Text = semiAutomaticApproval ? "Accept + Next" : "Next >", Width = semiAutomaticApproval ? 112 : 90 };
            var previous = new Button { Text = "< Previous", Width = 90 };
            var ignore = new Button { Text = "Ignore", Width = 100 };
            var notMatch = new Button { Text = "Not a Match + Next", Width = 145 };
            var deleteBoth = new Button { Text = "Delete Both…", Width = 120 };
            var reviewedNext = new Button { Text = "Reviewed + Next", Width = 130 };
            footer.Controls.Add(close);
            footer.Controls.Add(next);
            footer.Controls.Add(previous);
            footer.Controls.Add(ignore);
            footer.Controls.Add(notMatch);
            footer.Controls.Add(deleteBoth);
            footer.Controls.Add(reviewedNext);
            dialog.Controls.Add(body);
            dialog.Controls.Add(footer);
            dialog.Controls.Add(header);
            dialog.AcceptButton = close;

            bool loading = false;
            bool catalogStateChanged = false;
            bool currentReviewEligible = false;
            VisualSimilarityGroupRecord? currentReviewGroup = null;
            long? currentAutoKeeperFileId = null;
            CancellationTokenSource? groupPreviewCancellation = null;
            async Task LoadCurrentAsync()
            {
                if (loading || SelectedVisualGroup() is not { } selected)
                    return;
                loading = true;
                try
                {
                    VisualSimilarityGroupRecord? group = await Task.Run(() => _runtime.VisualCatalog.GetVisualGroup(selected.GroupId));
                    if (group == null || dialog.IsDisposed)
                        return;
                    // Opening review is observational. Use the catalog's existing lifecycle
                    // snapshot; authoritative scans and explicit action validation own
                    // presence reconciliation.
                    LibraryMatchEligibility eligibility = new(group.Eligibility, group.EligibilityReason);
                    currentReviewEligible = eligibility.IsActive;
                    currentReviewGroup = group;
                    IReadOnlyList<VisualSimilarityMemberRecord> members = await Task.Run(() => _runtime.VisualCatalog.GetVisualGroupMembers(group.GroupId));
                    if (dialog.IsDisposed)
                        return;
                    groupPreviewCancellation?.Cancel();
                    groupPreviewCancellation?.Dispose();
                    groupPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(previewCancellation.Token);
                    DisposeVisualReviewImages(body);
                    body.Controls.Clear();
                    DuplicatePreviewCacheService.PruneOlderThan(VisualPreviewCacheRoot, TimeSpan.FromDays(30));
                    long position = ((long)_visualPage * VisualPageSize) + _visualGroupsGrid.SelectedRows[0].Index + 1;
                    DuplicateKeeperEvaluation keeperEvaluation = DuplicateKeeperScoringService.Evaluate(
                        members.Select(LibraryVisualDuplicateCleanupService.ToLegacyItem).ToArray(),
                        _visualKeeperPreferences,
                        DuplicateKeeperScoringContext.Visual);
                    LibraryKeeperExplanation keeperDetails = _runtime.KeeperExplanations.Explain(members, _visualKeeperPreferences);
                    currentAutoKeeperFileId = group.ManualKeeperFileId
                        ?? (semiAutomaticApproval ? group.SuggestedKeeperFileId ?? keeperDetails.RecommendedKeeperFileId : null);
                    string keeperExplanation = group.ManualKeeperFileId.HasValue
                        ? "Manual keeper selected by the user."
                        : keeperDetails.Summary + (keeperDetails.Factors.Count == 0 ? "" : " Factors: " + string.Join("; ", keeperDetails.Factors) + ".");
                    header.Height = eligibility.IsActive ? 124 : 146;
                    header.Text = BuildVisualReviewHeader(group, position, _visualTotal, keeperExplanation) +
                        (eligibility.IsActive ? "" : Environment.NewLine + "Catalog status: " + eligibility.Reason);
                    dialog.Text = $"Review Visual Match {position:N0} of {_visualTotal:N0}";
                    ignore.Text = group.Ignored ? "Restore" : "Ignore";
                    notMatch.Text = group.NotMatch ? "Restore Match" : "Not a Match + Next";
                    previous.Enabled = _visualTotal > 1;
                    next.Enabled = _visualTotal > 1;
                    next.Text = semiAutomaticApproval && eligibility.IsActive ? "Accept + Next" : "Next >";
                    reviewedNext.Enabled = eligibility.IsActive;
                    foreach (VisualSimilarityMemberRecord member in members)
                    {
                        var card = CreateVisualReviewCard(
                            group,
                            member,
                            eligibility.IsActive,
                            semiAutomaticApproval && currentAutoKeeperFileId == member.FileId && !member.IsManualKeeper,
                            async () => { await SaveVisualKeeperAsync(group, member); catalogStateChanged = true; await LoadCurrentAsync(); },
                            async () => { await SaveVisualProtectionAsync(member); catalogStateChanged = true; await LoadCurrentAsync(); },
                            async () =>
                            {
                                await SaveVisualKeeperAsync(group, member);
                                catalogStateChanged = true;
                                bool deleted = await PreviewVisualCleanupAsync(new[] { group.GroupId });
                                if (deleted) await MoveAsync(1); else await LoadCurrentAsync();
                            });
                        body.Controls.Add(card.Panel);
                        _ = LoadVisualReviewThumbnailAsync(card.Picture, card.Status, member, groupPreviewCancellation.Token);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogService.Append(Application.StartupPath, "Library Analyzer visual review failed", exception: ex);
                    MessageBox.Show(dialog, "The visual match could not be loaded.\r\n\r\n" + ex.Message, "Library Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    loading = false;
                }
            }

            async Task MoveAsync(int delta)
            {
                await NavigateVisualSelectionAsync(delta);
                await LoadCurrentAsync();
            }

            previous.Click += async (_, _) => await MoveAsync(-1);
            next.Click += async (_, _) =>
            {
                if (semiAutomaticApproval && currentReviewEligible && currentReviewGroup is { } group &&
                    !group.ManualKeeperFileId.HasValue && currentAutoKeeperFileId is long keeperId)
                {
                    await Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                        new VisualGroupDecision(group.GroupId, keeperId, true, group.Ignored, group.NotMatch,
                            Source: "semi-automatic-review")));
                    catalogStateChanged = true;
                }
                await MoveAsync(1);
            };
            ignore.Click += async (_, _) =>
            {
                if (currentReviewGroup is not { } group)
                    return;
                await Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                    new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, true, !group.Ignored, group.NotMatch)));
                catalogStateChanged = true;
                await LoadCurrentAsync();
            };
            notMatch.Click += async (_, _) =>
            {
                if (currentReviewGroup is not { } group) return;
                int rowIndex = _visualGroupsGrid.SelectedRows.Count == 0 ? 0 : _visualGroupsGrid.SelectedRows[0].Index;
                await Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                    new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, group.Reviewed, group.Ignored, !group.NotMatch)));
                catalogStateChanged = true;
                await RefreshVisualGroupsAsync();
                if (_visualGroupsGrid.Rows.Count == 0)
                {
                    dialog.Close();
                    return;
                }
                SelectVisualGroupRow(Math.Min(rowIndex, _visualGroupsGrid.Rows.Count - 1));
                await RefreshVisualMembersAsync();
                await LoadCurrentAsync();
            };
            deleteBoth.Click += async (_, _) =>
            {
                if (currentReviewGroup is not { } group) return;
                bool deleted = await PreviewDeleteBothAsync(group.GroupId);
                catalogStateChanged |= deleted;
                if (deleted) await MoveAsync(1); else await LoadCurrentAsync();
            };
            reviewedNext.Click += async (_, _) =>
            {
                if (currentReviewGroup is not { } group)
                    return;
                await Task.Run(() => _runtime.VisualCatalog.SaveVisualDecision(
                    new VisualGroupDecision(group.GroupId, group.ManualKeeperFileId, true, group.Ignored, group.NotMatch)));
                catalogStateChanged = true;
                await MoveAsync(1);
            };
            dialog.KeyDown += async (_, e) =>
            {
                if (e.KeyCode is not (Keys.Left or Keys.Right))
                    return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                await MoveAsync(e.KeyCode == Keys.Left ? -1 : 1);
            };
            dialog.Shown += async (_, _) => await LoadCurrentAsync();
            dialog.FormClosed += (_, _) =>
            {
                groupPreviewCancellation?.Cancel();
                groupPreviewCancellation?.Dispose();
                previewCancellation.Cancel();
                DisposeVisualReviewImages(body);
            };
            dialog.ShowDialog(this);
            if (catalogStateChanged)
                await RefreshVisualGroupsAsync(SelectedVisualGroup()?.GroupId);
        }

        private static string BuildVisualReviewHeader(VisualSimilarityGroupRecord group, long position, long total, string keeperExplanation)
        {
            string state = group.NotMatch ? "Not a match" : group.Ignored ? "Ignored" : group.Reviewed ? "Reviewed" : "Unreviewed";
            string differences = $"Codec: {(group.CodecDiffers ? "different" : "same")} · Resolution: {(group.ResolutionDiffers ? "different" : "same")} · Duration delta: {group.DurationDeltaSeconds:0.###} s";
            return $"Visual match {position:N0} of {total:N0} · {group.ConfidenceScore:0.0}% confidence · {state}{Environment.NewLine}" +
                   $"{group.FrameMatches}/{group.FrameComparisons} representative frames matched · Average hash distance {group.AverageHashDistance:0.#}{Environment.NewLine}" +
                   differences + Environment.NewLine +
                   $"Keeper recommendation: {keeperExplanation}{Environment.NewLine}" +
                   $"Evidence: {group.EvidenceText}{Environment.NewLine}Visual matches are suggestions only; choose and protect a keeper before any separate cleanup decision.";
        }

        private (Panel Panel, PictureBox Picture, Label Status) CreateVisualReviewCard(
            VisualSimilarityGroupRecord group,
            VisualSimilarityMemberRecord member,
            bool decisionsAllowed,
            bool automaticallySelected,
            Func<Task> keepSelected,
            Func<Task> protectSelected,
            Func<Task> keepAndDeleteOther)
        {
            bool selectedKeeper = member.IsManualKeeper || automaticallySelected;
            bool suggestedKeeper = member.IsSuggestedKeeper && !selectedKeeper;
            var panel = new Panel
            {
                Width = 500,
                Height = 520,
                Margin = new Padding(8),
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };
            var picture = new PictureBox
            {
                Dock = DockStyle.Top,
                Height = 220,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            picture.DoubleClick += (_, _) => PlayVisualMember(member);
            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(8, 6, 8, 0),
                Text = Path.GetFileName(member.FullPath),
                AutoEllipsis = true,
                Font = new Font(Font, FontStyle.Bold)
            };
            var details = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 5, 8, 4),
                AutoEllipsis = true,
                Text = BuildVisualMemberDetails(member)
            };
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 70,
                Padding = new Padding(6, 4, 6, 2),
                WrapContents = true
            };
            var play = new Button { Text = "Play video", Width = 104, Enabled = File.Exists(member.FullPath) };
            var keep = new Button { Text = member.IsManualKeeper ? "Keeper selected" : automaticallySelected ? "Selected for Next" : suggestedKeeper ? "Keep (suggested)" : "Set as keeper", Width = 112, Enabled = decisionsAllowed && CanSelectVisualKeeper(member) && !member.IsManualKeeper };
            var protect = new Button { Text = member.IsProtected ? "Unprotect" : "Protect", Width = 90 };
            var folder = new Button { Text = "Open folder", Width = 100, Enabled = Directory.Exists(Path.GetDirectoryName(member.FullPath)) };
            var deleteOther = new Button { Text = "Keep this / delete other…", Width = 180, Enabled = decisionsAllowed && CanSelectVisualKeeper(member) };
            play.Click += (_, _) => PlayVisualMember(member);
            keep.Click += async (_, _) => await keepSelected();
            protect.Click += async (_, _) => await protectSelected();
            folder.Click += (_, _) => OpenVisualMemberFolder(member);
            deleteOther.Click += async (_, _) => await keepAndDeleteOther();
            if (selectedKeeper)
            {
                keep.UseVisualStyleBackColor = false;
                keep.FlatStyle = FlatStyle.Flat;
                keep.BackColor = Color.FromArgb(46, 125, 50);
                keep.ForeColor = Color.White;
            }
            actions.Controls.AddRange(new Control[] { play, keep, deleteOther, protect, folder });
            var status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Padding = new Padding(8, 3, 8, 0),
                ForeColor = SystemColors.GrayText,
                Text = File.Exists(member.FullPath) ? "Loading midpoint preview…" : "File is unavailable"
            };
            panel.Controls.Add(details);
            panel.Controls.Add(status);
            panel.Controls.Add(actions);
            panel.Controls.Add(title);
            panel.Controls.Add(picture);
            panel.AccessibleName = $"Visual review file: {member.FullPath}";
            return (panel, picture, status);
        }

        private static string BuildVisualMemberDetails(VisualSimilarityMemberRecord member)
        {
            string keeper = member.IsManualKeeper ? "MANUAL KEEPER" : member.IsSuggestedKeeper ? "Suggested keeper" : "Candidate";
            string protection = member.IsProtected ? "Protected" : "Not protected";
            string resolution = member.Width.HasValue && member.Height.HasValue ? $"{member.Width}×{member.Height}" : "Unknown resolution";
            string bitrate = member.TotalBitRate.HasValue ? $"{member.TotalBitRate / 1_000_000d:0.##} Mbps" : "Unknown bitrate";
            string duration = member.DurationSeconds.HasValue ? FormatDuration(member.DurationSeconds.Value) : "Unknown duration";
            return $"{keeper} · {protection} · {member.Availability}{Environment.NewLine}" +
                   $"{FormatBytes(member.SizeBytes)} · {member.VideoCodec} · {resolution}{Environment.NewLine}" +
                   $"{bitrate} · {duration}{Environment.NewLine}" +
                   $"Modified {member.LastWriteUtc.ToLocalTime():g}{Environment.NewLine}{Environment.NewLine}" +
                   member.FullPath;
        }

        private static bool CanSelectVisualKeeper(VisualSimilarityMemberRecord member) =>
            member.Availability == IndexedFileAvailability.Present && File.Exists(member.FullPath);

        private async Task LoadVisualReviewThumbnailAsync(PictureBox picture, Label status, VisualSimilarityMemberRecord member, CancellationToken cancellationToken)
        {
            if (!File.Exists(member.FullPath))
                return;
            try
            {
                string? thumbnail = await CreateVisualReviewThumbnailAsync(member, cancellationToken);
                if (cancellationToken.IsCancellationRequested || picture.IsDisposed)
                    return;
                if (string.IsNullOrWhiteSpace(thumbnail) || !File.Exists(thumbnail))
                {
                    status.Text = "Preview unavailable — use Play video";
                    return;
                }
                using var stream = new FileStream(thumbnail, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var image = Image.FromStream(stream);
                picture.Image?.Dispose();
                picture.Image = new Bitmap(image);
                status.Text = "Midpoint preview · double-click to play";
            }
            catch (OperationCanceledException)
            {
                // Closing the review window cancels preview work without affecting media.
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Library Analyzer visual preview failed", member.FullPath, ex);
                if (!status.IsDisposed)
                    status.Text = "Preview unavailable — use Play video";
            }
        }

        private async Task<string?> CreateVisualReviewThumbnailAsync(VisualSimilarityMemberRecord member, CancellationToken cancellationToken)
        {
            FfmpegToolPaths tools = FfmpegToolResolver.Resolve(Application.StartupPath, _reviewOptions.FfmpegPath);
            if (!tools.HasFfmpeg)
                return null;
            string previewDirectory = DuplicatePreviewCacheService.GetPreviewDirectory(VisualPreviewCacheRoot);
            Directory.CreateDirectory(previewDirectory);
            string thumbnailPath = DuplicatePreviewCacheService.GetThumbnailPath(VisualPreviewCacheRoot, member.FullPath);
            var source = new FileInfo(member.FullPath);
            if (File.Exists(thumbnailPath) && File.GetLastWriteTimeUtc(thumbnailPath) >= source.LastWriteTimeUtc)
                return thumbnailPath;

            double seekSeconds = member.DurationSeconds.HasValue && member.DurationSeconds.Value > 0
                ? Math.Max(0.1, member.DurationSeconds.Value * 0.5)
                : 1.0;
            var startInfo = new ProcessStartInfo
            {
                FileName = tools.FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-ss", seekSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", member.FullPath, "-frames:v", "1", "-vf", "scale=480:-2", "-y", thumbnailPath
            })
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process == null)
                return null;
            Task<string> errorRead = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token);
                string error = await errorRead;
                if (process.ExitCode != 0)
                {
                    ErrorLogService.Append(Application.StartupPath, "Library Analyzer visual preview FFmpeg failed", member.FullPath, details: error);
                    return null;
                }
                return File.Exists(thumbnailPath) ? thumbnailPath : null;
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }
        }

        private void VisualMembersGrid_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                PlaySelectedVisualMember();
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CopySelectedVisualMemberPath();
            }
        }

        private void PlaySelectedVisualMember()
        {
            if (SelectedVisualMember() is { } member)
                PlayVisualMember(member);
        }

        private void PlayVisualMember(VisualSimilarityMemberRecord member)
        {
            PlayLibraryVideo(member.FullPath);
        }

        private void OpenSelectedVisualMemberFolder()
        {
            if (SelectedVisualMember() is { } member)
                OpenVisualMemberFolder(member);
        }

        private void OpenVisualMemberFolder(VisualSimilarityMemberRecord member)
        {
            OpenLibraryFileLocation(member.FullPath);
        }

        private void CopySelectedVisualMemberPath()
        {
            if (SelectedVisualMember() is not { FullPath.Length: > 0 } member)
                return;
            try
            {
                Clipboard.SetText(member.FullPath);
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Copy Library Analyzer visual member path failed", member.FullPath, ex);
            }
        }

        private static void DisposeVisualReviewImages(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is PictureBox { Image: { } image } picture)
                {
                    picture.Image = null;
                    image.Dispose();
                }
                if (control.HasChildren)
                    DisposeVisualReviewImages(control);
            }
        }

        private string VisualPreviewCacheRoot => string.IsNullOrWhiteSpace(_reviewOptions.PreviewCacheRoot)
            ? AppPaths.UserDataDirectory
            : _reviewOptions.PreviewCacheRoot;
    }
}
