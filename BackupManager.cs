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

        public static string CreateBackup(string userDataDirectory, string? backupFolder, int backupsToKeep)
        {
            string source = Path.GetFullPath(userDataDirectory);
            string destinationFolder = ResolveBackupFolder(backupFolder);
            EnsureBackupFolderIsOutsideSource(source, destinationFolder);
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destinationFolder);

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
                        version = 1,
                        createdUtc = DateTimeOffset.UtcNow
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }

                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(source, file);
                    if (relative.Equals(".legacy-install-data-migrated-v1", StringComparison.OrdinalIgnoreCase))
                        continue;

                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                }
            }
            catch
            {
                try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                throw;
            }

            PruneOldBackups(destinationFolder, Math.Max(1, backupsToKeep));
            return archive;
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

        private static void ExtractUserDataValidated(string archivePath, string destination)
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
