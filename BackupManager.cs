using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace MediaFlux
{
    internal static class BackupManager
    {
        private const string BackupPrefix = "Encode_Backup_";

        public static string GetDefaultBackupFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Encode", "Backups");

        public static string ResolveBackupFolder(string? configuredPath) =>
            string.IsNullOrWhiteSpace(configuredPath)
                ? GetDefaultBackupFolder()
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));

        public static string CreateBackup(string installDirectory, string? backupFolder, int backupsToKeep)
        {
            string source = Path.GetFullPath(installDirectory);
            string destinationFolder = ResolveBackupFolder(backupFolder);
            EnsureBackupFolderIsOutsideInstall(source, destinationFolder);
            Directory.CreateDirectory(destinationFolder);

            string archive = Path.Combine(destinationFolder,
                $"{BackupPrefix}{DateTime.Now:yyyyMMdd_HHmmss_fff}.zip");

            try
            {
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(source, file);
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

        public static void StartRestoreAndExit(string archivePath, string installDirectory, string exeName, int processId)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("The selected backup no longer exists.", archivePath);

            string tempBase = Path.Combine(Path.GetTempPath(), "Encode_Restore_" + Guid.NewGuid().ToString("N"));
            string stage = Path.Combine(tempBase, "payload");
            Directory.CreateDirectory(stage);

            ExtractValidated(archivePath, stage);
            string stagedExe = Path.Combine(stage, exeName);
            if (!File.Exists(stagedExe))
                throw new InvalidDataException($"This archive is not an Encode program backup ({exeName} is missing).");

            string script = Path.Combine(tempBase, "run_restore.cmd");
            File.WriteAllText(script, BuildRestoreBatch(stage, Path.GetFullPath(installDirectory), exeName, processId),
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

        private static void ExtractValidated(string archivePath, string destination)
        {
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The backup contains an unsafe file path.");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        private static void EnsureBackupFolderIsOutsideInstall(string installDirectory, string backupFolder)
        {
            string installRoot = installDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
            string backupRoot = Path.GetFullPath(backupFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
            if (backupRoot.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The backup folder must be outside the Encode program folder.");
        }

        private static void PruneOldBackups(string folder, int keep)
        {
            foreach (var file in new DirectoryInfo(folder).GetFiles(BackupPrefix + "*.zip")
                         .OrderByDescending(file => file.CreationTimeUtc).Skip(keep))
            {
                try { file.Delete(); } catch { }
            }
        }

        private static string BuildRestoreBatch(string source, string destination, string exeName, int processId) =>
$@"@echo off
setlocal
set ""SRC={source}""
set ""DEST={destination}""
set ""EXE={exeName}""
set ""PID={processId}""
set ""LOG=%TEMP%\Encode_restore.log""
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
start """" /D ""%DEST%"" ""%DEST%\%EXE%""
:END
echo ==== RESTORE END %DATE% %TIME% ==== >> ""%LOG%""
endlocal
exit /b 0
";
    }
}
