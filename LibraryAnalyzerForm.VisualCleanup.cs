using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private async Task<bool> PreviewVisualCleanupAsync(IReadOnlyCollection<long>? groupIds, long? requiredCandidateId = null)
            => await PreviewVisualCleanupCoreAsync(groupIds, requiredCandidateId, deleteBoth: false);

        private async Task<bool> PreviewDeleteBothAsync(long groupId) =>
            await PreviewVisualCleanupCoreAsync(new[] { groupId }, null, deleteBoth: true);

        private async Task<bool> PreviewVisualCleanupCoreAsync(IReadOnlyCollection<long>? groupIds, long? requiredCandidateId, bool deleteBoth)
        {
            try
            {
                bool allowUnreviewed = groupIds == null && _cleanupOptions.AllowUnreviewedVisualBulkCleanup;
                double minimumConfidence = _cleanupOptions.VisualBulkCleanupMinimumConfidence;
                VisualCleanupProposal proposal = await Task.Run(() => deleteBoth
                    ? _runtime.VisualDuplicateCleanup.BuildDeleteBothProposal(groupIds?.Single() ?? throw new InvalidOperationException("Delete Both requires one visual match."))
                    : _runtime.VisualDuplicateCleanup.BuildProposal(allowUnreviewed, minimumConfidence, groupIds));
                VisualCleanupProposalItem[] initial = proposal.Items
                    .Where(item => deleteBoth || !requiredCandidateId.HasValue || item.Candidate.FileId == requiredCandidateId.Value)
                    .ToArray();
                if (initial.Length == 0)
                {
                    MessageBox.Show(this,
                        deleteBoth
                            ? "Delete Both is unavailable. Both files must be present, unprotected, unchanged, and independently addressable."
                            : "No safe visual cleanup candidates remain. Choose a valid keeper, mark the match reviewed (unless advanced mode is enabled), and ensure the other file is present and unprotected.",
                        "Visual cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                using var dialog = new MediaFluxForm
                {
                    Text = "Review Visual Duplicate Cleanup Plan",
                    StartPosition = FormStartPosition.CenterParent,
                    Size = new Size(1180, 650),
                    MinimumSize = new Size(900, 500)
                };
                var warning = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 54 + (allowUnreviewed ? 18 : 0) + (deleteBoth ? 18 : 0),
                    Padding = new Padding(10),
                    ForeColor = deleteBoth || allowUnreviewed || _cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete ? Color.DarkRed : SystemColors.ControlText,
                    Text = (deleteBoth ? "DELETE BOTH — NO KEEPER WILL REMAIN FOR THIS MATCH.\r\n" : "") +
                           (_cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete ? "PERMANENT DELETE — selected files cannot be recovered.\r\n" : "") +
                           (allowUnreviewed ? $"ADVANCED MODE — unreviewed matches at or above {minimumConfidence:0.0}% may be included. Visual similarity can produce false positives.\r\n" : "Only reviewed visual matches are eligible.\r\n") +
                           "Uncheck any row you do not want to remove. Every file will be revalidated immediately before cleanup."
                };
                var grid = CreateGrid();
                grid.Name = "VisualCleanupPreviewGrid";
                grid.ReadOnly = false;
                grid.MultiSelect = false;
                grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Delete", Width = 55 });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Intent", HeaderText = "Plan", Width = 105, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Confidence", HeaderText = "Confidence", Width = 85, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Keeper", HeaderText = "Keep", Width = 320, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Candidate", HeaderText = "Delete", Width = 460, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Reclaim", Width = 90, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Evidence", HeaderText = "Evidence / recommendation", Width = 300, ReadOnly = true });
                foreach (VisualCleanupProposalItem item in initial) AddVisualCleanupPreviewRow(grid, item);

                var summary = new Label { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 5, 0, 0) };
                void UpdateSummary()
                {
                    VisualCleanupProposalItem[] selected = grid.Rows.Cast<DataGridViewRow>()
                        .Where(row => Convert.ToBoolean(row.Cells["Include"].Value ?? false))
                        .Select(row => (VisualCleanupProposalItem)row.Tag!).ToArray();
                    int selectedFiles = selected.Sum(item => item.Intent == VisualCleanupIntent.DeleteBoth ? 2 : 1);
                    summary.Text = $"Selected: {selectedFiles:N0} files · estimated reclaimable space {FormatBytes(selected.Sum(x => x.ReclaimableBytes))} · {proposal.ExcludedGroups:N0} groups excluded by safety/eligibility rules" +
                                   (proposal.IsTruncated ? " · preview limited to 5,000 eligible items; run another plan after rescanning" : "");
                }
                grid.CellValueChanged += (_, e) => { if (e.ColumnIndex == grid.Columns["Include"].Index) UpdateSummary(); };
                grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                var execute = new Button { Text = "Approve selected plan…", Width = 170, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
                var swap = new Button { Text = "Swap keeper / candidate", Width = 170 };
                swap.Click += (_, _) =>
                {
                    if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not VisualCleanupProposalItem item) return;
                    if (item.Intent == VisualCleanupIntent.DeleteBoth) return;
                    if (item.Keeper.IsProtected)
                    {
                        MessageBox.Show(dialog, "The current keeper is protected and cannot become a deletion candidate.", "Protected file", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var changed = item with { Keeper = item.Candidate, Candidate = item.Keeper, KeeperReason = "Manual keeper override" };
                    _runtime.VisualCatalog.SaveVisualDecision(new VisualGroupDecision(item.Group.GroupId, changed.Keeper.FileId, true, item.Group.Ignored, item.Group.NotMatch));
                    DataGridViewRow row = grid.SelectedRows[0]; row.Tag = changed;
                    row.Cells["Keeper"].Value = changed.Keeper.FullPath; row.Cells["Candidate"].Value = changed.Candidate.FullPath;
                    row.Cells["Size"].Value = FormatBytes(changed.Candidate.SizeBytes); row.Cells["Evidence"].Value = "Manual keeper override";
                    UpdateSummary();
                };
                footer.Controls.Add(execute); footer.Controls.Add(cancel); footer.Controls.Add(swap);
                swap.Enabled = !deleteBoth;
                dialog.Controls.Add(grid); dialog.Controls.Add(summary); dialog.Controls.Add(footer); dialog.Controls.Add(warning);
                dialog.AcceptButton = execute; dialog.CancelButton = cancel;
                UpdateSummary();
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                VisualCleanupProposalItem[] approved = grid.Rows.Cast<DataGridViewRow>()
                    .Where(row => Convert.ToBoolean(row.Cells["Include"].Value ?? false))
                    .Select(row => (VisualCleanupProposalItem)row.Tag!).ToArray();
                if (approved.Length == 0) return false;

                string quarantine = _cleanupOptions.QuarantineFolder;
                if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.Quarantine && !Directory.Exists(quarantine))
                {
                    MessageBox.Show(this, "The configured quarantine folder is unavailable. No files were changed.", "Visual cleanup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                int approvedFiles = approved.Sum(item => item.Intent == VisualCleanupIntent.DeleteBoth ? 2 : 1);
                string confirmation = (deleteBoth ? "DELETE BOTH: NO KEEPER WILL REMAIN.\r\n\r\n" : "") +
                                      $"Execute the approved visual cleanup plan?\r\n\r\nAction: {CleanupActionLabel(_cleanupOptions.PreferredAction)}\r\nFiles: {approvedFiles:N0}\r\nEstimated space: {FormatBytes(approved.Sum(x => x.ReclaimableBytes))}\r\n\r\n" +
                                      "Visual similarity is probabilistic. MediaFlux will revalidate keeper existence, decisions, protection, identity, timestamps, fingerprints, and optional exact hashes before each action.";
                if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete)
                    confirmation = "WARNING: PERMANENT DELETION CANNOT BE UNDONE.\r\n\r\n" + confirmation;
                if (MessageBox.Show(this, confirmation, "Confirm visual duplicate cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
                VisualCleanupPlanRecord plan = await Task.Run(() => _runtime.VisualDuplicateCleanup.CreatePlan(approved, _cleanupOptions.PreferredAction, quarantine, allowUnreviewed, minimumConfidence));
                DuplicateCleanupExecutionResult result = await _runtime.VisualDuplicateCleanup.ExecutePlanAsync(plan.PlanId);
                MessageBox.Show(this, $"Visual cleanup plan {result.PlanId} finished.\r\n\r\nSucceeded: {result.Succeeded:N0}\r\nExcluded by revalidation: {result.Excluded:N0}\r\nFailed: {result.Failed:N0}\r\n\r\nThe catalog and all duplicate views were reconciled immediately. A later scan can verify the location.",
                    "Library Analyzer cleanup", MessageBoxButtons.OK, result.Failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (result.Succeeded > 0) await RefreshAfterSuccessfulRemovalAsync();
                else await RefreshVisualGroupsAsync();
                return result.Succeeded > 0;
            }
            catch (Exception ex)
            {
                ShowError("The visual cleanup plan could not be completed. No unvalidated files were changed.", ex);
                return false;
            }
        }

        private static void AddVisualCleanupPreviewRow(DataGridView grid, VisualCleanupProposalItem item)
        {
            bool deleteBoth = item.Intent == VisualCleanupIntent.DeleteBoth;
            int row = grid.Rows.Add(true, deleteBoth ? "DELETE BOTH" : "Delete duplicate", $"{item.Group.ConfidenceScore:0.0}%",
                deleteBoth ? "NO KEEPER — BOTH FILES WILL BE DELETED" : item.Keeper.FullPath,
                deleteBoth ? item.Candidate.FullPath + Environment.NewLine + item.Keeper.FullPath : item.Candidate.FullPath,
                FormatBytes(item.ReclaimableBytes), item.HasExactEvidence ? "Existing SHA-256 match (definitive)" : item.KeeperReason);
            grid.Rows[row].Tag = item;
            if (deleteBoth)
            {
                grid.Rows[row].DefaultCellStyle.ForeColor = Color.DarkRed;
                grid.Rows[row].Height = Math.Max(grid.RowTemplate.Height, 44);
            }
        }
    }
}
