using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private readonly DataGridView _recommendationsGrid = CreateGrid();
        private readonly Label _recommendationsStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 7, 0, 0) };
        private readonly DataGridView _optimizationGrid = CreateGrid();
        private readonly Label _optimizationStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 7, 0, 0) };

        private void BuildRecommendationsTab()
        {
            var tab = new TabPage("Cleanup Recommendations") { Padding = new Padding(10) };
            var intro = new Label
            {
                Name = "CleanupRecommendationsIntro",
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(4),
                ForeColor = LibraryAnalyzerAccentColor,
                Text = "This view estimates reclaimable storage from currently eligible catalog records. It never deletes files; visual suggestions still require separate review and cleanup confirmation."
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
            AddButton(actions, "Refresh", async (_, _) => await RefreshRecommendationsAsync());
            _recommendationsGrid.Columns.Add("Category", "Category");
            _recommendationsGrid.Columns.Add("Safety", "Status");
            _recommendationsGrid.Columns.Add("Matches", "Files / matches");
            _recommendationsGrid.Columns.Add("Space", "Reclaimable storage");
            _recommendationsGrid.Columns.Add("Details", "What this means");
            _recommendationsGrid.Columns[4].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            tab.Controls.Add(_recommendationsGrid);
            tab.Controls.Add(_recommendationsStatus);
            tab.Controls.Add(intro);
            tab.Controls.Add(actions);
            _tabs.TabPages.Add(tab);
        }

        private async Task RefreshRecommendationsAsync()
        {
            LibraryVisualReviewAutomationOptions options = (_reviewOptions.AutomationOptions ?? new LibraryVisualReviewAutomationOptions()).Normalize();
            LibraryCleanupRecommendationDashboard dashboard = await Task.Run(() =>
                _runtime.Recommendations.GetCleanupDashboard(options.MinimumVisualConfidence));
            if (IsDisposed) return;
            _recommendationsGrid.Rows.Clear();
            foreach (LibraryCleanupRecommendationCategory category in dashboard.Categories)
            {
                _recommendationsGrid.Rows.Add(category.Name, category.SafetyLabel, category.MatchCount.ToString("N0"),
                    FormatBytes(category.ReclaimableBytes), category.Description);
            }
            _recommendationsStatus.Text = $"Calculated {dashboard.CalculatedUtc.ToLocalTime():g}. Values are non-overlapping cleanup candidates, not actions.";
        }

        private void BuildStorageOptimizationTab()
        {
            var tab = new TabPage("Storage Optimization") { Padding = new Padding(10) };
            var intro = new Label
            {
                Name = "StorageOptimizationIntro",
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(4),
                ForeColor = LibraryAnalyzerAccentColor,
                Text = "Potential re-encode opportunities are ranked from catalog metadata. Adding files only places them in the normal Encode queue; it does not start encoding."
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
            AddButton(actions, "Refresh", async (_, _) => await RefreshStorageOptimizationAsync());
            AddButton(actions, "Add selected to Encode queue", AddOptimizationSelectionToQueue_Click);
            _optimizationGrid.MultiSelect = true;
            _optimizationGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _optimizationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", Visible = false });
            _optimizationGrid.Columns.Add("Score", "Opportunity");
            _optimizationGrid.Columns.Add("Size", "Size");
            _optimizationGrid.Columns.Add("Codec", "Codec");
            _optimizationGrid.Columns.Add("Resolution", "Resolution");
            _optimizationGrid.Columns.Add("Bitrate", "Bitrate");
            _optimizationGrid.Columns.Add("HDR", "HDR");
            _optimizationGrid.Columns.Add("Rationale", "Why it is listed");
            _optimizationGrid.Columns.Add("Path", "Path");
            _optimizationGrid.Columns[7].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _optimizationGrid.Columns[8].Width = 360;
            tab.Controls.Add(_optimizationGrid);
            tab.Controls.Add(_optimizationStatus);
            tab.Controls.Add(intro);
            tab.Controls.Add(actions);
            _tabs.TabPages.Add(tab);
        }

        private async Task RefreshStorageOptimizationAsync()
        {
            IReadOnlyList<LibraryStorageOptimizationCandidate> candidates = await Task.Run(() =>
                _runtime.Recommendations.GetStorageOptimizationCandidates());
            if (IsDisposed) return;
            _optimizationGrid.Rows.Clear();
            foreach (LibraryStorageOptimizationCandidate candidate in candidates)
            {
                int rowIndex = _optimizationGrid.Rows.Add(candidate.FileId, $"{candidate.OpportunityScore:0.0}", FormatBytes(candidate.SizeBytes),
                    candidate.VideoCodec.ToUpperInvariant(), candidate.Width.HasValue && candidate.Height.HasValue ? $"{candidate.Width}×{candidate.Height}" : "Unknown",
                    candidate.TotalBitRate.HasValue ? $"{candidate.TotalBitRate.Value / 1_000_000d:0.##} Mbps" : "Unknown", candidate.IsHdr ? "Yes" : "No",
                    candidate.Rationale, candidate.FullPath);
                _optimizationGrid.Rows[rowIndex].Tag = candidate;
            }
            _optimizationStatus.Text = $"{candidates.Count:N0} non-duplicate candidates. Rankings are recommendations, not estimated output sizes.";
        }

        private void AddOptimizationSelectionToQueue_Click(object? sender, EventArgs e)
        {
            string[] paths = _optimizationGrid.SelectedRows.Cast<DataGridViewRow>()
                .Select(row => row.Tag as LibraryStorageOptimizationCandidate)
                .Where(candidate => candidate != null && File.Exists(candidate.FullPath))
                .Select(candidate => candidate!.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                MessageBox.Show(this, "Select one or more currently available candidates first.", "Storage Optimization", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_reviewOptions.AddToEncodeQueue == null)
            {
                MessageBox.Show(this, "The Encode queue is not available in this Library Analyzer session.", "Storage Optimization", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _reviewOptions.AddToEncodeQueue(paths);
            _optimizationStatus.Text = $"Sent {paths.Length:N0} selected file(s) to the normal Encode queue.";
        }
    }
}
