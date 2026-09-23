using MediaFlux.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MediaFlux
{
    public partial class MainForm
    {
        private TableLayoutPanel? _queueWorkspaceHost;
        private Label? _queueWorkspaceTotalValue;
        private Label? _queueWorkspaceReadyValue;
        private Label? _queueWorkspaceRunningValue;
        private Label? _queueWorkspaceAttentionValue;
        private TextBox? _queueWorkspaceSearchBox;
        private ComboBox? _queueWorkspaceViewSelector;
        private Label? _queueWorkspaceShowingValue;
        private Label? _queueWorkspaceEstimateOutputValue;
        private Label? _queueWorkspaceEstimateSavingsValue;
        private Label? _queueWorkspaceEstimateEtaValue;
        private Button? _btnStartSelectedQueue;
        private Button? _btnRemoveSelectedQueue;
        private bool _queueWorkspaceRefreshPosted;
        private bool _applyingQueueWorkspaceView;

        private static readonly string[] QueueWorkspaceViews =
        [
            "All", "Ready", "Running", "Attention", "Encode", "Skip", "Review"
        ];

        internal readonly record struct QueueWorkspaceCounts(
            int Total,
            int Ready,
            int Running,
            int Attention);

        internal static QueueWorkspaceCounts CountQueueWorkspaceItems(
            IEnumerable<(string Status, bool IsRunning, bool RequiresAttention)> items)
        {
            int total = 0;
            int ready = 0;
            int running = 0;
            int attention = 0;

            foreach ((string status, bool isRunning, bool requiresAttention) in items)
            {
                total++;
                if (isRunning)
                {
                    running++;
                    continue;
                }

                if (requiresAttention || IsQueueWorkspaceAttentionStatus(status))
                {
                    attention++;
                    continue;
                }

                if (IsQueueWorkspaceReadyStatus(status))
                    ready++;
            }

            return new QueueWorkspaceCounts(total, ready, running, attention);
        }

        private static bool IsQueueWorkspaceReadyStatus(string? status) =>
            status?.Trim() is { } value &&
            (value.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("Retry Queued", StringComparison.OrdinalIgnoreCase));

        private static bool IsQueueWorkspaceAttentionStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return false;

            string value = status.Trim();
            return value.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Validation Failed", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Finalization Failed", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Canceled", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Review", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Warning", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Excluded - exact duplicate", StringComparison.OrdinalIgnoreCase);
        }

        private void InitializeQueueWorkspace()
        {
            if (_encodeQueueSplit == null || pnlQueueActionButtons == null)
                return;

            AddQueueWorkspaceColumns();
            CreateQueueWorkspaceSurface();
            AddQueueWorkspaceActions();

            dgvEncodeQueue.AccessibleName = "Encoding queue";
            dgvEncodeQueue.AccessibleDescription =
                "All queued items. View presentation does not change full-queue execution scope.";
            dgvEncodeQueue.CellFormatting += QueueWorkspace_CellFormatting;
            dgvEncodeQueue.CellValueChanged += QueueWorkspace_CellValueChanged;
            dgvEncodeQueue.RowsAdded += (_, __) => ScheduleQueueWorkspaceRefresh();
            dgvEncodeQueue.RowsRemoved += (_, __) => ScheduleQueueWorkspaceRefresh();
            dgvEncodeQueue.SelectionChanged += (_, __) => UpdateQueueWorkspaceActionState();
            dgvEncodeQueue.SizeChanged += (_, __) => UpdateQueueWorkspaceResponsiveLayout();

            if (_summaryNewSizeValue != null)
                _summaryNewSizeValue.TextChanged += (_, __) => UpdateQueueWorkspaceAggregateEstimateStrip();
            if (_summaryTotalEstimatedSavedValue != null)
                _summaryTotalEstimatedSavedValue.TextChanged += (_, __) => UpdateQueueWorkspaceAggregateEstimateStrip();
            if (_summaryEstimatedCompletionValue != null)
                _summaryEstimatedCompletionValue.TextChanged += (_, __) => UpdateQueueWorkspaceAggregateEta();

            if (comboVideoFormat != null)
                comboVideoFormat.SelectedIndexChanged += (_, __) => RefreshQueueWorkspaceConfiguredOutput();
            if (comboEncoderMode != null)
                comboEncoderMode.SelectedIndexChanged += (_, __) => RefreshQueueWorkspaceConfiguredOutput();
            if (comboResolution != null)
                comboResolution.SelectedIndexChanged += (_, __) => RefreshQueueWorkspaceConfiguredOutput();
            if (comboOutputContainer != null)
                comboOutputContainer.SelectedIndexChanged += (_, __) => RefreshQueueWorkspaceConfiguredOutput();

            UpdateQueueWorkspaceResponsiveLayout();
            RefreshQueueWorkspacePresentation();
        }

        private void AddQueueWorkspaceColumns()
        {
            if (!dgvEncodeQueue.Columns.Contains("colOrder"))
            {
                dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = "colOrder",
                    HeaderText = "Order",
                    Width = ScaleUi(56),
                    MinimumWidth = ScaleUi(46),
                    SortMode = DataGridViewColumnSortMode.Automatic,
                    Resizable = DataGridViewTriState.True,
                    DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
                    ToolTipText = "Logical execution position. Column sorting changes presentation only; it does not change queue priority."
                });
            }

            if (!dgvEncodeQueue.Columns.Contains("colPlannedOutput"))
            {
                dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = "colPlannedOutput",
                    HeaderText = "Planned Output",
                    Width = ScaleUi(190),
                    MinimumWidth = ScaleUi(145),
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    Resizable = DataGridViewTriState.True,
                    ToolTipText = "Shows the frozen plan after existing encode preflight; before then it is a configured preview."
                });
            }

            if (!dgvEncodeQueue.Columns.Contains("colSourceEstimate"))
            {
                dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = "colSourceEstimate",
                    HeaderText = "Source Size → Estimate",
                    Width = ScaleUi(205),
                    MinimumWidth = ScaleUi(165),
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    Resizable = DataGridViewTriState.True,
                    ToolTipText = "A display projection of the existing source-size and output-estimate values."
                });
            }
        }

        private void CreateQueueWorkspaceSurface()
        {
            Control panel = _encodeQueueSplit!.Panel1;
            panel.Controls.Remove(dgvEncodeQueue);

            var surface = new TableLayoutPanel
            {
                Name = "queueWorkspaceSurface",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                ColumnCount = 1,
                RowCount = 3,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            surface.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            surface.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            surface.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            surface.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            surface.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            surface.RowCount = 4;

            Control summary = CreateQueueWorkspaceSummaryCards();
            Control viewToolbar = CreateQueueWorkspaceViewToolbar();
            Control estimates = CreateQueueWorkspaceEstimateStrip();
            dgvEncodeQueue.Dock = DockStyle.Fill;
            dgvEncodeQueue.MinimumSize = new Size(0, ScaleUi(92));
            dgvEncodeQueue.Margin = Padding.Empty;
            surface.Controls.Add(summary, 0, 0);
            surface.Controls.Add(viewToolbar, 0, 1);
            surface.Controls.Add(estimates, 0, 2);
            surface.Controls.Add(dgvEncodeQueue, 0, 3);
            panel.Controls.Add(surface);
            _queueWorkspaceHost = surface;

            // The former estimate summary remains available in the Details tab, but
            // the operational strip above the queue is now its single top-level view.
            if (_queueCommandSummaryLabel != null)
                _queueCommandSummaryLabel.Visible = false;

            surface.SizeChanged += (_, __) => UpdateQueueWorkspaceResponsiveLayout();
        }

        private Control CreateQueueWorkspaceViewToolbar()
        {
            var toolbar = new TableLayoutPanel
            {
                Name = "queueWorkspaceViewToolbar",
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, ScaleUi(3)),
                Padding = new Padding(ScaleUi(4), ScaleUi(2), ScaleUi(4), ScaleUi(2)),
                AccessibleName = "Queue presentation filters"
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _queueWorkspaceSearchBox = new TextBox
            {
                Name = "txtQueueWorkspaceSearch",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, ScaleUi(8), 0),
                PlaceholderText = "Search queue...",
                AccessibleName = "Search queue",
                AccessibleDescription = "Searches queue filenames and full paths without changing queue execution."
            };
            _queueWorkspaceSearchBox.TextChanged += (_, __) =>
            {
                ApplyEncodeQueueViewFilter();
                RefreshQueueWorkspacePresentation();
            };

            var viewLabel = new Label
            {
                Text = "View:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 3, ScaleUi(4), 0),
                AccessibleName = "Queue view label"
            };
            _queueWorkspaceViewSelector = new ComboBox
            {
                Name = "cmbQueueWorkspaceView",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = ScaleUi(112),
                Margin = new Padding(0, 0, ScaleUi(10), 0),
                AccessibleName = "Queue view",
                AccessibleDescription = "Filters the queue presentation by operational state or recommendation."
            };
            _queueWorkspaceViewSelector.Items.AddRange(QueueWorkspaceViews);
            string initialView = NormalizeQueueWorkspaceView(_config.QueueWorkspaceView);
            _config.QueueWorkspaceView = initialView;
            _queueWorkspaceViewSelector.SelectedItem = initialView;
            _queueWorkspaceViewSelector.SelectedIndexChanged += (_, __) =>
            {
                if (_applyingQueueWorkspaceView || _queueWorkspaceViewSelector.SelectedItem == null)
                    return;

                _config.QueueWorkspaceView = NormalizeQueueWorkspaceView(
                    _queueWorkspaceViewSelector.SelectedItem.ToString());
                _config.Save(_configPath);
                ApplyEncodeQueueViewFilter();
                RefreshQueueWorkspacePresentation();
            };

            _queueWorkspaceShowingValue = new Label
            {
                Text = "Showing 0 of 0",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = Padding.Empty,
                AccessibleName = "Queue rows shown"
            };

            toolbar.Controls.Add(_queueWorkspaceSearchBox, 0, 0);
            toolbar.Controls.Add(viewLabel, 1, 0);
            toolbar.Controls.Add(_queueWorkspaceViewSelector, 2, 0);
            toolbar.Controls.Add(_queueWorkspaceShowingValue, 3, 0);
            return toolbar;
        }

        private Control CreateQueueWorkspaceSummaryCards()
        {
            var cards = new TableLayoutPanel
            {
                Name = "queueWorkspaceSummaryCards",
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, ScaleUi(3)),
                Padding = Padding.Empty,
                AccessibleName = "Queue summary"
            };

            string[] captions = ["TOTAL", "READY", "RUNNING", "ATTENTION"];
            Label?[] values = new Label?[4];
            for (int index = 0; index < captions.Length; index++)
            {
                cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
                var card = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 1,
                    Margin = new Padding(index == 0 ? 0 : ScaleUi(3), 0, index == captions.Length - 1 ? 0 : ScaleUi(3), 0),
                    Padding = new Padding(ScaleUi(8), ScaleUi(4), ScaleUi(8), ScaleUi(4)),
                    BackColor = index == 3 ? Color.FromArgb(255, 248, 235) : Color.FromArgb(248, 249, 251),
                    AccessibleRole = AccessibleRole.Grouping,
                    AccessibleName = captions[index]
                };
                card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                var caption = new Label
                {
                    Text = captions[index],
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                    ForeColor = SystemColors.GrayText,
                    Margin = Padding.Empty
                };
                var value = new Label
                {
                    Text = "0",
                    AutoSize = true,
                    Anchor = AnchorStyles.Right,
                    Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                    ForeColor = index == 3 ? Color.FromArgb(146, 64, 14) : SystemColors.ControlText,
                    Margin = Padding.Empty,
                    AccessibleName = captions[index] + " queue items"
                };
                values[index] = value;
                card.Controls.Add(caption, 0, 0);
                card.Controls.Add(value, 1, 0);
                cards.Controls.Add(card, index, 0);
            }

            _queueWorkspaceTotalValue = values[0];
            _queueWorkspaceReadyValue = values[1];
            _queueWorkspaceRunningValue = values[2];
            _queueWorkspaceAttentionValue = values[3];
            return cards;
        }

        private Control CreateQueueWorkspaceEstimateStrip()
        {
            var strip = new TableLayoutPanel
            {
                Name = "queueWorkspaceEstimateStrip",
                Dock = DockStyle.Top,
                ColumnCount = 3,
                RowCount = 1,
                Height = ScaleUi(30),
                Margin = new Padding(0, 0, 0, ScaleUi(4)),
                Padding = new Padding(ScaleUi(8), 0, ScaleUi(8), 0),
                BackColor = Color.FromArgb(241, 246, 252),
                AccessibleName = "Aggregate queue estimates"
            };

            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
            _queueWorkspaceEstimateOutputValue = AddQueueWorkspaceEstimateLabel(strip, "Est. output: --", 0);
            _queueWorkspaceEstimateSavingsValue = AddQueueWorkspaceEstimateLabel(strip, "Est. savings: --", 1);
            _queueWorkspaceEstimateEtaValue = AddQueueWorkspaceEstimateLabel(strip, "Queue ETA: --", 2);
            return strip;
        }

        private static Label AddQueueWorkspaceEstimateLabel(TableLayoutPanel strip, string text, int column)
        {
            var label = new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                ForeColor = SystemColors.ControlText,
                Margin = new Padding(column == 0 ? 0 : 8, 0, 4, 0),
                AccessibleName = text.Split(':')[0]
            };
            strip.Controls.Add(label, column, 0);
            return label;
        }

        private void AddQueueWorkspaceActions()
        {
            btnStartEncode.Text = "Start Queue";
            btnStartEncode.Click -= btnStartEncode_Click;
            btnStartEncode.Click += StartFullEncodeQueueFromContextMenu_Click;
            btnStartEncode.AccessibleName = "Start full queue";
            _uiToolTip.SetToolTip(
                btnStartEncode,
                "Starts every eligible item in logical queue order. View visibility does not change execution scope.");

            _btnStartSelectedQueue = new Button
            {
                Name = "btnStartSelectedQueue",
                Text = "Start Selected",
                AutoSize = true,
                Margin = new Padding(0, 0, 8, 0),
                AccessibleName = "Start selected queue items",
                AccessibleDescription = "Starts only selected eligible queue items."
            };
            _btnStartSelectedQueue.Click += StartSelectedEncodeFilesFromContextMenu_Click;
            _uiToolTip.SetToolTip(_btnStartSelectedQueue, "Starts only the selected eligible items.");

            _btnRemoveSelectedQueue = new Button
            {
                Name = "btnRemoveSelectedQueue",
                Text = "Remove Selected",
                AutoSize = true,
                Margin = new Padding(0, 0, 8, 0),
                AccessibleName = "Remove selected queue items"
            };
            _btnRemoveSelectedQueue.Click += RemoveSelectedRows_Click;

            pnlQueueActionButtons.Controls.Add(_btnStartSelectedQueue);
            pnlQueueActionButtons.Controls.SetChildIndex(_btnStartSelectedQueue, 1);
            pnlQueueActionButtons.Controls.Add(_btnRemoveSelectedQueue);
            pnlQueueActionButtons.Controls.SetChildIndex(_btnRemoveSelectedQueue, 2);

            btnPauseQueue.AccessibleName = "Pause dispatch of new queue jobs";
            btnPauseQueue.AccessibleDescription =
                "Pause prevents new jobs from being dispatched. FFmpeg processes already running continue.";
            _uiToolTip.SetToolTip(
                btnPauseQueue,
                "Pauses dispatch of new jobs only. Already-running FFmpeg processes continue until they finish or are stopped.");
            btnStopEncode.AccessibleDescription = "Cancels active queue work.";
            _uiToolTip.SetToolTip(btnStopEncode, "Stops the queue and cancels active encode work.");
            btnRefreshEncode.AccessibleDescription = "Rescans the current input folder and updates the queue.";
            if (_analyzeQueueButton != null)
                _analyzeQueueButton.ToolTipText = "Run queue analysis and refresh existing estimates and recommendations.";

            btnPauseQueue.Click += (_, __) => UpdateQueueWorkspaceActionState();
            UpdateQueueWorkspaceActionState();
        }

        private void ApplyQueueWorkspaceColumnPreferences()
        {
            bool migrateLegacyLayout = !_config.QueueWorkspaceLayoutInitialized;
            if (migrateLegacyLayout)
            {
                // These columns remain available through Column Settings, but the
                // combined source/estimate column replaces their former defaults.
                _config.ShowSizeColumn = false;
                _config.ShowEstimatedOutputColumn = false;
                _config.ShowCustomColumn = false;
                _config.ShowDuplicateColumn = false;
                _config.ShowDuplicateConfidenceColumn = false;
                _config.ShowDuplicateActionColumn = false;

                var rememberedOrder = (_config.EncodeGridColumnOrder ?? new List<string>())
                    .Where(name => dgvEncodeQueue.Columns.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (rememberedOrder.Count == 0)
                {
                    rememberedOrder = new List<string>
                    {
                        "colOrder", "colName", "colStatus", "colEncodeRecommendation", "colPlannedOutput",
                        "colSourceEstimate", "colProgress", "colETA", "colSize", "colEstimatedSize",
                        "colCreated", "colCustom", "colDuplicate", "colDuplicateConfidence", "colDuplicateAction"
                    };
                }
                else
                {
                    InsertRememberedColumnAfter(rememberedOrder, "colPlannedOutput", "colEncodeRecommendation");
                    InsertRememberedColumnAfter(rememberedOrder, "colSourceEstimate", "colEstimatedSize");
                    foreach (DataGridViewColumn column in dgvEncodeQueue.Columns)
                    {
                        if (!rememberedOrder.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
                            rememberedOrder.Add(column.Name);
                    }
                }

                _config.EncodeGridColumnOrder = rememberedOrder;
                if (!(_config.EncodeGridColumnWidths?.ContainsKey("colSourceEstimate") ?? false))
                {
                    int previousEstimateWidth = _config.EncodeGridColumnWidths != null &&
                                                _config.EncodeGridColumnWidths.TryGetValue("colEstimatedSize", out int savedEstimateWidth)
                        ? savedEstimateWidth
                        : ScaleUi(205);
                    _config.EncodeGridColumnWidths ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _config.EncodeGridColumnWidths["colSourceEstimate"] = Math.Clamp(previousEstimateWidth, ScaleUi(165), ScaleUi(360));
                }

                _config.QueueWorkspaceLayoutInitialized = true;
                _config.Save(_configPath);
            }

            dgvEncodeQueue.Columns["colName"].Visible = true;
            dgvEncodeQueue.Columns["colName"].HeaderText = "File";
            dgvEncodeQueue.Columns["colOrder"].Visible = _config.ShowExecutionOrderColumn;
            dgvEncodeQueue.Columns["colStatus"].Visible = true;
            dgvEncodeQueue.Columns["colEncodeRecommendation"].Visible = _config.ShowRecommendationColumn;
            dgvEncodeQueue.Columns["colEncodeRecommendation"].HeaderText = "Recommendation";
            dgvEncodeQueue.Columns["colPlannedOutput"].Visible = true;
            dgvEncodeQueue.Columns["colSourceEstimate"].Visible = true;
            dgvEncodeQueue.Columns["colProgress"].Visible = true;
            dgvEncodeQueue.Columns["colETA"].Visible = true;
            dgvEncodeQueue.Columns["colSize"].Visible = _config.ShowSizeColumn;
            dgvEncodeQueue.Columns["colEstimatedSize"].Visible = _config.ShowEstimatedOutputColumn;
            dgvEncodeQueue.Columns["colCreated"].Visible = _config.ShowCreatedColumn;
            dgvEncodeQueue.Columns["colCustom"].Visible = _config.ShowCustomColumn;
            dgvEncodeQueue.Columns["colDuplicate"].Visible = _config.ShowDuplicateColumn;
            dgvEncodeQueue.Columns["colDuplicateConfidence"].Visible = _config.ShowDuplicateConfidenceColumn;
            dgvEncodeQueue.Columns["colDuplicateAction"].Visible = _config.ShowDuplicateActionColumn;
        }

        private void InsertRememberedColumnAfter(List<string> names, string column, string after)
        {
            if (!dgvEncodeQueue.Columns.Contains(column) || names.Contains(column, StringComparer.OrdinalIgnoreCase))
                return;

            int index = names.FindIndex(name => string.Equals(name, after, StringComparison.OrdinalIgnoreCase));
            names.Insert(index >= 0 ? index + 1 : names.Count, column);
        }

        private void QueueWorkspace_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= dgvEncodeQueue.Rows.Count || e.ColumnIndex < 0)
                return;

            DataGridViewRow row = dgvEncodeQueue.Rows[e.RowIndex];
            string columnName = dgvEncodeQueue.Columns[e.ColumnIndex].Name;
            if (columnName is "colSize" or "colEstimatedSize")
                RefreshQueueWorkspaceRow(row);

            if (columnName is "colStatus" or "colEncodeRecommendation")
                ScheduleQueueWorkspaceRefresh();
            if (columnName == "colEncodeRecommendation")
                RefreshQueueWorkspaceRow(row);
        }

        private void QueueWorkspace_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= dgvEncodeQueue.Rows.Count || e.ColumnIndex < 0)
                return;

            DataGridViewColumn column = dgvEncodeQueue.Columns[e.ColumnIndex];
            DataGridViewRow row = dgvEncodeQueue.Rows[e.RowIndex];
            if (column.Name == "colStatus")
            {
                string raw = Convert.ToString(e.Value) ?? string.Empty;
                string display = CompactQueueWorkspaceStatus(raw);
                if (!string.Equals(raw, display, StringComparison.Ordinal))
                {
                    e.Value = display;
                    e.FormattingApplied = true;
                    DataGridViewCell cell = row.Cells[e.ColumnIndex];
                    string tooltip = cell.ToolTipText;
                    if (!tooltip.Contains(raw, StringComparison.Ordinal))
                    {
                        cell.ToolTipText = string.IsNullOrWhiteSpace(tooltip)
                            ? $"Current queue stage: {raw}"
                            : $"Current queue stage: {raw}{Environment.NewLine}{tooltip}";
                    }
                }
            }
            else if (column.Name == "colOrder")
            {
                e.Value = FormatQueueExecutionOrderCellValue(e.Value);
                e.FormattingApplied = true;
            }
            else if (column.Name == "colEncodeRecommendation")
            {
                string display = GetQueueWorkspaceRecommendationText(row);
                if (!string.Equals(Convert.ToString(e.Value), display, StringComparison.Ordinal))
                {
                    e.Value = display;
                    e.FormattingApplied = true;
                }
            }
        }

        private static string CompactQueueWorkspaceStatus(string status)
        {
            string value = status.Trim();
            if (value.Length == 0)
                return "Queued";
            if (value.StartsWith("Excluded - exact duplicate", StringComparison.OrdinalIgnoreCase))
                return "Duplicate excluded";
            if (value.StartsWith("Retry Queued", StringComparison.OrdinalIgnoreCase))
                return "Retry queued";
            if (value.StartsWith("Checking codec", StringComparison.OrdinalIgnoreCase))
                return "Scanning";
            if (value.StartsWith("Estimating", StringComparison.OrdinalIgnoreCase))
                return "Analyzing";
            if (value.StartsWith("Reading metadata", StringComparison.OrdinalIgnoreCase))
                return "Preparing";
            if (value.StartsWith("Preparing DVD", StringComparison.OrdinalIgnoreCase))
                return "Preparing";
            if (value.Contains("failed", StringComparison.OrdinalIgnoreCase))
                return "Failed";
            if (value.StartsWith("Canceled", StringComparison.OrdinalIgnoreCase))
                return "Canceled";
            if (value.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("recovered source", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("source corruption", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("salvage", StringComparison.OrdinalIgnoreCase))
                return "Recovering";
            if (value.Contains("finaliz", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("validating", StringComparison.OrdinalIgnoreCase))
                return "Finalizing";
            if (value.Equals("Queued", StringComparison.OrdinalIgnoreCase))
                return "Queued";
            if (value.Equals("Encoding", StringComparison.OrdinalIgnoreCase))
                return "Encoding";
            return value.Length <= 20 ? value : value[..19] + "…";
        }

        private string GetQueueWorkspaceRecommendationText(DataGridViewRow row)
        {
            RowMeta? meta = row.Tag as RowMeta;
            EncodingRecommendation? planRecommendation = meta?.IntelligencePlan?.Recommendation;
            if (planRecommendation != null)
                return planRecommendation.DisplayName;

            return meta?.EncodeRecommendation?.Kind switch
            {
                SmartEncodeRecommendationKind.StrongCandidate or SmartEncodeRecommendationKind.ModerateCandidate => "Encode",
                SmartEncodeRecommendationKind.Skip => "Skip",
                SmartEncodeRecommendationKind.Review => "Review",
                SmartEncodeRecommendationKind.RemuxOnly => "Remux only",
                SmartEncodeRecommendationKind.Unavailable => "Unavailable",
                _ => string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["colEncodeRecommendation"].Value))
                    ? "—"
                    : Convert.ToString(row.Cells["colEncodeRecommendation"].Value)!
            };
        }

        private enum QueueWorkspaceRecommendationFilter
        {
            None,
            Encode,
            Skip,
            Review
        }

        private static string NormalizeQueueWorkspaceView(string? value) =>
            QueueWorkspaceViews.FirstOrDefault(view =>
                string.Equals(view, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "All";

        private QueueWorkspaceRecommendationFilter GetQueueWorkspaceRecommendationFilter(RowMeta? meta)
        {
            if (meta == null || meta.IsDvdEncode)
                return QueueWorkspaceRecommendationFilter.None;

            EncodingRecommendation? planRecommendation = meta.IntelligencePlan?.Recommendation;
            if (planRecommendation != null)
            {
                return planRecommendation.Recommendation switch
                {
                    EncodingRecommendationKind.Encode => QueueWorkspaceRecommendationFilter.Encode,
                    EncodingRecommendationKind.Skip => QueueWorkspaceRecommendationFilter.Skip,
                    EncodingRecommendationKind.Review => QueueWorkspaceRecommendationFilter.Review,
                    _ => QueueWorkspaceRecommendationFilter.None
                };
            }

            SmartEncodeRecommendation? recommendation = meta.EncodeRecommendation;
            if (recommendation?.IsCandidate == true)
                return QueueWorkspaceRecommendationFilter.Encode;

            return recommendation?.Kind switch
            {
                SmartEncodeRecommendationKind.Skip => QueueWorkspaceRecommendationFilter.Skip,
                SmartEncodeRecommendationKind.Review or
                SmartEncodeRecommendationKind.RemuxOnly or
                SmartEncodeRecommendationKind.Unavailable => QueueWorkspaceRecommendationFilter.Review,
                _ => QueueWorkspaceRecommendationFilter.None
            };
        }

        private bool ShouldShowEncodeQueueRow(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow || row.DataGridView != dgvEncodeQueue)
                return false;

            RowMeta? meta = row.Tag as RowMeta;
            bool onlyDuplicates = chkOnlyDuplicateCandidates?.Checked == true && _lastDuplicateScanResult != null;
            if (onlyDuplicates && meta?.DuplicateGroupId == null)
                return false;

            string search = _queueWorkspaceSearchBox?.Text.Trim() ?? string.Empty;
            if (search.Length > 0)
            {
                string path = GetFullPathFromRow(row) ?? string.Empty;
                string fileName = Path.GetFileName(path);
                string displayedName = Convert.ToString(row.Cells["colName"].Value) ?? string.Empty;
                if (!path.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !fileName.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !displayedName.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string status = Convert.ToString(row.Cells["colStatus"].Value) ?? string.Empty;
            bool running = IsQueueRowActivelyEncoding(row);
            bool attention = QueueWorkspaceRecommendationNeedsAttention(meta) ||
                             IsQueueWorkspaceAttentionStatus(status);
            string view = NormalizeQueueWorkspaceView(_queueWorkspaceViewSelector?.SelectedItem?.ToString()
                                                      ?? _config.QueueWorkspaceView);
            return view switch
            {
                "Ready" => !running && !attention && IsQueueWorkspaceReadyStatus(status),
                "Running" => running,
                "Attention" => attention,
                "Encode" => GetQueueWorkspaceRecommendationFilter(meta) == QueueWorkspaceRecommendationFilter.Encode,
                "Skip" => GetQueueWorkspaceRecommendationFilter(meta) == QueueWorkspaceRecommendationFilter.Skip,
                "Review" => GetQueueWorkspaceRecommendationFilter(meta) == QueueWorkspaceRecommendationFilter.Review,
                _ => true
            };
        }

        private void ApplyEncodeQueueViewFilter()
        {
            if (_applyingQueueWorkspaceView || dgvEncodeQueue == null || IsQueueEncodingActive())
            {
                UpdateQueueWorkspaceShowingCount();
                return;
            }

            _applyingQueueWorkspaceView = true;
            bool selectionChanged = false;
            try
            {
                DataGridViewRow[] rows = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                    .Where(row => !row.IsNewRow)
                    .ToArray();
                HashSet<DataGridViewRow> visibleRows = rows
                    .Where(ShouldShowEncodeQueueRow)
                    .ToHashSet();

                DataGridViewRow? currentRow = dgvEncodeQueue.CurrentRow;
                bool currentRowHidden = currentRow != null &&
                                        !currentRow.IsNewRow &&
                                        !visibleRows.Contains(currentRow);
                bool hiddenRowSelected = rows.Any(row =>
                    row.Selected && !visibleRows.Contains(row));
                if (currentRowHidden)
                {
                    dgvEncodeQueue.CurrentCell = null;
                    selectionChanged = true;
                }

                dgvEncodeQueue.SuspendLayout();
                try
                {
                    foreach (DataGridViewRow row in rows)
                    {
                        bool shouldBeVisible = visibleRows.Contains(row);
                        if (row.Visible != shouldBeVisible)
                            row.Visible = shouldBeVisible;

                        if (!shouldBeVisible && row.Selected)
                        {
                            row.Selected = false;
                            selectionChanged = true;
                        }
                    }
                }
                finally
                {
                    dgvEncodeQueue.ResumeLayout(false);
                }

                if ((currentRowHidden || hiddenRowSelected) && visibleRows.Count > 0 &&
                    !rows.Any(row => row.Selected && visibleRows.Contains(row)))
                {
                    DataGridViewRow replacement = rows.First(row => visibleRows.Contains(row));
                    replacement.Selected = true;
                    dgvEncodeQueue.CurrentCell = replacement.Cells["colName"];
                    selectionChanged = true;
                }

                if (selectionChanged)
                    UpdateContextualDetails();
                dgvEncodeQueue.Invalidate();
            }
            catch (InvalidOperationException)
            {
                // Presentation visibility is best effort and must never invalidate queue state.
            }
            finally
            {
                _applyingQueueWorkspaceView = false;
            }

            UpdateQueueWorkspaceShowingCount();
        }

        private void UpdateQueueWorkspaceShowingCount()
        {
            if (_queueWorkspaceShowingValue == null || dgvEncodeQueue == null)
                return;

            int total = dgvEncodeQueue.Rows.Cast<DataGridViewRow>().Count(row => !row.IsNewRow);
            int visible = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                .Count(row => !row.IsNewRow && row.Visible);
            SetQueueWorkspaceLabel(_queueWorkspaceShowingValue, $"Showing {visible:N0} of {total:N0}");
        }

        private void ScheduleQueueWorkspaceRefresh()
        {
            if (_queueWorkspaceHost == null || IsDisposed || !IsHandleCreated)
                return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(ScheduleQueueWorkspaceRefresh)); }
                catch (InvalidOperationException) { }
                return;
            }

            if (_queueWorkspaceRefreshPosted)
                return;

            _queueWorkspaceRefreshPosted = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _queueWorkspaceRefreshPosted = false;
                    if (!IsDisposed)
                    {
                        RefreshQueueExecutionOrderPresentation();
                        ApplyEncodeQueueViewFilter();
                        RefreshQueueWorkspacePresentation();
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                _queueWorkspaceRefreshPosted = false;
            }
        }

        private void RefreshQueueWorkspacePresentation()
        {
            if (_queueWorkspaceHost == null || dgvEncodeQueue == null)
                return;

            UpdateQueueWorkspaceShowingCount();

            var items = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .Select(row =>
                {
                    string status = Convert.ToString(row.Cells["colStatus"].Value) ?? string.Empty;
                    bool running = _runningEncodeJobs.ContainsKey(row);
                    return (status, running, QueueWorkspaceRecommendationNeedsAttention(row.Tag as RowMeta));
                });

            QueueWorkspaceCounts counts = CountQueueWorkspaceItems(items);
            SetQueueWorkspaceLabel(_queueWorkspaceTotalValue, counts.Total.ToString("N0"));
            SetQueueWorkspaceLabel(_queueWorkspaceReadyValue, counts.Ready.ToString("N0"));
            SetQueueWorkspaceLabel(_queueWorkspaceRunningValue, counts.Running.ToString("N0"));
            SetQueueWorkspaceLabel(_queueWorkspaceAttentionValue, counts.Attention.ToString("N0"));
            UpdateQueueWorkspaceActionState();
            UpdateQueueWorkspaceAggregateEstimateStrip();
            UpdateQueueWorkspaceAggregateEta();
        }

        private bool QueueWorkspaceRecommendationNeedsAttention(RowMeta? meta)
        {
            if (meta == null || meta.IsDvdEncode)
                return false;

            EncodingRecommendation? planRecommendation = meta.IntelligencePlan?.Recommendation;
            if (planRecommendation != null)
            {
                if (planRecommendation.Recommendation is EncodingRecommendationKind.Skip or EncodingRecommendationKind.Review)
                    return true;
            }
            else
            {
                SmartEncodeRecommendation? recommendation = meta.EncodeRecommendation;
                if (recommendation?.Kind is SmartEncodeRecommendationKind.Skip or
                    SmartEncodeRecommendationKind.Review or
                    SmartEncodeRecommendationKind.RemuxOnly)
                {
                    return true;
                }

                if (_config.SmartRecommendationsEnabled &&
                    _config.WarnBeforeEncodingSkippedOrReviewItems &&
                    (recommendation == null || recommendation.Kind == SmartEncodeRecommendationKind.Unavailable))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsQueueEncodingActive() =>
            _encodingActive ||
            !_runningEncodeJobs.IsEmpty ||
            _activeEncodeRows.Count > 0 ||
            _activeEncodeRow != null;

        private bool IsQueueRowActivelyEncoding(DataGridViewRow row) =>
            _runningEncodeJobs.ContainsKey(row) ||
            _activeEncodeRows.Contains(row) ||
            ReferenceEquals(_activeEncodeRow, row);

        private bool CanRemoveQueueRows(IEnumerable<DataGridViewRow> rows)
        {
            DataGridViewRow[] candidates = rows
                .Where(row => !row.IsNewRow && row.DataGridView == dgvEncodeQueue)
                .Distinct()
                .ToArray();

            // Match the existing queue-wide lockout while encoding, and also
            // trust the active-job/row trackers if the display flag lags behind.
            return candidates.Length > 0 &&
                   !IsQueueEncodingActive() &&
                   candidates.All(row => !IsQueueRowActivelyEncoding(row));
        }

        private void UpdateQueueWorkspaceActionState()
        {
            if (_queueWorkspaceHost == null)
                return;

            bool busy = _encodingActive || !_runningEncodeJobs.IsEmpty || _mediaRemuxCts != null;
            bool anyEligible = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                .Any(row => !row.IsNewRow && row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true });
            bool hasSelectedEligible = dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>()
                .Any(row => !row.IsNewRow && row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true });

            btnStartEncode.Enabled = !busy && anyEligible;
            if (_btnStartSelectedQueue != null)
                _btnStartSelectedQueue.Enabled = !busy && hasSelectedEligible;
            btnPauseQueue.Enabled = _encodingActive;
            btnStopEncode.Enabled = _encodingActive;
            if (_btnRemoveSelectedQueue != null)
                _btnRemoveSelectedQueue.Enabled = !busy && CanRemoveQueueRows(
                    dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>());
            if (_analyzeQueueButton != null)
                _analyzeQueueButton.Enabled = dgvEncodeQueue.Rows.Count > 0 && !busy;

            if (btnPauseQueue != null)
                btnPauseQueue.Text = _encodeQueuePaused ? "Resume Queue" : "Pause Queue";
        }

        private void RefreshQueueWorkspaceRow(DataGridViewRow row)
        {
            if (_queueWorkspaceHost == null || row == null || row.IsNewRow || row.DataGridView != dgvEncodeQueue)
                return;

            string sourceText = Convert.ToString(row.Cells["colSize"].Value) ?? string.Empty;
            string estimateText = Convert.ToString(row.Cells["colEstimatedSize"].Value) ?? string.Empty;
            double sourceMb = ParseSizeToMb(sourceText);
            double estimateMb = ParseSizeToMb(estimateText);
            string? path = GetFullPathFromRow(row);
            RowMeta? meta = row.Tag as RowMeta;
            if (sourceMb <= 0 && meta?.SrcMb > 0)
                sourceMb = meta.SrcMb;
            if (estimateMb <= 0 && !string.IsNullOrWhiteSpace(path) &&
                _estimatedSizeMap.TryGetValue(path, out double mappedEstimate) && mappedEstimate > 0 &&
                !estimateText.Contains("unavailable", StringComparison.OrdinalIgnoreCase) &&
                !estimateText.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                estimateMb = mappedEstimate;
            }

            string source = sourceMb > 0 ? FormatSize(sourceMb) : "--";
            string estimate = estimateMb > 0
                ? FormatSize(estimateMb)
                : string.IsNullOrWhiteSpace(estimateText) ? "not analyzed" : estimateText;
            SetQueueWorkspaceCellValue(row.Cells["colSourceEstimate"], $"{source} → {estimate}");

            string sizeTooltip = $"Source size: {source}{Environment.NewLine}Estimated output: {estimate}";
            string existingEstimateTooltip = row.Cells["colEstimatedSize"].ToolTipText;
            if (!string.IsNullOrWhiteSpace(existingEstimateTooltip))
                sizeTooltip += Environment.NewLine + existingEstimateTooltip;
            SetQueueWorkspaceCellTooltip(row.Cells["colSourceEstimate"], sizeTooltip);

            (string planText, string planTooltip) = GetQueueWorkspacePlannedOutput(row, meta);
            SetQueueWorkspaceCellValue(row.Cells["colPlannedOutput"], planText);
            SetQueueWorkspaceCellTooltip(row.Cells["colPlannedOutput"], planTooltip);

            if (meta?.EncodeRecommendation != null || meta?.IntelligencePlan?.Recommendation != null)
            {
                string recommendationTooltip = GetQueueAnalysisPresentation(row, meta).BuildTooltip();
                SetQueueWorkspaceCellTooltip(row.Cells["colEncodeRecommendation"], recommendationTooltip);
            }
        }

        private (string Text, string ToolTip) GetQueueWorkspacePlannedOutput(DataGridViewRow row, RowMeta? meta)
        {
            EncodingPlan? plan = meta?.IntelligencePlan;
            if (plan is { IsAvailable: true, Video: not null })
            {
                string dimensions = plan.Video.EffectiveWidth is > 0 && plan.Video.EffectiveHeight is > 0
                    ? $"{plan.Video.EffectiveWidth}×{plan.Video.EffectiveHeight}"
                    : "source size";
                string container = plan.Container?.Effective.ToString() ?? "container pending";
                string text = $"{plan.Video.Codec} · {dimensions} · {container}";
                return (text, $"Frozen plan {plan.PlanId:N}, published by the existing encode preflight. {text}");
            }

            if (meta?.IsDvdEncode == true)
            {
                return ("Configured · DVD title", "Configured DVD output preview only. The existing encode preflight publishes the authoritative plan.");
            }

            string? codecText = comboVideoFormat?.Text;
            if (string.IsNullOrWhiteSpace(codecText))
                codecText = comboEncoderMode?.Text;
            if (string.IsNullOrWhiteSpace(codecText))
                codecText = "current codec";

            string resolutionText = comboResolution == null || comboResolution.SelectedIndex <= 0
                ? "source resolution"
                : comboResolution.Text;
            string containerText = comboOutputContainer?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(containerText))
                containerText = _configuredOutputContainer.ToString();

            string scope = meta?.HasCustomSettings == true ? "Per-item configured" : "Configured";
            string display = $"{scope} · {codecText} · {resolutionText} · {containerText}";
            string detail = meta?.HasCustomSettings == true
                ? "A per-item setting or library policy may override the global profile."
                : "Uses current configured settings.";
            return (display,
                $"Configured preview only — not a frozen plan. {detail} The existing encode preflight publishes the authoritative plan; this display does not trigger plan generation.");
        }

        private void RefreshQueueWorkspaceConfiguredOutput()
        {
            if (_queueWorkspaceHost == null)
                return;

            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (!row.IsNewRow)
                    RefreshQueueWorkspaceRow(row);
            }
        }

        private void UpdateQueueWorkspaceAggregateEstimateStrip()
        {
            if (_queueWorkspaceEstimateOutputValue == null || _queueWorkspaceEstimateSavingsValue == null)
                return;

            if (_queueFileCount <= 0)
            {
                SetQueueWorkspaceLabel(_queueWorkspaceEstimateOutputValue, "Est. output: --");
                SetQueueWorkspaceLabel(_queueWorkspaceEstimateSavingsValue, "Est. savings: --");
                return;
            }

            int estimatedCount = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                .Count(row => !row.IsNewRow &&
                    _estimatedSizeMap.TryGetValue(GetPathFromRow(row) ?? string.Empty, out double value) && value > 0);
            bool complete = estimatedCount == _queueFileCount;
            SetQueueWorkspaceLabel(
                _queueWorkspaceEstimateOutputValue,
                complete ? $"Est. output: {FormatSize(_queueTotalEstimatedMb)}" : "Est. output: waiting for estimates");

            if (!complete || _queueTotalSourceMb <= 0)
            {
                SetQueueWorkspaceLabel(
                    _queueWorkspaceEstimateSavingsValue,
                    "Est. savings: available after all estimates");
                if (estimatedCount > 0)
                {
                    SetQueueWorkspaceLabel(
                        _queueWorkspaceEstimateOutputValue,
                        $"Est. output: {FormatSize(_queueTotalEstimatedMb)} partial ({estimatedCount:N0}/{_queueFileCount:N0})");
                }
                return;
            }

            double savingsMb = Math.Max(0, _queueTotalSourceMb - _queueTotalEstimatedMb);
            double savingsPercent = savingsMb / _queueTotalSourceMb * 100;
            SetQueueWorkspaceLabel(
                _queueWorkspaceEstimateSavingsValue,
                $"Est. savings: {FormatSize(savingsMb)} ({savingsPercent:0}% saved)");
        }

        private void UpdateQueueWorkspaceAggregateEta()
        {
            if (_queueWorkspaceEstimateEtaValue == null)
                return;

            string eta = _summaryEstimatedCompletionValue?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(eta))
                eta = dgvEncodeQueue.Rows.Count > 0 ? "Starts when encoding begins" : "--";
            SetQueueWorkspaceLabel(_queueWorkspaceEstimateEtaValue, $"Queue ETA: {eta}");
        }

        private static void SetQueueWorkspaceLabel(Label? label, string value)
        {
            if (label != null && !string.Equals(label.Text, value, StringComparison.Ordinal))
                label.Text = value;
        }

        private static void SetQueueWorkspaceCellValue(DataGridViewCell cell, string value)
        {
            if (!string.Equals(Convert.ToString(cell.Value), value, StringComparison.Ordinal))
                cell.Value = value;
        }

        private static void SetQueueWorkspaceCellTooltip(DataGridViewCell cell, string value)
        {
            if (!string.Equals(cell.ToolTipText, value, StringComparison.Ordinal))
                cell.ToolTipText = value;
        }

        private void UpdateQueueWorkspaceResponsiveLayout()
        {
            if (_queueWorkspaceHost == null || pnlQueueControlsCard == null)
                return;

            UpdateQueueControlsResponsiveLayout();

            int width = _queueWorkspaceHost.ClientSize.Width;
            bool compact = width < ScaleUi(980);
            foreach (Control card in _queueWorkspaceHost.Controls.Cast<Control>()
                         .SelectMany(control => control.Controls.Cast<Control>())
                         .Where(control => control.AccessibleRole == AccessibleRole.Grouping))
            {
                card.Padding = compact
                    ? new Padding(ScaleUi(5), ScaleUi(3), ScaleUi(5), ScaleUi(3))
                    : new Padding(ScaleUi(8), ScaleUi(4), ScaleUi(8), ScaleUi(4));
            }
        }
    }
}
