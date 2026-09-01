using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MediaFlux
{
    internal static class BackupManager
    {
        private const string BackupPrefix = "MediaFlux_Backup_";
        private const string ManifestName = ".mediaflux-backup.json";
        private static readonly string[] RuntimeDataDirectories =
        {
            "ai-intermediates", "restoration-previews", "frame-previews",
            "staging", "encode-staging", "temporary-encodes"
        };
        private static readonly string[] PersistentRootFiles = { "config.json" };
        private static readonly string[] PersistentDataRoots = { "data" };

        public static string GetDefaultBackupFolder() => Services.AppPaths.BackupDirectory;

        public static string ResolveBackupFolder(string? configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                return GetDefaultBackupFolder();

            string resolved = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            string legacyDefault = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Encode",
                "Backups");

            return string.Equals(
                resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(legacyDefault).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
                ? GetDefaultBackupFolder()
                : resolved;
        }

        public static string CreateBackup(string userDataDirectory, string? backupFolder, int backupsToKeep, Action<string>? reportProgress = null)
        {
            string source = Path.GetFullPath(userDataDirectory);
            string destinationFolder = ResolveBackupFolder(backupFolder);
            EnsureBackupFolderIsOutsideSource(source, destinationFolder);
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destinationFolder);

            Report("Preparing backup...");
            Log("Preparing backup...");
            Report("Cleaning temporary AI files...");
            BackupCleanupResult cleanup = CleanupRuntimeArtifacts(source, warning => { Report("Warning: " + warning); Log(warning); });
            if (cleanup.FilesDeleted == 0 && cleanup.FoldersDeleted == 0)
                Report("No temporary MediaFlux data found.");
            else
            {
                Report($"✓ Deleted {cleanup.FoldersDeleted:N0} folders");
                Report($"✓ Deleted {cleanup.FilesDeleted:N0} files");
                Report($"✓ Reclaimed {FormatBytes(cleanup.BytesReclaimed)}");
            }
            Report("Backing up persistent settings...");
            Log("Backing up persistent user data...");

            string archive = Path.Combine(
                destinationFolder,
                $"{BackupPrefix}{DateTime.Now:yyyyMMdd_HHmmss_fff}.zip");

            try
            {
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
                var manifestEntry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write(JsonSerializer.Serialize(new
                    {
                        format = "MediaFlux user data backup",
                        version = 2,
                        scope = "persistent user data",
                        createdUtc = DateTimeOffset.UtcNow
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }

                BackupCopyResult copied = CopyPersistentUserData(source, zip);
                Report($"✓ {copied.Files:N0} files");
                Report($"✓ {copied.Folders:N0} folders");
                Report($"✓ {FormatBytes(copied.Bytes)}");
                Log($"Copied {copied.Files:N0} files; {copied.Folders:N0} folders; {FormatBytes(copied.Bytes)}.");
            }
            catch
            {
                try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                throw;
            }

            PruneOldBackups(destinationFolder, Math.Max(1, backupsToKeep));
            Report("Backup complete.");
            Log("Backup completed.");
            return archive;

            void Report(string message) => reportProgress?.Invoke(message);
            void Log(string message)
            {
                if (IsCurrentUserDataDirectory(source))
                    Services.ErrorLogService.Append(source, "MediaFlux updater backup", details: message);
            }
        }

        public static void StartRestoreAndExit(
            string archivePath,
            string userDataDirectory,
            string executablePath,
            int processId)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("The selected backup no longer exists.", archivePath);

            string tempBase = Path.Combine(Path.GetTempPath(), "MediaFlux_Restore_" + Guid.NewGuid().ToString("N"));
            string stage = Path.Combine(tempBase, "payload");
            Directory.CreateDirectory(stage);

            ExtractUserDataValidated(archivePath, stage);

            string script = Path.Combine(tempBase, "run_restore.cmd");
            File.WriteAllText(
                script,
                BuildRestoreBatch(stage, Path.GetFullPath(userDataDirectory), Path.GetFullPath(executablePath), processId),
                new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /q /c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Environment.Exit(0);
        }

        internal static void ExtractUserDataValidated(string archivePath, string destination)
        {
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(archivePath);
            bool isMediaFluxDataBackup = zip.GetEntry(ManifestName) != null;
            bool extractedAny = false;

            foreach (var entry in zip.Entries)
            {
                string normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (normalized.Length == 0 || normalized.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!isMediaFluxDataBackup &&
                    !normalized.Equals("config.json", StringComparison.OrdinalIgnoreCase) &&
                    !normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string target = Path.GetFullPath(Path.Combine(destination, normalized));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The backup contains an unsafe file path.");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                extractedAny = true;
            }

            if (!extractedAny)
                throw new InvalidDataException("This archive does not contain MediaFlux user data.");
        }

        private static void EnsureBackupFolderIsOutsideSource(string sourceDirectory, string backupFolder)
        {
            string sourceRoot = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
            string backupRoot = Path.GetFullPath(backupFolder)
                                  .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
            if (backupRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The backup folder must be outside the MediaFlux user-data folder.");
        }

        private static void PruneOldBackups(string folder, int keep)
        {
            foreach (var file in new DirectoryInfo(folder).GetFiles(BackupPrefix + "*.zip")
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(keep))
            {
                try { file.Delete(); } catch { }
            }
        }

        private static BackupCopyResult CopyPersistentUserData(string source, ZipArchive zip)
        {
            int files = 0, folders = 0;
            long bytes = 0;
            foreach (string file in PersistentRootFiles)
                CopyFile(Path.Combine(source, file), file);
            foreach (string directory in PersistentDataRoots)
                CopyDirectory(Path.Combine(source, directory), directory);
            return new(files, folders, bytes);

            void CopyDirectory(string directory, string relative)
            {
                if (!Directory.Exists(directory))
                    return;

                var pending = new Stack<(string Directory, string Relative)>();
                pending.Push((directory, relative));
                while (pending.Count > 0)
                {
                    (string current, string currentRelative) = pending.Pop();
                    folders++;
                    foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(file);
                        if (IsTransientFile(name) && !currentRelative.StartsWith("restoration-profiles", StringComparison.OrdinalIgnoreCase))
                            continue;
                        CopyFile(file, currentRelative + "/" + name);
                    }
                    foreach (string child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(child);
                        if (currentRelative.Equals("data", StringComparison.OrdinalIgnoreCase) && IsRuntimeDirectory(name))
                            continue;
                        pending.Push((child, currentRelative + "/" + name));
                    }
                }
            }

            void CopyFile(string file, string relative)
            {
                if (!File.Exists(file))
                    return;
                zip.CreateEntryFromFile(file, relative.Replace('\\', '/'), CompressionLevel.Optimal);
                files++;
                bytes += new FileInfo(file).Length;
            }
        }

        private static BackupCleanupResult CleanupRuntimeArtifacts(string source, Action<string> warning)
        {
            var result = new BackupCleanupResult();
            string data = Path.Combine(source, "data");
            DeleteOwnedDirectory(Path.Combine(source, "temp"));
            foreach (string name in RuntimeDataDirectories)
                DeleteOwnedDirectory(Path.Combine(data, name));
            if (Directory.Exists(data))
            {
                try
                {
                    foreach (string directory in Directory.EnumerateDirectories(data, "ai-intermediate-*", SearchOption.TopDirectoryOnly))
                        DeleteOwnedDirectory(directory);
                }
                catch (Exception ex) { warning($"Could not enumerate temporary MediaFlux folders ({ex.Message})."); }
                DeleteTransientFiles(source);
                DeleteTransientFiles(data);
            }
            return result;

            void DeleteOwnedDirectory(string directory)
            {
                if (!Directory.Exists(directory))
                    return;
                try
                {
                    var removed = new BackupCleanupResult();
                    CountDirectory(directory, removed);
                    Directory.Delete(directory, recursive: true);
                    result.FilesDeleted += removed.FilesDeleted;
                    result.FoldersDeleted += removed.FoldersDeleted + 1;
                    result.BytesReclaimed += removed.BytesReclaimed;
                }
                catch (Exception ex) { warning($"Could not remove temporary MediaFlux folder '{Path.GetFileName(directory)}' ({ex.Message})."); }
            }

            void DeleteTransientFiles(string directory)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (!IsTransientFile(Path.GetFileName(file)))
                            continue;
                        try
                        {
                            long length = new FileInfo(file).Length;
                            File.Delete(file);
                            result.FilesDeleted++;
                            result.BytesReclaimed += length;
                        }
                        catch (Exception ex) { warning($"Could not remove temporary MediaFlux file '{Path.GetFileName(file)}' ({ex.Message})."); }
                    }
                }
                catch (Exception ex) { warning($"Could not enumerate temporary MediaFlux files ({ex.Message})."); }
            }
        }

        private static void CountDirectory(string directory, BackupCleanupResult result)
        {
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    try { result.FilesDeleted++; result.BytesReclaimed += new FileInfo(file).Length; } catch { }
                }
                foreach (string child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    result.FoldersDeleted++;
                    pending.Push(child);
                }
            }
        }

        private static bool IsRuntimeDirectory(string name) => RuntimeDataDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("ai-intermediate-", StringComparison.OrdinalIgnoreCase);
        private static bool IsTransientFile(string name) => name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.Contains(".partial", StringComparison.OrdinalIgnoreCase);
        private static bool IsCurrentUserDataDirectory(string source) => string.Equals(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Services.AppPaths.UserDataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        private static string FormatBytes(long bytes) => bytes < 1024L * 1024 ? $"{bytes:N0} bytes" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
        private sealed class BackupCleanupResult { public int FilesDeleted { get; set; } public int FoldersDeleted { get; set; } public long BytesReclaimed { get; set; } }
        private readonly record struct BackupCopyResult(int Files, int Folders, long Bytes);

        private static string BuildRestoreBatch(
            string source,
            string destination,
            string executablePath,
            int processId)
        {
            string executableDirectory = Path.GetDirectoryName(executablePath)!;
            return
$@"@echo off
setlocal
set ""SRC={source}""
set ""DEST={destination}""
set ""EXE={executablePath}""
set ""EXE_DIR={executableDirectory}""
set ""PID={processId}""
set ""LOG=%TEMP%\MediaFlux_restore.log""
echo ==== RESTORE START %DATE% %TIME% ==== > ""%LOG%""
:WAIT_APP
tasklist /FI ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if %ERRORLEVEL%==0 (
  timeout /t 1 /nobreak >nul
  goto WAIT_APP
)
robocopy ""%SRC%"" ""%DEST%"" /MIR /R:3 /W:1 /NFL /NDL /NP /NS /NC >> ""%LOG%"" 2>&1
set ""RC=%ERRORLEVEL%""
echo ROBOCOPY RC=%RC% >> ""%LOG%""
if %RC% GEQ 8 goto END
if exist ""%EXE%"" start """" /D ""%EXE_DIR%"" ""%EXE%""
:END
echo ==== RESTORE END %DATE% %TIME% ==== >> ""%LOG%""
endlocal
exit /b 0
";
        }
    }
}
