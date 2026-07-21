using System.Text.Json;

namespace MediaFlux.Models
{
    public class Config
    {
        public bool AutomaticallyBackupBeforeUpdates { get; set; } = true;
        public string BackupFolderPath { get; set; } = "";
        public int BackupsToKeep { get; set; } = 5;
        public string AutoNamingPattern { get; set; } = "clip_%TITLE%_%START%-%END%";

        // Filename suffixes
        public string OutputSuffix { get; set; } = "_2";
        public bool EnableOutputSuffix { get; set; } = false;
        public bool EnableCodecSuffix { get; set; } = false;

        // Persist column visibility
        public bool ShowSizeColumn { get; set; } = true;
        public bool ShowCreatedColumn { get; set; } = false;
        public bool ShowCustomColumn { get; set; } = true;

        // Persist the Encode queue's last selected sort.
        public string EncodeQueueSortColumn { get; set; } = "";
        public bool EncodeQueueSortDescending { get; set; } = false;

        // Empty means all supported extensions are enabled (legacy/default behavior).
        public List<string> EnabledVideoExtensions { get; set; } = new();

        // Persist delete-source setting
        public bool DeleteSourceAfterCompression { get; set; } = true;

        // Persist the last widths the user set
        public int NameColumnWidth { get; set; } = 0;
        public int SizeColumnWidth { get; set; } = 0;
        public int CreatedColumnWidth { get; set; } = 0;

        // Keep history of up to N folders
        public List<string> LastInputFolders { get; set; } = new();
        public List<string> LastOutputFolders { get; set; } = new();
        public int FolderHistoryLimit { get; set; } = 5;

        public bool WarnOnDuplicate { get; set; } = true;
        public bool AutoFetchMetadata { get; set; } = true;
        public string ExternalPlayerPath { get; set; } = ""; // e.g. "C:\\Program Files\\VideoLAN\\VLC\\vlc.exe"

        // lightweight history to help duplicate detection (keep it short)
        public List<string> DownloadHistory { get; set; } = new(); // store normalized output paths

        public bool RememberCheckboxStates { get; set; } = true;
        public bool PreventSleepDuringEncoding { get; set; } = false;
        public bool LimitGpuEncodingQueueToOneJob { get; set; } = false;
        public bool DeleteFailedEncodeOutputs { get; set; } = false;
        public bool DeleteCanceledEncodeOutputs { get; set; } = false;
        public int LargeQueueThreshold { get; set; } = 300;
        public bool AutoAnalyzeLargeQueues { get; set; } = false;
        public bool FindDuplicatesOnImport { get; set; } = false;
        public bool OnlyQueueDuplicateCandidates { get; set; } = false;
        public string DuplicateScanMode { get; set; } = "Strict visual duplicates";
        public string DuplicateReferenceFolder { get; set; } = "";
        public string DuplicateQuarantineFolder { get; set; } = "";
        public DuplicateKeeperPreferences DuplicateKeeperPreferences { get; set; } = new();
        public bool EnableDuplicateSignatureCache { get; set; } = true;
        public bool AutoDisableDuplicateFinderAfterCleanup { get; set; } = true;
        public bool AllowDuplicateRecycleBin { get; set; } = true;
        public bool AllowDuplicateQuarantine { get; set; } = false;
        public bool AllowDuplicatePermanentDelete { get; set; } = false;
        public bool RequireDuplicateCleanupConfirmation { get; set; } = true;
        public bool ShowDuplicateReferenceFolderOnMain { get; set; } = true;
        public bool EnablePersistentMediaInfoCache { get; set; } = true;
        public bool ExplorerFileContextMenuEnabled { get; set; } = false;
        public bool ExplorerFolderContextMenuEnabled { get; set; } = false;
        public bool ConfirmExplorerFolderImports { get; set; } = true;
        public bool PromptToClearQueueOnExplorerFolderImport { get; set; } = true;
        public bool ExplorerFolderIncludeSubfolders { get; set; } = true;
        public string FfmpegPath { get; set; } = "";
        public string FfprobePath { get; set; } = "";

