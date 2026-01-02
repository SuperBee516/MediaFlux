using Encode.Models;
using Encode.Services;
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
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Encode
{
    public partial class MainForm : Form
    {
        private readonly string _configPath;
        private Config _config;        

        private bool _suppressRowEvents;        

        private volatile bool _cancelEncode = false;
        private Encode.Services.HistoryService _historyService;
        private readonly object _historyLock = new();
        private readonly Dictionary<string, double> _estimatedSizeMap = new();

        private readonly Dictionary<string, double> _etaSpeedState = new();
        private readonly EncodingService _encodingService;
        private readonly AudioService _audioService;
        private readonly MediaInfoService _mediaInfoService;

        private readonly SizeEstimateService _sizeEstimateService;
        private readonly EstimateBackgroundService _estimateService;
        private readonly EncodeQueueRunner _encodeQueueRunner;

        private DateTime? _encodeScheduledUtc = null;
        private CancellationTokenSource? _encodeScheduleCts = null;        
        private StringBuilder? _activeJobLogSb;
        private NumericUpDown? nudAutoQuality;
        private System.Windows.Forms.Timer? _estSmartUiTimer;        

        private ToolStripStatusLabel? _statusTotalSize;
        private ToolStripStatusLabel? _statusTotalEstimated;
        private ToolStripStatusLabel? _statusSpaceSaved;
        private ToolStripStatusLabel? _statusSelectedSpace;

        private PictureBox? _encodingSpinner;
        private Label? _activityLabel;
        private ActivityIndicatorService? _activityIndicator;

        // Advanced video / GPU options
        private ComboBox? comboNvencPreset;
        private CheckBox? chkTenBit;
        private ComboBox? comboAudioChannels;        

        // UI pump to apply results in small batches
        private System.Windows.Forms.Timer? _estUiTimer;

        // Map row lookup by path (keep this in sync when you add/remove rows)
        private readonly ConcurrentDictionary<string, DataGridViewRow> _rowsByPath = new();

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

            
            // Promote progressPanel to a global, bottom-docked panel shared by all modes
            if (progressPanel != null && progressPanel.Parent != null)
            {
                // Remove from tlEncode and attach directly to the form
                progressPanel.Parent.Controls.Remove(progressPanel);
                progressPanel.Dock = DockStyle.Bottom;
                Controls.Add(progressPanel);
                progressPanel.BringToFront();
            }

            InitializeEncodingSpinner();

            // NEW: add total size labels to the StatusStrip (if present)
            var statusStrip = this.Controls.OfType<StatusStrip>().FirstOrDefault();
            if (statusStrip != null)
            {
                _statusTotalSize = new ToolStripStatusLabel
                {
                    BorderSides = ToolStripStatusLabelBorderSides.Left,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Text = "Total Size: --"
                };

                _statusTotalEstimated = new ToolStripStatusLabel
                {
                    BorderSides = ToolStripStatusLabelBorderSides.Left,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Text = "Total Est: --"
                };

                _statusSpaceSaved = new ToolStripStatusLabel
                {
                    BorderSides = ToolStripStatusLabelBorderSides.Left,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Text = "Space Saved: --"
                };

                // Try to reuse an existing 'Selected:' label from the designer (if you already added one)
                _statusSelectedSpace = statusStrip.Items
                    .OfType<ToolStripStatusLabel>()
                    .FirstOrDefault(i =>
                        (i.Text != null) &&
                        i.Text.StartsWith("Selected", StringComparison.OrdinalIgnoreCase));

                // If there isn't one, create it
                if (_statusSelectedSpace == null)
                {
                    _statusSelectedSpace = new ToolStripStatusLabel
                    {
                        BorderSides = ToolStripStatusLabelBorderSides.Left,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Text = "Selected: no estimates yet"
                    };
                    statusStrip.Items.Add(_statusSelectedSpace);
                }

                // Append the total labels after whatever you already had
                statusStrip.Items.Add(_statusTotalSize);
                statusStrip.Items.Add(_statusTotalEstimated);
                statusStrip.Items.Add(_statusSpaceSaved);
            }

            StartEstimateUiPump();
            CreateAutoQualityControl();
            UpdateAudioUiState();
            CreateAdvancedVideoControls();

            // Tools → View History
            var viewHistoryToolStripMenuItem = new ToolStripMenuItem("View History");
            viewHistoryToolStripMenuItem.Click += ViewHistoryToolStripMenuItem_Click;
            toolsToolStripMenuItem.DropDownItems.Insert(0, viewHistoryToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());

            //History service init
            var historyPath = Path.Combine(Application.StartupPath, "data", "history.json");
            _historyService = new Encode.Services.HistoryService(historyPath);

            // Touch lblResolution so the field is considered "used" by the analyzer
            _ = lblResolution;

            // Codec Selection
            comboVideoFormat.SelectedIndex = 0; // H.265 / HEVC (x265)

            dgvEncodeQueue.RowsAdded += (_, __) => { if (!_suppressRowEvents) SafeRefreshEstimates(); };
            dgvEncodeQueue.RowsRemoved += (_, __) => { if (!_suppressRowEvents) SafeRefreshEstimates(); };
            dgvEncodeQueue.SortCompare += DgvEncodeQueue_SortCompare;
            dgvEncodeQueue.SelectionChanged += (s, e) => UpdateSelectedSpaceTotals();

            chkAutoTargetSize.CheckedChanged += (_, __) => SafeRefreshEstimates();
            comboCompressionProfile.SelectedIndexChanged += (_, __) => SafeRefreshEstimates();

            chkIncludeSubfolders.CheckedChanged += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(cmbInputFolder.Text) &&
                    Directory.Exists(cmbInputFolder.Text))
                {
                    RefreshEncodeGrid();
                }
            };

            void SafeRefreshEstimates()
            {
                // only refresh if there are rows
                if (dgvEncodeQueue.Rows.Count > 0)
                    btnRefreshEncode_Click(null, EventArgs.Empty);
            }

            _encodingService = new EncodingService(
                Application.StartupPath,
                HandleFfmpegProgressLine
            );

            _audioService = new AudioService(
                Application.StartupPath,
                HandleFfmpegProgressLine
            );
            
            _mediaInfoService = new MediaInfoService(AppDomain.CurrentDomain.BaseDirectory);
            _sizeEstimateService = new SizeEstimateService(_mediaInfoService);            
            _estimateService = new EstimateBackgroundService(_sizeEstimateService, _mediaInfoService);

            _encodeQueueRunner = new EncodeQueueRunner();

            InitializeAudioQueueContextMenu();

            // ─── menu / toolbar handlers ───────────────────────────
            this.exitToolStripMenuItem.Click += exitToolStripMenuItem_Click;
            this.clearFolderHistoryToolStripMenuItem.Click += new System.EventHandler(this.clearFolderHistoryToolStripMenuItem_Click);
            this.settingsToolStripMenuItem.Click += new System.EventHandler(this.SettingsToolStripMenuItem_Click);
            this.checkForUpdatesToolStripMenuItem.Click += new System.EventHandler(this.CheckForUpdatesToolStripMenuItem_Click);
            this.columnSettingsToolStripMenuItem.Click += new System.EventHandler(this.ColumnSettingsToolStripMenuItem_Click);

            // Job timer wiring
            jobTimer.Interval = 1000;
            jobTimer.Tick += JobTimer_Tick;

            // wire up Load for restoring settings
            this.Load += MainForm_Load;

            // load configuration
            _configPath = Path.Combine(Application.StartupPath, "config.json");
            _config = Config.Load(_configPath);

            WireCheckboxPersistence();
            ApplyRememberedCheckboxStates();

            // Encode defaults
            comboEncoderMode.SelectedItem = "GPU (NVENC)";

            // extensions checklist
            checkedListExt.Items.AddRange(new object[] { ".mp4", ".mkv", ".mov", ".avi", ".webm" });
            for (int i = 0; i < checkedListExt.Items.Count; i++)
                checkedListExt.SetItemChecked(i, true);

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

        private void UpdateSelectedSpaceTotals()
        {
            if (_statusSelectedSpace == null)
                return;

            // No selection
            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                _statusSelectedSpace.Text = "Selected: none";
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
                _statusSelectedSpace.Text = "Selected: no estimates yet";
                return;
            }

            var savedMb = Math.Max(0, totalSrcMb - totalEstMb);
            var pctSaved = totalSrcMb > 0 ? (savedMb / totalSrcMb) * 100.0 : 0.0;

            // FormatSize works in bytes, so convert MB -> bytes
            var savedBytes = (long)(savedMb * 1024.0 * 1024.0);

            _statusSelectedSpace.Text =
                $"Selected: {FormatSize(savedBytes)} ({pctSaved:F0}% saved)";
        }


        private void InitializeEncodingSpinner()
        {
            _encodingSpinner = new PictureBox
            {
                Name = "picEncodingSpinner",
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Visible = true,                // always visible; idle image when no activity
                Size = new Size(96, 96),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            Controls.Add(_encodingSpinner);
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

            Controls.Add(_activityLabel);
            _activityLabel.BringToFront();

            RepositionEncodingSpinner();
            this.Resize += (_, __) => RepositionEncodingSpinner();

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
            if (_encodingSpinner == null)
                return;

            const int margin = 16;

            _encodingSpinner.Location = new Point(
                ClientSize.Width - _encodingSpinner.Width - margin,
                margin * 2);

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

        // How many items in the active queue have finished
        private int _encodeProcessedCount = 0;

        // Simple metadata for a grid row
        private sealed class RowMeta
        {
            public string Path = "";
            public double DurationSec = 0; // Initialized to suppress warning
            public string Resolution = ""; // Changed to string; initialized
            public int Fps = 0; // Initialized to suppress warning
            public double SrcMb = 0; // Initialized to suppress warning
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
                Text = "Auto Quality (CRF/CQ):",
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

            // NVENC preset label + combo
            var lblPreset = new Label
            {
                Text = "Encoding Speed:",
                AutoSize = true,
                Margin = new Padding(4, 2, 4, 2),
                Anchor = AnchorStyles.Left
            };

            comboNvencPreset = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 180,
                Margin = new Padding(4, 2, 4, 2),
                Anchor = AnchorStyles.Left
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

            // 10-bit toggle
            chkTenBit = new CheckBox
            {
                Text = "Use 10-bit for HEVC/AV1",
                AutoSize = true,
                Margin = new Padding(4, 2, 4, 2),
                Anchor = AnchorStyles.Left
            };

            // Audio channels label + combo
            var lblChannels = new Label
            {
                Text = "Audio channels:",
                AutoSize = true,
                Margin = new Padding(4, 2, 4, 2),
                Anchor = AnchorStyles.Left
            };

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

            // --- Add as new rows in the existing 2-column table ---

            int startRow = tlOptions.RowCount;
            tlOptions.RowCount = startRow + 3;
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // preset row
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 10-bit + label row
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // audio combo row

            // Row: NVENC preset
            tlOptions.Controls.Add(lblPreset, 0, startRow);
            tlOptions.Controls.Add(comboNvencPreset, 1, startRow);

            // Row: 10-bit + "Audio channels" label
            tlOptions.Controls.Add(chkTenBit, 0, startRow + 1);
            tlOptions.Controls.Add(lblChannels, 1, startRow + 1);

            // Row: audio channels combo spanning full width
            tlOptions.Controls.Add(comboAudioChannels, 0, startRow + 2);
            tlOptions.SetColumnSpan(comboAudioChannels, 2);

            // NEW: Dual NVENC / parallel encodes checkbox
            var chkDualNvenc = new CheckBox
            {
                Name = "chkDualNvenc",
                Text = "Use 2 concurrent GPU encodes (dual NVENC)",
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 3)
            };
            tlOptions.Controls.Add(chkDualNvenc, 0, startRow + 3);
            tlOptions.SetColumnSpan(chkDualNvenc, 2);

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
            bool isGpu = comboEncoderMode.SelectedItem?.ToString()?
                .StartsWith("GPU", StringComparison.OrdinalIgnoreCase) == true;

            // NVENC preset combo
            var presetCombo = grpOptions.Controls
                .Find("comboNvencPreset", true)
                .OfType<ComboBox>()
                .FirstOrDefault();

            if (presetCombo != null)
            {
                presetCombo.Enabled = isGpu;
                if (!isGpu && presetCombo.Items.Count > 0)
                    presetCombo.SelectedIndex = 0; // "Auto"
            }

            // Ten-bit checkbox
            var tenBitCheck = grpOptions.Controls
                .Find("chkTenBit", true)
                .OfType<CheckBox>()
                .FirstOrDefault();
            if (tenBitCheck != null)
            {
                tenBitCheck.Enabled = isGpu;
                if (!isGpu) tenBitCheck.Checked = false;
            }

            // Dual NVENC checkbox
            var dualNvencCheck = grpOptions.Controls
                .Find("chkDualNvenc", true)
                .OfType<CheckBox>()
                .FirstOrDefault();
            if (dualNvencCheck != null)
            {
                dualNvencCheck.Enabled = isGpu;
                if (!isGpu) dualNvencCheck.Checked = false;
            }
        }

        private int GetDefaultQualityForSelection()
        {
            // If the user chose "No Compression", return a neutral quality.
            // (It will be ignored by the No-Compression ffmpeg branch, but avoids nulls/edge cases.)
            if (IsNoCompressionSelected())
            {
                var (_, isNvencNC) = GetSelectedCodecInfo();
                return isNvencNC ? 19 : 22; // CQ 19 for NVENC, CRF 22 for CPU
            }

            // If the numeric control is present, use it directly.
            if (nudAutoQuality != null)
                return (int)nudAutoQuality.Value;

            // Fallback if nudAutoQuality isn’t there yet: infer from selection.
            var (codec, isNvenc) = GetSelectedCodecInfo();
            if (isNvenc) return 19;
            return (codec.IndexOf("265", StringComparison.OrdinalIgnoreCase) >= 0
                    || codec.IndexOf("av1", StringComparison.OrdinalIgnoreCase) >= 0)
                   ? 24   // libx265/libaom-av1
                   : 22;  // libx264
        }

        // Return selected ffmpeg video encoder name + isNvenc flag
        private (string codec, bool isNvenc) GetSelectedCodecInfo()
        {
            // Replace with your actual UI selectors.
            // Examples:
            // var fmt = cmbVideoFormat.SelectedItem?.ToString() ?? "H.264";
            // var enc = cmbEncoder.SelectedItem?.ToString() ?? "CPU";
            string fmt = GetSelectedFormatText();   // implement by reading your combo
            string enc = GetSelectedEncoderText();  // implement by reading your combo

            bool nvenc = enc.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0
                         || enc.IndexOf("NVENC", StringComparison.OrdinalIgnoreCase) >= 0
                         || enc.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0;

            string codec = "libx264";
            if (fmt.Contains("265") || fmt.Contains("HEVC", StringComparison.OrdinalIgnoreCase))
                codec = nvenc ? "hevc_nvenc" : "libx265";
            else if (fmt.Contains("AV1", StringComparison.OrdinalIgnoreCase))
                codec = nvenc ? "av1_nvenc" : "libaom-av1";
            else // H.264
                codec = nvenc ? "h264_nvenc" : "libx264";

            return (codec, nvenc);
        }

        private string GetSelectedFormatText() => comboVideoFormat?.Text ?? "H.264";
        private string GetSelectedEncoderText() => comboEncoderMode?.Text ?? "CPU";

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
            // default to Encode mode
            modeComboBox.SelectedItem = "Encode";
            ModeComboBox_SelectedIndexChanged(modeComboBox, EventArgs.Empty);

            // restore column visibility
            if (dgvEncodeQueue.Columns.Contains("colSize"))
                dgvEncodeQueue.Columns["colSize"].Visible = _config.ShowSizeColumn;
            if (dgvEncodeQueue.Columns.Contains("colCreated"))
                dgvEncodeQueue.Columns["colCreated"].Visible = _config.ShowCreatedColumn;

            // restore column widths if previously saved (> 0)
            if (_config.NameColumnWidth > 0 && dgvEncodeQueue.Columns.Contains("colName"))
                dgvEncodeQueue.Columns["colName"].Width = _config.NameColumnWidth;
            if (_config.SizeColumnWidth > 0 && dgvEncodeQueue.Columns.Contains("colSize"))
                dgvEncodeQueue.Columns["colSize"].Width = _config.SizeColumnWidth;
            if (_config.CreatedColumnWidth > 0 && dgvEncodeQueue.Columns.Contains("colCreated"))
                dgvEncodeQueue.Columns["colCreated"].Width = _config.CreatedColumnWidth;
            if (dgvEncodeQueue.Columns.Contains("colEstimatedSize"))
                dgvEncodeQueue.Columns["colEstimatedSize"].Visible = true; // Ensure visible

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

            // when the user toggles, re‐save and re‐apply filter
            chkFilterX264.CheckedChanged += (s, e) => {
                _config.ShowX264Files = chkFilterX264.Checked;
                _config.Save(_configPath);
                RefreshEncodeGrid();
            };
            chkFilterX265.CheckedChanged += (s, e) => {
                _config.ShowX265Files = chkFilterX265.Checked;
                _config.Save(_configPath);
                RefreshEncodeGrid();
            };

            chkProcessAll.Checked = _config.LastChkProcessAll;
            chkProcessAll.CheckedChanged += (s, e) => {
                _config.LastChkProcessAll = chkProcessAll.Checked;
                _config.Save(_configPath);
            };

            LoadHistoryGrid();

        }

        

        private void ApplyRememberedCheckboxStates()
        {
            if (!_config.RememberCheckboxStates) return;

            // Apply last-used states to the UI:
            chkAutoTargetSize.Checked = _config.LastChkAutoTargetSize;
            chkDeleteSource.Checked = _config.LastChkDeleteSource;
            chkFilterX264.Checked = _config.LastChkFilterX264;
            chkFilterX265.Checked = _config.LastChkFilterX265;
            chkProcessAll.Checked = _config.LastChkProcessAll;

            // Respect your existing enable/disable behavior for target size box
            txtTargetSize.Enabled = !chkAutoTargetSize.Checked;
        }

        private void WireCheckboxPersistence()
        {
            // Any time a checkbox changes, persist if enabled
            chkAutoTargetSize.CheckedChanged += PersistCheckboxStatesIfEnabled;
            chkDeleteSource.CheckedChanged += PersistCheckboxStatesIfEnabled;
            chkFilterX264.CheckedChanged += PersistCheckboxStatesIfEnabled;
            chkFilterX265.CheckedChanged += PersistCheckboxStatesIfEnabled;

            chkProcessAll.CheckedChanged += PersistCheckboxStatesIfEnabled;
        }

        private void PersistCheckboxStatesIfEnabled(object? sender, EventArgs e)
        {
            if (!_config.RememberCheckboxStates) return;

            _config.LastChkAutoTargetSize = chkAutoTargetSize.Checked;
            _config.LastChkDeleteSource = chkDeleteSource.Checked;
            _config.LastChkFilterX264 = chkFilterX264.Checked;
            _config.LastChkFilterX265 = chkFilterX265.Checked;
            _config.LastChkProcessAll = chkProcessAll.Checked;

            _config.Save(_configPath);
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

            MessageBox.Show(
                "Cleared saved input/output folder history.",
                "Folder History Cleared",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
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

        private void btnBrowseInput_Click(object sender, EventArgs e)
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
            cmbInputFolder.Text = picked;

            // ─── History maintenance ─────────────────────────────
            AddToHistory(_config.LastInputFolders, picked);
            RefreshHistoryCombo(cmbInputFolder, _config.LastInputFolders);
            _config.Save(_configPath);

            // ─── Status update + scan ────────────────────────────
            toolStripStatusLabel1.Text = "Preparing to scan…";
            ScanAndPopulateEncodeGrid(picked);
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
                cmbEncodeOutput.Text = picked;

                AddToHistory(_config.LastOutputFolders, picked);
                RefreshHistoryCombo(cmbEncodeOutput, _config.LastOutputFolders);
                _config.Save(_configPath);
            }
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
            UpdateEncodeProgressFromLine_ForRow(row, durationSec, line);

            // You keep jobLog updated above.
        }

        private void UpdateEncodeProgressFromLine_ForRow(
    DataGridViewRow row,
    double durationSec,
    string line)
        {
            if (row == null || durationSec <= 0 || string.IsNullOrEmpty(line))
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
                StopEstimateUiPump();

                lock (_monLock)
                {
                    _monitorTimer?.Dispose();
                    _monitorTimer = null;
                    _monWatcher?.Dispose();
                    _monWatcher = null;
                    _monNeedsScan = false;
                    _monitoring = false;
                }
            }
            catch
            {
                /* ignore during shutdown */
            }

            base.OnFormClosing(e);
        }

        // Rescan the current input folder and MERGE results into the grid:
        // - Adds new files that match current filters
        // - Removes rows for files that no longer exist on disk
        // - Optionally triggers estimate recompute
        private void RescanInputFolderAndMerge(bool recomputeEstimates)
        {
            var folder = cmbInputFolder.Text;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            try
            {
                var allowedExts = GetAllowedExtensionsFromUi();
                var searchOpt = chkIncludeSubfolders.Checked
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                // All files on disk that pass the current filters
                var fsFiles = new HashSet<string>(
                    Directory.EnumerateFiles(folder, "*.*", searchOpt)
                        .Where(f =>
                        {
                            string ext = Path.GetExtension(f);
                            if (string.IsNullOrEmpty(ext) || !allowedExts.Contains(ext))
                                return false;

                            // Respect codec filters (if either is off)
                            if (!chkFilterX264.Checked || !chkFilterX265.Checked)
                            {
                                var codec = GetVideoCodec(f);
                                if ((codec == "h264" && !chkFilterX264.Checked) ||
                                    ((codec == "hevc" || codec == "h265") && !chkFilterX265.Checked))
                                    return false;
                            }

                            return true;
                        }),
                    StringComparer.OrdinalIgnoreCase);

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
            }
        }

        // Remove a row and keep internal maps tidy
        private void RemoveRowAndCleanup(DataGridViewRow row)
        {
            var path = GetPathFromRow(row);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _rowsByPath.TryRemove(path, out _);
                _estimatedSizeMap.Remove(path);
            }

            _suppressRowEvents = true;
            try
            {
                dgvEncodeQueue.Rows.Remove(row);
            }
            finally
            {
                _suppressRowEvents = false;
            }
            SafeRefreshEstimates();
            UpdateSizeTotals();
        }

        private void UpdateSelectionSizeTotals()
        {
            if (_statusSelectedSpace == null)
                return;

            // No rows or no selection
            if (dgvEncodeQueue.Rows.Count == 0 || dgvEncodeQueue.SelectedRows.Count == 0)
            {
                _statusSelectedSpace.Text = "Selected: none";
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
                _statusSelectedSpace.Text = "Selected: no estimates yet";
                return;
            }

            double savedMb = Math.Max(0, srcTotalMb - estTotalMb);
            double savedPct = srcTotalMb > 0 ? (savedMb / srcTotalMb) * 100.0 : 0.0;

            // Show selection stats in GB for readability
            _statusSelectedSpace.Text =
                $"Selected: {counted} file(s) — save ≈ {savedMb / 1024.0:0.00} GB ({savedPct:0}% of source)";
        }

        private void RefreshEncodeGrid()
        {
            var folder = cmbInputFolder.Text;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            dgvEncodeQueue.Rows.Clear();

            var extSet = GetAllowedExts();
            var searchOpt = chkIncludeSubfolders.Checked
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            int added = 0;

            foreach (var f in Directory.GetFiles(folder, "*.*", searchOpt))
            {
                var ext = Path.GetExtension(f);
                if (string.IsNullOrEmpty(ext) || !extSet.Contains(ext))
                    continue;

                var codec = GetVideoCodec(f);
                if ((codec == "h264" && !chkFilterX264.Checked) ||
                    ((codec == "hevc" || codec == "h265") && !chkFilterX265.Checked))
                    continue;

                AddFileToGrid(f);
                added++;
            }

            toolStripStatusLabel1.Text = added > 0
                ? $"Refreshed \"{folder}\" — {added} file(s) in view."
                : $"Refreshed \"{folder}\" — no files matched current filters.";
        }

        private void AddFileToGrid(string path)
        {
            AddEncodeItemIfNotPresent(path);
        }

        private void btnRefreshEncode_Click(object? sender, EventArgs e)
        {
            RescanInputFolderAndMerge();  // never calls estimates
            RunEstimatePass();            // never adds/removes rows
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
        private bool IsDualNvencRequested()
        {
            var chk = grpOptions.Controls
                .Find("chkDualNvenc", true)
                .OfType<CheckBox>()
                .FirstOrDefault();

            if (chk == null || !chk.Enabled)
                return false;

            return chk.Checked;
        }
        private int GetMaxConcurrentEncodes()
        {
            // Only ever use >1 when GPU NVENC is active.
            string encoderText = comboEncoderMode.SelectedItem?.ToString() ?? string.Empty;
            bool useGpu = encoderText.StartsWith("GPU", StringComparison.OrdinalIgnoreCase);

            if (!useGpu)
                return 1;

            return IsDualNvencRequested() ? 2 : 1;
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
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            var chkName = new CheckBox { Text = "Name", Checked = true, Top = 10, Left = 10 };
            var chkSize = new CheckBox { Text = "Size", Checked = _config.ShowSizeColumn, Top = 35, Left = 10 };
            var chkCreated = new CheckBox { Text = "Created", Checked = _config.ShowCreatedColumn, Top = 60, Left = 10 };
            var btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Top = 90, Left = 10 };

            dlg.Controls.AddRange(new Control[] { chkName, chkSize, chkCreated, btnOK });
            dlg.AcceptButton = btnOK;

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                dgvEncodeQueue.Columns["colName"].Visible = chkName.Checked;
                dgvEncodeQueue.Columns["colSize"].Visible = chkSize.Checked;
                dgvEncodeQueue.Columns["colCreated"].Visible = chkCreated.Checked;

                _config.ShowSizeColumn = chkSize.Checked;
                _config.ShowCreatedColumn = chkCreated.Checked;
                _config.Save(_configPath);
            }
        }

        
        private void UpdateEncodeProgressFromLine(string line)
        {
            if (_activeEncodeRow == null) return;
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

        private void SettingsToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            using var dlg = new SettingsForm(_config);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _config = dlg.Config;
                _config.Save(_configPath);
            }
        }

        private void CheckForUpdatesToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (UpdateManager.CheckAndPrompt(this, _config.UpdateFolderPath))
                return; // updater launched; app will exit from UpdateManager
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
                    _config.NameColumnWidth = e.Column.Width;
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
            var confirm = MessageBox.Show(
                "Clear all items from the encode queue?",
                "Confirm Clear",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm == DialogResult.Yes)
            {
                dgvEncodeQueue.Rows.Clear();
                dgvEncodeQueue.Rows.Clear();
                UpdateSizeTotals();
                UpdateSelectionSizeTotals();
            }
        }

        private void RenameFile_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count != 1)
            {
                MessageBox.Show("Select exactly one file to rename.", "Rename File",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var row = dgvEncodeQueue.SelectedRows[0];
            var fullPath = GetFullPathFromRow(row);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                MessageBox.Show("Original file not found on disk.", "Rename File",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Title = "Rename File",
                InitialDirectory = Path.GetDirectoryName(fullPath),
                FileName = Path.GetFileName(fullPath),
                Filter = "Video Files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts|All Files|*.*",
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
            using var dlg = new Encode.Services.ScheduleForm();
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
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".m2ts"
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

        private void dgvEncodeQueue_DragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                var files = ExpandFilesAndFolders(paths);
                var filtered = FilterByAllowedExtensions(files);
                if (filtered.Count == 0)
                {
                    MessageBox.Show("No supported video files were found.", "Nothing to add",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                int added = 0;
                foreach (var f in filtered)
                {
                    if (AddEncodeItemIfNotPresent(f))
                        added++;
                }                
            }
            SafeRefreshEstimates();
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

        // Use checklist if present; otherwise fallback list
        private HashSet<string> GetAllowedExts()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // pull from your UI checklist if it exists
                if (checkedListExt != null && checkedListExt.Items.Count > 0)
                {
                    for (int i = 0; i < checkedListExt.Items.Count; i++)
                    {
                        if (checkedListExt.GetItemChecked(i))
                        {
                            var s = checkedListExt.Items[i]?.ToString();
                            if (!string.IsNullOrWhiteSpace(s))
                                set.Add(s.StartsWith(".") ? s : "." + s);
                        }
                    }
                }
            }
            catch { /* ignore */ }

            if (set.Count == 0)
                set.UnionWith(DefaultVideoExts);

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
        
        private bool AddEncodeItemIfNotPresent(string path)
        {
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
            r.Cells["colSize"].Value = FormatSize(fi.Length);
            r.Cells["colEstimatedSize"].Value = "";
            r.Cells["colCreated"].Value = fi.CreationTime.ToString("yyyy-MM-dd HH:mm");
            r.Cells["colProgress"].Value = "";
            r.Cells["colETA"].Value = "";

            // Initially tag with just the path; RowMeta will be attached by the smart estimate UI pump
            r.Tag = path;

            // Now kick off/refresh background estimates for whatever is in the grid
            RunEstimatePass();

            return true;
        }        

        // Map UI choice to ffmpeg encoder
        private string ResolveVideoCodec(bool useGpu, string formatChoice)
        {
            if (useGpu)
            {
                if (formatChoice.StartsWith("H.264")) return "h264_nvenc";
                if (formatChoice.StartsWith("H.265") || formatChoice.StartsWith("H.265 / HEVC")) return "hevc_nvenc";
                if (formatChoice.StartsWith("AV1")) return "av1_nvenc";
            }
            if (formatChoice.StartsWith("H.264")) return "libx264";
            if (formatChoice.StartsWith("H.265") || formatChoice.StartsWith("H.265 / HEVC")) return "libx265";
            if (formatChoice.StartsWith("AV1")) return "libsvtav1";

            return useGpu ? "hevc_nvenc" : "libx265"; // safe fallback
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

    }
}

