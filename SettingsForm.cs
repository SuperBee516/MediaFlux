using MediaFlux.Models;
using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MediaFlux
{
    public partial class SettingsForm : MediaFluxForm
    {
        public Config Config { get; }

        private readonly string _supportedVideoExtsPath;
        private readonly IReadOnlyList<string> _defaultVideoExts;
        private readonly string _currentOutputFolder;
        private GroupBox grpExplorerIntegration = null!;
        private CheckBox chkExplorerFiles = null!;
        private CheckBox chkExplorerFolders = null!;
        private CheckBox chkExplorerConfirmFolders = null!;
        private CheckBox chkExplorerPromptClearQueue = null!;
        private CheckBox chkExplorerIncludeSubfolders = null!;
        private Label lblExplorerStatus = null!;
        private Button btnExplorerEnableRepair = null!;
        private Button btnExplorerRemove = null!;
        private Button btnDuplicateKeeperPreferences = null!;
        private Label lblFfmpegStatus = null!;
        private Label lblFfprobeStatus = null!;
        private TextBox txtDvdOutputNamingPattern = null!;
        private DuplicateKeeperPreferences _duplicateKeeperPreferences = new();
        private readonly ToolTip _settingsToolTip = new();

        public SettingsForm(
            Config cfg,
            string supportedVideoExtsPath,
            IEnumerable<string> defaultVideoExts,
            string currentOutputFolder,
            bool focusMediaTools = false)
        {
            InitializeComponent();
            InitializeFfmpegStatusControls();
            InitializeExplorerIntegrationControls();
            Config = cfg;
            InitializeDvdSettingsControls(cfg);
            _duplicateKeeperPreferences = (cfg.DuplicateKeeperPreferences ?? new DuplicateKeeperPreferences()).Clone();
            InitializeDuplicateKeeperPreferenceControls();

            _supportedVideoExtsPath = supportedVideoExtsPath;
            _defaultVideoExts = (defaultVideoExts ?? Array.Empty<string>()).ToList();
            _currentOutputFolder = currentOutputFolder?.Trim() ?? string.Empty;

            txtUpdateFolder.Text = "https://github.com/SuperBee516/MediaFlux/releases";
            txtUpdateFolder.ReadOnly = true;
            chkBackupBeforeUpdates.Checked = cfg.AutomaticallyBackupBeforeUpdates;
            txtBackupFolder.Text = BackupManager.ResolveBackupFolder(cfg.BackupFolderPath);
            nudBackupsToKeep.Value = Math.Clamp(cfg.BackupsToKeep, 1, 100);
            txtPattern.Text = cfg.AutoNamingPattern;
            txtSuffix.Text = cfg.OutputSuffix;
            chkEnableSuffix.Checked = cfg.EnableOutputSuffix;
            chkEnableCodecSuffix.Checked = cfg.EnableCodecSuffix;
            chkRememberCheckboxes.Checked = cfg.RememberCheckboxStates;
            chkPreventSleepDuringEncoding.Checked = cfg.PreventSleepDuringEncoding;
            chkLimitGpuEncodingQueueToOneJob.Checked = cfg.LimitGpuEncodingQueueToOneJob;
            chkDeleteFailedEncodeOutputs.Checked = cfg.DeleteFailedEncodeOutputs;
            chkDeleteCanceledEncodeOutputs.Checked = cfg.DeleteCanceledEncodeOutputs;
            nudLargeQueueThreshold.Value = Math.Clamp(cfg.LargeQueueThreshold, 1, 10000);
            chkAutoAnalyzeLargeQueues.Checked = cfg.AutoAnalyzeLargeQueues;
            chkFindDuplicatesOnImport.Checked = cfg.FindDuplicatesOnImport;
            chkOnlyQueueDuplicateCandidates.Checked = cfg.OnlyQueueDuplicateCandidates;
            comboDuplicateScanMode.SelectedItem = NormalizeDuplicateScanMode(cfg.DuplicateScanMode);
            txtDuplicateReferenceFolder.Text = cfg.DuplicateReferenceFolder;
            txtDuplicateQuarantineFolder.Text = cfg.DuplicateQuarantineFolder;
            chkEnableDuplicateSignatureCache.Checked = cfg.EnableDuplicateSignatureCache;
            chkAllowDuplicateRecycleBin.Checked = cfg.AllowDuplicateRecycleBin;
            chkAllowDuplicateQuarantine.Checked = cfg.AllowDuplicateQuarantine;
            chkAllowDuplicatePermanentDelete.Checked = cfg.AllowDuplicatePermanentDelete;
            chkRequireDuplicateCleanupConfirmation.Checked = cfg.RequireDuplicateCleanupConfirmation;
            chkShowDuplicateReferenceFolderOnMain.Checked = cfg.ShowDuplicateReferenceFolderOnMain;
            ToggleDuplicateManagementInputs();
            ToggleDuplicateCleanupInputs();
            chkFindDuplicatesOnImport.CheckedChanged += (_, __) => ToggleDuplicateManagementInputs();
            chkAllowDuplicateQuarantine.CheckedChanged += (_, __) => ToggleDuplicateCleanupInputs();
            chkAllowDuplicatePermanentDelete.CheckedChanged += (_, __) => ToggleDuplicateCleanupInputs();
            chkRequireDuplicateCleanupConfirmation.CheckedChanged += (_, __) => ToggleDuplicateCleanupInputs();
            chkEnablePersistentMediaInfoCache.Checked = cfg.EnablePersistentMediaInfoCache;
            txtFfmpegPath.Text = cfg.FfmpegPath;
            txtFfprobePath.Text = cfg.FfprobePath;
            txtFfmpegPath.TextChanged += (_, __) => RefreshFfmpegStatus();
            txtFfprobePath.TextChanged += (_, __) => RefreshFfmpegStatus();
            chkDiscordNotification.Checked = cfg.DiscordQueueNotificationEnabled;
            txtDiscordWebhookUrl.Text = cfg.DiscordWebhookUrl;
            txtDiscordUserMentionId.Text = cfg.DiscordUserMentionId;
            txtDiscordMessage.Text = cfg.DiscordQueueCompleteMessage;
            txtWatchFolderPath.Text = cfg.WatchFolderPath;
            nudWatchInterval.Value = Math.Clamp(cfg.WatchFolderIntervalMinutes, 1, 1440);
            chkWatchIncludeSubfolders.Checked = cfg.WatchFolderIncludeSubfolders;
            nudWatchStabilization.Value = Math.Clamp(cfg.WatchFolderStabilizationSeconds, 0, 3600);
            chkHideWatchFolderStatusText.Checked = cfg.HideWatchFolderStatusText;
            chkExplorerFiles.Checked = ExplorerContextMenuService.IsFileMenuInstalled || cfg.ExplorerFileContextMenuEnabled;
            chkExplorerFolders.Checked = ExplorerContextMenuService.HasAnyFolderMenuRegistration || cfg.ExplorerFolderContextMenuEnabled;
            chkExplorerConfirmFolders.Checked = cfg.ConfirmExplorerFolderImports;
            chkExplorerPromptClearQueue.Checked = cfg.PromptToClearQueueOnExplorerFolderImport;
            chkExplorerIncludeSubfolders.Checked = cfg.ExplorerFolderIncludeSubfolders;
            UpdateExplorerIntegrationStatus();
            ToggleSuffixInputs();
            ToggleDiscordInputs();

            LoadSupportedExtensionsIntoUi();
            RefreshFfmpegStatus();

            if (focusMediaTools)
            {
                Shown += (_, __) =>
                {
                    txtFfmpegPath.Focus();
                    txtFfmpegPath.SelectAll();
                };
            }
        }

        private void InitializeDvdSettingsControls(Config config)
        {
            var group = new GroupBox
            {
                Text = "DVD Output Naming",
                Location = new Point(15, 775),
                Size = new Size(790, 65),
                TabIndex = 25
            };
            var label = new Label
            {
                Text = "Pattern:",
                AutoSize = true,
                Location = new Point(12, 29)
            };
            txtDvdOutputNamingPattern = new TextBox
            {
                Location = new Point(72, 25),
                Size = new Size(300, 23),
                Text = string.IsNullOrWhiteSpace(config.DvdOutputNamingPattern)
                    ? "{MovieName}{TitleSetSuffix}"
                    : config.DvdOutputNamingPattern
            };
            var hint = new Label
            {
                Text = "Tokens: {MovieName}, {TitleSet}, {TitleSetSuffix}",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Location = new Point(385, 29)
            };
            group.Controls.Add(label);
            group.Controls.Add(txtDvdOutputNamingPattern);
            group.Controls.Add(hint);
            Controls.Add(group);
            group.BringToFront();
            _settingsToolTip.SetToolTip(
                txtDvdOutputNamingPattern,
                "{TitleSetSuffix} is blank for a single strong main feature and adds the title-set identifier otherwise.");
        }

        private void InitializeFfmpegStatusControls()
        {
            const int statusHeight = 20;

            lblFfmpegStatus = new Label
            {
                AutoEllipsis = true,
                Location = new Point(15, 621),
                Name = "lblFfmpegStatus",
                Size = new Size(380, statusHeight),
                TabIndex = 18
            };

            lblFfprobePath.Top += 25;
            txtFfprobePath.Top += 25;
            btnBrowseFfprobe.Top += 25;

            lblFfprobeStatus = new Label
            {
                AutoEllipsis = true,
                Location = new Point(15, 696),
                Name = "lblFfprobeStatus",
                Size = new Size(380, statusHeight),
                TabIndex = 21
            };

            grpIncompleteOutputCleanup.Top += 50;
            Controls.Add(lblFfmpegStatus);
            Controls.Add(lblFfprobeStatus);
        }

        private void RefreshFfmpegStatus()
        {
            UpdateToolStatusLabel(lblFfmpegStatus, txtFfmpegPath.Text, "ffmpeg.exe", configuredFfmpegPath: true);
            UpdateToolStatusLabel(lblFfprobeStatus, txtFfprobePath.Text, "ffprobe.exe", configuredFfmpegPath: false);
        }

        private void UpdateToolStatusLabel(
            Label label,
            string configuredPath,
            string fileName,
            bool configuredFfmpegPath)
        {
            string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!string.IsNullOrWhiteSpace(expanded))
            {
                bool exists = File.Exists(expanded);
                bool correctFile = string.Equals(
                    Path.GetFileName(expanded),
                    fileName,
                    StringComparison.OrdinalIgnoreCase);
                bool valid = exists && correctFile;
                label.Text = !exists
                    ? $"Not found: {expanded}"
                    : correctFile
                        ? $"Found: {expanded}"
                        : $"Select {fileName}, not {Path.GetFileName(expanded)}.";
                label.ForeColor = valid ? Color.DarkGreen : Color.Firebrick;
                _settingsToolTip.SetToolTip(label, label.Text);
                return;
            }

            var tools = configuredFfmpegPath
                ? FfmpegToolResolver.Resolve(AppPaths.InstallDirectory, configuredFfmpegPath: configuredPath)
                : FfmpegToolResolver.Resolve(AppPaths.InstallDirectory, configuredFfprobePath: configuredPath);
            string resolvedPath = configuredFfmpegPath ? tools.FfmpegPath : tools.FfprobePath;
            bool detected = File.Exists(resolvedPath);
            label.Text = detected
                ? $"Detected automatically: {resolvedPath}"
                : $"Not detected. Add {fileName} to the Programs folder or browse to it.";
            label.ForeColor = detected ? Color.DarkGreen : Color.Firebrick;
            _settingsToolTip.SetToolTip(label, label.Text);
        }


        private void btnBrowseUpdate_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = txtUpdateFolder.Text,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"MediaFlux could not open the GitHub Releases page.\n\n{ex.Message}",
                    "Open GitHub Releases",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void btnBrowseBackupFolder_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(txtBackupFolder.Text)
                    ? txtBackupFolder.Text
                    : BackupManager.GetDefaultBackupFolder()
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                txtBackupFolder.Text = dlg.SelectedPath;
        }

        private void btnBackupNow_Click(object sender, EventArgs e)
        {
            btnBackupNow.Enabled = false;
            UseWaitCursor = true;
            try
            {
                string archive = BackupManager.CreateBackup(
                    AppPaths.UserDataDirectory,
                    txtBackupFolder.Text,
                    (int)nudBackupsToKeep.Value);
                MessageBox.Show(this, $"Backup completed successfully.\n\n{archive}", "User data backup",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The backup could not be created.\n\n{ex.Message}", "Backup failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                btnBackupNow.Enabled = true;
            }
        }

        private void btnRestoreBackup_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select a MediaFlux backup to restore",
                Filter = "MediaFlux backups (*.zip)|*.zip|ZIP archives (*.zip)|*.zip",
                CheckFileExists = true,
                InitialDirectory = Directory.Exists(txtBackupFolder.Text)
                    ? txtBackupFolder.Text
                    : BackupManager.GetDefaultBackupFolder()
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            if (MessageBox.Show(this,
                    "MediaFlux will close, restore the selected user-data backup, and restart. Continue?",
                    "Restore user data backup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            try
            {
                BackupManager.StartRestoreAndExit(
                    dlg.FileName,
                    AppPaths.UserDataDirectory,
                    Application.ExecutablePath,
                    Environment.ProcessId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The backup could not be restored.\n\n{ex.Message}", "Restore failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnBrowseFfmpeg_Click(object sender, EventArgs e)
        {
            BrowseToolPath(txtFfmpegPath, "Select ffmpeg.exe", "ffmpeg.exe");
        }

        private void btnBrowseFfprobe_Click(object sender, EventArgs e)
        {
            BrowseToolPath(txtFfprobePath, "Select ffprobe.exe", "ffprobe.exe");
        }

        private void btnBrowseWatchFolder_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(txtWatchFolderPath.Text)
                    ? txtWatchFolderPath.Text
                    : Config.WatchFolderPath
            };

            if (dlg.ShowDialog(this) == DialogResult.OK)
                txtWatchFolderPath.Text = dlg.SelectedPath;
        }

        private void btnBrowseDuplicateQuarantineFolder_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select the default duplicate quarantine folder.",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(txtDuplicateQuarantineFolder.Text)
                    ? txtDuplicateQuarantineFolder.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dlg.ShowDialog(this) == DialogResult.OK)
                txtDuplicateQuarantineFolder.Text = dlg.SelectedPath;
        }

        private void btnBrowseDuplicateReferenceFolder_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select an optional protected reference folder. Files in this folder will be preferred as keepers.",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(txtDuplicateReferenceFolder.Text)
                    ? txtDuplicateReferenceFolder.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dlg.ShowDialog(this) == DialogResult.OK)
                txtDuplicateReferenceFolder.Text = dlg.SelectedPath;
        }

        private void ToggleDuplicateManagementInputs()
        {
            chkOnlyQueueDuplicateCandidates.Enabled = chkFindDuplicatesOnImport.Checked;
            if (!chkFindDuplicatesOnImport.Checked)
                chkOnlyQueueDuplicateCandidates.Checked = false;
        }

        private void InitializeDuplicateKeeperPreferenceControls()
        {
            btnDuplicateKeeperPreferences = new Button
            {
                Name = "btnDuplicateKeeperPreferences",
                Text = "Keeper Rules…",
                Location = new Point(270, 154),
                Size = new Size(101, 25),
                TabIndex = 16,
                UseVisualStyleBackColor = true
            };
            btnDuplicateKeeperPreferences.Click += btnDuplicateKeeperPreferences_Click;
            grpDuplicateManagement.Controls.Add(btnDuplicateKeeperPreferences);
            btnDuplicateKeeperPreferences.BringToFront();
            _settingsToolTip.SetToolTip(
                btnDuplicateKeeperPreferences,
                "Choose keeper presets, custom weights, codec preference, and close-score review safeguards.");
        }

        private void btnDuplicateKeeperPreferences_Click(object? sender, EventArgs e)
        {
            using var dialog = new DuplicateKeeperPreferencesForm(_duplicateKeeperPreferences);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _duplicateKeeperPreferences = dialog.Preferences.Clone();
            _settingsToolTip.SetToolTip(
                btnDuplicateKeeperPreferences,
                $"Current keeper preference: {_duplicateKeeperPreferences.Profile}. Click to change weights and safeguards.");
        }

        private void ToggleDuplicateCleanupInputs()
        {
            bool allowQuarantine = chkAllowDuplicateQuarantine.Checked;
            lblDuplicateQuarantineFolder.Enabled = allowQuarantine;
            txtDuplicateQuarantineFolder.Enabled = allowQuarantine;
            btnBrowseDuplicateQuarantineFolder.Enabled = allowQuarantine;

            if (chkRequireDuplicateCleanupConfirmation.Checked)
            {
                lblDuplicateManagementHint.Text = "Confirmations are recommended, especially for permanent delete.";
                lblDuplicateManagementHint.ForeColor = SystemColors.GrayText;
            }
            else
            {
                lblDuplicateManagementHint.Text = "Warning: cleanup actions, including permanent delete, run immediately.";
                lblDuplicateManagementHint.ForeColor = Color.DarkRed;
            }
        }

        private void btnClearDuplicateSignatureCache_Click(object sender, EventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                "Clear the duplicate signature cache? Future duplicate scans will rebuild signatures as needed.",
                "Clear Duplicate Cache",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes)
                return;

            DuplicateDetectionService.ClearPersistentCache(AppPaths.UserDataDirectory);
            MessageBox.Show(this, "Duplicate signature cache cleared.", "Duplicate Cache",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btnClearDuplicatePreviewCache_Click(object sender, EventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                "Clear cached duplicate review thumbnails? They will be recreated as needed when reviewing duplicate groups.",
                "Clear Preview Cache",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes)
                return;

            var result = DuplicatePreviewCacheService.Clear(AppPaths.UserDataDirectory);
            MessageBox.Show(
                this,
                $"Duplicate preview cache cleared.{Environment.NewLine}{result.DeletedFiles:N0} file(s), {FormatBytes(result.FreedBytes)} removed.",
                "Duplicate Preview Cache",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            string watchFolderPath = txtWatchFolderPath.Text.Trim();
            if (Config.WatchFolderEnabled && !Directory.Exists(watchFolderPath))
            {
                MessageBox.Show(this,
                    "Choose an existing watch folder, or disable Watch Folder on the Encode screen first.",
                    "Invalid watch folder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (Config.WatchFolderEnabled && string.IsNullOrWhiteSpace(_currentOutputFolder))
            {
                MessageBox.Show(this,
                    "Choose a separate Output Folder on the Encode screen before enabling folder watching.",
                    "Output folder required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (FolderPathComparer.OutputConflictsWithWatchFolder(
                    _currentOutputFolder,
                    watchFolderPath,
                    chkWatchIncludeSubfolders.Checked))
            {
                MessageBox.Show(this,
                    "The Output Folder cannot be the watched folder or a folder inside its watched subfolders. Choose a separate output location.",
                    "Folder conflict",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            bool allowDuplicateRecycle = chkAllowDuplicateRecycleBin.Checked;
            bool allowDuplicateQuarantine = chkAllowDuplicateQuarantine.Checked;
            bool allowDuplicatePermanentDelete = chkAllowDuplicatePermanentDelete.Checked;
            if (!allowDuplicateRecycle && !allowDuplicateQuarantine && !allowDuplicatePermanentDelete)
            {
                MessageBox.Show(this,
                    "Enable at least one duplicate cleanup action: Recycle Bin, quarantine, or permanent delete.",
                    "Duplicate cleanup action required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                chkAllowDuplicateRecycleBin.Focus();
                return;
            }

            string duplicateQuarantineFolder = txtDuplicateQuarantineFolder.Text.Trim();
            if (allowDuplicateQuarantine && !Directory.Exists(duplicateQuarantineFolder))
            {
                MessageBox.Show(this,
                    "Choose an existing duplicate quarantine folder, or disable the quarantine cleanup action.",
                    "Duplicate quarantine folder required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                txtDuplicateQuarantineFolder.Focus();
                return;
            }

            Config.AutomaticallyBackupBeforeUpdates = chkBackupBeforeUpdates.Checked;
            Config.BackupFolderPath = txtBackupFolder.Text.Trim();
            Config.BackupsToKeep = (int)nudBackupsToKeep.Value;
            Config.AutoNamingPattern = txtPattern.Text.Trim();
            Config.DvdOutputNamingPattern =
                string.IsNullOrWhiteSpace(txtDvdOutputNamingPattern.Text)
                    ? "{MovieName}{TitleSetSuffix}"
                    : txtDvdOutputNamingPattern.Text.Trim();
            Config.OutputSuffix = txtSuffix.Text.Trim();  // <-- NEW
            Config.EnableOutputSuffix = chkEnableSuffix.Checked;
            Config.EnableCodecSuffix = chkEnableCodecSuffix.Checked;
            Config.RememberCheckboxStates = chkRememberCheckboxes.Checked;
            Config.PreventSleepDuringEncoding = chkPreventSleepDuringEncoding.Checked;
            Config.LimitGpuEncodingQueueToOneJob = chkLimitGpuEncodingQueueToOneJob.Checked;
            Config.DeleteFailedEncodeOutputs = chkDeleteFailedEncodeOutputs.Checked;
            Config.DeleteCanceledEncodeOutputs = chkDeleteCanceledEncodeOutputs.Checked;
            Config.LargeQueueThreshold = (int)nudLargeQueueThreshold.Value;
            Config.AutoAnalyzeLargeQueues = chkAutoAnalyzeLargeQueues.Checked;
            Config.FindDuplicatesOnImport = chkFindDuplicatesOnImport.Checked;
            Config.OnlyQueueDuplicateCandidates = chkOnlyQueueDuplicateCandidates.Checked;
            Config.DuplicateScanMode = NormalizeDuplicateScanMode(comboDuplicateScanMode.SelectedItem?.ToString());
            Config.DuplicateReferenceFolder = txtDuplicateReferenceFolder.Text.Trim();
            Config.DuplicateQuarantineFolder = duplicateQuarantineFolder;
            Config.DuplicateKeeperPreferences = _duplicateKeeperPreferences.Clone();
            Config.EnableDuplicateSignatureCache = chkEnableDuplicateSignatureCache.Checked;
            Config.AllowDuplicateRecycleBin = allowDuplicateRecycle;
            Config.AllowDuplicateQuarantine = allowDuplicateQuarantine;
            Config.AllowDuplicatePermanentDelete = allowDuplicatePermanentDelete;
            Config.RequireDuplicateCleanupConfirmation = chkRequireDuplicateCleanupConfirmation.Checked;
            Config.ShowDuplicateReferenceFolderOnMain = chkShowDuplicateReferenceFolderOnMain.Checked;
            Config.EnablePersistentMediaInfoCache = chkEnablePersistentMediaInfoCache.Checked;
            Config.FfmpegPath = txtFfmpegPath.Text.Trim();
            Config.FfprobePath = txtFfprobePath.Text.Trim();
            Config.WatchFolderPath = watchFolderPath;
            Config.WatchFolderIntervalMinutes = (int)nudWatchInterval.Value;
            Config.WatchFolderIncludeSubfolders = chkWatchIncludeSubfolders.Checked;
            Config.WatchFolderStabilizationSeconds = (int)nudWatchStabilization.Value;
            Config.HideWatchFolderStatusText = chkHideWatchFolderStatusText.Checked;
            Config.ExplorerFileContextMenuEnabled = chkExplorerFiles.Checked;
            Config.ExplorerFolderContextMenuEnabled = chkExplorerFolders.Checked;
            Config.ConfirmExplorerFolderImports = chkExplorerConfirmFolders.Checked;
            Config.PromptToClearQueueOnExplorerFolderImport = chkExplorerPromptClearQueue.Checked;
            Config.ExplorerFolderIncludeSubfolders = chkExplorerIncludeSubfolders.Checked;

            if (chkDiscordNotification.Checked &&
                !DiscordWebhookService.IsValidWebhookUrl(txtDiscordWebhookUrl.Text))
            {
                MessageBox.Show(this,
                    "Enter a valid Discord webhook URL, or disable Discord notifications.",
                    "Invalid Discord webhook",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                txtDiscordWebhookUrl.Focus();
                return;
            }

            if (chkDiscordNotification.Checked && string.IsNullOrWhiteSpace(txtDiscordMessage.Text))
            {
                MessageBox.Show(this,
                    "Enter a queue-completion message, or disable Discord notifications.",
                    "Discord message required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                txtDiscordMessage.Focus();
                return;
            }

            if (!string.IsNullOrWhiteSpace(txtDiscordUserMentionId.Text) &&
                !DiscordWebhookService.IsValidUserMentionId(txtDiscordUserMentionId.Text))
            {
                MessageBox.Show(this,
                    "Enter the numeric Discord user ID to mention, or leave the field blank.",
                    "Invalid Discord user ID",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                txtDiscordUserMentionId.Focus();
                return;
            }

            int mentionLength = string.IsNullOrWhiteSpace(txtDiscordUserMentionId.Text)
                ? 0
                : txtDiscordUserMentionId.Text.Trim().Length + 4;
            if (txtDiscordMessage.Text.Trim().Length + mentionLength > 2000)
            {
                MessageBox.Show(this,
                    "The Discord message cannot exceed 2,000 characters.",
                    "Discord message too long",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                txtDiscordMessage.Focus();
                return;
            }

            Config.DiscordQueueNotificationEnabled = chkDiscordNotification.Checked;
            Config.DiscordWebhookUrl = txtDiscordWebhookUrl.Text.Trim();
            Config.DiscordUserMentionId = txtDiscordUserMentionId.Text.Trim();
            Config.DiscordQueueCompleteMessage = txtDiscordMessage.Text.Trim();

            if (!ValidateOptionalToolPath(Config.FfmpegPath, "FFmpeg", "ffmpeg.exe"))
                return;

            if (!ValidateOptionalToolPath(Config.FfprobePath, "FFprobe", "ffprobe.exe"))
                return;

            // Persist supported video extensions list
            var exts = ReadSupportedExtensionsFromUi();
            if (exts.Count == 0)
            {
                MessageBox.Show(this,
                    "You must keep at least one supported video file extension.",
                    "Invalid extensions list",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var enabledExtensions = lstSupportedExts.CheckedItems
                .Cast<object>()
                .Select(item => item?.ToString() ?? string.Empty)
                .Where(ext => !string.IsNullOrWhiteSpace(ext))
                .ToList();

            if (enabledExtensions.Count == 0)
            {
                MessageBox.Show(this,
                    "Select at least one video file extension to enable.",
                    "No extensions enabled",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SupportedExtensionsStore.Save(_supportedVideoExtsPath, exts);
            Config.EnabledVideoExtensions = enabledExtensions;

            try
            {
                if (chkExplorerFiles.Checked || chkExplorerFolders.Checked)
                    ExplorerContextMenuService.Apply(
                        chkExplorerFiles.Checked,
                        chkExplorerFolders.Checked,
                        enabledExtensions);
                else
                    ExplorerContextMenuService.Remove();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Settings were validated, but Windows Explorer integration could not be updated.\r\n\r\n" + ex.Message,
                    "Explorer Integration",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private static string NormalizeDuplicateScanMode(string? value)
        {
            return value switch
            {
                "Exact duplicates" => "Exact duplicates",
                "Review similar videos" => "Review similar videos",
                _ => "Strict visual duplicates"
            };
        }

        private void InitializeExplorerIntegrationControls()
        {
            grpExplorerIntegration = new GroupBox
            {
                Text = "Windows Explorer Integration",
                Location = new Point(415, 725),
                Size = new Size(795, 110),
                TabStop = false
            };
            lblExplorerStatus = new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(15, 24)
            };
            chkExplorerFiles = new CheckBox
            {
                Text = "Add menu for video files",
                AutoSize = true,
                Location = new Point(15, 50)
            };
            chkExplorerFolders = new CheckBox
            {
                Text = "Add queue and duplicate-check menus for folders",
                AutoSize = true,
                Location = new Point(15, 76)
            };
            chkExplorerConfirmFolders = new CheckBox
            {
                Text = "Confirm normal folder imports before adding files",
                AutoSize = true,
                Location = new Point(340, 24)
            };
            chkExplorerIncludeSubfolders = new CheckBox
            {
                Text = "Include subfolders by default",
                AutoSize = true,
                Location = new Point(340, 50)
            };
            chkExplorerPromptClearQueue = new CheckBox
            {
                Text = "Ask to clear an existing queue",
                AutoSize = true,
                Location = new Point(340, 76)
            };
            btnExplorerEnableRepair = new Button
            {
                Text = "Enable / Repair",
                Location = new Point(650, 42),
                Size = new Size(130, 28)
            };
            btnExplorerEnableRepair.Click += btnExplorerEnableRepair_Click;
            btnExplorerRemove = new Button
            {
                Text = "Remove",
                Location = new Point(650, 74),
                Size = new Size(130, 28)
            };
            btnExplorerRemove.Click += btnExplorerRemove_Click;
            chkExplorerFolders.CheckedChanged += (_, __) =>
            {
                chkExplorerConfirmFolders.Enabled = chkExplorerFolders.Checked;
                chkExplorerPromptClearQueue.Enabled = chkExplorerFolders.Checked;
                chkExplorerIncludeSubfolders.Enabled = chkExplorerFolders.Checked;
            };

            grpExplorerIntegration.Controls.AddRange(new Control[]
            {
                lblExplorerStatus, chkExplorerFiles, chkExplorerFolders,
                chkExplorerConfirmFolders, chkExplorerPromptClearQueue, chkExplorerIncludeSubfolders,
                btnExplorerEnableRepair, btnExplorerRemove
            });
            Controls.Add(grpExplorerIntegration);
        }

        private void btnExplorerEnableRepair_Click(object? sender, EventArgs e)
        {
            if (!chkExplorerFiles.Checked && !chkExplorerFolders.Checked)
            {
                MessageBox.Show(this, "Select at least one Explorer context-menu option.",
                    "Explorer Integration", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                ExplorerContextMenuService.Apply(
                    chkExplorerFiles.Checked,
                    chkExplorerFolders.Checked,
                    ReadEnabledExtensionsFromUi());
                Config.ExplorerFileContextMenuEnabled = chkExplorerFiles.Checked;
                Config.ExplorerFolderContextMenuEnabled = chkExplorerFolders.Checked;
                UpdateExplorerIntegrationStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Explorer integration could not be updated.\r\n\r\n" + ex.Message,
                    "Explorer Integration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnExplorerRemove_Click(object? sender, EventArgs e)
        {
            try
            {
                ExplorerContextMenuService.Remove();
                Config.ExplorerFileContextMenuEnabled = false;
                Config.ExplorerFolderContextMenuEnabled = false;
                chkExplorerFiles.Checked = false;
                chkExplorerFolders.Checked = false;
                UpdateExplorerIntegrationStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Explorer integration could not be removed.\r\n\r\n" + ex.Message,
                    "Explorer Integration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateExplorerIntegrationStatus()
        {
            var status = ExplorerContextMenuService.GetStatus(chkExplorerFiles.Checked, chkExplorerFolders.Checked);
            lblExplorerStatus.Text = status switch
            {
                ExplorerIntegrationStatus.Enabled => "Status: Enabled",
                ExplorerIntegrationStatus.Partial => "Status: Partially enabled or needs repair",
                _ => "Status: Disabled"
            };
            lblExplorerStatus.ForeColor = status == ExplorerIntegrationStatus.Enabled
                ? Color.DarkGreen
                : status == ExplorerIntegrationStatus.Partial ? Color.DarkOrange : SystemColors.ControlText;
            chkExplorerConfirmFolders.Enabled = chkExplorerFolders.Checked;
            chkExplorerPromptClearQueue.Enabled = chkExplorerFolders.Checked;
            chkExplorerIncludeSubfolders.Enabled = chkExplorerFolders.Checked;
        }

        private List<string> ReadEnabledExtensionsFromUi()
        {
            return lstSupportedExts.CheckedItems
                .Cast<object>()
                .Select(item => item?.ToString() ?? string.Empty)
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .ToList();
        }

        private void BrowseToolPath(TextBox target, string title, string fileName)
        {
            using var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = fileName,
                CheckFileExists = true
            };

            if (!string.IsNullOrWhiteSpace(target.Text))
            {
                try
                {
                    var dir = Path.GetDirectoryName(target.Text);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                }
                catch
                {
                    // ignore invalid typed paths
                }
            }

            if (dlg.ShowDialog(this) == DialogResult.OK)
                target.Text = dlg.FileName;
        }

        private bool ValidateOptionalToolPath(string path, string label, string expectedFileName)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            string expanded = Environment.ExpandEnvironmentVariables(path);
            if (File.Exists(expanded) &&
                string.Equals(Path.GetFileName(expanded), expectedFileName, StringComparison.OrdinalIgnoreCase))
                return true;

            MessageBox.Show(this,
                $"Choose {expectedFileName} for the {label} path, or leave it blank to auto-detect from the app folder.",
                "Invalid tool path",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {

        }

        private void chkEnableSuffix_CheckedChanged(object sender, EventArgs e)
        {
            ToggleSuffixInputs();
        }

        private void ToggleSuffixInputs()
        {
            bool enabled = chkEnableSuffix.Checked;
            txtSuffix.Enabled = enabled;
            lblSuffix.Enabled = enabled;
        }

        private void chkDiscordNotification_CheckedChanged(object sender, EventArgs e)
        {
            ToggleDiscordInputs();
        }

        private void ToggleDiscordInputs()
        {
            bool enabled = chkDiscordNotification.Checked;
            lblDiscordWebhookUrl.Enabled = enabled;
            txtDiscordWebhookUrl.Enabled = enabled;
            chkShowDiscordWebhook.Enabled = enabled;
            lblDiscordUserMentionId.Enabled = enabled;
            txtDiscordUserMentionId.Enabled = enabled;
            lblDiscordUserMentionHint.Enabled = enabled;
            lblDiscordMessage.Enabled = enabled;
            txtDiscordMessage.Enabled = enabled;
            lblDiscordPlaceholders.Enabled = enabled;
            btnTestDiscordWebhook.Enabled = enabled;
        }

        private void chkShowDiscordWebhook_CheckedChanged(object sender, EventArgs e)
        {
            txtDiscordWebhookUrl.UseSystemPasswordChar = !chkShowDiscordWebhook.Checked;
        }

        private async void btnTestDiscordWebhook_Click(object sender, EventArgs e)
        {
            if (!DiscordWebhookService.IsValidWebhookUrl(txtDiscordWebhookUrl.Text))
            {
                MessageBox.Show(this, "Enter a valid Discord webhook URL first.", "Invalid Discord webhook",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string message = MainForm.FormatDiscordQueueCompleteMessage(
                txtDiscordMessage.Text,
                succeeded: 3,
                failed: 0,
                retried: 1,
                startedUtc: DateTime.UtcNow.AddMinutes(-5),
                finishedUtc: DateTime.UtcNow);

            btnTestDiscordWebhook.Enabled = false;
            try
            {
                await DiscordWebhookService.SendAsync(
                    txtDiscordWebhookUrl.Text,
                    message,
                    txtDiscordUserMentionId.Text);
                MessageBox.Show(this, "Test message sent successfully.", "Discord webhook",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Discord webhook test failed", exception: ex);
                MessageBox.Show(this, $"Discord could not receive the test message.\n\n{ex.Message}",
                    "Discord webhook failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnTestDiscordWebhook.Enabled = chkDiscordNotification.Checked;
            }
        }

        private void LoadSupportedExtensionsIntoUi()
        {
            var list = SupportedExtensionsStore.Load(_supportedVideoExtsPath, _defaultVideoExts);
            var enabled = new HashSet<string>(Config.EnabledVideoExtensions, StringComparer.OrdinalIgnoreCase);
            bool enableAll = enabled.Count == 0;
            lstSupportedExts.BeginUpdate();
            try
            {
                lstSupportedExts.Items.Clear();
                foreach (var ext in list)
                    lstSupportedExts.Items.Add(ext, enableAll || enabled.Contains(ext));
            }
            finally
            {
                lstSupportedExts.EndUpdate();
            }
        }

        private List<string> ReadSupportedExtensionsFromUi()
        {
            var exts = new List<string>();
            foreach (var item in lstSupportedExts.Items)
            {
                var s = item?.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    exts.Add(s);
            }
            return SupportedExtensionsStore.Normalize(exts).ToList();
        }

        private void btnAddExt_Click(object sender, EventArgs e)
        {
            var raw = txtNewExt.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var normalized = SupportedExtensionsStore.Normalize(new[] { raw }).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                MessageBox.Show(this,
                    "Please enter a valid extension like .mp4 or mkv (letters/numbers only).",
                    "Invalid extension",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var exists = lstSupportedExts.Items.Cast<object>()
                .Select(o => o?.ToString() ?? string.Empty)
                .Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));

            if (!exists)
                lstSupportedExts.Items.Add(normalized, true);

            txtNewExt.Clear();
            txtNewExt.Focus();
        }

        private void btnRemoveExt_Click(object sender, EventArgs e)
        {
            if (lstSupportedExts.SelectedItems.Count == 0) return;

            // Copy because we'll mutate the collection
            var toRemove = lstSupportedExts.SelectedItems.Cast<object>().ToList();
            foreach (var item in toRemove)
                lstSupportedExts.Items.Remove(item);
        }

        private void btnResetExts_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(this,
                "Reset supported video extensions back to the built-in defaults?",
                "Reset extensions",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            lstSupportedExts.BeginUpdate();
            try
            {
                lstSupportedExts.Items.Clear();
                foreach (var ext in SupportedExtensionsStore.Normalize(_defaultVideoExts))
                    lstSupportedExts.Items.Add(ext, true);
            }
            finally
            {
                lstSupportedExts.EndUpdate();
            }
        }

        private void txtNewExt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                btnAddExt_Click(sender, EventArgs.Empty);
            }
        }
    }
}
