using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Encode
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private Label lblCompressionProfile;
        private ComboBox comboCompressionProfile;

        // Menu & Status
        private MenuStrip menuStrip1;
        private ToolStripMenuItem scheduleEncodeStartToolStripMenuItem;
        private ToolStripMenuItem cancelScheduledStartToolStripMenuItem;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem exitToolStripMenuItem;
        private ToolStripMenuItem exportQueueToolStripMenuItem;
        private ToolStripMenuItem importQueueToolStripMenuItem;
        private ToolStripComboBox modeComboBox;
        private ToolStripMenuItem toolsToolStripMenuItem;
        private ToolStripMenuItem settingsToolStripMenuItem;
        private ToolStripMenuItem clearFolderHistoryToolStripMenuItem;
        private ToolStripMenuItem columnSettingsToolStripMenuItem;
        private ToolStripMenuItem helpToolStripMenuItem;
        private ToolStripMenuItem checkForUpdatesToolStripMenuItem;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel toolStripStatusLabel1;
        private System.Windows.Forms.ContextMenuStrip ctxEncodeGrid;
        private System.Windows.Forms.ToolStripMenuItem mnuStartEncode;

        // Monitor-panel controls
        private Panel panelMonitor;
        private TableLayoutPanel tlMonitor;
        private Label lblMonFolder, lblMonInterval, lblMonStatus;
        private ComboBox cmbMonFolder;
        private Button btnBrowseMonFolder, btnMonStart, btnMonStop, btnMonScanNow;
        private CheckBox chkMonIncludeSubfolders, chkMonAutoStart, chkMonUseEncodeFilters;
        private NumericUpDown nudMonMinutes;
        private ListBox listMonLastFound;   // optional recent discoveries view
        private Label lblMonStabilize;
        private NumericUpDown nudMonStabilizeSec;
        private Label lblMonMinSize;
        private NumericUpDown nudMonMinSizeMb;
        private CheckBox chkMonUseWatcher;
        private Panel panelMonQueueHost;

        // Panels
        private Panel panelEncode;
        private Panel panelAudio;

        // Encode-panel controls
        private TableLayoutPanel tlEncode;
        private Label lblInputFolder;
        private ComboBox cmbInputFolder;
        private Button btnBrowseInput;
        private Label lblEncoderMode;
        private ComboBox comboEncoderMode;
        private Label lblExtensions;
        private CheckedListBox checkedListExt;
        private Label lblTargetSize;
        private TextBox txtTargetSize;
        private Label lblEncodeOutput;
        private ComboBox cmbEncodeOutput;
        private Button btnBrowseOutputEncode;
        private Button btnStartEncode;
        private Label lblEncodeStatus;
        private CheckBox chkDeleteSource;
        private CheckBox chkIncludeSubfolders;
        private DataGridView dgvEncodeQueue;
        private Button btnRefreshEncode;
        private CheckBox chkFilterX264;
        private CheckBox chkFilterX265;
        private CheckBox chkProcessAll;
        private System.Windows.Forms.Button btnPauseQueue;
        private GroupBox grpOptions;

        private Label lblResolution = null!;
        private ComboBox comboResolution = null!;
        private CheckBox chkAudioNormalize;
        private ComboBox comboAudioNormalizeMode;

        // NEW: audio quality controls
        private Label lblAudioQuality;
        private ComboBox comboAudioQuality;

        // Audio-panel controls
        private TableLayoutPanel tlAudio;
        private Label lblAudioInputFolder;
        private ComboBox cmbAudioInputFolder;
        private Button btnBrowseAudioInput;
        private CheckBox chkAudioIncludeSubfolders;
        private Label lblAudioOutputFolder;
        private ComboBox cmbAudioOutputFolder;
        private Button btnBrowseAudioOutput;
        private Label lblAudioOperation;
        private ComboBox comboAudioOperation;
        private Label lblAudioFormat;
        private ComboBox comboAudioFormat;

        // RNNoise controls
        private CheckBox chkAudioDenoise;
        private TextBox txtAudioDenoiseModel;
        private Button btnBrowseAudioDenoiseModel;

        // Audio activity indicator gutter (prevents overlay-clipping)
        private Panel panelAudioRightGutter;

        private Button btnStartAudio;
        private Label lblAudioStatus;
        private DataGridView dgvAudioQueue;

        // Video format selector
        private Label lblVideoFormat;
        private ComboBox comboVideoFormat;

        // History panel controls
        private DataGridView dgvHistory;
        private TextBox txtHistoryLog;
        private Button btnHistoryRefresh, btnHistoryRequeue, btnHistoryOpenSrc, btnHistoryOpenOut, btnHistoryDelete, btnHistoryClearAll;

        // Metrics/progress panel fields
        private Panel progressPanel;
        private Label lblSpeed, lblSpeedValue;
        private Label lblSize, lblSizeValue;
        private Label lblFPS, lblFPSValue;
        private Label lblBitrate, lblBitrateValue;
        private Label lblTime, lblTimeValue;
        private Label lblJobTimerDesc, lblJobTimer;
        private ProgressBar progressBarEncode;
        private Button btnStopEncode;
        private CheckBox chkAutoTargetSize;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            // Menu & Status
            menuStrip1 = new MenuStrip();
            fileToolStripMenuItem = new ToolStripMenuItem();
            clearFolderHistoryToolStripMenuItem = new ToolStripMenuItem();
            modeComboBox = new ToolStripComboBox();
            toolsToolStripMenuItem = new ToolStripMenuItem();
            settingsToolStripMenuItem = new ToolStripMenuItem();
            columnSettingsToolStripMenuItem = new ToolStripMenuItem();
            helpToolStripMenuItem = new ToolStripMenuItem();
            checkForUpdatesToolStripMenuItem = new ToolStripMenuItem();
            statusStrip1 = new StatusStrip();
            toolStripStatusLabel1 = new ToolStripStatusLabel();
            exitToolStripMenuItem = new ToolStripMenuItem();

            exportQueueToolStripMenuItem = new ToolStripMenuItem();
            importQueueToolStripMenuItem = new ToolStripMenuItem();

            exportQueueToolStripMenuItem.Text = "Export Queue…";
            importQueueToolStripMenuItem.Text = "Import Queue…";

            exportQueueToolStripMenuItem.Click += exportQueueToolStripMenuItem_Click;
            importQueueToolStripMenuItem.Click += importQueueToolStripMenuItem_Click;

            // Insert above Exit (so order: Export, Import, —, Exit)
            fileToolStripMenuItem.DropDownItems.Clear();
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                exportQueueToolStripMenuItem,
                importQueueToolStripMenuItem,
                new ToolStripSeparator(),
                exitToolStripMenuItem
            });

            // Panels
            panelEncode = new Panel();

            // Encode-panel controls
            tlEncode = new TableLayoutPanel();
            lblInputFolder = new Label();
            cmbInputFolder = new ComboBox();
            btnBrowseInput = new Button();
            lblEncoderMode = new Label();
            comboEncoderMode = new ComboBox();
            lblExtensions = new Label();
            checkedListExt = new CheckedListBox();
            lblTargetSize = new Label();
            txtTargetSize = new TextBox();
            lblEncodeOutput = new Label();
            cmbEncodeOutput = new ComboBox();
            btnBrowseOutputEncode = new Button();
            btnStartEncode = new Button();
            lblEncodeStatus = new Label();
            chkDeleteSource = new CheckBox();
            dgvEncodeQueue = new DataGridView();
            btnRefreshEncode = new Button();
            btnStopEncode = new Button();
            chkAutoTargetSize = new CheckBox();
            lblCompressionProfile = new Label();
            comboCompressionProfile = new ComboBox();
            chkFilterX264 = new CheckBox();
            chkFilterX265 = new CheckBox();
            chkProcessAll = new CheckBox();

            // Audio-panel controls
            panelAudio = new Panel();
            tlAudio = new TableLayoutPanel();
            lblAudioInputFolder = new Label();
            cmbAudioInputFolder = new ComboBox();
            btnBrowseAudioInput = new Button();
            chkAudioIncludeSubfolders = new CheckBox();
            lblAudioOutputFolder = new Label();
            cmbAudioOutputFolder = new ComboBox();
            btnBrowseAudioOutput = new Button();
            lblAudioOperation = new Label();
            comboAudioOperation = new ComboBox();
            lblAudioFormat = new Label();
            comboAudioFormat = new ComboBox();

            chkAudioDenoise = new CheckBox();
            txtAudioDenoiseModel = new TextBox();
            btnBrowseAudioDenoiseModel = new Button();

            // NEW: audio quality
            lblAudioQuality = new Label();
            comboAudioQuality = new ComboBox();

            chkAudioNormalize = new CheckBox();
            comboAudioNormalizeMode = new ComboBox();
            btnStartAudio = new Button();
            lblAudioStatus = new Label();
            dgvAudioQueue = new DataGridView();

            // Metrics/progress panel fields
            progressPanel = new Panel();
            lblSpeed = new Label();
            lblSpeedValue = new Label();
            lblSize = new Label();
            lblSizeValue = new Label();
            lblFPS = new Label();
            lblFPSValue = new Label();
            lblBitrate = new Label();
            lblBitrateValue = new Label();
            lblTime = new Label();
            lblTimeValue = new Label();
            lblJobTimerDesc = new Label();
            lblJobTimer = new Label();
            progressBarEncode = new ProgressBar();

            // MenuStrip
            menuStrip1.Items.AddRange(new ToolStripItem[] {
                fileToolStripMenuItem,
                modeComboBox,
                toolsToolStripMenuItem,
                helpToolStripMenuItem
            });

            // ─── File menu ───────────────────────────────────────────────
            fileToolStripMenuItem.Text = "File";
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                exitToolStripMenuItem
            });

            // ─── Tools menu ──────────────────────────────────────────────
            toolsToolStripMenuItem.Text = "Tools";
            toolsToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                settingsToolStripMenuItem,
                columnSettingsToolStripMenuItem,
                new ToolStripSeparator(),
                clearFolderHistoryToolStripMenuItem
            });

            // ─── Other menu items ────────────────────────────────────────
            helpToolStripMenuItem.Text = "Help";
            modeComboBox.Text = "Mode";

            // ─── Clear Folder History ────────────────────────────────────
            clearFolderHistoryToolStripMenuItem.Text = "Clear Folder History";
            clearFolderHistoryToolStripMenuItem.Click += clearFolderHistoryToolStripMenuItem_Click;

            // --- Schedule Encode Start ---
            scheduleEncodeStartToolStripMenuItem = new ToolStripMenuItem();
            cancelScheduledStartToolStripMenuItem = new ToolStripMenuItem();

            scheduleEncodeStartToolStripMenuItem.Text = "Schedule Encode Start…";
            scheduleEncodeStartToolStripMenuItem.Click += scheduleEncodeStartToolStripMenuItem_Click;

            cancelScheduledStartToolStripMenuItem.Text = "Cancel Scheduled Start";
            cancelScheduledStartToolStripMenuItem.Click += cancelScheduledStartToolStripMenuItem_Click;
            cancelScheduledStartToolStripMenuItem.Enabled = false;

            // Insert into Tools menu
            toolsToolStripMenuItem.DropDownItems.Insert(2, scheduleEncodeStartToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(3, cancelScheduledStartToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(4, new ToolStripSeparator());

            // ─── Exit ────────────────────────────────────────────────────
            exitToolStripMenuItem.Text = "Exit";
            exitToolStripMenuItem.Click += exitToolStripMenuItem_Click;

            // Add "Monitor"
            modeComboBox.Items.AddRange(new object[] { "Encode", "Audio", "Monitor" });
            modeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeComboBox.SelectedIndexChanged += ModeComboBox_SelectedIndexChanged;

            toolsToolStripMenuItem.DropDownItems.Add(settingsToolStripMenuItem);
            settingsToolStripMenuItem.Text = "Settings…";
            toolsToolStripMenuItem.DropDownItems.Add(columnSettingsToolStripMenuItem);
            columnSettingsToolStripMenuItem.Text = "Columns…";

            helpToolStripMenuItem.DropDownItems.Add(checkForUpdatesToolStripMenuItem);
            checkForUpdatesToolStripMenuItem.Text = "Check for Updates…";

            // StatusStrip
            statusStrip1.Items.Add(toolStripStatusLabel1);
            toolStripStatusLabel1.Text = "Ready";

            // progressPanel setup
            progressPanel.Dock = DockStyle.Top;
            progressPanel.Height = 60;
            progressPanel.Padding = new Padding(10, 5, 10, 5);
            progressPanel.BackColor = Color.FromArgb(245, 245, 245);

            Font metricFont = new Font("Segoe UI", 9F, FontStyle.Bold);
            Font valueFont = new Font("Segoe UI", 10F, FontStyle.Regular);

            lblSpeed.Text = "Speed:";
            lblSpeed.Font = metricFont;
            lblSpeed.Location = new Point(10, 8);
            lblSpeed.AutoSize = true;
            lblSpeedValue.Text = "--";
            lblSpeedValue.Font = valueFont;
            lblSpeedValue.Location = new Point(70, 8);
            lblSpeedValue.AutoSize = true;

            lblSize.Text = "Size:";
            lblSize.Font = metricFont;
            lblSize.Location = new Point(130, 8);
            lblSize.AutoSize = true;
            lblSizeValue.Text = "--";
            lblSizeValue.Font = valueFont;
            lblSizeValue.Location = new Point(160, 8);
            lblSizeValue.AutoSize = true;

            lblFPS.Text = "FPS:";
            lblFPS.Font = metricFont;
            lblFPS.Location = new Point(260, 8);
            lblFPS.AutoSize = true;
            lblFPSValue.Text = "--";
            lblFPSValue.Font = valueFont;
            lblFPSValue.Location = new Point(290, 8);
            lblFPSValue.AutoSize = true;

            lblBitrate.Text = "Bitrate:";
            lblBitrate.Font = metricFont;
            lblBitrate.Location = new Point(350, 8);
            lblBitrate.AutoSize = true;
            lblBitrateValue.Text = "--";
            lblBitrateValue.Font = valueFont;
            lblBitrateValue.Location = new Point(400, 8);
            lblBitrateValue.AutoSize = true;

            lblTime.Text = "Time:";
            lblTime.Font = metricFont;
            lblTime.Location = new Point(500, 8);
            lblTime.AutoSize = true;
            lblTimeValue.Text = "--";
            lblTimeValue.Font = valueFont;
            lblTimeValue.Location = new Point(540, 8);
            lblTimeValue.AutoSize = true;

            lblJobTimerDesc.Text = "Elapsed:";
            lblJobTimerDesc.Font = metricFont;
            lblJobTimerDesc.Location = new Point(660, 8);
            lblJobTimerDesc.AutoSize = true;
            lblJobTimer.Text = "--:--:--";
            lblJobTimer.Font = valueFont;
            lblJobTimer.Location = new Point(720, 8);
            lblJobTimer.AutoSize = true;

            progressBarEncode.Location = new Point(10, 35);
            progressBarEncode.Width = 770;
            progressBarEncode.Height = 16;
            progressBarEncode.Minimum = 0;
            progressBarEncode.Maximum = 100;
            progressBarEncode.Value = 0;

            progressPanel.Controls.Add(lblSpeed);
            progressPanel.Controls.Add(lblSpeedValue);
            progressPanel.Controls.Add(lblSize);
            progressPanel.Controls.Add(lblSizeValue);
            progressPanel.Controls.Add(lblFPS);
            progressPanel.Controls.Add(lblFPSValue);
            progressPanel.Controls.Add(lblBitrate);
            progressPanel.Controls.Add(lblBitrateValue);
            progressPanel.Controls.Add(lblTime);
            progressPanel.Controls.Add(lblTimeValue);
            progressPanel.Controls.Add(lblJobTimerDesc);
            progressPanel.Controls.Add(lblJobTimer);
            progressPanel.Controls.Add(progressBarEncode);

            // panelEncode setup
            panelEncode.Dock = DockStyle.Fill;
            tlEncode.Dock = DockStyle.Fill;
            tlEncode.ColumnCount = 4;
            tlEncode.RowCount = 14;
            tlEncode.Padding = new Padding(10, 30, 10, 10);

            // UPDATED column layout: label | main input | secondary label | secondary input
            tlEncode.ColumnStyles.Clear();
            tlEncode.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F)); // labels
            tlEncode.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));   // main inputs
            tlEncode.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F)); // secondary label
            tlEncode.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));   // secondary inputs

            tlEncode.RowStyles.Clear();
            for (int i = 0; i < 13; i++)
                tlEncode.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlEncode.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Instantiate additional controls created dynamically earlier
            this.cmbInputFolder = new System.Windows.Forms.ComboBox();
            this.cmbEncodeOutput = new System.Windows.Forms.ComboBox();
            this.chkFilterX264 = new System.Windows.Forms.CheckBox();
            this.chkFilterX265 = new System.Windows.Forms.CheckBox();

            // Row 0: Input Folder
            lblInputFolder.Text = "Input Folder:";
            lblInputFolder.Anchor = AnchorStyles.Right;
            tlEncode.Controls.Add(lblInputFolder, 0, 0);

            cmbInputFolder.Dock = DockStyle.Fill;
            cmbInputFolder.Name = "cmbInputFolder";
            cmbInputFolder.DropDownStyle = ComboBoxStyle.DropDown;
            tlEncode.SetColumnSpan(cmbInputFolder, 2);         // spans columns 1–2
            tlEncode.Controls.Add(cmbInputFolder, 1, 0);

            btnBrowseInput.Text = "Browse…";
            btnBrowseInput.Click += btnBrowseInput_Click;
            tlEncode.Controls.Add(btnBrowseInput, 3, 0);

            // Row 1: Output Folder
            lblEncodeOutput.Text = "Output Folder:";
            lblEncodeOutput.Anchor = AnchorStyles.Right;
            tlEncode.Controls.Add(lblEncodeOutput, 0, 1);

            cmbEncodeOutput.Dock = DockStyle.Fill;
            cmbEncodeOutput.Name = "cmbEncodeOutput";
            cmbEncodeOutput.DropDownStyle = ComboBoxStyle.DropDown;
            tlEncode.SetColumnSpan(cmbEncodeOutput, 2);
            tlEncode.Controls.Add(cmbEncodeOutput, 1, 1);

            btnBrowseOutputEncode.Text = "Browse…";
            btnBrowseOutputEncode.Click += btnBrowseOutputEncode_Click;
            tlEncode.Controls.Add(btnBrowseOutputEncode, 3, 1);

            // Row 2: Encoder
            lblEncoderMode.Text = "Encoder:";
            lblEncoderMode.Anchor = AnchorStyles.Right;
            tlEncode.Controls.Add(lblEncoderMode, 0, 2);

            comboEncoderMode.Items.AddRange(new object[] { "GPU (NVENC)", "CPU (libx264)" });
            comboEncoderMode.DropDownStyle = ComboBoxStyle.DropDownList;
            comboEncoderMode.SelectedIndex = 0;
            comboEncoderMode.Dock = DockStyle.Fill;
            tlEncode.Controls.Add(comboEncoderMode, 1, 2);
            tlEncode.SetColumnSpan(comboEncoderMode, 2);

            lblVideoFormat = new Label();
            lblVideoFormat.Text = "Video Format:";
            lblVideoFormat.Anchor = AnchorStyles.Right;
            tlEncode.Controls.Add(lblVideoFormat, 0, 3);

            comboVideoFormat = new ComboBox();
            comboVideoFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            comboVideoFormat.Items.AddRange(new object[] {
                "H.265 / HEVC (x265)",
                "H.264 (x264)",
                "AV1"
            });
            comboVideoFormat.SelectedIndex = 0; // default to H.265
            comboVideoFormat.Dock = DockStyle.Fill;
            tlEncode.Controls.Add(comboVideoFormat, 1, 3);
            tlEncode.SetColumnSpan(comboVideoFormat, 2);

            // Row 4: Extensions
            lblExtensions.Text = "Extensions:";
            lblExtensions.Anchor = AnchorStyles.Right;
            tlEncode.Controls.Add(lblExtensions, 0, 4);

            checkedListExt.Width = 200;
            checkedListExt.Height = 60;
            tlEncode.Controls.Add(checkedListExt, 1, 4);
            tlEncode.SetColumnSpan(checkedListExt, 3);         // give it room

            // Row 5: Target size + Auto-target
            lblTargetSize.Text = "Target Size (MB):";
            lblTargetSize.Anchor = AnchorStyles.Right;
            tlEncode.Controls.Add(lblTargetSize, 0, 5);

            txtTargetSize.Width = 80;
            txtTargetSize.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            tlEncode.Controls.Add(txtTargetSize, 1, 5);

            chkAutoTargetSize.Text = "Auto-determine best target size";
            chkAutoTargetSize.AutoSize = true;
            chkAutoTargetSize.Checked = false;
            chkAutoTargetSize.Anchor = AnchorStyles.Left;
            chkAutoTargetSize.CheckedChanged += (s, e) => {
                txtTargetSize.Enabled = !chkAutoTargetSize.Checked;
            };
            txtTargetSize.Enabled = !chkAutoTargetSize.Checked;
            tlEncode.Controls.Add(chkAutoTargetSize, 2, 5);
            tlEncode.SetColumnSpan(chkAutoTargetSize, 2);

            // Row 6: Compression profile
            lblCompressionProfile = new Label();
            lblCompressionProfile.Text = "Quality / File Size:";
            lblCompressionProfile.Anchor = AnchorStyles.Right;
            tlEncode.Controls.Add(lblCompressionProfile, 0, 6);

            comboCompressionProfile = new ComboBox();
            comboCompressionProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            comboCompressionProfile.Items.AddRange(new object[]
            {
                "Very High Quality (Largest File)",
                "High Quality",
                "Medium Quality (Default)",
                "Low Quality (Smaller File)",
                "Very Low Quality (Smallest File)"
            });
            comboCompressionProfile.SelectedItem = "Medium Quality (Default)";
            comboCompressionProfile.Width = 180;
            tlEncode.Controls.Add(comboCompressionProfile, 1, 6);
            tlEncode.SetColumnSpan(comboCompressionProfile, 3);

            // Row 8: Delete Source + Include Subfolders
            chkDeleteSource.Text = "Delete source file after compression";
            chkDeleteSource.AutoSize = true;
            chkDeleteSource.Checked = true;
            tlEncode.SetColumnSpan(chkDeleteSource, 2);
            tlEncode.Controls.Add(chkDeleteSource, 0, 8);

            chkIncludeSubfolders = new CheckBox();
            chkIncludeSubfolders.Text = "Include subfolders";
            chkIncludeSubfolders.AutoSize = true;
            chkIncludeSubfolders.Checked = true; // default on, matches prior behavior
            tlEncode.SetColumnSpan(chkIncludeSubfolders, 2);
            tlEncode.Controls.Add(chkIncludeSubfolders, 2, 8);

            // Row 9: Buttons
            this.btnPauseQueue = new System.Windows.Forms.Button();
            this.btnPauseQueue.Name = "btnPauseQueue";
            this.btnPauseQueue.Text = "Pause Queue";
            this.btnPauseQueue.Click += new System.EventHandler(this.btnPauseQueue_Click);
            tlEncode.Controls.Add(this.btnPauseQueue, 0, 9); // column 0, row 9

            btnStartEncode.Text = "Start Encoding";
            btnStartEncode.Click += btnStartEncode_Click;
            tlEncode.Controls.Add(btnStartEncode, 1, 9);

            btnStopEncode.Text = "Stop Encoding";
            btnStopEncode.Enabled = false;
            btnStopEncode.Click += btnStopEncode_Click;
            tlEncode.Controls.Add(btnStopEncode, 2, 9);

            btnRefreshEncode.Text = "Refresh";
            btnRefreshEncode.Click += btnRefreshEncode_Click;
            tlEncode.Controls.Add(btnRefreshEncode, 3, 9);

            // Row 10: Status (full width again)
            lblEncodeStatus.Text = "";
            lblEncodeStatus.AutoSize = true;
            lblEncodeStatus.Anchor = AnchorStyles.Left;
            tlEncode.SetColumnSpan(lblEncodeStatus, 4);
            tlEncode.Controls.Add(lblEncodeStatus, 0, 10);

            // Row 11: Options group (process-all + filters), neat 2-column layout
            this.grpOptions = new GroupBox
            {
                Text = "Options",
                AutoSize = true,
                Padding = new Padding(10, 8, 10, 10),
                Anchor = AnchorStyles.Left | AnchorStyles.Right
            };

            var tlOptions = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2
            };
            tlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // ensure texts + margins are consistent
            if (chkProcessAll == null) chkProcessAll = new CheckBox();
            chkProcessAll.Text = "Process entire queue (ignore selection)";
            chkProcessAll.AutoSize = true;
            chkProcessAll.Margin = new Padding(4, 2, 12, 2);
            chkProcessAll.Checked = false;

            chkFilterX264.Text = "Show x264 / h.264";
            chkFilterX264.AutoSize = true;
            chkFilterX264.Margin = new Padding(4, 2, 12, 2);

            chkFilterX265.Text = "Show x265 / h.265";
            chkFilterX265.AutoSize = true;
            chkFilterX265.Margin = new Padding(4, 2, 12, 2);

            // add controls row-major for alignment
            tlOptions.Controls.Add(chkProcessAll, 0, 0);
            tlOptions.Controls.Add(chkFilterX264, 1, 0);
            tlOptions.Controls.Add(chkFilterX265, 1, 1);

            grpOptions.Controls.Add(tlOptions);

            // place the group across all 4 columns on row 11
            tlEncode.Controls.Add(grpOptions, 0, 11);
            tlEncode.SetColumnSpan(grpOptions, 4);

            // Row 12: Progress Panel
            tlEncode.SetColumnSpan(progressPanel, 4);
            tlEncode.Controls.Add(progressPanel, 0, 12);

            // Row 13: Encode Queue Grid
            dgvEncodeQueue = new DataGridView();
            dgvEncodeQueue.Dock = DockStyle.Fill;
            dgvEncodeQueue.Margin = new Padding(0);
            dgvEncodeQueue.AllowDrop = true;
            dgvEncodeQueue.DragEnter += dgvEncodeQueue_DragEnter;
            dgvEncodeQueue.DragDrop += dgvEncodeQueue_DragDrop;
            dgvEncodeQueue.ReadOnly = true;
            dgvEncodeQueue.AllowUserToAddRows = false;
            dgvEncodeQueue.AllowUserToDeleteRows = false;
            dgvEncodeQueue.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvEncodeQueue.MultiSelect = true;
            dgvEncodeQueue.AutoGenerateColumns = false;

            // FORCE grid scrollbars (fix missing vertical scrollbar)
            dgvEncodeQueue.ScrollBars = ScrollBars.Both;
            dgvEncodeQueue.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            dgvEncodeQueue.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dgvEncodeQueue.RowHeadersVisible = false;
            dgvEncodeQueue.AllowUserToResizeRows = false;

            dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colName",
                HeaderText = "Name",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colSize",
                HeaderText = "Size",
                Width = 100
            });
            dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colEstimatedSize",
                HeaderText = "Estimated Output",
                Width = 140
            });
            dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colCreated",
                HeaderText = "Created",
                Width = 140
            });
            dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colProgress",
                HeaderText = "Progress",
                Width = 90
            });
            dgvEncodeQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colETA",
                HeaderText = "ETA",
                Width = 90
            });

            //
            // ctxEncodeGrid
            //
            this.ctxEncodeGrid = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.mnuStartEncode = new System.Windows.Forms.ToolStripMenuItem();
            this.ctxEncodeGrid.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                this.mnuStartEncode
            });
            this.ctxEncodeGrid.Name = "ctxEncodeGrid";

            //
            // mnuStartEncode
            //
            this.mnuStartEncode.Name = "mnuStartEncode";
            this.mnuStartEncode.Size = new System.Drawing.Size(180, 22);
            this.mnuStartEncode.Text = "Start Encode";
            this.mnuStartEncode.Click += new System.EventHandler(this.StartEncodeFromContextMenu_Click);

            // Attach to grid
            this.dgvEncodeQueue.ContextMenuStrip = this.ctxEncodeGrid;

            // === History Tab ===
            var tabHistory = new TabPage();
            tabHistory.Name = "tabHistory";
            tabHistory.Text = "History";
            tabHistory.Padding = new Padding(6);

            // Split: top grid, bottom log viewer
            var splitHistory = new SplitContainer();
            splitHistory.Dock = DockStyle.Fill;
            splitHistory.Orientation = Orientation.Horizontal;
            splitHistory.SplitterDistance = 260;

            // Top: grid + toolbar
            var pnlTop = new Panel { Dock = DockStyle.Fill };
            var toolHistory = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 6, 0, 0)
            };
            btnHistoryRefresh = new Button { Text = "Refresh", Width = 90 };
            btnHistoryRequeue = new Button { Text = "Requeue", Width = 90 };
            btnHistoryOpenSrc = new Button { Text = "Open Source", Width = 110 };
            btnHistoryOpenOut = new Button { Text = "Open Output", Width = 110 };
            btnHistoryDelete = new Button { Text = "Delete Selected", Width = 130 };
            btnHistoryClearAll = new Button { Text = "Clear All", Width = 90 };

            btnHistoryRefresh.Click += btnHistoryRefresh_Click;
            btnHistoryRequeue.Click += btnHistoryRequeue_Click;
            btnHistoryOpenSrc.Click += btnHistoryOpenSrc_Click;
            btnHistoryOpenOut.Click += btnHistoryOpenOut_Click;
            btnHistoryDelete.Click += btnHistoryDelete_Click;
            btnHistoryClearAll.Click += btnHistoryClearAll_Click;

            toolHistory.Controls.AddRange(new Control[] {
                btnHistoryRefresh, btnHistoryRequeue, btnHistoryOpenSrc,
                btnHistoryOpenOut, btnHistoryDelete, btnHistoryClearAll
            });

            // Grid
            dgvHistory = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoGenerateColumns = false,
                MultiSelect = true
            };
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { Name = "colH_When", HeaderText = "Finished (Local)", Width = 155 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { Name = "colH_Type", HeaderText = "Type", Width = 80 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { Name = "colH_Status", HeaderText = "Status", Width = 80 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { Name = "colH_Source", HeaderText = "Source", Width = 260 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { Name = "colH_Output", HeaderText = "Output", Width = 260 });
            dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { Name = "colH_Notes", HeaderText = "Notes", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            dgvHistory.SelectionChanged += dgvHistory_SelectionChanged;

            // Build top panel
            pnlTop.Controls.Add(dgvHistory);
            pnlTop.Controls.Add(toolHistory);
            splitHistory.Panel1.Controls.Add(pnlTop);

            // Bottom: log textbox
            txtHistoryLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false
            };
            splitHistory.Panel2.Controls.Add(txtHistoryLog);

            // Add split to tab, tab to TabControl
            tabHistory.Controls.Add(splitHistory);
            var tabs = this.Controls.OfType<TabControl>().FirstOrDefault();
            if (tabs != null)
                tabs.TabPages.Add(tabHistory);

            var cms = new ContextMenuStrip();
            cms.Items.Add("Start", null, StartEncodeFromContextMenu_Click);
            cms.Items.Add("Add to Encoding Queue", null, AddToEncodeQueueFromContextMenu_Click);
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add("Remove Selected", null, RemoveSelectedRows_Click);
            cms.Items.Add("Clear Grid", null, ClearGrid_Click);
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add("Rename File…", null, RenameFile_Click);
            cms.Items.Add("Open Location", null, OpenLocationFromContextMenu_Click);
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add("Schedule Start…", null, ScheduleEncode_Click);
            dgvEncodeQueue.ContextMenuStrip = cms;

            tlEncode.SetColumnSpan(dgvEncodeQueue, 4);
            tlEncode.Controls.Add(dgvEncodeQueue, 0, 13);

            panelEncode.Controls.Add(tlEncode);

            // ───────── panelAudio setup ─────────
            panelAudio.Dock = DockStyle.Fill;

            tlAudio.Dock = DockStyle.Fill;
            // NOTE: The activity indicator (gear) is positioned top-right and can overlay
            // the audio layout. We reserve a fixed-width gutter column on the right so
            // Browse buttons and drop-down arrows are never obscured.
            tlAudio.ColumnCount = 4;
            tlAudio.RowCount = 11;
            tlAudio.Padding = new Padding(10, 30, 10, 10);
            tlAudio.GrowStyle = TableLayoutPanelGrowStyle.AddRows;

            tlAudio.ColumnStyles.Clear();
            tlAudio.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F));
            tlAudio.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tlAudio.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
            tlAudio.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170F)); // right gutter for activity indicator

            panelAudioRightGutter = new Panel
            {
                Name = "panelAudioRightGutter",
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            tlAudio.Controls.Add(panelAudioRightGutter, 3, 0);
            tlAudio.SetRowSpan(panelAudioRightGutter, 3);            

            // Row 0: Input folder            
            lblAudioInputFolder.Text = "Input Folder:";
            lblAudioInputFolder.Anchor = AnchorStyles.Right;
            tlAudio.Controls.Add(lblAudioInputFolder, 0, 0);

            cmbAudioInputFolder.Name = "cmbAudioInputFolder";
            cmbAudioInputFolder.Dock = DockStyle.Fill;
            cmbAudioInputFolder.DropDownStyle = ComboBoxStyle.DropDownList;
            tlAudio.Controls.Add(cmbAudioInputFolder, 1, 0);

            btnBrowseAudioInput.Text = "Browse…";
            btnBrowseAudioInput.AutoSize = false;
            btnBrowseAudioInput.Dock = DockStyle.Fill;
            btnBrowseAudioInput.Margin = new Padding(6, 3, 0, 3);
            btnBrowseAudioInput.Click += btnBrowseAudioInput_Click;
            tlAudio.Controls.Add(btnBrowseAudioInput, 2, 0);

            // Row 1: Include subfolders            
            chkAudioIncludeSubfolders.Text = "Include subfolders";
            chkAudioIncludeSubfolders.AutoSize = true;
            chkAudioIncludeSubfolders.Checked = true;
            tlAudio.Controls.Add(chkAudioIncludeSubfolders, 1, 1);
            tlAudio.SetColumnSpan(chkAudioIncludeSubfolders, 2);

            // Row 2: Output folder            
            lblAudioOutputFolder.Text = "Output Folder:";
            lblAudioOutputFolder.Anchor = AnchorStyles.Right;
            tlAudio.Controls.Add(lblAudioOutputFolder, 0, 2);

            cmbAudioOutputFolder.Name = "cmbAudioOutputFolder";
            cmbAudioOutputFolder.Dock = DockStyle.Fill;
            tlAudio.Controls.Add(cmbAudioOutputFolder, 1, 2);

            btnBrowseAudioOutput.Text = "Browse…";
            btnBrowseAudioOutput.AutoSize = false;
            btnBrowseAudioOutput.Dock = DockStyle.Fill;
            btnBrowseAudioOutput.Margin = new Padding(6, 3, 0, 3);
            btnBrowseAudioOutput.Click += btnBrowseAudioOutput_Click;
            tlAudio.Controls.Add(btnBrowseAudioOutput, 2, 2);

            // Row 3: Operation
            lblAudioOperation.Text = "Operation:";
            lblAudioOperation.Anchor = AnchorStyles.Right;
            tlAudio.Controls.Add(lblAudioOperation, 0, 3);

            comboAudioOperation.DropDownStyle = ComboBoxStyle.DropDownList;
            comboAudioOperation.Dock = DockStyle.Fill;
            comboAudioOperation.Items.AddRange(new object[]
            {
                "Extract (no re-encode)",
                "Convert"
            });
            comboAudioOperation.SelectedIndex = 0;
            tlAudio.Controls.Add(comboAudioOperation, 1, 3);
            tlAudio.SetColumnSpan(comboAudioOperation, 3);
            comboAudioOperation.SelectedIndexChanged += comboAudioOperation_SelectedIndexChanged;

            // Row 4: Format
            lblAudioFormat.Text = "Format:";
            lblAudioFormat.Anchor = AnchorStyles.Right;
            tlAudio.Controls.Add(lblAudioFormat, 0, 4);

            comboAudioFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            comboAudioFormat.Dock = DockStyle.Fill;
            comboAudioFormat.Items.AddRange(new object[]
            {
                "Same as source (extract only)",
                "AAC (m4a)",
                "MP3",
                "FLAC",
                "Opus",
                "WAV",
                "AC3",
                "E-AC3"
            });
            comboAudioFormat.SelectedIndex = 0;
            tlAudio.Controls.Add(comboAudioFormat, 1, 4);
            tlAudio.SetColumnSpan(comboAudioFormat, 3);

            // NEW Row 5: Quality
            lblAudioQuality.Text = "Quality:";
            lblAudioQuality.Anchor = AnchorStyles.Right;
            tlAudio.Controls.Add(lblAudioQuality, 0, 5);

            comboAudioQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            comboAudioQuality.Dock = DockStyle.Fill;
            comboAudioQuality.Items.AddRange(new object[]
            {
                "Auto",
                "Very Low",
                "Low",
                "Medium",
                "High",
                "Very High"
            });
            // Default to Medium
            comboAudioQuality.SelectedIndex = 3;
            tlAudio.Controls.Add(comboAudioQuality, 1, 5);
            tlAudio.SetColumnSpan(comboAudioQuality, 3);

            // Row 6: Normalize (checkbox + mode)
            chkAudioNormalize.Text = "Normalize loudness";
            chkAudioNormalize.AutoSize = true;
            chkAudioNormalize.Checked = false;
            chkAudioNormalize.CheckedChanged += chkAudioNormalize_CheckedChanged;
            tlAudio.Controls.Add(chkAudioNormalize, 1, 6);

            comboAudioNormalizeMode.DropDownStyle = ComboBoxStyle.DropDownList;
            comboAudioNormalizeMode.Dock = DockStyle.Fill;
            comboAudioNormalizeMode.Items.AddRange(new object[]
            {
                "Single-pass (fast)",
                "Two-pass (accurate)"
            });
            comboAudioNormalizeMode.SelectedIndex = 0;
            tlAudio.Controls.Add(comboAudioNormalizeMode, 2, 6);
            tlAudio.SetColumnSpan(comboAudioNormalizeMode, 2);

            // Row 7: RNNoise denoise
            chkAudioDenoise.Text = "Apply RNNoise denoising";
            chkAudioDenoise.AutoSize = true;
            chkAudioDenoise.Checked = false;
            chkAudioDenoise.CheckedChanged += chkAudioDenoise_CheckedChanged;
            tlAudio.Controls.Add(chkAudioDenoise, 1, 7);
            tlAudio.SetColumnSpan(chkAudioDenoise, 3);

            var lblAudioDenoiseModel = new Label();
            lblAudioDenoiseModel.Text = "RNNoise model file:";
            lblAudioDenoiseModel.Anchor = AnchorStyles.Right;
            tlAudio.Controls.Add(lblAudioDenoiseModel, 0, 8);

            txtAudioDenoiseModel.Dock = DockStyle.Fill;
            tlAudio.Controls.Add(txtAudioDenoiseModel, 1, 8);
            tlAudio.SetColumnSpan(txtAudioDenoiseModel, 2);

            btnBrowseAudioDenoiseModel.Text = "Browse…";
            btnBrowseAudioDenoiseModel.AutoSize = false;
            btnBrowseAudioDenoiseModel.Dock = DockStyle.Fill;
            btnBrowseAudioDenoiseModel.Margin = new Padding(6, 3, 0, 3);
            btnBrowseAudioDenoiseModel.Click += btnBrowseAudioDenoiseModel_Click;
            tlAudio.Controls.Add(btnBrowseAudioDenoiseModel, 3, 8);

            // Row 9: Start + status
            btnStartAudio.Text = "Start Audio Jobs";
            btnStartAudio.Width = 160;
            btnStartAudio.Anchor = AnchorStyles.Left;
            btnStartAudio.Click += btnStartAudio_Click;
            // Align primary action with the inputs column for a more professional layout.
            tlAudio.Controls.Add(btnStartAudio, 1, 9);

            lblAudioStatus.AutoSize = true;
            lblAudioStatus.Anchor = AnchorStyles.Left;
            tlAudio.Controls.Add(lblAudioStatus, 2, 9);
            tlAudio.SetColumnSpan(lblAudioStatus, 2);

            // Row 8: Audio queue grid
            dgvAudioQueue.Dock = DockStyle.Fill;
            dgvAudioQueue.MinimumSize = new Size(0, 250);
            dgvAudioQueue.ReadOnly = true;
            dgvAudioQueue.AllowUserToAddRows = false;
            dgvAudioQueue.AllowUserToDeleteRows = false;
            dgvAudioQueue.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvAudioQueue.MultiSelect = true;
            dgvAudioQueue.AutoGenerateColumns = false;

            dgvAudioQueue.AllowDrop = true;
            dgvAudioQueue.DragEnter += dgvAudioQueue_DragEnter;
            dgvAudioQueue.DragDrop += dgvAudioQueue_DragDrop;

            dgvAudioQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colAudioName",
                HeaderText = "Name",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            dgvAudioQueue.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colAudioPath",
                HeaderText = "Path",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });

            // Update row styles to account for the extra row
            tlAudio.RowStyles.Clear();
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 0
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 1
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 2
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 3
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 4
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 5 (Quality)
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 6 (Normalize)
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 7 (RNNoise toggle)
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 8 (RNNoise model path)
            tlAudio.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 9 (Start + status)
            tlAudio.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // 10 (grid grows)

            tlAudio.Controls.Add(dgvAudioQueue, 0, 10);
            tlAudio.SetColumnSpan(dgvAudioQueue, 4);

            panelAudio.Controls.Add(tlAudio);

            // ───────── panelMonitor setup (as in your current file) ─────────
            panelMonitor = new Panel();
            panelMonitor.Dock = DockStyle.Fill;

            tlMonitor = new TableLayoutPanel();
            tlMonitor.Dock = DockStyle.Fill;
            tlMonitor.ColumnCount = 4;
            tlMonitor.RowCount = 9;                       // explicit rows for tidy layout
            tlMonitor.Padding = new Padding(10, 30, 10, 10);
            tlMonitor.GrowStyle = TableLayoutPanelGrowStyle.AddRows;

            // Columns: label | input | label | input
            tlMonitor.ColumnStyles.Clear();
            tlMonitor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));  // labels L
            tlMonitor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));    // inputs L
            tlMonitor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F));  // labels R
            tlMonitor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));    // inputs R

            // helpful autosize rows (top rows hug content, list grows)
            tlMonitor.RowStyles.Clear();
            for (int i = 0; i < 8; i++) tlMonitor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlMonitor.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // last row for list

            // Row 0: Folder picker
            lblMonFolder = new Label { Text = "Folder to monitor:", Anchor = AnchorStyles.Right, AutoSize = true };
            cmbMonFolder = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, Name = "cmbMonFolder" };
            btnBrowseMonFolder = new Button { Text = "Browse…" };
            btnBrowseMonFolder.Click += btnBrowseMonFolder_Click;

            tlMonitor.Controls.Add(lblMonFolder, 0, 0);
            tlMonitor.Controls.Add(cmbMonFolder, 1, 0);
            tlMonitor.SetColumnSpan(cmbMonFolder, 2);     // span across cols 1–2 for a wider input
            tlMonitor.Controls.Add(btnBrowseMonFolder, 3, 0);

            // Row 1: Options (left/right)
            chkMonIncludeSubfolders = new CheckBox { Text = "Include subfolders", AutoSize = true, Checked = true };
            chkMonAutoStart = new CheckBox { Text = "Auto-start encode on new files", AutoSize = true, Checked = true };

            tlMonitor.Controls.Add(new Label() { AutoSize = true }, 0, 1); // spacer (keeps grid symmetric)
            tlMonitor.Controls.Add(chkMonIncludeSubfolders, 1, 1);
            tlMonitor.Controls.Add(new Label() { AutoSize = true }, 2, 1); // spacer
            tlMonitor.Controls.Add(chkMonAutoStart, 3, 1);

            // Row 2: Use Encode filters (full width)
            chkMonUseEncodeFilters = new CheckBox { Text = "Use current Encode filters/options", AutoSize = true, Checked = true };
            tlMonitor.Controls.Add(chkMonUseEncodeFilters, 1, 2);
            tlMonitor.SetColumnSpan(chkMonUseEncodeFilters, 3);

            // Row 3: Watcher toggle (full width)
            chkMonUseWatcher = new CheckBox { Text = "React instantly (use FileSystemWatcher)", AutoSize = true, Checked = true };
            tlMonitor.Controls.Add(chkMonUseWatcher, 1, 3);
            tlMonitor.SetColumnSpan(chkMonUseWatcher, 3);

            // Row 4: Interval + Stabilize (same line, left/right)
            lblMonInterval = new Label { Text = "Scan every (minutes):", Anchor = AnchorStyles.Right, AutoSize = true };
            nudMonMinutes = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 2, Width = 80, Anchor = AnchorStyles.Left };

            lblMonStabilize = new Label { Text = "Stabilize (sec):", Anchor = AnchorStyles.Right, AutoSize = true };
            nudMonStabilizeSec = new NumericUpDown { Minimum = 0, Maximum = 3600, Value = 60, Width = 80, Anchor = AnchorStyles.Left };

            tlMonitor.Controls.Add(lblMonInterval, 0, 4);
            tlMonitor.Controls.Add(nudMonMinutes, 1, 4);
            tlMonitor.Controls.Add(lblMonStabilize, 2, 4);
            tlMonitor.Controls.Add(nudMonStabilizeSec, 3, 4);

            // Row 5: Min size (use right pair; leave left pair empty for visual balance)
            lblMonMinSize = new Label { Text = "Min size (MB):", Anchor = AnchorStyles.Right, AutoSize = true };
            nudMonMinSizeMb = new NumericUpDown { Minimum = 0, Maximum = 102400, Value = 10, Width = 80, Anchor = AnchorStyles.Left };

            tlMonitor.Controls.Add(new Label() { AutoSize = true }, 0, 5); // spacer left label
            tlMonitor.Controls.Add(new Label() { AutoSize = true }, 1, 5); // spacer left input
            tlMonitor.Controls.Add(lblMonMinSize, 2, 5);
            tlMonitor.Controls.Add(nudMonMinSizeMb, 3, 5);

            // Row 6: Buttons (aligned under inputs)
            btnMonStart = new Button { Text = "Start Monitoring", Width = 140 };
            btnMonStop = new Button { Text = "Stop", Width = 100, Enabled = false };
            btnMonScanNow = new Button { Text = "Scan Now", Width = 100 };
            btnMonStart.Click += btnMonStart_Click;
            btnMonStop.Click += btnMonStop_Click;
            btnMonScanNow.Click += btnMonScanNow_Click;

            tlMonitor.Controls.Add(new Label() { AutoSize = true }, 0, 6); // spacer label col
            tlMonitor.Controls.Add(btnMonStart, 1, 6);
            tlMonitor.Controls.Add(btnMonStop, 2, 6);
            tlMonitor.Controls.Add(btnMonScanNow, 3, 6);

            // Row 7: Status (full width)
            lblMonStatus = new Label { Text = "Idle", AutoSize = true, Anchor = AnchorStyles.Left };
            tlMonitor.Controls.Add(lblMonStatus, 0, 7);
            tlMonitor.SetColumnSpan(lblMonStatus, 4);

            // Row 8: Recent discoveries list (fills remaining area)
            panelMonQueueHost = new Panel { Dock = DockStyle.Fill, Name = "panelMonQueueHost" };
            tlMonitor.Controls.Add(panelMonQueueHost, 0, 8);
            tlMonitor.SetColumnSpan(panelMonQueueHost, 4);

            // Added controls
            listMonLastFound = new ListBox
            {
                Dock = DockStyle.Fill,
                Name = "listMonLastFound"
            };

            panelMonQueueHost.Controls.Add(listMonLastFound);
            panelMonitor.Controls.Add(tlMonitor);

            // Add to form (keep existing adds for other panels)
            Controls.Add(panelMonitor);
            Controls.Add(menuStrip1);
            Controls.Add(panelAudio);
            Controls.Add(panelEncode);
            Controls.Add(statusStrip1);

            MainMenuStrip = menuStrip1;

            Text = "GoEncode v0.5.7";
            ClientSize = new Size(800, 760);
        }
    }
}
