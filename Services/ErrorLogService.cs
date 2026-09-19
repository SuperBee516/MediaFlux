using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MediaFlux.Services
{
    public enum ErrorLogView { CentralErrorLog, LatestFailureDiagnostic, LatestRawFfmpegEvidence }

    public static class ErrorLogService
    {
        private static readonly object Sync = new();
        private const long MaxLogBytes = 10L * 1024 * 1024;
        private const int MaxFieldCharacters = 256 * 1024;

        public static string GetDefaultLogPath(string applicationDirectory)
        {
            return Path.Combine(AppPaths.LogsDirectory, "mediaflux-errors.log");
        }

        public static string Append(
            string applicationDirectory,
            string title,
            string? sourcePath = null,
            Exception? exception = null,
            string? details = null)
        {
            var logPath = GetDefaultLogPath(applicationDirectory);

            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"Local Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"UTC Time   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
                sb.AppendLine($"Title      : {title}");

                if (!string.IsNullOrWhiteSpace(sourcePath))
                    sb.AppendLine($"Source     : {sourcePath}");

                if (exception != null)
                {
                    sb.AppendLine($"Exception  : {exception.GetType().FullName}");
                    sb.AppendLine($"Message    : {Limit(exception.Message)}");
                }

                if (!string.IsNullOrWhiteSpace(details))
                {
                    sb.AppendLine();
                    sb.AppendLine(Limit(details.TrimEnd()));
                }

                if (exception?.StackTrace != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Stack Trace:");
                    sb.AppendLine(Limit(exception.StackTrace));
                }

                sb.AppendLine();

                lock (Sync)
                {
                    RotateOversizedLog(logPath);
                    File.AppendAllText(logPath, sb.ToString());
                }
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"Failed to append central encode error log: {logEx}");
            }

            return logPath;
        }

        public static string? FindLatestFailureDiagnosticReport(string? logDirectory = null)
        {
            return FindLatestArtifact(logDirectory, raw: false);
        }

        public static string? FindLatestRawFfmpegEvidence(string? logDirectory = null)
        {
            string directory = string.IsNullOrWhiteSpace(logDirectory) ? AppPaths.LogsDirectory : logDirectory;
            string? latestReport = FindLatestFailureDiagnosticReport(directory);
            if (!string.IsNullOrWhiteSpace(latestReport))
            {
                string paired = GetPairedRawArtifactPath(latestReport);
                return File.Exists(paired) ? paired : null;
            }

            return FindLatestArtifact(directory, raw: true);
        }

        public static bool TryDeleteFailureDiagnosticPair(
            string selectedPath,
            string? logDirectory,
            out string? error)
        {
            error = null;
            try
            {
                string directory = string.IsNullOrWhiteSpace(logDirectory) ? AppPaths.LogsDirectory : logDirectory;
                if (!TryGetSafeArtifactPath(selectedPath, directory, out string artifactPath))
                {
                    error = "The selected diagnostic path is not a valid MediaFlux artifact.";
                    return false;
                }

                string rawSuffix = ".raw.log";
                string curatedPath = artifactPath.EndsWith(rawSuffix, StringComparison.OrdinalIgnoreCase)
                    ? artifactPath[..^rawSuffix.Length] + ".log"
                    : artifactPath;
                string rawPath = artifactPath.EndsWith(rawSuffix, StringComparison.OrdinalIgnoreCase)
                    ? artifactPath
                    : GetPairedRawArtifactPath(artifactPath);

                bool deleted = false;
                foreach (string path in new[] { curatedPath, rawPath }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path))
                        continue;

                    File.Delete(path);
                    deleted = true;
                }

                return deleted;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string? FindLatestArtifact(string? logDirectory, bool raw)
        {
            try
            {
                string directory = string.IsNullOrWhiteSpace(logDirectory) ? AppPaths.LogsDirectory : logDirectory;
                if (!Directory.Exists(directory))
                    return null;

                return Directory.EnumerateFiles(directory, "MediaFlux_Error_*.log", SearchOption.TopDirectoryOnly)
                    .Where(path => raw
                        ? path.EndsWith(".raw.log", StringComparison.OrdinalIgnoreCase)
                        : !path.EndsWith(".raw.log", StringComparison.OrdinalIgnoreCase))
                    .Select(path => new
                    {
                        Path = path,
                        LastWriteUtc = GetLastWriteUtc(path)
                    })
                    .OrderByDescending(item => item.LastWriteUtc)
                    .ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(item => item.Path)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static DateTime GetLastWriteUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static bool TryGetSafeArtifactPath(string path, string directory, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
                return false;

            string directoryFullPath = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullPath = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(directoryFullPath, fullPath);
            bool insideDirectory = relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative);
            string fileName = Path.GetFileName(fullPath);
            bool validName = fileName.StartsWith("MediaFlux_Error_", StringComparison.Ordinal) &&
                (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".raw.log", StringComparison.OrdinalIgnoreCase));
            return insideDirectory && validName;
        }

        private static string GetPairedRawArtifactPath(string curatedPath)
        {
            return Path.Combine(
                Path.GetDirectoryName(curatedPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(curatedPath) + ".raw.log");
        }

        /// <summary>Writes bounded curated and raw-captured failure artifacts without changing the rolling central log.</summary>
        public static FailureDiagnosticReportArtifact? TryWriteFailureDiagnosticArtifacts(
            string applicationDirectory, string curatedReport, string rawCapturedStandardError,
            string? artifactDirectory = null)
        {
            string? reportPath = null;
            string? rawPath = null;
            try
            {
                string directory = string.IsNullOrWhiteSpace(artifactDirectory) ? AppPaths.LogsDirectory : artifactDirectory;
                Directory.CreateDirectory(directory);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8];
                string stem = Path.Combine(directory, "MediaFlux_Error_" + stamp);
                reportPath = stem + ".log";
                rawPath = stem + ".raw.log";
                Encoding utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                File.WriteAllText(reportPath, curatedReport, utf8NoBom);
                File.WriteAllText(rawPath, rawCapturedStandardError ?? string.Empty, utf8NoBom);
                return new FailureDiagnosticReportArtifact(reportPath, rawPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write failure diagnostic artifacts: {ex}");
                // Do not leave a report that claims to have a paired raw artifact.
                // The rolling central error log remains the fallback evidence path.
                try
                {
                    if (reportPath is { } report && File.Exists(report)) File.Delete(report);
                    if (rawPath is { } raw && File.Exists(raw)) File.Delete(raw);
                }
                catch { }
                return null;
            }
        }

        public static string ReadTail(string logPath, int maxBytes, out bool truncated)
        {
            truncated = false;
            if (!File.Exists(logPath))
                return "No error log has been created yet.";

            maxBytes = Math.Clamp(maxBytes, 4 * 1024, 8 * 1024 * 1024);
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            long start = Math.Max(0, stream.Length - maxBytes);
            truncated = start > 0;
            stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[stream.Length - start];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, totalRead);
            if (truncated)
            {
                int firstNewLine = text.IndexOf('\n');
                if (firstNewLine >= 0 && firstNewLine + 1 < text.Length)
                    text = text[(firstNewLine + 1)..];
            }

            return text;
        }

        private static string Limit(string value)
        {
            if (value.Length <= MaxFieldCharacters)
                return value;

            return value[..MaxFieldCharacters] +
                   $"{Environment.NewLine}[Diagnostic text truncated by MediaFlux.]";
        }

        private static void RotateOversizedLog(string logPath)
        {
            if (!File.Exists(logPath) || new FileInfo(logPath).Length < MaxLogBytes)
                return;

            string archivePath = Path.Combine(
                Path.GetDirectoryName(logPath)!,
                Path.GetFileNameWithoutExtension(logPath) + ".previous" + Path.GetExtension(logPath));
            File.Move(logPath, archivePath, overwrite: true);
        }
    }
}
