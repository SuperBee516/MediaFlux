using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private readonly DataGridView _recommendationsGrid = CreateGrid();
        private readonly Label _recommendationsStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 7, 0, 0) };
        private readonly DataGridView _optimizationGrid = CreateGrid();
        private readonly Label _optimizationStatus = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 7, 0, 0) };
        private readonly ComboBox _policySelection = DropDown();
        private readonly ComboBox _policyStateFilter = DropDown();
        private readonly Label _policySummary = new() { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8, 4, 0, 0) };
        private readonly Label _policyPageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
        private int _policyPage;
        private long _policyFilteredCount;

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
            var tab = new TabPage("Library Policies") { Padding = new Padding(10) };
            var intro = new Label
            {
                Name = "LibraryPoliciesIntro",
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(4),
                ForeColor = LibraryAnalyzerAccentColor,
                Text = "Evaluate the catalog against an explicit library policy. Results are advisory: nothing is encoded, remuxed, deleted, or queued until you select eligible rows. Projections use existing catalog metadata only."
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, WrapContents = true };
            actions.Controls.Add(new Label { Text = "Policy:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            _policySelection.Name = "LibraryPolicySelection";
            _policySelection.Width = 230;
            _policySelection.SelectedIndexChanged += async (_, _) => { _policyPage = 0; await RefreshStorageOptimizationAsync(); };
            actions.Controls.Add(_policySelection);
            actions.Controls.Add(new Label { Text = "Show:", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
            _policyStateFilter.Name = "LibraryPolicyStateFilter";
            _policyStateFilter.Width = 170;
            _policyStateFilter.Items.Add(new PolicyStateChoice("All results", null));
            foreach (LibraryPolicyComplianceState state in Enum.GetValues<LibraryPolicyComplianceState>())
                _policyStateFilter.Items.Add(new PolicyStateChoice(PolicyStateLabel(state), state));
            _policyStateFilter.SelectedIndex = 0;
            _policyStateFilter.SelectedIndexChanged += async (_, _) => { _policyPage = 0; await RefreshStorageOptimizationAsync(); };
            actions.Controls.Add(_policyStateFilter);
            AddButton(actions, "Refresh", async (_, _) => { _runtime.PolicyEvaluation.Invalidate(); await RefreshStorageOptimizationAsync(); });
            AddButton(actions, "New…", (_, _) => EditPolicy(null));
            AddButton(actions, "Clone…", (_, _) => CloneSelectedPolicy());
            AddButton(actions, "Edit…", (_, _) => EditSelectedPolicy());
            AddButton(actions, "Delete", (_, _) => DeleteSelectedPolicy());
            AddButton(actions, "Previous", async (_, _) => { if (_policyPage > 0) { _policyPage--; await RefreshStorageOptimizationAsync(); } });
            AddButton(actions, "Next", async (_, _) => { if ((_policyPage + 1L) * PageSize < _policyFilteredCount) { _policyPage++; await RefreshStorageOptimizationAsync(); } });
            actions.Controls.Add(_policyPageLabel);
            AddButton(actions, "Add selected candidates to Encode queue", AddOptimizationSelectionToQueue_Click);
            _optimizationGrid.MultiSelect = true;
            _optimizationGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _optimizationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", Visible = false });
            _optimizationGrid.Columns.Add("State", "Compliance");
            _optimizationGrid.Columns.Add("Action", "Suggested action");
            _optimizationGrid.Columns.Add("Score", "Opportunity");
            _optimizationGrid.Columns.Add("Size", "Original size");
            _optimizationGrid.Columns.Add("Projected", "Projected output");
            _optimizationGrid.Columns.Add("Savings", "Projected savings");
            _optimizationGrid.Columns.Add("Confidence", "Confidence");
            _optimizationGrid.Columns.Add("Runtime", "Estimated encode time");
            _optimizationGrid.Columns.Add("Efficiency", "Savings / hour");
            _optimizationGrid.Columns.Add("RuntimeConfidence", "Runtime confidence");
            _optimizationGrid.Columns.Add("Current", "Current characteristics");
            _optimizationGrid.Columns.Add("Proposed", "Proposed characteristics");
            _optimizationGrid.Columns.Add("Rationale", "Reasons / review gates");
            _optimizationGrid.Columns.Add("Path", "Path");
            _optimizationGrid.Columns["Rationale"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _optimizationGrid.Columns["Path"].Width = 320;
            tab.Controls.Add(_optimizationGrid);
            tab.Controls.Add(_optimizationStatus);
            tab.Controls.Add(_policySummary);
            tab.Controls.Add(intro);
            tab.Controls.Add(actions);
            _tabs.TabPages.Add(tab);
            ReloadPolicyChoices();
        }

        private async Task RefreshStorageOptimizationAsync()
        {
            if (_policySelection.SelectedItem is not PolicyChoice selected)
            {
                _optimizationGrid.Rows.Clear();
                _policySummary.Text = "No policy is active. Select a built-in or custom policy to evaluate the catalog.";
                _optimizationStatus.Text = "Existing users are not assigned a policy automatically.";
                _policyPageLabel.Text = "";
                return;
            }
            LibraryPolicyCapabilitySnapshot capabilities = _reviewOptions.PolicyCapabilities ?? new LibraryPolicyCapabilitySnapshot();
            LibraryPolicyComplianceState? state = (_policyStateFilter.SelectedItem as PolicyStateChoice)?.State;
            var query = new LibraryPolicyResultQuery(State: state, Offset: _policyPage * PageSize, Limit: PageSize);
            (LibraryPolicyEvaluationPage Page, LibraryPolicyEvaluationSummary Summary) result = await Task.Run(() =>
                _runtime.PolicyEvaluation.Evaluate(selected.Policy, query, capabilities));
            if (IsDisposed) return;
            _optimizationGrid.Rows.Clear();
            foreach (LibraryPolicyEvaluationResult candidate in result.Page.Results)
            {
                string reasons = string.Join(" ", candidate.Reasons.Concat(candidate.ReviewReasons));
                string savings = candidate.ProjectedReclaimableBytes.HasValue
                    ? $"{FormatBytes(candidate.ProjectedReclaimableBytes.Value)} ({candidate.ProjectedSavingsPercent:0.#}%)" : "Unknown";
                EncodingRuntimeEstimate estimate = candidate.State == LibraryPolicyComplianceState.OptimizationCandidate &&
                    candidate.SuggestedAction == LibraryPolicySuggestedAction.Reencode && _reviewOptions.RuntimeEstimator != null
                    ? _reviewOptions.RuntimeEstimator.Estimate(candidate) : new EncodingRuntimeEstimate();
                double? efficiencyGb = estimate.EstimatedProcessingSeconds is > 0 && candidate.ProjectedReclaimableBytes is > 0
                    ? candidate.ProjectedReclaimableBytes.Value * 3600d / estimate.EstimatedProcessingSeconds.Value / (1024d * 1024 * 1024) : null;
                int rowIndex = _optimizationGrid.Rows.Add(candidate.FileId, PolicyStateLabel(candidate.State), candidate.SuggestedAction,
                    $"{candidate.OpportunityScore:0.0}", FormatBytes(candidate.OriginalSizeBytes),
                    candidate.ProjectedOutputBytes.HasValue ? FormatBytes(candidate.ProjectedOutputBytes.Value) : "Unknown",
                    savings, candidate.Confidence, FormatRuntimeHours(estimate.EstimatedProcessingSeconds / 3600d), FormatEfficiency(efficiencyGb),
                    estimate.Confidence == RuntimeEstimateConfidence.Unknown ? "Unknown" : estimate.Confidence,
                    candidate.CurrentCharacteristics, candidate.ProposedCharacteristics, reasons, candidate.FullPath);
                _optimizationGrid.Rows[rowIndex].Tag = candidate;
            }
            _policyFilteredCount = result.Page.TotalCount;
            long first = result.Page.TotalCount == 0 ? 0 : (long)_policyPage * PageSize + 1;
            long last = Math.Min(result.Page.TotalCount, (long)(_policyPage + 1) * PageSize);
            _policyPageLabel.Text = result.Page.TotalCount == 0 ? "No rows" : $"{first:N0}–{last:N0} of {result.Page.TotalCount:N0}";
            _policySummary.Text = $"Evaluated {result.Summary.FilesEvaluated:N0}: {result.Summary.Compliant:N0} compliant, {result.Summary.OptimizationCandidates:N0} candidates, " +
                $"{result.Summary.ReviewRequired:N0} review, {result.Summary.NotApplicable:N0} not applicable, {result.Summary.UnableToEvaluate:N0} unavailable. " +
                $"Candidate reclaimable projection: {FormatBytes(result.Summary.ProjectedReclaimableBytes)}.";
            _optimizationStatus.Text = "Metadata-only advisory evaluation. Rounded projections are estimates, not promises; no deep probes or media samples were run.";
        }

        private void AddOptimizationSelectionToQueue_Click(object? sender, EventArgs e)
        {
            LibraryPolicyEvaluationResult[] candidates = _optimizationGrid.SelectedRows.Cast<DataGridViewRow>()
                .Select(row => row.Tag as LibraryPolicyEvaluationResult)
                .Where(candidate => candidate?.State == LibraryPolicyComplianceState.OptimizationCandidate &&
                                    candidate.SuggestedAction == LibraryPolicySuggestedAction.Reencode && File.Exists(candidate.FullPath))
                .Select(candidate => candidate!)
                .GroupBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .ToArray();
            LibraryPolicyQueueItem[] items = candidates.Select(candidate =>
                {
                    EncodingRuntimeEstimate estimate = _reviewOptions.RuntimeEstimator?.Estimate(candidate) ?? new EncodingRuntimeEstimate();
                    double? efficiency = estimate.EstimatedProcessingSeconds is > 0 && candidate.ProjectedReclaimableBytes is > 0
                        ? candidate.ProjectedReclaimableBytes.Value * 3600d / estimate.EstimatedProcessingSeconds.Value : null;
                    return new LibraryPolicyQueueItem(candidate.FullPath, candidate.PolicyId, candidate.PolicyName, candidate.ProposedCodec,
                        candidate.EncoderId, candidate.EncoderPreset, candidate.EncodingPresetName, candidate.QualityValue,
                        candidate.PreferredBitDepth, candidate.PreserveSourceResolution, candidate.MaximumOutputHeight, candidate.PreserveHdr,
                        candidate.TargetContainer, candidate.ProjectedOutputBytes, candidate.Confidence, estimate.EstimatedProcessingSeconds,
                        efficiency, estimate.Confidence, estimate.CohortExplanation);
                })
                .ToArray();
            if (items.Length == 0)
            {
                MessageBox.Show(this, "Select one or more available re-encode candidates first. Review, remux-only, compliant, and unavailable rows remain advisory.", "Library Policies", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_reviewOptions.AddPolicyCandidatesToEncodeQueue == null)
            {
                MessageBox.Show(this, "The Encode queue is not available in this Library Analyzer session.", "Library Policies", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _ = QueuePolicyItemsAsync(items);
        }

        private async Task QueuePolicyItemsAsync(IReadOnlyList<LibraryPolicyQueueItem> items)
        {
            try
            {
                await _reviewOptions.AddPolicyCandidatesToEncodeQueue!(items);
                if (!IsDisposed) _optimizationStatus.Text = $"Added {items.Count:N0} selected candidate(s) to the normal Encode queue with isolated policy settings. Encoding was not started.";
            }
            catch (Exception ex)
            {
                if (!IsDisposed) ShowError("Library policy queue handoff failed. No encode was started.", ex);
            }
        }

        private void ReloadPolicyChoices(string? selectId = null)
        {
            LibraryPolicyStore store = _reviewOptions.PolicyStore ?? new LibraryPolicyStore(AppPaths.LibraryPolicyFile);
            string? previous = selectId ?? (_policySelection.SelectedItem as PolicyChoice)?.Policy.Id;
            _policySelection.BeginUpdate();
            _policySelection.Items.Clear();
            foreach (LibraryPolicyDefinition policy in store.LoadAll()) _policySelection.Items.Add(new PolicyChoice(policy));
            _policySelection.EndUpdate();
            if (!string.IsNullOrWhiteSpace(previous))
                _policySelection.SelectedItem = _policySelection.Items.Cast<PolicyChoice>().FirstOrDefault(item => item.Policy.Id.Equals(previous, StringComparison.OrdinalIgnoreCase));
            else
                _policySelection.SelectedIndex = -1;
            ReloadReclamationPolicies();
        }

        private void EditPolicy(LibraryPolicyDefinition? source)
        {
            LibraryPolicyDefinition policy = source ?? new LibraryPolicyDefinition();
            using var dialog = new LibraryPolicyEditorDialog(policy);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            (_reviewOptions.PolicyStore ?? new LibraryPolicyStore(AppPaths.LibraryPolicyFile)).Save(dialog.Policy);
            _runtime.PolicyEvaluation.Invalidate();
            ReloadPolicyChoices(dialog.Policy.Id);
        }

        private void CloneSelectedPolicy()
        {
            if (_policySelection.SelectedItem is not PolicyChoice selected) return;
            EditPolicy(selected.Policy.CloneAsCustom($"{selected.Policy.Name} copy"));
        }

        private void EditSelectedPolicy()
        {
            if (_policySelection.SelectedItem is not PolicyChoice selected) return;
            if (selected.Policy.IsBuiltIn)
            {
                MessageBox.Show(this, "Built-in policies are read-only. Clone this policy to customize it.", "Library Policies", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            EditPolicy(selected.Policy);
        }

        private void DeleteSelectedPolicy()
        {
            if (_policySelection.SelectedItem is not PolicyChoice selected) return;
            if (selected.Policy.IsBuiltIn)
            {
                MessageBox.Show(this, "Built-in policies cannot be deleted.", "Library Policies", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, $"Delete custom policy '{selected.Policy.Name}'?", "Library Policies", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            (_reviewOptions.PolicyStore ?? new LibraryPolicyStore(AppPaths.LibraryPolicyFile)).Delete(selected.Policy.Id);
            _runtime.PolicyEvaluation.Invalidate();
            ReloadPolicyChoices();
            _ = RefreshStorageOptimizationAsync();
        }

        private static string PolicyStateLabel(LibraryPolicyComplianceState state) => state switch
        {
            LibraryPolicyComplianceState.OptimizationCandidate => "Candidate",
            LibraryPolicyComplianceState.ReviewRequired => "Review required",
            LibraryPolicyComplianceState.NotApplicable => "Not applicable",
            LibraryPolicyComplianceState.UnableToEvaluate => "Unable to evaluate",
            _ => "Compliant"
        };

        private sealed record PolicyChoice(LibraryPolicyDefinition Policy)
        {
            public override string ToString() => Policy.IsBuiltIn ? $"{Policy.Name} (built-in)" : Policy.Name;
        }
        private sealed record PolicyStateChoice(string Name, LibraryPolicyComplianceState? State)
        {
            public override string ToString() => Name;
        }
    }
}
