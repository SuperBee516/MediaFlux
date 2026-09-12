using System.Diagnostics;
using System.Drawing;
using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

public sealed class JobHistoryForm : MediaFluxForm
{
    private readonly HistoryService _history;
    private readonly Config _config;
    private readonly string _configPath;
    private readonly Func<string, bool>? _addToQueue;
    private readonly Action? _queueChanged;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, RowHeadersVisible = false, Name = "jobHistoryGrid" };
    private readonly TextBox _search = new() { Width = 230, PlaceholderText = "Search source, output, result…", Name = "jobHistorySearch" };
    private readonly ComboBox _status = FilterBox("All", "Successful", "Failed", "Canceled");
    private readonly ComboBox _type = FilterBox("All");
    private readonly ComboBox _date = FilterBox("All", "Today", "Last 7 Days", "Last 30 Days");
    private readonly TabControl _detailsTabs = new() { Dock = DockStyle.Fill, Name = "jobHistoryDetailsTabs" };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Name = "jobHistorySplit" };
    private readonly Label _resultLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(8) };
    private readonly TextBox _technical = DetailsTextBox();
    private readonly TextBox _log = DetailsTextBox();
    private readonly Label[] _countLabels = new Label[4];
    private readonly Button _requeue = new() { Text = "Requeue", Width = 82 };
    private readonly Button _openSource = new() { Text = "Open Source", Width = 96 };
    private readonly Button _openOutput = new() { Text = "Open Output", Width = 96 };
    private List<JobHistoryRecord> _records = new();
    private bool _restoring;

    public JobHistoryForm(HistoryService history, Config config, string configPath, Func<string, bool>? addToQueue = null, Action? queueChanged = null)
    {
        _history = history; _config = config; _configPath = configPath; _addToQueue = addToQueue; _queueChanged = queueChanged;
        Text = "Job History"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.Manual; MinimumSize = new Size(980, 680); Size = RestoreSize(); BackColor = Color.FromArgb(243, 246, 249);
        BuildUi(); RestoreState(); LoadRecords();
        Shown += (_, _) => { _split.Panel1MinSize = 220; _split.Panel2MinSize = 190; ClampSplitter(); }; ResizeEnd += (_, _) => SaveState(); FormClosing += (_, _) => SaveState(); KeyPreview = true; KeyDown += HandleKeyDown;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10), BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = BackColor };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        var title = new Label { Text = "Job History\nReview completed, failed, and canceled MediaFlux operations", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), Padding = new Padding(3, 5, 5, 0) };
        header.Controls.Add(title, 0, 0); header.SetRowSpan(title, 2);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(4, 2, 0, 2) };
        AddCard(cards, 0, "Total Jobs", Color.FromArgb(37, 99, 235), () => SetStatus("All")); AddCard(cards, 1, "Successful", Color.FromArgb(22, 132, 93), () => SetStatus("Successful")); AddCard(cards, 2, "Failed", Color.FromArgb(190, 65, 65), () => SetStatus("Failed")); AddCard(cards, 3, "Canceled", Color.FromArgb(161, 98, 7), () => SetStatus("Canceled"));
        header.Controls.Add(cards, 1, 0); header.SetRowSpan(cards, 2); root.Controls.Add(header, 0, 0);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 9, RowCount = 1, Padding = new Padding(0, 3, 0, 3) };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.Controls.Add(new Label { Text = "Search", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); filters.Controls.Add(_search, 1, 0); filters.Controls.Add(new Label { Text = "Status", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0); filters.Controls.Add(_status, 3, 0); filters.Controls.Add(new Label { Text = "Type", AutoSize = true, Anchor = AnchorStyles.Left }, 4, 0); filters.Controls.Add(_type, 5, 0); filters.Controls.Add(new Label { Text = "Date", AutoSize = true, Anchor = AnchorStyles.Left }, 6, 0); filters.Controls.Add(_date, 7, 0);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        _requeue.Click += (_, _) => Requeue(); _openSource.Click += (_, _) => OpenPath(true); _openOutput.Click += (_, _) => OpenPath(false);
        var refresh = new Button { Text = "Refresh", Width = 82 }; refresh.Click += (_, _) => LoadRecords();
        var more = new Button { Text = "More ▾", Width = 76 }; var moreMenu = new ContextMenuStrip(); AddMenu(moreMenu, "Delete Selected", (_, _) => DeleteSelected()); AddMenu(moreMenu, "Clear All", (_, _) => ClearAll()); more.Click += (_, _) => moreMenu.Show(more, 0, more.Height);
        commands.Controls.AddRange(new Control[] { _requeue, _openSource, _openOutput, more, refresh }); filters.Controls.Add(commands, 8, 0); root.Controls.Add(filters, 0, 1);
        foreach (var control in new Control[] { _search, _status, _type, _date }) control.TextChanged += (_, _) => Bind();
        _grid.Columns.Add(TextColumn("colFinished", "Finished", 145)); _grid.Columns.Add(TextColumn("colStatus", "Status", 105)); _grid.Columns.Add(TextColumn("colType", "Type", 105)); _grid.Columns.Add(TextColumn("colSource", "Source", 230)); _grid.Columns.Add(TextColumn("colOutput", "Output", 230)); _grid.Columns.Add(TextColumn("colResult", "Result", 260, DataGridViewAutoSizeColumnMode.Fill)); ApplyGridState();
        _grid.SelectionChanged += (_, _) => ShowSelected(); _grid.CellFormatting += (_, e) => { if (e.ColumnIndex == _grid.Columns["colFinished"].Index && e.Value is DateTime time) { e.Value = JobHistoryPresentation.FormatFinished(time.ToUniversalTime()); e.FormattingApplied = true; } }; _grid.ColumnHeaderMouseClick += (_, e) => { _config.JobHistorySortColumn = _grid.Columns[e.ColumnIndex].Name; _config.JobHistorySortDescending = _grid.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection == SortOrder.Descending; }; _grid.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is JobHistoryRecord r) e.ToolTipText = e.ColumnIndex < 4 ? r.SourcePath : r.OutputPath; };
        _grid.CellDoubleClick += (_, _) => _detailsTabs.Focus(); _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; } };
        BuildDetails(); _split.Panel1.Controls.Add(_grid); _split.Panel2.Controls.Add(_detailsTabs); root.Controls.Add(_split, 0, 2); Controls.Add(root);
        var menu = new ContextMenuStrip(); AddMenu(menu, "Requeue", (_, _) => Requeue()); AddMenu(menu, "Open Source", (_, _) => OpenPath(true)); AddMenu(menu, "Open Output", (_, _) => OpenPath(false)); menu.Items.Add(new ToolStripSeparator()); AddMenu(menu, "Copy Source Path", (_, _) => Copy(r => r.SourcePath)); AddMenu(menu, "Copy Output Path", (_, _) => Copy(r => r.OutputPath)); AddMenu(menu, "Copy Result", (_, _) => Copy(r => Result(r))); AddMenu(menu, "Copy Technical Details", (_, _) => Copy(r => Technical(r))); menu.Items.Add(new ToolStripSeparator()); AddMenu(menu, "Delete History Entry", (_, _) => DeleteSelected()); _grid.ContextMenuStrip = menu;
    }

    private void BuildDetails()
    { var summary = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White }; summary.Controls.Add(_resultLabel); _detailsTabs.TabPages.Add(new TabPage("Summary") { Controls = { summary } }); _detailsTabs.TabPages.Add(new TabPage("Technical Details") { Controls = { _technical } }); _detailsTabs.TabPages.Add(new TabPage("Log") { Controls = { _log } }); _detailsTabs.SelectedIndexChanged += (_, _) => SaveState(); }
    private void AddCard(TableLayoutPanel panel, int index, string text, Color color, Action click) { var label = new Label { Text = text + "\n0", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.White, ForeColor = color, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(4, 2, 4, 2), Cursor = Cursors.Hand, Tag = click }; label.Click += (_, _) => click(); _countLabels[index] = label; panel.Controls.Add(label, index, 0); }
    private void AddMenu(ContextMenuStrip menu, string text, EventHandler handler) { var item = new ToolStripMenuItem(text) { Name = text.Replace(" ", "") }; item.Click += handler; menu.Items.Add(item); }
    private static DataGridViewTextBoxColumn TextColumn(string name, string header, int width, DataGridViewAutoSizeColumnMode mode = DataGridViewAutoSizeColumnMode.None) => new() { Name = name, HeaderText = header, Width = width, AutoSizeMode = mode, SortMode = DataGridViewColumnSortMode.Automatic };
    private static ComboBox FilterBox(params string[] values) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill }; box.Items.AddRange(values); box.SelectedIndex = 0; return box; }
    private static TextBox DetailsTextBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };

    private void LoadRecords() { _records = _history.LoadAll(); _type.Items.Clear(); _type.Items.Add("All"); foreach (string type in _records.Select(x => JobHistoryPresentation.FormatType(x.Type)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)) _type.Items.Add(type); _type.SelectedIndex = 0; Bind(); }
    private void Bind() { if (_restoring) return; var list = JobHistoryPresentation.Filter(_records, _search.Text, _status.Text, _type.Text, (JobHistoryDateFilter)Math.Max(0, _date.SelectedIndex)); var counts = JobHistoryPresentation.Counts(list); int[] values = { counts.Total, counts.Successful, counts.Failed, counts.Canceled }; for (int i = 0; i < values.Length; i++) _countLabels[i].Text = _countLabels[i].Text.Split('\n')[0] + "\n" + values[i]; _grid.Rows.Clear(); foreach (var r in list) { int i = _grid.Rows.Add(r.EndUtc.ToLocalTime(), JobHistoryPresentation.StatusLabel(r.Status), JobHistoryPresentation.FormatType(r.Type), JobHistoryPresentation.FileNameOrUnavailable(r.SourcePath), JobHistoryPresentation.FileNameOrUnavailable(r.OutputPath), Result(r)); var row = _grid.Rows[i]; row.Tag = r; row.Cells["colFinished"].ToolTipText = r.EndUtc.ToLocalTime().ToString("F"); row.Cells["colSource"].ToolTipText = r.SourcePath; row.Cells["colOutput"].ToolTipText = r.OutputPath; row.Cells["colResult"].ToolTipText = Result(r); row.Cells["colStatus"].Style.ForeColor = r.Status switch { JobStatus.Success => Color.FromArgb(22, 132, 93), JobStatus.Failed => Color.FromArgb(190, 65, 65), _ => Color.FromArgb(161, 98, 7) }; } ShowSelected(); UpdateActions(); }
    private void ShowSelected() { var r = Selected(); if (r == null) { _resultLabel.Text = "Select a history entry to review its outcome."; _technical.Clear(); _log.Clear(); return; } _resultLabel.Text = $"{r.Status} — {Result(r)}\r\n\r\nSource: {r.SourcePath}\r\nOutput: {JobHistoryPresentation.FileNameOrUnavailable(r.OutputPath)}\r\nFull output path: {r.OutputPath}\r\nType: {JobHistoryPresentation.FormatType(r.Type)}\r\nStarted: {r.StartUtc.ToLocalTime():F}\r\nFinished: {r.EndUtc.ToLocalTime():F}\r\nElapsed: {(r.DurationSec.HasValue ? TimeSpan.FromSeconds(r.DurationSec.Value).ToString() : "Unavailable")}"; _technical.Text = Technical(r); _log.Text = (r.Log ?? "") + (r.DiagnosticSummary == null ? "" : Environment.NewLine + Environment.NewLine + EncodingDiagnosticsService.FormatCompletedSummary(r.DiagnosticSummary)); UpdateActions(); }
    private string Technical(JobHistoryRecord r) => string.Join(Environment.NewLine, new[] { $"Encoder Mode: {Value(r.EncoderMode)}", $"Target MB: {Value(r.TargetMb)}", $"Duration Sec: {Value(r.DurationSec)}", $"Source Size: {Bytes(r.SourceSizeBytes)}", $"Final Output: {Value(r.OutputPath)}", $"Staged File: {Value(r.StagingPath)}", $"Finalization: {Value(r.FinalizationOutcome)}", $"Failure: {Value(r.ErrorSummary)}", $"Container: {Value(r.ResolvedOutputContainer ?? r.RequestedOutputContainer)}" });
    private static string Result(JobHistoryRecord r) => !string.IsNullOrWhiteSpace(r.ErrorSummary) ? r.ErrorSummary! : !string.IsNullOrWhiteSpace(r.Notes) ? r.Notes : r.Status == JobStatus.Success ? "Completed successfully" : r.Status.ToString();
    private static string Value(object? value) => value?.ToString() is { Length: > 0 } text ? text : "Unavailable";
    private static string Bytes(long? bytes) => bytes.HasValue ? $"{bytes.Value / 1048576d:0.##} MB" : "Unavailable";
    private JobHistoryRecord? Selected() => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as JobHistoryRecord : null;
    private void UpdateActions() { var r = Selected(); _requeue.Enabled = r != null && _grid.SelectedRows.Cast<DataGridViewRow>().Any(row => row.Tag is JobHistoryRecord x && File.Exists(x.SourcePath)); _openSource.Enabled = r != null && !string.IsNullOrWhiteSpace(r.SourcePath); _openOutput.Enabled = r != null && !string.IsNullOrWhiteSpace(r.OutputPath); }
    private void SetStatus(string value) { _status.SelectedItem = value; Bind(); }
    private void Copy(Func<JobHistoryRecord, string> selector) { if (Selected() is { } r) Clipboard.SetText(selector(r) ?? ""); }
    private void OpenPath(bool source) { string path = source ? Selected()?.SourcePath ?? "" : Selected()?.OutputPath ?? ""; if (string.IsNullOrWhiteSpace(path)) return; string target = File.Exists(path) ? path : Path.GetDirectoryName(path) ?? path; if (Directory.Exists(target) || File.Exists(target)) Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(target) ? $"/select,\"{target}\"" : target) { UseShellExecute = true }); }
    private void Requeue() { if (_addToQueue == null) return; int added = 0; foreach (DataGridViewRow row in _grid.SelectedRows) if (row.Tag is JobHistoryRecord r && File.Exists(r.SourcePath) && _addToQueue(r.SourcePath)) added++; if (added > 0) { _queueChanged?.Invoke(); MessageBox.Show(this, $"Added {added} file(s) to the Encode queue.", "Requeue", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
    private void DeleteSelected() { string[] ids = _grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Tag as JobHistoryRecord).Where(x => x != null).Select(x => x!.Id).ToArray(); if (ids.Length == 0 || MessageBox.Show(this, $"Delete {ids.Length} selected entr{(ids.Length == 1 ? "y" : "ies")} ?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _history.DeleteByIds(ids); LoadRecords(); }
    private void ClearAll() { if (MessageBox.Show(this, "Clear ALL history?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _history.Clear(); LoadRecords(); }
    private void HandleKeyDown(object? sender, KeyEventArgs e) { if (e.KeyCode == Keys.F5) { LoadRecords(); e.Handled = true; } else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); e.Handled = true; } else if (e.KeyCode == Keys.Enter && Selected() != null) { _detailsTabs.Focus(); e.Handled = true; } }
    private Size RestoreSize() => JobHistoryPresentation.RestoreWindowBounds(_config.JobHistoryWindowX, _config.JobHistoryWindowY, _config.JobHistoryWindowWidth, _config.JobHistoryWindowHeight, MinimumSize, new Size(1280, 820), Screen.AllScreens.Select(screen => screen.WorkingArea)).Size;
    private void RestoreState() { _restoring = true; Bounds = JobHistoryPresentation.RestoreWindowBounds(_config.JobHistoryWindowX, _config.JobHistoryWindowY, _config.JobHistoryWindowWidth, _config.JobHistoryWindowHeight, MinimumSize, new Size(1280, 820), Screen.AllScreens.Select(screen => screen.WorkingArea)); _detailsTabs.SelectedIndex = Math.Clamp(_config.JobHistoryDetailsTab, 0, _detailsTabs.TabPages.Count - 1); _restoring = false; }
    private void ClampSplitter() { int available = _split.ClientSize.Height - _split.SplitterWidth; if (available > 0) _split.SplitterDistance = Math.Clamp(_config.JobHistoryDetailsSplitterDistance > 0 ? _config.JobHistoryDetailsSplitterDistance : (int)(available * .68), _split.Panel1MinSize, Math.Max(_split.Panel1MinSize, available - _split.Panel2MinSize)); }
    private void SaveState() { if (WindowState != FormWindowState.Normal) return; _config.JobHistoryWindowWidth = Width; _config.JobHistoryWindowHeight = Height; _config.JobHistoryWindowX = Location.X; _config.JobHistoryWindowY = Location.Y; _config.JobHistoryDetailsSplitterDistance = _split.SplitterDistance; _config.JobHistoryDetailsTab = _detailsTabs.SelectedIndex; foreach (DataGridViewColumn c in _grid.Columns) _config.JobHistoryGridColumnWidths[c.Name] = c.Width; _config.JobHistoryGridColumnOrder = _grid.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToList(); try { _config.Save(_configPath); } catch { } }
    private void ApplyGridState() { foreach (DataGridViewColumn c in _grid.Columns) if (_config.JobHistoryGridColumnWidths.TryGetValue(c.Name, out int width)) c.Width = Math.Clamp(width, 70, 900); var order = _config.JobHistoryGridColumnOrder ?? new List<string>(); for (int i = 0; i < order.Count; i++) if (_grid.Columns.Contains(order[i])) _grid.Columns[order[i]].DisplayIndex = i; }
}
