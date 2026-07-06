using Encode.Models;
using Encode.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Encode
{
    public partial class SettingsForm : Form
    {
        public Config Config { get; }

        private readonly string _supportedVideoExtsPath;
        private readonly IReadOnlyList<string> _defaultVideoExts;
        private readonly string _currentOutputFolder;

        public SettingsForm(
            Config cfg,
            string supportedVideoExtsPath,
            IEnumerable<string> defaultVideoExts,
            string currentOutputFolder)
        {
            InitializeComponent();
            Config = cfg;

            _supportedVideoExtsPath = supportedVideoExtsPath;
            _defaultVideoExts = (defaultVideoExts ?? Array.Empty<string>()).ToList();
            _currentOutputFolder = currentOutputFolder?.Trim() ?? string.Empty;

            txtUpdateFolder.Text = cfg.UpdateFolderPath;
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
            nudLargeQueueThreshold.Value = Math.Clamp(cfg.LargeQueueThreshold, 1, 10000);
            chkAutoAnalyzeLargeQueues.Checked = cfg.AutoAnalyzeLargeQueues;
            chkEnablePersistentMediaInfoCache.Checked = cfg.EnablePersistentMediaInfoCache;
            txtFfmpegPath.Text = cfg.FfmpegPath;
            txtFfprobePath.Text = cfg.FfprobePath;
            chkDiscordNotification.Checked = cfg.DiscordQueueNotificationEnabled;
            txtDiscordWebhookUrl.Text = cfg.DiscordWebhookUrl;
            txtDiscordUserMentionId.Text = cfg.DiscordUserMentionId;
            txtDiscordMessage.Text = cfg.DiscordQueueCompleteMessage;
            txtWatchFolderPath.Text = cfg.WatchFolderPath;
            nudWatchInterval.Value = Math.Clamp(cfg.WatchFolderIntervalMinutes, 1, 1440);
            chkWatchIncludeSubfolders.Checked = cfg.WatchFolderIncludeSubfolders;
            nudWatchStabilization.Value = Math.Clamp(cfg.WatchFolderStabilizationSeconds, 0, 3600);
            chkHideWatchFolderStatusText.Checked = cfg.HideWatchFolderStatusText;
            ToggleSuffixInputs();
            ToggleDiscordInputs();

            LoadSupportedExtensionsIntoUi();
        }


        private void btnBrowseUpdate_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = Config.UpdateFolderPath };
            if (dlg.ShowDialog() == DialogResult.OK)
                txtUpdateFolder.Text = dlg.SelectedPath;
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
                    Application.StartupPath,
                    txtBackupFolder.Text,
                    (int)nudBackupsToKeep.Value);
                MessageBox.Show(this, $"Backup completed successfully.\n\n{archive}", "Program backup",
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
                Title = "Select an Encode backup to restore",
                Filter = "Encode backups (Encode_Backup_*.zip)|Encode_Backup_*.zip|ZIP archives (*.zip)|*.zip",
                CheckFileExists = true,
                InitialDirectory = Directory.Exists(txtBackupFolder.Text)
                    ? txtBackupFolder.Text
                    : BackupManager.GetDefaultBackupFolder()
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            if (MessageBox.Show(this,
                    "Encode will close, restore all program files from the selected backup, and restart. Continue?",
                    "Restore program backup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            try
            {
                BackupManager.StartRestoreAndExit(
                    dlg.FileName,
                    Application.StartupPath,
                    Path.GetFileName(Application.ExecutablePath),
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

            Config.UpdateFolderPath = txtUpdateFolder.Text.Trim();
            Config.AutomaticallyBackupBeforeUpdates = chkBackupBeforeUpdates.Checked;
            Config.BackupFolderPath = txtBackupFolder.Text.Trim();
            Config.BackupsToKeep = (int)nudBackupsToKeep.Value;
            Config.AutoNamingPattern = txtPattern.Text.Trim();
            Config.OutputSuffix = txtSuffix.Text.Trim();  // <-- NEW
            Config.EnableOutputSuffix = chkEnableSuffix.Checked;
            Config.EnableCodecSuffix = chkEnableCodecSuffix.Checked;
            Config.RememberCheckboxStates = chkRememberCheckboxes.Checked;
            Config.PreventSleepDuringEncoding = chkPreventSleepDuringEncoding.Checked;
            Config.LimitGpuEncodingQueueToOneJob = chkLimitGpuEncodingQueueToOneJob.Checked;
            Config.LargeQueueThreshold = (int)nudLargeQueueThreshold.Value;
            Config.AutoAnalyzeLargeQueues = chkAutoAnalyzeLargeQueues.Checked;
            Config.EnablePersistentMediaInfoCache = chkEnablePersistentMediaInfoCache.Checked;
            Config.FfmpegPath = txtFfmpegPath.Text.Trim();
            Config.FfprobePath = txtFfprobePath.Text.Trim();
            Config.WatchFolderPath = watchFolderPath;
            Config.WatchFolderIntervalMinutes = (int)nudWatchInterval.Value;
            Config.WatchFolderIncludeSubfolders = chkWatchIncludeSubfolders.Checked;
            Config.WatchFolderStabilizationSeconds = (int)nudWatchStabilization.Value;
            Config.HideWatchFolderStatusText = chkHideWatchFolderStatusText.Checked;

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

            if (!ValidateOptionalToolPath(Config.FfmpegPath, "FFmpeg"))
                return;

            if (!ValidateOptionalToolPath(Config.FfprobePath, "FFprobe"))
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

            DialogResult = DialogResult.OK;
            Close();
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

        private bool ValidateOptionalToolPath(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            string expanded = Environment.ExpandEnvironmentVariables(path);
            if (File.Exists(expanded))
                return true;

            MessageBox.Show(this,
                $"{label} path does not exist. Leave it blank to auto-detect from the app folder.",
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
