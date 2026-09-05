using System.Text;
using Velopack.Locators;

namespace MediaFlux.Services
{
    internal static class AppPaths
    {
        private const string MigrationMarkerName = ".legacy-install-data-migrated-v1";
        private static readonly MediaFluxStoragePathService Storage = new();

        public static string InstallDirectory =>
            Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // RootDirectory remains the legacy container for callers that need installation-scoped
        // bootstrap state. All managed user data below is resolved by Storage.
        public static string RootDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaFlux");
        public static string UserDataDirectory => Storage.Root;
        public static string DataDirectory => Path.Combine(UserDataDirectory, "data");
        public static string LibraryCatalogFile => Path.Combine(DataDirectory, "library-catalog.db");
        public static string LibraryCatalogBackupDirectory => Path.Combine(DataDirectory, "catalog-backups");
        public static string LibraryCatalogRecoveryDirectory => Path.Combine(DataDirectory, "catalog-recovery");
        public static string LibraryPolicyFile => Path.Combine(DataDirectory, "library-policies.json");
        public static string StorageReclamationPlanFile => Path.Combine(DataDirectory, "storage-reclamation-plan.json");
        public static string EncodeJobsFile => Path.Combine(DataDirectory, "encode-jobs.json");
        public static string RestorationProfilesDirectory => Path.Combine(DataDirectory, "restoration-profiles");
        public static string NcnnPerformanceTuningCacheFile => Path.Combine(DataDirectory, "ncnn-performance-tuning.json");
        public static string AiBenchmarkDatabaseFile => Path.Combine(DataDirectory, "ai-benchmarks.db");
        public static string CommercialDetectorAnalysisFile => Path.Combine(DataDirectory, "commercial-detector-analysis.json");
        public static string TempDirectory => Storage.Temp;
        public static string ConfigFile => Storage.Config;
        public static string BackupDirectory => Storage.Backups;
        public static string AiIntermediatesDirectory => Storage.AiIntermediates;
        public static string RestorationPreviewsDirectory => Storage.RestorationPreviews;
        public static string FramePreviewsDirectory => Storage.FramePreviews;
        public static string DuplicatePreviewsDirectory => Storage.DuplicatePreviews;
        public static string TensorRtEnginesDirectory => Storage.TensorRtEngines;
        public static string AiBenchmarkRerunsDirectory => Storage.AiBenchmarkReruns;
        public static string LogsDirectory => Storage.Logs;
        public static string SupportedVideoExtensionsFile => Path.Combine(DataDirectory, "supported_video_extensions.json");
        public static string EncodingStatisticsFile => Path.Combine(DataDirectory, "encoding-statistics.jsonl");
        public static string HistoryFile => Path.Combine(DataDirectory, "history.json");
        public static string EncodingPresetsFile => Path.Combine(DataDirectory, "encoding_presets.json");
        public static string LibraryFileRemovalAuditFile => Path.Combine(DataDirectory, "library-file-removal-audit.jsonl");
        public static string DuplicateActionsLogFile => Path.Combine(LogsDirectory, "duplicate-actions.csv");
        public static string AiBenchmarkHistoryFile => Path.Combine(DataDirectory, "ai-benchmark-history.json");
        internal static MediaFluxStoragePathService StoragePaths => Storage;

        public static string LauncherExecutablePath
        {
            get
            {
                try
                {
                    var locator = VelopackLocator.Current;
                    if (locator.CurrentlyInstalledVersion != null && !string.IsNullOrWhiteSpace(locator.RootAppDir))
                    {
                        string executableName = Path.GetFileName(Environment.ProcessPath ?? "MediaFlux.exe");
                        string launcher = Path.Combine(locator.RootAppDir, executableName);
                        if (File.Exists(launcher))
                            return launcher;
                    }
                }
                catch
                {
                    // Development and legacy portable builds have no Velopack installation metadata.
                }

                return Environment.ProcessPath ?? Path.Combine(InstallDirectory, "MediaFlux.exe");
            }
        }

        public static void Initialize()
        {
            Storage.InitializeDirectories();
            // Pre-storage-root installs kept backups next to UserData. Preserve them when
            // adopting the root-based convention, including installations that already have
            // the legacy migration marker.
            CopyDirectoryIfMissing(Path.Combine(RootDirectory, "Backups"), BackupDirectory);
            DvdTempCleanupService.CleanupStaleOperations(
                TempDirectory,
                TimeSpan.FromDays(7));
            // Named generated artifacts only. It runs off the UI thread and never deletes user state.
            _ = new UserDataStorageManagementService(UserDataDirectory).CleanupAsync(UserDataCleanupScope.ExpiredGeneratedData);

            string marker = Path.Combine(UserDataDirectory, MigrationMarkerName);
            if (File.Exists(marker))
                return;

            MigrateLegacyInstallData();
            MigrateLegacyBackups();

            File.WriteAllText(
                marker,
                $"Legacy install-folder data migration completed {DateTimeOffset.UtcNow:O}.{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void MigrateLegacyInstallData()
        {
            string legacyConfig = Path.Combine(InstallDirectory, "config.json");
            CopyFileIfMissing(legacyConfig, ConfigFile);

            string legacyData = Path.Combine(InstallDirectory, "data");
            CopyDirectoryIfMissing(legacyData, DataDirectory);
        }

        private static void MigrateLegacyBackups()
        {
            string legacyBackupDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Encode",
                "Backups");

            if (!Directory.Exists(legacyBackupDirectory))
                return;

            foreach (string archive in Directory.EnumerateFiles(legacyBackupDirectory, "*.zip", SearchOption.TopDirectoryOnly))
            {
                string destination = Path.Combine(BackupDirectory, Path.GetFileName(archive));
                CopyFileIfMissing(archive, destination);
            }
        }

        private static void CopyDirectoryIfMissing(string sourceDirectory, string destinationDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
                return;

            Directory.CreateDirectory(destinationDirectory);
            foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDirectory, sourceFile);
                string destination = Path.Combine(destinationDirectory, relative);
                CopyFileIfMissing(sourceFile, destination);
            }
        }

        private static void CopyFileIfMissing(string source, string destination)
        {
            if (!File.Exists(source) || File.Exists(destination))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }
}