        // Optional Discord notification sent after the Encode queue drains normally.
        public bool DiscordQueueNotificationEnabled { get; set; } = false;
        public string DiscordWebhookUrl { get; set; } = "";
        public string DiscordUserMentionId { get; set; } = "";
        public string DiscordQueueCompleteMessage { get; set; } =
            "Encode queue finished on {computer}. Total: {total}, succeeded: {succeeded}, failed: {failed}, retried: {retried}. Finished at {finished}.";

        // Automatically watch one folder and append newly completed video files
        // to the normal Encode queue. Codec eligibility always follows the three
        // Show codec filters on the Encode screen.
        public bool WatchFolderEnabled { get; set; } = false;
        public string WatchFolderPath { get; set; } = "";
        public int WatchFolderIntervalMinutes { get; set; } = 5;
        public bool WatchFolderIncludeSubfolders { get; set; } = true;
        public int WatchFolderStabilizationSeconds { get; set; } = 60;
        public bool HideWatchFolderStatusText { get; set; } = false;

        // Per-checkbox “last used” values:
        public bool LastChkAutoTargetSize { get; set; } = false;
        public bool LastChkDeleteSource { get; set; } = true;
        public bool LastChkFilterX264 { get; set; } = true;
        public bool LastChkFilterX265 { get; set; } = true;
        public bool LastChkFilterAv1 { get; set; } = true;
        public bool LastChkDownloadPlaylist { get; set; } = false;
        public bool LastChkProcessAll { get; set; } = true;

        // Per-dropdown "last used" values.
        public string LastCompressionProfile { get; set; } = "Medium Quality (Default)";
        public string LastEncodingSpeedPreset { get; set; } = "Balanced (Recommended)";

        // Persist the main window's last usable size and position.
        public int MainWindowX { get; set; } = 0;
        public int MainWindowY { get; set; } = 0;
        public int MainWindowWidth { get; set; } = 0;
        public int MainWindowHeight { get; set; } = 0;
        public bool MainWindowMaximized { get; set; } = false;
        public bool EncodeInfoHeaderCollapsed { get; set; } = false;
        public bool EncodingOptionsCollapsed { get; set; } = false;
        public bool DuplicateFinderCollapsed { get; set; } = false;

        // Compact floating queue window preferences.
        public bool CompactWindowAlwaysOnTop { get; set; } = false;
        public int CompactWindowX { get; set; } = int.MinValue;
        public int CompactWindowY { get; set; } = int.MinValue;

        // Persist which codecs to show
        public bool ShowX264Files { get; set; } = true;
        public bool ShowX265Files { get; set; } = true;
        public bool ShowAv1Files { get; set; } = true;
        public bool ShowOtherCodecFiles { get; set; } = true;

        // Persist arbitrary main-form checkbox states keyed by a stable control path.
        public Dictionary<string, bool> CheckboxStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static Config Load(string path)
        {
            if (!File.Exists(path))
                return new Config();

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<Config>(json) ?? new Config();

            config.CheckboxStates ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            config.EnabledVideoExtensions ??= new List<string>();
            if (config.LargeQueueThreshold < 1)
                config.LargeQueueThreshold = 300;
            if (config.WatchFolderIntervalMinutes < 1)
                config.WatchFolderIntervalMinutes = 5;
            if (config.WatchFolderStabilizationSeconds < 0)
                config.WatchFolderStabilizationSeconds = 60;
            if (config.BackupsToKeep < 1)
                config.BackupsToKeep = 5;
            if (!config.FindDuplicatesOnImport)
                config.OnlyQueueDuplicateCandidates = false;
            if (string.IsNullOrWhiteSpace(config.DuplicateScanMode))
                config.DuplicateScanMode = "Strict visual duplicates";
            config.DuplicateKeeperPreferences ??= new DuplicateKeeperPreferences();
            config.DuplicateKeeperPreferences.Normalize();
            if (!config.AllowDuplicateRecycleBin &&
                !config.AllowDuplicateQuarantine &&
                !config.AllowDuplicatePermanentDelete)
            {
                config.AllowDuplicateRecycleBin = true;
            }
            // Older configs never persisted checkbox state unless the user opted in.
            // Treat missing settings as enabled so existing installs pick up the new behavior.
            if (!json.Contains("\"RememberCheckboxStates\"", StringComparison.OrdinalIgnoreCase))
                config.RememberCheckboxStates = true;

            return config;
        }

        public void Save(string path)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(path, json);
        }
    }
}
