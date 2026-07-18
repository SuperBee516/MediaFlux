namespace MediaFlux
{
    partial class SettingsForm
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.Label lblUpdateFolder;
        private System.Windows.Forms.TextBox txtUpdateFolder;
        private System.Windows.Forms.Button btnBrowseUpdate;

        private System.Windows.Forms.Label lblPattern;
        private System.Windows.Forms.TextBox txtPattern;

        private System.Windows.Forms.Label lblSuffix;
        private System.Windows.Forms.TextBox txtSuffix;
        private System.Windows.Forms.CheckBox chkEnableSuffix;
        private System.Windows.Forms.CheckBox chkEnableCodecSuffix;

        private System.Windows.Forms.GroupBox grpExtensions;
        private System.Windows.Forms.CheckedListBox lstSupportedExts;
        private System.Windows.Forms.Label lblNewExt;
        private System.Windows.Forms.TextBox txtNewExt;
        private System.Windows.Forms.Button btnAddExt;
        private System.Windows.Forms.Button btnRemoveExt;
        private System.Windows.Forms.Button btnResetExts;
        private System.Windows.Forms.Label lblExtHint;

        private System.Windows.Forms.CheckBox chkRememberCheckboxes;
        private System.Windows.Forms.CheckBox chkPreventSleepDuringEncoding;
        private System.Windows.Forms.CheckBox chkLimitGpuEncodingQueueToOneJob;
        private System.Windows.Forms.GroupBox grpIncompleteOutputCleanup;
        private System.Windows.Forms.CheckBox chkDeleteFailedEncodeOutputs;
        private System.Windows.Forms.CheckBox chkDeleteCanceledEncodeOutputs;
        private System.Windows.Forms.Label lblLargeQueueThreshold;
        private System.Windows.Forms.NumericUpDown nudLargeQueueThreshold;
        private System.Windows.Forms.CheckBox chkAutoAnalyzeLargeQueues;
        private System.Windows.Forms.CheckBox chkEnablePersistentMediaInfoCache;
        private System.Windows.Forms.Label lblFfmpegPath;
        private System.Windows.Forms.TextBox txtFfmpegPath;
        private System.Windows.Forms.Button btnBrowseFfmpeg;
        private System.Windows.Forms.Label lblFfprobePath;
        private System.Windows.Forms.TextBox txtFfprobePath;
        private System.Windows.Forms.Button btnBrowseFfprobe;

        private System.Windows.Forms.GroupBox grpWatchFolder;
        private System.Windows.Forms.Label lblWatchFolderPath;
        private System.Windows.Forms.TextBox txtWatchFolderPath;
        private System.Windows.Forms.Button btnBrowseWatchFolder;
        private System.Windows.Forms.Label lblWatchInterval;
        private System.Windows.Forms.NumericUpDown nudWatchInterval;
        private System.Windows.Forms.CheckBox chkWatchIncludeSubfolders;
        private System.Windows.Forms.CheckBox chkHideWatchFolderStatusText;
        private System.Windows.Forms.Label lblWatchStabilization;
        private System.Windows.Forms.NumericUpDown nudWatchStabilization;
        private System.Windows.Forms.Label lblWatchHint;

        private System.Windows.Forms.GroupBox grpDiscordNotification;
        private System.Windows.Forms.CheckBox chkDiscordNotification;
        private System.Windows.Forms.Label lblDiscordWebhookUrl;
        private System.Windows.Forms.TextBox txtDiscordWebhookUrl;
        private System.Windows.Forms.CheckBox chkShowDiscordWebhook;
        private System.Windows.Forms.Label lblDiscordUserMentionId;
        private System.Windows.Forms.TextBox txtDiscordUserMentionId;
        private System.Windows.Forms.Label lblDiscordUserMentionHint;
        private System.Windows.Forms.Label lblDiscordMessage;
        private System.Windows.Forms.TextBox txtDiscordMessage;
        private System.Windows.Forms.Label lblDiscordPlaceholders;
        private System.Windows.Forms.Button btnTestDiscordWebhook;

        private System.Windows.Forms.GroupBox grpBackupRestore;
        private System.Windows.Forms.CheckBox chkBackupBeforeUpdates;
        private System.Windows.Forms.Label lblBackupFolder;
        private System.Windows.Forms.TextBox txtBackupFolder;
        private System.Windows.Forms.Button btnBrowseBackupFolder;
        private System.Windows.Forms.Label lblBackupsToKeep;
        private System.Windows.Forms.NumericUpDown nudBackupsToKeep;
        private System.Windows.Forms.Button btnBackupNow;
        private System.Windows.Forms.Button btnRestoreBackup;
        private System.Windows.Forms.Label lblBackupHint;

        private System.Windows.Forms.GroupBox grpDuplicateManagement;
        private System.Windows.Forms.CheckBox chkFindDuplicatesOnImport;
        private System.Windows.Forms.CheckBox chkOnlyQueueDuplicateCandidates;
        private System.Windows.Forms.Label lblDuplicateScanMode;
        private System.Windows.Forms.ComboBox comboDuplicateScanMode;
        private System.Windows.Forms.Label lblDuplicateReferenceFolder;
        private System.Windows.Forms.TextBox txtDuplicateReferenceFolder;
        private System.Windows.Forms.Button btnBrowseDuplicateReferenceFolder;
        private System.Windows.Forms.Label lblDuplicateQuarantineFolder;
        private System.Windows.Forms.TextBox txtDuplicateQuarantineFolder;
        private System.Windows.Forms.Button btnBrowseDuplicateQuarantineFolder;
        private System.Windows.Forms.CheckBox chkEnableDuplicateSignatureCache;
        private System.Windows.Forms.CheckBox chkAllowDuplicateRecycleBin;
        private System.Windows.Forms.CheckBox chkAllowDuplicateQuarantine;
        private System.Windows.Forms.CheckBox chkAllowDuplicatePermanentDelete;
        private System.Windows.Forms.CheckBox chkRequireDuplicateCleanupConfirmation;
        private System.Windows.Forms.CheckBox chkShowDuplicateReferenceFolderOnMain;
        private System.Windows.Forms.Label lblDuplicateCleanupActions;
        private System.Windows.Forms.Button btnClearDuplicateSignatureCache;
        private System.Windows.Forms.Button btnClearDuplicatePreviewCache;
        private System.Windows.Forms.Label lblDuplicateManagementHint;

        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Button btnCancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.lblUpdateFolder = new System.Windows.Forms.Label();
            this.txtUpdateFolder = new System.Windows.Forms.TextBox();
            this.btnBrowseUpdate = new System.Windows.Forms.Button();
            this.lblPattern = new System.Windows.Forms.Label();
            this.txtPattern = new System.Windows.Forms.TextBox();
            this.lblSuffix = new System.Windows.Forms.Label();
            this.txtSuffix = new System.Windows.Forms.TextBox();
            this.chkEnableSuffix = new System.Windows.Forms.CheckBox();
            this.chkEnableCodecSuffix = new System.Windows.Forms.CheckBox();
            this.grpExtensions = new System.Windows.Forms.GroupBox();
            this.lstSupportedExts = new System.Windows.Forms.CheckedListBox();
            this.lblNewExt = new System.Windows.Forms.Label();
            this.txtNewExt = new System.Windows.Forms.TextBox();
            this.btnAddExt = new System.Windows.Forms.Button();
            this.btnRemoveExt = new System.Windows.Forms.Button();
            this.btnResetExts = new System.Windows.Forms.Button();
            this.lblExtHint = new System.Windows.Forms.Label();
            this.chkRememberCheckboxes = new System.Windows.Forms.CheckBox();
            this.chkPreventSleepDuringEncoding = new System.Windows.Forms.CheckBox();
            this.chkLimitGpuEncodingQueueToOneJob = new System.Windows.Forms.CheckBox();
            this.grpIncompleteOutputCleanup = new System.Windows.Forms.GroupBox();
            this.chkDeleteFailedEncodeOutputs = new System.Windows.Forms.CheckBox();
            this.chkDeleteCanceledEncodeOutputs = new System.Windows.Forms.CheckBox();
            this.lblLargeQueueThreshold = new System.Windows.Forms.Label();
            this.nudLargeQueueThreshold = new System.Windows.Forms.NumericUpDown();
            this.chkAutoAnalyzeLargeQueues = new System.Windows.Forms.CheckBox();
            this.chkEnablePersistentMediaInfoCache = new System.Windows.Forms.CheckBox();
            this.lblFfmpegPath = new System.Windows.Forms.Label();
            this.txtFfmpegPath = new System.Windows.Forms.TextBox();
            this.btnBrowseFfmpeg = new System.Windows.Forms.Button();
            this.lblFfprobePath = new System.Windows.Forms.Label();
            this.txtFfprobePath = new System.Windows.Forms.TextBox();
            this.btnBrowseFfprobe = new System.Windows.Forms.Button();
            this.grpWatchFolder = new System.Windows.Forms.GroupBox();
            this.lblWatchFolderPath = new System.Windows.Forms.Label();
            this.txtWatchFolderPath = new System.Windows.Forms.TextBox();
            this.btnBrowseWatchFolder = new System.Windows.Forms.Button();
            this.lblWatchInterval = new System.Windows.Forms.Label();
            this.nudWatchInterval = new System.Windows.Forms.NumericUpDown();
            this.chkWatchIncludeSubfolders = new System.Windows.Forms.CheckBox();
            this.chkHideWatchFolderStatusText = new System.Windows.Forms.CheckBox();
            this.lblWatchStabilization = new System.Windows.Forms.Label();
            this.nudWatchStabilization = new System.Windows.Forms.NumericUpDown();
            this.lblWatchHint = new System.Windows.Forms.Label();
            this.grpDiscordNotification = new System.Windows.Forms.GroupBox();
            this.chkDiscordNotification = new System.Windows.Forms.CheckBox();
            this.lblDiscordWebhookUrl = new System.Windows.Forms.Label();
            this.txtDiscordWebhookUrl = new System.Windows.Forms.TextBox();
            this.chkShowDiscordWebhook = new System.Windows.Forms.CheckBox();
            this.lblDiscordUserMentionId = new System.Windows.Forms.Label();
            this.txtDiscordUserMentionId = new System.Windows.Forms.TextBox();
            this.lblDiscordUserMentionHint = new System.Windows.Forms.Label();
            this.lblDiscordMessage = new System.Windows.Forms.Label();
            this.txtDiscordMessage = new System.Windows.Forms.TextBox();
            this.lblDiscordPlaceholders = new System.Windows.Forms.Label();
            this.btnTestDiscordWebhook = new System.Windows.Forms.Button();
            this.grpBackupRestore = new System.Windows.Forms.GroupBox();
            this.chkBackupBeforeUpdates = new System.Windows.Forms.CheckBox();
            this.lblBackupFolder = new System.Windows.Forms.Label();
            this.txtBackupFolder = new System.Windows.Forms.TextBox();
            this.btnBrowseBackupFolder = new System.Windows.Forms.Button();
            this.lblBackupsToKeep = new System.Windows.Forms.Label();
            this.nudBackupsToKeep = new System.Windows.Forms.NumericUpDown();
            this.btnBackupNow = new System.Windows.Forms.Button();
            this.btnRestoreBackup = new System.Windows.Forms.Button();
            this.lblBackupHint = new System.Windows.Forms.Label();
            this.grpDuplicateManagement = new System.Windows.Forms.GroupBox();
            this.chkFindDuplicatesOnImport = new System.Windows.Forms.CheckBox();
            this.chkOnlyQueueDuplicateCandidates = new System.Windows.Forms.CheckBox();
            this.lblDuplicateScanMode = new System.Windows.Forms.Label();
            this.comboDuplicateScanMode = new System.Windows.Forms.ComboBox();
            this.lblDuplicateReferenceFolder = new System.Windows.Forms.Label();
            this.txtDuplicateReferenceFolder = new System.Windows.Forms.TextBox();
            this.btnBrowseDuplicateReferenceFolder = new System.Windows.Forms.Button();
            this.lblDuplicateQuarantineFolder = new System.Windows.Forms.Label();
            this.txtDuplicateQuarantineFolder = new System.Windows.Forms.TextBox();
            this.btnBrowseDuplicateQuarantineFolder = new System.Windows.Forms.Button();
            this.chkEnableDuplicateSignatureCache = new System.Windows.Forms.CheckBox();
            this.chkAllowDuplicateRecycleBin = new System.Windows.Forms.CheckBox();
            this.chkAllowDuplicateQuarantine = new System.Windows.Forms.CheckBox();
            this.chkAllowDuplicatePermanentDelete = new System.Windows.Forms.CheckBox();
            this.chkRequireDuplicateCleanupConfirmation = new System.Windows.Forms.CheckBox();
            this.chkShowDuplicateReferenceFolderOnMain = new System.Windows.Forms.CheckBox();
            this.lblDuplicateCleanupActions = new System.Windows.Forms.Label();
            this.btnClearDuplicateSignatureCache = new System.Windows.Forms.Button();
            this.btnClearDuplicatePreviewCache = new System.Windows.Forms.Button();
            this.lblDuplicateManagementHint = new System.Windows.Forms.Label();
            this.btnOK = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.grpExtensions.SuspendLayout();
            this.grpIncompleteOutputCleanup.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudLargeQueueThreshold)).BeginInit();
            this.grpWatchFolder.SuspendLayout();
            this.grpDiscordNotification.SuspendLayout();
            this.grpBackupRestore.SuspendLayout();
            this.grpDuplicateManagement.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudBackupsToKeep)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudWatchInterval)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudWatchStabilization)).BeginInit();
            this.SuspendLayout();
            // 
            // lblUpdateFolder
            // 
            this.lblUpdateFolder.AutoSize = true;
            this.lblUpdateFolder.Location = new System.Drawing.Point(12, 15);
            this.lblUpdateFolder.Name = "lblUpdateFolder";
            this.lblUpdateFolder.Size = new System.Drawing.Size(116, 15);
            this.lblUpdateFolder.TabIndex = 0;
            this.lblUpdateFolder.Text = "Update Folder Path:";
            // 
            // txtUpdateFolder
            // 
            this.txtUpdateFolder.Location = new System.Drawing.Point(15, 35);
            this.txtUpdateFolder.Name = "txtUpdateFolder";
            this.txtUpdateFolder.Size = new System.Drawing.Size(300, 23);
            this.txtUpdateFolder.TabIndex = 1;
            // 
            // btnBrowseUpdate
            // 
            this.btnBrowseUpdate.Location = new System.Drawing.Point(320, 34);
            this.btnBrowseUpdate.Name = "btnBrowseUpdate";
            this.btnBrowseUpdate.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseUpdate.TabIndex = 2;
            this.btnBrowseUpdate.Text = "Browse…";
            this.btnBrowseUpdate.UseVisualStyleBackColor = true;
            this.btnBrowseUpdate.Click += new System.EventHandler(this.btnBrowseUpdate_Click);
            // 
            // lblPattern
            // 
            this.lblPattern.AutoSize = true;
            this.lblPattern.Location = new System.Drawing.Point(12, 70);
            this.lblPattern.Name = "lblPattern";
            this.lblPattern.Size = new System.Drawing.Size(127, 15);
            this.lblPattern.TabIndex = 3;
            this.lblPattern.Text = "Auto-Naming Pattern:";
            // 
            // txtPattern
            // 
            this.txtPattern.Location = new System.Drawing.Point(15, 90);
            this.txtPattern.Name = "txtPattern";
            this.txtPattern.Size = new System.Drawing.Size(300, 23);
            this.txtPattern.TabIndex = 4;
            // 
            // lblSuffix
            // 
            this.lblSuffix.AutoSize = true;
            this.lblSuffix.Location = new System.Drawing.Point(12, 175);
            this.lblSuffix.Name = "lblSuffix";
            this.lblSuffix.Size = new System.Drawing.Size(137, 15);
            this.lblSuffix.TabIndex = 6;
            this.lblSuffix.Text = "Output Filename Suffix:";
            // 
            // txtSuffix
            // 
            this.txtSuffix.Location = new System.Drawing.Point(15, 195);
            this.txtSuffix.Name = "txtSuffix";
            this.txtSuffix.Size = new System.Drawing.Size(100, 23);
            this.txtSuffix.TabIndex = 7;
            // 
            // chkEnableSuffix
            // 
            this.chkEnableSuffix.AutoSize = true;
            this.chkEnableSuffix.Location = new System.Drawing.Point(15, 130);
            this.chkEnableSuffix.Name = "chkEnableSuffix";
            this.chkEnableSuffix.Size = new System.Drawing.Size(187, 19);
            this.chkEnableSuffix.TabIndex = 5;
            this.chkEnableSuffix.Text = "Enable output filename suffix";
            this.chkEnableSuffix.UseVisualStyleBackColor = true;
            this.chkEnableSuffix.CheckedChanged += new System.EventHandler(this.chkEnableSuffix_CheckedChanged);
            // chkEnableCodecSuffix
            // 
            this.chkEnableCodecSuffix.AutoSize = true;
            this.chkEnableCodecSuffix.Location = new System.Drawing.Point(15, 150);
            this.chkEnableCodecSuffix.Name = "chkEnableCodecSuffix";
            this.chkEnableCodecSuffix.Size = new System.Drawing.Size(231, 19);
            this.chkEnableCodecSuffix.TabIndex = 6;
            this.chkEnableCodecSuffix.Text = "Enable codec suffix on output files";
            this.chkEnableCodecSuffix.UseVisualStyleBackColor = true;
            // 
            // 
            // grpExtensions
            // 
            this.grpExtensions.Controls.Add(this.lstSupportedExts);
            this.grpExtensions.Controls.Add(this.lblNewExt);
            this.grpExtensions.Controls.Add(this.txtNewExt);
            this.grpExtensions.Controls.Add(this.btnAddExt);
            this.grpExtensions.Controls.Add(this.btnRemoveExt);
            this.grpExtensions.Controls.Add(this.btnResetExts);
            this.grpExtensions.Controls.Add(this.lblExtHint);
            this.grpExtensions.Location = new System.Drawing.Point(15, 230);
            this.grpExtensions.Name = "grpExtensions";
            this.grpExtensions.Size = new System.Drawing.Size(380, 175);
            this.grpExtensions.TabIndex = 9;
            this.grpExtensions.TabStop = false;
            this.grpExtensions.Text = "Video File Extensions (checked = enabled)";
            // 
            // lstSupportedExts
            // 
            this.lstSupportedExts.FormattingEnabled = true;
            this.lstSupportedExts.IntegralHeight = false;
            this.lstSupportedExts.ItemHeight = 15;
            this.lstSupportedExts.Location = new System.Drawing.Point(12, 22);
            this.lstSupportedExts.Name = "lstSupportedExts";
            this.lstSupportedExts.CheckOnClick = true;
            this.lstSupportedExts.Size = new System.Drawing.Size(150, 140);
            this.lstSupportedExts.TabIndex = 0;
            // 
            // lblNewExt
            // 
            this.lblNewExt.AutoSize = true;
            this.lblNewExt.Location = new System.Drawing.Point(175, 25);
            this.lblNewExt.Name = "lblNewExt";
            this.lblNewExt.Size = new System.Drawing.Size(86, 15);
            this.lblNewExt.TabIndex = 1;
            this.lblNewExt.Text = "Add extension:";
            // 
            // txtNewExt
            // 
            this.txtNewExt.Location = new System.Drawing.Point(175, 45);
            this.txtNewExt.Name = "txtNewExt";
            this.txtNewExt.Size = new System.Drawing.Size(90, 23);
            this.txtNewExt.TabIndex = 2;
            this.txtNewExt.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtNewExt_KeyDown);
            // 
            // btnAddExt
            // 
            this.btnAddExt.Location = new System.Drawing.Point(270, 43);
            this.btnAddExt.Name = "btnAddExt";
            this.btnAddExt.Size = new System.Drawing.Size(94, 26);
            this.btnAddExt.TabIndex = 3;
            this.btnAddExt.Text = "Add";
            this.btnAddExt.UseVisualStyleBackColor = true;
            this.btnAddExt.Click += new System.EventHandler(this.btnAddExt_Click);
            // 
            // btnRemoveExt
            // 
            this.btnRemoveExt.Location = new System.Drawing.Point(175, 80);
            this.btnRemoveExt.Name = "btnRemoveExt";
            this.btnRemoveExt.Size = new System.Drawing.Size(189, 26);
            this.btnRemoveExt.TabIndex = 4;
            this.btnRemoveExt.Text = "Remove selected";
            this.btnRemoveExt.UseVisualStyleBackColor = true;
            this.btnRemoveExt.Click += new System.EventHandler(this.btnRemoveExt_Click);
            // 
            // btnResetExts
            // 
            this.btnResetExts.Location = new System.Drawing.Point(175, 115);
            this.btnResetExts.Name = "btnResetExts";
            this.btnResetExts.Size = new System.Drawing.Size(189, 26);
            this.btnResetExts.TabIndex = 5;
            this.btnResetExts.Text = "Reset to defaults";
            this.btnResetExts.UseVisualStyleBackColor = true;
            this.btnResetExts.Click += new System.EventHandler(this.btnResetExts_Click);
            // 
            // lblExtHint
            // 
            this.lblExtHint.AutoSize = true;
            this.lblExtHint.Location = new System.Drawing.Point(175, 147);
            this.lblExtHint.Name = "lblExtHint";
            this.lblExtHint.Size = new System.Drawing.Size(194, 15);
            this.lblExtHint.TabIndex = 6;
            this.lblExtHint.Text = "Examples: .mp4  mkv  m2ts";
            // 
            // chkRememberCheckboxes
            // 
            this.chkRememberCheckboxes.AutoSize = true;
            this.chkRememberCheckboxes.Location = new System.Drawing.Point(15, 415);
            this.chkRememberCheckboxes.Name = "chkRememberCheckboxes";
            this.chkRememberCheckboxes.Size = new System.Drawing.Size(288, 19);
            this.chkRememberCheckboxes.TabIndex = 10;
            this.chkRememberCheckboxes.Text = "Remember last-used settings for checkboxes";
            this.chkRememberCheckboxes.UseVisualStyleBackColor = true;
            // 
            // chkPreventSleepDuringEncoding
            // 
            this.chkPreventSleepDuringEncoding.AutoSize = true;
            this.chkPreventSleepDuringEncoding.Location = new System.Drawing.Point(15, 440);
            this.chkPreventSleepDuringEncoding.Name = "chkPreventSleepDuringEncoding";
            this.chkPreventSleepDuringEncoding.Size = new System.Drawing.Size(284, 19);
            this.chkPreventSleepDuringEncoding.TabIndex = 11;
            this.chkPreventSleepDuringEncoding.Text = "Prevent computer sleep while encoding jobs run";
            this.chkPreventSleepDuringEncoding.UseVisualStyleBackColor = true;
            // 
            // chkLimitGpuEncodingQueueToOneJob
            // 
            this.chkLimitGpuEncodingQueueToOneJob.AutoSize = true;
            this.chkLimitGpuEncodingQueueToOneJob.Location = new System.Drawing.Point(15, 465);
            this.chkLimitGpuEncodingQueueToOneJob.Name = "chkLimitGpuEncodingQueueToOneJob";
            this.chkLimitGpuEncodingQueueToOneJob.Size = new System.Drawing.Size(285, 19);
            this.chkLimitGpuEncodingQueueToOneJob.TabIndex = 12;
            this.chkLimitGpuEncodingQueueToOneJob.Text = "Limit GPU encoding queue to one job at a time";
            this.chkLimitGpuEncodingQueueToOneJob.UseVisualStyleBackColor = true;
            // 
            // lblLargeQueueThreshold
            // 
            this.lblLargeQueueThreshold.AutoSize = true;
            this.lblLargeQueueThreshold.Location = new System.Drawing.Point(15, 492);
            this.lblLargeQueueThreshold.Name = "lblLargeQueueThreshold";
            this.lblLargeQueueThreshold.Size = new System.Drawing.Size(136, 15);
            this.lblLargeQueueThreshold.TabIndex = 13;
            this.lblLargeQueueThreshold.Text = "Large queue threshold:";
            // 
            // nudLargeQueueThreshold
            // 
            this.nudLargeQueueThreshold.Location = new System.Drawing.Point(160, 490);
            this.nudLargeQueueThreshold.Maximum = new decimal(new int[] {
            10000,
            0,
            0,
            0});
            this.nudLargeQueueThreshold.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.nudLargeQueueThreshold.Name = "nudLargeQueueThreshold";
            this.nudLargeQueueThreshold.Size = new System.Drawing.Size(80, 23);
            this.nudLargeQueueThreshold.TabIndex = 13;
            this.nudLargeQueueThreshold.Value = new decimal(new int[] {
            300,
            0,
            0,
            0});
            // 
            // chkAutoAnalyzeLargeQueues
            // 
            this.chkAutoAnalyzeLargeQueues.AutoSize = true;
            this.chkAutoAnalyzeLargeQueues.Location = new System.Drawing.Point(15, 520);
            this.chkAutoAnalyzeLargeQueues.Name = "chkAutoAnalyzeLargeQueues";
            this.chkAutoAnalyzeLargeQueues.Size = new System.Drawing.Size(258, 19);
            this.chkAutoAnalyzeLargeQueues.TabIndex = 14;
            this.chkAutoAnalyzeLargeQueues.Text = "Automatically analyze large queues";
            this.chkAutoAnalyzeLargeQueues.UseVisualStyleBackColor = true;
            // 
            // chkEnablePersistentMediaInfoCache
            // 
            this.chkEnablePersistentMediaInfoCache.AutoSize = true;
            this.chkEnablePersistentMediaInfoCache.Location = new System.Drawing.Point(15, 545);
            this.chkEnablePersistentMediaInfoCache.Name = "chkEnablePersistentMediaInfoCache";
            this.chkEnablePersistentMediaInfoCache.Size = new System.Drawing.Size(257, 19);
            this.chkEnablePersistentMediaInfoCache.TabIndex = 15;
            this.chkEnablePersistentMediaInfoCache.Text = "Cache media metadata between launches";
            this.chkEnablePersistentMediaInfoCache.UseVisualStyleBackColor = true;
            // 
            // lblFfmpegPath
            // 
            this.lblFfmpegPath.AutoSize = true;
            this.lblFfmpegPath.Location = new System.Drawing.Point(15, 575);
            this.lblFfmpegPath.Name = "lblFfmpegPath";
            this.lblFfmpegPath.Size = new System.Drawing.Size(128, 15);
            this.lblFfmpegPath.TabIndex = 16;
            this.lblFfmpegPath.Text = "Custom FFmpeg path:";
            // 
            // txtFfmpegPath
            // 
            this.txtFfmpegPath.Location = new System.Drawing.Point(15, 595);
            this.txtFfmpegPath.Name = "txtFfmpegPath";
            this.txtFfmpegPath.Size = new System.Drawing.Size(300, 23);
            this.txtFfmpegPath.TabIndex = 16;
            // 
            // btnBrowseFfmpeg
            // 
            this.btnBrowseFfmpeg.Location = new System.Drawing.Point(320, 594);
            this.btnBrowseFfmpeg.Name = "btnBrowseFfmpeg";
            this.btnBrowseFfmpeg.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseFfmpeg.TabIndex = 17;
            this.btnBrowseFfmpeg.Text = "Browse…";
            this.btnBrowseFfmpeg.UseVisualStyleBackColor = true;
            this.btnBrowseFfmpeg.Click += new System.EventHandler(this.btnBrowseFfmpeg_Click);
            // 
            // lblFfprobePath
            // 
            this.lblFfprobePath.AutoSize = true;
            this.lblFfprobePath.Location = new System.Drawing.Point(15, 625);
            this.lblFfprobePath.Name = "lblFfprobePath";
            this.lblFfprobePath.Size = new System.Drawing.Size(128, 15);
            this.lblFfprobePath.TabIndex = 18;
            this.lblFfprobePath.Text = "Custom FFprobe path:";
            // 
            // txtFfprobePath
            // 
            this.txtFfprobePath.Location = new System.Drawing.Point(15, 645);
            this.txtFfprobePath.Name = "txtFfprobePath";
            this.txtFfprobePath.Size = new System.Drawing.Size(300, 23);
            this.txtFfprobePath.TabIndex = 19;
            // 
            // btnBrowseFfprobe
            // 
            this.btnBrowseFfprobe.Location = new System.Drawing.Point(320, 644);
            this.btnBrowseFfprobe.Name = "btnBrowseFfprobe";
            this.btnBrowseFfprobe.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseFfprobe.TabIndex = 20;
            this.btnBrowseFfprobe.Text = "Browse…";
            this.btnBrowseFfprobe.UseVisualStyleBackColor = true;
            this.btnBrowseFfprobe.Click += new System.EventHandler(this.btnBrowseFfprobe_Click);
            // 
            // grpIncompleteOutputCleanup
            // 
            this.grpIncompleteOutputCleanup.Controls.Add(this.chkDeleteFailedEncodeOutputs);
            this.grpIncompleteOutputCleanup.Controls.Add(this.chkDeleteCanceledEncodeOutputs);
            this.grpIncompleteOutputCleanup.Location = new System.Drawing.Point(15, 680);
            this.grpIncompleteOutputCleanup.Name = "grpIncompleteOutputCleanup";
            this.grpIncompleteOutputCleanup.Size = new System.Drawing.Size(380, 85);
            this.grpIncompleteOutputCleanup.TabIndex = 21;
            this.grpIncompleteOutputCleanup.TabStop = false;
            this.grpIncompleteOutputCleanup.Text = "Incomplete Output Cleanup";
            // 
            // chkDeleteFailedEncodeOutputs
            // 
            this.chkDeleteFailedEncodeOutputs.AutoSize = true;
            this.chkDeleteFailedEncodeOutputs.Location = new System.Drawing.Point(12, 25);
            this.chkDeleteFailedEncodeOutputs.Name = "chkDeleteFailedEncodeOutputs";
            this.chkDeleteFailedEncodeOutputs.Size = new System.Drawing.Size(247, 19);
            this.chkDeleteFailedEncodeOutputs.TabIndex = 0;
            this.chkDeleteFailedEncodeOutputs.Text = "Delete output files from failed encode jobs";
            this.chkDeleteFailedEncodeOutputs.UseVisualStyleBackColor = true;
            // 
            // chkDeleteCanceledEncodeOutputs
            // 
            this.chkDeleteCanceledEncodeOutputs.AutoSize = true;
            this.chkDeleteCanceledEncodeOutputs.Location = new System.Drawing.Point(12, 50);
            this.chkDeleteCanceledEncodeOutputs.Name = "chkDeleteCanceledEncodeOutputs";
            this.chkDeleteCanceledEncodeOutputs.Size = new System.Drawing.Size(262, 19);
            this.chkDeleteCanceledEncodeOutputs.TabIndex = 1;
            this.chkDeleteCanceledEncodeOutputs.Text = "Delete output files from canceled encode jobs";
            this.chkDeleteCanceledEncodeOutputs.UseVisualStyleBackColor = true;
            // 
            // grpWatchFolder
            // 
            this.grpWatchFolder.Controls.Add(this.lblWatchFolderPath);
            this.grpWatchFolder.Controls.Add(this.txtWatchFolderPath);
            this.grpWatchFolder.Controls.Add(this.btnBrowseWatchFolder);
            this.grpWatchFolder.Controls.Add(this.lblWatchInterval);
            this.grpWatchFolder.Controls.Add(this.nudWatchInterval);
            this.grpWatchFolder.Controls.Add(this.chkWatchIncludeSubfolders);
            this.grpWatchFolder.Controls.Add(this.chkHideWatchFolderStatusText);
            this.grpWatchFolder.Controls.Add(this.lblWatchStabilization);
            this.grpWatchFolder.Controls.Add(this.nudWatchStabilization);
            this.grpWatchFolder.Controls.Add(this.lblWatchHint);
            this.grpWatchFolder.Location = new System.Drawing.Point(415, 12);
            this.grpWatchFolder.Name = "grpWatchFolder";
            this.grpWatchFolder.Size = new System.Drawing.Size(390, 250);
            this.grpWatchFolder.TabIndex = 21;
            this.grpWatchFolder.TabStop = false;
            this.grpWatchFolder.Text = "Watch Folder";
            // 
            // grpDuplicateManagement
            // 
            this.grpDuplicateManagement.Controls.Add(this.chkFindDuplicatesOnImport);
            this.grpDuplicateManagement.Controls.Add(this.chkOnlyQueueDuplicateCandidates);
            this.grpDuplicateManagement.Controls.Add(this.lblDuplicateScanMode);
            this.grpDuplicateManagement.Controls.Add(this.comboDuplicateScanMode);
            this.grpDuplicateManagement.Controls.Add(this.lblDuplicateReferenceFolder);
            this.grpDuplicateManagement.Controls.Add(this.txtDuplicateReferenceFolder);
            this.grpDuplicateManagement.Controls.Add(this.btnBrowseDuplicateReferenceFolder);
            this.grpDuplicateManagement.Controls.Add(this.lblDuplicateQuarantineFolder);
            this.grpDuplicateManagement.Controls.Add(this.txtDuplicateQuarantineFolder);
            this.grpDuplicateManagement.Controls.Add(this.btnBrowseDuplicateQuarantineFolder);
            this.grpDuplicateManagement.Controls.Add(this.chkShowDuplicateReferenceFolderOnMain);
            this.grpDuplicateManagement.Controls.Add(this.lblDuplicateCleanupActions);
            this.grpDuplicateManagement.Controls.Add(this.chkAllowDuplicateRecycleBin);
            this.grpDuplicateManagement.Controls.Add(this.chkAllowDuplicateQuarantine);
            this.grpDuplicateManagement.Controls.Add(this.chkAllowDuplicatePermanentDelete);
            this.grpDuplicateManagement.Controls.Add(this.chkRequireDuplicateCleanupConfirmation);
            this.grpDuplicateManagement.Controls.Add(this.chkEnableDuplicateSignatureCache);
            this.grpDuplicateManagement.Controls.Add(this.btnClearDuplicateSignatureCache);
            this.grpDuplicateManagement.Controls.Add(this.btnClearDuplicatePreviewCache);
            this.grpDuplicateManagement.Controls.Add(this.lblDuplicateManagementHint);
            this.grpDuplicateManagement.Location = new System.Drawing.Point(820, 275);
            this.grpDuplicateManagement.Name = "grpDuplicateManagement";
            this.grpDuplicateManagement.Size = new System.Drawing.Size(390, 445);
            this.grpDuplicateManagement.TabIndex = 24;
            this.grpDuplicateManagement.TabStop = false;
            this.grpDuplicateManagement.Text = "Duplicate Management";
            // 
            // chkFindDuplicatesOnImport
            // 
            this.chkFindDuplicatesOnImport.AutoSize = true;
            this.chkFindDuplicatesOnImport.Location = new System.Drawing.Point(15, 25);
            this.chkFindDuplicatesOnImport.Name = "chkFindDuplicatesOnImport";
            this.chkFindDuplicatesOnImport.Size = new System.Drawing.Size(215, 19);
            this.chkFindDuplicatesOnImport.TabIndex = 0;
            this.chkFindDuplicatesOnImport.Text = "Check for duplicates before queueing";
            this.chkFindDuplicatesOnImport.UseVisualStyleBackColor = true;
            // 
            // chkOnlyQueueDuplicateCandidates
            // 
            this.chkOnlyQueueDuplicateCandidates.AutoSize = true;
            this.chkOnlyQueueDuplicateCandidates.Location = new System.Drawing.Point(15, 50);
            this.chkOnlyQueueDuplicateCandidates.Name = "chkOnlyQueueDuplicateCandidates";
            this.chkOnlyQueueDuplicateCandidates.Size = new System.Drawing.Size(202, 19);
            this.chkOnlyQueueDuplicateCandidates.TabIndex = 1;
            this.chkOnlyQueueDuplicateCandidates.Text = "Show only duplicate candidates";
            this.chkOnlyQueueDuplicateCandidates.UseVisualStyleBackColor = true;
            // 
            // lblDuplicateScanMode
            // 
            this.lblDuplicateScanMode.AutoSize = true;
            this.lblDuplicateScanMode.Location = new System.Drawing.Point(12, 78);
            this.lblDuplicateScanMode.Name = "lblDuplicateScanMode";
            this.lblDuplicateScanMode.Size = new System.Drawing.Size(69, 15);
            this.lblDuplicateScanMode.Text = "Scan mode:";
            // 
            // comboDuplicateScanMode
            // 
            this.comboDuplicateScanMode.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.comboDuplicateScanMode.Items.AddRange(new object[] {
            "Exact duplicates",
            "Strict visual duplicates",
            "Review similar videos"});
            this.comboDuplicateScanMode.Location = new System.Drawing.Point(115, 74);
            this.comboDuplicateScanMode.Name = "comboDuplicateScanMode";
            this.comboDuplicateScanMode.Size = new System.Drawing.Size(256, 23);
            this.comboDuplicateScanMode.TabIndex = 2;
            // 
            // lblDuplicateReferenceFolder
            // 
            this.lblDuplicateReferenceFolder.AutoSize = true;
            this.lblDuplicateReferenceFolder.Location = new System.Drawing.Point(12, 108);
            this.lblDuplicateReferenceFolder.Name = "lblDuplicateReferenceFolder";
            this.lblDuplicateReferenceFolder.Size = new System.Drawing.Size(96, 15);
            this.lblDuplicateReferenceFolder.Text = "Reference folder:";
            // 
            // txtDuplicateReferenceFolder
            // 
            this.txtDuplicateReferenceFolder.Location = new System.Drawing.Point(15, 129);
            this.txtDuplicateReferenceFolder.Name = "txtDuplicateReferenceFolder";
            this.txtDuplicateReferenceFolder.Size = new System.Drawing.Size(275, 23);
            this.txtDuplicateReferenceFolder.TabIndex = 3;
            // 
            // btnBrowseDuplicateReferenceFolder
            // 
            this.btnBrowseDuplicateReferenceFolder.Location = new System.Drawing.Point(296, 128);
            this.btnBrowseDuplicateReferenceFolder.Name = "btnBrowseDuplicateReferenceFolder";
            this.btnBrowseDuplicateReferenceFolder.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseDuplicateReferenceFolder.TabIndex = 4;
            this.btnBrowseDuplicateReferenceFolder.Text = "Browse…";
            this.btnBrowseDuplicateReferenceFolder.UseVisualStyleBackColor = true;
            this.btnBrowseDuplicateReferenceFolder.Click += new System.EventHandler(this.btnBrowseDuplicateReferenceFolder_Click);
            // 
            // lblDuplicateQuarantineFolder
            // 
            this.lblDuplicateQuarantineFolder.AutoSize = true;
            this.lblDuplicateQuarantineFolder.Location = new System.Drawing.Point(12, 278);
            this.lblDuplicateQuarantineFolder.Name = "lblDuplicateQuarantineFolder";
            this.lblDuplicateQuarantineFolder.Size = new System.Drawing.Size(103, 15);
            this.lblDuplicateQuarantineFolder.Text = "Quarantine folder:";
            // 
            // txtDuplicateQuarantineFolder
            // 
            this.txtDuplicateQuarantineFolder.Location = new System.Drawing.Point(15, 299);
            this.txtDuplicateQuarantineFolder.Name = "txtDuplicateQuarantineFolder";
            this.txtDuplicateQuarantineFolder.Size = new System.Drawing.Size(275, 23);
            this.txtDuplicateQuarantineFolder.TabIndex = 10;
            // 
            // btnBrowseDuplicateQuarantineFolder
            // 
            this.btnBrowseDuplicateQuarantineFolder.Location = new System.Drawing.Point(296, 298);
            this.btnBrowseDuplicateQuarantineFolder.Name = "btnBrowseDuplicateQuarantineFolder";
            this.btnBrowseDuplicateQuarantineFolder.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseDuplicateQuarantineFolder.TabIndex = 11;
            this.btnBrowseDuplicateQuarantineFolder.Text = "Browse…";
            this.btnBrowseDuplicateQuarantineFolder.UseVisualStyleBackColor = true;
            this.btnBrowseDuplicateQuarantineFolder.Click += new System.EventHandler(this.btnBrowseDuplicateQuarantineFolder_Click);
            // 
            // chkShowDuplicateReferenceFolderOnMain
            // 
            this.chkShowDuplicateReferenceFolderOnMain.AutoEllipsis = true;
            this.chkShowDuplicateReferenceFolderOnMain.AutoSize = false;
            this.chkShowDuplicateReferenceFolderOnMain.Location = new System.Drawing.Point(15, 157);
            this.chkShowDuplicateReferenceFolderOnMain.Name = "chkShowDuplicateReferenceFolderOnMain";
            this.chkShowDuplicateReferenceFolderOnMain.Size = new System.Drawing.Size(245, 19);
            this.chkShowDuplicateReferenceFolderOnMain.TabIndex = 5;
            this.chkShowDuplicateReferenceFolderOnMain.Text = "Show reference controls on main screen";
            this.chkShowDuplicateReferenceFolderOnMain.UseVisualStyleBackColor = true;
            // 
            // lblDuplicateCleanupActions
            // 
            this.lblDuplicateCleanupActions.AutoSize = true;
            this.lblDuplicateCleanupActions.Location = new System.Drawing.Point(12, 188);
            this.lblDuplicateCleanupActions.Name = "lblDuplicateCleanupActions";
            this.lblDuplicateCleanupActions.Size = new System.Drawing.Size(93, 15);
            this.lblDuplicateCleanupActions.Text = "Cleanup actions:";
            // 
            // chkAllowDuplicateRecycleBin
            // 
            this.chkAllowDuplicateRecycleBin.AutoSize = true;
            this.chkAllowDuplicateRecycleBin.Location = new System.Drawing.Point(15, 210);
            this.chkAllowDuplicateRecycleBin.Name = "chkAllowDuplicateRecycleBin";
            this.chkAllowDuplicateRecycleBin.Size = new System.Drawing.Size(180, 19);
            this.chkAllowDuplicateRecycleBin.TabIndex = 6;
            this.chkAllowDuplicateRecycleBin.Text = "Allow move to Recycle Bin";
            this.chkAllowDuplicateRecycleBin.UseVisualStyleBackColor = true;
            // 
            // chkAllowDuplicateQuarantine
            // 
            this.chkAllowDuplicateQuarantine.AutoSize = true;
            this.chkAllowDuplicateQuarantine.Location = new System.Drawing.Point(15, 233);
            this.chkAllowDuplicateQuarantine.Name = "chkAllowDuplicateQuarantine";
            this.chkAllowDuplicateQuarantine.Size = new System.Drawing.Size(163, 19);
            this.chkAllowDuplicateQuarantine.TabIndex = 7;
            this.chkAllowDuplicateQuarantine.Text = "Allow move to quarantine";
            this.chkAllowDuplicateQuarantine.UseVisualStyleBackColor = true;
            // 
            // chkAllowDuplicatePermanentDelete
            // 
            this.chkAllowDuplicatePermanentDelete.AutoSize = true;
            this.chkAllowDuplicatePermanentDelete.Location = new System.Drawing.Point(15, 256);
            this.chkAllowDuplicatePermanentDelete.Name = "chkAllowDuplicatePermanentDelete";
            this.chkAllowDuplicatePermanentDelete.Size = new System.Drawing.Size(148, 19);
            this.chkAllowDuplicatePermanentDelete.TabIndex = 8;
            this.chkAllowDuplicatePermanentDelete.Text = "Allow permanent delete";
            this.chkAllowDuplicatePermanentDelete.UseVisualStyleBackColor = true;
            // 
            // chkRequireDuplicateCleanupConfirmation
            // 
            this.chkRequireDuplicateCleanupConfirmation.AutoSize = true;
            this.chkRequireDuplicateCleanupConfirmation.Location = new System.Drawing.Point(15, 330);
            this.chkRequireDuplicateCleanupConfirmation.Name = "chkRequireDuplicateCleanupConfirmation";
            this.chkRequireDuplicateCleanupConfirmation.Size = new System.Drawing.Size(214, 19);
            this.chkRequireDuplicateCleanupConfirmation.TabIndex = 12;
            this.chkRequireDuplicateCleanupConfirmation.Text = "Require confirmation before duplicate cleanup";
            this.chkRequireDuplicateCleanupConfirmation.UseVisualStyleBackColor = true;
            // 
            // chkEnableDuplicateSignatureCache
            // 
            this.chkEnableDuplicateSignatureCache.AutoSize = true;
            this.chkEnableDuplicateSignatureCache.Location = new System.Drawing.Point(15, 360);
            this.chkEnableDuplicateSignatureCache.Name = "chkEnableDuplicateSignatureCache";
            this.chkEnableDuplicateSignatureCache.Size = new System.Drawing.Size(180, 19);
            this.chkEnableDuplicateSignatureCache.TabIndex = 13;
            this.chkEnableDuplicateSignatureCache.Text = "Cache duplicate signatures";
            this.chkEnableDuplicateSignatureCache.UseVisualStyleBackColor = true;
            // 
            // btnClearDuplicateSignatureCache
            // 
            this.btnClearDuplicateSignatureCache.Location = new System.Drawing.Point(205, 355);
            this.btnClearDuplicateSignatureCache.Name = "btnClearDuplicateSignatureCache";
            this.btnClearDuplicateSignatureCache.Size = new System.Drawing.Size(166, 27);
            this.btnClearDuplicateSignatureCache.TabIndex = 14;
            this.btnClearDuplicateSignatureCache.Text = "Clear Duplicate Cache";
            this.btnClearDuplicateSignatureCache.UseVisualStyleBackColor = true;
            this.btnClearDuplicateSignatureCache.Click += new System.EventHandler(this.btnClearDuplicateSignatureCache_Click);
            // 
            // btnClearDuplicatePreviewCache
            // 
            this.btnClearDuplicatePreviewCache.Location = new System.Drawing.Point(205, 385);
            this.btnClearDuplicatePreviewCache.Name = "btnClearDuplicatePreviewCache";
            this.btnClearDuplicatePreviewCache.Size = new System.Drawing.Size(166, 27);
            this.btnClearDuplicatePreviewCache.TabIndex = 15;
            this.btnClearDuplicatePreviewCache.Text = "Clear Preview Cache";
            this.btnClearDuplicatePreviewCache.UseVisualStyleBackColor = true;
            this.btnClearDuplicatePreviewCache.Click += new System.EventHandler(this.btnClearDuplicatePreviewCache_Click);
            // 
            // lblDuplicateManagementHint
            // 
            this.lblDuplicateManagementHint.AutoSize = false;
            this.lblDuplicateManagementHint.Location = new System.Drawing.Point(15, 389);
            this.lblDuplicateManagementHint.Name = "lblDuplicateManagementHint";
            this.lblDuplicateManagementHint.Size = new System.Drawing.Size(184, 45);
            this.lblDuplicateManagementHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblDuplicateManagementHint.Text = "Confirmations are recommended, especially for permanent delete.";
            // 
            // lblWatchFolderPath
            // 
            this.lblWatchFolderPath.AutoSize = true;
            this.lblWatchFolderPath.Location = new System.Drawing.Point(12, 25);
            this.lblWatchFolderPath.Text = "Folder to watch:";
            // 
            // txtWatchFolderPath
            // 
            this.txtWatchFolderPath.Location = new System.Drawing.Point(15, 46);
            this.txtWatchFolderPath.Name = "txtWatchFolderPath";
            this.txtWatchFolderPath.Size = new System.Drawing.Size(275, 23);
            // 
            // btnBrowseWatchFolder
            // 
            this.btnBrowseWatchFolder.Location = new System.Drawing.Point(296, 45);
            this.btnBrowseWatchFolder.Name = "btnBrowseWatchFolder";
            this.btnBrowseWatchFolder.Size = new System.Drawing.Size(78, 25);
            this.btnBrowseWatchFolder.Text = "Browse…";
            this.btnBrowseWatchFolder.UseVisualStyleBackColor = true;
            this.btnBrowseWatchFolder.Click += new System.EventHandler(this.btnBrowseWatchFolder_Click);
            // 
            // lblWatchInterval
            // 
            this.lblWatchInterval.AutoSize = true;
            this.lblWatchInterval.Location = new System.Drawing.Point(15, 87);
            this.lblWatchInterval.Text = "Check every (minutes):";
            // 
            // nudWatchInterval
            // 
            this.nudWatchInterval.Location = new System.Drawing.Point(175, 85);
            this.nudWatchInterval.Maximum = new decimal(new int[] { 1440, 0, 0, 0 });
            this.nudWatchInterval.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            this.nudWatchInterval.Name = "nudWatchInterval";
            this.nudWatchInterval.Size = new System.Drawing.Size(75, 23);
            this.nudWatchInterval.Value = new decimal(new int[] { 5, 0, 0, 0 });
            // 
            // chkWatchIncludeSubfolders
            // 
            this.chkWatchIncludeSubfolders.AutoSize = true;
            this.chkWatchIncludeSubfolders.Location = new System.Drawing.Point(15, 120);
            this.chkWatchIncludeSubfolders.Name = "chkWatchIncludeSubfolders";
            this.chkWatchIncludeSubfolders.Text = "Include subfolders";
            this.chkWatchIncludeSubfolders.UseVisualStyleBackColor = true;
            // 
            // chkHideWatchFolderStatusText
            // 
            this.chkHideWatchFolderStatusText.AutoSize = true;
            this.chkHideWatchFolderStatusText.Location = new System.Drawing.Point(15, 181);
            this.chkHideWatchFolderStatusText.Name = "chkHideWatchFolderStatusText";
            this.chkHideWatchFolderStatusText.Text = "Hide Checked / Next check status on Encode screen";
            this.chkHideWatchFolderStatusText.UseVisualStyleBackColor = true;
            // 
            // lblWatchStabilization
            // 
            this.lblWatchStabilization.AutoSize = true;
            this.lblWatchStabilization.Location = new System.Drawing.Point(15, 153);
            this.lblWatchStabilization.Text = "Wait after last file change (seconds):";
            // 
            // nudWatchStabilization
            // 
            this.nudWatchStabilization.Location = new System.Drawing.Point(265, 151);
            this.nudWatchStabilization.Maximum = new decimal(new int[] { 3600, 0, 0, 0 });
            this.nudWatchStabilization.Name = "nudWatchStabilization";
            this.nudWatchStabilization.Size = new System.Drawing.Size(75, 23);
            this.nudWatchStabilization.Value = new decimal(new int[] { 60, 0, 0, 0 });
            // 
            // lblWatchHint
            // 
            this.lblWatchHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblWatchHint.Location = new System.Drawing.Point(15, 207);
            this.lblWatchHint.Name = "lblWatchHint";
            this.lblWatchHint.Size = new System.Drawing.Size(355, 36);
            this.lblWatchHint.Text = "Enable watching on the Encode screen. New files follow the current Show x264 / x265 / AV1 / other codec filters and begin encoding automatically.";
            // 
            // grpDiscordNotification
            // 
            this.grpDiscordNotification.Controls.Add(this.chkDiscordNotification);
            this.grpDiscordNotification.Controls.Add(this.lblDiscordWebhookUrl);
            this.grpDiscordNotification.Controls.Add(this.txtDiscordWebhookUrl);
            this.grpDiscordNotification.Controls.Add(this.chkShowDiscordWebhook);
            this.grpDiscordNotification.Controls.Add(this.lblDiscordUserMentionId);
            this.grpDiscordNotification.Controls.Add(this.txtDiscordUserMentionId);
            this.grpDiscordNotification.Controls.Add(this.lblDiscordUserMentionHint);
            this.grpDiscordNotification.Controls.Add(this.lblDiscordMessage);
            this.grpDiscordNotification.Controls.Add(this.txtDiscordMessage);
            this.grpDiscordNotification.Controls.Add(this.lblDiscordPlaceholders);
            this.grpDiscordNotification.Controls.Add(this.btnTestDiscordWebhook);
            this.grpDiscordNotification.Location = new System.Drawing.Point(415, 275);
            this.grpDiscordNotification.Name = "grpDiscordNotification";
            this.grpDiscordNotification.Size = new System.Drawing.Size(390, 410);
            this.grpDiscordNotification.TabIndex = 22;
            this.grpDiscordNotification.TabStop = false;
            this.grpDiscordNotification.Text = "Discord Queue Notification";
            // 
            // chkDiscordNotification
            // 
            this.chkDiscordNotification.AutoSize = true;
            this.chkDiscordNotification.Location = new System.Drawing.Point(15, 25);
            this.chkDiscordNotification.Name = "chkDiscordNotification";
            this.chkDiscordNotification.Size = new System.Drawing.Size(276, 19);
            this.chkDiscordNotification.Text = "Notify Discord when the Encode queue finishes";
            this.chkDiscordNotification.UseVisualStyleBackColor = true;
            this.chkDiscordNotification.CheckedChanged += new System.EventHandler(this.chkDiscordNotification_CheckedChanged);
            // 
            // lblDiscordWebhookUrl
            // 
            this.lblDiscordWebhookUrl.AutoSize = true;
            this.lblDiscordWebhookUrl.Location = new System.Drawing.Point(15, 58);
            this.lblDiscordWebhookUrl.Name = "lblDiscordWebhookUrl";
            this.lblDiscordWebhookUrl.Text = "Discord webhook URL:";
            // 
            // txtDiscordWebhookUrl
            // 
            this.txtDiscordWebhookUrl.Location = new System.Drawing.Point(15, 78);
            this.txtDiscordWebhookUrl.Name = "txtDiscordWebhookUrl";
            this.txtDiscordWebhookUrl.Size = new System.Drawing.Size(355, 23);
            this.txtDiscordWebhookUrl.UseSystemPasswordChar = true;
            // 
            // chkShowDiscordWebhook
            // 
            this.chkShowDiscordWebhook.AutoSize = true;
            this.chkShowDiscordWebhook.Location = new System.Drawing.Point(15, 107);
            this.chkShowDiscordWebhook.Name = "chkShowDiscordWebhook";
            this.chkShowDiscordWebhook.Text = "Show webhook URL";
            this.chkShowDiscordWebhook.UseVisualStyleBackColor = true;
            this.chkShowDiscordWebhook.CheckedChanged += new System.EventHandler(this.chkShowDiscordWebhook_CheckedChanged);
            // 
            // lblDiscordUserMentionId
            // 
            this.lblDiscordUserMentionId.AutoSize = true;
            this.lblDiscordUserMentionId.Location = new System.Drawing.Point(15, 137);
            this.lblDiscordUserMentionId.Name = "lblDiscordUserMentionId";
            this.lblDiscordUserMentionId.Text = "User ID to mention (optional):";
            // 
            // txtDiscordUserMentionId
            // 
            this.txtDiscordUserMentionId.Location = new System.Drawing.Point(15, 157);
            this.txtDiscordUserMentionId.Name = "txtDiscordUserMentionId";
            this.txtDiscordUserMentionId.Size = new System.Drawing.Size(220, 23);
            // 
            // lblDiscordUserMentionHint
            // 
            this.lblDiscordUserMentionHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblDiscordUserMentionHint.Location = new System.Drawing.Point(15, 185);
            this.lblDiscordUserMentionHint.Name = "lblDiscordUserMentionHint";
            this.lblDiscordUserMentionHint.Size = new System.Drawing.Size(355, 30);
            this.lblDiscordUserMentionHint.Text = "In Discord, enable Developer Mode, then right-click the user and select Copy User ID.";
            // 
            // lblDiscordMessage
            // 
            this.lblDiscordMessage.AutoSize = true;
            this.lblDiscordMessage.Location = new System.Drawing.Point(15, 220);
            this.lblDiscordMessage.Name = "lblDiscordMessage";
            this.lblDiscordMessage.Text = "Queue-completion message:";
            // 
            // txtDiscordMessage
            // 
            this.txtDiscordMessage.Location = new System.Drawing.Point(15, 240);
            this.txtDiscordMessage.Multiline = true;
            this.txtDiscordMessage.Name = "txtDiscordMessage";
            this.txtDiscordMessage.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtDiscordMessage.Size = new System.Drawing.Size(355, 70);
            // 
            // lblDiscordPlaceholders
            // 
            this.lblDiscordPlaceholders.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblDiscordPlaceholders.Location = new System.Drawing.Point(15, 317);
            this.lblDiscordPlaceholders.Name = "lblDiscordPlaceholders";
            this.lblDiscordPlaceholders.Size = new System.Drawing.Size(355, 45);
            this.lblDiscordPlaceholders.Text = "Placeholders: {status}, {total}, {succeeded}, {failed}, {retried}, {computer}, {started}, {finished}, {duration}";
            // 
            // btnTestDiscordWebhook
            // 
            this.btnTestDiscordWebhook.Location = new System.Drawing.Point(15, 369);
            this.btnTestDiscordWebhook.Name = "btnTestDiscordWebhook";
            this.btnTestDiscordWebhook.Size = new System.Drawing.Size(145, 27);
            this.btnTestDiscordWebhook.Text = "Send Test Message";
            this.btnTestDiscordWebhook.UseVisualStyleBackColor = true;
            this.btnTestDiscordWebhook.Click += new System.EventHandler(this.btnTestDiscordWebhook_Click);
            // 
            // grpBackupRestore
            // 
            this.grpBackupRestore.Controls.Add(this.chkBackupBeforeUpdates);
            this.grpBackupRestore.Controls.Add(this.lblBackupFolder);
            this.grpBackupRestore.Controls.Add(this.txtBackupFolder);
            this.grpBackupRestore.Controls.Add(this.btnBrowseBackupFolder);
            this.grpBackupRestore.Controls.Add(this.lblBackupsToKeep);
            this.grpBackupRestore.Controls.Add(this.nudBackupsToKeep);
            this.grpBackupRestore.Controls.Add(this.btnBackupNow);
            this.grpBackupRestore.Controls.Add(this.btnRestoreBackup);
            this.grpBackupRestore.Controls.Add(this.lblBackupHint);
            this.grpBackupRestore.Location = new System.Drawing.Point(820, 12);
            this.grpBackupRestore.Name = "grpBackupRestore";
            this.grpBackupRestore.Size = new System.Drawing.Size(390, 250);
            this.grpBackupRestore.TabIndex = 23;
            this.grpBackupRestore.TabStop = false;
            this.grpBackupRestore.Text = "Program Backup and Restore";
            // 
            // chkBackupBeforeUpdates
            // 
            this.chkBackupBeforeUpdates.AutoSize = true;
            this.chkBackupBeforeUpdates.Location = new System.Drawing.Point(15, 25);
            this.chkBackupBeforeUpdates.Name = "chkBackupBeforeUpdates";
            this.chkBackupBeforeUpdates.Size = new System.Drawing.Size(231, 19);
            this.chkBackupBeforeUpdates.Text = "Automatically backup before updates";
            this.chkBackupBeforeUpdates.UseVisualStyleBackColor = true;
            // 
            // lblBackupFolder
            // 
            this.lblBackupFolder.AutoSize = true;
            this.lblBackupFolder.Location = new System.Drawing.Point(15, 58);
            this.lblBackupFolder.Name = "lblBackupFolder";
            this.lblBackupFolder.Text = "Backup folder:";
            // 
            // txtBackupFolder
            // 
            this.txtBackupFolder.Location = new System.Drawing.Point(15, 78);
            this.txtBackupFolder.Name = "txtBackupFolder";
            this.txtBackupFolder.Size = new System.Drawing.Size(275, 23);
            // 
            // btnBrowseBackupFolder
            // 
            this.btnBrowseBackupFolder.Location = new System.Drawing.Point(296, 77);
            this.btnBrowseBackupFolder.Name = "btnBrowseBackupFolder";
            this.btnBrowseBackupFolder.Size = new System.Drawing.Size(78, 25);
            this.btnBrowseBackupFolder.Text = "Browse…";
            this.btnBrowseBackupFolder.UseVisualStyleBackColor = true;
            this.btnBrowseBackupFolder.Click += new System.EventHandler(this.btnBrowseBackupFolder_Click);
            // 
            // lblBackupsToKeep
            // 
            this.lblBackupsToKeep.AutoSize = true;
            this.lblBackupsToKeep.Location = new System.Drawing.Point(15, 119);
            this.lblBackupsToKeep.Name = "lblBackupsToKeep";
            this.lblBackupsToKeep.Text = "Backups to keep:";
            // 
            // nudBackupsToKeep
            // 
            this.nudBackupsToKeep.Location = new System.Drawing.Point(125, 117);
            this.nudBackupsToKeep.Maximum = new decimal(new int[] { 100, 0, 0, 0 });
            this.nudBackupsToKeep.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            this.nudBackupsToKeep.Name = "nudBackupsToKeep";
            this.nudBackupsToKeep.Size = new System.Drawing.Size(70, 23);
            this.nudBackupsToKeep.Value = new decimal(new int[] { 5, 0, 0, 0 });
            // 
            // btnBackupNow
            // 
            this.btnBackupNow.Location = new System.Drawing.Point(15, 155);
            this.btnBackupNow.Name = "btnBackupNow";
            this.btnBackupNow.Size = new System.Drawing.Size(125, 27);
            this.btnBackupNow.Text = "Perform Backup Now";
            this.btnBackupNow.UseVisualStyleBackColor = true;
            this.btnBackupNow.Click += new System.EventHandler(this.btnBackupNow_Click);
            // 
            // btnRestoreBackup
            // 
            this.btnRestoreBackup.Location = new System.Drawing.Point(150, 155);
            this.btnRestoreBackup.Name = "btnRestoreBackup";
            this.btnRestoreBackup.Size = new System.Drawing.Size(145, 27);
            this.btnRestoreBackup.Text = "Restore Previous Backup";
            this.btnRestoreBackup.UseVisualStyleBackColor = true;
            this.btnRestoreBackup.Click += new System.EventHandler(this.btnRestoreBackup_Click);
            // 
            // lblBackupHint
            // 
            this.lblBackupHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblBackupHint.Location = new System.Drawing.Point(15, 193);
            this.lblBackupHint.Name = "lblBackupHint";
            this.lblBackupHint.Size = new System.Drawing.Size(355, 42);
            this.lblBackupHint.Text = "Backups are complete program snapshots. Restoring closes Encode, replaces the installed files, and restarts it.";
            // 
            // btnOK
            // 
            this.btnOK.Location = new System.Drawing.Point(15, 850);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(75, 25);
            this.btnOK.TabIndex = 21;
            this.btnOK.Text = "OK";
            this.btnOK.UseVisualStyleBackColor = true;
            this.btnOK.Click += new System.EventHandler(this.btnOK_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(100, 850);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(75, 25);
            this.btnCancel.TabIndex = 22;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // SettingsForm
            // 
            this.AcceptButton = this.btnOK;
            this.CancelButton = this.btnCancel;
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1225, 890);
            this.Controls.Add(this.grpIncompleteOutputCleanup);
            this.Controls.Add(this.grpBackupRestore);
            this.Controls.Add(this.grpWatchFolder);
            this.Controls.Add(this.grpDuplicateManagement);
            this.Controls.Add(this.grpDiscordNotification);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.btnBrowseFfprobe);
            this.Controls.Add(this.txtFfprobePath);
            this.Controls.Add(this.lblFfprobePath);
            this.Controls.Add(this.btnBrowseFfmpeg);
            this.Controls.Add(this.txtFfmpegPath);
            this.Controls.Add(this.lblFfmpegPath);
            this.Controls.Add(this.chkEnablePersistentMediaInfoCache);
            this.Controls.Add(this.chkAutoAnalyzeLargeQueues);
            this.Controls.Add(this.nudLargeQueueThreshold);
            this.Controls.Add(this.lblLargeQueueThreshold);
            this.Controls.Add(this.chkLimitGpuEncodingQueueToOneJob);
            this.Controls.Add(this.chkPreventSleepDuringEncoding);
            this.Controls.Add(this.chkRememberCheckboxes);
            this.Controls.Add(this.grpExtensions);
            this.Controls.Add(this.txtSuffix);
            this.Controls.Add(this.lblSuffix);
            this.Controls.Add(this.chkEnableCodecSuffix);
            this.Controls.Add(this.chkEnableSuffix);
            this.Controls.Add(this.txtPattern);
            this.Controls.Add(this.lblPattern);
            this.Controls.Add(this.btnBrowseUpdate);
            this.Controls.Add(this.txtUpdateFolder);
            this.Controls.Add(this.lblUpdateFolder);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "SettingsForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Settings";
            this.Load += new System.EventHandler(this.SettingsForm_Load);
            this.grpExtensions.ResumeLayout(false);
            this.grpExtensions.PerformLayout();
            this.grpIncompleteOutputCleanup.ResumeLayout(false);
            this.grpIncompleteOutputCleanup.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudLargeQueueThreshold)).EndInit();
            this.grpWatchFolder.ResumeLayout(false);
            this.grpWatchFolder.PerformLayout();
            this.grpDiscordNotification.ResumeLayout(false);
            this.grpDiscordNotification.PerformLayout();
            this.grpBackupRestore.ResumeLayout(false);
            this.grpBackupRestore.PerformLayout();
            this.grpDuplicateManagement.ResumeLayout(false);
            this.grpDuplicateManagement.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudBackupsToKeep)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudWatchInterval)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudWatchStabilization)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
