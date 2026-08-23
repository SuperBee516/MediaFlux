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
        public bool ShowRecommendationColumn { get; set; } = true;

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

        // DVD-folder workflow preferences. Strings keep config.json readable and
        // allow older files to load without enum-conversion failures.
        public string LastDvdInputFolder { get; set; } = "";
        public string LastDvdOutputFolder { get; set; } = "";
        public string LastDvdOutputMode { get; set; } =
            nameof(DvdOutputMode.LosslessRemuxToMkv);
        public string DvdOutputNamingPattern { get; set; } =
            "{MovieName}{TitleSetSuffix}";

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
        public bool SmartRecommendationsEnabled { get; set; } = true;
        public double MinimumExpectedSavingsPercent { get; set; } = 15;
        public bool WarnBeforeEncodingSkippedOrReviewItems { get; set; } = true;
        public StorageSavingsOptions StorageSavings { get; set; } = new();
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
        public string LibraryAnalyzerCleanupMode { get; set; } = "PermanentDelete";
        public bool AllowUnreviewedVisualBulkCleanup { get; set; } = false;
        public double VisualBulkCleanupMinimumConfidence { get; set; } = 95;
        public bool SemiAutomaticVisualKeeperApproval { get; set; } = false;
        public int VisualMassReviewMaximumMatches { get; set; } = 100;
        public double VisualMassReviewMinimumAutomationMargin { get; set; } = 15;
        public double VisualMassReviewMinimumConfidence { get; set; } = 95;
        public LibraryAnalyzerUiState LibraryAnalyzerUiState { get; set; } = new();
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
        // Legacy JSON field retained to migrate NVENC-only configuration files.
        public string LastEncodingSpeedPreset { get; set; } = "Balanced (Recommended)";
        public string LastEncoderId { get; set; } = VideoEncoderIds.Nvenc;
        public string LastVideoCodec { get; set; } = nameof(VideoCodecFamily.Hevc);
        public string LastEncoderPreset { get; set; } = "p5";
        public string LastOutputContainer { get; set; } = nameof(OutputContainerSelection.Mp4);
        public int LastQualityValue { get; set; } = 22;

        // Persist the main window's last usable size and position.
        public int MainWindowX { get; set; } = 0;
        public int MainWindowY { get; set; } = 0;
        public int MainWindowWidth { get; set; } = 0;
        public int MainWindowHeight { get; set; } = 0;

        // Video Splitter / Trimmer window placement. The actual trim selection is
        // deliberately session-only until Phase 2 introduces saved projects.
        public int VideoSplitterWindowX { get; set; } = int.MinValue;
        public int VideoSplitterWindowY { get; set; } = int.MinValue;
        public int VideoSplitterWindowWidth { get; set; } = 0;
        public int VideoSplitterWindowHeight { get; set; } = 0;
        // Zero keeps the balanced default preview/editor proportion. Persisting this
        // independently from the window bounds makes the splitter feel stable when
        // the window is restored on a different display or DPI scale.
        public int VideoSplitterPreviewSplitterDistance { get; set; } = 0;
        public int VideoSplitterMediaEditorSplitterDistance { get; set; } = 0;
        public int VideoSplitterTimelineDetailsSplitterDistance { get; set; } = 0;
        public int VideoSplitterBoundarySegmentsSplitterDistance { get; set; } = 0;
        public int VideoSplitterSegmentsOutputSplitterDistance { get; set; } = 0;
        public bool MainWindowMaximized { get; set; } = false;
        public bool EncodeInfoHeaderCollapsed { get; set; } = false;
        // Zero preserves the application's default Summary / Preview height.
        public int EncodeInfoHeight { get; set; } = 0;
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
            config.MinimumExpectedSavingsPercent =
                Math.Clamp(config.MinimumExpectedSavingsPercent, 0, 90);
            config.StorageSavings ??= new StorageSavingsOptions();
            config.StorageSavings.Normalize();
            config.LibraryAnalyzerUiState ??= new LibraryAnalyzerUiState();
            config.LibraryAnalyzerUiState.Normalize();
            if (config.WatchFolderIntervalMinutes < 1)
                config.WatchFolderIntervalMinutes = 5;
            if (config.WatchFolderStabilizationSeconds < 0)
                config.WatchFolderStabilizationSeconds = 60;
            if (config.BackupsToKeep < 1)
                config.BackupsToKeep = 5;
            config.LastDvdInputFolder ??= "";
            config.LastDvdOutputFolder ??= "";
            config.LastDvdOutputMode = NormalizeDvdOutputMode(
                config.LastDvdOutputMode);
            if (string.IsNullOrWhiteSpace(config.DvdOutputNamingPattern))
                config.DvdOutputNamingPattern = "{MovieName}{TitleSetSuffix}";
            if (!config.FindDuplicatesOnImport)
                config.OnlyQueueDuplicateCandidates = false;
            if (string.IsNullOrWhiteSpace(config.DuplicateScanMode))
                config.DuplicateScanMode = "Strict visual duplicates";
            config.DuplicateKeeperPreferences ??= new DuplicateKeeperPreferences();
            config.DuplicateKeeperPreferences.Normalize();
            if (config.LibraryAnalyzerCleanupMode is not ("PermanentDelete" or "RecycleBin" or "Quarantine"))
                config.LibraryAnalyzerCleanupMode = "PermanentDelete";
            config.VisualBulkCleanupMinimumConfidence = Math.Clamp(config.VisualBulkCleanupMinimumConfidence, 76, 100);
            config.VisualMassReviewMaximumMatches = Math.Clamp(config.VisualMassReviewMaximumMatches, 1, 1_000);
            config.VisualMassReviewMinimumAutomationMargin = Math.Clamp(config.VisualMassReviewMinimumAutomationMargin, 0, 100);
            config.VisualMassReviewMinimumConfidence = Math.Clamp(config.VisualMassReviewMinimumConfidence, 76, 100);
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

            VideoCodecFamily codecFamily =
                VideoEncoderCompatibility.ParseCodecFamily(config.LastVideoCodec);
            config.LastVideoCodec = codecFamily.ToString();
            config.LastOutputContainer = Enum.TryParse(
                    config.LastOutputContainer,
                    true,
                    out OutputContainerSelection outputContainer)
                ? outputContainer.ToString()
                : OutputContainerSelection.Mp4.ToString();

            string[] knownEncoderIds =
            [
                VideoEncoderIds.Nvenc,
                VideoEncoderIds.Qsv,
                VideoEncoderIds.Libx264,
                VideoEncoderIds.Libx265,
                VideoEncoderIds.SvtAv1
            ];
            if (!knownEncoderIds.Contains(
                    config.LastEncoderId,
                    StringComparer.OrdinalIgnoreCase))
            {
                config.LastEncoderId = VideoEncoderIds.Nvenc;
            }

            if (!json.Contains("\"LastEncoderId\"", StringComparison.OrdinalIgnoreCase))
                config.LastEncoderId = VideoEncoderIds.Nvenc;
            if (!json.Contains("\"LastVideoCodec\"", StringComparison.OrdinalIgnoreCase))
                config.LastVideoCodec = nameof(VideoCodecFamily.Hevc);
            if (!json.Contains("\"LastEncoderPreset\"", StringComparison.OrdinalIgnoreCase))
            {
                config.LastEncoderPreset =
                    VideoEncoderCompatibility.NormalizeLegacyNvencPreset(
                        config.LastEncodingSpeedPreset);
            }

            config.LastQualityValue = Math.Clamp(config.LastQualityValue, 12, 35);

            return config;
        }

        public DvdOutputMode GetLastDvdOutputMode()
        {
            return Enum.TryParse(
                    LastDvdOutputMode,
                    ignoreCase: true,
                    out DvdOutputMode mode)
                ? mode
                : DvdOutputMode.LosslessRemuxToMkv;
        }

        public void SetLastDvdOutputMode(DvdOutputMode mode)
        {
            LastDvdOutputMode = mode.ToString();
        }

        public void Save(string path)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, fullPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Preserve the original save exception. A uniquely named temp file
                    // can be cleaned up manually without risking the active config.
                }
            }
        }

        private static string NormalizeDvdOutputMode(string? value)
        {
            return Enum.TryParse(
                    value,
                    ignoreCase: true,
                    out DvdOutputMode mode)
                ? mode.ToString()
                : nameof(DvdOutputMode.LosslessRemuxToMkv);
        }
    }
}
