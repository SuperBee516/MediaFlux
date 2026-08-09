using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private async Task<bool> PreviewVisualCleanupAsync(IReadOnlyCollection<long>? groupIds, long? requiredCandidateId = null)
        {
            try
            {
                bool allowUnreviewed = groupIds == null && _cleanupOptions.AllowUnreviewedVisualBulkCleanup;
                double minimumConfidence = _cleanupOptions.VisualBulkCleanupMinimumConfidence;
                VisualCleanupProposal proposal = await Task.Run(() => _runtime.VisualDuplicateCleanup.BuildProposal(
                    allowUnreviewed, minimumConfidence, groupIds));
                VisualCleanupProposalItem[] initial = proposal.Items
                    .Where(item => !requiredCandidateId.HasValue || item.Candidate.FileId == requiredCandidateId.Value)
                    .ToArray();
                if (initial.Length == 0)
                {
                    MessageBox.Show(this,
                        "No safe visual cleanup candidates remain. Choose a valid keeper, mark the match reviewed (unless advanced mode is enabled), and ensure the other file is present and unprotected.",
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
                    Height = allowUnreviewed ? 72 : 54,
                    Padding = new Padding(10),
                    ForeColor = allowUnreviewed || _cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete ? Color.DarkRed : SystemColors.ControlText,
                    Text = (_cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete ? "PERMANENT DELETE — selected files cannot be recovered.\r\n" : "") +
                           (allowUnreviewed ? $"ADVANCED MODE — unreviewed matches at or above {minimumConfidence:0.0}% may be included. Visual similarity can produce false positives.\r\n" : "Only reviewed visual matches are eligible.\r\n") +
                           "Uncheck any row you do not want to remove. Every file will be revalidated immediately before cleanup."
                };
                var grid = CreateGrid();
                grid.Name = "VisualCleanupPreviewGrid";
                grid.ReadOnly = false;
                grid.MultiSelect = false;
                grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Delete", Width = 55 });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Confidence", HeaderText = "Confidence", Width = 85, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Keeper", HeaderText = "Keep", Width = 390, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Candidate", HeaderText = "Delete", Width = 390, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Reclaim", Width = 90, ReadOnly = true });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Evidence", HeaderText = "Evidence / recommendation", Width = 300, ReadOnly = true });
                foreach (VisualCleanupProposalItem item in initial) AddVisualCleanupPreviewRow(grid, item);

                var summary = new Label { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 5, 0, 0) };
                void UpdateSummary()
                {
                    VisualCleanupProposalItem[] selected = grid.Rows.Cast<DataGridViewRow>()
                        .Where(row => Convert.ToBoolean(row.Cells["Include"].Value ?? false))
                        .Select(row => (VisualCleanupProposalItem)row.Tag!).ToArray();
                    summary.Text = $"Selected: {selected.Length:N0} files · estimated reclaimable space {FormatBytes(selected.Sum(x => x.Candidate.SizeBytes))} · {proposal.ExcludedGroups:N0} groups excluded by safety/eligibility rules" +
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
                    if (item.Keeper.IsProtected)
                    {
                        MessageBox.Show(dialog, "The current keeper is protected and cannot become a deletion candidate.", "Protected file", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var changed = item with { Keeper = item.Candidate, Candidate = item.Keeper, KeeperReason = "Manual keeper override" };
                    _runtime.VisualCatalog.SaveVisualDecision(new VisualGroupDecision(item.Group.GroupId, changed.Keeper.FileId, true, item.Group.Ignored));
                    DataGridViewRow row = grid.SelectedRows[0]; row.Tag = changed;
                    row.Cells["Keeper"].Value = changed.Keeper.FullPath; row.Cells["Candidate"].Value = changed.Candidate.FullPath;
                    row.Cells["Size"].Value = FormatBytes(changed.Candidate.SizeBytes); row.Cells["Evidence"].Value = "Manual keeper override";
                    UpdateSummary();
                };
                footer.Controls.Add(execute); footer.Controls.Add(cancel); footer.Controls.Add(swap);
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
                string confirmation = $"Execute the approved visual cleanup plan?\r\n\r\nAction: {CleanupActionLabel(_cleanupOptions.PreferredAction)}\r\nFiles: {approved.Length:N0}\r\nEstimated space: {FormatBytes(approved.Sum(x => x.Candidate.SizeBytes))}\r\n\r\n" +
                                      "Visual similarity is probabilistic. MediaFlux will revalidate keeper existence, decisions, protection, identity, timestamps, fingerprints, and optional exact hashes before each action.";
                if (_cleanupOptions.PreferredAction == DuplicateCleanupAction.PermanentDelete)
                    confirmation = "WARNING: PERMANENT DELETION CANNOT BE UNDONE.\r\n\r\n" + confirmation;
                if (MessageBox.Show(this, confirmation, "Confirm visual duplicate cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
                VisualCleanupPlanRecord plan = await Task.Run(() => _runtime.VisualDuplicateCleanup.CreatePlan(approved, _cleanupOptions.PreferredAction, quarantine, allowUnreviewed, minimumConfidence));
                DuplicateCleanupExecutionResult result = await _runtime.VisualDuplicateCleanup.ExecutePlanAsync(plan.PlanId);
                MessageBox.Show(this, $"Visual cleanup plan {result.PlanId} finished.\r\n\r\nSucceeded: {result.Succeeded:N0}\r\nExcluded by revalidation: {result.Excluded:N0}\r\nFailed: {result.Failed:N0}\r\n\r\nRescan affected locations to reconcile the catalog.",
                    "Library Analyzer cleanup", MessageBoxButtons.OK, result.Failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                await RefreshVisualGroupsAsync();
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
            int row = grid.Rows.Add(true, $"{item.Group.ConfidenceScore:0.0}%", item.Keeper.FullPath, item.Candidate.FullPath,
                FormatBytes(item.Candidate.SizeBytes), item.HasExactEvidence ? "Existing SHA-256 match (definitive)" : item.KeeperReason);
            grid.Rows[row].Tag = item;
        }
    }
}
