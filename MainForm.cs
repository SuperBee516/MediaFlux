using MediaFlux.Models;
using MediaFlux.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MediaFlux
{
    public partial class MainForm : Form
    {
        private readonly string _configPath;
        private Config _config;

        private readonly string _supportedVideoExtsPath;

        private bool _suppressRowEvents;

        private volatile bool _cancelEncode = false;
        private HistoryService _historyService;
        private EncodingPresetService _presetService;
        private ToolStripMenuItem? _applyPresetToolStripMenuItem;
        private readonly object _historyLock = new();
        private readonly Dictionary<string, double> _estimatedSizeMap = new();

        private readonly Dictionary<string, double> _etaSpeedState = new();
        private EncodingService _encodingService = null!;
        private AudioService _audioService = null!;
        private MediaInfoService _mediaInfoService = null!;
        private DuplicateDetectionService _duplicateDetectionService = null!;

        private SizeEstimateService _sizeEstimateService = null!;
        private EstimateBackgroundService _estimateService = null!;
        private CancellationTokenSource? _duplicateScanCts;
        private DuplicateScanResult? _lastDuplicateScanResult;
        private readonly EncodeQueueRunner _encodeQueueRunner;

        private DateTime? _encodeScheduledUtc = null;
        private CancellationTokenSource? _encodeScheduleCts = null;
        private CancellationTokenSource? _encodeCts = null;
        private StringBuilder? _activeJobLogSb;
        private int _encodeFailedCount;
        private int _encodeSucceededCount;
        private NumericUpDown? nudAutoQuality;
        private System.Windows.Forms.Timer? _estSmartUiTimer;

        private TableLayoutPanel? _encodeQueueLayout;
        private TableLayoutPanel? _encodeInfoHeaderContent;
        private Button? _btnToggleEncodeInfoHeader;
        private TabControl? _encodeInfoTabs;
        private TableLayoutPanel? _queueSummaryTable;
        private TableLayoutPanel? _encodePreviewTable;
        private Label? _summaryFileCountValue;
        private Label? _summaryQueueStatusValue;
        private Label? _collapsedQueueStatusLabel;
        private Label? _summarySelectedCountValue;
        private Label? _summarySelectedSavedValue;
        private Label? _summaryTotalCurrentValue;
        private Label? _summaryNewSizeValue;
        private Label? _summaryEstimatedCompletionValue;
        private Label? _summaryDuplicateGroupsValue;
        private Label? _summaryDuplicateFilesValue;
        private Label? _summaryDuplicateRecoverableValue;
        private Label? _summaryTotalEstimatedSavedValue;
        private readonly Dictionary<string, Label> _previewValueLabels = new(StringComparer.OrdinalIgnoreCase);
        private readonly ToolTip _uiToolTip = new();
        private Button? _btnToggleDuplicateFinder;
        private Control? _duplicateFinderBodyPanel;
        private Label? _duplicateFinderHeaderStatusLabel;
        private bool _applyingEncodeDropdownSettings;
        private bool _applyingCheckboxStates;
        private bool _applyingRememberedSort;
        private CompactModeForm? _compactModeForm;
        private Button? _btnCompactMode;

        private PictureBox? _encodingSpinner;
        private Label? _activityLabel;
        private ActivityIndicatorService? _activityIndicator;
        private ToolStripButton? _cancelQueueWorkButton;
        private ToolStripButton? _analyzeQueueButton;
        private ToolStripProgressBar? _queueProgressBar;
        private CancellationTokenSource? _importCts;
        private CancellationTokenSource? _codecFilterCts;
        private int _lastImportDiscoveredCount;
        private int _lastImportAddedCount;
        private int _lastEstimateQueuedCount;
        private bool _largeQueueModeActive;
        private bool _estimatesDeferredForLargeQueue;
        private double _queueTotalSourceMb;
        private double _queueTotalEstimatedMb;
        private int _queueFileCount;
        private bool _queueTotalsDirty = true;
        private DateTime _lastQueueTotalsRefreshUtc = DateTime.MinValue;
        private readonly Dictionary<string, double> _queueSourceSizeMap = new(StringComparer.OrdinalIgnoreCase);

        // Advanced video / GPU options
        private ComboBox? comboNvencPreset;
        private CheckBox? chkTenBit;
        private ComboBox? comboAudioChannels;
        private CheckBox? chkWatchFolder;
        private Label? lblWatchFolderStatus;

        // UI pump to apply results in small batches
        private System.Windows.Forms.Timer? _estUiTimer;

        // Map row lookup by path (keep this in sync when you add/remove rows)
        private readonly ConcurrentDictionary<string, DataGridViewRow> _rowsByPath = new();
        // Worker-owned running-job state. Unlike the visible grid collection, this
        // remains reliable while watched-folder scans rebuild or reparent UI rows.
        private readonly ConcurrentDictionary<DataGridViewRow, string> _runningEncodeJobs = new();

        // Successful source/output paths stay out of automatic folder merges until
        // the user explicitly refreshes or selects an input folder again.
        private readonly HashSet<string> _completedEncodePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _completedEncodePathsLock = new();

        private static readonly Regex _ffmpegTimeRegex =
            new Regex(@"time=(\d+:\d+:\d+\.\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);



        // ───────── Encode grid reparenting ─────────
        private Control? _encodeGridOriginalParent;
        private int _encodeGridOriginalIndex;

        private void MoveEncodeGridTo(Control newParent)
        {
            if (dgvEncodeQueue.Parent == newParent) return;

            if (_encodeGridOriginalParent == null)
            {
                _encodeGridOriginalParent = dgvEncodeQueue.Parent;
                _encodeGridOriginalIndex = _encodeGridOriginalParent!.Controls.GetChildIndex(dgvEncodeQueue);
            }

            // reparent
            dgvEncodeQueue.Parent = newParent;
            dgvEncodeQueue.Dock = DockStyle.Fill;

            // keep context menu and events intact; nothing else to do
        }

        private void RestoreEncodeGridToOriginalParent()
        {
            if (_encodeGridOriginalParent == null) return;

            dgvEncodeQueue.Parent = _encodeGridOriginalParent;
            _encodeGridOriginalParent.Controls.SetChildIndex(dgvEncodeQueue, _encodeGridOriginalIndex);
            dgvEncodeQueue.Dock = DockStyle.Fill;
        }



        // JOB TIMER FIELDS
        private System.Windows.Forms.Timer jobTimer = new System.Windows.Forms.Timer();
        private Stopwatch jobStopwatch = new Stopwatch();

        public MainForm()
        {
            InitializeComponent();
            Text = $"MediaFlux v{UpdateManager.CurrentVersion}";


            // Promote progressPanel to a global, bottom-docked panel shared by all modes
            if (progressPanel != null && progressPanel.Parent != null)
            {
                // Remove from tlEncode and attach directly to the form
                progressPanel.Parent.Controls.Remove(progressPanel);
                progressPanel.Dock = DockStyle.Bottom;
                Controls.Add(progressPanel);
                progressPanel.SendToBack();
                menuStrip1.BringToFront();
                statusStrip1.BringToFront();
            }

            InitializeEncodingSpinner();

            // load configuration before constructing FFmpeg-dependent services
            _configPath = AppPaths.ConfigFile;
            _config = Config.Load(_configPath);

            InitializeCompactModeControls();

            // supported extension list storage (managed via Settings)
            _supportedVideoExtsPath = Path.Combine(AppPaths.DataDirectory, "supported_video_extensions.json");
            RepairConfiguredExplorerIntegration();

            InitializeLargeQueueControls();
            StartEstimateUiPump();
            CreateAutoQualityControl();
            UpdateAudioUiState();
            CreateAdvancedVideoControls();
            CreateEncodeInfoPanels();

            // Tools → View History
            var viewHistoryToolStripMenuItem = new ToolStripMenuItem("View History");
            viewHistoryToolStripMenuItem.Click += ViewHistoryToolStripMenuItem_Click;
            toolsToolStripMenuItem.DropDownItems.Insert(0, viewHistoryToolStripMenuItem);
            var analyzeDuplicatesToolStripMenuItem = new ToolStripMenuItem("Run Duplicate Check Again");
            analyzeDuplicatesToolStripMenuItem.Click += AnalyzeDuplicatesNow_Click;
            var duplicateManagerToolStripMenuItem = new ToolStripMenuItem("Duplicate Manager");
            duplicateManagerToolStripMenuItem.Click += ShowDuplicateManager_Click;
            var exportDuplicateReportToolStripMenuItem = new ToolStripMenuItem("Export Duplicate Report");
            exportDuplicateReportToolStripMenuItem.Click += ExportDuplicateReport_Click;
            toolsToolStripMenuItem.DropDownItems.Insert(1, analyzeDuplicatesToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(2, duplicateManagerToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(3, exportDuplicateReportToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(4, new ToolStripSeparator());

            // File → View Error Log
            var viewErrorLogToolStripMenuItem = new ToolStripMenuItem("View Error Log");
            viewErrorLogToolStripMenuItem.Click += ViewErrorLogToolStripMenuItem_Click;
            var viewDuplicateActionLogToolStripMenuItem = new ToolStripMenuItem("View Duplicate Action Log");
            viewDuplicateActionLogToolStripMenuItem.Click += ViewDuplicateActionLogToolStripMenuItem_Click;
            var exitIndex = fileToolStripMenuItem.DropDownItems.IndexOf(exitToolStripMenuItem);
            if (exitIndex < 0)
            {
                fileToolStripMenuItem.DropDownItems.Add(viewErrorLogToolStripMenuItem);
                fileToolStripMenuItem.DropDownItems.Add(viewDuplicateActionLogToolStripMenuItem);
            }
            else
            {
                fileToolStripMenuItem.DropDownItems.Insert(exitIndex, viewErrorLogToolStripMenuItem);
                fileToolStripMenuItem.DropDownItems.Insert(exitIndex + 1, viewDuplicateActionLogToolStripMenuItem);
                fileToolStripMenuItem.DropDownItems.Insert(exitIndex + 2, new ToolStripSeparator());
            }

            //History service init
            var historyPath = Path.Combine(AppPaths.DataDirectory, "history.json");
            _historyService = new HistoryService(historyPath);
            _presetService = new EncodingPresetService(
                Path.Combine(AppPaths.DataDirectory, "encoding_presets.json"));

            // Touch lblResolution so the field is considered "used" by the analyzer
            _ = lblResolution;

            // Codec Selection
            comboVideoFormat.SelectedIndex = 0; // H.265 / HEVC (x265)

            dgvEncodeQueue.RowsAdded += (_, __) => { if (!_suppressRowEvents) SafeRefreshEstimates(); };
            dgvEncodeQueue.RowsRemoved += (_, __) => { if (!_suppressRowEvents) SafeRefreshEstimates(); };
            dgvEncodeQueue.SortCompare += DgvEncodeQueue_SortCompare;
            dgvEncodeQueue.Sorted += DgvEncodeQueue_Sorted;
            dgvEncodeQueue.CellValueChanged += (_, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < dgvEncodeQueue.Rows.Count)
                    ApplyEncodeRowVisualState(dgvEncodeQueue.Rows[e.RowIndex]);
            };
            dgvEncodeQueue.SelectionChanged += (s, e) =>
            {
                UpdateSelectedSpaceTotals();
                UpdateEncodePreview();
            };

            chkAutoTargetSize.CheckedChanged += (_, __) => SafeRefreshEstimates();
            comboCompressionProfile.SelectedIndexChanged += (_, __) => SafeRefreshEstimates();

            chkIncludeSubfolders.CheckedChanged += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(cmbInputFolder.Text) &&
                    Directory.Exists(cmbInputFolder.Text))
                {
                    _ = ImportEncodePathsAsync(
                        new[] { cmbInputFolder.Text },
                        chkIncludeSubfolders.Checked,
                        applyCodecFilters: true,
                        replaceExisting: !_encodingActive);
                }
            };

            void SafeRefreshEstimates()
            {
                // only refresh if there are rows
                if (dgvEncodeQueue.Rows.Count > 0)
                    RunEstimatePass();
            }

            RecreateMediaServices();

            _encodeQueueRunner = new EncodeQueueRunner();

            InitializeAudioQueueContextMenu();
            InitializeEncodeQueueContextMenu();

            // ─── menu / toolbar handlers ───────────────────────────
            this.exitToolStripMenuItem.Click += exitToolStripMenuItem_Click;
            this.clearFolderHistoryToolStripMenuItem.Click += new System.EventHandler(this.clearFolderHistoryToolStripMenuItem_Click);
            this.settingsToolStripMenuItem.Click += new System.EventHandler(this.SettingsToolStripMenuItem_Click);
            this.checkForUpdatesToolStripMenuItem.Click += new System.EventHandler(this.CheckForUpdatesToolStripMenuItem_Click);
            this.columnSettingsToolStripMenuItem.Click += new System.EventHandler(this.ColumnSettingsToolStripMenuItem_Click);
            InitializePresetMenu();

            // Job timer wiring
            jobTimer.Interval = 1000;
            jobTimer.Tick += JobTimer_Tick;

            // wire up Load for restoring settings
            this.Load += MainForm_Load;

            WireCheckboxPersistence();
            ApplyRememberedCheckboxStates();
            ApplyEncodeInfoHeaderCollapsedState(_config.EncodeInfoHeaderCollapsed);
            ApplyDuplicateFinderCollapsedState(_config.DuplicateFinderCollapsed);

            // Encode defaults
            comboEncoderMode.SelectedItem = "GPU (NVENC)";

            WireEncodePreviewAndDropdownPersistence();
            ApplyRememberedEncodeDropdowns();

            // persist column‐width changes
            dgvEncodeQueue.ColumnWidthChanged += DgvEncodeQueue_ColumnWidthChanged;

            // enable drag‐drop reordering
            dgvEncodeQueue.GiveFeedback += (s, ev) => ev.UseDefaultCursors = true;

            //Enable keydown for delete
            dgvEncodeQueue.KeyDown += dgvEncodeQueue_KeyDown;

            dgvEncodeQueue.CellPainting += dgvEncodeQueue_CellPainting;

            comboAudioOperation_SelectedIndexChanged(null, EventArgs.Empty);

            // Init metrics panel to dashes/defaults
            ResetEncodeMetrics();
            InitializeExplorerQueueIntegration();
        }

        private static double TryParseSizeToMb(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;

            var parts = s.Split(' ');
            if (parts.Length < 2)
                return 0;

            // First token is the number ("39.2"), second is the unit ("MB", "GB", etc.)
            var numberPart = parts[0].Trim();

            if (!double.TryParse(
                    numberPart,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture,
                    out var value) &&
                !double.TryParse(
                    numberPart,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value))
            {
                return 0;
            }

            var unit = parts[1].Trim().ToUpperInvariant();

            if (unit.StartsWith("GB"))
                return value * 1024.0;
            if (unit.StartsWith("MB"))
                return value;
            if (unit.StartsWith("KB"))
                return value / 1024.0;
            if (unit.StartsWith("B"))
                return value / (1024.0 * 1024.0);

            return 0;
        }

        private void CreateEncodeInfoPanels()
        {
            if (tlEncode == null || dgvEncodeQueue == null || _encodeQueueLayout != null)
                return;

            var position = tlEncode.GetPositionFromControl(dgvEncodeQueue);
            if (position.Row < 0)
                return;

            tlEncode.Controls.Remove(dgvEncodeQueue);

            _encodeQueueLayout = new TableLayoutPanel
            {
                Name = "tlEncodeQueueAndInfo",
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            _encodeQueueLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _encodeQueueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _encodeQueueLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            dgvEncodeQueue.Dock = DockStyle.Fill;
            _encodeQueueLayout.Controls.Add(CreateEncodeInfoHeader(), 0, 0);
            _encodeQueueLayout.Controls.Add(dgvEncodeQueue, 0, 1);

            tlEncode.Controls.Add(_encodeQueueLayout, position.Column, position.Row);
            tlEncode.SetColumnSpan(_encodeQueueLayout, 4);
            InitializeEncodeStatusRelocation();
            UpdateSizeTotals();
            UpdateEncodePreview();
        }

        private void InitializeEncodeStatusRelocation()
        {
            if (lblEncodeStatus == null)
                return;

            lblEncodeStatus.Visible = false;
            lblEncodeStatus.TextChanged += (_, __) => UpdateRelocatedEncodeStatus(lblEncodeStatus.Text);
            UpdateRelocatedEncodeStatus(lblEncodeStatus.Text);
        }

        private void UpdateRelocatedEncodeStatus(string? statusText)
        {
            string displayText = string.IsNullOrWhiteSpace(statusText)
                ? "Ready"
                : statusText.Trim();

            if (_summaryQueueStatusValue != null)
            {
                _summaryQueueStatusValue.Text = displayText;
                _summaryQueueStatusValue.MaximumSize = new Size(Math.Max(240, _summaryQueueStatusValue.Parent?.Width ?? 600), 0);
            }

            if (_collapsedQueueStatusLabel != null)
            {
                _collapsedQueueStatusLabel.Text = displayText;
                UpdateCollapsedQueueStatusLabelMaximumWidth();
            }
        }

        private void InitializeLargeQueueControls()
        {
            _analyzeQueueButton = new ToolStripButton("Analyze Queue")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false
            };
            _analyzeQueueButton.Click += (_, __) => AnalyzeQueueNow();

            _cancelQueueWorkButton = new ToolStripButton("Cancel Queue Work")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Visible = false
            };
            _cancelQueueWorkButton.Click += (_, __) => CancelQueueBackgroundWork();

            _queueProgressBar = new ToolStripProgressBar
            {
                Visible = false,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 140
            };

            statusStrip1.Items.Add(new ToolStripStatusLabel { Spring = true });
            statusStrip1.Items.Add(_queueProgressBar);
            statusStrip1.Items.Add(_analyzeQueueButton);
            statusStrip1.Items.Add(_cancelQueueWorkButton);
        }

        private void SetQueueProgress(int current, int total, bool visible)
        {
            if (_queueProgressBar == null)
                return;

            _queueProgressBar.Visible = visible;
            if (!visible)
            {
                _queueProgressBar.Value = 0;
                return;
            }

            _queueProgressBar.Maximum = 100;
            int percent = total > 0
                ? Math.Clamp((int)Math.Round((current / (double)total) * 100), 0, 100)
                : 0;
            _queueProgressBar.Value = percent;
        }

        private void SetQueueWorkCancelVisible(bool visible)
        {
            if (_cancelQueueWorkButton != null)
                _cancelQueueWorkButton.Visible = visible;
        }

        private void UpdateAnalyzeQueueButtonState()
        {
            if (_analyzeQueueButton != null)
                _analyzeQueueButton.Enabled = dgvEncodeQueue != null && dgvEncodeQueue.Rows.Count > 0 && !_encodingActive;
        }

        private void AnalyzeQueueNow()
        {
            if (dgvEncodeQueue.Rows.Count == 0)
                return;

            _estimatesDeferredForLargeQueue = false;
            RunEstimatePass(force: true);
        }

        private void CancelQueueBackgroundWork()
        {
            try { _importCts?.Cancel(); } catch { }
            try { _codecFilterCts?.Cancel(); } catch { }
            try { _duplicateScanCts?.Cancel(); } catch { }
            _estimateService?.ResetAndCancel();
            _estimatesDeferredForLargeQueue = false;
            SetQueueProgress(0, 0, visible: false);
            SetQueueWorkCancelVisible(false);
            toolStripStatusLabel1.Text = "Queue background work canceled.";
            UpdateRelocatedEncodeStatus("Queue background work canceled.");
        }

        private int GetLargeQueueThreshold()
        {
            return Math.Clamp(_config?.LargeQueueThreshold ?? 300, 1, 10000);
        }

        private void UpdateCollapsedQueueStatusLabelMaximumWidth()
        {
            if (_collapsedQueueStatusLabel?.Parent == null)
                return;

            _collapsedQueueStatusLabel.Parent.PerformLayout();
        }

        private Control CreateEncodeInfoHeader()
        {
            var outer = new Panel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 0, 0, 8),
                Padding = Padding.Empty
            };

            var shell = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = Padding.Empty,
                Margin = new Padding(0),
                BackColor = Color.White
            };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            shell.Controls.Add(CreateEncodeInfoToggleBar(), 0, 0);

            _encodeInfoHeaderContent = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                ColumnCount = 1,
                RowCount = 1,
                Padding = new Padding(0),
                Margin = new Padding(0),
                BackColor = SystemColors.Control
            };
            _encodeInfoHeaderContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _encodeInfoHeaderContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _encodeInfoHeaderContent.Controls.Add(CreateEncodeInfoTabs(), 0, 0);

            shell.Controls.Add(_encodeInfoHeaderContent, 0, 1);
            outer.Controls.Add(shell);
            return outer;
        }

        private Control CreateEncodeInfoTabs()
        {
            _encodeInfoTabs = new TabControl
            {
                Dock = DockStyle.Top,
                Height = 230,
                Margin = Padding.Empty
            };

            _encodeInfoTabs.TabPages.Add(CreateScrollableInfoTab("Queue Summary", CreateQueueSummaryGroup()));
            _encodeInfoTabs.TabPages.Add(CreateScrollableInfoTab("Output Preview", CreateEncodePreviewGroup()));
            return _encodeInfoTabs;
        }

        private static TabPage CreateScrollableInfoTab(string title, Control content)
        {
            var page = new TabPage(title)
            {
                Padding = new Padding(6),
                BackColor = SystemColors.Control
            };

            var scrollHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = SystemColors.Control
            };

            content.Dock = DockStyle.Top;
            scrollHost.Controls.Add(content);
            page.Controls.Add(scrollHost);
            return page;
        }

        private Control CreateEncodeInfoToggleBar()
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 4),
                Padding = new Padding(8, 5, 8, 5),
                BackColor = Color.FromArgb(248, 249, 251),
                Cursor = Cursors.Hand
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _btnToggleEncodeInfoHeader = new Button
            {
                Text = "v",
                Width = 26,
                Height = 24,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(0, 0, 8, 0)
            };
            _btnToggleEncodeInfoHeader.Click += (_, __) => ToggleEncodeInfoHeaderCollapsed();

            var title = new Label
            {
                Text = "Summary / Preview",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(27, 34, 43),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 14, 0)
            };

            panel.Controls.Add(_btnToggleEncodeInfoHeader, 0, 0);
            panel.Controls.Add(title, 1, 0);

            _collapsedQueueStatusLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 24,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = Color.FromArgb(35, 35, 35),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 0, 0),
                Text = "Ready"
            };
            panel.Controls.Add(_collapsedQueueStatusLabel, 2, 0);

            panel.Click += (_, __) => ToggleEncodeInfoHeaderCollapsed();
            title.Click += (_, __) => ToggleEncodeInfoHeaderCollapsed();
            _collapsedQueueStatusLabel.Click += (_, __) => ToggleEncodeInfoHeaderCollapsed();

            panel.SizeChanged += (_, __) => UpdateCollapsedQueueStatusLabelMaximumWidth();
            _btnToggleEncodeInfoHeader.SizeChanged += (_, __) => UpdateCollapsedQueueStatusLabelMaximumWidth();

            return panel;
        }

        private void ToggleEncodeInfoHeaderCollapsed()
        {
            bool collapse = _encodeInfoHeaderContent?.Visible == true;
            ApplyEncodeInfoHeaderCollapsedState(collapse);

            if (_config != null)
            {
                _config.EncodeInfoHeaderCollapsed = collapse;
                _config.Save(_configPath);
            }
        }

        private void ApplyEncodeInfoHeaderCollapsedState(bool collapsed)
        {
            if (_encodeInfoHeaderContent != null)
                _encodeInfoHeaderContent.Visible = !collapsed;

            if (_btnToggleEncodeInfoHeader != null)
                _btnToggleEncodeInfoHeader.Text = collapsed ? ">" : "v";
        }

        private void ApplyDuplicateFinderCollapsedState(bool collapsed)
        {
            if (_duplicateFinderBodyPanel != null)
                _duplicateFinderBodyPanel.Visible = !collapsed;

            if (_btnToggleDuplicateFinder != null)
                _btnToggleDuplicateFinder.Text = collapsed ? ">" : "v";

            UpdateDuplicateFinderHeaderStatus();
        }

        private void ToggleDuplicateFinderCollapsed()
        {
            bool collapsed = _duplicateFinderBodyPanel?.Visible == true;
            ApplyDuplicateFinderCollapsedState(collapsed);

            if (_config != null)
            {
                _config.DuplicateFinderCollapsed = collapsed;
                _config.Save(_configPath);
            }
        }

        private void UpdateDuplicateFinderHeaderStatus()
        {
            if (_duplicateFinderHeaderStatusLabel == null)
                return;

            _duplicateFinderHeaderStatusLabel.Text = lblDuplicateFinderStatus?.Text ?? "Duplicate Finder";
            _duplicateFinderHeaderStatusLabel.ForeColor = lblDuplicateFinderStatus?.ForeColor ?? SystemColors.GrayText;
        }

        private Control CreateQueueSummaryGroup()
        {
            var group = new GroupBox
            {
                Text = "Queue Summary",
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(12, 10, 12, 12),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Margin = new Padding(0, 0, 8, 0),
                BackColor = Color.White
            };

            _queueSummaryTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 0,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.White
            };
            _queueSummaryTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _queueSummaryTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _summaryQueueStatusValue = AddSummaryStatusRow(_queueSummaryTable, "Ready");
            _summaryFileCountValue = AddSummaryTile(_queueSummaryTable, "Files", "0", 0, 1);
            _summarySelectedCountValue = AddSummaryTile(_queueSummaryTable, "Selected", "0", 1, 1);
            _summaryTotalCurrentValue = AddSummaryTile(_queueSummaryTable, "Current size", "--", 0, 2);
            _summaryNewSizeValue = AddSummaryTile(_queueSummaryTable, "Estimated output", "--", 1, 2);
            _summarySelectedSavedValue = AddSummaryTile(_queueSummaryTable, "Selected savings", "--", 0, 3, Color.FromArgb(23, 117, 74));
            _summaryTotalEstimatedSavedValue = AddSummaryTile(_queueSummaryTable, "Total estimated savings", "--", 1, 3, Color.FromArgb(23, 117, 74));
            _summaryEstimatedCompletionValue = AddSummaryTile(_queueSummaryTable, "Estimated completion", "--", 0, 4);
            if (_summaryEstimatedCompletionValue.Parent is Control completionTile)
                _queueSummaryTable.SetColumnSpan(completionTile, 2);
            _summaryDuplicateGroupsValue = AddSummaryTile(_queueSummaryTable, "Duplicate groups", "--", 0, 5);
            _summaryDuplicateFilesValue = AddSummaryTile(_queueSummaryTable, "Duplicate files", "--", 1, 5);
            _summaryDuplicateRecoverableValue = AddSummaryTile(_queueSummaryTable, "Recoverable duplicate space", "--", 0, 6, Color.FromArgb(138, 75, 12));
            if (_summaryDuplicateRecoverableValue.Parent is Control duplicateSpaceTile)
                _queueSummaryTable.SetColumnSpan(duplicateSpaceTile, 2);
            group.Controls.Add(_queueSummaryTable);
            return group;
        }

        private Control CreateEncodePreviewGroup()
        {
            var group = new GroupBox
            {
                Text = "Output Preview",
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(12, 8, 12, 10),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Margin = new Padding(0),
                BackColor = Color.White
            };

            _encodePreviewTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 0,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.White
            };
            _encodePreviewTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            AddPreviewSection(_encodePreviewTable, "SELECTED JOB");
            AddPreviewPathRow(_encodePreviewTable, "Source", "--");
            AddPreviewPathRow(_encodePreviewTable, "Output", "--");

            var jobMetrics = CreatePreviewMetricGrid();
            AddPreviewMetric(jobMetrics, "Target", "--", 0, 0);
            AddPreviewControl(_encodePreviewTable, jobMetrics);

            AddPreviewSection(_encodePreviewTable, "VIDEO");
            var videoMetrics = CreatePreviewMetricGrid();
            AddPreviewMetric(videoMetrics, "Length", "--", 0, 0);
            AddPreviewMetric(videoMetrics, "Quality", "--", 1, 0);
            AddPreviewMetric(videoMetrics, "Dimensions", "--", 0, 1);
            AddPreviewMetric(videoMetrics, "Codec", "--", 1, 1);
            AddPreviewMetric(videoMetrics, "Data rate", "--", 0, 2);
            AddPreviewMetric(videoMetrics, "Total bitrate", "--", 1, 2);
            AddPreviewMetric(videoMetrics, "Frame rate", "--", 0, 3);
            AddPreviewControl(_encodePreviewTable, videoMetrics);

            AddPreviewSection(_encodePreviewTable, "AUDIO");
            var audioMetrics = CreatePreviewMetricGrid(3);
            AddPreviewStackedMetric(audioMetrics, "Bit rate", "--", 0);
            AddPreviewStackedMetric(audioMetrics, "Channels", "--", 1);
            AddPreviewStackedMetric(audioMetrics, "Audio sample rate", "Keep source", 2);
            AddPreviewControl(_encodePreviewTable, audioMetrics);
            group.Controls.Add(_encodePreviewTable);
            return group;
        }

        private static Label AddSummaryStatusRow(TableLayoutPanel table, string value)
        {
            table.RowCount = Math.Max(table.RowCount, 1);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(9, 6, 9, 7),
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Color.FromArgb(241, 246, 252)
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            card.Controls.Add(CreateInfoCaption("QUEUE STATUS"), 0, 0);
            var valueLabel = CreateInfoValue(value, bold: true);
            valueLabel.AutoSize = true;
            valueLabel.MaximumSize = new Size(600, 0);
            card.Controls.Add(valueLabel, 0, 1);
            table.Controls.Add(card, 0, 0);
            table.SetColumnSpan(card, 2);
            return valueLabel;
        }

        private static Label AddSummaryTile(TableLayoutPanel table, string caption, string value, int column, int row, Color? valueColor = null)
        {
            while (table.RowCount <= row)
            {
                table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            var tile = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8, 4, 8, 5),
                Margin = new Padding(column == 0 ? 0 : 3, 0, column == 0 ? 3 : 0, 3),
                BackColor = Color.FromArgb(248, 249, 251)
            };
            tile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tile.Controls.Add(CreateInfoCaption(caption.ToUpperInvariant()), 0, 0);
            var valueLabel = CreateInfoValue(value, bold: true);
            if (valueColor.HasValue)
                valueLabel.ForeColor = valueColor.Value;
            tile.Controls.Add(valueLabel, 0, 1);
            table.Controls.Add(tile, column, row);
            return valueLabel;
        }

        private static Label CreateInfoCaption(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(92, 101, 112),
            Margin = new Padding(0, 0, 0, 1)
        };

        private static Label CreateInfoValue(string text, bool bold = false) => new()
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 9F, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = Color.FromArgb(27, 34, 43),
            TextAlign = ContentAlignment.MiddleLeft,
            Height = 20,
            Margin = Padding.Empty
        };

        private static void AddPreviewSection(TableLayoutPanel table, string text)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = CreateInfoCaption(text);
            header.ForeColor = Color.FromArgb(31, 88, 166);
            header.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
            header.Margin = new Padding(0, row == 0 ? 1 : 7, 0, 3);
            table.Controls.Add(header, 0, row);
        }

        private void AddPreviewPathRow(TableLayoutPanel table, string caption, string value)
        {
            var rowPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 3),
                Padding = new Padding(7, 3, 7, 3),
                BackColor = Color.FromArgb(248, 249, 251)
            };
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54F));
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            var captionLabel = CreateInfoCaption(caption.ToUpperInvariant());
            captionLabel.Anchor = AnchorStyles.Left;
            var valueLabel = CreateInfoValue(value);
            valueLabel.Height = 34;
            rowPanel.Controls.Add(captionLabel, 0, 0);
            rowPanel.Controls.Add(valueLabel, 1, 0);
            AddPreviewControl(table, rowPanel);
            _previewValueLabels[caption] = valueLabel;
        }

        private static TableLayoutPanel CreatePreviewMetricGrid(int columns = 2)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = columns,
                RowCount = 0,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            for (int column = 0; column < columns; column++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
            return grid;
        }

        private void AddPreviewStackedMetric(TableLayoutPanel grid, string caption, string value, int column)
        {
            if (grid.RowCount == 0)
            {
                grid.RowCount = 1;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            var metric = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Padding = Padding.Empty,
                Margin = new Padding(column == 0 ? 0 : 5, 0, column == grid.ColumnCount - 1 ? 0 : 5, 2)
            };
            metric.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            metric.Controls.Add(CreateInfoCaption(caption.ToUpperInvariant()), 0, 0);
            var valueLabel = CreateInfoValue(value);
            metric.Controls.Add(valueLabel, 0, 1);
            grid.Controls.Add(metric, column, 0);
            _previewValueLabels[caption] = valueLabel;
        }

        private void AddPreviewMetric(TableLayoutPanel grid, string caption, string value, int column, int row)
        {
            while (grid.RowCount <= row)
            {
                grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            var metric = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(column == 0 ? 0 : 5, 0, column == 0 ? 5 : 0, 2),
                Padding = Padding.Empty
            };
            metric.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
            metric.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));
            var captionLabel = CreateInfoCaption(caption.ToUpperInvariant());
            captionLabel.Anchor = AnchorStyles.Left;
            var valueLabel = CreateInfoValue(value);
            metric.Controls.Add(captionLabel, 0, 0);
            metric.Controls.Add(valueLabel, 1, 0);
            grid.Controls.Add(metric, column, row);
            _previewValueLabels[caption] = valueLabel;
        }

        private static void AddPreviewControl(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control, 0, row);
        }

        private void WireEncodePreviewAndDropdownPersistence()
        {
            comboCompressionProfile.SelectedIndexChanged += (_, __) =>
            {
                if (!_applyingEncodeDropdownSettings)
                {
                    _config.LastCompressionProfile = comboCompressionProfile.Text;
                    _config.Save(_configPath);
                }

                UpdateEncodePreview();
            };

            if (comboNvencPreset != null)
            {
                comboNvencPreset.SelectedIndexChanged += (_, __) =>
                {
                    if (!_applyingEncodeDropdownSettings)
                    {
                        _config.LastEncodingSpeedPreset = comboNvencPreset.Text;
                        _config.Save(_configPath);
                    }

                    UpdateEncodePreview();
                };
            }

            comboVideoFormat.SelectedIndexChanged += (_, __) => UpdateEncodePreview();
            comboEncoderMode.SelectedIndexChanged += (_, __) => UpdateEncodePreview();
            txtTargetSize.TextChanged += (_, __) => UpdateEncodePreview();
            chkAutoTargetSize.CheckedChanged += (_, __) => UpdateEncodePreview();

            if (comboAudioChannels != null)
                comboAudioChannels.SelectedIndexChanged += (_, __) => UpdateEncodePreview();
            if (comboResolution != null)
                comboResolution.SelectedIndexChanged += (_, __) => UpdateEncodePreview();
            if (chkTenBit != null)
                chkTenBit.CheckedChanged += (_, __) => UpdateEncodePreview();
        }

        private void ApplyRememberedEncodeDropdowns()
        {
            _applyingEncodeDropdownSettings = true;
            try
            {
                SelectComboText(comboCompressionProfile, _config.LastCompressionProfile);
                if (comboNvencPreset != null)
                    SelectComboText(comboNvencPreset, _config.LastEncodingSpeedPreset);
            }
            finally
            {
                _applyingEncodeDropdownSettings = false;
            }

            UpdateEncodePreview();
        }

        private static void SelectComboText(ComboBox combo, string? savedValue)
        {
            if (combo == null || string.IsNullOrWhiteSpace(savedValue))
                return;

            foreach (var item in combo.Items)
            {
                if (string.Equals(item?.ToString(), savedValue, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void SaveEncodeDropdownPreferences()
        {
            if (comboCompressionProfile != null)
                _config.LastCompressionProfile = comboCompressionProfile.Text;
            if (comboNvencPreset != null)
                _config.LastEncodingSpeedPreset = comboNvencPreset.Text;
        }

        private void SetPreviewValue(string key, string value)
        {
            if (_previewValueLabels.TryGetValue(key, out var label))
            {
                string actual = string.IsNullOrWhiteSpace(value) ? "--" : value;
                label.Text = actual;
                _uiToolTip.SetToolTip(label, key is "Output" or "Source" ? actual : string.Empty);
            }
        }

        private void UpdateEncodePreview()
        {
            if (_previewValueLabels.Count == 0 || dgvEncodeQueue == null)
                return;

            var row = dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault(r => !r.IsNewRow)
                      ?? dgvEncodeQueue.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => !r.IsNewRow);

            string? path = row != null ? GetFullPathFromRow(row) : null;
            double durationSec = 0;
            int width = 0;
            int height = 0;
            double fps = 0;
            double estimatedMb = 0;

            if (row != null)
            {
                if (row.Tag is RowMeta meta)
                {
                    durationSec = meta.DurationSec;
                    if (meta.SrcMb > 0 && string.IsNullOrWhiteSpace(path))
                        path = meta.Path;
                }

                estimatedMb = ParseSizeToMb(row.Cells["colEstimatedSize"].Value?.ToString());
            }

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                if (durationSec <= 0)
                    durationSec = ProbeDurationSeconds(path);

                var dimensions = ProbeResolutionPixels(path);
                width = dimensions.w;
                height = dimensions.h;
                fps = ProbeFps(path);

                if (estimatedMb <= 0 && _estimatedSizeMap.TryGetValue(path, out var mappedEstimate))
                    estimatedMb = mappedEstimate;
            }

            var outputDimensions = GetPreviewOutputDimensions(width, height);
            var bitrateKbps = durationSec > 0 && estimatedMb > 0
                ? (estimatedMb * 8192.0) / durationSec
                : 0;
            int audioBitrate = EstimatePreviewAudioBitrateKbps();
            int dataRate = bitrateKbps > 0
                ? Math.Max(0, (int)Math.Round(bitrateKbps - audioBitrate))
                : 0;

            var encoderText = comboEncoderMode?.Text ?? string.Empty;
            var formatText = comboVideoFormat?.Text ?? string.Empty;
            string codec = ResolveVideoCodec(encoderText, formatText);
            if (chkTenBit?.Checked == true && (codec.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                                               codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                                               codec.Contains("av1", StringComparison.OrdinalIgnoreCase)))
            {
                codec += " 10-bit";
            }

            double? targetMb = null;
            string outputPreview = "--";
            if (row?.Tag is RowMeta selectedMeta)
                targetMb = selectedMeta.CustomTargetMb;

            if (!targetMb.HasValue && estimatedMb > 0)
                targetMb = estimatedMb;

            if (!string.IsNullOrWhiteSpace(path))
            {
                string outputFolder = cmbEncodeOutput?.Text ?? "";
                if (string.IsNullOrWhiteSpace(outputFolder))
                    outputFolder = Path.GetDirectoryName(path) ?? "";

                outputPreview = Path.Combine(
                    outputFolder,
                    Path.GetFileNameWithoutExtension(path) + BuildOutputSuffix(formatText) + ".mp4");
            }

            SetPreviewValue("Source", !string.IsNullOrWhiteSpace(path) ? path : "--");
            SetPreviewValue("Target", targetMb.HasValue ? $"{targetMb.Value:N1} MB" : "Auto");
            SetPreviewValue("Output", outputPreview);
            SetPreviewValue("Length", durationSec > 0 ? TimeSpan.FromSeconds(durationSec).ToString(@"hh\:mm\:ss") : "--");
            SetPreviewValue("Dimensions", outputDimensions.width > 0 && outputDimensions.height > 0
                ? $"{outputDimensions.width} × {outputDimensions.height}"
                : "--");
            SetPreviewValue("Codec", codec);
            SetPreviewValue("Quality", comboCompressionProfile?.Text ?? "--");
            SetPreviewValue("Data rate", dataRate > 0 ? $"{dataRate:0}kbps" : "--");
            SetPreviewValue("Total bitrate", bitrateKbps > 0 ? $"{bitrateKbps:0}kbps" : "--");
            SetPreviewValue("Frame rate", fps > 0 ? $"{fps:0.##} frames/second" : "--");
            SetPreviewValue("Bit rate", audioBitrate > 0 ? $"{audioBitrate:0}kbps" : "Keep source");
            SetPreviewValue("Channels", GetPreviewAudioChannelsText());
            SetPreviewValue("Audio sample rate", "Keep source");
        }

        private (int width, int height) GetPreviewOutputDimensions(int sourceWidth, int sourceHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return (0, 0);

            int targetHeight = GetSelectedScaleMode() switch
            {
                EncodingService.ScaleMode.To720p => 720,
                EncodingService.ScaleMode.To1080p => 1080,
                EncodingService.ScaleMode.To1440p => 1440,
                EncodingService.ScaleMode.To4K => 2160,
                _ => sourceHeight
            };

            if (targetHeight == sourceHeight)
                return (sourceWidth, sourceHeight);

            int targetWidth = (int)Math.Round(sourceWidth * (targetHeight / (double)sourceHeight));
            if (targetWidth % 2 != 0)
                targetWidth++;

            return (Math.Max(2, targetWidth), targetHeight);
        }

        private int EstimatePreviewAudioBitrateKbps()
        {
            return GetSelectedAudioChannels() switch
            {
                2 => 192,
                6 => 384,
                _ => 0
            };
        }

        private string GetPreviewAudioChannelsText()
        {
            return GetSelectedAudioChannels() switch
            {
                2 => "Stereo",
                6 => "5.1",
                _ => "Keep source layout"
            };
        }

        private void UpdateSelectedSpaceTotals()
        {
            if (_summarySelectedCountValue != null)
                _summarySelectedCountValue.Text = dgvEncodeQueue.SelectedRows.Count.ToString();

            // No selection
            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                if (_summarySelectedSavedValue != null)
                    _summarySelectedSavedValue.Text = "--";
                return;
            }

            double totalSrcMb = 0;
            double totalEstMb = 0;
            int rowsWithEstimate = 0;

            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                // We use what is currently displayed in the grid
                var srcText = row.Cells["colSize"].Value as string;
                var estText = row.Cells["colEstimatedSize"].Value as string;

                double srcMb = TryParseSizeToMb(srcText);
                double estMb = TryParseSizeToMb(estText);

                // Skip rows that don't have an estimate yet
                if (srcMb <= 0 || estMb <= 0)
                    continue;

                totalSrcMb += srcMb;
                totalEstMb += estMb;
                rowsWithEstimate++;
            }

            if (rowsWithEstimate == 0)
            {
                if (_summarySelectedSavedValue != null)
                    _summarySelectedSavedValue.Text = "Waiting for estimates";
                return;
            }

            var savedMb = Math.Max(0, totalSrcMb - totalEstMb);
            var pctSaved = totalSrcMb > 0 ? (savedMb / totalSrcMb) * 100.0 : 0.0;

            // FormatSize works in bytes, so convert MB -> bytes
            var savedBytes = (long)(savedMb * 1024.0 * 1024.0);

            var selectedSummary = $"{FormatSize(savedBytes)} ({pctSaved:F0}% saved)";
            if (_summarySelectedSavedValue != null)
                _summarySelectedSavedValue.Text = selectedSummary;
        }


        private void InitializeEncodingSpinner()
        {
            if (progressPanel == null)
                return;

            _encodingSpinner = new PictureBox
            {
                Name = "picEncodingSpinner",
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Visible = false,
                Size = new Size(32, 32),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };

            progressPanel.Controls.Add(_encodingSpinner);
            _encodingSpinner.BringToFront();

            // Small status label next to the spinner (for "Encoding…", "Scanning…", etc.)
            _activityLabel = new Label
            {
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = SystemColors.ControlText,
                Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold),
                Visible = false
            };

            progressPanel.Controls.Add(_activityLabel);
            _activityLabel.BringToFront();

            RepositionEncodingSpinner();
            this.Resize += (_, __) => RepositionEncodingSpinner();
            progressPanel.Resize += (_, __) => RepositionEncodingSpinner();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var config = ActivityIndicatorConfigLoader.Load(baseDir);

            _activityIndicator = new ActivityIndicatorService(
                this,
                _encodingSpinner,
                _activityLabel,
                baseDir,
                config);
        }

        private void RepositionEncodingSpinner()
        {
            if (_encodingSpinner == null || progressPanel == null)
                return;

            const int margin = 10;

            _encodingSpinner.Location = new Point(
                progressPanel.ClientSize.Width - _encodingSpinner.Width - margin,
                progressPanel.ClientSize.Height - _encodingSpinner.Height - margin);

            if (progressBarEncode != null)
            {
                int rightLimit = _encodingSpinner.Left - 10;
                progressBarEncode.Width = Math.Max(120, rightLimit - progressBarEncode.Left);
            }

            if (_activityLabel != null)
            {
                int spacing = 8;

                // Place label to the left of the spinner, vertically centered.
                int x = _encodingSpinner.Left - _activityLabel.Width - spacing;
                if (x < margin) x = margin;

                int y = _encodingSpinner.Top +
                        (_encodingSpinner.Height - _activityLabel.Height) / 2;

                _activityLabel.Location = new Point(x, y);
            }
        }

        // Pause flag for encode queue
        private bool _encodeQueuePaused = false;

        // Track the active encode row for progress updates
        private DataGridViewRow? _activeEncodeRow = null;

        // Dynamic in-progress queue so we can append new rows while encoding
        private List<DataGridViewRow>? _activeEncodeQueue = null;
        private readonly object _activeEncodeQueueLock = new();
        private int _pendingEncodeImports = 0;
        private bool _suppressEncodeFolderSelectionScan = false;
        private int _folderImportGeneration = 0;
        private bool _duplicateRescanPending = false;

        // How many items in the active queue have finished
        private int _encodeProcessedCount = 0;
        private int _encodeRetryCount = 0;

        // Simple metadata for a grid row
        private sealed class RowMeta
        {
            public string Path = "";
            public double DurationSec = 0; // Initialized to suppress warning
            public string Resolution = ""; // Changed to string; initialized
            public string VideoCodec = "";
            public int Fps = 0; // Initialized to suppress warning
            public double SrcMb = 0; // Initialized to suppress warning
            public string? CustomCompressionProfile = null;
            public double? CustomTargetMb = null;
            public bool AutoRetryScheduled = false;
            public int? DuplicateGroupId = null;
            public string DuplicateConfidence = "";
            public int DuplicateConfidenceScore = 0;
            public string DuplicateRecommendation = "";
            public string DuplicateReason = "";
            public bool ExcludedFromEncodeAsDuplicate = false;
            public bool DuplicateExclusionOverridden = false;
            public string StatusBeforeDuplicateExclusion = "Queued";

            public bool HasCustomSettings =>
                CustomTargetMb.HasValue || !string.IsNullOrWhiteSpace(CustomCompressionProfile);
        }

        private RowMeta EnsureRowMeta(DataGridViewRow row)
        {
            if (row.Tag is RowMeta rm)
                return rm;

            var path = GetPathFromRow(row) ?? string.Empty;
            rm = new RowMeta { Path = path };
            row.Tag = rm;
            return rm;
        }

        private bool RowHasCustomSettings(RowMeta? meta)
        {
            return meta != null && meta.HasCustomSettings;
        }

        private void UpdateRowCustomFlag(DataGridViewRow row)
        {
            if (!dgvEncodeQueue.Columns.Contains("colCustom"))
                return;

            var meta = row.Tag as RowMeta;
            bool hasCustom = RowHasCustomSettings(meta);

            row.Cells["colCustom"].Value = hasCustom ? "Custom" : "";
            row.Cells["colCustom"].ToolTipText = hasCustom
                ? BuildCustomSettingsTooltip(meta!)
                : "";
        }

        private string BuildCustomSettingsTooltip(RowMeta meta)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(meta.CustomCompressionProfile))
                parts.Add($"Profile: {meta.CustomCompressionProfile}");

            if (meta.CustomTargetMb.HasValue)
                parts.Add($"Target: {meta.CustomTargetMb.Value:0.#} MB");

            return string.Join(" | ", parts);
        }

        private void ApplyCustomSettingsEstimate(DataGridViewRow row, RowMeta meta)
        {
            var path = GetPathFromRow(row);
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (meta.CustomTargetMb.HasValue && meta.CustomTargetMb.Value > 0)
            {
                double customMb = meta.CustomTargetMb.Value;
                _estimatedSizeMap[path] = customMb;
                row.Cells["colEstimatedSize"].Value = $"{FormatSize(customMb)} (custom)";
            }
            else if (!string.IsNullOrWhiteSpace(meta.CustomCompressionProfile))
            {
                double estMb = EstimateAutoTargetMbSmart(path, meta.CustomCompressionProfile);
                if (estMb > 0)
                {
                    _estimatedSizeMap[path] = estMb;
                    row.Cells["colEstimatedSize"].Value = $"{FormatSize(estMb)} (custom)";
                }
                else
                {
                    row.Cells["colEstimatedSize"].Value = "Custom";
                }
            }

            UpdateRowCustomFlag(row);
        }

        private bool TryGetRowPathAndDuration(DataGridViewRow row, out string path, out double durationSec)
        {
            path = "";
            durationSec = 0;

            if (row.Tag is RowMeta rm)
            {
                path = rm.Path;
                durationSec = rm.DurationSec;
                // No probing here – if durationSec is still 0, caller will simply not use it
                return !string.IsNullOrWhiteSpace(path);
            }

            if (row.Tag is string s)
            {
                // We only know the path at this point; metadata will arrive via the estimator
                path = s;
                durationSec = 0;
                return !string.IsNullOrWhiteSpace(path);
            }

            return false;
        }

        private void CreateAutoQualityControl()
        {
            nudAutoQuality = new NumericUpDown
            {
                Minimum = 12,   // sharper
                Maximum = 35,   // smaller
                Value = 22,   // sensible default for x264/x265
                Increment = 1,
                DecimalPlaces = 0,
                Width = 50,
                Name = "nudAutoQuality",
                TabIndex = (chkAutoTargetSize?.TabIndex ?? 0) + 1
            };

            var lbl = new Label
            {
                AutoSize = true,
                Text = "(CRF/CQ):",
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Try to place near your existing auto-size controls
            // Tweak the parent/layout to match your UI containers.
            var parent = chkAutoTargetSize?.Parent ?? this;
            parent.Controls.Add(lbl);
            parent.Controls.Add(nudAutoQuality);

            // naïve layout: put to the right of chkAutoTargetSize
            if (chkAutoTargetSize != null)
            {
                lbl.Location = new Point(chkAutoTargetSize.Right + 12, chkAutoTargetSize.Top + 3);
                nudAutoQuality.Location = new Point(lbl.Right + 6, chkAutoTargetSize.Top - 2);
            }

            // when Auto is unchecked, disable quality
            nudAutoQuality.Enabled = chkAutoTargetSize?.Checked ?? true;
            if (chkAutoTargetSize != null)
                chkAutoTargetSize.CheckedChanged += (_, __) =>
                    nudAutoQuality!.Enabled = chkAutoTargetSize.Checked;
        }

        private void CreateAdvancedVideoControls()
        {
            if (grpOptions == null)
                return;

            // Find the existing 2-column table inside the Options group
            var tlOptions = grpOptions.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            if (tlOptions == null)
            {
                // Safety fallback, but in your layout this should not trigger
                tlOptions = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    ColumnCount = 2
                };
                grpOptions.Controls.Add(tlOptions);
            }

            // --- Create controls ---

            // NVENC preset label + combo (placed under Quality / File Size)
            var lblPreset = new Label
            {
                Text = "Encoding Speed:",
                AutoSize = true,
                Margin = new Padding(4, 2, 4, 2),
                Anchor = AnchorStyles.Right
            };

            comboNvencPreset = new ComboBox
            {
                Name = "comboNvencPreset",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(4, 2, 4, 2),
                Dock = DockStyle.Fill
            };
            comboNvencPreset.Items.AddRange(new object[]
            {
                "Fastest (Lowest Quality)",   // p1
                "Fast (Lower Quality)",       // p2
                "Balanced (Recommended)",     // p5
                "High Quality (Slower)",      // p6
                "Max Quality (Slowest)"       // p7
            });
            comboNvencPreset.SelectedItem = "Balanced (Recommended)";

            if (tlEncode != null)
            {
                const int encodingSpeedRow = 6;
                tlEncode.Controls.Add(lblPreset, 0, encodingSpeedRow);
                tlEncode.Controls.Add(comboNvencPreset, 1, encodingSpeedRow);
                tlEncode.SetColumnSpan(comboNvencPreset, 1);
            }

            // 10-bit toggle
            chkTenBit = new CheckBox
            {
                Name = "chkTenBit",
                Text = "Use 10-bit for HEVC/AV1",
                AutoSize = true,
                Margin = new Padding(4, 2, 4, 2),
                Anchor = AnchorStyles.Left
            };

            // Audio channels label + combo
            //var lblChannels = new Label
            //{
            //  Text = "Audio channels:",
            //AutoSize = true,
            //Margin = new Padding(4, 2, 4, 2),
            //Anchor = AnchorStyles.Left
            //};

            comboAudioChannels = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 140,
                Margin = new Padding(4, 2, 4, 2),
                Anchor = AnchorStyles.Left
            };
            comboAudioChannels.Items.AddRange(new object[]
            {
                "Keep source layout",
                "Stereo (2.0)",
                "5.1 (5.1)"
            });
            comboAudioChannels.SelectedIndex = 0;

            chkWatchFolder = new CheckBox
            {
                Name = "chkWatchFolder",
                Text = "Watch folder automatically",
                AutoSize = true,
                Margin = new Padding(4, 4, 4, 2),
                Anchor = AnchorStyles.Left
            };

            lblWatchFolderStatus = new Label
            {
                Name = "lblWatchFolderStatus",
                Text = "Folder watching is off.",
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                Margin = new Padding(22, 0, 4, 4),
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Left
            };

            // --- Add as new rows in the existing 2-column table ---

            int startRow = tlOptions.RowCount;
            tlOptions.RowCount = startRow + 4;
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 10-bit + label row
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // audio combo row
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // watch-folder row
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // watch status/countdown row

            // Row: 10-bit + "Audio channels" label
            tlOptions.Controls.Add(chkTenBit, 0, startRow);
            // tlOptions.Controls.Add(lblChannels, 1, startRow);

            // Row: audio channels combo spanning full width
            tlOptions.Controls.Add(comboAudioChannels, 0, startRow + 1);
            tlOptions.SetColumnSpan(comboAudioChannels, 2);

            tlOptions.Controls.Add(chkWatchFolder, 0, startRow + 2);
            tlOptions.SetColumnSpan(chkWatchFolder, 2);

            tlOptions.Controls.Add(lblWatchFolderStatus, 0, startRow + 3);
            tlOptions.SetColumnSpan(lblWatchFolderStatus, 2);

            // Make sure tlOptions is in the group (in case something changed)
            if (!grpOptions.Controls.Contains(tlOptions))
            {
                tlOptions.Dock = DockStyle.Fill;
                grpOptions.Controls.Add(tlOptions);
            }

            // Tie preset enablement to GPU/CPU selection
            comboEncoderMode.SelectedIndexChanged += (_, __) => UpdateNvencUiState();
            UpdateNvencUiState();
        }

        private void UpdateNvencUiState()
        {
            bool isHardware = IsHardwareEncoderSelected();
            bool isNvenc = IsNvencSelected();

            // NVENC preset combo
            var presetCombo = grpOptions.Controls
                .Find("comboNvencPreset", true)
                .OfType<ComboBox>()
                .FirstOrDefault();

            if (presetCombo != null)
            {
                presetCombo.Enabled = isNvenc;
            }

            // Ten-bit checkbox
            var tenBitCheck = grpOptions.Controls
                .Find("chkTenBit", true)
                .OfType<CheckBox>()
                .FirstOrDefault();
            if (tenBitCheck != null)
            {
                tenBitCheck.Enabled = isHardware;
                if (!isHardware) tenBitCheck.Checked = false;
            }

        }

        private int GetDefaultQualityForSelection()
        {
            // If the user chose "No Compression", return a neutral quality.
            // (It will be ignored by the No-Compression ffmpeg branch, but avoids nulls/edge cases.)
            if (IsNoCompressionSelected())
            {
                var (_, isHardwareNC) = GetSelectedCodecInfo();
                return isHardwareNC ? 19 : 22; // CQ 19 for HW encoders, CRF 22 for CPU
            }

            // If the numeric control is present, use it directly.
            if (nudAutoQuality != null)
                return (int)nudAutoQuality.Value;

            // Fallback if nudAutoQuality isn’t there yet: infer from selection.
            var (codec, isHardware) = GetSelectedCodecInfo();
            if (isHardware) return 19;
            return (codec.IndexOf("265", StringComparison.OrdinalIgnoreCase) >= 0
                    || codec.IndexOf("av1", StringComparison.OrdinalIgnoreCase) >= 0)
                   ? 24   // libx265/libaom-av1
                   : 22;  // libx264
        }

        // Return selected ffmpeg video encoder name + isHardware flag
        private (string codec, bool isHardware) GetSelectedCodecInfo()
        {
            // Replace with your actual UI selectors.
            // Examples:
            // var fmt = cmbVideoFormat.SelectedItem?.ToString() ?? "H.264";
            // var enc = cmbEncoder.SelectedItem?.ToString() ?? "CPU";
            string fmt = GetSelectedFormatText();   // implement by reading your combo
            string enc = GetSelectedEncoderText();  // implement by reading your combo

            string codec = ResolveVideoCodec(enc, fmt);
            bool isHardware = IsHardwareEncoderSelected(enc);

            return (codec, isHardware);
        }

        private string GetSelectedFormatText() => comboVideoFormat?.Text ?? "H.264";
        private string GetSelectedEncoderText() => comboEncoderMode?.Text ?? "CPU";

        private bool IsHardwareEncoderSelected(string? encoderText = null)
        {
            encoderText ??= GetSelectedEncoderText();
            return encoderText.StartsWith("GPU", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsNvencSelected(string? encoderText = null)
        {
            encoderText ??= GetSelectedEncoderText();
            return encoderText.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsQsvSelected(string? encoderText = null)
        {
            encoderText ??= GetSelectedEncoderText();
            return encoderText.IndexOf("qsv", StringComparison.OrdinalIgnoreCase) >= 0
                   || encoderText.IndexOf("intel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private double ProbeDurationSeconds(string file)
        {
            return _mediaInfoService.GetDurationSeconds(file);
        }

        private static double ParseFfmpegTimeToSeconds(string hhmmss)
        {
            var parts = hhmmss.Split(':');
            if (parts.Length != 3) return 0;
            int hh = int.TryParse(parts[0], out var h) ? h : 0;
            int mm = int.TryParse(parts[1], out var m) ? m : 0;
            double ss = double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
            return hh * 3600 + mm * 60 + ss;
        }

        private void MainForm_Load(object? sender, EventArgs e)
        {
            RestoreMainWindowBounds();

            // default to Encode mode
            modeComboBox.SelectedItem = "Encode";
            ModeComboBox_SelectedIndexChanged(modeComboBox, EventArgs.Empty);

            // restore column visibility
            if (dgvEncodeQueue.Columns.Contains("colSize"))
                dgvEncodeQueue.Columns["colSize"].Visible = _config.ShowSizeColumn;
            if (dgvEncodeQueue.Columns.Contains("colCreated"))
                dgvEncodeQueue.Columns["colCreated"].Visible = _config.ShowCreatedColumn;
            if (dgvEncodeQueue.Columns.Contains("colCustom"))
                dgvEncodeQueue.Columns["colCustom"].Visible = _config.ShowCustomColumn;

            // restore column widths if previously saved (> 0)
            if (dgvEncodeQueue.Columns.Contains("colName"))
            {
                dgvEncodeQueue.Columns["colName"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                dgvEncodeQueue.Columns["colName"].Resizable = DataGridViewTriState.True;
            }
            if (_config.SizeColumnWidth > 0 && dgvEncodeQueue.Columns.Contains("colSize"))
                dgvEncodeQueue.Columns["colSize"].Width = _config.SizeColumnWidth;
            if (_config.CreatedColumnWidth > 0 && dgvEncodeQueue.Columns.Contains("colCreated"))
                dgvEncodeQueue.Columns["colCreated"].Width = _config.CreatedColumnWidth;
            if (dgvEncodeQueue.Columns.Contains("colEstimatedSize"))
                dgvEncodeQueue.Columns["colEstimatedSize"].Visible = true; // Ensure visible
            ApplyEncodeGridColumnLayout();
            ApplyRememberedEncodeQueueSort();

            // Populate input‐folder history:
            RefreshHistoryCombo(cmbInputFolder, _config.LastInputFolders);
            RefreshHistoryCombo(cmbEncodeOutput, _config.LastOutputFolders);

            // Populate Audio panel folder history (reuse same lists)
            RefreshHistoryCombo(cmbAudioInputFolder, _config.LastInputFolders);
            RefreshHistoryCombo(cmbAudioOutputFolder, _config.LastOutputFolders);

            // When user picks a previous audio input folder, scan it
            cmbAudioInputFolder.SelectedIndexChanged += (s, e) =>
            {
                var path = cmbAudioInputFolder.Text?.Trim();
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    ScanAndPopulateAudioGrid(path);
                    toolStripStatusLabel1.Text = $"Scanned \"{path}\" for audio";
                }
            };

            // when the user picks a previous folder, scan it immediately:
            cmbInputFolder.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressEncodeFolderSelectionScan)
                    return;

                ScanAndPopulateEncodeGrid(cmbInputFolder.Text);
            };

            // NEW: allow paste + Enter to scan typed path
            cmbInputFolder.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    var path = cmbInputFolder.Text?.Trim();
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        // keep behavior consistent with Browse (save to history)
                        AddToHistory(_config.LastInputFolders, path);
                        RefreshHistoryCombo(cmbInputFolder, _config.LastInputFolders);
                        _config.Save(_configPath);

                        ScanAndPopulateEncodeGrid(path);
                        toolStripStatusLabel1.Text = $"Scanned \"{path}\"";
                    }
                    else
                    {
                        toolStripStatusLabel1.Text = "Invalid folder path.";
                    }
                    e.Handled = true;
                    e.SuppressKeyPress = true; // prevent ding
                }
            };

            // restore delete‐source setting
            chkDeleteSource.Checked = _config.DeleteSourceAfterCompression;

            // restore filter settings
            chkFilterX264.Checked = _config.ShowX264Files;
            chkFilterX265.Checked = _config.ShowX265Files;
            chkFilterAv1.Checked = _config.ShowAv1Files;
            chkFilterOtherCodecs.Checked = _config.ShowOtherCodecFiles;
            chkFindDuplicates.Checked = _config.FindDuplicatesOnImport;
            chkOnlyDuplicateCandidates.Checked = _config.OnlyQueueDuplicateCandidates;
            chkAutoDisableDuplicateFinder.Checked = _config.AutoDisableDuplicateFinderAfterCleanup;
            chkOnlyDuplicateCandidates.Enabled = chkFindDuplicates.Checked;
            comboDuplicateScanMode.SelectedItem = DuplicateScanModes.Normalize(_config.DuplicateScanMode);
            comboDuplicateScanMode.Enabled = chkFindDuplicates.Checked;
            UpdateDuplicateFinderUiState();
            ResetCodecFilterCounts();

            const string codecFilterHelp = "Filters by the detected video codec. Supported file extensions are configured in Settings.";
            _uiToolTip.SetToolTip(chkFilterX264, codecFilterHelp);
            _uiToolTip.SetToolTip(chkFilterX265, codecFilterHelp);
            _uiToolTip.SetToolTip(chkFilterAv1, codecFilterHelp);
            _uiToolTip.SetToolTip(chkFilterOtherCodecs, codecFilterHelp);
            _uiToolTip.SetToolTip(chkFindDuplicates, "Checks all supported videos for duplicates before codec filtering and queue finalization.");
            _uiToolTip.SetToolTip(chkOnlyDuplicateCandidates, "Temporarily hides nonduplicate rows for review. Hidden rows remain in the encoding queue.");
            _uiToolTip.SetToolTip(chkAutoDisableDuplicateFinder, "After duplicate cleanup leaves no groups, prompts to turn Duplicate Finder off.");
            _uiToolTip.SetToolTip(comboDuplicateScanMode, "Exact duplicates uses file hashes only. Strict visual finds actionable visual matches. Review similar videos includes inspect-only weak matches.");
            _uiToolTip.SetToolTip(btnAnalyzeDuplicatesNow, "Analyze all supported videos in the selected Input Folder, ignoring codec show filters. Falls back to the current queue when no folder is selected.");
            _uiToolTip.SetToolTip(btnOpenDuplicateManager, "Open duplicate review and cleanup tools for the latest scan results.");
            _uiToolTip.SetToolTip(btnClearDuplicateResults, "Clear duplicate markings without removing files.");

            // when the user toggles, re‐save and re‐apply filter
            chkFilterX264.CheckedChanged += async (s, e) => {
                _config.ShowX264Files = chkFilterX264.Checked;
                _config.Save(_configPath);
                await ReapplyCodecFiltersAsync();
            };
            chkFilterX265.CheckedChanged += async (s, e) => {
                _config.ShowX265Files = chkFilterX265.Checked;
                _config.Save(_configPath);
                await ReapplyCodecFiltersAsync();
            };
            chkFilterAv1.CheckedChanged += async (s, e) => {
                _config.ShowAv1Files = chkFilterAv1.Checked;
                _config.Save(_configPath);
                await ReapplyCodecFiltersAsync();
            };
            chkFilterOtherCodecs.CheckedChanged += async (s, e) => {
                _config.ShowOtherCodecFiles = chkFilterOtherCodecs.Checked;
                _config.Save(_configPath);
                await ReapplyCodecFiltersAsync();
            };
            chkFindDuplicates.CheckedChanged += (s, e) => {
                _config.FindDuplicatesOnImport = chkFindDuplicates.Checked;
                chkOnlyDuplicateCandidates.Enabled = chkFindDuplicates.Checked;
                comboDuplicateScanMode.Enabled = chkFindDuplicates.Checked;
                _config.Save(_configPath);
                UpdateDuplicateFinderUiState();
                if (chkFindDuplicates.Checked)
                    StartDuplicateScanIfEnabled();
                else
                    ClearDuplicateAnnotations();
            };
            chkOnlyDuplicateCandidates.CheckedChanged += (s, e) => {
                _config.OnlyQueueDuplicateCandidates = chkOnlyDuplicateCandidates.Checked;
                _config.Save(_configPath);
                if (_lastDuplicateScanResult != null)
                    ApplyDuplicateCandidateViewFilter();
                else if (chkOnlyDuplicateCandidates.Checked && chkFindDuplicates.Checked)
                    StartDuplicateScanIfEnabled();
            };
            chkAutoDisableDuplicateFinder.CheckedChanged += (s, e) => {
                _config.AutoDisableDuplicateFinderAfterCleanup = chkAutoDisableDuplicateFinder.Checked;
                _config.Save(_configPath);
            };
            comboDuplicateScanMode.SelectedIndexChanged += (s, e) => {
                _config.DuplicateScanMode = DuplicateScanModes.Normalize(comboDuplicateScanMode.SelectedItem?.ToString());
                _config.Save(_configPath);
                UpdateDuplicateFinderUiState();
                if (chkFindDuplicates.Checked)
                    StartDuplicateScanIfEnabled();
            };

            chkProcessAll.Checked = _config.LastChkProcessAll;
            chkProcessAll.CheckedChanged += (s, e) => {
                _config.LastChkProcessAll = chkProcessAll.Checked;
                _config.Save(_configPath);
            };

            InitializeWatchFolderUi();

            LoadHistoryGrid();

        }

        private void RestoreMainWindowBounds()
        {
            if (_config.MainWindowWidth <= 0 || _config.MainWindowHeight <= 0)
                return;

            var savedBounds = new Rectangle(
                _config.MainWindowX,
                _config.MainWindowY,
                _config.MainWindowWidth,
                _config.MainWindowHeight);

            if (!IsUsableWindowBounds(savedBounds))
                return;

            StartPosition = FormStartPosition.Manual;
            Bounds = savedBounds;

            if (_config.MainWindowMaximized)
                WindowState = FormWindowState.Maximized;
        }

        private static bool IsUsableWindowBounds(Rectangle bounds)
        {
            if (bounds.Width < 700 || bounds.Height < 500)
                return false;

            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(bounds))
                    return true;
            }

            return false;
        }

        private void SaveMainWindowBounds()
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            _config.MainWindowX = bounds.X;
            _config.MainWindowY = bounds.Y;
            _config.MainWindowWidth = bounds.Width;
            _config.MainWindowHeight = bounds.Height;
            _config.MainWindowMaximized = WindowState == FormWindowState.Maximized;
        }



        private void ApplyRememberedCheckboxStates()
        {
            if (!_config.RememberCheckboxStates) return;

            _applyingCheckboxStates = true;
            try
            {
                foreach (var checkbox in GetAllCheckboxes(this))
                {
                    string key = GetCheckboxPersistenceKey(checkbox);
                    if (_config.CheckboxStates.TryGetValue(key, out bool isChecked))
                        checkbox.Checked = isChecked;
                }
            }
            finally
            {
                _applyingCheckboxStates = false;
            }

            SyncLegacyCheckboxConfigFields();
            txtTargetSize.Enabled = !chkAutoTargetSize.Checked;
        }

        private void WireCheckboxPersistence()
        {
            foreach (var checkbox in GetAllCheckboxes(this))
                checkbox.CheckedChanged += PersistCheckboxStatesIfEnabled;
        }

        private void PersistCheckboxStatesIfEnabled(object? sender, EventArgs e)
        {
            if (!_config.RememberCheckboxStates || _applyingCheckboxStates) return;

            if (sender is CheckBox checkbox)
            {
                string key = GetCheckboxPersistenceKey(checkbox);
                _config.CheckboxStates[key] = checkbox.Checked;
            }

            SyncLegacyCheckboxConfigFields();
            _config.Save(_configPath);
        }

        private void SyncLegacyCheckboxConfigFields()
        {
            _config.LastChkAutoTargetSize = chkAutoTargetSize.Checked;
            _config.LastChkDeleteSource = chkDeleteSource.Checked;
            _config.LastChkFilterX264 = chkFilterX264.Checked;
            _config.LastChkFilterX265 = chkFilterX265.Checked;
            _config.LastChkFilterAv1 = chkFilterAv1.Checked;
            _config.LastChkProcessAll = chkProcessAll.Checked;
        }

        private static IEnumerable<CheckBox> GetAllCheckboxes(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is CheckBox checkbox)
                    yield return checkbox;

                foreach (var nested in GetAllCheckboxes(child))
                    yield return nested;
            }
        }

        private static string GetCheckboxPersistenceKey(CheckBox checkbox)
        {
            var segments = new Stack<string>();
            Control? current = checkbox;

            while (current != null)
            {
                segments.Push(GetPersistentControlSegment(current));
                current = current.Parent;
            }

            return string.Join("/", segments);
        }

        private static string GetPersistentControlSegment(Control control)
        {
            if (!string.IsNullOrWhiteSpace(control.Name))
                return control.Name.Trim();

            int index = control.Parent?.Controls.IndexOf(control) ?? 0;
            return $"{control.GetType().Name}[{index}]";
        }

        private void DgvEncodeQueue_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
        {
            if (e.Column.Name == "colSize" || e.Column.Name == "colEstimatedSize")
            {
                double val1 = ParseSizeToMb(e.CellValue1?.ToString());
                double val2 = ParseSizeToMb(e.CellValue2?.ToString());

                e.SortResult = val1.CompareTo(val2);
                e.Handled = true; // we handled sorting
            }
        }

        #region Download Tab

        private void exitToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            this.Close();
        }

        private void clearFolderHistoryToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            // Clear both histories
            _config.LastInputFolders.Clear();
            _config.LastOutputFolders.Clear();

            // Persist the change
            _config.Save(_configPath);

            // Refresh the combo‐boxes
            RefreshHistoryCombo(cmbInputFolder, _config.LastInputFolders);
            RefreshHistoryCombo(cmbEncodeOutput, _config.LastOutputFolders);

            ShowStatusInfo("Cleared saved input/output folder history.");
        }

        private void SwitchToEncodeTab()
        {
            var tabs = this.Controls.OfType<TabControl>().FirstOrDefault();
            if (tabs == null) return;

            // Find the tab page that contains the encode grid
            foreach (TabPage tp in tabs.TabPages)
            {
                if (tp.Contains(dgvEncodeQueue))
                {
                    tabs.SelectedTab = tp;
                    break;
                }
            }
        }

        private void dgvEncodeQueue_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            // Guard: headers or invalid indexes
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var grid = sender as DataGridView;
            if (grid == null) return;
            if (e.ColumnIndex >= grid.Columns.Count) return;

            var col = grid.Columns[e.ColumnIndex];
            if (col == null || !string.Equals(col.Name, "colEstimatedSize", StringComparison.Ordinal))
                return;

            e.Handled = true;
            e.PaintBackground(e.ClipBounds, true);

            // Read stored values (may be null if not refreshed yet)
            var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            var tag = cell.Tag as Tuple<double, double>;
            double srcMb = tag?.Item1 ?? 0;
            double estMb = tag?.Item2 ?? 0;

            // Draw bar only if we have numbers
            if (srcMb > 0 && estMb > 0)
            {
                double ratio = Math.Min(1.0, Math.Max(0.0, estMb / srcMb));

                var rect = e.CellBounds;
                rect.Inflate(-4, -6); // padding

                using (var bg = new SolidBrush(SystemColors.ControlLight))
                    e.Graphics!.FillRectangle(bg, rect);

                var fill = new Rectangle(rect.X, rect.Y, (int)(rect.Width * (1.0 - ratio)), rect.Height);
                using (var fg = new SolidBrush(Color.Orange))
                    e.Graphics!.FillRectangle(fg, fill);

                using (var pen = new Pen(SystemColors.ControlDark))
                    e.Graphics!.DrawRectangle(pen, rect);
            }

            // Draw text last
            var text = e.FormattedValue?.ToString() ?? string.Empty;
            var font = e.CellStyle?.Font ?? this.Font;
            var fore = e.CellStyle?.ForeColor ?? this.ForeColor;

            TextRenderer.DrawText(
                e.Graphics!, text, font, e.CellBounds, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left
            );

            e.Paint(e.CellBounds, DataGridViewPaintParts.Focus);
        }



        #endregion

        #region Encode Tab

        private async void btnBrowseInput_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = string.IsNullOrWhiteSpace(cmbInputFolder.Text)
                    ? Application.StartupPath
                    : cmbInputFolder.Text
            };
            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            // Grab the folder the user picked
            string picked = dlg.SelectedPath;
            _suppressEncodeFolderSelectionScan = true;
            try
            {
                cmbInputFolder.Text = picked;

            // ─── History maintenance ─────────────────────────────
                AddToHistory(_config.LastInputFolders, picked);
                RefreshHistoryCombo(cmbInputFolder, _config.LastInputFolders);
                _config.Save(_configPath);
            }
            finally
            {
                _suppressEncodeFolderSelectionScan = false;
            }

            // ─── Status update + scan ────────────────────────────
            toolStripStatusLabel1.Text = "Preparing to scan…";
            await ImportEncodePathsAsync(
                new[] { picked },
                chkIncludeSubfolders.Checked,
                applyCodecFilters: true,
                replaceExisting: !_encodingActive);
        }

        private void btnClearInput_Click(object? sender, EventArgs e)
        {
            ClearEncodeInputFolder();
            ShowStatusInfo("Input Folder cleared.");
        }

        private void btnBrowseOutputEncode_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = cmbEncodeOutput.Text
            };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                string picked = dlg.SelectedPath;
                if (!ValidateOutputFolderAgainstWatchFolder(picked, showMessage: true))
                    return;

                cmbEncodeOutput.Text = picked;

                AddToHistory(_config.LastOutputFolders, picked);
                RefreshHistoryCombo(cmbEncodeOutput, _config.LastOutputFolders);
                _config.Save(_configPath);
            }
        }

        private void btnClearOutputEncode_Click(object? sender, EventArgs e)
        {
            cmbEncodeOutput.SelectedIndex = -1;
            cmbEncodeOutput.Text = string.Empty;
            ShowStatusInfo("Output Folder cleared.");
        }

        private void HandleFfmpegProgressLineForRow(
            DataGridViewRow row,
            StringBuilder jobLog,
            double durationSec,
            string line)
        {
            if (row == null || line == null)
                return;

            // Parse timestamps, fps, bitrate using your existing logic:
            Ui(() => UpdateEncodeProgressFromLine_ForRow(row, durationSec, line));
            HandleFfmpegProgressLineForRowMetrics(row, line);

            // You keep jobLog updated above.
        }

        private void UpdateEncodeProgressFromLine_ForRow(
    DataGridViewRow row,
    double durationSec,
    string line)
        {
            if (row == null || row.DataGridView != dgvEncodeQueue || durationSec <= 0 || string.IsNullOrEmpty(line))
                return;

            // Find "time=HH:MM:SS.xx"
            var tIdx = line.IndexOf("time=", StringComparison.OrdinalIgnoreCase);
            if (tIdx < 0)
                return;

            var seg = line.Substring(tIdx + 5);
            var space = seg.IndexOf(' ');
            if (space >= 0)
                seg = seg.Substring(0, space);

            // Convert ffmpeg time to seconds
            double cur = ParseFfmpegTimeToSeconds(seg);
            if (cur < 0) cur = 0;
            if (cur > durationSec) cur = durationSec;

            double pct = durationSec > 0 ? (cur / durationSec) : 0;

            // Parse "speed=1.23x" from the same line
            double speedX = ParseSpeedX(line);

            // Compute ETA – account for speed if we have it
            double remaining = Math.Max(0, durationSec - cur);
            TimeSpan eta = speedX > 0
                ? TimeSpan.FromSeconds(remaining / speedX)
                : TimeSpan.FromSeconds(remaining);

            // Push into the grid
            row.Cells["colProgress"].Value = $"{pct * 100:0}%";
            row.Cells["colETA"].Value = eta.ToString(@"hh\:mm\:ss");

            // Color-code ETA cell based on speed
            SetEtaCellColor(row, speedX);
        }

        private void AddToHistory(List<string> history, string path)
        {
            history.Remove(path);
            history.Insert(0, path);
            if (history.Count > _config.FolderHistoryLimit)
                history.RemoveRange(_config.FolderHistoryLimit,
                                    history.Count - _config.FolderHistoryLimit);
        }

        private void RefreshHistoryCombo(ComboBox combo, List<string> history)
        {
            combo.Items.Clear();
            combo.Items.AddRange(history.ToArray());
        }


        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                _compactModeForm?.CloseForApplicationExit();
                SaveMainWindowBounds();
                SaveEncodeDropdownPreferences();

                if (_config.RememberCheckboxStates)
                {
                    foreach (var checkbox in GetAllCheckboxes(this))
                        _config.CheckboxStates[GetCheckboxPersistenceKey(checkbox)] = checkbox.Checked;

                    SyncLegacyCheckboxConfigFields();
                }

                _config.Save(_configPath);

                StopEstimateUiPump();
                _mediaInfoService?.FlushCache();

                lock (_monLock)
                {
                    _monitorTimer?.Dispose();
                    _monitorTimer = null;
                    _monitoring = false;
                }

                _watchCountdownTimer?.Stop();
                _watchCountdownTimer?.Dispose();
                _watchCountdownTimer = null;
            }
            catch
            {
                /* ignore during shutdown */
            }

            base.OnFormClosing(e);
        }

        private void InitializeCompactModeControls()
        {
            var compactMenuItem = new ToolStripMenuItem("Compact Mode");
            compactMenuItem.ShortcutKeys = Keys.Control | Keys.M;
            compactMenuItem.Click += (_, __) => EnterCompactMode();
            toolsToolStripMenuItem.DropDownItems.Insert(0, compactMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());

            _btnCompactMode = new Button
            {
                Name = "btnCompactMode",
                Text = "Compact",
                AutoSize = true,
                Margin = new Padding(3, 0, 0, 0)
            };
            _btnCompactMode.Click += (_, __) => EnterCompactMode();

            // Share the existing Refresh cell so the main layout does not grow taller.
            tlEncode.Controls.Remove(btnRefreshEncode);
            var buttonPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            btnRefreshEncode.AutoSize = true;
            btnRefreshEncode.Margin = Padding.Empty;
            buttonPanel.Controls.Add(btnRefreshEncode);
            buttonPanel.Controls.Add(_btnCompactMode);
            tlEncode.Controls.Add(buttonPanel, 3, 8);
        }

        private void EnterCompactMode()
        {
            if (_compactModeForm == null || _compactModeForm.IsDisposed)
                _compactModeForm = new CompactModeForm(this, _config.CompactWindowAlwaysOnTop);

            if (_config.CompactWindowX != int.MinValue && _config.CompactWindowY != int.MinValue)
            {
                var savedLocation = new Point(_config.CompactWindowX, _config.CompactWindowY);
                var savedBounds = new Rectangle(savedLocation, _compactModeForm.Size);
                if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(savedBounds)))
                    _compactModeForm.Location = savedLocation;
            }

            _compactModeForm.Show();
            _compactModeForm.BringToFront();
            Hide();
        }

        internal void RestoreFromCompactMode()
        {
            if (IsDisposed)
                return;

            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        }

        internal void SaveCompactWindowPreferences(Point location, bool alwaysOnTop)
        {
            _config.CompactWindowX = location.X;
            _config.CompactWindowY = location.Y;
            _config.CompactWindowAlwaysOnTop = alwaysOnTop;
            _config.Save(_configPath);
        }

        internal void ToggleCompactQueuePause()
        {
            if (!_encodingActive && _runningEncodeJobs.IsEmpty)
            {
                toolStripStatusLabel1.Text = "No encode is currently running to pause.";
                return;
            }

            _encodeQueuePaused = !_encodeQueuePaused;
            btnPauseQueue.Text = _encodeQueuePaused ? "Resume Queue" : "Pause Queue";
            toolStripStatusLabel1.Text = _encodeQueuePaused
                ? "Encode queue paused."
                : "Encode queue resumed.";
        }

        internal void StopEncodingFromCompactMode() => btnStopEncode_Click(btnStopEncode, EventArgs.Empty);

        internal CompactQueueSnapshot GetCompactQueueSnapshot()
        {
            var gridRows = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .ToList();

            List<DataGridViewRow> queuedRows;
            lock (_activeEncodeQueueLock)
            {
                queuedRows = _activeEncodeQueue?
                    .Where(row => row != null && !row.IsNewRow)
                    .ToList() ?? new List<DataGridViewRow>();
            }

            var rows = gridRows
                .Concat(queuedRows)
                .Distinct()
                .ToList();
            var runningJobs = _runningEncodeJobs.ToArray();
            var activeRows = runningJobs.Select(job => job.Key)
                .Concat(_activeEncodeRows)
                .Where(row => row.DataGridView == dgvEncodeQueue)
                .Distinct()
                .OrderBy(row => row.Index)
                .ToList();

            var current = activeRows.FirstOrDefault()
                ?? runningJobs.Select(job => job.Key).FirstOrDefault()
                ?? _activeEncodeRow;
            string fileName = "Encode queue";
            int progress = 0;
            string eta = "--";

            if (current != null)
            {
                string? path = runningJobs
                    .Where(job => ReferenceEquals(job.Key, current))
                    .Select(job => job.Value)
                    .FirstOrDefault();
                path ??= current.Tag is RowMeta meta ? meta.Path : current.Tag as string;
                fileName = !string.IsNullOrWhiteSpace(path)
                    ? Path.GetFileName(path)
                    : current.DataGridView == dgvEncodeQueue
                        ? current.Cells["colName"].Value?.ToString() ?? fileName
                        : fileName;
                if (current.DataGridView == dgvEncodeQueue)
                {
                    string progressText = current.Cells["colProgress"].Value?.ToString() ?? "";
                    int.TryParse(progressText.Trim().TrimEnd('%'), out progress);
                    eta = current.Cells["colETA"].Value?.ToString() ?? "--";
                }
            }

            bool IsTerminal(DataGridViewRow row)
            {
                // Completed watched rows can remain in the runner's queue after
                // RemoveRowAndCleanup detaches them from the visible grid. Named
                // cell lookup is invalid once a row has no owning DataGridView.
                if (row.DataGridView != dgvEncodeQueue)
                    return true;

                string status = row.Cells["colStatus"].Value?.ToString() ?? "";
                return status.Equals("Done", StringComparison.OrdinalIgnoreCase) ||
                       status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                       status.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                       status.Equals("Excluded - exact duplicate", StringComparison.OrdinalIgnoreCase);
            }

            int runningCount = Math.Max(activeRows.Count, runningJobs.Length);
            int remaining = rows.Count(row => !IsTerminal(row) && !runningJobs.Any(job => ReferenceEquals(job.Key, row)));
            bool isEncoding = _encodingActive || runningCount > 0;
            string state = _encodeQueuePaused ? "Paused" : isEncoding ? "Encoding" : "Ready";
            return new CompactQueueSnapshot(
                fileName,
                Math.Clamp(progress, 0, 100),
                runningCount,
                remaining,
                eta,
                state,
                isEncoding,
                _encodeQueuePaused);
        }

        // Rescan the current input folder and MERGE results into the grid:
        // - Adds new files that match current filters
        // - Removes rows for files that no longer exist on disk
        // - Optionally triggers estimate recompute
        private void RescanInputFolderAndMerge(bool recomputeEstimates)
        {
            var folder = cmbInputFolder.Text;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                ResetCodecFilterCounts();
                return;
            }

            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            try
            {
                var allowedExts = GetAllowedExts();
                var searchOpt = chkIncludeSubfolders.Checked
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                int h264Count = 0;
                int h265Count = 0;
                int av1Count = 0;
                int otherCount = 0;
                var fsFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool allowH264 = chkFilterX264.Checked;
                bool allowHevc = chkFilterX265.Checked;
                bool allowAv1 = chkFilterAv1.Checked;
                bool allowOther = chkFilterOtherCodecs.Checked;

                foreach (var file in Directory.EnumerateFiles(folder, "*.*", searchOpt))
                {
                    if (IsCompletedEncodePath(file))
                        continue;

                    string ext = Path.GetExtension(file);
                    if (string.IsNullOrEmpty(ext) || !allowedExts.Contains(ext))
                        continue;

                    var codec = GetVideoCodec(file);
                    if (IsH264Codec(codec))
                        h264Count++;
                    else if (IsH265Codec(codec))
                        h265Count++;
                    else if (IsAv1Codec(codec))
                        av1Count++;
                    else
                        otherCount++;

                    if (PassesCodecFilter(codec, allowH264, allowHevc, allowAv1, allowOther))
                        fsFiles.Add(file);
                }

                UpdateCodecFilterCounts(h264Count, h265Count, av1Count, otherCount);

                // Add any new files that aren't already in the grid
                foreach (var path in fsFiles)
                    AddEncodeItemIfNotPresent(path);

                // Remove rows for files that no longer match / no longer exist
                foreach (DataGridViewRow row in dgvEncodeQueue.Rows.Cast<DataGridViewRow>().ToList())
                {
                    var path = GetPathFromRow(row);
                    if (string.IsNullOrWhiteSpace(path) || !fsFiles.Contains(path))
                        RemoveRowAndCleanup(row);
                }

                if (recomputeEstimates)
                    SafeRefreshEstimates();
            }
            finally
            {
                _activityIndicator?.StopActivity(UiActivity.FolderScan);
                ApplyRememberedEncodeQueueSort();
            }
        }

        // Remove a row and keep internal maps tidy
        private void RemoveRowAndCleanup(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow)
                return;

            var path = GetPathFromRow(row);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _rowsByPath.TryRemove(path, out _);
                _estimatedSizeMap.Remove(path);
                _queueSourceSizeMap.Remove(path);
            }

            if (row.DataGridView == dgvEncodeQueue)
            {
                _suppressRowEvents = true;
                try
                {
                    dgvEncodeQueue.Rows.Remove(row);
                }
                finally
                {
                    _suppressRowEvents = false;
                }
            }

            if (ReferenceEquals(_activeEncodeRow, row))
                _activeEncodeRow = null;

            if (_activeEncodeRows.Contains(row))
                EndEncodeMetricsForRow(row);

            if (!IsDisposed && IsHandleCreated)
            {
                MarkQueueTotalsDirty();
                SafeRefreshEstimates();
                UpdateSizeTotals(force: true);
                UpdateAnalyzeQueueButtonState();
            }
        }

        private void ClearEncodeInputFolderIfQueueEmptyAfterProcessing()
        {
            if (Volatile.Read(ref _pendingEncodeImports) > 0 ||
                string.IsNullOrWhiteSpace(cmbInputFolder.Text))
            {
                return;
            }

            // Successful rows are removed. Failed/canceled rows intentionally
            // remain for inspection, but they are still completed jobs and should
            // not keep the source-folder field populated. Any other state means
            // work is still queued or active.
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                string status = dgvEncodeQueue.Columns.Contains("colStatus")
                    ? row.Cells["colStatus"].Value?.ToString() ?? string.Empty
                    : string.Empty;

                bool terminal = status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                                status.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                                status.Equals("Done", StringComparison.OrdinalIgnoreCase);
                if (!terminal)
                    return;
            }

            ClearEncodeInputFolder();
        }

        private void ClearEncodeInputFolder()
        {
            _suppressEncodeFolderSelectionScan = true;
            try
            {
                cmbInputFolder.SelectedIndex = -1;
                cmbInputFolder.Text = string.Empty;
            }
            finally
            {
                _suppressEncodeFolderSelectionScan = false;
            }
        }

        private void DgvEncodeQueue_Sorted(object? sender, EventArgs e)
        {
            if (_applyingRememberedSort || dgvEncodeQueue.SortedColumn == null)
                return;

            _config.EncodeQueueSortColumn = dgvEncodeQueue.SortedColumn.Name;
            _config.EncodeQueueSortDescending = dgvEncodeQueue.SortOrder == SortOrder.Descending;
            _config.Save(_configPath);
        }

        private void ApplyRememberedEncodeQueueSort()
        {
            if (string.IsNullOrWhiteSpace(_config.EncodeQueueSortColumn) ||
                !dgvEncodeQueue.Columns.Contains(_config.EncodeQueueSortColumn))
                return;

            var column = dgvEncodeQueue.Columns[_config.EncodeQueueSortColumn];
            if (column.SortMode == DataGridViewColumnSortMode.NotSortable)
                return;

            _applyingRememberedSort = true;
            try
            {
                dgvEncodeQueue.Sort(
                    column,
                    _config.EncodeQueueSortDescending
                        ? System.ComponentModel.ListSortDirection.Descending
                        : System.ComponentModel.ListSortDirection.Ascending);
            }
            finally
            {
                _applyingRememberedSort = false;
            }
        }

        private void ReapplyCurrentEncodeQueueSort()
        {
            if (dgvEncodeQueue.SortedColumn == null)
                return;

            var column = dgvEncodeQueue.SortedColumn;
            if (column.SortMode == DataGridViewColumnSortMode.NotSortable)
                return;

            var direction = dgvEncodeQueue.SortOrder == SortOrder.Descending
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending;

            _applyingRememberedSort = true;
            try
            {
                dgvEncodeQueue.Sort(column, direction);
            }
            finally
            {
                _applyingRememberedSort = false;
            }
        }

        private IEnumerable<DataGridViewRow> GetEncodeRowsInVisualOrder()
        {
            if (dgvEncodeQueue.Rows.Count == 0)
                yield break;

            // Rows hidden by "Show only duplicate candidates" remain part of the
            // queue. DataGridView keeps the collection in the active sort order,
            // so enumerate it directly instead of treating visibility as eligibility.
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (!row.IsNewRow)
                    yield return row;
            }
        }

        private void UpdateSelectionSizeTotals()
        {
            if (_summarySelectedCountValue != null)
                _summarySelectedCountValue.Text = dgvEncodeQueue.SelectedRows.Count.ToString();

            // No rows or no selection
            if (dgvEncodeQueue.Rows.Count == 0 || dgvEncodeQueue.SelectedRows.Count == 0)
            {
                if (_summarySelectedSavedValue != null)
                    _summarySelectedSavedValue.Text = "--";
                return;
            }

            double srcTotalMb = 0;
            double estTotalMb = 0;
            int counted = 0;

            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow) continue;

                // Get path and source size
                string? path = GetFullPathFromRow(row); // you already have this helper
                double srcMb = 0;

                if (row.Tag is RowMeta rm && rm.SrcMb > 0)
                {
                    srcMb = rm.SrcMb;
                }
                else if (!string.IsNullOrWhiteSpace(path))
                {
                    // Fallback – in case RowMeta wasn’t populated for some reason
                    srcMb = GetMbOnDisk(path);
                }

                if (srcMb <= 0)
                    continue;

                // Get estimated size for this file (from the estimator map)
                double estMb = 0;
                if (!string.IsNullOrWhiteSpace(path) &&
                    _estimatedSizeMap.TryGetValue(path, out var est) &&
                    est > 0)
                {
                    estMb = est;
                }

                // Skip rows where we don’t yet have an estimate
                if (estMb <= 0)
                    continue;

                srcTotalMb += srcMb;
                estTotalMb += estMb;
                counted++;
            }

            if (counted == 0)
            {
                if (_summarySelectedSavedValue != null)
                    _summarySelectedSavedValue.Text = "Waiting for estimates";
                return;
            }

            double savedMb = Math.Max(0, srcTotalMb - estTotalMb);
            double savedPct = srcTotalMb > 0 ? (savedMb / srcTotalMb) * 100.0 : 0.0;

            var selectedSummary = $"{FormatSize(savedMb)} ({savedPct:0}% saved)";
            if (_summarySelectedSavedValue != null)
                _summarySelectedSavedValue.Text = selectedSummary;
        }

        private void RefreshEncodeGrid()
        {
            var folder = cmbInputFolder.Text;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                ResetCodecFilterCounts();
                return;
            }

            _ = ImportEncodePathsAsync(
                new[] { folder },
                chkIncludeSubfolders.Checked,
                applyCodecFilters: true,
                replaceExisting: !_encodingActive);
        }

        private void AddFileToGrid(string path)
        {
            AddEncodeItemIfNotPresent(path);
        }

        private void btnRefreshEncode_Click(object? sender, EventArgs e)
        {
            RefreshEncodeGrid();
        }

        // Map NVENC preset combo → "p1" .. "p7"
        private string GetSelectedNvencPreset()
        {
            // Default
            string preset = "p5";

            var txt = comboNvencPreset?.SelectedItem?.ToString()
                      ?? comboNvencPreset?.Text;

            if (string.IsNullOrWhiteSpace(txt))
                return preset;

            txt = txt.Trim();

            if (txt.StartsWith("p1", StringComparison.OrdinalIgnoreCase)) return "p1";
            if (txt.StartsWith("p2", StringComparison.OrdinalIgnoreCase)) return "p2";
            if (txt.StartsWith("p3", StringComparison.OrdinalIgnoreCase)) return "p3";
            if (txt.StartsWith("p4", StringComparison.OrdinalIgnoreCase)) return "p4";
            if (txt.StartsWith("p5", StringComparison.OrdinalIgnoreCase)) return "p5";
            if (txt.StartsWith("p6", StringComparison.OrdinalIgnoreCase)) return "p6";
            if (txt.StartsWith("p7", StringComparison.OrdinalIgnoreCase)) return "p7";

            return preset;
        }
        private int GetMaxConcurrentEncodes()
        {
            // Only ever use >1 when GPU NVENC is active.
            string encoderText = comboEncoderMode.SelectedItem?.ToString() ?? string.Empty;
            bool useNvenc = IsNvencSelected(encoderText);

            if (!useNvenc)
                return 1;

            if (_config.LimitGpuEncodingQueueToOneJob)
                return 1;

            return GetAutomaticNvencConcurrencyLimit();
        }

        private static int GetAutomaticNvencConcurrencyLimit()
        {
            return 2;
        }

        private bool GetTenBitRequested()
        {
            return chkTenBit?.Checked == true;
        }

        private void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch { }
        }

        private void ColumnSettingsToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            using var dlg = new Form
            {
                Text = "Show / Hide Columns",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12)
            };

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 6,
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            var chkName = new CheckBox { Text = "Name", Checked = true, Enabled = false, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            var chkSize = new CheckBox { Text = "Size", Checked = _config.ShowSizeColumn, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            var chkCreated = new CheckBox { Text = "Created", Checked = _config.ShowCreatedColumn, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            var chkCustom = new CheckBox { Text = "Custom", Checked = _config.ShowCustomColumn, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            var btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80, Anchor = AnchorStyles.Left };

            layout.Controls.Add(chkName, 0, 0);
            layout.Controls.Add(chkSize, 0, 1);
            layout.Controls.Add(chkCreated, 0, 2);
            layout.Controls.Add(chkCustom, 0, 3);
            layout.Controls.Add(btnOK, 0, 4);
            dlg.Controls.Add(layout);
            dlg.AcceptButton = btnOK;

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                dgvEncodeQueue.Columns["colName"].Visible = chkName.Checked;
                dgvEncodeQueue.Columns["colSize"].Visible = chkSize.Checked;
                dgvEncodeQueue.Columns["colCreated"].Visible = chkCreated.Checked;
                dgvEncodeQueue.Columns["colCustom"].Visible = chkCustom.Checked;

                _config.ShowSizeColumn = chkSize.Checked;
                _config.ShowCreatedColumn = chkCreated.Checked;
                _config.ShowCustomColumn = chkCustom.Checked;
                _config.Save(_configPath);
                ApplyEncodeGridColumnLayout();
            }
        }

        private void ApplyEncodeGridColumnLayout()
        {
            if (dgvEncodeQueue == null)
                return;

            if (dgvEncodeQueue.Columns.Contains("colName"))
            {
                var col = dgvEncodeQueue.Columns["colName"];
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                col.MinimumWidth = 180;
                col.FillWeight = 100;
            }

            SetFixedGridColumn("colSize", 92);
            SetFixedGridColumn("colEstimatedSize", 150);
            SetFixedGridColumn("colStatus", 86);
            SetFixedGridColumn("colProgress", 78);
            SetFixedGridColumn("colETA", 76);
            SetFixedGridColumn("colCustom", 72);
        }

        private void SetFixedGridColumn(string name, int width)
        {
            if (!dgvEncodeQueue.Columns.Contains(name))
                return;

            var col = dgvEncodeQueue.Columns[name];
            if (!col.Visible)
                return;

            col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            col.MinimumWidth = Math.Min(width, 60);
            if (col.Width < col.MinimumWidth)
                col.Width = width;
        }


        private void UpdateEncodeProgressFromLine(string line)
        {
            if (_activeEncodeRow == null) return;
            if (_activeEncodeRow.DataGridView != dgvEncodeQueue) return;
            if (!TryGetRowPathAndDuration(_activeEncodeRow, out _, out var durationSec)) return;
            if (durationSec <= 0) return;

            // find "time=HH:MM:SS.xx"
            var tIdx = line.IndexOf("time=", StringComparison.OrdinalIgnoreCase);
            if (tIdx < 0) return;
            var seg = line.Substring(tIdx + 5);
            var space = seg.IndexOf(' ');
            if (space >= 0) seg = seg.Substring(0, space);

            double cur = ParseFfmpegTimeToSeconds(seg);
            if (cur < 0) cur = 0;
            if (cur > durationSec) cur = durationSec;

            double pct = durationSec > 0 ? (cur / durationSec) : 0;
            double speedX = ParseSpeedX(line);
            double remaining = Math.Max(0, durationSec - cur);
            TimeSpan eta = speedX > 0
                ? TimeSpan.FromSeconds(remaining / speedX)
                : TimeSpan.FromSeconds(remaining);

            _activeEncodeRow.Cells["colProgress"].Value = $"{pct * 100:0}%";
            _activeEncodeRow.Cells["colETA"].Value = eta.ToString("hh\\:mm\\:ss");
            SetEtaCellColor(_activeEncodeRow, speedX);
        }

        // Thread-safe label update
        private void SetLabel(Label label, string text)
        {
            if (label.InvokeRequired)
                label.Invoke(new Action(() => label.Text = text));
            else
                label.Text = text;
        }

        // Thread-safe progress update
        private void SetProgress(ProgressBar bar, int value)
        {
            if (bar.InvokeRequired)
                bar.Invoke(new Action(() => bar.Value = Math.Max(bar.Minimum, Math.Min(bar.Maximum, value))));
            else
                bar.Value = Math.Max(bar.Minimum, Math.Min(bar.Maximum, value));
        }

        private TimeSpan GetVideoDuration(string file)
        {
            return _mediaInfoService.GetDuration(file);
        }

        private int? ProbeSourceVideoBitrateKbps(string file)
        {
            return _mediaInfoService.GetBitrateKbps(file);
        }

        // JOB TIMER METHODS
        private void JobTimer_Tick(object? sender, EventArgs e)

        {
            if (jobStopwatch.IsRunning)
            {
                TimeSpan elapsed = jobStopwatch.Elapsed;
                SetLabel(lblJobTimer, elapsed.ToString(@"hh\:mm\:ss"));
            }
        }

        private void StartJobTimer()
        {
            jobStopwatch.Restart();
            jobTimer.Start();
            SetLabel(lblJobTimer, "00:00:00");
        }

        private void StopJobTimer()
        {
            jobStopwatch.Stop();
            jobTimer.Stop();
        }

        private void ResetJobTimer()
        {
            SetLabel(lblJobTimer, "--:--:--");
        }

        #endregion

        #region Settings & Update

        private void RecreateMediaServices()
        {
            _estimateService?.Dispose();
            _duplicateScanCts?.Cancel();
            _mediaInfoService?.FlushCache();

            _encodingService = new EncodingService(
                AppPaths.InstallDirectory,
                HandleFfmpegProgressLine,
                null,
                _config.FfmpegPath,
                _config.FfprobePath);

            _audioService = new AudioService(
                AppPaths.InstallDirectory,
                HandleFfmpegProgressLine,
                _config.FfmpegPath);

            _mediaInfoService = new MediaInfoService(
                AppPaths.InstallDirectory,
                _config.FfprobePath,
                _config.EnablePersistentMediaInfoCache,
                AppPaths.DataDirectory);

            _sizeEstimateService = new SizeEstimateService(_mediaInfoService);
            _estimateService = new EstimateBackgroundService(_sizeEstimateService, _mediaInfoService);
            _duplicateDetectionService = new DuplicateDetectionService(
                _mediaInfoService,
                AppPaths.InstallDirectory,
                _config.FfmpegPath,
                _config.EnableDuplicateSignatureCache,
                AppPaths.DataDirectory);
        }

        private void SettingsToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            using var dlg = new SettingsForm(
                _config,
                _supportedVideoExtsPath,
                DefaultVideoExts,
                cmbEncodeOutput.Text);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _config = dlg.Config;
                _config.Save(_configPath);
                RecreateMediaServices();
                ApplyDuplicateConfigurationToUi();
                UpdateDuplicateReferenceFolderUi();
                RescoreDuplicateKeeperRecommendations();

                RefreshEncodeGrid();
                ApplyWatchFolderConfiguration();
            }
        }

        private async void CheckForUpdatesToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (_encodingActive ||
                _pendingEncodeImports > 0 ||
                (_importCts != null && !_importCts.IsCancellationRequested) ||
                (_duplicateScanCts != null && !_duplicateScanCts.IsCancellationRequested))
            {
                MessageBox.Show(
                    this,
                    "Finish or cancel the active MediaFlux work before installing an update.",
                    "MediaFlux is busy",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SaveMainWindowBounds();
            SaveEncodeDropdownPreferences();
            if (_config.RememberCheckboxStates)
            {
                foreach (var checkbox in GetAllCheckboxes(this))
                    _config.CheckboxStates[GetCheckboxPersistenceKey(checkbox)] = checkbox.Checked;

                SyncLegacyCheckboxConfigFields();
            }
            _config.Save(_configPath);

            checkForUpdatesToolStripMenuItem.Enabled = false;
            try
            {
                await UpdateManager.CheckAndPromptAsync(
                    this,
                    _config.AutomaticallyBackupBeforeUpdates,
                    _config.BackupFolderPath,
                    _config.BackupsToKeep,
                    reportStatus: ShowUpdateStatus);
            }
            finally
            {
                if (!IsDisposed)
                    checkForUpdatesToolStripMenuItem.Enabled = true;
            }
        }

        private void ShowUpdateStatus(string status)
        {
            toolStripStatusLabel1.Text = status;
            UpdateRelocatedEncodeStatus(status);

            // Backup and update staging are synchronous. Force the status surfaces
            // to paint before those phases begin so the user sees each transition.
            statusStrip1.Refresh();
            _summaryQueueStatusValue?.Refresh();
        }

        // If other code only needs the full path:
        private string? GetFullPathFromRow(DataGridViewRow row)
        {
            if (row.Tag is RowMeta rm) return rm.Path;
            if (row.Tag is string s) return s;
            return null;
        }

        #endregion

        private void ModeComboBox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var mode = modeComboBox.SelectedItem?.ToString();

            panelEncode.Visible = (mode == "Encode");
            panelAudio.Visible = (mode == "Audio");
            panelMonitor.Visible = (mode == "Monitor");

            if (mode == "Monitor")
            {
                // Show the same video encode queue in Monitor
                MoveEncodeGridTo(panelMonQueueHost);
            }
            else
            {
                // Ensure encode grid is back on the Encode panel when not in Monitor
                RestoreEncodeGridToOriginalParent();
            }
        }

        private void DgvEncodeQueue_ColumnWidthChanged(object? sender, DataGridViewColumnEventArgs e)
        {
            switch (e.Column.Name)
            {
                case "colName":
                    // Name fills remaining space so the fixed columns stay visible.
                    // Do not persist its calculated width, which can become enormous after resizing.
                    break;
                case "colSize":
                    _config.SizeColumnWidth = e.Column.Width;
                    break;
                case "colCreated":
                    _config.CreatedColumnWidth = e.Column.Width;
                    break;
            }
            _config.Save(_configPath);
        }

        private void RemoveSelectedRows_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0) return;

            // If you maintain a separate encode queue/list, remove from it here too.
            foreach (DataGridViewRow r in dgvEncodeQueue.SelectedRows)
            {
                dgvEncodeQueue.Rows.Remove(r);
            }
            UpdateSizeTotals();
            UpdateSelectionSizeTotals();
        }

        private void ClearGrid_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.Rows.Count == 0) return;
            if (_encodingActive)
            {
                ShowStatusInfo("Stop the active encode before clearing the grid.");
                return;
            }

            var confirm = MessageBox.Show(
                "Clear all items from the encode queue?",
                "Confirm Clear",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm == DialogResult.Yes)
            {
                _suppressRowEvents = true;
                try
                {
                    dgvEncodeQueue.Rows.Clear();
                }
                finally
                {
                    _suppressRowEvents = false;
                }

                _rowsByPath.Clear();
                _codecFilterImportRoots.Clear();
                _estimatedSizeMap.Clear();
                _queueSourceSizeMap.Clear();
                _queueTotalSourceMb = 0;
                _queueTotalEstimatedMb = 0;
                _queueFileCount = 0;
                _queueTotalsDirty = false;
                ClearCompletedEncodePaths();
                ResetCodecFilterCounts();
                ClearDuplicateAnnotations();
                ResetEncodeMetrics();
                ClearEncodeInputFolder();
                UpdateAnalyzeQueueButtonState();
                UpdateSizeTotals(force: true);
                UpdateSelectionSizeTotals();
                ShowStatusInfo("Encode queue and Input Folder cleared.");
            }
        }

        private void RenameFile_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count != 1)
            {
                ShowStatusInfo("Select exactly one file to rename.");
                return;
            }

            var row = dgvEncodeQueue.SelectedRows[0];
            var fullPath = GetFullPathFromRow(row);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                ShowStatusInfo("Original file was not found on disk.");
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Title = "Rename File",
                InitialDirectory = Path.GetDirectoryName(fullPath),
                FileName = Path.GetFileName(fullPath),
                Filter = "Video Files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv|All Files|*.*",
                OverwritePrompt = true
            };

            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            var newPath = sfd.FileName;
            try
            {
                if (!string.Equals(fullPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Move/rename on disk
                    File.Move(fullPath, newPath, overwrite: true);

                    // Update row display + tag
                    row.Tag = newPath;
                    row.Cells["colName"].Value = Path.GetFileName(newPath);

                    // Update Size/Created, too
                    var fi = new FileInfo(newPath);
                    row.Cells["colSize"].Value = FormatSize(fi.Length);
                    row.Cells["colCreated"].Value = fi.CreationTime.ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Rename failed: {ex.Message}", "Rename File",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Context menu → Start Encode
        private void StartEncodeFromContextMenu_Click(object? sender, EventArgs e)
        {
            // Just behave exactly like pressing the Start button.
            // Let btnStartEncode_Click handle _encodingActive, status, etc.
            btnStartEncode_Click(btnStartEncode, EventArgs.Empty);
        }

        // Delete key context menu handler
        private void dgvEncodeQueue_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && dgvEncodeQueue.SelectedRows.Count > 0)
            {
                RemoveSelectedRows_Click(null, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void scheduleEncodeStartToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            using var dlg = new ScheduleForm();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _encodeScheduledUtc = dlg.ScheduledUtc;
            cancelScheduledStartToolStripMenuItem.Enabled = true;

            var local = _encodeScheduledUtc.Value.ToLocalTime();
            toolStripStatusLabel1.Text = $"Encode scheduled for {local:g}";
        }

        private void cancelScheduledStartToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            _encodeScheduledUtc = null;
            _encodeScheduleCts?.Cancel();
            cancelScheduledStartToolStripMenuItem.Enabled = false;
            toolStripStatusLabel1.Text = "Scheduled start canceled.";
        }

        // Which extensions count as "video" for drag-drop (fallback if checklist isn't loaded yet)
        private static readonly string[] DefaultVideoExts =
        {
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".m2ts", ".wmv"
        };

        // Which extensions count as "audio-capable" for the Audio panel (drag/drop + scan)
        private static readonly string[] DefaultAudioExts =
        {
            ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma",
            ".ac3", ".eac3", ".dts", ".thd",
            ".mkv", ".mp4", ".m4v", ".mov", ".ts", ".m2ts", ".mka"
        };

        private void dgvEncodeQueue_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private async void dgvEncodeQueue_DragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                await ImportEncodePathsAsync(
                    paths,
                    includeSubfolders: true,
                    applyCodecFilters: true,
                    replaceExisting: false);
            }
        }

        // ===== Encoding status & helpers =====
        private volatile bool _encodingActive = false; // single source of truth

        private void SetStatusEncoding(bool on)
        {
            _encodingActive = on;

            bool isUpscale = false;

            if (on)
            {
                try
                {
                    // If resolution is "None", this returns ScaleMode.None.
                    var scaleMode = GetSelectedScaleMode();
                    isUpscale = scaleMode != EncodingService.ScaleMode.None;
                }
                catch
                {
                    // If anything weird happens, just treat it as normal encoding.
                    isUpscale = false;
                }
            }

            toolStripStatusLabel1.Text = on
                ? (isUpscale ? "Upscaling…" : "Encoding…")
                : "Ready";

            // Keep normal cursor
            if (this.Cursor != Cursors.Default)
                this.Cursor = Cursors.Default;

            if (_activityIndicator == null)
                return;

            if (on)
            {
                var activity = isUpscale ? UiActivity.Upscaling : UiActivity.Encoding;
                _activityIndicator.StartActivity(activity);
            }
            else
            {
                // Stop both possible encoding-related activities to be safe.
                _activityIndicator.StopActivity(UiActivity.Encoding);
                _activityIndicator.StopActivity(UiActivity.Upscaling);
            }
        }

        // Recursively expand dropped items into a flat list of files
        private List<string> ExpandFilesAndFolders(IEnumerable<string> paths)
        {
            var list = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (File.Exists(p))
                    {
                        list.Add(p);
                    }
                    else if (Directory.Exists(p))
                    {
                        foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories))
                            list.Add(f);
                    }
                }
                catch { /* ignore inaccessible paths */ }
            }
            return list;
        }

        // Extension selection is managed in Tools > Settings.
        private HashSet<string> GetAllowedExts()
        {
            var supported = SupportedExtensionsStore.Load(_supportedVideoExtsPath, DefaultVideoExts);
            var enabled = new HashSet<string>(_config.EnabledVideoExtensions, StringComparer.OrdinalIgnoreCase);
            var set = enabled.Count == 0
                ? new HashSet<string>(supported, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(supported.Where(enabled.Contains), StringComparer.OrdinalIgnoreCase);

            if (set.Count == 0)
                set.UnionWith(supported);

            return set;
        }

        private List<string> FilterByAllowedExtensions(IEnumerable<string> files)
        {
            var allowed = GetAllowedExts();
            var list = new List<string>();
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (!string.IsNullOrEmpty(ext) && allowed.Contains(ext))
                    list.Add(f);
            }
            return list;
        }

        private bool AddEncodeItemIfNotPresent(
            string path,
            bool refreshEstimates = true,
            bool appendToActiveQueue = true)
        {
            if (_rowsByPath.ContainsKey(path))
                return false;

            // Skip if already present (by Tag.Path or Tag string)
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.Tag is RowMeta rm &&
                    string.Equals(rm.Path, path, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (row.Tag is string s &&
                    string.Equals(s, path, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            double sourceMb = fi.Length / (1024.0 * 1024.0);

            // Add row without firing estimate refresh while we mutate rows
            _suppressRowEvents = true;
            int idx;
            try
            {
                idx = dgvEncodeQueue.Rows.Add();
            }
            finally
            {
                _suppressRowEvents = false;
            }

            var r = dgvEncodeQueue.Rows[idx];

            // Fill static columns only; leave estimated/metadata blank
            r.Cells["colName"].Value = fi.Name;
            r.Cells["colSize"].Value = FormatSize(sourceMb);
            r.Cells["colEstimatedSize"].Value = "";
            r.Cells["colCreated"].Value = fi.CreationTime.ToString("yyyy-MM-dd HH:mm");
            if (dgvEncodeQueue.Columns.Contains("colStatus"))
                SetEncodeRowState(r, "Queued", "");
            else
                r.Cells["colProgress"].Value = "";
            r.Cells["colETA"].Value = "";
            if (dgvEncodeQueue.Columns.Contains("colCustom"))
                r.Cells["colCustom"].Value = "";

            // Initially tag with just the path; RowMeta will be attached by the smart estimate UI pump
            r.Tag = path;
            _rowsByPath[path] = r;
            _queueSourceSizeMap[path] = sourceMb;
            _queueTotalSourceMb += sourceMb;
            _queueFileCount++;
            _queueTotalsDirty = false;
            UpdateAnalyzeQueueButtonState();

            // Imports performed during an encode are automatically appended to the live queue.
            lock (_activeEncodeQueueLock)
            {
                if (appendToActiveQueue && _encodingActive && _activeEncodeQueue != null && !_activeEncodeQueue.Contains(r))
                    _activeEncodeQueue.Add(r);
            }

            // Now kick off/refresh background estimates for whatever is in the grid
            if (refreshEstimates)
                RunEstimatePass();

            return true;
        }

        // Map UI choice to ffmpeg encoder
        private string ResolveVideoCodec(string encoderText, string formatChoice)
        {
            bool useNvenc = IsNvencSelected(encoderText);
            bool useQsv = IsQsvSelected(encoderText);

            if (useNvenc)
            {
                if (formatChoice.StartsWith("H.264")) return "h264_nvenc";
                if (formatChoice.StartsWith("H.265") || formatChoice.StartsWith("H.265 / HEVC")) return "hevc_nvenc";
                if (formatChoice.StartsWith("AV1")) return "av1_nvenc";
            }
            if (useQsv)
            {
                if (formatChoice.StartsWith("H.264")) return "h264_qsv";
                if (formatChoice.StartsWith("H.265") || formatChoice.StartsWith("H.265 / HEVC")) return "hevc_qsv";
                if (formatChoice.StartsWith("AV1")) return "av1_qsv";
            }
            if (formatChoice.StartsWith("H.264")) return "libx264";
            if (formatChoice.StartsWith("H.265") || formatChoice.StartsWith("H.265 / HEVC")) return "libx265";
            if (formatChoice.StartsWith("AV1")) return "libsvtav1";

            return useNvenc ? "hevc_nvenc"
                 : useQsv ? "hevc_qsv"
                 : "libx265"; // safe fallback
        }

        private string BuildOutputSuffix(string formatChoice)
        {
            var parts = new List<string>();

            if (_config.EnableCodecSuffix)
            {
                string codecLabel = GetCodecSuffixLabel(formatChoice);
                if (!string.IsNullOrWhiteSpace(codecLabel))
                    parts.Add($"[{codecLabel}]");
            }

            if (_config.EnableOutputSuffix)
            {
                string outputSuffix = _config.OutputSuffix?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(outputSuffix))
                {
                    if (_config.EnableCodecSuffix)
                        parts.Add($"[{outputSuffix}]");
                    else
                        parts.Add(outputSuffix);
                }
            }

            if (parts.Count == 0)
                return string.Empty;

            return $" {string.Join(" ", parts)}";
        }

        private static string GetCodecSuffixLabel(string formatChoice)
        {
            if (formatChoice.StartsWith("H.264")) return "x264";
            if (formatChoice.StartsWith("H.265") || formatChoice.StartsWith("H.265 / HEVC")) return "HEVC";
            if (formatChoice.StartsWith("AV1")) return "AV1";

            return formatChoice.Trim();
        }

        // Map UI Resolution combo to EncodingService.ScaleMode
        private EncodingService.ScaleMode GetSelectedScaleMode()
        {
            if (comboResolution == null)
                return EncodingService.ScaleMode.None;

            return comboResolution.SelectedIndex switch
            {
                1 => EncodingService.ScaleMode.To720p,
                2 => EncodingService.ScaleMode.To1080p,
                3 => EncodingService.ScaleMode.To1440p,
                4 => EncodingService.ScaleMode.To4K,
                _ => EncodingService.ScaleMode.None
            };
        }

        // Apply saved settings (soft-apply; won’t break if values are absent)
        private void ApplySnapshotSettingsToUi(QueueSettings s)
        {
            try
            {
                chkAutoTargetSize.Checked = s.AutoTargetSize;
                if (!s.AutoTargetSize && s.ManualTargetMb.HasValue && s.ManualTargetMb > 0)
                    txtTargetSize.Text = s.ManualTargetMb.Value.ToString("0");

                if (!string.IsNullOrWhiteSpace(s.CompressionProfile))
                    comboCompressionProfile.SelectedItem = s.CompressionProfile;

                if (!string.IsNullOrWhiteSpace(s.EncoderMode))
                    comboEncoderMode.SelectedItem = s.EncoderMode;

                if (!string.IsNullOrWhiteSpace(s.OutputFolder))
                    cmbEncodeOutput.Text = s.OutputFolder;
            }
            catch
            {
                // Best-effort; ignore mismatches
            }
        }

        private void SetEtaCellColor(DataGridViewRow row, double speedX)
        {
            // speedX: 1.0 = realtime, >1 faster than realtime
            var cell = row.Cells["colETA"];

            // ---- identify this row for smoothing / trend ----
            string key;
            if (TryGetRowPathAndDuration(row, out var path, out _)
                && !string.IsNullOrWhiteSpace(path))
            {
                key = path;
            }
            else
            {
                // fall back to row index if we somehow don't know the path yet
                key = $"row-{row.Index}";
            }

            _etaSpeedState.TryGetValue(key, out var prevSmoothed);

            // If ffmpeg gives us garbage or 0, fall back to previous value
            if (speedX <= 0 && prevSmoothed > 0)
                speedX = prevSmoothed;

            // ---- exponential smoothing to reduce jitter ----
            // alpha controls "responsiveness": 0.3 = 30% new, 70% old
            const double alpha = 0.3;
            double smoothed;

            if (prevSmoothed <= 0)
                smoothed = speedX; // first sample
            else
                smoothed = prevSmoothed * (1 - alpha) + speedX * alpha;

            if (smoothed < 0) smoothed = 0;
            _etaSpeedState[key] = smoothed;

            // ---- map smoothed speed to a color gradient ----
            // Keep your old semantics but make them smooth:
            //   < 0.7x  : red-ish -> yellow
            //   0.7–1.2x: yellow -> green
            //   > 1.2x  : solid green
            Color slow = Color.MistyRose;
            Color medium = Color.Khaki;
            Color fast = Color.LightGreen;

            Color back;
            if (smoothed <= 0)
            {
                back = Color.LightGray; // unknown / initializing
            }
            else if (smoothed < 0.7)
            {
                // 0..0.7 : blend slow -> medium
                double t = smoothed / 0.7;
                back = LerpColor(slow, medium, t);
            }
            else if (smoothed < 1.2)
            {
                // 0.7..1.2 : blend medium -> fast
                double t = (smoothed - 0.7) / (1.2 - 0.7);
                back = LerpColor(medium, fast, t);
            }
            else
            {
                back = fast;
            }

            Color fore = Color.Black;

            cell.Style.BackColor = back;
            cell.Style.ForeColor = fore;
            cell.Style.SelectionBackColor = back;
            cell.Style.SelectionForeColor = fore;

            // ---- optional “speed trend” in tooltip (not in the cell text) ----
            string trend = "";
            if (prevSmoothed > 0 && smoothed > 0)
            {
                double delta = smoothed - prevSmoothed;
                const double deadZone = 0.03; // ignore tiny noise

                if (delta > deadZone)
                    trend = "↑ faster";
                else if (delta < -deadZone)
                    trend = "↓ slower";
                else
                    trend = "→ stable";
            }

            cell.ToolTipText = smoothed > 0
                ? $"Speed: {smoothed:F2}x {trend}".Trim()
                : string.Empty;
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t)); // clamp 0..1
            int r = (int)(a.R + (b.R - a.R) * t);
            int g = (int)(a.G + (b.G - a.G) * t);
            int bVal = (int)(a.B + (b.B - a.B) * t);
            return Color.FromArgb(r, g, bVal);
        }

        private void SetEncodeRowState(DataGridViewRow row, string status, string? progress = null, string? eta = null, string? tooltip = null)
        {
            if (row == null || row.IsNewRow || row.DataGridView != dgvEncodeQueue)
                return;

            if (dgvEncodeQueue.Columns.Contains("colStatus"))
                row.Cells["colStatus"].Value = status;

            if (progress != null && dgvEncodeQueue.Columns.Contains("colProgress"))
                row.Cells["colProgress"].Value = progress;

            if (eta != null && dgvEncodeQueue.Columns.Contains("colETA"))
                row.Cells["colETA"].Value = eta;

            if (tooltip != null && dgvEncodeQueue.Columns.Contains("colStatus"))
                row.Cells["colStatus"].ToolTipText = tooltip;

            ApplyEncodeRowVisualState(row);
        }

        private void ApplyEncodeRowVisualState(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow || row.DataGridView != dgvEncodeQueue)
                return;

            string status = dgvEncodeQueue.Columns.Contains("colStatus")
                ? row.Cells["colStatus"].Value?.ToString() ?? ""
                : "";

            Color back = Color.White;
            Color fore = Color.FromArgb(24, 24, 24);
            Color statusBack = Color.WhiteSmoke;
            Color statusFore = Color.FromArgb(24, 24, 24);

            switch (status.Trim().ToLowerInvariant())
            {
                case "queued":
                case "retry queued":
                    back = Color.FromArgb(248, 250, 252);
                    statusBack = Color.FromArgb(226, 232, 240);
                    statusFore = Color.FromArgb(51, 65, 85);
                    break;
                case "estimating":
                    back = Color.FromArgb(255, 251, 235);
                    statusBack = Color.FromArgb(254, 243, 199);
                    statusFore = Color.FromArgb(146, 64, 14);
                    break;
                case "encoding":
                    back = Color.FromArgb(239, 246, 255);
                    statusBack = Color.FromArgb(191, 219, 254);
                    statusFore = Color.FromArgb(30, 64, 175);
                    break;
                case "done":
                    back = Color.FromArgb(240, 253, 244);
                    statusBack = Color.FromArgb(187, 247, 208);
                    statusFore = Color.FromArgb(22, 101, 52);
                    break;
                case "failed":
                    back = Color.FromArgb(254, 242, 242);
                    statusBack = Color.FromArgb(254, 202, 202);
                    statusFore = Color.FromArgb(153, 27, 27);
                    break;
                case "canceled":
                    back = Color.FromArgb(255, 251, 235);
                    statusBack = Color.FromArgb(253, 230, 138);
                    statusFore = Color.FromArgb(120, 53, 15);
                    break;
                case "excluded - exact duplicate":
                    back = Color.FromArgb(248, 250, 252);
                    fore = Color.FromArgb(100, 116, 139);
                    statusBack = Color.FromArgb(226, 232, 240);
                    statusFore = Color.FromArgb(71, 85, 105);
                    break;
            }

            row.DefaultCellStyle.BackColor = back;
            row.DefaultCellStyle.ForeColor = fore;
            row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(37, 99, 235);
            row.DefaultCellStyle.SelectionForeColor = Color.White;

            if (dgvEncodeQueue.Columns.Contains("colStatus"))
            {
                var cell = row.Cells["colStatus"];
                cell.Style.BackColor = statusBack;
                cell.Style.ForeColor = statusFore;
                cell.Style.SelectionBackColor = statusBack;
                cell.Style.SelectionForeColor = statusFore;
            }
        }

    }
}




