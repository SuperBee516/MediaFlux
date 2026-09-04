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
        private GroupBox grpSmartRecommendations = null!;
        private CheckBox chkSmartRecommendations = null!;
        private NumericUpDown nudMinimumExpectedSavings = null!;
        private CheckBox chkWarnBeforeEncodingRecommendations = null!;
        private StorageSavingsOptions _storageSavingsOptions = new();
        private DuplicateKeeperPreferences _duplicateKeeperPreferences = new();
        private readonly ToolTip _settingsToolTip = new();
        private ComboBox comboLibraryAnalyzerCleanupMode = null!;
        private CheckBox chkAllowUnreviewedVisualBulkCleanup = null!;
        private NumericUpDown nudVisualBulkCleanupConfidence = null!;
        private Label lblVisualBulkCleanupWarning = null!;
        private CheckBox chkSemiAutomaticVisualKeeperApproval = null!;
        private CheckBox chkMinimizeToSystemTray = null!;
        private NumericUpDown nudVisualMassReviewMaximumMatches = null!;
        private NumericUpDown nudVisualMassReviewMinimumMargin = null!;
        private NumericUpDown nudVisualMassReviewMinimumConfidence = null!;
        private FlowLayoutPanel? _libraryAnalyzerSettingsPanel;
        private ListBox? _settingsNavigation;
        private Panel? _settingsContentHost;
        private readonly Dictionary<string, Control> _settingsPages = new(StringComparer.Ordinal);
        internal const string VisualBulkCleanupRiskWarning = "Visual similarity is probabilistic. Enabling this option may propose unreviewed false positives for permanent deletion. Every plan is previewed and requires confirmation, but you must verify each proposed keeper and deletion.";

        public SettingsForm(
            Config cfg,
            string supportedVideoExtsPath,
            IEnumerable<string> defaultVideoExts,
            string currentOutputFolder,
            bool focusMediaTools = false)
        {
            InitializeComponent();
            AutoScroll = true;
            InitializeFfmpegStatusControls();
            InitializeExplorerIntegrationControls();
            Config = cfg;
            InitializeLibraryAnalyzerCleanupControls(cfg);
            InitializeLibraryAnalyzerReviewProductivityControls(cfg);
            InitializeSmartRecommendationControls(cfg);
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
            InitializeSystemTraySettingsControl(cfg);
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
            BuildTwoPaneSettingsLayout();

            if (focusMediaTools)
            {
                Shown += (_, __) =>
                {
                    txtFfmpegPath.Focus();
                    txtFfmpegPath.SelectAll();
                };
            }
        }

        private void BuildTwoPaneSettingsLayout()
        {
            Control[] existing = Controls.Cast<Control>().ToArray();
            var categories = SettingsCategoryCatalog.Names;
            _settingsNavigation = new ListBox { Name = "SettingsCategoryNavigation", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false, Font = new Font(Font, FontStyle.Regular) };
            _settingsNavigation.Items.AddRange(categories.Cast<object>().ToArray());
            _settingsNavigation.SelectedIndexChanged += (_, _) => ShowSettingsCategory(_settingsNavigation.SelectedItem?.ToString());
            _settingsContentHost = new Panel { Name = "SettingsCategoryContent", Dock = DockStyle.Fill, AutoScroll = false, Padding = new Padding(8), BackColor = SystemColors.Control };

            var split = new SplitContainer { Name = "SettingsTwoPane", Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, IsSplitterFixed = false, SplitterWidth = 5 };
            split.Panel1.Padding = new Padding(8, 8, 4, 8); split.Panel2.Padding = new Padding(4, 8, 8, 8);
            split.Panel1.Controls.Add(_settingsNavigation); split.Panel2.Controls.Add(_settingsContentHost);
            split.Resize += (_, _) =>
            {
                if (split.Width >= 760)
                    split.SplitterDistance = Math.Clamp(220, 150, split.Width - 360);
            };

            var footer = new Panel { Name = "SettingsActions", Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8, 8, 8, 8) };
            btnCancel.Dock = DockStyle.Right; btnCancel.Margin = new Padding(6, 0, 0, 0); btnOK.Dock = DockStyle.Right; btnOK.Margin = new Padding(0);
            footer.Controls.Add(btnCancel); footer.Controls.Add(btnOK);

            SuspendLayout();
            try
            {
                Controls.Clear(); AutoScroll = true; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimumSize = new Size(760, 560); ClientSize = new Size(Math.Max(ClientSize.Width, 980), Math.Max(ClientSize.Height, 700));
                Controls.Add(split); Controls.Add(footer);
                foreach (string category in categories)
                {
                    var page = new Panel { Name = "SettingsPage" + category.Replace(" ", ""), Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Control, Visible = false };
                    page.Resize += (_, _) =>
                    {
                        int width = Math.Max(360, page.ClientSize.Width - page.Padding.Horizontal);
                        foreach (Control child in page.Controls)
                            if (child.Dock == DockStyle.Top) child.Width = width;
                    };
                    _settingsPages[category] = page;
                    _settingsContentHost.Controls.Add(page);
                }

                var assigned = new HashSet<Control>();
                AddCategory("General", assigned, FindExisting(existing, "lblPattern", "txtPattern", "lblSuffix", "txtSuffix", "chkEnableSuffix", "chkEnableCodecSuffix", "grpExtensions", "chkRememberCheckboxes", "chkPreventSleepDuringEncoding", "chkMinimizeToSystemTray"));
                AddCategory("Encoding", assigned, FindExisting(existing, "chkLimitGpuEncodingQueueToOneJob", "lblLargeQueueThreshold", "nudLargeQueueThreshold", "chkAutoAnalyzeLargeQueues", "grpIncompleteOutputCleanup"));
                AddCategory("Encoding", assigned, FindGroup(existing, "DVD Output Naming"));
                AddCategory("FFmpeg & Tools", assigned, FindExisting(existing, "lblFfmpegPath", "txtFfmpegPath", "btnBrowseFfmpeg", "lblFfmpegStatus", "lblFfprobePath", "txtFfprobePath", "btnBrowseFfprobe", "lblFfprobeStatus", "chkEnablePersistentMediaInfoCache"));
                AddCategory("Automation", assigned, FindExisting(existing, "grpWatchFolder"));
                AddCategory("Duplicates", assigned, FindExisting(existing, "grpDuplicateManagement"));
                AddCategory("Duplicates", assigned, _libraryAnalyzerSettingsPanel);
                AddCategory("Storage & Cache", assigned, grpSmartRecommendations);
                AddCategory("Backup & Restore", assigned, FindExisting(existing, "grpBackupRestore"));
                AddCategory("Integrations", assigned, FindExisting(existing, "grpDiscordNotification"));
                AddCategory("Integrations", assigned, grpExplorerIntegration);
                AddCategory("Updates", assigned, FindExisting(existing, "lblUpdateFolder", "txtUpdateFolder", "btnBrowseUpdate"));

                // Preserve any newly-added or less common settings controls by keeping them represented.
                AddCategory("General", assigned, existing.Where(control => control != btnOK && control != btnCancel && !assigned.Contains(control)).ToArray());
                _settingsNavigation.SelectedIndex = 0;
            }
            finally { ResumeLayout(true); }
        }

        private void AddCategory(string category, HashSet<Control> assigned, params Control?[] controls)
        {
            if (!_settingsPages.TryGetValue(category, out Control? page)) return;
            foreach (Control? control in controls)
            {
                if (control == null || !assigned.Add(control)) continue;
                control.Parent?.Controls.Remove(control); control.Dock = DockStyle.Top; control.Margin = new Padding(0, 0, 0, 10); control.Width = Math.Max(360, page.ClientSize.Width - 24); page.Controls.Add(control); control.BringToFront();
            }
        }

        private void ShowSettingsCategory(string? category)
        {
            if (category == null || _settingsContentHost == null || !_settingsPages.TryGetValue(category, out Control? page)) return;
            foreach (Control candidate in _settingsPages.Values) candidate.Visible = ReferenceEquals(candidate, page);
            if (page.Parent != _settingsContentHost) _settingsContentHost.Controls.Add(page);
            page.Dock = DockStyle.Fill;
            if (page is Panel panel) panel.AutoScrollPosition = Point.Empty;
        }

        private static Control?[] FindExisting(IEnumerable<Control> controls, params string[] names) => names.Select(name => controls.FirstOrDefault(control => control.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        private static Control? FindGroup(IEnumerable<Control> controls, string text) => controls.FirstOrDefault(control => control is GroupBox group && group.Text.Equals(text, StringComparison.Ordinal));

        private void InitializeSmartRecommendationControls(Config config)
        {
            _storageSavingsOptions =
                (config.StorageSavings ?? new StorageSavingsOptions())
                .CloneNormalized();
            grpSmartRecommendations = new GroupBox
            {
                Text = "Smart Encode Recommendations",
                Location = new Point(820, 730),
                Size = new Size(390, 105),
                TabStop = false
            };

            chkSmartRecommendations = new CheckBox
            {
                Text = "Analyze encode candidates",
                AutoSize = true,
                Location = new Point(15, 23),
                Checked = config.SmartRecommendationsEnabled
            };
            var storageSavingsButton = new Button
            {
                Text = "Storage savings…",
                Location = new Point(225, 18),
                Size = new Size(145, 27)
            };
            storageSavingsButton.Click += (_, __) =>
            {
                using var dialog =
                    new StorageSavingsSettingsForm(_storageSavingsOptions);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _storageSavingsOptions =
                        dialog.Options.CloneNormalized();
                    UpdateStorageSavingsButton();
                }
            };

            void UpdateStorageSavingsButton()
            {
                storageSavingsButton.Text = _storageSavingsOptions.Enabled
                    ? "Storage savings: On"
                    : "Storage savings…";
                _settingsToolTip.SetToolTip(
                    storageSavingsButton,
                    _storageSavingsOptions.Enabled
                        ? _storageSavingsOptions.UsesQualityTarget
                            ? $"HEVC storage mode enabled: quality {_storageSavingsOptions.QualityValue}. Stronger compression can reduce visual quality."
                            : $"HEVC storage mode enabled: {_storageSavingsOptions.SourceVideoBitratePercent:0.#}% of source video bitrate. Stronger compression can reduce visual quality."
                        : "Configure an optional aggressive HEVC quality or bitrate target. Conservative behavior remains the default.");
            }

            var minimumLabel = new Label
            {
                Text = "Minimum worthwhile saving:",
                AutoSize = true,
                Location = new Point(15, 52)
            };
            nudMinimumExpectedSavings = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 90,
                DecimalPlaces = 1,
                Increment = 1,
                Value = (decimal)Math.Clamp(
                    config.MinimumExpectedSavingsPercent,
                    0,
                    90),
                Location = new Point(230, 49),
                Size = new Size(70, 23)
            };
            var percentLabel = new Label
            {
                Text = "%",
                AutoSize = true,
                Location = new Point(306, 52)
            };

            chkWarnBeforeEncodingRecommendations = new CheckBox
            {
                Text = "Warn before encoding Skip, Review, or unavailable files",
                AutoSize = true,
                Location = new Point(15, 78),
                Checked = config.WarnBeforeEncodingSkippedOrReviewItems
            };

            chkSmartRecommendations.CheckedChanged += (_, __) =>
            {
                nudMinimumExpectedSavings.Enabled = chkSmartRecommendations.Checked;
                minimumLabel.Enabled = chkSmartRecommendations.Checked;
                percentLabel.Enabled = chkSmartRecommendations.Checked;
                chkWarnBeforeEncodingRecommendations.Enabled =
                    chkSmartRecommendations.Checked;
            };

            grpSmartRecommendations.Controls.Add(chkSmartRecommendations);
            grpSmartRecommendations.Controls.Add(storageSavingsButton);
            grpSmartRecommendations.Controls.Add(minimumLabel);
            grpSmartRecommendations.Controls.Add(nudMinimumExpectedSavings);
            grpSmartRecommendations.Controls.Add(percentLabel);
            grpSmartRecommendations.Controls.Add(chkWarnBeforeEncodingRecommendations);
            Controls.Add(grpSmartRecommendations);

            nudMinimumExpectedSavings.Enabled = chkSmartRecommendations.Checked;
            minimumLabel.Enabled = chkSmartRecommendations.Checked;
            percentLabel.Enabled = chkSmartRecommendations.Checked;
            chkWarnBeforeEncodingRecommendations.Enabled =
                chkSmartRecommendations.Checked;
            UpdateStorageSavingsButton();
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
                "Choose keeper presets, custom weights, codec preference, and close-score safeguards for Duplicate Finder and Library Analyzer visual matches.");
        }

        private void btnDuplicateKeeperPreferences_Click(object? sender, EventArgs e)
        {
            using var dialog = new DuplicateKeeperPreferencesForm(_duplicateKeeperPreferences);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _duplicateKeeperPreferences = dialog.Preferences.Clone();
            _settingsToolTip.SetToolTip(
                btnDuplicateKeeperPreferences,
                $"Current shared keeper preference: {_duplicateKeeperPreferences.Profile}. Used by Duplicate Finder and Library Analyzer visual matches.");
        }

        private void ToggleDuplicateCleanupInputs()
        {
            bool allowQuarantine = chkAllowDuplicateQuarantine.Checked || comboLibraryAnalyzerCleanupMode?.SelectedIndex == 2;
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

        private void InitializeLibraryAnalyzerCleanupControls(Config cfg)
        {
            FlowLayoutPanel container = EnsureLibraryAnalyzerSettingsPanel();
            var group = new GroupBox { Name = "grpLibraryAnalyzerCleanup", Text = "Library Analyzer Cleanup", Size = new Size(390, 150), Margin = Padding.Empty };
            var modeLabel = new Label { Text = "Deletion mode:", AutoSize = true, Location = new Point(12, 26) };
            comboLibraryAnalyzerCleanupMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(115, 22), Size = new Size(255, 23) };
            comboLibraryAnalyzerCleanupMode.Items.AddRange(new object[] { "Permanent delete", "Recycle Bin", "Quarantine" });
            comboLibraryAnalyzerCleanupMode.SelectedIndex = cfg.LibraryAnalyzerCleanupMode switch { "RecycleBin" => 1, "Quarantine" => 2, _ => 0 };
            comboLibraryAnalyzerCleanupMode.SelectedIndexChanged += (_, _) => ToggleDuplicateCleanupInputs();
            chkAllowUnreviewedVisualBulkCleanup = new CheckBox { Text = "Advanced: include unreviewed visual matches", AutoSize = true, Location = new Point(15, 55), Checked = cfg.AllowUnreviewedVisualBulkCleanup };
            var confidenceLabel = new Label { Text = "Minimum confidence:", AutoSize = true, Location = new Point(31, 84) };
            nudVisualBulkCleanupConfidence = new NumericUpDown { Minimum = 76, Maximum = 100, DecimalPlaces = 1, Increment = 0.5M, Location = new Point(160, 80), Size = new Size(70, 23), Value = (decimal)Math.Clamp(cfg.VisualBulkCleanupMinimumConfidence, 76, 100) };
            lblVisualBulkCleanupWarning = new Label { Text = "Higher risk: unreviewed matches still require preview and confirmation.", AutoSize = false, Location = new Point(15, 110), Size = new Size(355, 32), ForeColor = Color.DarkRed };
            group.Controls.AddRange(new Control[] { modeLabel, comboLibraryAnalyzerCleanupMode, chkAllowUnreviewedVisualBulkCleanup, confidenceLabel, nudVisualBulkCleanupConfidence, lblVisualBulkCleanupWarning });
            container.Controls.Add(group);
            chkAllowUnreviewedVisualBulkCleanup.CheckedChanged += (_, _) =>
            {
                if (chkAllowUnreviewedVisualBulkCleanup.Checked && !cfg.AllowUnreviewedVisualBulkCleanup)
                {
                    if (MessageBox.Show(this, VisualBulkCleanupRiskWarning + "\r\n\r\nEnable this advanced option?", "Enable higher-risk visual cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        chkAllowUnreviewedVisualBulkCleanup.Checked = false;
                }
                nudVisualBulkCleanupConfidence.Enabled = chkAllowUnreviewedVisualBulkCleanup.Checked;
                lblVisualBulkCleanupWarning.Visible = chkAllowUnreviewedVisualBulkCleanup.Checked;
            };
            nudVisualBulkCleanupConfidence.Enabled = chkAllowUnreviewedVisualBulkCleanup.Checked;
            lblVisualBulkCleanupWarning.Visible = chkAllowUnreviewedVisualBulkCleanup.Checked;
        }

        private void InitializeLibraryAnalyzerReviewProductivityControls(Config cfg)
        {
            FlowLayoutPanel container = EnsureLibraryAnalyzerSettingsPanel();
            var group = new GroupBox { Name = "grpLibraryAnalyzerReviewProductivity", Text = "Library Analyzer Review Productivity", Size = new Size(390, 155), Margin = new Padding(0, 10, 0, 0) };
            chkSemiAutomaticVisualKeeperApproval = new CheckBox
            {
                Text = "Semi-Automatic Visual Keeper Approval",
                AutoSize = true,
                Location = new Point(15, 24),
                Checked = cfg.SemiAutomaticVisualKeeperApproval
            };
            var maximumLabel = new Label { Text = "Mass review maximum:", AutoSize = true, Location = new Point(15, 58) };
            nudVisualMassReviewMaximumMatches = new NumericUpDown
            {
                Minimum = 1, Maximum = 1000, Location = new Point(165, 54), Size = new Size(75, 23),
                Value = Math.Clamp(cfg.VisualMassReviewMaximumMatches, 1, 1000)
            };
            var marginLabel = new Label { Text = "Minimum score margin:", AutoSize = true, Location = new Point(15, 86) };
            nudVisualMassReviewMinimumMargin = new NumericUpDown
            {
                Minimum = 0, Maximum = 100, DecimalPlaces = 1, Increment = 0.5M, Location = new Point(165, 82), Size = new Size(75, 23),
                Value = (decimal)Math.Clamp(cfg.VisualMassReviewMinimumAutomationMargin, 0, 100)
            };
            var confidenceLabel = new Label { Text = "Minimum visual confidence:", AutoSize = true, Location = new Point(15, 114) };
            nudVisualMassReviewMinimumConfidence = new NumericUpDown
            {
                Minimum = 76, Maximum = 100, DecimalPlaces = 1, Increment = 0.5M, Location = new Point(165, 110), Size = new Size(75, 23),
                Value = (decimal)Math.Clamp(cfg.VisualMassReviewMinimumConfidence, 76, 100)
            };
            group.Controls.AddRange(new Control[]
            {
                chkSemiAutomaticVisualKeeperApproval, maximumLabel, nudVisualMassReviewMaximumMatches,
                marginLabel, nudVisualMassReviewMinimumMargin, confidenceLabel, nudVisualMassReviewMinimumConfidence
            });
            container.Controls.Add(group);
        }

        private FlowLayoutPanel EnsureLibraryAnalyzerSettingsPanel()
        {
            if (_libraryAnalyzerSettingsPanel != null)
                return _libraryAnalyzerSettingsPanel;

            _libraryAnalyzerSettingsPanel = new FlowLayoutPanel
            {
                Name = "LibraryAnalyzerSettingsPanel",
                Location = new Point(820, 970),
                Size = new Size(390, 315),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Margin = Padding.Empty
            };
            Controls.Add(_libraryAnalyzerSettingsPanel);
            return _libraryAnalyzerSettingsPanel;
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

            if (comboLibraryAnalyzerCleanupMode.SelectedIndex == 2 && !Directory.Exists(duplicateQuarantineFolder))
            {
                MessageBox.Show(this, "Choose an existing duplicate quarantine folder before selecting Library Analyzer quarantine cleanup.", "Quarantine folder required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Config.MinimizeToSystemTrayWhenMinimized = chkMinimizeToSystemTray.Checked;
            Config.LimitGpuEncodingQueueToOneJob = chkLimitGpuEncodingQueueToOneJob.Checked;
            Config.DeleteFailedEncodeOutputs = chkDeleteFailedEncodeOutputs.Checked;
            Config.DeleteCanceledEncodeOutputs = chkDeleteCanceledEncodeOutputs.Checked;
            Config.LargeQueueThreshold = (int)nudLargeQueueThreshold.Value;
            Config.AutoAnalyzeLargeQueues = chkAutoAnalyzeLargeQueues.Checked;
            Config.SmartRecommendationsEnabled = chkSmartRecommendations.Checked;
            Config.MinimumExpectedSavingsPercent =
                (double)nudMinimumExpectedSavings.Value;
            Config.WarnBeforeEncodingSkippedOrReviewItems =
                chkWarnBeforeEncodingRecommendations.Checked;
            Config.StorageSavings =
                _storageSavingsOptions.CloneNormalized();
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
            Config.LibraryAnalyzerCleanupMode = comboLibraryAnalyzerCleanupMode.SelectedIndex switch { 1 => "RecycleBin", 2 => "Quarantine", _ => "PermanentDelete" };
            Config.AllowUnreviewedVisualBulkCleanup = chkAllowUnreviewedVisualBulkCleanup.Checked;
            Config.VisualBulkCleanupMinimumConfidence = (double)nudVisualBulkCleanupConfidence.Value;
            Config.SemiAutomaticVisualKeeperApproval = chkSemiAutomaticVisualKeeperApproval.Checked;
            Config.VisualMassReviewMaximumMatches = (int)nudVisualMassReviewMaximumMatches.Value;
            Config.VisualMassReviewMinimumAutomationMargin = (double)nudVisualMassReviewMinimumMargin.Value;
            Config.VisualMassReviewMinimumConfidence = (double)nudVisualMassReviewMinimumConfidence.Value;
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
                // Keep this below Smart Encode Recommendations. The smart settings
                // group was added later and otherwise covered the right half of these
                // Explorer controls.
                Location = new Point(415, 850),
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
