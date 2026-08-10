using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private async Task PreviewMassReviewAsync()
        {
            LibraryVisualReviewAutomationOptions options = (_reviewOptions.AutomationOptions ?? new LibraryVisualReviewAutomationOptions()).Normalize();
            LibraryMassReviewPreview preview;
            try
            {
                UseWaitCursor = true;
                preview = await Task.Run(() => _runtime.MassReview.CreatePreview(options));
            }
            catch (Exception ex)
            {
                ShowError("The mass-review preview could not be created.", ex);
                return;
            }
            finally
            {
                UseWaitCursor = false;
            }

            using var dialog = new MediaFluxForm
            {
                Text = "Mass Review Preview",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(1120, 650),
                MinimumSize = new Size(900, 460)
            };
            var explanation = new Label
            {
                Dock = DockStyle.Top,
                Height = 62,
                Padding = new Padding(10, 8, 10, 0),
                ForeColor = Color.DarkOrange,
                Text = $"Preview only: up to {options.MaximumMassReviewMatches:N0} unreviewed pair matches with confidence ≥ {options.MinimumVisualConfidence:0.0}% and automation margin ≥ {options.MinimumAutomationMargin:0.0}. " +
                       "Applying records keeper and reviewed decisions only; it never deletes files. Each included match will be revalidated."
            };
            var grid = CreateGrid();
            grid.Dock = DockStyle.Fill;
            grid.MultiSelect = false;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Apply", Width = 50 });
            grid.Columns.Add("Group", "Match");
            grid.Columns.Add("Confidence", "Confidence");
            grid.Columns.Add("Margin", "Margin");
            grid.Columns.Add("Keeper", "Recommended keeper");
            grid.Columns.Add("Explanation", "Why it qualifies");
            grid.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            foreach (LibraryMassReviewPreviewItem item in preview.EligibleItems)
            {
                int row = grid.Rows.Add(true, item.GroupKey, $"{item.Confidence:0.0}%", $"{item.Margin:0.0}", item.KeeperPath, item.Explanation);
                grid.Rows[row].Tag = item;
            }
            var status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Padding = new Padding(10, 6, 10, 0),
                Text = $"{preview.EligibleItems.Count:N0} eligible; {preview.ExcludedItems.Count:N0} excluded. Batch {preview.BatchId}."
            };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), WrapContents = false };
            var close = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 95 };
            var apply = new Button { Text = "Apply selected reviews", Width = 160 };
            footer.Controls.Add(close);
            footer.Controls.Add(apply);
            dialog.Controls.Add(grid);
            dialog.Controls.Add(status);
            dialog.Controls.Add(footer);
            dialog.Controls.Add(explanation);

            apply.Click += async (_, _) =>
            {
                long[] included = grid.Rows.Cast<DataGridViewRow>()
                    .Where(row => Convert.ToBoolean(row.Cells["Include"].Value ?? false))
                    .Select(row => row.Tag as LibraryMassReviewPreviewItem)
                    .Where(item => item != null)
                    .Select(item => item!.GroupId)
                    .ToArray();
                if (included.Length == 0)
                {
                    MessageBox.Show(dialog, "Select at least one previewed match.", "Mass Review", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (MessageBox.Show(dialog,
                    $"Apply keeper and reviewed decisions to {included.Length:N0} selected match(es)?\r\n\r\nNo files will be deleted. Changed, unavailable, protected, or otherwise ineligible matches will be skipped.",
                    "Confirm Mass Review", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                    return;
                try
                {
                    apply.Enabled = false;
                    LibraryMassReviewApplyResult result = await Task.Run(() => _runtime.MassReview.Apply(preview, included));
                    MessageBox.Show(dialog,
                        $"Mass review completed.\r\n\r\nApplied: {result.Applied:N0}\r\nSkipped after revalidation: {result.Excluded:N0}\r\nBatch: {result.BatchId}",
                        "Mass Review", MessageBoxButtons.OK, result.Excluded > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                }
                catch (Exception ex)
                {
                    apply.Enabled = true;
                    ShowError("The mass-review batch could not be applied.", ex);
                }
            };
            dialog.ShowDialog(this);
            await RefreshVisualGroupsAsync();
        }
    }
}
