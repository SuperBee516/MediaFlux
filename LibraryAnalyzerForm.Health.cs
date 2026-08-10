using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed partial class LibraryAnalyzerForm
    {
        private readonly DataGridView _healthGrid = CreateGrid();
        private readonly DataGridView _historyGrid = CreateGrid();
        private readonly Label _healthStatus = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
        private readonly Button _healthRebuild = new() { Text = "Rebuild catalog…", AutoSize = true, Enabled = false };
        private LibraryHealthSnapshot? _healthSnapshot;

        private void BuildHealthTab()
        {
            var tab = new TabPage("Health & Recovery") { Padding = new Padding(8) };
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, AutoScroll = true };
            AddButton(toolbar, "Refresh health", async (_, _) => await RefreshHealthAsync());
            AddButton(toolbar, "Queue suggested re-analysis", QueueHealthReanalysis_Click);
            AddButton(toolbar, "Restore selected quarantine", RestoreQuarantine_Click);
            AddButton(toolbar, "Undo selected decision", UndoDecision_Click);
            _healthRebuild.Click += RebuildCatalog_Click;
            toolbar.Controls.Add(_healthRebuild);
            toolbar.Controls.Add(_healthStatus);

            AddHealthColumn("Severity", "Severity", 80);
            AddHealthColumn("Problem", "Actionable problem", 220);
            AddHealthColumn("Details", "Details", 430);
            AddHealthColumn("Action", "Recommended action", 300);
            _healthGrid.MultiSelect = true;

            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "When", HeaderText = "When", Width = 145 });
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Action", HeaderText = "Decision", Width = 140 });
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = "Target", Width = 260 });
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Source / batch", Width = 180 });
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Undo", HeaderText = "Undo", Width = 90 });

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330, Panel1MinSize = 180, Panel2MinSize = 150 };
            var issuesBox = new GroupBox { Text = "Actionable catalog issues", Dock = DockStyle.Fill, Padding = new Padding(6) };
            issuesBox.Controls.Add(_healthGrid);
            var historyBox = new GroupBox { Text = "Recent Library Analyzer decisions", Dock = DockStyle.Fill, Padding = new Padding(6) };
            historyBox.Controls.Add(_historyGrid);
            split.Panel1.Controls.Add(issuesBox); split.Panel2.Controls.Add(historyBox);
            tab.Controls.Add(split); tab.Controls.Add(toolbar);
            _tabs.TabPages.Add(tab);
        }

        private async Task RefreshHealthAsync()
        {
            try
            {
                (LibraryHealthSnapshot snapshot, IReadOnlyList<LibraryDecisionEvent> history) = await Task.Run(() =>
                    (_runtime.Insights.GetHealth(), _runtime.Decisions.GetRecent()));
                if (IsDisposed) return;
                _healthSnapshot = snapshot;
                _healthGrid.Rows.Clear();
                foreach (LibraryHealthIssue issue in snapshot.Issues)
                {
                    int row = _healthGrid.Rows.Add(issue.Severity, issue.Title, issue.Details, issue.RecommendedAction);
                    _healthGrid.Rows[row].Tag = issue;
                    if (issue.Severity == LibraryHealthSeverity.Error) _healthGrid.Rows[row].DefaultCellStyle.ForeColor = Color.Firebrick;
                }
                _historyGrid.Rows.Clear();
                foreach (LibraryDecisionEvent item in history)
                {
                    int row = _historyGrid.Rows.Add(item.OccurredUtc.ToLocalTime().ToString("g"), item.EventKind,
                        $"{item.TargetKind}: {item.TargetKey}", string.IsNullOrWhiteSpace(item.BatchId) ? item.Source : $"{item.Source} / {item.BatchId}",
                        item.CanUndo && !string.Equals(item.Source, "restored-history", StringComparison.Ordinal) ? "Available" : "Blocked");
                    _historyGrid.Rows[row].Tag = item;
                }
                _healthRebuild.Enabled = !snapshot.Integrity.IsHealthy;
                _healthStatus.Text = snapshot.Issues.Count == 0 ? "No actionable catalog issues." : $"{snapshot.Issues.Count:N0} actionable issue(s)";
            }
            catch (Exception ex) { ShowError("Library health could not be refreshed.", ex); }
        }

        private async void QueueHealthReanalysis_Click(object? sender, EventArgs e)
        {
            LibraryHealthIssue[] issues = _healthGrid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Tag)
                .OfType<LibraryHealthIssue>().Where(x => x.FileId.HasValue && x.SuggestedReanalysis != LibraryReanalysisWork.None).ToArray();
            if (issues.Length == 0) return;
            string batch = Guid.NewGuid().ToString("N");
            await Task.Run(() =>
            {
                foreach (LibraryHealthIssue issue in issues)
                    _runtime.Reanalysis.Queue(issue.FileId!.Value, issue.SuggestedReanalysis, batch);
            });
            await RefreshHealthAsync();
        }

        private async void UndoDecision_Click(object? sender, EventArgs e)
        {
            if (_historyGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is not LibraryDecisionEvent item) return;
            LibraryDecisionUndoResult result = await Task.Run(() => _runtime.Decisions.Undo(item.Id));
            MessageBox.Show(this, result.Message, "Library Analyzer decision", MessageBoxButtons.OK,
                result.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            await RefreshHealthAsync();
            await RefreshSelectedAnalysisViewsAsync();
        }

        private async void RestoreQuarantine_Click(object? sender, EventArgs e)
        {
            if (_healthGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is not LibraryHealthIssue issue ||
                issue.Kind != LibraryHealthIssueKind.RestorableQuarantine || !issue.CleanupAuditId.HasValue) return;
            LibraryQuarantineRestoreItem? item = await Task.Run(() => _runtime.Insights.GetRestoreCandidates()
                .FirstOrDefault(x => x.AuditId == issue.CleanupAuditId.Value));
            if (item == null) return;
            if (MessageBox.Show(this, $"Restore the quarantined file to its audited original path?\r\n\r\n{item.SourcePath}",
                "Restore quarantine", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            LibraryDecisionUndoResult result = await Task.Run(() => _runtime.Decisions.RestoreQuarantine(item));
            MessageBox.Show(this, result.Message, "Restore quarantine", MessageBoxButtons.OK,
                result.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            await RefreshHealthAsync();
        }

        private async void RebuildCatalog_Click(object? sender, EventArgs e)
        {
            if (_healthSnapshot?.Integrity.IsHealthy != false) return;
            if (MessageBox.Show(this, "The integrity check reports a catalog problem. Back up decisions and rebuild the derived catalog now?\r\n\r\nMedia files will not be changed.",
                "Rebuild damaged catalog", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            try
            {
                string backup = await Task.Run(() => _runtime.AnalysisCatalog.CreateUserDataBackup());
                await Task.Run(() => _runtime.Catalog.RebuildCatalog());
                LibraryUserDataRestoreResult restored = await Task.Run(() => _runtime.AnalysisCatalog.RestoreUserDataBackup(backup));
                MessageBox.Show(this, $"The catalog was rebuilt and user decisions were restored.\r\n\r\n" +
                    $"Exact decisions: {restored.DuplicateDecisions:N0}\r\nProtected paths: {restored.FileProtections:N0}\r\n" +
                    $"Visual pair decisions: {restored.VisualDecisions:N0}\r\nVisual family decisions: {restored.FamilyDecisions:N0}\r\nDecision history: {restored.DecisionEvents:N0}\r\n\r\nBackup:\r\n{backup}",
                    "Library Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshAllAsync(); await RefreshHealthAsync();
            }
            catch (Exception ex) { ShowError("The catalog could not be rebuilt.", ex); }
        }

        private async Task RefreshSelectedAnalysisViewsAsync()
        {
            await RefreshDuplicateGroupsAsync();
            await RefreshVisualGroupsAsync();
        }

        private void AddHealthColumn(string name, string header, int width) => _healthGrid.Columns.Add(
            new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
    }
}
